using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CloudEntitle.Desktop.ViewModels;

namespace CloudEntitle.Desktop;

public partial class MainWindow : Window
{
    private static readonly TimeSpan SessionIdleTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TrialStatusRefreshInterval = TimeSpan.FromSeconds(30);
    private readonly MainWindowViewModel _viewModel;
    private readonly DispatcherTimer _idleTimer;
    private readonly DispatcherTimer _trialStatusTimer;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        FitStartupWindowToWorkArea(this, SystemParameters.WorkArea);
        _viewModel = viewModel;
        DataContext = viewModel;
        _idleTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = SessionIdleTimeout };
        _idleTimer.Tick += IdleTimer_Tick;
        _trialStatusTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TrialStatusRefreshInterval };
        _trialStatusTimer.Tick += TrialStatusTimer_Tick;
        Loaded += (_, _) =>
        {
            RestartIdleTimer();
            _trialStatusTimer.Start();
        };
        Closed += (_, _) =>
        {
            _idleTimer.Stop();
            _trialStatusTimer.Stop();
        };
        PreviewMouseMove += UserActivity;
        PreviewMouseDown += UserActivity;
        PreviewMouseWheel += UserActivity;
        PreviewKeyDown += UserActivity;
        PreviewTouchDown += UserActivity;
    }

    internal static void FitStartupWindowToWorkArea(Window window, Rect workArea)
    {
        window.MinWidth = Math.Min(window.MinWidth, workArea.Width);
        window.MinHeight = Math.Min(window.MinHeight, workArea.Height);
        window.Width = Math.Min(window.Width, workArea.Width);
        window.Height = Math.Min(window.Height, workArea.Height);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = workArea.Left + (workArea.Width - window.Width) / 2;
        window.Top = workArea.Top + (workArea.Height - window.Height) / 2;
    }

    private void WindowHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void UserActivity(object? sender, RoutedEventArgs e) => RestartIdleTimer();

    private void RestartIdleTimer()
    {
        _idleTimer.Stop();
        _idleTimer.Start();
    }

    private async void IdleTimer_Tick(object? sender, EventArgs e)
    {
        _idleTimer.Stop();
        if (_viewModel.CurrentViewModel is WorkspaceViewModel { IsLoading: true })
        {
            RestartIdleTimer();
            return;
        }

        try
        {
            await _viewModel.DisconnectCloudForInactivityAsync();
        }
        finally
        {
            RestartIdleTimer();
        }
    }

    private async void TrialStatusTimer_Tick(object? sender, EventArgs e)
    {
        await _viewModel.RefreshTrialStatusAsync();
    }
}
