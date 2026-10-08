using System.IO;
using System.Text.Json;

namespace CloudEntitle.Desktop.Services;

public sealed record TicketingRequestRecord(
    Guid Id,
    TicketingProvider Provider,
    string DedupeKey,
    string ExternalId,
    string TicketUrl,
    string Status,
    string? StatusUrl,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public interface ITicketingRequestStore
{
    Task<TicketingRequestRecord?> FindAsync(TicketingProvider provider, string dedupeKey, CancellationToken cancellationToken = default);
    Task SaveAsync(TicketingRequestRecord request, CancellationToken cancellationToken = default);
}

public sealed class LocalTicketingRequestStore : ITicketingRequestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LocalTicketingRequestStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YASK-CIEM", "ticketing", "requests.json");
    }

    public async Task<TicketingRequestRecord?> FindAsync(TicketingProvider provider, string dedupeKey, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return (await ReadAsync(cancellationToken)).FirstOrDefault(item => item.Provider == provider
                && string.Equals(item.DedupeKey, dedupeKey, StringComparison.Ordinal));
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(TicketingRequestRecord request, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var records = await ReadAsync(cancellationToken);
            var index = records.FindIndex(item => item.Provider == request.Provider
                && string.Equals(item.DedupeKey, request.DedupeKey, StringComparison.Ordinal));
            if (index < 0) records.Add(request);
            else records[index] = request;

            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var temporaryPath = _filePath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(records, JsonOptions), cancellationToken);
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally { _gate.Release(); }
    }

    private async Task<List<TicketingRequestRecord>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath)) return new List<TicketingRequestRecord>();
        var json = await File.ReadAllTextAsync(_filePath, cancellationToken);
        return JsonSerializer.Deserialize<List<TicketingRequestRecord>>(json, JsonOptions) ?? new List<TicketingRequestRecord>();
    }
}
