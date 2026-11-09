$ErrorActionPreference = 'Stop'
if (-not ('YaskCredentialWriter' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
public static class YaskCredentialWriter
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string TargetName, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias, UserName;
    }
    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, uint flags);
    public static void Write(string target, string user, SecureString secret)
    {
        var buffer = Marshal.SecureStringToGlobalAllocUnicode(secret);
        try
        {
            var credential = new Credential {
                Type = 1, TargetName = target, Comment = "YASK-CIEM License Server",
                CredentialBlobSize = checked((uint)(secret.Length * 2)), CredentialBlob = buffer,
                Persist = 2, UserName = user
            };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.ZeroFreeGlobalAllocUnicode(buffer); }
    }
}
'@
}
