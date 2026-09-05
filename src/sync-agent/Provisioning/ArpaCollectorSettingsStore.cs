using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Nodes;
using SyncAgent.Configuration;

namespace SyncAgent.Provisioning;

/// <summary>
/// Guarda, num arquivo JSON simples nesta maquina (sem DPAPI — nao ha
/// segredo aqui, so um booleano), a decisao de ligar/desligar o coletor Arpa
/// feita na aba Configuracoes > Arpa. Enquanto ausente, prevalece
/// <c>ArpaCollector:Enabled</c> do appsettings.json (comportamento historico,
/// preservado para instalacoes que nunca usaram o botao). Uma vez usado o
/// botao, o valor gravado aqui manda — inclusive para religar um agente que
/// veio com <c>Enabled=false</c> no appsettings, sem precisar editar o
/// arquivo nem reiniciar o servico manualmente.
/// </summary>
public sealed class ArpaCollectorSettingsStore
{
    private readonly ILogger<ArpaCollectorSettingsStore> _logger;
    private readonly IOptionsMonitor<ArpaCollectorOptions> _collectorOptions;
    private readonly IOptionsMonitor<SyncAgentProvisioningOptions> _provisioningOptions;
    private readonly object _lock = new();

    public ArpaCollectorSettingsStore(
        ILogger<ArpaCollectorSettingsStore> logger,
        IOptionsMonitor<ArpaCollectorOptions> collectorOptions,
        IOptionsMonitor<SyncAgentProvisioningOptions> provisioningOptions)
    {
        _logger = logger;
        _collectorOptions = collectorOptions;
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
            ? "arpa-collector-settings.json"
            : Path.Combine(directory, "arpa-collector-settings.json");
    }

    /// <summary>Null enquanto o botao nunca foi usado nesta instalacao.</summary>
    public bool? ReadEnabledOverride()
    {
        try
        {
            var path = ResolvePath();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            var node = JsonNode.Parse(File.ReadAllText(path));
            return node?["enabled"]?.GetValue<bool?>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao ler a preferencia local de habilitacao do coletor Arpa; usando o appsettings.json.");
            return null;
        }
    }

    public void SetEnabledOverride(bool enabled)
    {
        var path = ResolvePath()
            ?? throw new InvalidOperationException("Nao foi possivel resolver o arquivo de preferencias do coletor Arpa (Provisioning:ProtectedFile vazio).");

        lock (_lock)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(new { enabled }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
            File.WriteAllText(path, json);
        }
    }

    /// <summary>Estado que o coletor de fato usa: override local se ja foi
    /// definido pelo botao; senao, o valor estatico do appsettings.json.</summary>
    public bool IsEffectivelyEnabled() => ReadEnabledOverride() ?? _collectorOptions.CurrentValue.Enabled;
}
