using System.Windows;

namespace CloudEntitle.Desktop.Services;

public sealed class DialogService : IDialogService
{
    public void ShowInformation(string message, string title = "YASK-CIEM") =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void ShowError(string message, string title = "YASK-CIEM") =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public bool Confirm(string message, string title = "YASK-CIEM") =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
}
