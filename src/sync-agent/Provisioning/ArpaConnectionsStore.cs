using Microsoft.Extensions.Options;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SyncAgent.Configuration;

namespace SyncAgent.Provisioning;

/// <summary>
/// Persiste a lista de conexoes Arpa geridas pelo dashboard (aba
/// Configuracoes > Arpa) num arquivo JSON cifrado por DPAPI LocalMachine.
/// Espelha <see cref="ArpaRemoteConfigCache"/>: contem senhas, cifrado inteiro.
/// </summary>
public sealed class ArpaConnectionsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly ILogger<ArpaConnectionsStore> _logger;
    private readonly IOptionsMonitor<ArpaCollectorOptions> _collectorOptions;
    private readonly IOptionsMonitor<SyncAgentProvisioningOptions> _provisioningOptions;
    private readonly object _lock = new();

    public ArpaConnectionsStore(
        ILogger<ArpaConnectionsStore> logger,
        IOptionsMonitor<ArpaCollectorOptions> collectorOptions,
        IOptionsMonitor<SyncAgentProvisioningOptions> provisioningOptions)
    {
        _logger = logger;
        _collectorOptions = collectorOptions;
        _provisioningOptions = provisioningOptions;
    }

    public string? ResolvePath()
    {
        var configured = _collectorOptions.CurrentValue.LocalConnectionsProtectedFile;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var protectedFile = _provisioningOptions.CurrentValue.ProtectedFile;
        if (string.IsNullOrWhiteSpace(protectedFile))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(protectedFile));
        return string.IsNullOrWhiteSpace(directory)
            ? "arpa-connections.dpapi"
            : Path.Combine(directory, "arpa-connections.dpapi");
    }

    public IReadOnlyList<ArpaLocalConnection> ReadAll()
    {
        try
        {
            var path = ResolvePath();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return [];
            }

            var protectedValue = File.ReadAllText(path).Trim();
            if (string.IsNullOrWhiteSpace(protectedValue))
            {
                return [];
            }

            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("ArpaCollector:LocalConnectionsProtectedFile requires Windows DPAPI.");
            }

            var json = UnprotectWindowsDpapiSecret(protectedValue);
            return JsonSerializer.Deserialize<List<ArpaLocalConnection>>(json, JsonOptions) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao ler o store de conexoes Arpa; tratando como vazio.");
            return [];
        }
    }

    public void WriteAll(IReadOnlyList<ArpaLocalConnection> connections)
    {
        var path = ResolvePath()
            ?? throw new InvalidOperationException("ArpaCollector:LocalConnectionsProtectedFile nao resolvido (Provisioning:ProtectedFile vazio).");

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("ArpaCollector:LocalConnectionsProtectedFile requires Windows DPAPI.");
        }

        lock (_lock)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(connections, JsonOptions);
            File.WriteAllText(path, ProtectWindowsDpapiSecret(json), Encoding.ASCII);
        }
    }

    /// <summary>Insere (gera Id) ou atualiza a conexao pelo Id. Retorna o Id efetivo.</summary>
    public string Upsert(ArpaLocalConnection connection)
    {
        lock (_lock)
        {
            var list = ReadAll().ToList();
            var id = string.IsNullOrWhiteSpace(connection.Id)
                ? Guid.NewGuid().ToString("N")
                : connection.Id;
            var withId = connection with { Id = id };

            var index = list.FindIndex(c => c.Id == id);
            if (index >= 0)
            {
                list[index] = withId;
            }
            else
            {
                list.Add(withId);
            }

            WriteAll(list);
            return id;
        }
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            var list = ReadAll().ToList();
            var removed = list.RemoveAll(c => c.Id == id) > 0;
            if (removed)
            {
                WriteAll(list);
            }

            return removed;
        }
    }

    public ArpaLocalConnection? Get(string id)
        => ReadAll().FirstOrDefault(c => c.Id == id);

    [SupportedOSPlatform("windows")]
    private static string ProtectWindowsDpapiSecret(string value)
    {
        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(value),
            optionalEntropy: null,
            scope: DataProtectionScope.LocalMachine);

        return Convert.ToBase64String(protectedBytes);
    }

    [SupportedOSPlatform("windows")]
    private static string UnprotectWindowsDpapiSecret(string protectedValue)
    {
        var plainBytes = ProtectedData.Unprotect(
            Convert.FromBase64String(protectedValue),
            optionalEntropy: null,
            scope: DataProtectionScope.LocalMachine);

        return Encoding.UTF8.GetString(plainBytes);
    }
}
