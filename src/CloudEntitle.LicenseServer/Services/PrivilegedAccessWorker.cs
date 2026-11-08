using CloudEntitle.Integration.Contracts;
using CloudEntitle.OrganizationManagement.Models;
using CloudEntitle.OrganizationManagement.Services;

namespace CloudEntitle.LicenseServer.Services;

public sealed class PrivilegedAccessWorker(SharedWorkspaceStore store, IConfiguration configuration, IAzurePimTransport transport,
    ILogger<PrivilegedAccessWorker> logger, WorkerHealthRegistry? workers = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProcessOnceAsync(stoppingToken); workers?.Heartbeat("privileged-access", success: true); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { workers?.Heartbeat("privileged-access", failed: true); logger.LogError("JIT worker failed ({ErrorType}); durable requests retain their state.", exception.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
    public async Task ProcessOnceAsync(CancellationToken token)
    {
        foreach (var work in await store.GetPrivilegedAccessWorkAsync(token))
        {
            token.ThrowIfCancellationRequested();
            await using var lease = await store.LockPrivilegedAccessAsync(work.TenantId, work.OrganizationId, work.Request.RequestId, token);
            var current = work.Request;
            try
            {
                var saved = await store.GetPrivilegedAccessAsync(work.TenantId, work.OrganizationId, current.RequestId, token);
                if (saved is null || saved.Version != current.Version) continue;
                if (current.Status == "Queued")
                {
                    SharedWorkspaceEndpoints.ValidateJitTarget(configuration, work.TenantId, work.OrganizationId, current);
                    var service = new OrganizationManagementService(store.CreateOrganizationStore(work.TenantId));
                    var requester = new OrganizationIdentity(current.RequestedBy, current.RequestedBy.ToString("D"), work.TenantId.ToString("D"), "jit-requester");
                    if (!PrivilegedAccessPolicy.Authorized(service, work.OrganizationId, requester, false)
                        || !service.GetTenantBindings(work.OrganizationId, requester).Any(binding => binding.Provider == current.Provider
                            && string.Equals(binding.ExternalTenantId, current.SubscriptionId, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("Requester authorization or binding changed.");
                    var members = service.GetMembersAuthorizedForAction(work.OrganizationId, requester,
                        OrganizationAction.ViewInventory, OrganizationAction.ManageRemediations);
                    if (!current.ApprovedBy.All(id => members.Any(member => member.UserId == id
                            && PrivilegedAccessPolicy.Authorized(service, work.OrganizationId,
                                new(member.UserId, member.UserId.ToString("D"), work.TenantId.ToString("D"), member.Email), true)))
                        || current.ApprovalDeadlineUtc <= DateTimeOffset.UtcNow)
                        throw new InvalidOperationException("Approvers or deadline are no longer valid.");
                    var now = DateTimeOffset.UtcNow;
                    current = await TransitionAsync(work, current, "Issuing", null, token, now, now.AddMinutes(current.DurationMinutes));
                    PimObservation observation;
                    try { observation = await transport.SubmitAsync(work.TenantId, current, token); }
                    catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
                    {
                        logger.LogWarning("JIT request {RequestId} has an uncertain submission ({ErrorType}); it will only be reconciled with GET.", current.RequestId, exception.GetType().Name);
                        observation = new("OutcomeUnknown", FailureCode: "submission_outcome_unknown");
                    }
                    using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await TransitionAsync(work, current, observation.Status, observation.FailureCode, completion.Token, schedule: observation.ScheduleId);
                }
                else if (current.RevokeId is not null)
                {
                    var observed = await transport.ObserveAsync(work.TenantId, current, token);
                    if (observed.Status == "Revoked")
                        await TransitionAsync(work, current, "Revoked", null, token, schedule: observed.ScheduleId);
                    else if (observed.Status == "Failed")
                        await TransitionAsync(work, current, "RevokeOutcomeUnknown", observed.FailureCode ?? "native_failure_not_removal_evidence",
                            token, schedule: observed.ScheduleId);
                    else if (current.Status == "RevokeQueued" && (observed.ScheduleId ?? current.ScheduleId) is not null)
                    {
                        current = await TransitionAsync(work, current, "RevokeIssuing", null, token, schedule: observed.ScheduleId);
                        PimObservation result;
                        try { result = await transport.RevokeAsync(work.TenantId, current, token); }
                        catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
                        { result = new("RevokeOutcomeUnknown", current.ScheduleId, "revocation_outcome_unknown"); }
                        using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        await TransitionAsync(work, current, "RevokeOutcomeUnknown", result.FailureCode, completion.Token, schedule: result.ScheduleId);
                    }
                    else
                    {
                        // Issuing/unknown revocations are GET-only, even if GET still shows Active or 404.
                        var status = current.Status == "RevokeQueued" ? "RevokeQueued" : "RevokeOutcomeUnknown";
                        if (status != current.Status || observed.ScheduleId != current.ScheduleId || observed.FailureCode != current.FailureCode)
                            await TransitionAsync(work, current, status, observed.FailureCode ?? "revocation_not_verified", token, schedule: observed.ScheduleId);
                    }
                }
                else
                {
                    // Never reissue a PUT after a crash, timeout or unknown outcome. Native Azure expiration is independent of this worker.
                    var observation = await transport.ObserveAsync(work.TenantId, current, token);
                    if (observation.Status != current.Status || observation.ScheduleId != current.ScheduleId || observation.FailureCode != current.FailureCode)
                        await TransitionAsync(work, current, observation.Status, observation.FailureCode, token,
                            observation.StartsAtUtc, observation.ExpiresAtUtc, observation.ScheduleId);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
            {
                if (current.Status == "Queued")
                {
                    try { await TransitionAsync(work, current, "Failed", "authorization_or_configuration_changed", token); }
                    catch (InvalidOperationException) { logger.LogWarning("Concurrent JIT transition for request {RequestId}; refresh required.", current.RequestId); }
                }
                else logger.LogWarning("JIT transition conflict for request {RequestId}; the next iteration will reconcile.", current.RequestId);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
            { logger.LogWarning("JIT observation failed for request {RequestId} ({ErrorType}); revocation remains unverified.", current.RequestId, exception.GetType().Name); }
            finally
            {
                if (!token.IsCancellationRequested) await store.TouchPrivilegedAccessAsync(work, token);
            }
        }
    }
    private Task<PrivilegedAccessRequest> TransitionAsync(PrivilegedAccessWork work, PrivilegedAccessRequest current, string status,
        string? failure, CancellationToken token, DateTimeOffset? start = null, DateTimeOffset? expires = null, string? schedule = null)
        => store.UpdatePrivilegedAccessAsync(work.TenantId, work.OrganizationId, current.RequestId, Guid.Empty, status,
            failure ?? "Native PIM state transition; no success inferred from acceptance or local expiry.", previous =>
            {
                if (previous is null || previous.Version != current.Version) throw new InvalidOperationException("Concurrent JIT transition.");
                return previous with { Status = status, FailureCode = failure, Version = previous.Version + 1,
                    StartsAtUtc = start ?? previous.StartsAtUtc, ExpiresAtUtc = expires ?? previous.ExpiresAtUtc, ScheduleId = schedule ?? previous.ScheduleId };
            }, token);
}
