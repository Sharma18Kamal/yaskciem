using System.IO;
using System.Text.Json;
using CloudEntitle.IdentityGovernance.Models;

namespace CloudEntitle.Desktop.Services;

public interface IAccessReviewStore
{
    Task<IReadOnlyList<AccessReviewCampaign>> GetAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AccessReviewCampaign campaign, CancellationToken cancellationToken = default);
}

public sealed class LocalAccessReviewStore : IAccessReviewStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LocalAccessReviewStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YASK-CIEM", "governance", "access-reviews.json");
    }

    public async Task<IReadOnlyList<AccessReviewCampaign>> GetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AccessReviewCampaign campaign, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var campaigns = (await ReadAsync(cancellationToken)).ToDictionary(item => item.Id);
            campaigns[campaign.Id] = campaign;
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var temporaryPath = _filePath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(campaigns.Values.OrderBy(item => item.CreatedAt), JsonOptions), cancellationToken);
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<AccessReviewCampaign>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath)) return Array.Empty<AccessReviewCampaign>();
        var json = await File.ReadAllTextAsync(_filePath, cancellationToken);
        return JsonSerializer.Deserialize<List<AccessReviewCampaign>>(json, JsonOptions) ?? new List<AccessReviewCampaign>();
    }
}
