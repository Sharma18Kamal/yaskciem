using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;

namespace YaskCiemSetup;

internal static class Program
{
    private const string Publisher = "CN=YASK-CIEM";
    private const string PublisherThumbprint = "537C64CF83CF2C6C1EBC89553B7B4EDD13E7F104";
    private const string PackageResource = "YaskCiemSetup.Payload.msix";
    private const string CertificateResource = "YaskCiemSetup.Publisher.cer";
    private const string PackageIdentityName = "Sharma18Kamal.YASKCIEM";
    private const string PackagePublisherEnvironmentVariable = "YASK_CIEM_SETUP_PACKAGE";

    private static async Task<int> Main()
    {
        var certificateAdded = false;
        X509Certificate2? certificate = null;
        var extractedPackagePath = Path.Combine(Path.GetTempPath(), $"YASK-CIEM-{Guid.NewGuid():N}.msix");

        try
        {
            var packageBytes = ReadEmbeddedResource(PackageResource);
            var certificateBytes = ReadEmbeddedResource(CertificateResource);
            certificate = new X509Certificate2(certificateBytes);
            ValidateCertificate(certificate);
            var packageVersion = ValidatePackageManifest(packageBytes, certificate.Subject);

            Console.WriteLine("YASK-CIEM Setup");
            Console.WriteLine($"Package version: {packageVersion}");
            Console.WriteLine($"Publisher: {certificate.Subject}");
            Console.WriteLine();
            Console.WriteLine("This setup will trust the YASK-CIEM development certificate for your Windows account and install the app.");
            Console.WriteLine("Only continue if you trust the source of this installer.");
            Console.Write("Continue? [y/N]: ");
            if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Installation cancelled.");
                return 0;
            }

            using (var store = new X509Store(StoreName.TrustedPeople, StoreLocation.CurrentUser))
            {
                store.Open(OpenFlags.ReadWrite);
                var installedCertificate = store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, validOnly: false).Count > 0;
                if (!installedCertificate)
                {
                    store.Add(certificate);
                    certificateAdded = true;
                }
            }

            await File.WriteAllBytesAsync(extractedPackagePath, packageBytes);
            var installResult = await InstallPackageAsync(extractedPackagePath);
            if (installResult.ExitCode != 0)
            {
                if (certificateAdded)
                {
                    RemoveCertificate(certificate);
                }

                Console.Error.WriteLine("Windows could not install the MSIX package.");
                if (!string.IsNullOrWhiteSpace(installResult.StandardError)) Console.Error.WriteLine(installResult.StandardError.Trim());
                if (!string.IsNullOrWhiteSpace(installResult.StandardOutput)) Console.Error.WriteLine(installResult.StandardOutput.Trim());
                WaitForUser();
                return installResult.ExitCode;
            }

            Console.WriteLine();
            Console.WriteLine("YASK-CIEM was installed successfully. You can launch it from the Start menu.");
            WaitForUser();
            return 0;
        }
        catch (Exception exception)
        {
            if (certificateAdded && certificate is not null)
            {
                RemoveCertificate(certificate);
            }

            Console.Error.WriteLine($"Setup failed: {exception.Message}");
            WaitForUser();
            return 1;
        }
        finally
        {
            certificate?.Dispose();
            try
            {
                File.Delete(extractedPackagePath);
            }
            catch (IOException)
            {
            }
        }
    }

    private static byte[] ReadEmbeddedResource(string resourceName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Installer payload is missing: {resourceName}");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static void ValidateCertificate(X509Certificate2 certificate)
    {
        if (!string.Equals(certificate.Subject, Publisher, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(certificate.Thumbprint, PublisherThumbprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The bundled publisher certificate is not the expected YASK-CIEM certificate.");
        }

        if (DateTime.UtcNow < certificate.NotBefore.ToUniversalTime() || DateTime.UtcNow > certificate.NotAfter.ToUniversalTime())
        {
            throw new InvalidOperationException("The bundled publisher certificate is not currently valid.");
        }
    }

    private static string ValidatePackageManifest(byte[] packageBytes, string certificateSubject)
    {
        using var packageStream = new MemoryStream(packageBytes, writable: false);
        using var archive = new ZipArchive(packageStream, ZipArchiveMode.Read);
        var manifestEntry = archive.GetEntry("AppxManifest.xml")
            ?? throw new InvalidOperationException("The bundled MSIX manifest is missing.");
        using var manifestStream = manifestEntry.Open();
        var manifest = XDocument.Load(manifestStream);
        XNamespace appx = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        var identity = manifest.Root?.Element(appx + "Identity")
            ?? throw new InvalidOperationException("The bundled MSIX identity is missing.");
        var packageName = (string?)identity.Attribute("Name");
        var packagePublisher = (string?)identity.Attribute("Publisher");
        var packageVersion = (string?)identity.Attribute("Version");

        if (!string.Equals(packageName, PackageIdentityName, StringComparison.Ordinal)
            || !string.Equals(packagePublisher, certificateSubject, StringComparison.OrdinalIgnoreCase)
            || !Version.TryParse(packageVersion, out _))
        {
            throw new InvalidOperationException("The bundled MSIX identity does not match the expected YASK-CIEM publisher.");
        }

        return packageVersion!;
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> InstallPackageAsync(string packagePath)
    {
        var powershellPath = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = powershellPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("Add-AppxPackage -Path $env:YASK_CIEM_SETUP_PACKAGE -ErrorAction Stop");
        startInfo.Environment[PackagePublisherEnvironmentVariable] = packagePath;

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await outputTask, await errorTask);
    }

    private static void RemoveCertificate(X509Certificate2 certificate)
    {
        try
        {
            using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            store.Remove(certificate);
        }
        catch (CryptographicException)
        {
        }
    }

    private static void WaitForUser()
    {
        if (!Console.IsInputRedirected)
        {
            Console.WriteLine();
            Console.Write("Press Enter to close this window...");
            Console.ReadLine();
        }
    }
}
