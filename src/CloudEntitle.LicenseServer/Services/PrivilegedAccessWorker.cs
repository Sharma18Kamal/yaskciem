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
                    var exactGrantObserved = current.ScheduleId is not null
                        && string.Equals(observed.ScheduleId, current.ScheduleId, StringComparison.Ordinal);
                    if (observed.Status == "Revoked" && exactGrantObserved)
                        await TransitionAsync(work, current, "Revoked", null, token, schedule: observed.ScheduleId,
                            clearNextRevocationAttempt: true);
                    else if (observed.Status is "Active" or "ExpiryPending"
                        && exactGrantObserved)
                    {
                        current = await TransitionAsync(work, current, "RevokeIssuing", null, token,
                            schedule: observed.ScheduleId, revocationAttemptCount: current.RevocationAttemptCount + 1,
                            clearNextRevocationAttempt: true);
                        PimObservation result;
                        try { result = await transport.RevokeAsync(work.TenantId, current, token); }
                        catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
                        { result = new("RevokeOutcomeUnknown", current.ScheduleId, "revocation_outcome_unknown"); }
                        using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        await TransitionAsync(work, current, "RevokeOutcomeUnknown",
                            result.FailureCode ?? "revocation_accepted_unverified", completion.Token,
                            schedule: result.ScheduleId, nextRevocationAttempt: DateTimeOffset.UtcNow
                                .Add(RevocationRetryDelay(current.RevocationAttemptCount)));
                    }
                    else
                    {
                        // Only a matching provider GET that still shows the exact grant active permits a retry.
                        // Unknown, pending, denied, expired, and not-found outcomes never trigger another mutation.
                        await TransitionAsync(work, current, "RevokeOutcomeUnknown",
                            observed.FailureCode ?? (observed.Status is "Active" or "ExpiryPending" or "Revoked"
                                ? "revocation_observed_different_grant" : "revocation_not_verified"),
                            token, schedule: current.ScheduleId,
                            nextRevocationAttempt: DateTimeOffset.UtcNow.Add(RevocationRetryDelay(
                                Math.Max(1, current.RevocationAttemptCount))));
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
                else if (current.RevokeId is not null)
                {
                    try
                    {
                        using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        await TransitionAsync(work, current, "RevokeOutcomeUnknown", "revocation_observation_failed",
                            completion.Token, nextRevocationAttempt: DateTimeOffset.UtcNow.Add(RevocationRetryDelay(
                                Math.Max(1, current.RevocationAttemptCount))));
                    }
                    catch (Exception persistenceException)
                    {
                        logger.LogError("JIT revocation failure for request {RequestId} could not be persisted ({ErrorType}); provider state remains unverified.",
                            current.RequestId, persistenceException.GetType().Name);
                    }
                    logger.LogWarning("JIT revocation observation failed for request {RequestId} ({ErrorType}); provider state remains unverified.",
                        current.RequestId, exception.GetType().Name);
                }
                else logger.LogWarning("JIT transition conflict for request {RequestId}; the next iteration will reconcile.", current.RequestId);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
            {
                if (current.RevokeId is not null)
                {
                    try
                    {
                        using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        await TransitionAsync(work, current, "RevokeOutcomeUnknown", "revocation_observation_failed",
                            completion.Token, nextRevocationAttempt: DateTimeOffset.UtcNow.Add(RevocationRetryDelay(
                                Math.Max(1, current.RevocationAttemptCount))));
                    }
                    catch (Exception persistenceException)
                    {
                        logger.LogError("JIT revocation failure for request {RequestId} could not be persisted ({ErrorType}); provider state remains unverified.",
                            current.RequestId, persistenceException.GetType().Name);
                    }
                }
                logger.LogWarning("JIT observation failed for request {RequestId} ({ErrorType}); revocation remains unverified.",
                    current.RequestId, exception.GetType().Name);
            }
            finally
            {
                if (!token.IsCancellationRequested) await store.TouchPrivilegedAccessAsync(work, token);
            }
        }
    }
    private Task<PrivilegedAccessRequest> TransitionAsync(PrivilegedAccessWork work, PrivilegedAccessRequest current, string status,
        string? failure, CancellationToken token, DateTimeOffset? start = null, DateTimeOffset? expires = null, string? schedule = null,
        int? revocationAttemptCount = null, DateTimeOffset? nextRevocationAttempt = null, bool clearNextRevocationAttempt = false)
        => store.UpdatePrivilegedAccessAsync(work.TenantId, work.OrganizationId, current.RequestId, Guid.Empty, status,
            failure ?? "Native PIM state transition; no success inferred from acceptance or local expiry.", previous =>
            {
                if (previous is null || previous.Version != current.Version) throw new InvalidOperationException("Concurrent JIT transition.");
                return previous with { Status = status, FailureCode = failure, Version = previous.Version + 1,
                    StartsAtUtc = start ?? previous.StartsAtUtc, ExpiresAtUtc = expires ?? previous.ExpiresAtUtc,
                    ScheduleId = schedule ?? previous.ScheduleId,
                    RevocationAttemptCount = revocationAttemptCount ?? previous.RevocationAttemptCount,
                    NextRevocationAttemptAtUtc = clearNextRevocationAttempt ? null
                        : nextRevocationAttempt ?? previous.NextRevocationAttemptAtUtc };
            }, token);

    private static TimeSpan RevocationRetryDelay(int attempt)
        => TimeSpan.FromSeconds(Math.Min(3600, 30 * Math.Pow(2, Math.Clamp(attempt - 1, 0, 7))));
}
