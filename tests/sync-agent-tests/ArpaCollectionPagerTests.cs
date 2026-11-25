using SyncAgent.Collectors;
using Xunit;

namespace SyncAgent.Tests;

/// <summary>
/// Laco de coleta (alcance + releitura) contra uma view em memoria com a mesma
/// semantica do QueryTemplate de ArpaStandardEntities, em varios ciclos seguidos,
/// com um outbox que ignora event_id repetido (como o real).
/// </summary>
public sealed class ArpaCollectionPagerTests
{
    private static readonly DateTimeOffset Agora = new(2026, 11, 24, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset HojeMeiaNoite = new(2026, 11, 24, 3, 0, 0, TimeSpan.Zero); // date do Arpa em UTC-3

    private sealed class FakeView
    {
        public List<ArpaCollectorRow> Rows { get; } = [];
        public int Reads { get; private set; }

        public void Add(string key, DateTimeOffset occurred) =>
            Rows.Add(new ArpaCollectorRow(key, occurred, "{}", null));

        // WHERE (ts, key) > (from) AND (ts < to OR (ts = to AND (toKey IS NULL OR key <= toKey)))
        // ORDER BY ts, key COLLATE "C" LIMIT n
        public Task<IReadOnlyList<ArpaCollectorRow>> Read(
            ArpaCollectionCursor after, DateTimeOffset toUtc, string? toKey, int limit, CancellationToken _)
        {
            Reads++;
            IReadOnlyList<ArpaCollectorRow> page = Rows
                .Where(r => Compare(r, after.OccurredAtUtc, after.EntityKey) > 0)
                .Where(r => r.OccurredAtUtc < toUtc
                    || (r.OccurredAtUtc == toUtc && (toKey is null || string.CompareOrdinal(r.EntityKey, toKey) <= 0)))
                .OrderBy(r => r.OccurredAtUtc).ThenBy(r => r.EntityKey, StringComparer.Ordinal)
                .Take(limit)
                .ToList();
            return Task.FromResult(page);
        }

        // Query antiga do appsettings: so "occurred_at_utc > @watermark_utc", sem limite superior.
        public Task<IReadOnlyList<ArpaCollectorRow>> ReadLegacy(
            ArpaCollectionCursor after, DateTimeOffset toUtc, string? toKey, int limit, CancellationToken _)
        {
            Reads++;
            IReadOnlyList<ArpaCollectorRow> page = Rows
                .Where(r => r.OccurredAtUtc > after.OccurredAtUtc)
                .OrderBy(r => r.OccurredAtUtc).ThenBy(r => r.EntityKey, StringComparer.Ordinal)
                .Take(limit)
                .ToList();
            return Task.FromResult(page);
        }

        private static int Compare(ArpaCollectorRow r, DateTimeOffset ts, string key)
        {
            var c = r.OccurredAtUtc.CompareTo(ts);
            return c != 0 ? c : string.CompareOrdinal(r.EntityKey, key);
        }
    }

    private sealed class FakeOutbox
    {
        public HashSet<(string, DateTimeOffset)> Ids { get; } = [];
        public int Attempts { get; private set; }

        // event_id = DeterministicGuid(entity_key, occurred_at): ON CONFLICT DO NOTHING
        public Task<bool> Enqueue(ArpaCollectorRow row)
        {
            Attempts++;
            return Task.FromResult(Ids.Add((row.EntityKey, row.OccurredAtUtc)));
        }

        public bool Has(string key) => Ids.Any(i => i.Item1 == key);
    }

    private static async Task<ArpaCollectionCursor> Ciclo(
        FakeView view, FakeOutbox outbox, ArpaCollectionCursor saved, int batch, DateTimeOffset? now = null, bool legacy = false)
    {
        var result = await ArpaCollectionPager.RunAsync(
            saved, now ?? Agora, batch, legacy ? view.ReadLegacy : view.Read, outbox.Enqueue, CancellationToken.None);
        Assert.False(result.Position.IsInFuture(now ?? Agora), "posicao gravada nunca pode ficar no futuro");
        return result.Position;
    }

    [Fact]
    public async Task Empate_de_data_na_virada_do_lote_nao_perde_linha()
    {
        var view = new FakeView();
        for (var i = 1; i <= 7; i++)
        {
            view.Add($"10{i}", HojeMeiaNoite.AddDays(-5));
        }

        var outbox = new FakeOutbox();
        var pos = await Ciclo(view, outbox, ArpaCollectionCursor.Initial, batch: 2);

        Assert.Equal(7, outbox.Ids.Count);
        Assert.Equal("107", pos.EntityKey);
    }

    [Fact]
    public async Task Linha_no_futuro_nao_e_lida_nem_move_a_posicao()
    {
        var view = new FakeView();
        view.Add("1", HojeMeiaNoite.AddDays(-10));
        view.Add("2", new DateTimeOffset(2028, 3, 10, 3, 0, 0, TimeSpan.Zero)); // vencimento 2028 (view antiga)

        var outbox = new FakeOutbox();
        var pos = await Ciclo(view, outbox, ArpaCollectionCursor.Initial, batch: 50);

        Assert.True(outbox.Has("1"));
        Assert.False(outbox.Has("2"));
        Assert.Equal("1", pos.EntityKey);

        // Quando a data chega, a linha e lida.
        await Ciclo(view, outbox, pos, batch: 50, now: new DateTimeOffset(2028, 3, 11, 0, 0, 0, TimeSpan.Zero));
        Assert.True(outbox.Has("2"));
    }

    [Fact]
    public async Task Titulo_novo_com_a_mesma_data_e_chave_menor_e_pego_pela_releitura()
    {
        var view = new FakeView();
        view.Add("receber:999", HojeMeiaNoite);
        var outbox = new FakeOutbox();
        var pos = await Ciclo(view, outbox, ArpaCollectionCursor.Initial, batch: 50);
        Assert.Equal("receber:999", pos.EntityKey);

        // Venda nova no mesmo dia: chave "receber:1000" ordena antes de "receber:999".
        view.Add("receber:1000", HojeMeiaNoite);
        await Ciclo(view, outbox, pos, batch: 50);

        Assert.True(outbox.Has("receber:1000"));
    }

    [Fact]
    public async Task Releitura_nao_reenfileira_o_que_ja_foi_enviado()
    {
        var view = new FakeView();
        for (var i = 0; i < 5; i++)
        {
            view.Add($"k{i}", HojeMeiaNoite.AddHours(-i));
        }

        var outbox = new FakeOutbox();
        var pos = await Ciclo(view, outbox, ArpaCollectionCursor.Initial, batch: 50);
        var depois = await ArpaCollectionPager.RunAsync(pos, Agora, 50, view.Read, outbox.Enqueue, CancellationToken.None);

        Assert.Equal(0, depois.Collected);
        Assert.Equal(5, depois.Reread);
        Assert.Equal(0, depois.Inserted);
        Assert.False(depois.PositionChanged);
    }

    [Fact]
    public async Task Marcador_no_futuro_e_descartado_e_o_que_ficou_para_tras_chega()
    {
        var view = new FakeView();
        view.Add("antigo", HojeMeiaNoite.AddDays(-40));
        view.Add("venda-nova", HojeMeiaNoite.AddDays(-1));
        view.Add("baixa-hoje", HojeMeiaNoite);
        var outbox = new FakeOutbox();
        outbox.Ids.Add(("antigo", HojeMeiaNoite.AddDays(-40))); // ja enviado antes do salto

        var travado = new ArpaCollectionCursor(new DateTimeOffset(2028, 3, 10, 3, 0, 0, TimeSpan.Zero), "555");
        var result = await ArpaCollectionPager.RunAsync(travado, Agora, 50, view.Read, outbox.Enqueue, CancellationToken.None);

        Assert.True(result.DiscardedFutureState);
        Assert.True(result.PositionChanged);
        Assert.Equal("baixa-hoje", result.Position.EntityKey);
        Assert.True(outbox.Has("venda-nova"));
        Assert.True(outbox.Has("baixa-hoje"));
        Assert.Equal(2, result.Inserted); // "antigo" nao foi reenviado
    }

    [Fact]
    public async Task Carga_grande_continua_nos_proximos_ciclos_sem_perder_nem_duplicar()
    {
        var view = new FakeView();
        var fixo = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero); // timestamp fixo da carga inicial
        var total = (ArpaCollectionCursor.MaxPagesPerCycle * 3 * 10) + 7;
        for (var i = 0; i < total; i++)
        {
            view.Add($"{i:D6}", fixo);
        }

        var outbox = new FakeOutbox();
        var pos = ArpaCollectionCursor.Initial;
        var ciclos = 0;
        ArpaCollectionPagerResult result;
        do
        {
            result = await ArpaCollectionPager.RunAsync(pos, Agora, 10, view.Read, outbox.Enqueue, CancellationToken.None);
            pos = result.Position;
            ciclos++;
        }
        while (!result.CaughtUp && ciclos < 100);

        Assert.True(result.CaughtUp);
        Assert.Equal(4, ciclos);
        Assert.Equal(total, outbox.Ids.Count);
        Assert.Equal(total, outbox.Attempts); // posicao antiga (2000): sem releitura
    }

