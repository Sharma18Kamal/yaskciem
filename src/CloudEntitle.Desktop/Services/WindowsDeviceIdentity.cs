using Microsoft.Win32;
using CloudEntitle.Licensing;

namespace CloudEntitle.Desktop.Services;

public static class WindowsDeviceIdentity
{
    public static string GetIdentifier()
    {
        try
        {
            if (Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography",
                    "MachineGuid",
                    null) is string machineGuid
                && !string.IsNullOrWhiteSpace(machineGuid))
            {
                return machineGuid.Trim();
            }
        }
        catch (UnauthorizedAccessException)
        {
        }

        return MacAddressHardwareFingerprint.GetPrimaryMacAddress();
    }
}
