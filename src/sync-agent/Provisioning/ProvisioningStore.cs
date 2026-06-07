using Microsoft.Extensions.Options;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SyncAgent.Configuration;

namespace SyncAgent.Provisioning;

public sealed class ProvisioningStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly IOptionsMonitor<SyncAgentProvisioningOptions> _options;

    public ProvisioningStore(IOptionsMonitor<SyncAgentProvisioningOptions> options)
    {
        _options = options;
    }

    public bool IsEnabled => _options.CurrentValue.Enabled;

    public bool HasProvisioning()
    {
        var path = _options.CurrentValue.ProtectedFile;
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
    }

    public ProvisionedAgentCredentials? TryRead()
    {
        var options = _options.CurrentValue;
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.ProtectedFile) || !File.Exists(options.ProtectedFile))
        {
            return null;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Provisioning protected file requires Windows DPAPI.");
        }

        var protectedValue = File.ReadAllText(options.ProtectedFile).Trim();
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            return null;
        }

        var json = UnprotectWindowsDpapiSecret(protectedValue);
        return JsonSerializer.Deserialize<ProvisionedAgentCredentials>(json, JsonOptions);
    }

    public async Task SaveAsync(ProvisionedAgentCredentials credentials, CancellationToken cancellationToken)
    {
        var path = _options.CurrentValue.ProtectedFile;
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Provisioning:ProtectedFile is required.");
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Provisioning protected file requires Windows DPAPI.");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(credentials, JsonOptions);
        var protectedValue = ProtectWindowsDpapiSecret(json);
        await File.WriteAllTextAsync(path, protectedValue, Encoding.ASCII, cancellationToken);
    }

    [SupportedOSPlatform("windows")]
    private static string ProtectWindowsDpapiSecret(string value)
    {
        var plainBytes = Encoding.UTF8.GetBytes(value);
        var protectedBytes = ProtectedData.Protect(
            plainBytes,
            optionalEntropy: null,
            scope: DataProtectionScope.LocalMachine);

        return Convert.ToBase64String(protectedBytes);
    }

    [SupportedOSPlatform("windows")]
    private static string UnprotectWindowsDpapiSecret(string protectedValue)
    {
        var encryptedBytes = Convert.FromBase64String(protectedValue);
        var plainBytes = ProtectedData.Unprotect(
            encryptedBytes,
            optionalEntropy: null,
            scope: DataProtectionScope.LocalMachine);

        return Encoding.UTF8.GetString(plainBytes);
    }
}
