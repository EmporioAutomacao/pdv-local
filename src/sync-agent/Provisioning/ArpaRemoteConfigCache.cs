using Microsoft.Extensions.Options;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SyncAgent.Configuration;

namespace SyncAgent.Provisioning;

/// <summary>
/// Cache local, protegido por DPAPI, da ultima configuracao de conexao Arpa
/// obtida com sucesso do ERP. Permite que o ArpaCollector continue operando
/// com a ultima configuracao conhecida caso o ERP fique temporariamente
/// inacessivel.
/// </summary>
public sealed class ArpaRemoteConfigCache
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly IOptionsMonitor<ArpaCollectorOptions> _options;

    public ArpaRemoteConfigCache(IOptionsMonitor<ArpaCollectorOptions> options)
    {
        _options = options;
    }

    public ArpaConnectionConfig? TryRead()
    {
        var path = _options.CurrentValue.RemoteConfigCacheProtectedFile;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("ArpaCollector:RemoteConfigCacheProtectedFile requires Windows DPAPI.");
        }

        var protectedValue = File.ReadAllText(path).Trim();
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            return null;
        }

        var json = UnprotectWindowsDpapiSecret(protectedValue);
        return JsonSerializer.Deserialize<ArpaConnectionConfig>(json, JsonOptions);
    }

    public async Task SaveAsync(ArpaConnectionConfig config, CancellationToken cancellationToken)
    {
        var path = _options.CurrentValue.RemoteConfigCacheProtectedFile;
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("ArpaCollector:RemoteConfigCacheProtectedFile is required.");
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("ArpaCollector:RemoteConfigCacheProtectedFile requires Windows DPAPI.");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(config, JsonOptions);
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
