namespace CloudEntitle.Desktop.Services;

public interface IDialogService
{
    void ShowInformation(string message, string title = "CloudEntitle CIEM");
    void ShowError(string message, string title = "CloudEntitle CIEM");
    bool Confirm(string message, string title = "CloudEntitle CIEM");
}
