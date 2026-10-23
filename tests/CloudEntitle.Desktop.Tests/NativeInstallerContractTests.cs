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
}
