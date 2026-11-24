using System.Text.Json.Nodes;
using SyncAgent.Collectors;
using Xunit;

namespace SyncAgent.Tests;

public sealed class ArpaCollectionCursorTests
{
    private static readonly DateTimeOffset Agora = new(2026, 11, 24, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Sem_estado_comeca_do_inicio()
    {
        Assert.Equal(ArpaCollectionCursor.Initial, ArpaCollectionCursor.FromState(null));
    }

    [Fact]
    public void Le_estado_antigo_so_com_value()
    {
        var cursor = ArpaCollectionCursor.FromState(new JsonObject { ["value"] = "2026-11-20T03:00:00.0000000+00:00" });

        Assert.Equal(new DateTimeOffset(2026, 11, 20, 3, 0, 0, TimeSpan.Zero), cursor.OccurredAtUtc);
        Assert.Equal(string.Empty, cursor.EntityKey);
    }

    [Fact]
    public void Estado_ida_e_volta_preserva_instante_e_chave()
    {
        var original = new ArpaCollectionCursor(new DateTimeOffset(2026, 11, 20, 3, 0, 0, 123, TimeSpan.Zero).AddTicks(4560), "9876");

        var lido = ArpaCollectionCursor.FromState(original.ToState());

        Assert.Equal(original, lido);
        Assert.Equal(original.ToState()["value"]!.GetValue<string>(), lido.ToState()["value"]!.GetValue<string>());
    }

    [Fact]
    public void Estado_invalido_volta_ao_inicio()
    {
        Assert.Equal(ArpaCollectionCursor.Initial, ArpaCollectionCursor.FromState(new JsonObject { ["value"] = "lixo" }));
    }

    [Fact]
    public void Marcador_no_futuro_e_detectado()
    {
        // Watermark deixado pela view antiga: vencimento de parcela em 2028.
        var cursor = new ArpaCollectionCursor(new DateTimeOffset(2028, 3, 10, 3, 0, 0, TimeSpan.Zero), "555");

        Assert.True(cursor.IsInFuture(Agora));
        Assert.False(new ArpaCollectionCursor(Agora, "1").IsInFuture(Agora));
    }

    [Fact]
    public void Releitura_cobre_os_ultimos_dois_dias_quando_a_posicao_e_recente()
    {
        var hojeMeiaNoite = new ArpaCollectionCursor(new DateTimeOffset(2026, 11, 24, 3, 0, 0, TimeSpan.Zero), "800");

        var inicio = hojeMeiaNoite.OverlapStart(Agora);

        Assert.NotNull(inicio);
        Assert.Equal(Agora - ArpaCollectionCursor.Overlap, inicio!.OccurredAtUtc);
        Assert.Equal(string.Empty, inicio.EntityKey);
    }

    [Fact]
    public void Sem_releitura_quando_a_posicao_e_mais_antiga_que_a_janela()
    {
        // Carga inicial com timestamp fixo (2000-01-01): nao rele a tabela toda a cada ciclo.
        var fixo = new ArpaCollectionCursor(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero), "20000");

        Assert.Null(fixo.OverlapStart(Agora));
    }

    [Fact]
    public void Query_pagina_por_instante_e_chave_com_limite_superior()
    {
        var financeiro = Assert.Single(
            ArpaStandardEntities.Build(false, false, false, financeiro: true),
            e => e.EntityType == "financeiro");

        Assert.Contains("(occurred_at_utc, entity_key COLLATE \"C\") > (@from_utc, @from_key::text COLLATE \"C\")", financeiro.Query);
        Assert.Contains("occurred_at_utc < @to_utc", financeiro.Query);
        Assert.Contains("ORDER BY occurred_at_utc, entity_key COLLATE \"C\"", financeiro.Query);
        Assert.DoesNotContain("@watermark_utc", financeiro.Query);
    }
}
