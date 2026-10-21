using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SyncAgent.Configuration;
using SyncAgent.Provisioning;

namespace SyncAgent.Tests;

public sealed class ProvisioningStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "provisioning-store-tests", Guid.NewGuid().ToString("N"));
    private readonly string _file;
    private readonly ProvisioningStore _store;

    public ProvisioningStoreTests()
    {
        Directory.CreateDirectory(_directory);
        _file = Path.Combine(_directory, "sync-agent-provisioning.dpapi");
        _store = new ProvisioningStore(
            new FixedOptionsMonitor<SyncAgentProvisioningOptions>(new() { Enabled = true, ProtectedFile = _file }),
            NullLogger<ProvisioningStore>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    [Fact]
    public void ZeroFilledFileIsQuarantinedInsteadOfThrowing()
    {
        // O que a queda de energia deixou na VM: tamanho certo, conteudo zerado.
        File.WriteAllBytes(_file, new byte[1204]);

        Assert.Null(_store.TryRead());
        Assert.False(File.Exists(_file));
        Assert.Single(Directory.GetFiles(_directory, "sync-agent-provisioning.dpapi.corrupt-*"));
    }

    [Fact]
    public void ValidBase64ThatIsNotDpapiIsQuarantined()
    {
        File.WriteAllText(_file, Convert.ToBase64String("nao e dpapi"u8.ToArray()));

        Assert.Null(_store.TryRead());
        Assert.False(File.Exists(_file));
    }

    [Fact]
    public async Task SaveThenReadRoundTrips()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var credentials = new ProvisionedAgentCredentials(
            "instance", "tenant", "https://erp.example.com",
            "access", DateTimeOffset.UtcNow.AddHours(1),
            "refresh", null,
            false, DateTimeOffset.UtcNow);

        await _store.SaveAsync(credentials, CancellationToken.None);

        var read = _store.TryRead();
        Assert.NotNull(read);
        Assert.Equal("refresh", read!.RefreshToken);
        Assert.False(File.Exists(_file + ".tmp"));
    }

    private sealed class FixedOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
