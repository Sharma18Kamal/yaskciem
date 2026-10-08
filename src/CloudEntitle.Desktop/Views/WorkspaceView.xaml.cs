using System.Windows.Controls;
using System.Windows;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Navigation;
using CloudEntitle.Desktop.ViewModels;

namespace CloudEntitle.Desktop.Views;

public partial class WorkspaceView : UserControl
{
    public WorkspaceView()
    {
        InitializeComponent();
    }

    private async void SaveTicketingCredentials_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || DataContext is not SettingsViewModel settings) return;
        var passwordBox = FindVisualChild<PasswordBox>(this);
        if (passwordBox is null) return;

        await settings.SaveTicketingCredentialsAsync(passwordBox.Password);
        passwordBox.Clear();
    }

    private void TicketLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        if (e.Uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(e.Uri.UserInfo)) return;
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static T? FindVisualChild<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent is null) return null;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            var descendant = FindVisualChild<T>(child);
            if (descendant is not null) return descendant;
        }
        return null;
    }
}
