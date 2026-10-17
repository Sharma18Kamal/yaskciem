using System.IO;
using System.Xml.Linq;
using Xunit;

namespace CloudEntitle.Desktop.Tests;

public sealed class WorkspaceViewBindingTests
{
    [Fact]
    public void InlineRunBindings_ShouldBeOneWay()
    {
        var workspaceRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var xamlPath = Path.Combine(workspaceRoot, "src", "CloudEntitle.Desktop", "Views", "WorkspaceView.xaml");
        var document = XDocument.Load(xamlPath);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var bindings = document.Descendants(presentation + "Run")
            .Select(run => (string?)run.Attribute("Text"))
            .Where(text => text?.StartsWith("{Binding ", StringComparison.Ordinal) == true)
            .ToArray();

        Assert.NotEmpty(bindings);
        Assert.All(bindings, binding => Assert.Contains("Mode=OneWay", binding, StringComparison.Ordinal));
    }

    [Fact]
    public void ApplicationHeader_ShouldShowBoldDeveloperCreditBesideProductName()
    {
        var workspaceRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var document = XDocument.Load(Path.Combine(workspaceRoot, "src", "CloudEntitle.Desktop", "MainWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var credit = Assert.Single(document.Descendants(presentation + "TextBlock"), textBlock =>
            (string?)textBlock.Attribute("Text") == "Developed By: Sharma Kamal K");

        Assert.Equal("Bold", (string?)credit.Attribute("FontWeight"));
        Assert.Equal("Wrap", (string?)credit.Attribute("TextWrapping"));
        Assert.Equal("2", (string?)credit.Attribute("Grid.Column"));
        var brand = Assert.Single(credit.Parent!.Elements(presentation + "StackPanel"));
        Assert.Equal("1", (string?)brand.Attribute("Grid.Column"));
        Assert.Contains(brand.Descendants(presentation + "TextBlock"), textBlock => (string?)textBlock.Attribute("Text") == "YASK-CIEM");
        var header = credit.Ancestors(presentation + "Border").First();
        Assert.Equal("0", (string?)header.Attribute("Grid.Row"));
        Assert.Contains(header.Descendants(presentation + "StackPanel"), panel => (string?)panel.Attribute("Grid.Row") == "1");
    }

    [Fact]
    public void SecurityWorkspace_ShouldAppearOnRightOfApplicationHeader()
    {
        var workspaceRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var document = XDocument.Load(Path.Combine(workspaceRoot, "src", "CloudEntitle.Desktop", "MainWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var label = Assert.Single(document.Descendants(presentation + "TextBlock"), textBlock =>
            (string?)textBlock.Attribute("Text") == "SECURITY WORKSPACE");
        var panel = label.Ancestors(presentation + "Border").First();

        Assert.Equal("1", (string?)panel.Attribute("Grid.Column"));
        Assert.Equal("Right", (string?)panel.Attribute("HorizontalAlignment"));
        Assert.Empty(panel.Ancestors(presentation + "DockPanel"));
        Assert.Contains(panel.Descendants(presentation + "TextBlock"), textBlock => (string?)textBlock.Attribute("Text") == "{Binding CloudConnectionStatus}");
        Assert.Empty(panel.Ancestors(presentation + "ScrollViewer"));
        var header = panel.Ancestors(presentation + "Border").First();
        Assert.Equal("0", (string?)header.Attribute("Grid.Row"));
        Assert.Empty(panel.Descendants(presentation + "DataTrigger"));
    }

    [Fact]
    public void DashboardToolbar_ShouldExposeSharedCloudConnectionStatus()
    {
        var workspaceRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var xamlPath = Path.Combine(workspaceRoot, "src", "CloudEntitle.Desktop", "Views", "WorkspaceView.xaml");
        var document = XDocument.Load(xamlPath);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        Assert.Contains(document.Descendants(presentation + "TextBlock"), textBlock =>
            ((string?)textBlock.Attribute("Text"))?.Contains("CloudConnectionStatus", StringComparison.Ordinal) == true
            && ((string?)textBlock.Attribute("Grid.Column")) == "4");
    }

    [Fact]
    public void DashboardAccountScope_ShouldExposeDiscoveryAndTwoWaySelection()
    {
        var workspaceRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var xamlPath = Path.Combine(workspaceRoot, "src", "CloudEntitle.Desktop", "Views", "WorkspaceView.xaml");
        var document = XDocument.Load(xamlPath);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        Assert.Contains(document.Descendants(presentation + "Button"), button =>
            (string?)button.Attribute("Content") == "Discover accounts"
            && ((string?)button.Attribute("Command"))?.Contains("DiscoverAccountsCommand", StringComparison.Ordinal) == true);
        Assert.Contains(document.Descendants(presentation + "DataGrid"), grid =>
            ((string?)grid.Attribute("ItemsSource"))?.Contains("AvailableAccounts", StringComparison.Ordinal) == true
            && grid.Descendants(presentation + "DataGridCheckBoxColumn").Any(column =>
                ((string?)column.Attribute("Binding"))?.Contains("IsSelected, Mode=TwoWay", StringComparison.Ordinal) == true));
    }

    [Fact]
    public void Settings_ShouldExposeJiraUrlSaveAndConnectControls()
    {
        var workspaceRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var xamlPath = Path.Combine(workspaceRoot, "src", "CloudEntitle.Desktop", "Views", "WorkspaceView.xaml");
        var document = XDocument.Load(xamlPath);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        Assert.Contains(document.Descendants(presentation + "ComboBox"), comboBox =>
            ((string?)comboBox.Attribute("ItemsSource"))?.Contains("TicketingProviders", StringComparison.Ordinal) == true
            && ((string?)comboBox.Attribute("SelectedItem"))?.Contains("SelectedTicketingProviderOption", StringComparison.Ordinal) == true
            && (string?)comboBox.Attribute("DisplayMemberPath") == "DisplayName");
        Assert.Contains(document.Descendants(presentation + "TextBox"), textBox =>
            ((string?)textBox.Attribute("Text"))?.Contains("TicketingSiteUrl", StringComparison.Ordinal) == true);
        Assert.Contains(document.Descendants(presentation + "Button"), button =>
            (string?)button.Attribute("Content") == "Save URL"
            && ((string?)button.Attribute("Command"))?.Contains("SaveTicketingConnectionCommand", StringComparison.Ordinal) == true);
        Assert.Contains(document.Descendants(presentation + "Button"), button =>
            ((string?)button.Attribute("Content"))?.Contains("TicketingRequestButtonText", StringComparison.Ordinal) == true
            && ((string?)button.Attribute("IsEnabled"))?.Contains("CanOpenTicketingRequest", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void SearchFields_ShouldAllUseTheSharedSearchIconTemplate()
    {
        var workspaceRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var workspace = XDocument.Load(Path.Combine(workspaceRoot, "src", "CloudEntitle.Desktop", "Views", "WorkspaceView.xaml"));
        var app = XDocument.Load(Path.Combine(workspaceRoot, "src", "CloudEntitle.Desktop", "App.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var searchFields = workspace.Descendants(presentation + "TextBox")
            .Where(textBox => ((string?)textBox.Attribute("Text"))?.Contains("SearchText", StringComparison.Ordinal) == true)
            .ToArray();
        Assert.NotEmpty(searchFields);
        Assert.All(searchFields, textBox => Assert.Equal("{StaticResource SearchBox}", (string?)textBox.Attribute("Style")));

        var searchStyle = Assert.Single(app.Descendants(presentation + "Style"),
            style => (string?)style.Attribute(xaml + "Key") == "SearchBox");
        Assert.Contains(searchStyle.Descendants(presentation + "TextBlock"), icon =>
            ((string?)icon.Attribute("Text"))?.Contains("\uE721", StringComparison.Ordinal) == true
            && (string?)icon.Attribute("IsHitTestVisible") == "False");
    }

    [Fact]
    public void DashboardScanProgress_ShouldBeIndeterminateAndFollowScanState()
    {
        var workspaceRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var document = XDocument.Load(Path.Combine(workspaceRoot, "src", "CloudEntitle.Desktop", "Views", "WorkspaceView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var progress = Assert.Single(document.Descendants(presentation + "ProgressBar"));
        Assert.Equal("True", (string?)progress.Attribute("IsIndeterminate"));
        var scanPanel = progress.Ancestors(presentation + "Border").First();
        Assert.Equal("1", (string?)scanPanel.Attribute("Grid.Column"));
        Assert.Contains(scanPanel.Descendants(presentation + "TextBlock"), textBlock =>
            ((string?)textBlock.Attribute("Text"))?.Contains("ScanStatusMessage", StringComparison.Ordinal) == true);
        Assert.Contains(scanPanel.Descendants(presentation + "TextBlock"), textBlock =>
            ((string?)textBlock.Attribute("Text"))?.Contains("ScanElapsedText", StringComparison.Ordinal) == true);
        Assert.Contains(scanPanel.Descendants(presentation + "TextBlock"), textBlock =>
            ((string?)textBlock.Attribute("Text"))?.Contains("\u2601", StringComparison.Ordinal) == true);
        var hourglass = Assert.Single(scanPanel.Descendants(presentation + "TextBlock"), textBlock =>
            ((string?)textBlock.Attribute("Text"))?.Contains("\u231B", StringComparison.Ordinal) == true);
        var scanningTrigger = Assert.Single(hourglass.Descendants(presentation + "DataTrigger"), trigger =>
            (string?)trigger.Attribute("Binding") == "{Binding IsScanning}"
            && (string?)trigger.Attribute("Value") == "True");
        var animation = Assert.Single(scanningTrigger.Descendants(presentation + "DoubleAnimation"));
        Assert.Equal("180", (string?)animation.Attribute("To"));
        Assert.Equal("True", (string?)animation.Attribute("AutoReverse"));
        Assert.Equal("Forever", (string?)scanningTrigger.Descendants(presentation + "Storyboard").Single().Attribute("RepeatBehavior"));
        Assert.Single(scanningTrigger.Descendants(presentation + "StopStoryboard"));
    }

    [Fact]
    public void InteractiveWorkspaceOptions_ShouldAllHaveTooltips()
    {
        var workspaceRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var document = XDocument.Load(Path.Combine(workspaceRoot, "src", "CloudEntitle.Desktop", "Views", "WorkspaceView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var optionTypes = new HashSet<XName>
        {
            presentation + "Button",
            presentation + "CheckBox",
            presentation + "ComboBox",
            presentation + "DataGrid",
            presentation + "DatePicker",
            presentation + "Expander",
            presentation + "PasswordBox",
            presentation + "TextBox"
        };
        var dataGridTooltipStyleExists = document.Descendants(presentation + "Style")
            .Any(style => (string?)style.Attribute("TargetType") == "{x:Type DataGrid}"
                && style.Elements(presentation + "Setter").Any(setter => (string?)setter.Attribute("Property") == "ToolTip"));

        var missing = document.Descendants()
            .Where(element => optionTypes.Contains(element.Name)
                && string.IsNullOrWhiteSpace((string?)element.Attribute("ToolTip"))
                && !(element.Name == presentation + "DataGrid" && dataGridTooltipStyleExists))
            .Select(element => $"{element.Name.LocalName}: {string.Join(" ", new[] { "Content", "Text", "Header", "ItemsSource", "Name" }.Select(name => (string?)element.Attribute(name)).Where(value => !string.IsNullOrWhiteSpace(value)))}")
            .ToArray();

        Assert.True(missing.Length == 0, string.Join(Environment.NewLine, missing));

        var mainWindow = XDocument.Load(Path.Combine(workspaceRoot, "src", "CloudEntitle.Desktop", "MainWindow.xaml"));
        var buttonsWithoutTooltips = mainWindow.Descendants(presentation + "Button")
            .Where(button => string.IsNullOrWhiteSpace((string?)button.Attribute("ToolTip")))
            .Select(button => (string?)button.Attribute("Content") ?? (string?)button.Attribute("Command") ?? "Button")
            .ToArray();
        Assert.True(buttonsWithoutTooltips.Length == 0, string.Join(Environment.NewLine, buttonsWithoutTooltips));
    }
}
