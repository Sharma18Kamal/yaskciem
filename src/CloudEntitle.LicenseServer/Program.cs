using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.LicenseServer.Services;
using CloudEntitle.Security.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();
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
});

var auditRetentionDays = builder.Configuration.GetValue("AuditRetention:MinimumRetentionDays", 2555);
IAuditRetentionStore auditStore;
var auditProvider = builder.Configuration["AuditRetention:Provider"];
var auditIntegrityKey = builder.Configuration["AuditRetention:IntegrityKey"];
var integrityKeyBytes = TryDecodeAuditIntegrityKey(auditIntegrityKey);
if (!string.IsNullOrWhiteSpace(auditProvider) && auditProvider.Equals("AzureBlob", StringComparison.OrdinalIgnoreCase))
{
	var storageUri = builder.Configuration["AuditRetention:BlobServiceUri"];
	var containerName = builder.Configuration["AuditRetention:ContainerName"];
	var accountResourceId = builder.Configuration["AuditRetention:StorageAccountResourceId"];
	var managedIdentityClientId = builder.Configuration["AuditRetention:ManagedIdentityClientId"];
	if (auditRetentionDays is < 1 or > 36500 || integrityKeyBytes is null
		|| !Uri.TryCreate(storageUri, UriKind.Absolute, out var parsedStorageUri)
		|| string.IsNullOrWhiteSpace(containerName) || string.IsNullOrWhiteSpace(accountResourceId)
		|| string.IsNullOrWhiteSpace(managedIdentityClientId))
	{
		auditStore = new UnavailableAuditRetentionStore("Azure Blob audit configuration is incomplete; configure HTTPS storage URI, container, account resource ID, managed identity client ID, a retention period, and an integrity key from a secret provider.");
	}
	else
	{
		try
		{
			auditStore = new AzureBlobAuditRetentionStore(parsedStorageUri, containerName, accountResourceId,
				auditRetentionDays, integrityKeyBytes, managedIdentityClientId);
		}
		catch (Exception exception)
		{
			auditStore = new UnavailableAuditRetentionStore($"Azure Blob audit configuration is invalid: {exception.Message}");
		}
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
	auditStore = new UnavailableAuditRetentionStore("Production requires AuditRetention:Provider=AzureBlob and a verified locked Azure Blob retention policy; the local file store is development-only.");
}
if (integrityKeyBytes is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(integrityKeyBytes);
builder.Services.AddSingleton<IAuditRetentionStore>(auditStore);
var sharedConnectionString = builder.Configuration.GetConnectionString("SharedWorkspace")
	?? builder.Configuration["SharedData:ConnectionString"];
if (string.IsNullOrWhiteSpace(sharedConnectionString))
{
	var sharedDirectory = builder.Configuration["SharedData:Directory"]
		?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YASK-CIEM", "SharedApi");
	Directory.CreateDirectory(sharedDirectory);
	sharedConnectionString = $"Data Source={Path.Combine(sharedDirectory, "shared-workspace.db")};Cache=Shared";
}
var sharedWorkspaceStore = new SharedWorkspaceStore(sharedConnectionString);
await sharedWorkspaceStore.InitializeAsync();
builder.Services.AddSingleton(sharedWorkspaceStore);

var app = builder.Build();

var useHsts = app.Configuration.GetValue("Security:UseHsts", !app.Environment.IsDevelopment());
app.Use(async (context, next) =>
{
	SecurityHeaderPolicy.Apply(context.Response, useHsts);
	await next();
});
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health");

app.MapGet("/api/v1/health", () => Results.Ok(new ApiHealthResponse("Healthy", ApiContractVersion.Current, DateTime.UtcNow))).AllowAnonymous();
app.MapGet("/api/v1/readiness", async (IAuditRetentionStore retentionStore, CancellationToken cancellationToken) =>
{
	var audit = await retentionStore.CheckReadinessAsync(cancellationToken);
	var productionReady = audit.Ready && audit.ProductionReady;
	var response = new ApiReadinessResponse(productionReady ? "Ready" : "NotReady", true, productionReady, ApiContractVersion.Current)
	{
		AuditStorageReady = audit.Ready,
		ProductionReady = productionReady,
		AuditStorageDetails = audit.Details
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

app.Run();

static bool HasScope(ClaimsPrincipal principal, string requiredScope)
    => principal.FindAll("scp").SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        .Contains(requiredScope, StringComparer.Ordinal);

static byte[]? TryDecodeAuditIntegrityKey(string? value)
{
	if (string.IsNullOrWhiteSpace(value)) return null;
	try
	{
		var bytes = Convert.FromBase64String(value);
		if (bytes.Length >= 32) return bytes;
		System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
		return null;
	}
	catch (FormatException) { return null; }
}
