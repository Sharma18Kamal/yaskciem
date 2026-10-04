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
}
