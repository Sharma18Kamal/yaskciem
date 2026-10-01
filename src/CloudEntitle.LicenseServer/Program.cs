using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.Security.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();

var app = builder.Build();

var useHsts = app.Configuration.GetValue("Security:UseHsts", !app.Environment.IsDevelopment());
app.Use(async (context, next) =>
{
	SecurityHeaderPolicy.Apply(context.Response, useHsts);
	await next();
});

app.MapHealthChecks("/health");

app.MapGet("/api/v1/health", () => Results.Ok(new ApiHealthResponse("Healthy", ApiContractVersion.Current, DateTime.UtcNow)));
app.MapGet("/api/v1/readiness", () => Results.Ok(new ApiReadinessResponse("Ready", true, true, ApiContractVersion.Current)));
app.MapGet("/api/v1/providers", () => Results.Ok(new ApiEnvelope<IReadOnlyList<ProviderCapabilityDto>>(
	new[]
	{
		new ProviderCapabilityDto(CloudProvider.Azure, true, true, "Azure CLI / DefaultAzureCredential"),
		new ProviderCapabilityDto(CloudProvider.Aws, true, true, "AWS CLI / default credential chain"),
		new ProviderCapabilityDto(CloudProvider.Gcp, true, true, "gcloud / Application Default Credentials")
	},
	Guid.NewGuid().ToString("N"),
	ApiContractVersion.Current)));

app.Run();
