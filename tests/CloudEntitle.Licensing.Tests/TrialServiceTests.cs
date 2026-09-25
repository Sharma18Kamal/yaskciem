using CloudEntitle.Domain;
using CloudEntitle.Licensing;
using Xunit;

namespace CloudEntitle.Licensing.Tests;

public class TrialServiceTests
{
    [Fact]
    public async Task TrialService_ShouldCreateFourteenDayTrial()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var service = new TrialService(() => now, new InMemoryTrialStateStore());

        var trial = await service.StartTrialAsync("User@Example.com", "AA:BB:CC:DD:EE:FF");

        Assert.Equal(now, trial.TrialStart);
        Assert.Equal(now.AddDays(14), trial.TrialEnd);
        Assert.Equal(LicenseStatus.Trial, trial.Status);
    }

    [Fact]
    public async Task TrialService_ShouldExpireTrialAfterEndDate()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var service = new TrialService(() => now, new InMemoryTrialStateStore());
        await service.StartTrialAsync("user-1@example.com", "device-hash");

        now = now.AddDays(14);
        var trial = await service.GetTrialStatusAsync("USER-1@EXAMPLE.COM");

        Assert.NotNull(trial);
        Assert.Equal(LicenseStatus.Expired, trial.Status);
    }

    [Fact]
    public async Task TrialService_ShouldRejectSecondUseOfEmail()
    {
        var service = new TrialService(stateStore: new InMemoryTrialStateStore());
        await service.StartTrialAsync("user@example.com", "device-one");

        await Assert.ThrowsAsync<TrialAlreadyUsedException>(() =>
            service.StartTrialAsync("USER@EXAMPLE.COM", "device-two"));
    }

    [Fact]
    public async Task TrialService_ShouldRejectSecondEmailOnSameHardware()
    {
        var service = new TrialService(stateStore: new InMemoryTrialStateStore());
        await service.StartTrialAsync("first@example.com", "AA-BB-CC-DD-EE-FF");

        await Assert.ThrowsAsync<TrialAlreadyUsedException>(() =>
            service.StartTrialAsync("second@example.com", "aabbccddeeff"));
    }

    [Fact]
    public async Task TrialService_ShouldPersistOnlyHashes()
    {
        var store = new InMemoryTrialStateStore();
        var service = new TrialService(stateStore: store);
        await service.StartTrialAsync("User@Example.com", "AA:BB:CC:DD:EE:FF");

        var registrations = await store.LoadAsync();

        Assert.DoesNotContain("USER@EXAMPLE.COM", registrations[0].EmailHash);
        Assert.DoesNotContain("AABBCCDDEEFF", registrations[0].HardwareHash);
        Assert.Equal(64, registrations[0].EmailHash.Length);
        Assert.Equal(64, registrations[0].HardwareHash.Length);
    }
}
