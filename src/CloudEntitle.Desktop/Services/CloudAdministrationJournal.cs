using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace CloudEntitle.Desktop.Services;

public interface ICloudAdministrationJournal
{
    Task AppendAsync(CloudAdministrationPlan plan, string action, CancellationToken token = default);
}

public sealed class CloudAdministrationJournal(string? directory = null) : ICloudAdministrationJournal
{
    private readonly string _directory = directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YASK-CIEM", "cloud-administration");
    public async Task AppendAsync(CloudAdministrationPlan plan, string action, CancellationToken token = default)
    {
        EnsurePrivateDirectory(_directory);
        var path = Path.Combine(_directory, $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffffff}-{Guid.NewGuid():N}.json");
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(file, new { recordedAtUtc = DateTimeOffset.UtcNow, action, plan },
            new JsonSerializerOptions { WriteIndented = true }, token);
        await file.FlushAsync(token);
        file.Flush(flushToDisk: true);
    }

    internal static void EnsurePrivateDirectory(string directory)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("A Windows user identity is required for private administration files.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(user);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        Directory.CreateDirectory(directory);
        new DirectoryInfo(directory).SetAccessControl(security);
    }
}
