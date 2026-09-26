using System.Windows;
using System.Windows.Threading;
using System.IO;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace CloudEntitle.Desktop;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
	private ServiceProvider? _services;

	protected override void OnStartup(StartupEventArgs e)
	{
		base.OnStartup(e);
		var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudEntitle", "Logs");
		Directory.CreateDirectory(logDirectory);
		Log.Logger = new LoggerConfiguration().MinimumLevel.Information().WriteTo.File(Path.Combine(logDirectory, "application-.log"), rollingInterval: RollingInterval.Day).CreateLogger();
		DispatcherUnhandledException += OnDispatcherUnhandledException;
		AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
		TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
		Log.Information("CloudEntitle CIEM application started");

		var services = new ServiceCollection();
		services.AddSingleton<INavigationService, NavigationService>();
		services.AddSingleton<IDialogService, DialogService>();
		services.AddSingleton<MainWindowViewModel>();
		services.AddTransient<DashboardViewModel>();
		services.AddTransient<CloudAccountsViewModel>();
		services.AddTransient<IdentitiesViewModel>();
		services.AddTransient<ResourcesViewModel>();
		services.AddTransient<EntitlementsViewModel>();
		services.AddTransient<EffectiveAccessViewModel>();
		services.AddTransient<RiskFindingsViewModel>();
		services.AddTransient<RecommendationsViewModel>();
		services.AddTransient<ScanHistoryViewModel>();
		services.AddTransient<SettingsViewModel>();
		services.AddSingleton<MainWindow>();
		_services = services.BuildServiceProvider();
		_services.GetRequiredService<MainWindow>().Show();
	}

	protected override void OnExit(ExitEventArgs e)
	{
		Log.Information("CloudEntitle CIEM application stopped");
		Log.CloseAndFlush();
		_services?.Dispose();
		base.OnExit(e);
	}

	private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
	{
		Log.Error(e.Exception, "Unhandled UI exception");
		MessageBox.Show("The application encountered an unexpected error. Details were written to the application log.", "CloudEntitle CIEM", MessageBoxButton.OK, MessageBoxImage.Error);
		e.Handled = true;
	}

	private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e) => Log.Fatal(e.ExceptionObject as Exception, "Unhandled application exception");
	private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e) { Log.Error(e.Exception, "Unobserved task exception"); e.SetObserved(); }
}

