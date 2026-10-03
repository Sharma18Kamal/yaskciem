namespace CloudEntitle.Desktop.Services;

public interface IDialogService
{
    void ShowInformation(string message, string title = "YASK-CIEM");
    void ShowError(string message, string title = "YASK-CIEM");
    bool Confirm(string message, string title = "YASK-CIEM");
    string? ChooseReportPath();
    string? ChoosePolicyDraftPath();
}
