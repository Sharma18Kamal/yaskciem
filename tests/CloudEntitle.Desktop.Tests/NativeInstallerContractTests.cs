using System.IO;
using System.Xml.Linq;
using Xunit;

namespace CloudEntitle.Desktop.Tests;

public sealed class NativeInstallerContractTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public void NativeUpgrade_ShouldRemainInWindowsInstallerTransaction()
    {
        var document = XDocument.Load(Path.Combine(Root, "installer", "native", "YaskCiemMsi.wxs"));
        XNamespace wix = "http://wixtoolset.org/schemas/v4/wxs";
        var upgrade = Assert.Single(document.Descendants(wix + "MajorUpgrade"));
        Assert.Equal("afterInstallInitialize", (string?)upgrade.Attribute("Schedule"));
        Assert.Equal("yes", (string?)upgrade.Attribute("AllowSameVersionUpgrades"));
        Assert.NotNull(upgrade.Attribute("DowngradeErrorMessage"));

        var setup = File.ReadAllText(Path.Combine(Root, "installer", "YaskCiemNativeSetup", "Program.cs"));
        Assert.DoesNotContain("MsiEnumRelatedProducts", setup);
        Assert.DoesNotContain("msiexec /x", setup);
        Assert.DoesNotContain("cmd.exe", setup);
        Assert.Contains("Environment.SystemDirectory, \"msiexec.exe\"", setup);
        Assert.Contains("return installer.ExitCode;", setup);
    }

    [Fact]
    public void NativeCandidate_ShouldBundleRuntimeAndRejectMismatchedPackageVersion()
    {
        var script = File.ReadAllText(Path.Combine(Root, "scripts", "package-native-installers.ps1"));
        Assert.Contains("-SelfContained $true", script);
        Assert.Contains("PackageVersion -cne $buildProperties.Project.PropertyGroup.Version", script);
        Assert.Contains("verify /pa $msiPath", script);
        Assert.Contains("verify /pa $setupPath", script);
    }

    [Fact]
    public void DesktopPackages_ShouldIncludeGuideAndExposeNativeShortcut()
    {
        var project = XDocument.Load(Path.Combine(Root, "src", "CloudEntitle.Desktop", "CloudEntitle.Desktop.csproj"));
        var content = Assert.Single(project.Descendants("Content"),
            element => (string?)element.Attribute("Link") == @"Guides\getting-started.html");
        Assert.Equal("PreserveNewest", (string?)content.Attribute("CopyToOutputDirectory"));
        Assert.Equal("PreserveNewest", (string?)content.Attribute("CopyToPublishDirectory"));
        var source = Path.GetFullPath(Path.Combine(Root, "src", "CloudEntitle.Desktop",
            Assert.IsType<string>(content.Attribute("Include")?.Value)));
        var copiedGuide = Path.Combine(AppContext.BaseDirectory, "Guides", "getting-started.html");
        Assert.Equal(File.ReadAllText(source), File.ReadAllText(copiedGuide));

        XNamespace wix = "http://wixtoolset.org/schemas/v4/wxs";
        var installer = XDocument.Load(Path.Combine(Root, "installer", "native", "YaskCiemMsi.wxs"));
        var shortcut = Assert.Single(installer.Descendants(wix + "Shortcut"),
            element => (string?)element.Attribute("Id") == "YaskCiemSetupGuideShortcut");
        Assert.Equal(@"[INSTALLFOLDER]Guides\getting-started.html", (string?)shortcut.Attribute("Target"));
        Assert.Equal("StartMenuShortcut", (string?)shortcut.Parent?.Attribute("Id"));
        Assert.Contains(installer.Descendants(wix + "ComponentRef"),
            element => (string?)element.Attribute("Id") == "StartMenuShortcut");

        var setup = File.ReadAllText(Path.Combine(Root, "installer", "YaskCiemNativeSetup", "Program.cs"));
        Assert.Contains("\"Guides\", \"getting-started.html\"", setup);
        Assert.Contains("Open the setup and cloud sign-in guide?", setup);
        var window = XDocument.Load(Path.Combine(Root, "src", "CloudEntitle.Desktop", "MainWindow.xaml"));
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var button = Assert.Single(window.Descendants(wpf + "Button"),
            element => (string?)element.Attribute("Content") == "Setup guide");
        Assert.Equal("SetupGuideButton_Click", (string?)button.Attribute("Click"));
    }

    [Fact]
    public void OfflineGuide_ShouldHaveWorkingSectionLinksAndNoRemoteAssets()
    {
        var guide = XDocument.Load(Path.Combine(Root, "docs", "deployment", "getting-started.html"));
        XNamespace html = "http://www.w3.org/1999/xhtml";
        var ids = guide.Descendants().Attributes("id").Select(attribute => attribute.Value).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        foreach (var section in new[] { "install", "app-sign-in", "cloud-sign-in", "azure", "aws", "gcp", "kubernetes", "first-scan", "server", "troubleshooting" })
            Assert.Contains(section, ids);
        foreach (var href in guide.Descendants(html + "a").Attributes("href").Select(attribute => attribute.Value))
        {
            if (href.StartsWith('#')) Assert.Contains(href[1..], ids);
            else Assert.True(Uri.TryCreate(href, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps,
                $"Unexpected guide link: {href}");
        }
        Assert.Empty(guide.Descendants(html + "script"));
        Assert.Empty(guide.Descendants(html + "link"));
        Assert.Empty(guide.Descendants().Attributes("src"));
        var instructions = guide.Root!.Value;
        Assert.Contains(".NET 10 runtimes are", instructions);
        Assert.Contains("Do not install PostgreSQL on each Desktop PC.", instructions);
        Assert.Contains("Organizations", instructions);
        Assert.Contains("Start 30-day trial", instructions);
        Assert.Contains("aws configure sso --profile ciem-audit", instructions);
        Assert.Contains("az login --tenant", instructions);
        Assert.Contains("gcloud auth login", instructions);
    }
}
