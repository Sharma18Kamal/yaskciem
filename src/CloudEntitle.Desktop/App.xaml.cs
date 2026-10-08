using System.Windows;
using System.Windows.Threading;
using System.IO;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Desktop.ViewModels;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Analytics.Services;
using CloudEntitle.Cloud.Aws;
using CloudEntitle.Cloud.Azure;
using CloudEntitle.Cloud.Gcp;
using CloudEntitle.Infrastructure;
using CloudEntitle.Infrastructure.Persistence;
using CloudEntitle.Licensing;
using CloudEntitle.IdentityGovernance.Services;
using CloudEntitle.Compliance.Services;
using CloudEntitle.EffectiveAccess.Abstractions;
using CloudEntitle.EffectiveAccess.Services;
using CloudEntitle.RemediationPlanning.Abstractions;
using CloudEntitle.RemediationPlanning.Services;
using CloudEntitle.OrganizationManagement.Abstractions;
using CloudEntitle.OrganizationManagement.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace CloudEntitle.Desktop;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
	private ServiceProvider? _services;

	protected override async void OnStartup(StartupEventArgs e)
	{
		base.OnStartup(e);
		var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudEntitle", "Logs");
		Directory.CreateDirectory(logDirectory);
		Log.Logger = new LoggerConfiguration().MinimumLevel.Information().WriteTo.File(Path.Combine(logDirectory, "application-.log"), rollingInterval: RollingInterval.Day).CreateLogger();
		DispatcherUnhandledException += OnDispatcherUnhandledException;
		AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
		TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
		Log.Information("YASK-CIEM application started");

		var services = new ServiceCollection();
		var databaseConfiguration = new DatabaseConfiguration();
		await new DbInitializer(databaseConfiguration).InitializeAsync();
		services.AddDbContextFactory<CloudEntitleDbContext>(options => options.UseSqlite(databaseConfiguration.ConnectionString));
		services.AddSingleton<IDemoDataService, DemoDataService>();
		services.AddSingleton<IEffectiveAccessEngine, EffectiveAccessEngine>();
		services.AddSingleton<IAnalyticsService, AnalyticsService>();
		services.AddSingleton<IRecommendationStore, SqliteRecommendationStore>();
		services.AddSingleton<IReportService, SqliteReportService>();
		services.AddSingleton<ILiveScanHistoryStore, LocalLiveScanHistoryStore>();
		services.AddSingleton<IMonitoringScheduleStore, LocalMonitoringScheduleStore>();
		services.AddSingleton<ContinuousMonitoringService>();
		services.AddSingleton<ITrialService, TrialService>();
		services.AddSingleton<ICloudCommandRunner, ProcessCloudCommandRunner>();
		services.AddSingleton<IPostureNotificationChannel>(_ => new WebhookPostureNotificationChannel());
		services.AddSingleton<IJiraConnectionSettingsStore, LocalJiraConnectionSettingsStore>();
		services.AddSingleton<IRemediationPlanner, RemediationPlanner>();
		services.AddSingleton<IRemediationSimulator, RemediationSimulator>();
		services.AddSingleton<IRemediationEngine, RemediationEngine>();
		services.AddSingleton<IRemediationPlanStore>(provider => new LocalRemediationPlanStore(
			contextFactory: provider.GetRequiredService<IDbContextFactory<CloudEntitleDbContext>>()));
		services.AddSingleton<RemediationWorkflowService>();
		services.AddSingleton<IOrganizationStore, LocalOrganizationStore>();
		services.AddSingleton<AuthenticatedOrganizationContext>();
		services.AddSingleton<OrganizationManagementService>();
		services.AddSingleton<IOrganizationSsoService, EntraOrganizationSsoService>();
		services.AddSingleton<IdentityGovernanceService>();
		services.AddSingleton<ComplianceAssessmentService>();
		services.AddSingleton<ComplianceExportService>();
		services.AddSingleton<IComplianceExportWorkflow, ComplianceExportWorkflow>();
		services.AddSingleton<IAccessReviewStore, LocalAccessReviewStore>();
		services.AddSingleton<ICloudProvider>(provider => new AzureProvider(provider.GetRequiredService<ICloudCommandRunner>(), useLiveDiscovery: true));
		services.AddSingleton<ICloudProvider>(provider => new AwsProvider(provider.GetRequiredService<ICloudCommandRunner>(), useLiveDiscovery: true));
		services.AddSingleton<ICloudProvider>(provider => new GcpProvider(provider.GetRequiredService<ICloudCommandRunner>(), useLiveDiscovery: true));
		services.AddSingleton<IKubernetesRbacProvider>(provider => new KubernetesRbacScanner(provider.GetRequiredService<ICloudCommandRunner>()));
		services.AddSingleton<ILiveCloudScanService, LiveCloudScanService>();
		services.AddSingleton<INavigationService, NavigationService>();
		services.AddSingleton<CloudConnectionState>();
		services.AddSingleton<IDialogService, DialogService>();
		services.AddSingleton<IUserFeedbackService, LocalUserFeedbackService>();
		services.AddSingleton<ICloudAuthenticationLauncher, CloudAuthenticationLauncher>();
		services.AddSingleton<MainWindowViewModel>();
		services.AddTransient<DashboardViewModel>();
		services.AddTransient<CloudAccountsViewModel>();
		services.AddTransient<IdentitiesViewModel>();
		services.AddTransient<ResourcesViewModel>();
		services.AddTransient<EntitlementsViewModel>();
		services.AddTransient<EffectiveAccessViewModel>();
		services.AddTransient<AccessPathExplorerViewModel>();
		services.AddTransient<RiskFindingsViewModel>();
		services.AddTransient<RecommendationsViewModel>();
		services.AddTransient<RemediationViewModel>();
		services.AddSingleton<OrganizationViewModel>();
		services.AddTransient<AccessReviewViewModel>();
		services.AddTransient<ScanHistoryViewModel>();
		services.AddTransient<FeedbackViewModel>();
		services.AddTransient<SettingsViewModel>();
		services.AddSingleton<MainWindow>();
		_services = services.BuildServiceProvider();
		await _services.GetRequiredService<ContinuousMonitoringService>().InitializeAsync();
		await _services.GetRequiredService<MainWindowViewModel>().InitializeTrialAsync();
		_services.GetRequiredService<MainWindow>().Show();
	}

	protected override void OnExit(ExitEventArgs e)
	{
		Log.Information("YASK-CIEM application stopped");
		Log.CloseAndFlush();
		_services?.Dispose();
		base.OnExit(e);
	}

	private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
	{
		Log.Error(e.Exception, "Unhandled UI exception");
		MessageBox.Show("The application encountered an unexpected error. Details were written to the application log.", "YASK-CIEM", MessageBoxButton.OK, MessageBoxImage.Error);
		e.Handled = true;
	}

	private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e) => Log.Fatal(e.ExceptionObject as Exception, "Unhandled application exception");
	private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e) { Log.Error(e.Exception, "Unobserved task exception"); e.SetObserved(); }
}

