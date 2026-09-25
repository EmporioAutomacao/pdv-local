using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Nodes;
using SyncAgent.Configuration;

namespace SyncAgent.Provisioning;

/// <summary>
/// Guarda, num arquivo JSON simples nesta maquina (sem DPAPI — tamanho de
/// lote nao e segredo), o ajuste de <c>ErpDispatcher:BatchSize</c> feito na
/// aba Configuracoes > Arpa (painel "Envio ao ERP"). Enquanto ausente,
/// prevalece o valor estatico do appsettings.json (comportamento historico,
/// preservado para instalacoes que nunca usaram o campo). Mesmo padrao de
/// <see cref="ArpaCollectorSettingsStore"/>.
/// </summary>
public sealed class ErpDispatcherSettingsStore
{
    private readonly ILogger<ErpDispatcherSettingsStore> _logger;
    private readonly IOptionsMonitor<ErpDispatcherOptions> _dispatcherOptions;
    private readonly IOptionsMonitor<SyncAgentProvisioningOptions> _provisioningOptions;
    private readonly object _lock = new();

    public ErpDispatcherSettingsStore(
        ILogger<ErpDispatcherSettingsStore> logger,
        IOptionsMonitor<ErpDispatcherOptions> dispatcherOptions,
        IOptionsMonitor<SyncAgentProvisioningOptions> provisioningOptions)
    {
        _logger = logger;
        _dispatcherOptions = dispatcherOptions;
        _provisioningOptions = provisioningOptions;
    }

    public string? ResolvePath()
    {
        var protectedFile = _provisioningOptions.CurrentValue.ProtectedFile;
        if (string.IsNullOrWhiteSpace(protectedFile))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(protectedFile));
        return string.IsNullOrWhiteSpace(directory)
            ? "erp-dispatcher-settings.json"
            : Path.Combine(directory, "erp-dispatcher-settings.json");
    }

    /// <summary>Null enquanto o campo nunca foi ajustado nesta instalacao.</summary>
    public int? ReadBatchSizeOverride()
    {
        try
        {
            var path = ResolvePath();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            var node = JsonNode.Parse(File.ReadAllText(path));
            return node?["batchSize"]?.GetValue<int?>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao ler a preferencia local de batch size do dispatcher ERP; usando o appsettings.json.");
            return null;
        }
    }

    public void SetBatchSizeOverride(int batchSize)
    {
        var path = ResolvePath()
            ?? throw new InvalidOperationException("Nao foi possivel resolver o arquivo de preferencias do dispatcher ERP (Provisioning:ProtectedFile vazio).");

        lock (_lock)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(new { batchSize }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
            File.WriteAllText(path, json);
        }
    }

    /// <summary>Valor que o dispatcher de fato usa: override local se ja foi
    /// definido pelo campo; senao, o valor estatico do appsettings.json.</summary>
    public int EffectiveBatchSize() => ReadBatchSizeOverride() ?? _dispatcherOptions.CurrentValue.BatchSize;
}
