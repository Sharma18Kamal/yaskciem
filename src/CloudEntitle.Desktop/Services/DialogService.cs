using System.IO;
using System.Windows;
using Microsoft.Win32;
using CloudEntitle.Desktop.Views;

namespace CloudEntitle.Desktop.Services;

public sealed class DialogService : IDialogService
{
    public string? ChooseWhatIfReportPath()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save non-executable what-if simulation report",
            Filter = "Simulation JSON (*.json)|*.json",
            DefaultExt = ".json", AddExtension = true, OverwritePrompt = true,
            FileName = $"yask-ciem-what-if-{DateTime.Now:yyyyMMdd-HHmmss}.json"
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
    public string? ChooseAuthorizationBundlePath()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import offline authorization bundle (schema 1.0)",
            Filter = "Authorization JSON (*.json)|*.json",
            CheckFileExists = true,
            Multiselect = false
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
    public void ShowInformation(string message, string title = "YASK-CIEM") =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void ShowError(string message, string title = "YASK-CIEM") =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public bool Confirm(string message, string title = "YASK-CIEM") =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public string? ChooseReportPath()
    {
        var picker = new ReportFormatDialog { Owner = System.Windows.Application.Current.MainWindow };
        if (picker.ShowDialog() != true) return null;
        var format = picker.SelectedFormat;
        var reportDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YASK-CIEM", "reports");
        var dialog = new SaveFileDialog
        {
            Title = $"Save YASK-CIEM report - {format.Label}",
            Filter = $"{format.Label}|*{format.Extension}",
            DefaultExt = format.Extension,
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = Directory.Exists(reportDirectory) ? reportDirectory : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            FileName = $"yask-ciem-report-{DateTime.Now:yyyyMMdd-HHmmss}"
        };

        if (dialog.ShowDialog() != true)
        {
            return null;
        }

        var path = Path.ChangeExtension(dialog.FileName, format.Extension);
        if (!string.Equals(path, dialog.FileName, StringComparison.OrdinalIgnoreCase) && File.Exists(path)
            && !Confirm($"The selected format uses {format.Extension}.\n\n{path}\n\nThis file already exists. Replace it?"))
        {
            return null;
        }
        return path;
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

    public string? ChooseLocalBackupSavePath()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Back up YASK-CIEM local data",
            Filter = "YASK-CIEM backup (*.yaskbackup)|*.yaskbackup",
            DefaultExt = ".yaskbackup",
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            FileName = $"yask-ciem-backup-{DateTime.Now:yyyyMMdd-HHmmss}.yaskbackup"
        };

        return dialog.ShowDialog() == true ? Path.ChangeExtension(dialog.FileName, ".yaskbackup") : null;
    }

    public string? ChooseLocalBackupOpenPath()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Restore YASK-CIEM local data",
            Filter = "YASK-CIEM backup (*.yaskbackup)|*.yaskbackup",
            CheckFileExists = true,
            Multiselect = false
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
