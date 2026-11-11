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
using CloudEntitle.Domain;
using CloudEntitle.EffectiveAccess.Models;
using CloudEntitle.EffectiveAccess.Services;
using CloudEntitle.OrganizationManagement.Services;
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
                    new IdentitiesViewModel(demo), new ResourcesViewModel(demo), new EntitlementsViewModel(demo),
                    new EffectiveAccessViewModel(demo), new AccessPathExplorerViewModel(demo), new ScanHistoryViewModel(demo),
                    new WhatIfSimulatorViewModel(demo, new EmptyWhatIfData(), new(new EffectiveAccessEngine()),
                        new DialogService(), new AuthenticatedOrganizationContext(), new OrganizationManagementService()), settings };
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
                        if (page is AccessPathExplorerViewModel)
                        {
                            Assert.NotNull(view.ActiveScroller);
                            Assert.Equal("TabScroller", view.ActiveScroller.Name);
                        }
                        if (page == settings)
                        {
                            var sections = WorkspaceSections.Find(view);
                            Assert.Contains(sections, section => section.Title == "TICKETING CONNECTION");
                            Assert.Contains(sections, section => section.Title == "LOCAL DATA BACKUP");
                        }
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
                var picker = new ReportFormatDialog { Resources = app.Resources, Owner = window };
                try
                {
                    picker.Show();
                    Pump();
                    var selector = (ComboBox)picker.FindName("FormatSelector");
                    Assert.Equal(new[] { ".json", ".csv", ".html", ".xml", ".pdf", ".xlsx" },
                        ReportFormatDialog.Formats.Select(format => format.Extension));
                    foreach (var format in ReportFormatDialog.Formats)
                    {
                        selector.SelectedItem = format;
                        Pump();
                        Assert.Same(format, picker.SelectedFormat);
                        Assert.Equal(format.Description, ((TextBlock)picker.FindName("FormatDescription")).Text);
                    }
                }
                finally { picker.Close(); }
                var navigation = new TestNavigation(dashboard, settings);
                var main = new MainWindow(new MainWindowViewModel(navigation)) { ShowInTaskbar = false };
                try
                {
                    main.Show();
                    Pump();
                    navigation.NavigateTo<SettingsViewModel>();
                    main.UpdateLayout();
                    Pump();
                    var sections = (ComboBox)main.FindName("SectionSelector");
                    var scroller = main.ActiveWorkspaceScroller!;
                    Assert.NotNull(scroller);
                    Assert.Contains(sections.Items.Cast<WorkspaceSections.Section>(),
                        section => section.Title == "TICKETING CONNECTION");
                    sections.SelectedItem = sections.Items.Cast<WorkspaceSections.Section>()
                        .Single(section => section.Title == "TICKETING CONNECTION");
                    Pump();
                    var ticketing = sections.Items.Cast<WorkspaceSections.Section>()
                        .Single(section => section.Title == "TICKETING CONNECTION");
                    Assert.Equal("Ticketing", Assert.Single(ticketing.Tabs).Header);
                    Assert.True(ticketing.Tabs[0].IsSelected);
                    Assert.True(ticketing.Target.IsVisible);
                    var credential = Assert.Single(Descendants<PasswordBox>(main),
                        box => box.Name == "TicketingCredentialBox");
                    credential.Password = "unsaved-test-draft";
                    var application = sections.Items.Cast<WorkspaceSections.Section>()
                        .Single(section => section.Title == "APPLICATION");
                    sections.SelectedItem = application;
                    Pump();
                    sections.SelectedItem = ticketing;
                    Pump();
                    Assert.Same(credential, Assert.Single(Descendants<PasswordBox>(main),
                        box => box.Name == "TicketingCredentialBox"));
                    Assert.Equal("unsaved-test-draft", credential.Password);
                    credential.Clear();
                    var back = (Button)main.FindName("BackToTopButton");
                    back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Pump();
                    Assert.Equal(0, main.ActiveWorkspaceScroller!.VerticalOffset);
                    foreach (var size in new[] { new Size(900, 600), new Size(1440, 900) })
                    {
                        main.Width = size.Width;
                        main.Height = size.Height;
                        main.UpdateLayout();
                        Pump();
                        var position = sections.TranslatePoint(new Point(), main);
                        Assert.True(position.X >= 0 && position.X + sections.ActualWidth <= main.ActualWidth);
                        Assert.True(position.Y >= 0 && position.Y + sections.ActualHeight <= main.ActualHeight);
                        Assert.True(main.ActiveWorkspaceScroller!.ViewportHeight > 100);
                    }
                    navigation.NavigateTo<DashboardViewModel>();
                    main.UpdateLayout();
                    Pump();
                    var assessment = sections.Items.Cast<WorkspaceSections.Section>()
                        .Single(section => section.Title == "Standards and assessment history");
                    Assert.False(((Expander)assessment.Target).IsExpanded);
                    sections.SelectedItem = assessment;
                    Pump();
                    Assert.True(((Expander)assessment.Target).IsExpanded);
                    Assert.True(assessment.Target.IsVisible);
                    Assert.Equal("Compliance", Assert.Single(assessment.Tabs).Header);
                    VerifyAllWorkspaceTabs(main, firstWorkspace!);
                }
                finally { main.Close(); }
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(90)), "WPF navigation regression exceeded 90 seconds.");
        Assert.Null(failure);
    }

    private static void VerifyAllWorkspaceTabs(MainWindow main, WorkspaceView resources)
    {
        var host = (ContentControl)main.FindName("WorkspaceContent");
        var templates = resources.Resources.Keys.OfType<DataTemplateKey>().ToArray();
        foreach (var size in new[] { new Size(900, 600), new Size(1440, 900) })
        {
            main.Width = size.Width;
            main.Height = size.Height;
            foreach (var key in templates)
            {
                var content = (FrameworkElement)((DataTemplate)resources.Resources[key]).LoadContent();
                content.DataContext = key.DataType is Type type && type == typeof(OrganizationViewModel)
                    ? new OrganizationLayoutData()
                    : new object();
                host.Content = content;
                main.UpdateLayout();
                Pump();
                foreach (var tabs in Descendants<TabControl>(content).ToArray())
                    VerifyTabs(tabs, key, size);
            }
        }

        void VerifyTabs(TabControl tabs, DataTemplateKey key, Size size)
        {
            foreach (var tab in tabs.Items.Cast<TabItem>())
            {
                tabs.SelectedItem = tab;
                main.UpdateLayout();
                Pump();
                var header = tab.TranslatePoint(new Point(), main);
                Assert.True(tab.IsVisible, $"{key.DataType}: {tab.Header} header is hidden.");
                Assert.True(header.X >= 0 && header.X + tab.ActualWidth <= main.ActualWidth,
                    $"{key.DataType}: {tab.Header} header overflows horizontally at {size}.");
                Assert.True(header.Y >= 0 && header.Y + tab.ActualHeight <= main.ActualHeight - 33,
                    $"{key.DataType}: {tab.Header} header overflows vertically at {size}.");
                if (tab.Content is TabControl nested)
                {
                    VerifyTabs(nested, key, size);
                    continue;
                }
                var scroller = (ScrollViewer)tabs.Template.FindName("TabScroller", tabs);
                Assert.True(scroller.ViewportHeight > 100,
                    $"{key.DataType}: {tab.Header} viewport is only {scroller.ViewportHeight} at {size}.");
                var position = scroller.TranslatePoint(new Point(), main);
                foreach (var button in Descendants<Button>(scroller).Where(button => button.IsVisible))
                {
                    var point = button.TranslatePoint(new Point(), scroller);
                    Assert.True(point.X >= -1 && point.X + button.ActualWidth <= scroller.ViewportWidth + 1,
                        $"{key.DataType}: {tab.Header} button '{button.Content}' overflows horizontally at {size}.");
                }
                scroller.ScrollToBottom();
                main.UpdateLayout();
                Pump();
                Assert.Equal(header, tab.TranslatePoint(new Point(), main));
                Assert.True(position.Y + scroller.ActualHeight <= main.ActualHeight - 33,
                    $"{key.DataType}: {tab.Header} viewport extends below the workspace.");
            }
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match)
        {
            yield return match;
            yield break;
        }
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, index))) yield return child;
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

    private sealed class OrganizationLayoutData
    {
        public OrganizationLayoutData() => SelectedOrganization = Organizations[0];
        public string StatusMessage => "Sign in with Microsoft Entra ID to manage organizations and view tenant data.";
        public string ErrorMessage => string.Empty;
        public IReadOnlyList<OrganizationLayoutSelection> Organizations { get; } = [new()];
        public OrganizationLayoutSelection? SelectedOrganization { get; set; }
        public string CurrentRole => "Administrator";
    }

    private sealed class OrganizationLayoutSelection
    {
        public string Name => "Example organization";
    }

    private sealed class TestNavigation(params WorkspaceViewModel[] pages) : INavigationService
    {
        public object CurrentViewModel { get; private set; } = pages[0];
        public event EventHandler? CurrentViewModelChanged;
        public void NavigateTo<TViewModel>() where TViewModel : class
        {
            CurrentViewModel = pages.Single(page => page is TViewModel);
            CurrentViewModelChanged?.Invoke(this, EventArgs.Empty);
        }
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
    private sealed class EmptyWhatIfData : IWhatIfSavedData
    {
        public Task<IReadOnlyList<CloudAccount>> GetAccountsAsync(CancellationToken token)
            => Task.FromResult<IReadOnlyList<CloudAccount>>(Array.Empty<CloudAccount>());
        public Task<WhatIfInput> LoadAsync(Guid accountId, CancellationToken token) => throw new NotSupportedException();
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
