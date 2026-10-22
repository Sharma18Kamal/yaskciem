using CloudEntitle.Domain;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.LicenseServer.Services;
using CloudEntitle.Infrastructure;
using CloudEntitle.Security.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;
using Npgsql;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks()
	.AddCheck<OperationsReadinessHealthCheck>("operations-ready", tags: ["ready"]);
var operationsMetrics = new OperationsMetrics();
builder.Services.AddSingleton(operationsMetrics);
var workerHealth = new WorkerHealthRegistry();
builder.Services.AddSingleton(workerHealth);
var auditArchiveStatus = new AuditArchiveStatus();
builder.Services.AddSingleton(auditArchiveStatus);
var postgresConnectionString = builder.Configuration.GetConnectionString("Postgres");
if (string.IsNullOrWhiteSpace(postgresConnectionString))
	throw new InvalidOperationException("Configure ConnectionStrings:Postgres before starting the License Server.");
using var postgresDataSource = new NpgsqlDataSourceBuilder(postgresConnectionString).Build();

var entraConfiguration = builder.Configuration.GetSection("AzureAd");
var apiClientId = entraConfiguration["ClientId"];
if (string.IsNullOrWhiteSpace(apiClientId))
    throw new InvalidOperationException("Configure AzureAd:ClientId with the Entra API app registration before starting the shared API.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(entraConfiguration);
builder.Services.AddAuthorization(options =>
{
	options.AddPolicy("CiemApiAccess", policy => policy
		.RequireAuthenticatedUser()
		.RequireClaim("tid")
		.RequireClaim("oid")
		.RequireAssertion(context =>
			HasScope(context.User, "access_as_user")
			|| context.User.Claims.Any(claim => claim.Type == "roles" && claim.Value == "Ciem.Api.Access")));
	options.AddPolicy("CiemAuditRead", policy => policy
		.RequireAuthenticatedUser()
		.RequireClaim("tid")
		.RequireClaim("oid")
		.RequireAssertion(context =>
			HasScope(context.User, "audit.read")
			|| context.User.Claims.Any(claim => claim.Type == "roles" && claim.Value == "Ciem.Audit.Read")));
	options.AddPolicy("CiemOperationsRead", policy => policy
		.RequireAuthenticatedUser()
		.RequireClaim("tid")
		.RequireClaim("oid")
		.RequireAssertion(context =>
			HasScope(context.User, "operations.read")
			|| context.User.Claims.Any(claim => claim.Type == "roles" && claim.Value == "Ciem.Operations.Read")));
});

var auditRetentionDays = builder.Configuration.GetValue("AuditRetention:MinimumRetentionDays", 2555);
IAuditRetentionStore auditStore;
var auditProvider = builder.Configuration["AuditRetention:Provider"];
var integrityKeys = AuditIntegrityKeyRing.FromConfiguration(builder.Configuration);
var integrityKeyBytes = integrityKeys?.CopyCurrentKey();
if (integrityKeys is not null) builder.Services.AddSingleton(integrityKeys);
if (!string.IsNullOrWhiteSpace(auditProvider) && auditProvider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
{
	if (auditRetentionDays is < 1 or > 36500 || integrityKeys is null)
	{
		auditStore = new UnavailableAuditRetentionStore("PostgreSQL audit configuration is incomplete; configure the retention period and versioned integrity keys through a secret provider.");
	}
	else
	{
		auditStore = new PostgresAuditRetentionStore(postgresDataSource, auditRetentionDays, integrityKeys);
	}
}
else if (builder.Environment.IsDevelopment() && (string.IsNullOrWhiteSpace(auditProvider) || auditProvider.Equals("LocalFile", StringComparison.OrdinalIgnoreCase))
	&& auditRetentionDays is >= 1 and <= 36500 && integrityKeyBytes is not null)
{
	var auditDirectory = builder.Configuration["AuditRetention:Directory"]
		?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YASK-CIEM", "CentralAudit");
	auditStore = new AppendOnlyAuditRetentionStore(auditDirectory, auditRetentionDays, integrityKeyBytes);
}
else
{
	auditStore = new UnavailableAuditRetentionStore("Production requires AuditRetention:Provider=Postgres and an initialized PostgreSQL audit schema; the local file store is development-only.");
}
builder.Services.AddSingleton<IAuditRetentionStore>(_ => auditStore);
var maxSchedulesPerTenant = builder.Configuration.GetValue("Operations:Limits:MaxScanSchedulesPerTenant", 100);
var maxActiveJobsPerTenant = builder.Configuration.GetValue("Operations:Limits:MaxActiveScanJobsPerTenant", 25);
var sharedWorkspaceStore = new SharedWorkspaceStore(postgresDataSource, maxSchedulesPerTenant, maxActiveJobsPerTenant);
builder.Services.AddSingleton<SharedWorkspaceStore>(_ => sharedWorkspaceStore);
builder.Services.AddSingleton<ICloudCommandRunner, ProcessCloudCommandRunner>();
builder.Services.AddSingleton<IScanJobExecutor, CloudProviderScanJobExecutor>();
builder.Services.AddHostedService(serviceProvider => new ScanOrchestrationWorker(
	sharedWorkspaceStore, serviceProvider.GetRequiredService<IScanJobExecutor>(),
	serviceProvider.GetRequiredService<ILogger<ScanOrchestrationWorker>>(),
	serviceProvider.GetRequiredService<WorkerHealthRegistry>(),
	serviceProvider.GetRequiredService<OperationsMetrics>()));
builder.Services.AddSingleton<ICentralNotificationTransport, CentralWebhookTransport>();
builder.Services.AddHostedService(serviceProvider => new CentralNotificationWorker(
	sharedWorkspaceStore, serviceProvider.GetRequiredService<ICentralNotificationTransport>(),
	serviceProvider.GetRequiredService<ILogger<CentralNotificationWorker>>(),
	serviceProvider.GetRequiredService<WorkerHealthRegistry>(),
	serviceProvider.GetRequiredService<OperationsMetrics>()));
builder.Services.AddHostedService(serviceProvider => new GovernanceDeadlineWorker(
	sharedWorkspaceStore, serviceProvider.GetRequiredService<ILogger<GovernanceDeadlineWorker>>(),
	serviceProvider.GetRequiredService<WorkerHealthRegistry>()));
builder.Services.AddHostedService<OperationsTelemetrySampler>();
builder.Services.AddHostedService<OperationsRetentionWorker>();

var auditArchiveConfiguration = builder.Configuration.GetSection("AuditArchive");
var archiveCredentialError = string.Empty;
WindowsGenericCredential? archiveDatabaseCredential = null;
WindowsGenericCredential? s3Credential = null;
try
{
	var databaseCredentialTarget = auditArchiveConfiguration["DatabaseCredentialTarget"]
		?? "YASK-CIEM/LicenseServer/AuditArchive/Postgres";
	var s3CredentialTarget = auditArchiveConfiguration["S3CredentialTarget"]
		?? "YASK-CIEM/LicenseServer/AuditArchive/S3";
	archiveDatabaseCredential = WindowsGenericCredentialStore.Read(databaseCredentialTarget);
	s3Credential = WindowsGenericCredentialStore.Read(s3CredentialTarget);
}
catch (Exception exception)
{
	archiveCredentialError = SensitiveDataRedactor.RedactText(exception.Message);
	auditArchiveStatus.Set(false, $"Unable to read archive credentials from Windows Credential Manager: {archiveCredentialError}");
}
var auditArchiveDatabase = builder.Configuration.GetConnectionString("AuditArchive") ?? archiveDatabaseCredential?.Secret;
var auditArchiveProvider = auditArchiveConfiguration["Provider"];
var archiveAccessKey = auditArchiveConfiguration["AccessKey"] ?? s3Credential?.UserName;
var archiveSecretKey = auditArchiveConfiguration["SecretKey"] ?? s3Credential?.Secret;
if (auditArchiveProvider?.Equals("S3ObjectLock", StringComparison.OrdinalIgnoreCase) == true
    && !string.IsNullOrWhiteSpace(auditArchiveDatabase)
    && string.IsNullOrEmpty(archiveCredentialError)
    && !string.IsNullOrWhiteSpace(auditArchiveConfiguration["Endpoint"])
    && !string.IsNullOrWhiteSpace(auditArchiveConfiguration["Region"])
    && !string.IsNullOrWhiteSpace(auditArchiveConfiguration["Bucket"])
	&& !string.IsNullOrWhiteSpace(archiveAccessKey)
	&& !string.IsNullOrWhiteSpace(archiveSecretKey)
    && integrityKeyBytes is not null)
{
	NpgsqlDataSource? archiveDataSource = null;
	S3ObjectLockAuditArchive? archiveDestination = null;
	try
	{
		archiveDataSource = new NpgsqlDataSourceBuilder(auditArchiveDatabase).Build();
		var archiveRetentionDays = builder.Configuration.GetValue("AuditRetention:MinimumRetentionDays", 2555);
		var segmentEventCount = auditArchiveConfiguration.GetValue("SegmentEventCount", 500);
		var objectPrefix = auditArchiveConfiguration["ObjectPrefix"] ?? "yask-ciem/audit/v1";
		var pollIntervalSeconds = auditArchiveConfiguration.GetValue("PollIntervalSeconds", 60);
		var maximumPendingEvents = auditArchiveConfiguration.GetValue("MaxPendingEvents", 5000L);
		var maximumLagMinutes = auditArchiveConfiguration.GetValue("MaxLagMinutes", 60);
		var alertWebhookUrl = builder.Configuration["Monitoring:AlertWebhookUrl"]
			?? Environment.GetEnvironmentVariable("YASK_CIEM_ALERT_WEBHOOK_URL");
		archiveDestination = new S3ObjectLockAuditArchive(
			auditArchiveConfiguration["Endpoint"]!, auditArchiveConfiguration["Region"]!, auditArchiveConfiguration["Bucket"]!,
			archiveAccessKey!, archiveSecretKey!, archiveRetentionDays);
		var segmentArchiver = new PostgresAuditSegmentArchiver(archiveDataSource, integrityKeys!,
			segmentEventCount, archiveRetentionDays, objectPrefix);
		builder.Services.AddSingleton(archiveDataSource);
		builder.Services.AddSingleton<IAuditSegmentArchive>(archiveDestination);
		builder.Services.AddSingleton(segmentArchiver);
		builder.Services.AddHostedService(serviceProvider => new AuditArchiveWorker(
			serviceProvider.GetRequiredService<PostgresAuditSegmentArchiver>(),
			serviceProvider.GetRequiredService<IAuditSegmentArchive>(),
			serviceProvider.GetRequiredService<AuditArchiveStatus>(), pollIntervalSeconds,
			maximumPendingEvents, maximumLagMinutes,
			alertWebhookUrl,
			serviceProvider.GetRequiredService<ILogger<AuditArchiveWorker>>()));
	}
	catch (Exception exception)
	{
		archiveDestination?.Dispose();
		archiveDataSource?.Dispose();
		auditArchiveStatus.Set(false, $"WORM audit archive configuration failed: {SensitiveDataRedactor.RedactText(exception.Message)}");
	}
}
else
{
	auditArchiveStatus.Set(false, "Configure AuditArchive:Provider=S3ObjectLock, the independent archive PostgreSQL connection, HTTPS endpoint, bucket, region, and secret-provider credentials.");
}
if (integrityKeyBytes is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(integrityKeyBytes);

var app = builder.Build();

var useHsts = app.Configuration.GetValue("Security:UseHsts", !app.Environment.IsDevelopment());
app.Use(async (context, next) =>
{
	SecurityHeaderPolicy.Apply(context.Response, useHsts);
	await next();
});
app.UseMiddleware<ApiRequestMetricsMiddleware>();
app.UseOperationsConsole();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health");
app.MapGet("/health/live", () => Results.Ok(new { status = "Healthy" })).AllowAnonymous();
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
	Predicate = check => check.Tags.Contains("ready")
}).AllowAnonymous();
app.MapGet("/metrics", (OperationsMetrics metrics) =>
	Results.Text(metrics.ToPrometheusText(), "text/plain; version=0.0.4; charset=utf-8"))
	.RequireAuthorization("CiemOperationsRead");

app.MapGet("/api/v1/health", () => Results.Ok(new ApiHealthResponse("Healthy", ApiContractVersion.Current, DateTime.UtcNow))).AllowAnonymous();
app.MapGet("/api/v1/readiness", async (IAuditRetentionStore retentionStore, SharedWorkspaceStore workspaceStore,
	AuditArchiveStatus archiveStatus, CancellationToken cancellationToken) =>
{
	var audit = await retentionStore.CheckReadinessAsync(cancellationToken);
	var workspaceReady = await workspaceStore.IsReadyAsync(cancellationToken);
	var operationsReady = await workspaceStore.CheckOperationsReadinessAsync(cancellationToken);
	var workerStatuses = workerHealth.Snapshot();
	var workersReady = new[] { "scan", "notifications", "governance", "operations", "retention" }
		.All(name => workerStatuses.TryGetValue(name, out var status)
			&& DateTime.UtcNow - status.LastHeartbeatUtc <= TimeSpan.FromSeconds(90)
			&& (name != "retention" || status.LastSuccessUtc is DateTime success
				&& DateTime.UtcNow - success <= TimeSpan.FromHours(36)));
	var archive = archiveStatus.Current;
	var snapshot = operationsMetrics.Current;
	var queueReady = snapshot is not null
		&& snapshot.OldestQueuedScanSeconds <= builder.Configuration.GetValue("Operations:Readiness:MaxQueueAgeSeconds", 3600)
		&& snapshot.QueuedScanJobs <= builder.Configuration.GetValue("Operations:Readiness:MaxQueueDepth", 1000);
	var productionReady = audit.Ready && audit.ProductionReady && workspaceReady && operationsReady && workersReady && queueReady && archive.Ready;
	var response = new ApiReadinessResponse(productionReady ? "Ready" : "NotReady",
		audit.Ready && workspaceReady && operationsReady, productionReady, ApiContractVersion.Current)
	{
		AuditStorageReady = audit.Ready && workspaceReady,
		OperationsReady = operationsReady,
		QueueReady = queueReady,
		WorkersReady = workersReady,
		ProductionReady = productionReady,
		AuditStorageDetails = workspaceReady ? audit.Details : $"{audit.Details} PostgreSQL shared-workspace schema is unavailable.",
		AuditArchiveReady = archive.Ready,
		AuditArchiveDetails = archive.Details,
		AuditArchivePendingEvents = archive.PendingEvents,
		AuditArchiveOldestPendingAtUtc = archive.OldestPendingAtUtc,
		AuditArchiveLastSuccessAtUtc = archive.LastSuccessfulArchiveAtUtc
	};
	return Results.Json(response, statusCode: productionReady ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
}).AllowAnonymous();
app.MapGet("/api/v1/providers", () => Results.Ok(new ApiEnvelope<IReadOnlyList<ProviderCapabilityDto>>(
	new[]
	{
		new ProviderCapabilityDto(CloudProvider.Azure, true, true, "Azure CLI / DefaultAzureCredential"),
		new ProviderCapabilityDto(CloudProvider.Aws, true, true, "AWS CLI / default credential chain"),
		new ProviderCapabilityDto(CloudProvider.Gcp, true, true, "gcloud / Application Default Credentials")
	},
	Guid.NewGuid().ToString("N"),
	ApiContractVersion.Current))).AllowAnonymous();

app.MapGet("/api/v1/identity", (ClaimsPrincipal principal) =>
{
	var tenantId = principal.FindFirstValue("tid");
	var objectId = principal.FindFirstValue("oid");
	if (!Guid.TryParse(tenantId, out var tenantGuid) || !Guid.TryParse(objectId, out var objectGuid))
		return Results.Forbid();

	return Results.Ok(new ApiEnvelope<ApiIdentityDto>(
		new ApiIdentityDto(tenantGuid, objectGuid),
		Guid.NewGuid().ToString("N"),
		ApiContractVersion.Current));
})
	.RequireAuthorization("CiemApiAccess");

app.MapPost("/api/v1/audit/events", async (ClaimsPrincipal principal, CentralAuditAppendRequest request, IAuditRetentionStore store, CancellationToken cancellationToken) =>
{
	if (!Guid.TryParse(principal.FindFirstValue("tid"), out var tenantId)
		|| !Guid.TryParse(principal.FindFirstValue("oid"), out var actorObjectId)
		|| tenantId == Guid.Empty || actorObjectId == Guid.Empty)
		return Results.Forbid();

	try
	{
		var entry = await store.AppendAsync(tenantId, actorObjectId, request, cancellationToken);
		return Results.Created($"/api/v1/audit/events?afterSequence={entry.Sequence - 1}",
			new ApiEnvelope<CentralAuditEventDto>(entry, Guid.NewGuid().ToString("N"), ApiContractVersion.Current));
	}
	catch (ArgumentException exception)
	{
		return Results.BadRequest(new ApiError("invalid_audit_event", exception.Message, Guid.NewGuid().ToString("N")));
	}
	catch (InvalidOperationException exception)
	{
		return Results.Conflict(new ApiError("audit_event_conflict", exception.Message, Guid.NewGuid().ToString("N")));
	}
	catch (InvalidDataException)
	{
		return Results.Problem("Central audit integrity validation failed; writes are disabled until the log is investigated.", statusCode: StatusCodes.Status503ServiceUnavailable);
	}
})
	.RequireAuthorization("CiemApiAccess");

app.MapGet("/api/v1/audit/events", async (ClaimsPrincipal principal, long afterSequence, int limit, IAuditRetentionStore store, CancellationToken cancellationToken) =>
{
	if (!Guid.TryParse(principal.FindFirstValue("tid"), out var tenantId) || tenantId == Guid.Empty)
		return Results.Forbid();
	try
	{
		var entries = await store.ReadAsync(tenantId, afterSequence, limit, cancellationToken);
		return Results.Ok(new ApiEnvelope<IReadOnlyList<CentralAuditEventDto>>(entries, Guid.NewGuid().ToString("N"), ApiContractVersion.Current));
	}
	catch (ArgumentOutOfRangeException exception)
	{
		return Results.BadRequest(new ApiError("invalid_audit_query", exception.Message, Guid.NewGuid().ToString("N")));
	}
	catch (InvalidDataException)
	{
		return Results.Problem("Central audit integrity validation failed; reads are disabled until the log is investigated.", statusCode: StatusCodes.Status503ServiceUnavailable);
	}
})
	.RequireAuthorization("CiemAuditRead");

app.MapSharedWorkspaceEndpoints(sharedWorkspaceStore);

static bool HasScope(ClaimsPrincipal principal, string requiredScope)
	=> principal.FindAll("scp").SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
		.Contains(requiredScope, StringComparer.Ordinal);
app.Run();
