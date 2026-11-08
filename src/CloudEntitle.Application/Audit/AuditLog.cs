using System.Security.Cryptography;
using System.Text;

namespace CloudEntitle.Application.Audit;

public sealed record AuditEntry(
    long Sequence,
    Guid OrganizationId,
    DateTimeOffset Timestamp,
    string Actor,
    string Action,
    string Payload,
    string PreviousHash,
    string Hash);

public sealed record AuditVerification(bool IsValid, long? BrokenAtSequence, string? Reason);

/// <summary>Append-only, tamper-evident log. Each organization has its own chain.</summary>
public interface IAuditLog
{
    Task<AuditEntry> AppendAsync(Guid organizationId, string actor, string action, string payload, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AuditEntry>> ReadAsync(Guid organizationId, CancellationToken cancellationToken = default);

    Task<AuditVerification> VerifyAsync(Guid organizationId, CancellationToken cancellationToken = default);
}

public static class AuditChain
{
    public static readonly string GenesisHash = new('0', 64);

    /// <summary>SHA256(previousHash + canonical payload), hex-encoded.</summary>
    public static string ComputeHash(string previousHash, long sequence, Guid organizationId, DateTimeOffset timestamp, string actor, string action, string payload)
    {
        var canonical = string.Join('|', sequence, organizationId, timestamp.UtcDateTime.ToString("O"), actor, action, payload);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(previousHash + canonical))).ToLowerInvariant();
    }

    public static AuditEntry Next(AuditEntry? previous, Guid organizationId, DateTimeOffset timestamp, string actor, string action, string payload)
    {
        var sequence = (previous?.Sequence ?? 0) + 1;
        var previousHash = previous?.Hash ?? GenesisHash;
        var utc = new DateTimeOffset(timestamp.UtcDateTime, TimeSpan.Zero);
        return new AuditEntry(sequence, organizationId, utc, actor, action, payload, previousHash,
            ComputeHash(previousHash, sequence, organizationId, utc, actor, action, payload));
    }

    public static AuditVerification Verify(IReadOnlyList<AuditEntry> entries)
    {
        var previousHash = GenesisHash;
        long expected = 1;
        foreach (var entry in entries.OrderBy(e => e.Sequence))
        {
            if (entry.Sequence != expected)
            {
                return new AuditVerification(false, entry.Sequence, "Sequence gap or reordering.");
            }

            if (entry.PreviousHash != previousHash)
            {
                return new AuditVerification(false, entry.Sequence, "Previous hash does not match the chain.");
            }

            var recomputed = ComputeHash(previousHash, entry.Sequence, entry.OrganizationId, entry.Timestamp, entry.Actor, entry.Action, entry.Payload);
            if (!string.Equals(recomputed, entry.Hash, StringComparison.Ordinal))
            {
                return new AuditVerification(false, entry.Sequence, "Entry content was modified.");
            }

            previousHash = entry.Hash;
            expected++;
        }

        return new AuditVerification(true, null, null);
    }
}

public sealed class InMemoryAuditLog : IAuditLog
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, List<AuditEntry>> _chains = [];

    public Task<AuditEntry> AppendAsync(Guid organizationId, string actor, string action, string payload, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_chains.TryGetValue(organizationId, out var chain))
            {
                _chains[organizationId] = chain = [];
            }

            var entry = AuditChain.Next(chain.Count == 0 ? null : chain[^1], organizationId, DateTimeOffset.UtcNow, actor, action, payload);
            chain.Add(entry);
            return Task.FromResult(entry);
        }
    }

    public Task<IReadOnlyList<AuditEntry>> ReadAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<AuditEntry> copy = _chains.TryGetValue(organizationId, out var chain) ? chain.ToList() : [];
            return Task.FromResult(copy);
        }
    }

    public async Task<AuditVerification> VerifyAsync(Guid organizationId, CancellationToken cancellationToken = default)
        => AuditChain.Verify(await ReadAsync(organizationId, cancellationToken));
}
