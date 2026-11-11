using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using CloudEntitle.Desktop.ViewModels;

namespace CloudEntitle.Desktop.Views;

public partial class WorkspaceView : UserControl
{
    private readonly Dictionary<object, ContentControl> _pages = new(ReferenceEqualityComparer.Instance);

    public WorkspaceView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ShowPage();
        Loaded += (_, _) => ShowPage();
        AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler(WorkspaceTabChanged));
    }

    private void ShowPage()
    {
        if (DataContext is not WorkspaceViewModel viewModel) return;
        if (!_pages.TryGetValue(viewModel, out var page))
        {
            var template = (DataTemplate)Resources[new DataTemplateKey(viewModel.GetType())];
            var content = (FrameworkElement)template.LoadContent();
            page = new ContentControl
            {
                Content = ContainsTabs(content) ? content : new ScrollViewer
                {
                    Content = content,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(0, 0, 8, 0)
                },
                DataContext = viewModel,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch
            };
            _pages.Add(viewModel, page);
        }
        if (ReferenceEquals(PageHost.Content, page)) return;
        PageHost.Content = page;
        if (SystemParameters.ClientAreaAnimation)
            page.BeginAnimation(OpacityProperty, new DoubleAnimation(0.75, 1, TimeSpan.FromMilliseconds(120)));
    }

    internal ScrollViewer? ActiveScroller => PageHost.Content is ContentControl { Content: ScrollViewer scroller }
        ? scroller
        : FindVisualChild<ScrollViewer>(PageHost, "TabScroller");

    private static bool ContainsTabs(DependencyObject root)
    {
        if (root is TabControl) return true;
        if (root is ItemsControl) return false;
        return LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>().Any(ContainsTabs);
    }

    private void WorkspaceTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource is not TabControl) return;
        if (ActiveScroller is { } previous) MouseWheelScrolling.StopAnimation(previous);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            if (ActiveScroller is not { } scroller) return;
            MouseWheelScrolling.StopAnimation(scroller);
            scroller.ScrollToTop();
        }));
    }

    private async void SaveTicketingCredentials_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || DataContext is not SettingsViewModel settings) return;
        var passwordBox = FindVisualChild<PasswordBox>(this, button.Tag as string);
        if (passwordBox is null) return;

        await settings.SaveTicketingCredentialsAsync(passwordBox.Password);
        passwordBox.Clear();
    }

    private async void SavePostureWebhookCredential_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || DataContext is not SettingsViewModel settings) return;
        var passwordBox = FindVisualChild<PasswordBox>(this, button.Tag as string);
        if (passwordBox is null) return;

        await settings.SavePostureWebhookCredentialAsync(passwordBox.Password);
        passwordBox.Clear();
    }

    private void TicketLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        if (e.Uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(e.Uri.UserInfo)) return;
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static T? FindVisualChild<T>(DependencyObject? parent, string? name = null) where T : DependencyObject
    {
        if (parent is null) return null;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match && (string.IsNullOrEmpty(name) || child is FrameworkElement element && element.Name == name)) return match;
            var descendant = FindVisualChild<T>(child, name);
            if (descendant is not null) return descendant;
        }
        return null;
    }
}
