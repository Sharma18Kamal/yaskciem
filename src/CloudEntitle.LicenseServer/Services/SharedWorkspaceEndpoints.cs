using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudEntitle.Domain;
using CloudEntitle.IdentityGovernance.Models;
using CloudEntitle.IdentityGovernance.Services;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.OrganizationManagement.Models;
using CloudEntitle.OrganizationManagement.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace CloudEntitle.LicenseServer.Services;

public static partial class SharedWorkspaceEndpoints
{
    public static void MapSharedWorkspaceEndpoints(this WebApplication app, SharedWorkspaceStore store)
    {
    app.MapCentralMonitoringEndpoints(store);
    app.MapOperationalIntegrationEndpoints(store);
    app.MapGovernanceEndpoints(store);
        app.MapGet("/api/v1/organizations", (ClaimsPrincipal principal) => GetOrganizations(principal, store))
            .RequireAuthorization("CiemApiAccess");
        app.MapPost("/api/v1/organizations", (ClaimsPrincipal principal, CreateOrganizationRequest request) => CreateOrganization(principal, request, store))
            .RequireAuthorization("CiemApiAccess");
        app.MapGet("/api/v1/organizations/{organizationId:guid}/workspace", (ClaimsPrincipal principal, Guid organizationId) => GetWorkspace(principal, organizationId, store))
            .RequireAuthorization("CiemApiAccess");
        app.MapPost("/api/v1/organizations/{organizationId:guid}/members", (ClaimsPrincipal principal, Guid organizationId, InviteOrganizationMemberRequest request) => InviteMember(principal, organizationId, request, store))
            .RequireAuthorization("CiemApiAccess");
        app.MapPut("/api/v1/organizations/{organizationId:guid}/members/{memberId:guid}/role", (ClaimsPrincipal principal, Guid organizationId, Guid memberId, UpdateOrganizationMemberRoleRequest request) => UpdateMemberRole(principal, organizationId, memberId, request, store))
            .RequireAuthorization("CiemApiAccess");
        app.MapPut("/api/v1/organizations/{organizationId:guid}/members/{memberId:guid}/status", (ClaimsPrincipal principal, Guid organizationId, Guid memberId, SetOrganizationMemberStatusRequest request) => SetMemberStatus(principal, organizationId, memberId, request, store))
            .RequireAuthorization("CiemApiAccess");
        app.MapPost("/api/v1/organizations/{organizationId:guid}/bindings", (ClaimsPrincipal principal, Guid organizationId, BindOrganizationAccountRequest request) => BindAccount(principal, organizationId, request, store))
            .RequireAuthorization("CiemApiAccess");
        app.MapPost("/api/v1/organizations/{organizationId:guid}/custom-roles", (ClaimsPrincipal principal, Guid organizationId, SaveOrganizationCustomRoleRequest request) => SaveCustomRole(principal, organizationId, null, request, store))
            .RequireAuthorization("CiemApiAccess");
        app.MapPut("/api/v1/organizations/{organizationId:guid}/custom-roles/{roleId:guid}", (ClaimsPrincipal principal, Guid organizationId, Guid roleId, SaveOrganizationCustomRoleRequest request) => SaveCustomRole(principal, organizationId, roleId, request, store))
            .RequireAuthorization("CiemApiAccess");
        app.MapDelete("/api/v1/organizations/{organizationId:guid}/custom-roles/{roleId:guid}", (ClaimsPrincipal principal, Guid organizationId, Guid roleId) => DeleteCustomRole(principal, organizationId, roleId, store))
            .RequireAuthorization("CiemApiAccess");
        app.MapGet("/api/v1/organizations/{organizationId:guid}/audit-history", (ClaimsPrincipal principal, Guid organizationId) => GetOrganizationAudit(principal, organizationId, store))
            .RequireAuthorization("CiemApiAccess");
        app.MapPost("/api/v1/organizations/{organizationId:guid}/scan-summaries", (ClaimsPrincipal principal, Guid organizationId, UploadSharedScanSummaryRequest request, SharedWorkspaceStore workspaceStore, CancellationToken cancellationToken) => UploadScanSummary(principal, organizationId, request, workspaceStore, cancellationToken))
            .RequireAuthorization("CiemApiAccess");
        app.MapGet("/api/v1/organizations/{organizationId:guid}/scan-summaries", (ClaimsPrincipal principal, Guid organizationId, int limit, SharedWorkspaceStore workspaceStore, CancellationToken cancellationToken) => GetScanSummaries(principal, organizationId, limit, workspaceStore, cancellationToken))
            .RequireAuthorization("CiemApiAccess");
        app.MapPost("/api/v1/organizations/{organizationId:guid}/scan-schedules", (ClaimsPrincipal principal, Guid organizationId, CreateScanScheduleRequest request, SharedWorkspaceStore workspaceStore, CancellationToken cancellationToken) => CreateScanSchedule(principal, organizationId, request, workspaceStore, cancellationToken))
            .RequireAuthorization("CiemApiAccess");
        app.MapGet("/api/v1/organizations/{organizationId:guid}/scan-schedules", (ClaimsPrincipal principal, Guid organizationId, SharedWorkspaceStore workspaceStore, CancellationToken cancellationToken) => GetScanSchedules(principal, organizationId, workspaceStore, cancellationToken))
            .RequireAuthorization("CiemApiAccess");
        app.MapPut("/api/v1/organizations/{organizationId:guid}/scan-schedules/{scheduleId:guid}", (ClaimsPrincipal principal, Guid organizationId, Guid scheduleId, UpdateScanScheduleRequest request, SharedWorkspaceStore workspaceStore, CancellationToken cancellationToken) => UpdateScanSchedule(principal, organizationId, scheduleId, request, workspaceStore, cancellationToken))
            .RequireAuthorization("CiemApiAccess");
        app.MapPost("/api/v1/organizations/{organizationId:guid}/scan-schedules/{scheduleId:guid}/run-now", (ClaimsPrincipal principal, Guid organizationId, Guid scheduleId, SharedWorkspaceStore workspaceStore, CancellationToken cancellationToken) => QueueScanNow(principal, organizationId, scheduleId, workspaceStore, cancellationToken))
            .RequireAuthorization("CiemApiAccess");
        app.MapGet("/api/v1/organizations/{organizationId:guid}/scan-jobs", (ClaimsPrincipal principal, Guid organizationId, int limit, SharedWorkspaceStore workspaceStore, CancellationToken cancellationToken) => GetScanJobs(principal, organizationId, limit, workspaceStore, cancellationToken))
            .RequireAuthorization("CiemApiAccess");
        app.MapGet("/api/v1/organizations/{organizationId:guid}/scan-jobs/{jobId:guid}", (ClaimsPrincipal principal, Guid organizationId, Guid jobId, SharedWorkspaceStore workspaceStore, CancellationToken cancellationToken) => GetScanJobStatus(principal, organizationId, jobId, workspaceStore, cancellationToken))
            .RequireAuthorization("CiemApiAccess");
        app.MapPost("/api/v1/organizations/{organizationId:guid}/scan-jobs/{jobId:guid}/cancel", (ClaimsPrincipal principal, Guid organizationId, Guid jobId, SharedWorkspaceStore workspaceStore, CancellationToken cancellationToken) => CancelScanJob(principal, organizationId, jobId, workspaceStore, cancellationToken))
            .RequireAuthorization("CiemApiAccess");
        app.MapGet("/api/v1/organizations/{organizationId:guid}/scans/{scanId:guid}/records", (ClaimsPrincipal principal, Guid organizationId, Guid scanId, string? kind, string? accountId, int limit, SharedWorkspaceStore workspaceStore, CancellationToken cancellationToken) => GetCiemScanRecords(principal, organizationId, scanId, kind, accountId, limit, workspaceStore, cancellationToken))
            .RequireAuthorization("CiemApiAccess");
        app.MapGet("/api/v1/organizations/{organizationId:guid}/findings", (ClaimsPrincipal principal, Guid organizationId, Guid? scanId, int limit, SharedWorkspaceStore workspaceStore, CancellationToken cancellationToken) => GetCiemFindings(principal, organizationId, scanId, limit, workspaceStore, cancellationToken))
            .RequireAuthorization("CiemApiAccess");
        app.MapGet("/api/v1/organizations/{organizationId:guid}/access-reviews", (ClaimsPrincipal principal, Guid organizationId, SharedWorkspaceStore workspaceStore, CancellationToken cancellationToken) => GetAccessReviews(principal, organizationId, workspaceStore, cancellationToken))
            .RequireAuthorization("CiemApiAccess");
        app.MapGet("/api/v1/organizations/{organizationId:guid}/access-reviews/reviewers", (ClaimsPrincipal principal, Guid organizationId, SharedWorkspaceStore workspaceStore) => GetEligibleAccessReviewers(principal, organizationId, workspaceStore))
            .RequireAuthorization("CiemApiAccess");
        app.MapPut("/api/v1/organizations/{organizationId:guid}/access-reviews/{campaignId:guid}", (ClaimsPrincipal principal, Guid organizationId, Guid campaignId, AccessReviewCampaign campaign, HttpContext httpContext, SharedWorkspaceStore workspaceStore, CancellationToken cancellationToken) => SaveAccessReview(principal, organizationId, campaignId, campaign, httpContext, workspaceStore, cancellationToken))
            .RequireAuthorization("CiemApiAccess");
    }

