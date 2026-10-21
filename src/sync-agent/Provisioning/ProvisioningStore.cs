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
    private readonly ILogger<ProvisioningStore> _logger;

    public ProvisioningStore(IOptionsMonitor<SyncAgentProvisioningOptions> options, ILogger<ProvisioningStore> logger)
    {
        _options = options;
        _logger = logger;
    }

    public bool IsEnabled => _options.CurrentValue.Enabled;

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

        try
        {
            var json = UnprotectWindowsDpapiSecret(protectedValue);
            return JsonSerializer.Deserialize<ProvisionedAgentCredentials>(json, JsonOptions)
                ?? throw new JsonException("Credenciais de provisionamento vazias.");
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or JsonException)
        {
            QuarantineCorruptFile(options.ProtectedFile, ex);
            return null;
        }
    }

    /// <summary>
    /// Arquivo ilegivel (ex.: zerado por queda de energia logo apos uma rotacao
    /// do refresh, ou DPAPI de outra maquina) nunca se recupera sozinho. Antes,
    /// cada leitura estourava e derrubava com 500 /status, /, /help e
    /// /update-check (a bandeja parava de atualizar) e todo ciclo de sync. Tirar
    /// o arquivo do caminho deixa a instalacao "nao ativada": o dashboard volta
    /// a funcionar e mostra /setup para reativar. O original fica ao lado para
    /// diagnostico.
    /// </summary>
    private void QuarantineCorruptFile(string path, Exception reason)
    {
        var quarantinePath = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
        try
        {
            File.Move(path, quarantinePath, overwrite: true);
            _logger.LogError(
                reason,
                "Credenciais de provisionamento ilegiveis em {Path}; movidas para {QuarantinePath}. A instalacao precisa ser reativada em /setup.",
                path,
                quarantinePath);
        }
        catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException)
        {
            // Outra leitura concorrente (API local x Worker) pode ter movido primeiro.
            if (File.Exists(path))
            {
                _logger.LogError(moveEx, "Nao foi possivel mover as credenciais ilegiveis de {Path}.", path);
            }
        }
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
        // O refresh e regravado a cada rotacao: grava em arquivo temporario e troca de uma vez,
        // para uma queda no meio nunca deixar as credenciais truncadas. O Flush(true) antes da
        // troca e obrigatorio: o NTFS so registra metadados no journal, e sem forcar os dados
        // para o disco uma queda logo apos o Move deixava o arquivo com o tamanho certo e o
        // conteudo zerado (VM de homologacao, 2026-10-14 -> agente parado ate reativar).
        var tempPath = path + ".tmp";
        await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = Encoding.ASCII.GetBytes(protectedValue);
            await stream.WriteAsync(bytes, cancellationToken);
            stream.Flush(flushToDisk: true);
        }

        File.Move(tempPath, path, overwrite: true);
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