    [Fact]
    public async Task Sem_alcancar_o_fim_nao_rele_a_janela()
    {
        var view = new FakeView();
        for (var i = 0; i < (ArpaCollectionCursor.MaxPagesPerCycle * 2) + 1; i++)
        {
            view.Add($"{i:D4}", HojeMeiaNoite);
        }

        var outbox = new FakeOutbox();
        var result = await ArpaCollectionPager.RunAsync(ArpaCollectionCursor.Initial, Agora, 2, view.Read, outbox.Enqueue, CancellationToken.None);

        Assert.False(result.CaughtUp);
        Assert.Equal(0, result.Reread);
        Assert.Equal(ArpaCollectionCursor.MaxPagesPerCycle * 2, result.Collected);
    }

    [Fact]
    public async Task Pagina_exata_do_tamanho_do_lote_termina_na_pagina_vazia()
    {
        var view = new FakeView();
        view.Add("a", HojeMeiaNoite.AddDays(-9));
        view.Add("b", HojeMeiaNoite.AddDays(-9));
        var outbox = new FakeOutbox();

        var result = await ArpaCollectionPager.RunAsync(ArpaCollectionCursor.Initial, Agora, 2, view.Read, outbox.Enqueue, CancellationToken.None);

        Assert.True(result.CaughtUp);
        Assert.Equal(2, view.Reads);
        Assert.Equal("b", result.Position.EntityKey);
    }

