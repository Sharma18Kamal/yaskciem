namespace CloudEntitle.Licensing;

using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;

public sealed class TrialService : ITrialService
{
    private static readonly TimeSpan TrialDuration = TimeSpan.FromDays(14);
    private static readonly TimeSpan ClockRollbackTolerance = TimeSpan.FromMinutes(5);
    private readonly ITrialStateStore _stateStore;
    private readonly Func<DateTime> _utcNow;

    public TrialService(Func<DateTime>? utcNow = null, ITrialStateStore? stateStore = null)
    {
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _stateStore = stateStore ?? JsonTrialStateStore.CreateDefault();
    }

    public async Task<TrialInfo> StartTrialAsync(string userId, string deviceIdentifierHash, CancellationToken cancellationToken = default)
    {
        var email = NormalizeEmail(userId);
        var hardware = NormalizeHardwareIdentifier(deviceIdentifierHash);
        var emailHash = Hash(email);
        var hardwareHash = Hash(hardware);
        var registrations = await _stateStore.LoadAsync(cancellationToken);

        if (registrations.Any(registration => registration.EmailHash == emailHash))
        {
            throw new TrialAlreadyUsedException("This email address has already been used for a trial.");
        }

        if (registrations.Any(registration => registration.HardwareHash == hardwareHash))
        {
            throw new TrialAlreadyUsedException("This hardware has already been used for a trial.");
        }

        var start = AsUtc(_utcNow());
        var record = new TrialRegistration(emailHash, hardwareHash, start, start.Add(TrialDuration), start);
        await _stateStore.SaveAsync(registrations.Append(record), cancellationToken);
        return ToTrialInfo(record, email);
    }

    public async Task<TrialInfo?> GetTrialStatusAsync(string userId, string deviceIdentifierHash, CancellationToken cancellationToken = default)
    {
        var email = NormalizeEmail(userId);
        var emailHash = Hash(email);
        var hardwareHash = Hash(NormalizeHardwareIdentifier(deviceIdentifierHash));
        var registrations = await _stateStore.LoadAsync(cancellationToken);
        var record = registrations
            .FirstOrDefault(registration => registration.EmailHash == emailHash);
        if (record is null)
        {
            return null;
        }

        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(record.HardwareHash), Convert.FromHexString(hardwareHash)))
        {
            throw new InvalidOperationException("The trial is registered to a different device.");
        }

        var now = AsUtc(_utcNow());
        var lastObserved = record.LastObservedUtc == default ? record.TrialStart : AsUtc(record.LastObservedUtc);
        if (now < record.TrialStart || now + ClockRollbackTolerance < lastObserved)
        {
            record = record with { TrialEnd = record.TrialStart, LastObservedUtc = lastObserved };
            await _stateStore.SaveAsync(registrations.Select(registration =>
                registration.EmailHash == emailHash ? record : registration), cancellationToken);
            return ToTrialInfo(record, email);
        }

        var trialStart = AsUtc(record.TrialStart);
        var updatedTrialEnd = trialStart.Add(TrialDuration);
        var shouldExtendTrial = AsUtc(record.TrialEnd) > trialStart
            && AsUtc(record.TrialEnd) < updatedTrialEnd
            && now < updatedTrialEnd;
        var updatedRecord = record with
        {
            TrialEnd = shouldExtendTrial ? updatedTrialEnd : record.TrialEnd,
            LastObservedUtc = now > lastObserved ? now : record.LastObservedUtc
        };
        if (updatedRecord != record)
        {
            record = updatedRecord;
            await _stateStore.SaveAsync(registrations.Select(registration =>
                registration.EmailHash == emailHash ? record : registration), cancellationToken);
        }

        return ToTrialInfo(record, email);
    }

    private TrialInfo ToTrialInfo(TrialRegistration record, string email)
        => new()
        {
            TrialStart = record.TrialStart,
            TrialEnd = record.TrialEnd,
            Status = AsUtc(_utcNow()) >= AsUtc(record.TrialEnd) ? LicenseStatus.Expired : LicenseStatus.Trial,
            UserId = email,
            DeviceIdentifierHash = record.HardwareHash
        };

    private static string NormalizeEmail(string email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        return email.Trim().ToUpperInvariant();
    }

    public static string NormalizeHardwareIdentifier(string hardwareIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hardwareIdentifier);
        return new string(hardwareIdentifier.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}

public sealed class TrialAlreadyUsedException : InvalidOperationException
{
    public TrialAlreadyUsedException(string message) : base(message) { }
}

public sealed record TrialRegistration(string EmailHash, string HardwareHash, DateTime TrialStart, DateTime TrialEnd, DateTime LastObservedUtc = default);

public interface ITrialStateStore
{
    Task<IReadOnlyList<TrialRegistration>> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(IEnumerable<TrialRegistration> registrations, CancellationToken cancellationToken = default);
}

public sealed class InMemoryTrialStateStore : ITrialStateStore
{
    private readonly List<TrialRegistration> _registrations = new();

    public Task<IReadOnlyList<TrialRegistration>> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<TrialRegistration>>(_registrations.ToArray());
    }

    public Task SaveAsync(IEnumerable<TrialRegistration> registrations, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _registrations.Clear();
        _registrations.AddRange(registrations);
        return Task.CompletedTask;
    }
}

public sealed class JsonTrialStateStore : ITrialStateStore
{
    private readonly string _filePath;

    public JsonTrialStateStore(string filePath) => _filePath = filePath;

    public static JsonTrialStateStore CreateDefault()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YASK-CIEM");
        Directory.CreateDirectory(directory);
        return new JsonTrialStateStore(Path.Combine(directory, "trial-state.json"));
    }

    public async Task<IReadOnlyList<TrialRegistration>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath)) return Array.Empty<TrialRegistration>();
        await using var stream = File.OpenRead(_filePath);
        return await JsonSerializer.DeserializeAsync<List<TrialRegistration>>(stream, cancellationToken: cancellationToken) ?? new List<TrialRegistration>();
    }

    public async Task SaveAsync(IEnumerable<TrialRegistration> registrations, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temporaryPath = _filePath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, registrations.ToArray(), cancellationToken: cancellationToken);
        }
        File.Move(temporaryPath, _filePath, true);
    }
}

public static class MacAddressHardwareFingerprint
{
    public static string GetPrimaryMacAddress()
    {
        var networkInterface = NetworkInterface.GetAllNetworkInterfaces()
            .Where(candidate => candidate.OperationalStatus == OperationalStatus.Up)
            .Where(candidate => candidate.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
            .OrderBy(candidate => candidate.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        return networkInterface?.GetPhysicalAddress().ToString()
            ?? throw new InvalidOperationException("No active Ethernet or Wi-Fi MAC address was found.");
    }
}
