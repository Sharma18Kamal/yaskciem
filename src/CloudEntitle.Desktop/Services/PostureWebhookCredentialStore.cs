using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace CloudEntitle.Desktop.Services;

public interface IPostureWebhookCredentialStore
{
    string? ReadUrl();
    void SaveUrl(string url);
    void Delete();
}

public sealed class WindowsPostureWebhookCredentialStore : IPostureWebhookCredentialStore
{
    private const uint GenericCredential = 1;
    private const uint LocalMachinePersistence = 2;
    private const int ErrorNotFound = 1168;
    private const int MaximumCredentialBlobSize = 2560;
    private const string TargetName = "YASK-CIEM/PostureAlertWebhook";

    public string? ReadUrl()
    {
        EnsureWindows();
        if (!CredRead(TargetName, GenericCredential, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return null;
            throw new Win32Exception(error, "Unable to read the posture alert endpoint from Windows Credential Manager.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlobSize > MaximumCredentialBlobSize)
                throw new InvalidDataException("The stored posture alert endpoint has an invalid size.");
            var bytes = new byte[credential.CredentialBlobSize];
            try
            {
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public void SaveUrl(string url)
    {
        EnsureWindows();
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        var bytes = Encoding.UTF8.GetBytes(url);
        if (bytes.Length > MaximumCredentialBlobSize)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new ArgumentException("The posture alert endpoint exceeds the Windows Credential Manager size limit.", nameof(url));
        }

        var blob = Marshal.AllocHGlobal(bytes.Length);
        var targetName = Marshal.StringToCoTaskMemUni(TargetName);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = GenericCredential,
                TargetName = targetName,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = LocalMachinePersistence
            };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to save the posture alert endpoint in Windows Credential Manager.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            Marshal.FreeHGlobal(blob);
            Marshal.ZeroFreeCoTaskMemUnicode(targetName);
        }
    }

    public void Delete()
    {
        EnsureWindows();
        if (!CredDelete(TargetName, GenericCredential, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
                throw new Win32Exception(error, "Unable to remove the posture alert endpoint from Windows Credential Manager.");
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Posture alert endpoints require Windows Credential Manager.");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string targetName, uint type, uint flags, out IntPtr credential);

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string targetName, uint type, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredFree", SetLastError = false)]
    private static extern void CredFree(IntPtr credential);
}
