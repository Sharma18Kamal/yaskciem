using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;

namespace YaskCiemNativeSetup;

internal static class Program
{
    private const string MsiResourceName = "YaskCiemNativeSetup.Payload.msi";

    private static async Task<int> Main()
    {
        var extractedMsiPath = Path.Combine(Path.GetTempPath(), $"YASK-CIEM-{Guid.NewGuid():N}.msi");
        try
        {
            await using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(MsiResourceName)
                ?? throw new InvalidOperationException("The embedded YASK-CIEM MSI is missing."))
            await using (var output = File.Create(extractedMsiPath))
            {
                await resource.CopyToAsync(output);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "msiexec.exe",
                Arguments = $"/i \"{extractedMsiPath}\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Normal
            };
            using var installer = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows Installer could not be started.");
            await installer.WaitForExitAsync();
            return installer.ExitCode;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            Console.WriteLine("Installation canceled at the Windows permission prompt.");
            return exception.NativeErrorCode;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"YASK-CIEM Setup failed: {exception.Message}");
            return 1;
        }
        finally
        {
            try
            {
                File.Delete(extractedMsiPath);
            }
            catch (IOException)
            {
            }
        }
    }
}
