using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CloudEntitle.Desktop.Services;

public sealed record TicketingCredentials(string UserName, string Secret);

public interface ITicketingCredentialStore
{
    TicketingCredentials? Read(TicketingProvider provider);
    void Save(TicketingProvider provider, TicketingCredentials credentials);
    void Delete(TicketingProvider provider);
}

public sealed class WindowsTicketingCredentialStore : ITicketingCredentialStore
{
    private const uint GenericCredential = 1;
    private const uint LocalMachinePersistence = 2;
    private const int ErrorNotFound = 1168;
    private const int MaximumCredentialBlobSize = 2560;
    private static readonly JsonSerializerOptions JsonOptions = new();

    public TicketingCredentials? Read(TicketingProvider provider)
    {
        EnsureWindows();
        if (!CredRead(TargetName(provider), GenericCredential, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return null;
            throw new Win32Exception(error, "Unable to read ticketing credentials from Windows Credential Manager.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlobSize > MaximumCredentialBlobSize)
                throw new InvalidDataException("The stored ticketing credential has an invalid size.");
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return JsonSerializer.Deserialize<TicketingCredentials>(Encoding.UTF8.GetString(bytes), JsonOptions);
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public void Save(TicketingProvider provider, TicketingCredentials credentials)
    {
        EnsureWindows();
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentials.Secret);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(credentials, JsonOptions));
        if (bytes.Length > MaximumCredentialBlobSize)
            throw new ArgumentException("Ticketing credentials exceed the Windows Credential Manager size limit.", nameof(credentials));

        var blob = Marshal.AllocHGlobal(bytes.Length);
        var targetName = Marshal.StringToCoTaskMemUni(TargetName(provider));
        var userName = Marshal.StringToCoTaskMemUni(credentials.UserName ?? string.Empty);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var native = new NativeCredential
            {
                Type = GenericCredential,
                TargetName = targetName,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = LocalMachinePersistence,
                UserName = userName
            };
            if (!CredWrite(ref native, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to save ticketing credentials in Windows Credential Manager.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            Marshal.FreeHGlobal(blob);
            Marshal.ZeroFreeCoTaskMemUnicode(targetName);
            Marshal.ZeroFreeCoTaskMemUnicode(userName);
            Array.Clear(bytes);
        }
    }

    public void Delete(TicketingProvider provider)
    {
        EnsureWindows();
        if (!CredDelete(TargetName(provider), GenericCredential, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound) throw new Win32Exception(error, "Unable to delete ticketing credentials from Windows Credential Manager.");
        }
    }

    private static string TargetName(TicketingProvider provider) => $"YASK-CIEM/Ticketing/{provider}";

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Ticketing credentials require Windows Credential Manager.");
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
