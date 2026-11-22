using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using SyncAgent.Configuration;
using SyncAgent.Persistence;
using SyncAgent.Provisioning;

namespace SyncAgent.Heartbeat;

/// <summary>
/// Aviso ativo no painel, enviado ao ERP no heartbeat (contrato Sync 2.20.0, <c>alerts[]</c>).
/// O ERP compara a lista inteira com a anterior por (<see cref="Code"/>, <see cref="Key"/>) e
/// notifica os superusuarios quando um aviso aparece e quando some.
/// </summary>
public sealed record AgentAlert(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("connection_name")] string? ConnectionName = null);

public static class AgentAlertCodes
{
    public const string ErpSecurityConfig = "erp_security_config";
    public const string LocalDbEntityTypeCheck = "local_db_entity_type_check";
    public const string ArpaConnectionBlocked = "arpa_connection_blocked";
    public const string ArpaConnectionWarning = "arpa_connection_warning";

    public const string SeverityWarning = "warning";
    public const string SeverityError = "error";
}

/// <summary>
/// Monta a lista de avisos a partir das mesmas fontes do topo do dashboard
/// (<c>LocalStatusServer.BuildConfigWarningsAsync</c>). Puro, para teste.
/// </summary>
public static class AgentAlertsBuilder
{
    /// <summary>Limite do contrato (<c>maxItems</c>).</summary>
    public const int MaxAlerts = 50;

    /// <summary>Limite do contrato para <c>message</c>.</summary>
    public const int MaxMessageLength = 2000;

    public static IReadOnlyList<AgentAlert> Build(
        IReadOnlyList<ErpSecurityConfigWarning> securityWarnings,
        IReadOnlyList<string> outdatedEntityTypes,
        IReadOnlyList<ArpaConnectionBlock> blockedConnections,
        IReadOnlyList<ArpaConnectionBlock> connectionWarnings)
    {
        var alerts = new List<AgentAlert>();

        alerts.AddRange(securityWarnings.Select(w => new AgentAlert(
            AgentAlertCodes.ErpSecurityConfig, w.Key, AgentAlertCodes.SeverityError, w.Message)));

        if (outdatedEntityTypes.Count > 0)
        {
            alerts.Add(new AgentAlert(
                AgentAlertCodes.LocalDbEntityTypeCheck,
                string.Empty,
                AgentAlertCodes.SeverityError,
                "O banco local do SyncAgent esta com a lista de entity_type desatualizada (faltando: "
                + $"{string.Join(", ", outdatedEntityTypes)}). A sincronizacao dessas entidades falha ate rodar "
                + "'Corrigir outbox_events_entity_type_check' no painel do SyncAgent "
                + "(Configuracoes > Manutencao do banco local)."));
        }

        alerts.AddRange(blockedConnections.Select(b => new AgentAlert(
            AgentAlertCodes.ArpaConnectionBlocked, b.ConnectionId, AgentAlertCodes.SeverityError,
            b.Reason, b.ConnectionName)));

        alerts.AddRange(connectionWarnings.Select(b => new AgentAlert(
            AgentAlertCodes.ArpaConnectionWarning, b.ConnectionId, AgentAlertCodes.SeverityWarning,
            b.Reason, b.ConnectionName)));

        return alerts
            .Take(MaxAlerts)
            .Select(a => a.Message.Length > MaxMessageLength
                ? a with { Message = a.Message[..MaxMessageLength] }
                : a)
            .ToList();
    }
}

/// <summary>Le as fontes reais de aviso para o heartbeat.</summary>
public sealed class AgentAlertsProvider
{
    private readonly IOptionsMonitor<ErpSecurityOptions> _erpSecurityOptions;
    private readonly EffectiveSyncAgentConfigurationProvider _effectiveConfigProvider;
    private readonly LocalSyncStore _localStore;
    private readonly ArpaConnectionBlocks _arpaConnectionBlocks;

    public AgentAlertsProvider(
        IOptionsMonitor<ErpSecurityOptions> erpSecurityOptions,
        EffectiveSyncAgentConfigurationProvider effectiveConfigProvider,
        LocalSyncStore localStore,
        ArpaConnectionBlocks arpaConnectionBlocks)
    {
        _erpSecurityOptions = erpSecurityOptions;
        _effectiveConfigProvider = effectiveConfigProvider;
        _localStore = localStore;
        _arpaConnectionBlocks = arpaConnectionBlocks;
    }

    public async Task<IReadOnlyList<AgentAlert>> GetActiveAlertsAsync(CancellationToken cancellationToken)
    {
        var security = ErpSecurityConfigDiagnostics.Evaluate(
            _erpSecurityOptions.CurrentValue,
            _effectiveConfigProvider.GetCurrent().AccessToken);
        var outdated = await _localStore.GetOutdatedEntityTypeCheckEntitiesAsync(cancellationToken);

        return AgentAlertsBuilder.Build(
            security,
            outdated,
            _arpaConnectionBlocks.Snapshot,
            _arpaConnectionBlocks.WarningsSnapshot);
    }
}
