using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace YaskCiemNativeSetup;

internal static class Program
{
    private const string MsiResourceName = "YaskCiemNativeSetup.Payload.msi";
    private static async Task<int> Main()
    {
        var extractedMsiPath = Path.Combine(Path.GetTempPath(), $"YASK-CIEM-{Guid.NewGuid():N}.msi");
        var logPath = Path.Combine(Path.GetTempPath(), "YASK-CIEM-Setup.log");
        var title = $"YASK-CIEM Setup {BuildLabel()}";
        try
        {
            if (MessageBoxW(IntPtr.Zero, $"Install YASK-CIEM?\n\n{BuildLabel()}\n\n.NET 10 runtimes and an offline setup/cloud sign-in guide are included.\nPostgreSQL is a separate License Server prerequisite, not required on each Desktop PC.", title, 0x21) != 1) return 2;
            await using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(MsiResourceName)
                ?? throw new InvalidOperationException("The embedded YASK-CIEM MSI is missing."))
            await using (var output = File.Create(extractedMsiPath))
            {
                await resource.CopyToAsync(output);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "msiexec.exe"),
                Arguments = $"/i \"{extractedMsiPath}\" /qb /norestart /l*v \"{logPath}\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var installer = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows Installer could not be started.");
            await installer.WaitForExitAsync();

            if (installer.ExitCode is 0 or 3010)
            {
                var installationDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "YASK-CIEM");
                if (MessageBoxW(IntPtr.Zero, "Open the setup and cloud sign-in guide?\n\nIt explains API/trial sign-in, Azure/AWS/Google Cloud connections and your first scan.\nYou can reopen it from the app's Setup guide button or the YASK-CIEM Start Menu folder.", title, 0x24) == 6)
                {
                    var guidePath = Path.Combine(installationDirectory, "Guides", "getting-started.html");
                    try
                    {
                        if (!File.Exists(guidePath)) throw new FileNotFoundException("The installed setup guide is missing. Repair the installation.", guidePath);
                        Process.Start(new ProcessStartInfo(guidePath) { UseShellExecute = true });
                    }
                    catch (Exception exception) when (exception is IOException or Win32Exception or InvalidOperationException)
                    {
                        MessageBoxW(IntPtr.Zero, $"YASK-CIEM is installed, but the guide could not be opened: {exception.Message}\n\nYou can find it at {guidePath}.", title, 0x30);
                    }
                }
                var launch = MessageBoxW(IntPtr.Zero, $"YASK-CIEM was installed successfully.\n\n{BuildLabel()}\n\nLaunch it now?", title, 0x24) == 6;
                if (launch)
                {
                    var appPath = Path.Combine(installationDirectory, "CloudEntitle.Desktop.exe");
                    if (File.Exists(appPath)) Process.Start(new ProcessStartInfo(appPath) { UseShellExecute = true });
                }
                return installer.ExitCode;
            }

            MessageBoxW(IntPtr.Zero, $"YASK-CIEM installation failed (Windows Installer code {installer.ExitCode}).\n\n{BuildLabel()}\nDetails: {logPath}", title, 0x10);
            return installer.ExitCode;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            Console.WriteLine("Installation canceled at the Windows permission prompt.");
            return exception.NativeErrorCode;
        }
        catch (Exception exception)
        {
            MessageBoxW(IntPtr.Zero, $"YASK-CIEM Setup failed: {exception.Message}", title, 0x10);
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private static string BuildLabel()
    {
        var info = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;
        var parts = info.Split('+', 2);
        return parts.Length == 2 ? $"Version {parts[0]} (built {parts[1].Replace('T', ' ')})" : $"Version {parts[0]}";
    }

}
