using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;
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
});

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
app.MapGet("/api/v1/readiness", () => Results.Ok(new ApiReadinessResponse("Ready", true, true, ApiContractVersion.Current))).AllowAnonymous();
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

app.Run();

static bool HasScope(ClaimsPrincipal principal, string requiredScope)
    => principal.FindAll("scp").SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        .Contains(requiredScope, StringComparer.Ordinal);
