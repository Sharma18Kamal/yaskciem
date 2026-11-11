using System.Windows;
using System.Windows.Controls;

namespace CloudEntitle.Desktop.Views;

public partial class ReportFormatDialog : Window
{
    internal sealed record FormatOption(string Label, string Extension, string Description);

    internal static IReadOnlyList<FormatOption> Formats { get; } =
    [
        new("JSON (.json)", ".json", "Structured report data for integrations and further analysis."),
        new("CSV (.csv)", ".csv", "A section and JSON-record table for spreadsheet import."),
        new("HTML (.html)", ".html", "A readable evidence snapshot that opens in a web browser."),
        new("XML (.xml)", ".xml", "Structured XML data; Item elements represent arrays and kind attributes preserve value types."),
        new("PDF (.pdf)", ".pdf", "A printable report for sharing and review."),
        new("Excel (.xlsx)", ".xlsx", "An Excel workbook containing report sections and evidence records.")
    ];

    internal FormatOption SelectedFormat => (FormatOption)FormatSelector.SelectedItem;

    public ReportFormatDialog()
    {
        InitializeComponent();
        FormatSelector.ItemsSource = Formats;
        FormatSelector.SelectedIndex = 0;
    }

    private void FormatSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FormatDescription is not null && FormatSelector.SelectedItem is FormatOption option)
            FormatDescription.Text = option.Description;
    }

    private void Continue_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
