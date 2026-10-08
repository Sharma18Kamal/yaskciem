using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace CloudEntitle.Desktop.Services;

public sealed class DialogService : IDialogService
{
    public void ShowInformation(string message, string title = "YASK-CIEM") =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void ShowError(string message, string title = "YASK-CIEM") =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public bool Confirm(string message, string title = "YASK-CIEM") =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public string? ChooseReportPath()
    {
        var reportDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YASK-CIEM", "reports");
        var dialog = new SaveFileDialog
        {
            Title = "Save YASK-CIEM report",
            Filter = "JSON report (*.json)|*.json|CSV report (*.csv)|*.csv",
            FilterIndex = 1,
            DefaultExt = ".json",
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = Directory.Exists(reportDirectory) ? reportDirectory : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            FileName = $"yask-ciem-report-{DateTime.Now:yyyyMMdd-HHmmss}"
        };

        if (dialog.ShowDialog() != true)
        {
            return null;
        }

        var extension = dialog.FilterIndex == 2 ? ".csv" : ".json";
        return string.Equals(Path.GetExtension(dialog.FileName), extension, StringComparison.OrdinalIgnoreCase)
            ? dialog.FileName
            : Path.ChangeExtension(dialog.FileName, extension);
    }

    public string? ChoosePolicyDraftPath()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export policy review draft",
            Filter = "JSON policy draft (*.json)|*.json",
            DefaultExt = ".json",
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            FileName = $"yask-ciem-policy-draft-{DateTime.Now:yyyyMMdd-HHmmss}.json"
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? ChooseComplianceBundlePath()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save organization compliance evidence bundle",
            Filter = "Compliance evidence bundle (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            FileName = $"yask-ciem-compliance-{DateTime.Now:yyyyMMdd-HHmmss}.zip"
        };

        return dialog.ShowDialog() == true ? Path.ChangeExtension(dialog.FileName, ".zip") : null;
    }
}