    private static IResult GetOrganizations(ClaimsPrincipal principal, SharedWorkspaceStore store)
    {
        if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
        var service = CreateService(store, identity);
        foreach (var organization in service.GetOrganizations().Where(item => item.EntraTenantId.Equals(identity.TenantId, StringComparison.OrdinalIgnoreCase)))
            service.AcceptInvitation(organization.Id, identity);
        return Results.Ok(Envelope(service.GetOrganizationsForIdentity(identity)));
    }

    private static IResult CreateOrganization(ClaimsPrincipal principal, CreateOrganizationRequest request, SharedWorkspaceStore store)
    {
        if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
        try
        {
            var organization = CreateService(store, identity).CreateOrganization(request.Name, request.TenantKey, identity.TenantId, identity);
            return Results.Created($"/api/v1/organizations/{organization.Id:D}", Envelope(organization));
        }
        catch (ArgumentException exception) { return BadRequest("invalid_organization", exception.Message); }
        catch (InvalidOperationException exception) { return Results.Conflict(Error("organization_conflict", exception.Message)); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
    }

    private static IResult GetWorkspace(ClaimsPrincipal principal, Guid organizationId, SharedWorkspaceStore store)
    {
        if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
        try
        {
            var service = CreateService(store, identity);
            service.AcceptInvitation(organizationId, identity);
            var organization = service.GetOrganizationsForIdentity(identity).FirstOrDefault(item => item.Id == organizationId);
            var member = service.GetCurrentMember(organizationId, identity);
            if (organization is null || member is null) return Results.NotFound();
            var allowed = Enum.GetValues<OrganizationAction>().Where(action => service.Authorize(organizationId, identity, action).Allowed).ToArray();
            var canManageMembers = allowed.Contains(OrganizationAction.ManageMembers);
            var canViewInventory = allowed.Contains(OrganizationAction.ViewInventory);
            return Results.Ok(Envelope(new OrganizationWorkspaceDto(organization, member, allowed,
                canManageMembers ? service.GetMembers(organizationId, identity) : Array.Empty<OrganizationMember>(),
                canViewInventory ? service.GetTenantBindings(organizationId, identity) : Array.Empty<TenantBinding>(),
                canManageMembers ? service.GetCustomRoles(organizationId, identity) : Array.Empty<OrganizationCustomRole>(),
                canManageMembers ? service.GetAuditTrail(organizationId, identity) : Array.Empty<OrganizationAuditEntry>())));
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }

    private static IResult InviteMember(ClaimsPrincipal principal, Guid organizationId, InviteOrganizationMemberRequest request, SharedWorkspaceStore store)
    {
        if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
        try
        {
            var member = CreateService(store, identity).AddMember(organizationId, identity, request.Email, request.Role);
            return Results.Created($"/api/v1/organizations/{organizationId:D}/members/{member.Id:D}", Envelope(member));
        }
        catch (ArgumentException exception) { return BadRequest("invalid_member", exception.Message); }
        catch (InvalidOperationException exception) { return Results.Conflict(Error("member_conflict", exception.Message)); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }

    private static IResult UpdateMemberRole(ClaimsPrincipal principal, Guid organizationId, Guid memberId, UpdateOrganizationMemberRoleRequest request, SharedWorkspaceStore store)
    {
        if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
        try { return Results.Ok(Envelope(CreateService(store, identity).UpdateMemberRole(organizationId, identity, memberId, request.Role))); }
        catch (ArgumentException exception) { return BadRequest("invalid_role", exception.Message); }
        catch (InvalidOperationException exception) { return Results.Conflict(Error("role_conflict", exception.Message)); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }

    private static IResult SetMemberStatus(ClaimsPrincipal principal, Guid organizationId, Guid memberId, SetOrganizationMemberStatusRequest request, SharedWorkspaceStore store)
    {
        if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
        try { return Results.Ok(Envelope(CreateService(store, identity).SetMemberActive(organizationId, identity, memberId, request.IsActive))); }
        catch (InvalidOperationException exception) { return Results.Conflict(Error("member_status_conflict", exception.Message)); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }

    private static async Task<IResult> BindAccount(ClaimsPrincipal principal, Guid organizationId, BindOrganizationAccountRequest request, SharedWorkspaceStore store)
    {
        if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
        OrganizationManagementService service;
        try
        {
            service = CreateService(store, identity);
            if (!service.Authorize(organizationId, identity, OrganizationAction.ManageConnections).Allowed) return Results.Forbid();
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        if (request is null || string.IsNullOrWhiteSpace(request.ExternalAccountId) || request.CloudAccountId == Guid.Empty)
            return BadRequest("invalid_binding", "Cloud account and external account ID are required.");
        if (await store.IsAccountBoundAsync(request.Provider, request.ExternalAccountId))
            return Results.Conflict(Error("account_already_bound", "This provider account is already bound to an organization."));
        try { return Results.Created($"/api/v1/organizations/{organizationId:D}/bindings", Envelope(service.BindCloudAccount(organizationId, identity, request.Provider, request.CloudAccountId, request.ExternalAccountId, request.DisplayName))); }
        catch (ArgumentException exception) { return BadRequest("invalid_binding", exception.Message); }
        catch (InvalidOperationException exception) { return Results.Conflict(Error("binding_conflict", exception.Message)); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }

    private static IResult SaveCustomRole(ClaimsPrincipal principal, Guid organizationId, Guid? roleId, SaveOrganizationCustomRoleRequest request, SharedWorkspaceStore store)
    {
        if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
        try
        {
            var service = CreateService(store, identity);
            var role = roleId.HasValue
                ? service.UpdateCustomRole(organizationId, identity, roleId.Value, request.Name, request.Permissions)
                : service.CreateCustomRole(organizationId, identity, request.Name, request.Permissions);
            return Results.Ok(Envelope(role));
        }
        catch (ArgumentException exception) { return BadRequest("invalid_custom_role", exception.Message); }
        catch (InvalidOperationException exception) { return Results.Conflict(Error("custom_role_conflict", exception.Message)); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }

    private static IResult DeleteCustomRole(ClaimsPrincipal principal, Guid organizationId, Guid roleId, SharedWorkspaceStore store)
    {
        if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
        try
        {
            CreateService(store, identity).DeleteCustomRole(organizationId, identity, roleId);
            return Results.NoContent();
        }
        catch (InvalidOperationException exception) { return Results.Conflict(Error("custom_role_conflict", exception.Message)); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }

    private static IResult GetOrganizationAudit(ClaimsPrincipal principal, Guid organizationId, SharedWorkspaceStore store)
    {
        if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
        try { return Results.Ok(Envelope(CreateService(store, identity).GetAuditTrail(organizationId, identity))); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }

    private static async Task<IResult> UploadScanSummary(ClaimsPrincipal principal, Guid organizationId, UploadSharedScanSummaryRequest request, SharedWorkspaceStore store, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
        if (request is null || request.Accounts is null || request.Coverage is null || request.Records is null || request.MonitoringScopes is null)
            return BadRequest("invalid_scan_summary", "Scan summary account, coverage, and record lists are required.");
        OrganizationManagementService service;
        try
        {
            service = CreateService(store, identity);
            if (!service.Authorize(organizationId, identity, OrganizationAction.RunScan).Allowed) return Results.Forbid();
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        if (request.ScanId == Guid.Empty || request.CompletedAtUtc < request.StartedAtUtc || request.Accounts.Count is 0 or > 200
            || request.Coverage.Count > 10000 || request.Records.Count > 20000
            || request.Coverage.Any(item => item is null || string.IsNullOrWhiteSpace(item.Provider) || item.Provider.Length > 64
                || string.IsNullOrWhiteSpace(item.DataSource) || item.DataSource.Length > 128 || string.IsNullOrWhiteSpace(item.Status)
                || item.Status.Length > 32 || item.Records < 0 || item.Details is { Length: > 1024 })
            || request.Accounts.Any(account => account is null || !Enum.IsDefined(account.Provider)
                || string.IsNullOrWhiteSpace(account.ExternalAccountId) || account.ExternalAccountId.Length > 512
                || string.IsNullOrWhiteSpace(account.DisplayName) || account.DisplayName.Length > 256)
            || new[] { request.Identities, request.Resources, request.Roles, request.Permissions, request.EffectiveAccess, request.Findings, request.CriticalFindings, request.HighFindings }
                .Any(value => value is < 0 or > 10_000_000)
            || (long)request.CriticalFindings + request.HighFindings > request.Findings)
            return BadRequest("invalid_scan_summary", "Scan summary identifiers, times, counts, or collection sizes are invalid.");
        var recordKinds = new HashSet<string>(StringComparer.Ordinal)
        {
            "identity", "resource", "role", "permission", "effective-access", "finding", "resource-finding", "access-path"
        };
        var recordKeys = new HashSet<string>(StringComparer.Ordinal);
        long payloadBytes = 0;
        foreach (var record in request.Records)
        {
            if (record is null || !Enum.IsDefined(record.Provider) || string.IsNullOrWhiteSpace(record.Kind) || !recordKinds.Contains(record.Kind)
                || string.IsNullOrWhiteSpace(record.RecordId) || record.RecordId.Length > 200
                || string.IsNullOrWhiteSpace(record.ExternalAccountId) || record.ExternalAccountId.Length > 512
                || string.IsNullOrWhiteSpace(record.PayloadJson) || record.PayloadJson.Length > 262144)
                return BadRequest("invalid_scan_record", "Scan records must contain a supported kind, bounded identifiers, and a bounded JSON payload.");
            try { using var _ = JsonDocument.Parse(record.PayloadJson); }
            catch (JsonException) { return BadRequest("invalid_scan_record", "Scan record payloads must be valid JSON."); }
            payloadBytes += Encoding.UTF8.GetByteCount(record.PayloadJson);
            if (payloadBytes > 15 * 1024 * 1024)
                return BadRequest("scan_payload_too_large", "The combined scan record payload cannot exceed 15 MiB.");
            if (!recordKeys.Add($"{record.Provider}\n{record.Kind}\n{record.ExternalAccountId}\n{record.RecordId}"))
                return BadRequest("duplicate_scan_record", "A scan cannot contain duplicate record keys.");
        }
        IReadOnlyList<TenantBinding> bindings;
        try { bindings = service.GetTenantBindings(organizationId, identity); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        if (request.Accounts.Any(account => !bindings.Any(binding => binding.Provider == account.Provider
                    && binding.ExternalTenantId.Equals(account.ExternalAccountId, StringComparison.OrdinalIgnoreCase))))
            return Results.Forbid();
        if (request.Accounts.Select(account => $"{account.Provider}\n{account.ExternalAccountId}")
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Accounts.Count
            || request.Records.Any(record => !request.Accounts.Any(account => account.Provider == record.Provider
                && account.ExternalAccountId.Equals(record.ExternalAccountId, StringComparison.OrdinalIgnoreCase))))
            return BadRequest("invalid_scan_account", "Scan records must refer to unique accounts included in the scan.");
        var providerLabel = string.Join(", ", request.Accounts.Select(account => account.Provider.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(provider => provider, StringComparer.OrdinalIgnoreCase));
        var summary = new SharedScanSummaryDto(request.ScanId, organizationId, Guid.Parse(identity.TenantId), identity.UserId,
            request.StartedAtUtc.ToUniversalTime(), request.CompletedAtUtc.ToUniversalTime(), providerLabel,
            request.Accounts, request.Identities, request.Resources, request.Roles, request.Permissions, request.EffectiveAccess,
            request.Findings, request.CriticalFindings, request.HighFindings, request.Coverage)
        {
            MonitoringScopes = request.MonitoringScopes
        };
        try { SharedWorkspaceStore.ValidateMonitoringScopes(summary, request.Records); }
        catch (ArgumentException exception) { return BadRequest("invalid_monitoring_scope", exception.Message); }
        var canonicalRequest = request with
        {
            Accounts = request.Accounts.OrderBy(account => account.Provider).ThenBy(account => account.ExternalAccountId, StringComparer.OrdinalIgnoreCase).ToArray(),
            Coverage = request.Coverage.OrderBy(item => item.Provider, StringComparer.Ordinal).ThenBy(item => item.DataSource, StringComparer.Ordinal).ToArray(),
            Records = request.Records.OrderBy(record => record.Kind, StringComparer.Ordinal)
                .ThenBy(record => record.Provider)
                .ThenBy(record => record.ExternalAccountId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(record => record.RecordId, StringComparer.Ordinal).ToArray(),
            MonitoringScopes = request.MonitoringScopes.OrderBy(scope => scope.Provider)
                .ThenBy(scope => scope.ExternalAccountId, StringComparer.OrdinalIgnoreCase).ThenBy(scope => scope.Kind, StringComparer.Ordinal).ToArray()
        };
        var requestHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonicalRequest, new JsonSerializerOptions(JsonSerializerDefaults.Web))))
            .ToLowerInvariant();
        try
        {
            var (saved, created) = await store.SaveCiemScanAsync(summary, request.Records, requestHash, cancellationToken);
            return created
                ? Results.Created($"/api/v1/organizations/{organizationId:D}/scan-summaries/{summary.ScanId:D}", Envelope(saved))
                : Results.Ok(Envelope(saved));
        }
        catch (InvalidOperationException exception) { return Results.Conflict(Error("scan_conflict", exception.Message)); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
    }

    private static async Task<IResult> GetCiemScanRecords(ClaimsPrincipal principal, Guid organizationId, Guid scanId, string? kind,
        string? accountId, int limit, SharedWorkspaceStore store, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
        try
        {
            var service = CreateService(store, identity);
            if (!service.Authorize(organizationId, identity, OrganizationAction.ViewInventory).Allowed) return Results.Forbid();
            if (kind is not null && (kind.Length > 64 || string.IsNullOrWhiteSpace(kind)))
                return BadRequest("invalid_scan_record_query", "Record kind is invalid.");
            if (kind is null || kind.Equals("finding", StringComparison.OrdinalIgnoreCase)
                || kind.Equals("resource-finding", StringComparison.OrdinalIgnoreCase))
            {
                if (!service.Authorize(organizationId, identity, OrganizationAction.ViewFindings).Allowed) return Results.Forbid();
            }
            if (accountId is { Length: > 512 })
                return BadRequest("invalid_scan_record_query", "Account ID is invalid.");
            return Results.Ok(Envelope(await store.GetCiemScanRecordsAsync(Guid.Parse(identity.TenantId), organizationId, scanId,
                kind, accountId, limit, cancellationToken)));
        }
        catch (ArgumentOutOfRangeException exception) { return BadRequest("invalid_scan_record_query", exception.Message); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }

    private static async Task<IResult> GetCiemFindings(ClaimsPrincipal principal, Guid organizationId, Guid? scanId, int limit,
        SharedWorkspaceStore store, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
        try
        {
            if (!CreateService(store, identity).Authorize(organizationId, identity, OrganizationAction.ViewFindings).Allowed) return Results.Forbid();
            return Results.Ok(Envelope(await store.GetCiemFindingsAsync(Guid.Parse(identity.TenantId), organizationId, scanId, limit, cancellationToken)));
        }
        catch (ArgumentOutOfRangeException exception) { return BadRequest("invalid_findings_query", exception.Message); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }

    private static async Task<IResult> GetScanSummaries(ClaimsPrincipal principal, Guid organizationId, int limit, SharedWorkspaceStore store, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
        try
        {
            if (!CreateService(store, identity).Authorize(organizationId, identity, OrganizationAction.ViewInventory).Allowed) return Results.Forbid();
            return Results.Ok(Envelope(await store.GetScanSummariesAsync(Guid.Parse(identity.TenantId), organizationId, limit, cancellationToken)));
        }
        catch (ArgumentOutOfRangeException exception) { return BadRequest("invalid_scan_summary_query", exception.Message); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }

    private static async Task<IResult> CreateScanSchedule(
        ClaimsPrincipal principal, Guid organizationId, CreateScanScheduleRequest request,
        SharedWorkspaceStore store, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
        if (request is null || !Enum.IsDefined(request.Provider) || request.IntervalMinutes is < 5 or > 10080
            || request.MaxRetries is < 0 or > 4 || string.IsNullOrWhiteSpace(request.ExternalAccountId)
            || request.ExternalAccountId.Length > 512 || string.IsNullOrWhiteSpace(request.DisplayName)
            || request.DisplayName.Length > 256)
            return BadRequest("invalid_scan_schedule", "Provider, account, display name, interval (5 minutes to 7 days), or retry count (0 to 4) is invalid.");
        try
        {
            var service = CreateService(store, identity);
            if (!service.Authorize(organizationId, identity, OrganizationAction.RunScan).Allowed) return Results.Forbid();
            var binding = service.GetTenantBindings(organizationId, identity).Any(item =>
                item.Provider == request.Provider && item.ExternalTenantId.Equals(request.ExternalAccountId, StringComparison.OrdinalIgnoreCase));
            if (!binding) return Results.Forbid();
            var schedule = await store.CreateScanScheduleAsync(Guid.Parse(identity.TenantId), organizationId, request, cancellationToken);
            return schedule is null
                ? Results.Conflict(Error("scan_schedule_exists", "A schedule already exists for this provider account in the organization."))
                : Results.Created($"/api/v1/organizations/{organizationId:D}/scan-schedules/{schedule.ScheduleId:D}", Envelope(schedule));
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (TenantResourceLimitException exception) { return Results.Json(Error("tenant_scan_limit", exception.Message), statusCode: StatusCodes.Status429TooManyRequests); }
    }

    private static async Task<IResult> GetScanSchedules(
        ClaimsPrincipal principal, Guid organizationId, SharedWorkspaceStore store, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
        try
        {
            if (!CreateService(store, identity).Authorize(organizationId, identity, OrganizationAction.RunScan).Allowed) return Results.Forbid();
            return Results.Ok(Envelope(await store.GetScanSchedulesAsync(Guid.Parse(identity.TenantId), organizationId, cancellationToken)));
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }

    private static async Task<IResult> UpdateScanSchedule(
        ClaimsPrincipal principal, Guid organizationId, Guid scheduleId, UpdateScanScheduleRequest request,
        SharedWorkspaceStore store, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
        if (request is null || request.IntervalMinutes is < 5 or > 10080 || request.MaxRetries is < 0 or > 4)
            return BadRequest("invalid_scan_schedule", "Interval must be 5 minutes to 7 days and retries must be between 0 and 4.");
        try
        {
            if (!CreateService(store, identity).Authorize(organizationId, identity, OrganizationAction.RunScan).Allowed) return Results.Forbid();
            var schedule = await store.UpdateScanScheduleAsync(Guid.Parse(identity.TenantId), organizationId, scheduleId, request, cancellationToken);
            return schedule is null ? Results.NotFound() : Results.Ok(Envelope(schedule));
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }

    private static async Task<IResult> QueueScanNow(
        ClaimsPrincipal principal, Guid organizationId, Guid scheduleId,
        SharedWorkspaceStore store, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
        try
        {
            if (!CreateService(store, identity).Authorize(organizationId, identity, OrganizationAction.RunScan).Allowed) return Results.Forbid();
            var job = await store.QueueScanNowAsync(Guid.Parse(identity.TenantId), organizationId, scheduleId, cancellationToken);
            return job is null ? Results.NotFound() : Results.Accepted($"/api/v1/organizations/{organizationId:D}/scan-jobs/{job.JobId:D}", Envelope(job));
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (TenantResourceLimitException exception) { return Results.Json(Error("tenant_scan_limit", exception.Message), statusCode: StatusCodes.Status429TooManyRequests); }
    }

    private static async Task<IResult> GetScanJobs(
        ClaimsPrincipal principal, Guid organizationId, int limit, SharedWorkspaceStore store, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
        try
        {
            if (!CreateService(store, identity).Authorize(organizationId, identity, OrganizationAction.RunScan).Allowed) return Results.Forbid();
            return Results.Ok(Envelope(await store.GetScanJobsAsync(Guid.Parse(identity.TenantId), organizationId, limit, cancellationToken)));
        }
        catch (ArgumentOutOfRangeException exception) { return BadRequest("invalid_scan_job_query", exception.Message); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }

    private static async Task<IResult> GetScanJobStatus(
        ClaimsPrincipal principal, Guid organizationId, Guid jobId, SharedWorkspaceStore store, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
        try
        {
            if (!CreateService(store, identity).Authorize(organizationId, identity, OrganizationAction.RunScan).Allowed) return Results.Forbid();
            var tenantId = Guid.Parse(identity.TenantId);
            var job = await store.GetScanJobAsync(tenantId, organizationId, jobId, cancellationToken);
            if (job is null) return Results.NotFound();
            var status = new ScanJobStatusDto(job,
                await store.GetScanJobAttemptsAsync(tenantId, organizationId, jobId, cancellationToken),
                await store.GetScanJobCheckpointsAsync(tenantId, organizationId, jobId, cancellationToken));
            return Results.Ok(Envelope(status));
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }

    private static async Task<IResult> CancelScanJob(
        ClaimsPrincipal principal, Guid organizationId, Guid jobId, SharedWorkspaceStore store, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
        try
        {
            if (!CreateService(store, identity).Authorize(organizationId, identity, OrganizationAction.RunScan).Allowed) return Results.Forbid();
            return await store.RequestScanJobCancellationAsync(Guid.Parse(identity.TenantId), organizationId, jobId, cancellationToken)
                ? Results.Accepted($"/api/v1/organizations/{organizationId:D}/scan-jobs/{jobId:D}")
                : Results.Conflict(Error("scan_job_not_cancellable", "The scan job was not found or is already terminal."));
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }

    private static async Task<IResult> GetAccessReviews(ClaimsPrincipal principal, Guid organizationId, SharedWorkspaceStore store, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
        try
        {
            var service = CreateService(store, identity);
            if (!service.Authorize(organizationId, identity, OrganizationAction.ViewFindings).Allowed
                && !service.Authorize(organizationId, identity, OrganizationAction.ManageRemediations).Allowed) return Results.Forbid();
            var campaigns = await store.GetAccessReviewsAsync(Guid.Parse(identity.TenantId), organizationId, cancellationToken);
            var governance = new IdentityGovernanceService();
            var reevaluated = campaigns.Select(campaign => governance.ReevaluateCampaign(campaign)).ToArray();
            for (var index = 0; index < campaigns.Count; index++)
            {
                if (HasReviewLifecycleChanges(campaigns[index], reevaluated[index]))
                {
                    var initial = reevaluated[index];
                    reevaluated[index] = await store.UpdateAccessReviewAsync(Guid.Parse(identity.TenantId), organizationId, initial.Id,
                        current =>
                        {
                            var baseline = current ?? initial;
                            var refreshed = governance.ReevaluateCampaign(baseline);
                            return HasReviewLifecycleChanges(baseline, refreshed)
                                ? EnsureAuditMetadata(refreshed, Guid.NewGuid())
                                : baseline;
                        }, cancellationToken);
                }
            }
            return Results.Ok(Envelope(reevaluated));
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }

    private static IResult GetEligibleAccessReviewers(ClaimsPrincipal principal, Guid organizationId, SharedWorkspaceStore store)
    {
        if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
        try
        {
            var service = CreateService(store, identity);
            var callerAction = service.Authorize(organizationId, identity, OrganizationAction.ViewFindings).Allowed
                ? OrganizationAction.ViewFindings
                : OrganizationAction.ManageRemediations;
            if (!service.Authorize(organizationId, identity, callerAction).Allowed) return Results.Forbid();
            var reviewers = service.GetMembersAuthorizedForAction(organizationId, identity, callerAction, OrganizationAction.ManageRemediations)
                .Where(member => member.UserId != identity.UserId)
                .Select(member => member.Email)
                .ToArray();
            return Results.Ok(Envelope(reviewers));
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }

    private static async Task<IResult> SaveAccessReview(
        ClaimsPrincipal principal,
        Guid organizationId,
        Guid campaignId,
        AccessReviewCampaign campaign,
        HttpContext httpContext,
        SharedWorkspaceStore store,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
        OrganizationManagementService service;
        try
        {
            service = CreateService(store, identity);
            if (!service.Authorize(organizationId, identity, OrganizationAction.ManageRemediations).Allowed) return Results.Forbid();
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        if (campaign is null || campaign.Items is null || campaign.Id != campaignId || campaignId == Guid.Empty || campaign.Items.Count is 0 or > 10000
            || campaign.Items.Any(item => item is null || item.Id == Guid.Empty || item.CampaignId != campaignId || !Enum.IsDefined(item.Decision))
            || campaign.Items.Any(item => string.IsNullOrWhiteSpace(item.AccessSummary) || item.AccessSummary.Length > 4000
                || string.IsNullOrWhiteSpace(item.EvidenceSnapshot) || item.EvidenceSnapshot.Length > 65536)
            || campaign.Items.Select(item => item.Id).Distinct().Count() != campaign.Items.Count)
            return BadRequest("invalid_access_review", "Access review IDs, evidence snapshots, or items are invalid.");
        var tenantId = Guid.Parse(identity.TenantId);
        try
        {
            var correlationId = Guid.TryParse(httpContext.Request.Headers["X-Correlation-ID"].FirstOrDefault(), out var requestedCorrelationId)
                && requestedCorrelationId != Guid.Empty
                    ? requestedCorrelationId
                    : Guid.NewGuid();
            var sanitized = await store.UpdateAccessReviewAsync(tenantId, organizationId, campaignId, existing =>
            {
                var updated = existing is null
                    ? SanitizeNewReview(campaign, identity, service, organizationId, correlationId)
                    : SanitizeReviewUpdate(existing, campaign, identity, service, organizationId, correlationId);
                return EnsureAuditMetadata(new IdentityGovernanceService().ReevaluateCampaign(updated), correlationId);
            }, cancellationToken);
            return Results.Ok(Envelope(sanitized, correlationId));
        }
        catch (ArgumentException exception) { return BadRequest("invalid_access_review", exception.Message); }
        catch (InvalidOperationException exception) { return Results.Conflict(Error("access_review_conflict", exception.Message)); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
    }

    private static AccessReviewCampaign SanitizeNewReview(
        AccessReviewCampaign campaign,
        OrganizationIdentity identity,
        OrganizationManagementService service,
        Guid organizationId,
        Guid correlationId)
    {
        var now = DateTime.UtcNow;
        if (campaign.DueAt <= now) throw new ArgumentException("A shared review campaign requires a future due date.");
        ValidateReviewerAssignments(campaign, null, identity, service, organizationId);
        var owners = service.GetMembersAuthorizedForAction(organizationId, identity, OrganizationAction.ManageRemediations, OrganizationAction.ViewInventory)
            .Select(member => member.Email).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in campaign.Items)
        {
            ValidateGovernanceLabels(item, null, service, organizationId, identity);
            if (NormalizeLabel(item.AssignedOwner) is { } owner && !owners.Contains(owner))
                throw new ArgumentException("The owner must be an active organization member with inventory access.");
            if (item.DueAt.HasValue && item.DueAt <= now) throw new ArgumentException("Review item deadlines must be in the future.");
        }
        var items = campaign.Items.Select(item => item with
        {
            AssignedReviewer = item.AssignedReviewer?.Trim(),
            OwnerSnapshot = item.OwnerSnapshot?.Trim(),
            AssignedOwner = NormalizeLabel(item.AssignedOwner),
            Team = NormalizeLabel(item.Team),
            ExternalAccountId = NormalizeLabel(item.ExternalAccountId),
            AssignmentVersion = 0,
            Decision = ReviewDecision.Pending,
            Reviewer = null,
            ReviewComment = null,
            DecidedAt = null,
            ReminderCount = 0,
            LastReminderAt = null,
            Attestations = Array.Empty<AccessReviewAttestation>(),
            ExceptionExpiresAt = null,
            ExceptionExpiredAt = null,
            ExceptionReminderCount = 0,
            LastExceptionReminderAt = null,
            EscalatedAt = null,
            EscalationReason = null,
            AuditTrail = new[] { CreateReviewAuditEntry(now, identity.Email, "ItemCreated", "Review item created by the shared service.", correlationId, item.Id, actorUserId: identity.UserId) }
        }).ToArray();
        return campaign with
        {
            Status = CampaignStatus.Open,
            CreatedAt = now,
            CreatedBy = identity.Email,
            ReminderLeadDays = Math.Clamp(campaign.ReminderLeadDays, 1, 30),
            ReminderIntervalDays = Math.Clamp(campaign.ReminderIntervalDays, 1, 30),
            Items = items,
            AuditTrail = new[] { CreateReviewAuditEntry(now, identity.Email, "CampaignCreated", $"{items.Length} review item(s) created.", correlationId, actorUserId: identity.UserId) }
        };
    }

    private static AccessReviewCampaign SanitizeReviewUpdate(
        AccessReviewCampaign existing,
        AccessReviewCampaign incoming,
        OrganizationIdentity identity,
        OrganizationManagementService service,
        Guid organizationId,
        Guid correlationId,
        string assignmentReason = "Shared review assignment update")
    {
        var now = DateTime.UtcNow;
        if (!existing.Items.Select(item => item.Id).OrderBy(id => id).SequenceEqual(incoming.Items.Select(item => item.Id).OrderBy(id => id)))
            throw new InvalidOperationException("A shared review update cannot add or remove review items.");
        ValidateReviewerAssignments(incoming, existing, identity, service, organizationId);
        var audit = existing.AuditTrail.ToList();
        var updatedItems = new List<AccessReviewItem>(existing.Items.Count);
        foreach (var previous in existing.Items)
        {
            var submitted = incoming.Items.Single(item => item.Id == previous.Id);
            if (submitted.PrincipalId != previous.PrincipalId || submitted.EffectiveAccessId != previous.EffectiveAccessId
                || submitted.AccessPathId != previous.AccessPathId || submitted.Provider != previous.Provider
                || submitted.AccessSummary != previous.AccessSummary || submitted.EvidenceSnapshot != previous.EvidenceSnapshot
                || submitted.OwnerSnapshot != previous.OwnerSnapshot)
                throw new InvalidOperationException("A shared review update cannot alter its source evidence.");

            var itemAudit = previous.AuditTrail.ToList();
            var assignedReviewer = submitted.AssignedReviewer?.Trim();
            var itemDueAt = submitted.DueAt?.ToUniversalTime();
            var assignmentChanged = !string.Equals(previous.AssignedReviewer, assignedReviewer, StringComparison.OrdinalIgnoreCase)
                || previous.DueAt != itemDueAt || NormalizeLabel(submitted.AssignedOwner) != previous.AssignedOwner
                || NormalizeLabel(submitted.Team) != previous.Team || NormalizeLabel(submitted.ExternalAccountId) != previous.ExternalAccountId;
            var decisionChanged = submitted.Decision != previous.Decision
                || submitted.Decision == ReviewDecision.ExceptionGranted && submitted.ExceptionExpiresAt?.ToUniversalTime() != previous.ExceptionExpiresAt;
            if (submitted.AssignmentVersion != previous.AssignmentVersion && (assignmentChanged || decisionChanged))
                throw new InvalidOperationException("The assignment changed since this item was loaded. Refresh before updating it.");
            if (assignmentChanged)
            {
                if (itemDueAt.HasValue && itemDueAt <= now) throw new ArgumentException("The review deadline must be in the future.");
                ValidateGovernanceLabels(submitted, previous, service, organizationId, identity);
                if (NormalizeLabel(submitted.AssignedOwner) is { } owner
                    && !service.GetMembersAuthorizedForAction(organizationId, identity, OrganizationAction.ManageRemediations, OrganizationAction.ViewInventory)
                        .Any(member => member.Email.Equals(owner, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("The owner must be an active organization member with inventory access.");
                var details = JsonSerializer.Serialize(new
                {
                    Before = new { previous.AssignedOwner, previous.AssignedReviewer, previous.Team, previous.ExternalAccountId, previous.DueAt },
                    After = new { AssignedOwner = NormalizeLabel(submitted.AssignedOwner), AssignedReviewer = assignedReviewer,
                        Team = NormalizeLabel(submitted.Team), ExternalAccountId = NormalizeLabel(submitted.ExternalAccountId), DueAt = itemDueAt },
                    Reason = CloudEntitle.Security.Services.SensitiveDataRedactor.RedactText(assignmentReason.Trim())
                });
                var entry = CreateReviewAuditEntry(now, identity.Email, "AssignmentChanged", details, correlationId, previous.Id, actorUserId: identity.UserId);
                itemAudit.Add(entry);
                audit.Add(entry);
            }

            var decision = previous.Decision;
            var reviewer = previous.Reviewer;
            var comment = previous.ReviewComment;
            var decidedAt = previous.DecidedAt;
            var exceptionExpiresAt = previous.ExceptionExpiresAt;
            var attestations = previous.Attestations;
            if (decisionChanged)
            {
                if (submitted.Decision == ReviewDecision.Pending) throw new InvalidOperationException("A recorded access-review decision cannot be reset to Pending.");
                if (string.IsNullOrWhiteSpace(previous.AssignedReviewer)
                    || !string.Equals(previous.AssignedReviewer, identity.Email, StringComparison.OrdinalIgnoreCase))
                    throw new UnauthorizedAccessException("Only the assigned reviewer may record a decision.");
                if (string.Equals(existing.CreatedBy, identity.Email, StringComparison.OrdinalIgnoreCase))
                    throw new UnauthorizedAccessException("Campaign creators cannot review their own campaign items.");
                if (submitted.Decision != ReviewDecision.Certified && string.IsNullOrWhiteSpace(submitted.ReviewComment))
                    throw new ArgumentException("A comment is required for investigation, rejection, and exception decisions.");
                if (submitted.ReviewComment?.Length > 4000)
                    throw new ArgumentException("Review rationale cannot exceed 4,000 characters.");
                if (submitted.Decision == ReviewDecision.ExceptionGranted
                    && (!submitted.ExceptionExpiresAt.HasValue || submitted.ExceptionExpiresAt.Value.ToUniversalTime() <= now))
                    throw new ArgumentException("An exception requires a future expiration time.");
                decision = submitted.Decision;
                reviewer = identity.Email;
                comment = submitted.ReviewComment is null ? null : CloudEntitle.Security.Services.SensitiveDataRedactor.RedactText(submitted.ReviewComment.Trim());
                decidedAt = now;
                exceptionExpiresAt = decision == ReviewDecision.ExceptionGranted ? submitted.ExceptionExpiresAt?.ToUniversalTime() : null;
                var evidenceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(previous.EvidenceSnapshot))).ToLowerInvariant();
                var attestation = new AccessReviewAttestation(identity.Email, identity.Email, decision, comment ?? string.Empty,
                    now, evidenceHash, previous.EvidenceSnapshot) { CorrelationId = correlationId, ActorUserId = identity.UserId };
                attestations = attestations.Append(attestation).ToArray();
                itemAudit.Add(CreateReviewAuditEntry(now, identity.Email, "AttestationRecorded", $"{decision} attested against evidence SHA-256 {evidenceHash}.", correlationId, previous.Id, decision, evidenceHash, identity.UserId));
                audit.Add(CreateReviewAuditEntry(now, identity.Email, "AttestationRecorded", $"Item {previous.Id:D}; evidence SHA-256 {evidenceHash}.", correlationId, previous.Id, decision, evidenceHash, identity.UserId));
                var action = $"DecisionRecorded:{decision}";
                itemAudit.Add(CreateReviewAuditEntry(now, identity.Email, action, comment ?? "No comment supplied.", correlationId, previous.Id, decision, evidenceHash, identity.UserId));
                audit.Add(CreateReviewAuditEntry(now, identity.Email, action, $"Item {previous.Id:D}; {comment ?? "No comment supplied."}", correlationId, previous.Id, decision, evidenceHash, identity.UserId));
            }

            updatedItems.Add(previous with
            {
                AssignedReviewer = assignedReviewer,
                AssignedOwner = NormalizeLabel(submitted.AssignedOwner),
                Team = NormalizeLabel(submitted.Team),
                ExternalAccountId = NormalizeLabel(submitted.ExternalAccountId),
                AssignmentVersion = previous.AssignmentVersion + (assignmentChanged ? 1 : 0),
                DueAt = itemDueAt,
                Decision = decision,
                Reviewer = reviewer,
                ReviewComment = comment,
                DecidedAt = decidedAt,
                Attestations = attestations,
                ExceptionExpiresAt = exceptionExpiresAt,
                ExceptionExpiredAt = decisionChanged ? null : previous.ExceptionExpiredAt,
                ExceptionReminderCount = decisionChanged ? 0 : previous.ExceptionReminderCount,
                LastExceptionReminderAt = decisionChanged ? null : previous.LastExceptionReminderAt,
                LastReminderAt = assignmentChanged || decisionChanged ? null : previous.LastReminderAt,
                EscalatedAt = decisionChanged || assignmentChanged ? null : previous.EscalatedAt,
                EscalationReason = decisionChanged || assignmentChanged ? null : previous.EscalationReason,
                AuditTrail = itemAudit
            });
        }

        var name = string.IsNullOrWhiteSpace(incoming.Name) ? existing.Name : incoming.Name.Trim();
        var dueAt = incoming.DueAt > now ? incoming.DueAt.ToUniversalTime() : existing.DueAt;
        if (name != existing.Name || dueAt != existing.DueAt)
            audit.Add(CreateReviewAuditEntry(now, identity.Email, "CampaignUpdated", $"Name={name}; DueAt={dueAt:O}.", correlationId, actorUserId: identity.UserId));
        var status = updatedItems.All(item => item.Decision != ReviewDecision.Pending) ? CampaignStatus.Completed : CampaignStatus.Open;
        return existing with { Name = name, DueAt = dueAt,
            Status = existing.Status is CampaignStatus.Cancelled or CampaignStatus.Draft ? existing.Status : status,
            Items = updatedItems, AuditTrail = audit };
    }

    private static string? NormalizeLabel(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void ValidateGovernanceLabels(AccessReviewItem item, AccessReviewItem? previous,
        OrganizationManagementService service, Guid organizationId, OrganizationIdentity identity)
    {
        if (item.Team?.Length > 120 || item.ExternalAccountId?.Length > 512 || item.AssignedOwner?.Length > 320
            || item.AssignedReviewer?.Length > 320) throw new ArgumentException("Governance labels exceed the supported length.");
        var account = NormalizeLabel(item.ExternalAccountId);
        if (account is not null && (previous is null || previous.ExternalAccountId != account)
            && !service.GetTenantBindings(organizationId, identity).Any(binding => binding.Provider == item.Provider
                && binding.ExternalTenantId.Equals(account, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("The account must be bound to this organization and provider.");
    }

    private static void ValidateReviewerAssignments(
        AccessReviewCampaign incoming,
        AccessReviewCampaign? existing,
        OrganizationIdentity identity,
        OrganizationManagementService service,
        Guid organizationId)
    {
        var eligibleEmails = service.GetMembersAuthorizedForAction(organizationId, identity, OrganizationAction.ManageRemediations)
            .Select(member => member.Email)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in incoming.Items)
        {
            var previous = existing?.Items.FirstOrDefault(candidate => candidate.Id == item.Id);
            var reviewer = item.AssignedReviewer?.Trim();
            if (string.Equals(previous?.AssignedReviewer, reviewer, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(reviewer)) continue;
            var creator = existing?.CreatedBy ?? identity.Email;
            if (string.Equals(identity.Email, reviewer, StringComparison.OrdinalIgnoreCase)
                || string.Equals(creator, reviewer, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Reviewers must be active authorized organization members other than the campaign creator.");
            if (!eligibleEmails.Contains(reviewer))
                throw new InvalidOperationException("The assigned reviewer must be an active organization member authorized to manage access reviews.");
        }
    }

    private static AccessReviewAuditEntry CreateReviewAuditEntry(
        DateTime timestamp,
        string actor,
        string action,
        string details,
        Guid correlationId,
        Guid? itemId = null,
        ReviewDecision? decision = null,
        string? evidenceSha256 = null,
        Guid actorUserId = default)
        => new(timestamp, actor, action, details)
        {
            EventId = Guid.NewGuid(),
            CorrelationId = correlationId,
            ItemId = itemId,
            ActorUserId = actorUserId,
            Decision = decision,
            EvidenceSha256 = evidenceSha256
        };

    internal static AccessReviewCampaign EnsureAuditMetadata(AccessReviewCampaign campaign, Guid correlationId)
    {
        var stamped = new Dictionary<AccessReviewAuditEntry, AccessReviewAuditEntry>();
        AccessReviewAuditEntry EnsureEntry(AccessReviewAuditEntry entry)
        {
            if (stamped.TryGetValue(entry, out var existing)) return existing;
            var result = entry.EventId != Guid.Empty && entry.CorrelationId != Guid.Empty
                ? entry
                : entry with
                {
                    EventId = entry.EventId == Guid.Empty ? Guid.NewGuid() : entry.EventId,
                    CorrelationId = entry.CorrelationId == Guid.Empty ? correlationId : entry.CorrelationId
                };
            stamped[entry] = result;
            return result;
        }

        return campaign with
        {
            AuditTrail = campaign.AuditTrail.Select(EnsureEntry).ToArray(),
            Items = campaign.Items.Select(item => item with { AuditTrail = item.AuditTrail.Select(EnsureEntry).ToArray() }).ToArray()
        };
    }

    private static OrganizationManagementService CreateService(SharedWorkspaceStore store, OrganizationIdentity identity)
        => new(store.CreateOrganizationStore(Guid.Parse(identity.TenantId)));

    private static bool HasReviewLifecycleChanges(AccessReviewCampaign before, AccessReviewCampaign after)
        => before.Status != after.Status || before.AuditTrail.Count != after.AuditTrail.Count
            || before.Items.Zip(after.Items).Any(pair => pair.First.ExceptionExpiredAt != pair.Second.ExceptionExpiredAt
                || pair.First.EscalatedAt != pair.Second.EscalatedAt
                || pair.First.AuditTrail.Count != pair.Second.AuditTrail.Count);

    private static bool TryGetIdentity(ClaimsPrincipal principal, out OrganizationIdentity identity)
    {
        identity = null!;
        if (!Guid.TryParse(principal.FindFirstValue("tid"), out var tenantId) || tenantId == Guid.Empty
            || !Guid.TryParse(principal.FindFirstValue("oid"), out var userId) || userId == Guid.Empty) return false;
        var subject = principal.FindFirstValue("sub") ?? userId.ToString("D");
        var email = principal.FindFirstValue(ClaimTypes.Email) ?? principal.FindFirstValue("email")
            ?? principal.FindFirstValue("preferred_username") ?? principal.FindFirstValue("upn");
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(email)) return false;
        identity = new OrganizationIdentity(userId, subject, tenantId.ToString("D"), email.Trim());
        return true;
    }

    private static ApiEnvelope<T> Envelope<T>(T data, Guid? correlationId = null)
        => new(data, (correlationId ?? Guid.NewGuid()).ToString("N"), ApiContractVersion.Current);
    private static IResult BadRequest(string code, string message) => Results.BadRequest(Error(code, message));
    private static ApiError Error(string code, string message) => new(code, message, Guid.NewGuid().ToString("N"));
}
