using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
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
        var rootCertificateAdded = false;
        var trustedPeopleCertificateAdded = false;
        X509Certificate2? certificate = null;
        var extractedPackagePath = Path.Combine(Path.GetTempPath(), $"YASK-CIEM-{Guid.NewGuid():N}.msix");

        try
        {
            var packageBytes = ReadEmbeddedResource(PackageResource);
            var certificateBytes = ReadEmbeddedResource(CertificateResource);
            certificate = X509CertificateLoader.LoadCertificate(certificateBytes);
            ValidateCertificate(certificate);
            var packageVersion = ValidatePackageManifest(packageBytes, certificate.Subject);

            Console.WriteLine("YASK-CIEM Setup");
            Console.WriteLine($"Package version: {packageVersion}");
            Console.WriteLine($"Publisher: {certificate.Subject}");
            Console.WriteLine();
            if (!ContainsCertificate(StoreName.Root, certificate))
            {
                Console.WriteLine("This is a self-signed development code-signing certificate.");
                Console.WriteLine("Trusting it in Current User\\Root allows packages signed by this key to be trusted by this Windows account.");
                Console.Write("Only continue if you received this installer from a trusted source. Type TRUST to continue: ");
                if (Console.IsInputRedirected || !string.Equals(Console.ReadLine(), "TRUST", StringComparison.Ordinal))
                {
                    Console.WriteLine("Certificate trust was not approved; no certificate or app was installed.");
                    WaitForUser();
                    return 2;
                }
            }

            rootCertificateAdded = AddCertificateIfMissing(StoreName.Root, certificate);
            trustedPeopleCertificateAdded = AddCertificateIfMissing(StoreName.TrustedPeople, certificate);

            await File.WriteAllBytesAsync(extractedPackagePath, packageBytes);
            var installResult = await InstallPackageAsync(extractedPackagePath);
            if (installResult.ExitCode != 0)
            {
                if (trustedPeopleCertificateAdded) RemoveCertificate(StoreName.TrustedPeople, certificate);
                if (rootCertificateAdded) RemoveCertificate(StoreName.Root, certificate);

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
            if (certificate is not null)
            {
                if (trustedPeopleCertificateAdded) RemoveCertificate(StoreName.TrustedPeople, certificate);
                if (rootCertificateAdded) RemoveCertificate(StoreName.Root, certificate);
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

        var allowsCodeSigning = certificate.Extensions
            .OfType<X509EnhancedKeyUsageExtension>()
            .SelectMany(extension => extension.EnhancedKeyUsages.Cast<Oid>())
            .Any(oid => string.Equals(oid.Value, "1.3.6.1.5.5.7.3.3", StringComparison.Ordinal));
        if (!allowsCodeSigning) throw new InvalidOperationException("The bundled certificate is not valid for code signing.");
    }

    private static bool ContainsCertificate(StoreName storeName, X509Certificate2 certificate)
    {
        using var store = new X509Store(storeName, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, validOnly: false).Count > 0;
    }

    private static bool AddCertificateIfMissing(StoreName storeName, X509Certificate2 certificate)
    {
        if (ContainsCertificate(storeName, certificate)) return false;
        using var store = new X509Store(storeName, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(certificate);
        return true;
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

    private static void RemoveCertificate(StoreName storeName, X509Certificate2 certificate)
    {
        try
        {
            using var store = new X509Store(storeName, StoreLocation.CurrentUser);
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
