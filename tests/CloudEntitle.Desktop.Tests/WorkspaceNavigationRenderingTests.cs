using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using CloudEntitle.Desktop.ViewModels;
using CloudEntitle.Desktop.Views;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Analytics.Services;
using System.ComponentModel;
using Xunit;

namespace CloudEntitle.Desktop.Tests;

[CollectionDefinition("WPF rendering", DisableParallelization = true)]
public sealed class WpfRenderingCollection;

[Collection("WPF rendering")]
public sealed class WorkspaceNavigationRenderingTests
{
    [Fact]
    public void CompiledDashboard_ShouldSurviveNavigationDuringActiveScan()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Window? window = null;
            System.Windows.Application? app = null;
            try
            {
                var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
                var document = XDocument.Load(Path.Combine(root, "src", "CloudEntitle.Desktop", "App.xaml"));
                XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
                var resources = new XElement(presentation + "ResourceDictionary",
                    document.Root!.Attributes().Where(attribute => attribute.IsNamespaceDeclaration),
                    document.Root.Element(presentation + "Application.Resources")!.Elements());
                foreach (var attribute in resources.DescendantsAndSelf().Attributes().Where(attribute =>
                    attribute.IsNamespaceDeclaration && attribute.Value.StartsWith("clr-namespace:", StringComparison.Ordinal)
                    && !attribute.Value.Contains(";assembly=", StringComparison.Ordinal)))
                    attribute.Value += ";assembly=CloudEntitle.Desktop";
                foreach (var element in resources.Descendants().Where(element =>
                    element.Name.NamespaceName.StartsWith("clr-namespace:", StringComparison.Ordinal)))
                    element.Name = XName.Get(element.Name.LocalName, element.Name.NamespaceName + ";assembly=CloudEntitle.Desktop");
                app = new System.Windows.Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown,
                    Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString())
                };
                var shell = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
                window = new Window
                {
                    Width = 1200,
                    Height = 800,
                    ShowInTaskbar = false,
                    Resources = app.Resources
                };
                var grid = new Grid();
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition());
                var state = new ScanState();
                var indicator = new ScanProgressView { DataContext = state, HorizontalAlignment = HorizontalAlignment.Right };
                grid.Children.Add(indicator);
                Grid.SetRow(shell, 1);
                grid.Children.Add(shell);
                window.Content = grid;
                var demo = new EmptyDemoData();
                var dashboard = new DashboardViewModel(demo, new AnalyticsService());
                var settings = new SettingsViewModel(demo, jiraSettingsStore: new EmptySettings(),
                    ticketingCredentialStore: new EmptyCredentials(), postureWebhookCredentialStore: new EmptyWebhook());
                var pages = new WorkspaceViewModel[] { dashboard, new CloudAccountsViewModel(demo),
                    new IdentitiesViewModel(demo), new ResourcesViewModel(demo), new EntitlementsViewModel(demo), settings };
                var cached = new Dictionary<WorkspaceViewModel, object>();
                WorkspaceView? firstWorkspace = null;
                window.Show();
                for (var index = 0; index < 3; index++)
                {
                    state.IsScanning = true;
                    foreach (var page in pages)
                    {
                        shell.Content = page;
                        window.UpdateLayout();
                        Pump();
                        var view = FindWorkspace(shell);
                        Assert.NotNull(view);
                        firstWorkspace ??= view;
                        Assert.Same(firstWorkspace, view);
                        var host = (ContentControl)view.FindName("PageHost");
                        if (cached.TryGetValue(page, out var previous)) Assert.Same(previous, host.Content);
                        else cached.Add(page, host.Content);
                        Assert.True(indicator.IsVisible);
                        var position = indicator.TranslatePoint(new Point(), grid);
                        Assert.Equal(grid.ActualWidth, position.X + indicator.ActualWidth, 1);
                    }
                    state.IsScanning = false;
                    window.UpdateLayout();
                    Pump();
                    Assert.False(indicator.IsVisible);
                    grid.Children.Remove(indicator);
                    Pump();
                    grid.Children.Add(indicator);
                    window.UpdateLayout();
                    Pump();
                }
            }

            catch (Exception exception) { failure = exception; }
            finally
            {
                window?.Close();
                app?.Shutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "WPF navigation regression exceeded 30 seconds.");
        Assert.Null(failure);
    }

    private sealed class ScanState : INotifyPropertyChanged
    {
        private bool _isScanning;
        public event PropertyChangedEventHandler? PropertyChanged;
        public bool IsScanning
        {
            get => _isScanning;
            set { _isScanning = value; PropertyChanged?.Invoke(this, new(nameof(IsScanning))); }
        }
        public string ScanStatusMessage => "Synthetic scan";
        public string ScanElapsedText => "1 sec";
    }

    private sealed class EmptyDemoData : IDemoDataService
    {
        public event EventHandler? DataChanged { add { } remove { } }
        public bool IsDemoMode => false;
        public Task<DemoSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new DemoSnapshot([], [], [], [], [], [], [], []));
        public Task<DemoStatistics> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new DemoStatistics(0, 0, 0, 0, 0, 0, 0, null));
        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class EmptySettings : IJiraConnectionSettingsStore
    {
        public JiraConnectionSettings Load() => new("");
        public void Save(string baseUrl) => throw new NotSupportedException();
        public void Save(JiraConnectionSettings settings) => throw new NotSupportedException();
    }
    private sealed class EmptyCredentials : ITicketingCredentialStore
    {
        public TicketingCredentials? Read(TicketingProvider provider) => null;
        public void Save(TicketingProvider provider, TicketingCredentials credentials) => throw new NotSupportedException();
        public void Delete(TicketingProvider provider) => throw new NotSupportedException();
    }
    private sealed class EmptyWebhook : IPostureWebhookCredentialStore
    {
        public string? ReadUrl() => null;
        public void SaveUrl(string url) => throw new NotSupportedException();
        public void Delete() => throw new NotSupportedException();
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static WorkspaceView? FindWorkspace(DependencyObject parent)
    {
        if (parent is WorkspaceView view) return view;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = FindWorkspace(VisualTreeHelper.GetChild(parent, index));
            if (child is not null) return child;
        }
        return null;
    }
}
