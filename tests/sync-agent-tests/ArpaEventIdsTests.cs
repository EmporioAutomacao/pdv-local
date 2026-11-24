using System.Text.Json.Nodes;
using SyncAgent.Collectors;
using Xunit;

namespace SyncAgent.Tests;

public sealed class ArpaEventIdsTests
{
    private static readonly DateTimeOffset Dia = new(2026, 11, 24, 3, 0, 0, TimeSpan.Zero);

    private static JsonObject Titulo(string status, string recebido) =>
        new() { ["titulo_externo_id"] = "72", ["status"] = status, ["valor_recebido"] = recebido };

    [Fact]
    public void Releitura_sem_mudanca_gera_o_mesmo_event_id()
    {
        var a = ArpaEventIds.ForCollectedRow("inst", "financeiro", "72", Dia, Titulo("A", "0"), 0);
        var b = ArpaEventIds.ForCollectedRow("inst", "financeiro", "72", Dia, Titulo("A", "0"), 0);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Baixa_no_mesmo_dia_da_venda_gera_event_id_novo()
    {
        // occurred_at igual (data da venda, coluna date): so o payload mudou.
        var aberto = ArpaEventIds.ForCollectedRow("inst", "financeiro", "72", Dia, Titulo("A", "0"), 0);
        var quitado = ArpaEventIds.ForCollectedRow("inst", "financeiro", "72", Dia, Titulo("Q", "30"), 0);

        Assert.NotEqual(aberto, quitado);
    }

    [Fact]
    public void Geracao_de_resync_muda_o_event_id()
    {
        var normal = ArpaEventIds.ForCollectedRow("inst", "financeiro", "72", Dia, Titulo("A", "0"), 0);
        var resync = ArpaEventIds.ForCollectedRow("inst", "financeiro", "72", Dia, Titulo("A", "0"), 3);

        Assert.NotEqual(normal, resync);
    }

    [Fact]
    public void Chave_instante_entidade_e_instalacao_diferenciam()
    {
        var baseId = ArpaEventIds.ForCollectedRow("inst", "financeiro", "72", Dia, Titulo("A", "0"), 0);

        Assert.NotEqual(baseId, ArpaEventIds.ForCollectedRow("inst", "financeiro", "73", Dia, Titulo("A", "0"), 0));
        Assert.NotEqual(baseId, ArpaEventIds.ForCollectedRow("inst", "financeiro", "72", Dia.AddDays(1), Titulo("A", "0"), 0));
        Assert.NotEqual(baseId, ArpaEventIds.ForCollectedRow("inst", "venda", "72", Dia, Titulo("A", "0"), 0));
        Assert.NotEqual(baseId, ArpaEventIds.ForCollectedRow("outra", "financeiro", "72", Dia, Titulo("A", "0"), 0));
    }
}
