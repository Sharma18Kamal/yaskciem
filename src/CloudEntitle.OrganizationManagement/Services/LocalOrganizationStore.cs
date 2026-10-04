using System.IO;
using System.Text.Json;
using CloudEntitle.OrganizationManagement.Abstractions;
using CloudEntitle.OrganizationManagement.Models;

namespace CloudEntitle.OrganizationManagement.Services;

public sealed class LocalOrganizationStore : IOrganizationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly string _filePath;
    private readonly object _gate = new();

    public LocalOrganizationStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YASK-CIEM", "organizations", "directory.json");
    }

    public OrganizationDirectoryState Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_filePath)) return EmptyState();
            try
            {
                var state = JsonSerializer.Deserialize<OrganizationDirectoryState>(File.ReadAllText(_filePath), JsonOptions);
                return state is null ? EmptyState() : Normalize(state);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The local organization directory is corrupted and was not loaded.", exception);
            }
        }
    }

    public void Save(OrganizationDirectoryState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var temporaryPath = _filePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(Normalize(state), JsonOptions));
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
    }

    private static OrganizationDirectoryState EmptyState() => new(
        Array.Empty<OrganizationProfile>(), Array.Empty<OrganizationMember>(), Array.Empty<TenantBinding>());

    private static OrganizationDirectoryState Normalize(OrganizationDirectoryState state) => new(
        state.Organizations ?? Array.Empty<OrganizationProfile>(),
        state.Members ?? Array.Empty<OrganizationMember>(),
        state.Bindings ?? Array.Empty<TenantBinding>())
    {
        AuditEntries = state.AuditEntries ?? Array.Empty<OrganizationAuditEntry>()
    };
}
