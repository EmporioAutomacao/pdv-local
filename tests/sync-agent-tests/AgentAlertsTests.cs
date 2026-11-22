using System.Text.Json;
using SyncAgent.Configuration;
using SyncAgent.Heartbeat;
using SyncAgent.Provisioning;
using Xunit;

namespace SyncAgent.Tests;

/// <summary>Avisos do painel enviados ao ERP no heartbeat (contrato Sync 2.20.0).</summary>
public sealed class AgentAlertsTests
{
    private static readonly IReadOnlyList<ErpSecurityConfigWarning> NoSecurity = Array.Empty<ErpSecurityConfigWarning>();
    private static readonly IReadOnlyList<string> NoOutdated = Array.Empty<string>();
    private static readonly IReadOnlyList<ArpaConnectionBlock> NoBlocks = Array.Empty<ArpaConnectionBlock>();

    [Fact]
    public void Sem_avisos_gera_lista_vazia()
    {
        Assert.Empty(AgentAlertsBuilder.Build(NoSecurity, NoOutdated, NoBlocks, NoBlocks));
    }

    [Fact]
    public void Conexao_pausada_vira_erro_com_id_da_conexao_como_chave()
    {
        var blocked = new[] { new ArpaConnectionBlock("c1", "Geral", "coleta pausada", ArpaBlockKind.Dependencias) };
        var warning = new[] { new ArpaConnectionBlock("c2", "Loja 2", "atualize o ERP") };

        var alerts = AgentAlertsBuilder.Build(NoSecurity, NoOutdated, blocked, warning);

        Assert.Equal(2, alerts.Count);
        Assert.Equal(new AgentAlert(AgentAlertCodes.ArpaConnectionBlocked, "c1", "error", "coleta pausada", "Geral"), alerts[0]);
        Assert.Equal(new AgentAlert(AgentAlertCodes.ArpaConnectionWarning, "c2", "warning", "atualize o ERP", "Loja 2"), alerts[1]);
    }

    [Fact]
    public void Banco_local_desatualizado_lista_os_tipos_e_nao_leva_link_local()
    {
        var alerts = AgentAlertsBuilder.Build(NoSecurity, new[] { "catalogo", "cobranca" }, NoBlocks, NoBlocks);

        var alert = Assert.Single(alerts);
        Assert.Equal(AgentAlertCodes.LocalDbEntityTypeCheck, alert.Code);
        Assert.Equal(string.Empty, alert.Key);
        Assert.Equal("error", alert.Severity);
        Assert.Contains("catalogo, cobranca", alert.Message);
        Assert.DoesNotContain("/config/", alert.Message);
    }

    [Fact]
    public void Respeita_limites_do_contrato()
    {
        var blocks = Enumerable.Range(0, 60)
            .Select(i => new ArpaConnectionBlock($"c{i}", $"C{i}", new string('x', 3000)))
            .ToList();

        var alerts = AgentAlertsBuilder.Build(NoSecurity, NoOutdated, blocks, NoBlocks);

        Assert.Equal(AgentAlertsBuilder.MaxAlerts, alerts.Count);
        Assert.All(alerts, a => Assert.Equal(AgentAlertsBuilder.MaxMessageLength, a.Message.Length));
    }

    [Fact]
    public void Token_da_ativacao_nao_gera_aviso_de_bearer_token()
    {
        var options = new ErpSecurityOptions
        {
            RequireBearerToken = true,
            AccessToken = string.Empty,
            AccessTokenEnvironmentVariable = "PDV_SYNC_TEST_TOKEN_QUE_NAO_EXISTE",
        };

        Assert.Empty(ErpSecurityConfigDiagnostics.Evaluate(options, provisionedAccessToken: "tok"));

        var warning = Assert.Single(ErpSecurityConfigDiagnostics.Evaluate(options, provisionedAccessToken: null));
        Assert.Equal(ErpSecurityConfigDiagnostics.BearerTokenKey, warning.Key);
    }

    [Fact]
    public void Heartbeat_serializa_alerts_no_formato_do_contrato()
    {
        var request = new HeartbeatRequest(
            DateTimeOffset.Parse("2026-11-22T00:00:00Z"), "1.6.56", 0, 0, "online", null,
            new[] { new AgentAlert("arpa_connection_blocked", "c1", "error", "msg", "Geral") });

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(request));
        var alert = doc.RootElement.GetProperty("alerts")[0];

        Assert.Equal("arpa_connection_blocked", alert.GetProperty("code").GetString());
        Assert.Equal("c1", alert.GetProperty("key").GetString());
        Assert.Equal("error", alert.GetProperty("severity").GetString());
        Assert.Equal("msg", alert.GetProperty("message").GetString());
        Assert.Equal("Geral", alert.GetProperty("connection_name").GetString());
    }
}