    [Fact]
    public async Task Query_antiga_sem_limite_superior_nunca_grava_posicao_no_futuro()
    {
        var view = new FakeView();
        view.Add("1", HojeMeiaNoite.AddDays(-3));
        view.Add("2", Agora.AddDays(30));
        view.Add("3", Agora.AddDays(400));
        var outbox = new FakeOutbox();

        var pos = await Ciclo(view, outbox, ArpaCollectionCursor.Initial, batch: 1, legacy: true);
        pos = await Ciclo(view, outbox, pos, batch: 1, legacy: true);

        Assert.Equal("1", pos.EntityKey);
        Assert.Equal(["1"], outbox.Ids.Select(i => i.Item1).ToArray());
    }

    [Fact]
    public async Task Baixa_no_mesmo_dia_com_o_mesmo_occurred_at_chega_pela_releitura()
    {
        var view = new FakeView();
        view.Rows.Add(new ArpaCollectorRow("72", HojeMeiaNoite, "{\"status\":\"A\",\"valor_recebido\":0}", null));
        var enviados = new HashSet<Guid>();
        Task<bool> Enqueue(ArpaCollectorRow r) => Task.FromResult(enviados.Add(ArpaEventIds.ForCollectedRow(
            "inst", "financeiro", r.EntityKey, r.OccurredAtUtc, (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(r.PayloadJson)!, 0)));

        var primeiro = await ArpaCollectionPager.RunAsync(ArpaCollectionCursor.Initial, Agora, 50, view.Read, Enqueue, CancellationToken.None);
        Assert.Equal(1, primeiro.Inserted);

        // Quitado no Arpa; a data (venda e pagamento no mesmo dia) nao muda.
        view.Rows[0] = new ArpaCollectorRow("72", HojeMeiaNoite, "{\"status\":\"Q\",\"valor_recebido\":30}", null);
        var segundo = await ArpaCollectionPager.RunAsync(primeiro.Position, Agora.AddMinutes(1), 50, view.Read, Enqueue, CancellationToken.None);
        Assert.Equal(1, segundo.Inserted);

        // Sem nova mudanca, a releitura nao envia de novo.
        var terceiro = await ArpaCollectionPager.RunAsync(segundo.Position, Agora.AddMinutes(2), 50, view.Read, Enqueue, CancellationToken.None);
        Assert.Equal(0, terceiro.Inserted);
        Assert.Equal(2, enviados.Count);
    }

    [Fact]
    public async Task Reenvio_por_periodo_le_so_entre_inicio_e_fim()
    {
        var view = new FakeView();
        var inicio = HojeMeiaNoite.AddDays(-10);
        var fim = HojeMeiaNoite.AddDays(-5).AddDays(1).AddTicks(-10); // dia -5 inteiro
        view.Add("antes", inicio.AddDays(-1));
        view.Add("no-inicio", inicio);
        view.Add("meio", HojeMeiaNoite.AddDays(-7));
        view.Add("no-fim", HojeMeiaNoite.AddDays(-5).AddHours(20));
        view.Add("depois", HojeMeiaNoite.AddDays(-4));
        var outbox = new FakeOutbox();

        var result = await ArpaCollectionPager.RunRangeAsync(
            new ArpaCollectionCursor(inicio, string.Empty), fim, 50, view.Read, outbox.Enqueue, CancellationToken.None);

        Assert.True(result.Finished);
        Assert.Equal(["no-inicio", "meio", "no-fim"], outbox.Ids.Select(i => i.Item1).ToArray());
        Assert.Equal("no-fim", result.Position.EntityKey);
    }

    [Fact]
    public async Task Reenvio_por_periodo_continua_entre_ciclos_sem_perder_nem_repetir()
    {
        var view = new FakeView();
        var fixo = HojeMeiaNoite.AddDays(-3);
        var total = (ArpaCollectionCursor.MaxPagesPerCycle * 2 * 5) + 3;
        for (var i = 0; i < total; i++)
        {
            view.Add($"{i:D5}", fixo);
        }

        var outbox = new FakeOutbox();
        var pos = new ArpaCollectionCursor(HojeMeiaNoite.AddDays(-30), string.Empty);
        var ciclos = 0;
        ArpaRangePagerResult result;
        do
        {
            result = await ArpaCollectionPager.RunRangeAsync(pos, Agora, 5, view.Read, outbox.Enqueue, CancellationToken.None);
            pos = result.Position;
            ciclos++;
        }
        while (!result.Finished && ciclos < 100);

        Assert.Equal(3, ciclos);
        Assert.Equal(total, outbox.Ids.Count);
        Assert.Equal(total, outbox.Attempts);
    }

    [Fact]
    public async Task Reenvio_com_query_antiga_para_no_fim_do_periodo()
    {
        var view = new FakeView();
        view.Add("1", HojeMeiaNoite.AddDays(-20));
        view.Add("2", HojeMeiaNoite.AddDays(-2));
        var outbox = new FakeOutbox();

        var result = await ArpaCollectionPager.RunRangeAsync(
            ArpaCollectionCursor.Initial, HojeMeiaNoite.AddDays(-10), 1, view.ReadLegacy, outbox.Enqueue, CancellationToken.None);

        Assert.True(result.Finished);
        Assert.Equal(["1"], outbox.Ids.Select(i => i.Item1).ToArray());
    }

    [Fact]
    public async Task Reenvio_com_inicio_depois_do_fim_nao_le_nada()
    {
        var view = new FakeView();
        view.Add("1", HojeMeiaNoite);
        var outbox = new FakeOutbox();

        var result = await ArpaCollectionPager.RunRangeAsync(
            new ArpaCollectionCursor(Agora, "zzz"), HojeMeiaNoite, 10, view.Read, outbox.Enqueue, CancellationToken.None);

        Assert.True(result.Finished);
        Assert.Equal(0, view.Reads);
        Assert.Empty(outbox.Ids);
    }

    [Fact]
    public async Task Cenario_do_incidente_vendas_novas_chegam_com_financeiro_por_varios_dias()
    {
        // Carga inicial com parcela de crediario vencendo em 2028; depois, todo dia
        // uma venda nova (titulo com a data da venda) e uma baixa.
        var view = new FakeView();
        view.Add("receber:100", HojeMeiaNoite.AddDays(-60));
        view.Add("receber:101", HojeMeiaNoite.AddDays(-60));
        var outbox = new FakeOutbox();
        var pos = ArpaCollectionCursor.Initial;

        for (var dia = 0; dia < 5; dia++)
        {
            var meiaNoite = HojeMeiaNoite.AddDays(dia);
            view.Add($"receber:{200 + dia}", meiaNoite);
            view.Add($"pag-{900 + dia}", meiaNoite);
            for (var hora = 9; hora <= 18; hora += 3)
            {
                pos = await Ciclo(view, outbox, pos, batch: 3, now: meiaNoite.AddHours(hora - 3 + 3));
            }

            // titulo lancado no fim do dia, mesma data, chave que ordena antes
            view.Add($"receber:{1000 + dia}", meiaNoite);
            pos = await Ciclo(view, outbox, pos, batch: 3, now: meiaNoite.AddHours(20));
        }

        Assert.Equal(view.Rows.Count, outbox.Ids.Count);
    }
}
