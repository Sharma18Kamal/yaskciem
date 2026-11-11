using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using CloudEntitle.Desktop.ViewModels;
using CloudEntitle.Desktop.Views;

namespace CloudEntitle.Desktop;

public partial class MainWindow : Window
{
    private static readonly TimeSpan SessionIdleTimeout = TimeSpan.FromMinutes(5);
    private readonly MainWindowViewModel _viewModel;
    private readonly DispatcherTimer _idleTimer;
    private bool _updatingSections;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        FitStartupWindowToWorkArea(this, SystemParameters.WorkArea);
        _viewModel = viewModel;
        DataContext = viewModel;
        _idleTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = SessionIdleTimeout };
        _idleTimer.Tick += IdleTimer_Tick;
        Loaded += (_, _) =>
        {
            RestartIdleTimer();
        };
        Closed += (_, _) =>
        {
            _idleTimer.Stop();
        };
        PreviewMouseMove += UserActivity;
        PreviewMouseDown += UserActivity;
        AddHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(UserActivity), handledEventsToo: true);
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

    private void WorkspaceContent_Loaded(object sender, RoutedEventArgs e) => RefreshSections();

    private void WorkspaceContent_TargetUpdated(object sender, DataTransferEventArgs e)
    {
        if (e.TargetObject == WorkspaceContent) RefreshSections();
    }

    private void RefreshSections()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            ScrollWorkspaceToTop();
            SectionSelector.ItemsSource = WorkspaceSections.Find(WorkspaceContent);
            SectionSelector.SelectedIndex = -1;
            SectionSelector.Text = string.Empty;
        }));
    }

    private void SectionSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSections || SectionSelector.SelectedItem is not WorkspaceSections.Section section) return;
        if (ActiveWorkspaceScroller is { } scroller) MouseWheelScrolling.StopAnimation(scroller);
        WorkspaceSections.Navigate(section);
    }

    private void SectionSelector_DropDownOpened(object? sender, EventArgs e)
    {
        var title = SectionSelector.Text;
        _updatingSections = true;
        try
        {
            SectionSelector.ItemsSource = WorkspaceSections.Find(WorkspaceContent);
            SectionSelector.Text = title;
        }
        finally { _updatingSections = false; }
    }

    private void BackToTop_Click(object sender, RoutedEventArgs e)
    {
        ScrollWorkspaceToTop();
        SectionSelector.SelectedIndex = -1;
        SectionSelector.Text = string.Empty;
    }

    internal ScrollViewer? ActiveWorkspaceScroller => FindWorkspace(WorkspaceContent)?.ActiveScroller;

    private void ScrollWorkspaceToTop()
    {
        if (ActiveWorkspaceScroller is not { } scroller) return;
        MouseWheelScrolling.StopAnimation(scroller);
        scroller.ScrollToTop();
    }

    private static WorkspaceView? FindWorkspace(DependencyObject root)
    {
        if (root is WorkspaceView workspace) return workspace;
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var found = FindWorkspace(System.Windows.Media.VisualTreeHelper.GetChild(root, index));
            if (found is not null) return found;
        }
        return null;
    }
    private void SetupGuideButton_Click(object sender, RoutedEventArgs e)
    {
        var guidePath = Path.Combine(AppContext.BaseDirectory, "Guides", "getting-started.html");
        try
        {
            if (!File.Exists(guidePath))
                throw new FileNotFoundException("The setup guide is missing. Repair or reinstall YASK-CIEM.", guidePath);
            Process.Start(new ProcessStartInfo(guidePath) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is IOException or Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, $"Unable to open the setup guide: {exception.Message}", "YASK-CIEM",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

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

}
