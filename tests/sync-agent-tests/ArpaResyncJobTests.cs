using SyncAgent.Collectors;
using Xunit;

namespace SyncAgent.Tests;

public sealed class ArpaResyncJobTests
{
    private static readonly DateTimeOffset Agora = new(2026, 11, 24, 15, 0, 0, TimeSpan.Zero);

    // Brasilia, UTC-3 sem horario de verao: meia-noite local = 03:00Z.
    private static readonly TimeZoneInfo Brasilia = TimeZoneInfo.CreateCustomTimeZone("BRT-test", TimeSpan.FromHours(-3), "BRT", "BRT");

    [Fact]
    public void Data_especifica_vai_da_meia_noite_do_inicio_ao_fim_do_dia_final()
    {
        var (from, to) = ArpaResyncJob.ResolvePeriod(
            "custom", new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 10), Agora, Brasilia);

        Assert.Equal(new DateTimeOffset(2026, 11, 1, 3, 0, 0, TimeSpan.Zero), from);
        Assert.Equal(new DateTimeOffset(2026, 11, 11, 3, 0, 0, TimeSpan.Zero).AddTicks(-10), to);
    }

    [Fact]
    public void Data_final_hoje_e_limitada_a_agora()
    {
        var (_, to) = ArpaResyncJob.ResolvePeriod(
            "custom", new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 24), Agora, Brasilia);

        Assert.Equal(Agora, to);
    }

    [Fact]
    public void Mesmo_dia_no_inicio_e_no_fim_e_aceito()
    {
        var (from, to) = ArpaResyncJob.ResolvePeriod(
            "custom", new DateOnly(2026, 11, 5), new DateOnly(2026, 11, 5), Agora, Brasilia);

        Assert.True(from < to);
        Assert.Equal(TimeSpan.FromDays(1).Subtract(TimeSpan.FromTicks(10)), to - from);
    }

    [Theory]
    [InlineData("2026-11-10", "2026-11-01")] // inicio depois do fim
    [InlineData("2026-11-25", "2026-11-26")] // inicio no futuro
    [InlineData(null, "2026-11-10")]
    [InlineData("2026-11-01", null)]
    public void Data_especifica_invalida_e_recusada(string? since, string? until)
    {
        Assert.Throws<ArgumentException>(() => ArpaResyncJob.ResolvePeriod(
            "custom",
            since is null ? null : DateOnly.Parse(since),
            until is null ? null : DateOnly.Parse(until),
            Agora,
            Brasilia));
    }

    [Theory]
    [InlineData("all")]
    [InlineData(null)]
    [InlineData("qualquer")]
    public void Tudo_vai_do_inicio_ate_agora(string? preset)
    {
        var (from, to) = ArpaResyncJob.ResolvePeriod(preset, null, null, Agora, Brasilia);

        Assert.Equal(DateTimeOffset.UnixEpoch, from);
        Assert.Equal(Agora, to);
    }

    [Fact]
    public void Presets_relativos_comecam_na_meia_noite_local_e_terminam_agora()
    {
        // Agora = 24/11 12:00 em Brasilia. "Ontem e hoje" desde 23/11 00:00 local (03:00Z):
        // a venda com data 23/11 (coluna date = meia-noite local) entra.
        Assert.Equal(
            (new DateTimeOffset(2026, 11, 23, 3, 0, 0, TimeSpan.Zero), Agora),
            ArpaResyncJob.ResolvePeriod("1d", null, null, Agora, Brasilia));
        Assert.Equal(
            (new DateTimeOffset(2026, 8, 24, 3, 0, 0, TimeSpan.Zero), Agora),
            ArpaResyncJob.ResolvePeriod("3m", null, null, Agora, Brasilia));
    }

    [Fact]
    public void Ontem_e_hoje_logo_apos_a_meia_noite_ainda_pega_ontem()
    {
        // 25/11 01:35 local (04:35Z): o caso real da VM, em que a janela movel de 24h
        // comecava em 24/11 01:35 e perdia as vendas de 24/11.
        var madrugada = new DateTimeOffset(2026, 11, 25, 4, 35, 0, TimeSpan.Zero);
        var (from, _) = ArpaResyncJob.ResolvePeriod("1d", null, null, madrugada, Brasilia);
        Assert.Equal(new DateTimeOffset(2026, 11, 24, 3, 0, 0, TimeSpan.Zero), from);
    }

    [Fact]
    public void Estado_gravado_volta_igual()
    {
        var job = new ArpaResyncJob
        {
            Generation = 7,
            Preset = "custom",
            FromUtc = new DateTimeOffset(2026, 11, 1, 3, 0, 0, TimeSpan.Zero),
            ToUtc = new DateTimeOffset(2026, 11, 11, 3, 0, 0, TimeSpan.Zero).AddTicks(-10),
            EntityTypes = ["venda", "financeiro"],
            StartedAtUtc = Agora,
        };
        job.Cursors["venda"] = new ArpaCollectionCursor(new DateTimeOffset(2026, 11, 3, 3, 0, 0, TimeSpan.Zero), "loja:42");
        job.DoneEntities.Add("financeiro");
        job.DoneEntities.Add("cobranca");
        job.FailureCounts["venda"] = 2;
        job.FailedEntities["cobranca"] = "relation \"sync_export.cobranca\" does not exist";
        job.ReadingDoneAtUtc = Agora.AddMinutes(5);

        var back = ArpaResyncJob.FromState(job.ToState())!;

        Assert.Equal(7, back.Generation);
        Assert.Equal("custom", back.Preset);
        Assert.Equal(job.FromUtc, back.FromUtc);
        Assert.Equal(job.ToUtc, back.ToUtc);
        Assert.Equal(["venda", "financeiro"], back.EntityTypes);
        Assert.Equal(job.Cursors["venda"], back.CursorFor("venda"));
        Assert.Contains("financeiro", back.DoneEntities);
        Assert.Equal(2, back.FailureCounts["venda"]);
        Assert.Equal("relation \"sync_export.cobranca\" does not exist", back.FailedEntities["cobranca"]);
        Assert.Equal(job.ReadingDoneAtUtc, back.ReadingDoneAtUtc);
        Assert.True(back.ReadingDone);
        Assert.Equal("01/11/2026 a 10/11/2026", back.PeriodLabel(Brasilia));
    }

    [Fact]
    public void Cursor_inicial_e_o_inicio_do_periodo()
    {
        var job = new ArpaResyncJob
        {
            Generation = 1,
            Preset = "3m",
            FromUtc = Agora.AddMonths(-3),
            ToUtc = Agora,
            EntityTypes = ["produto"],
            StartedAtUtc = Agora,
        };

        Assert.Equal(new ArpaCollectionCursor(Agora.AddMonths(-3), string.Empty), job.CursorFor("produto"));
        Assert.False(job.ReadingDone);
    }

    private static ArpaResyncJob NovoJob() => new()
    {
        Generation = 1,
        Preset = "1d",
        FromUtc = Agora.AddDays(-1),
        ToUtc = Agora,
        EntityTypes = ["cobranca", "venda"],
        StartedAtUtc = Agora,
    };

    [Fact]
    public void Queda_curta_nao_faz_o_reenvio_desistir()
    {
        var job = NovoJob();

        // 10 falhas em 5 minutos (ciclos de 30s): rede fora do ar por pouco tempo.
        for (var i = 0; i < 10; i++)
        {
            Assert.False(job.RegisterFailure("venda", "timeout", Agora.AddSeconds(30 * i)));
        }

        Assert.DoesNotContain("venda", job.DoneEntities);
        Assert.Empty(job.FailedEntities);

        // Voltou: a sequencia zera.
        job.RegisterSuccess("venda");
        Assert.False(job.FailureCounts.ContainsKey("venda"));
        Assert.False(job.FirstFailureAtUtc.ContainsKey("venda"));
    }

    [Fact]
    public void Falha_persistente_desiste_apos_tentativas_e_tempo_minimo()
    {
        var job = NovoJob();
        Assert.False(job.RegisterFailure("cobranca", "view ausente", Agora));
        Assert.False(job.RegisterFailure("cobranca", "view ausente", Agora.AddMinutes(10)));
        Assert.False(job.RegisterFailure("cobranca", "view ausente", Agora.AddMinutes(29))); // 3 tentativas, mas < 30 min
        Assert.True(job.RegisterFailure("cobranca", "view ausente", Agora.AddMinutes(31)));

        Assert.Contains("cobranca", job.DoneEntities);
        Assert.Equal("view ausente", job.FailedEntities["cobranca"]);

        // Muito tempo mas poucas tentativas (agente parado entre elas) tambem nao desiste.
        var outro = NovoJob();
        Assert.False(outro.RegisterFailure("venda", "x", Agora));
        Assert.False(outro.RegisterFailure("venda", "x", Agora.AddHours(5)));
    }

    [Fact]
    public void Sequencia_de_falhas_sobrevive_ao_estado_gravado()
    {
        var job = NovoJob();
        job.RegisterFailure("cobranca", "view ausente", Agora);
        var back = ArpaResyncJob.FromState(job.ToState())!;

        Assert.Equal(1, back.FailureCounts["cobranca"]);
        Assert.Equal(Agora, back.FirstFailureAtUtc["cobranca"]);
    }

    [Fact]
    public void Estado_com_campo_de_tipo_errado_vira_null_sem_lancar()
    {
        var state = NovoJob().ToState();
        state["preset"] = 123;
        Assert.Null(ArpaResyncJob.FromState(state));

        var cursorRuim = NovoJob().ToState();
        cursorRuim["cursors"] = new System.Text.Json.Nodes.JsonObject { ["venda"] = new System.Text.Json.Nodes.JsonObject { ["value"] = 5 } };
        Assert.Null(ArpaResyncJob.FromState(cursorRuim));
    }

    [Fact]
    public void Meia_noite_inexistente_no_horario_de_verao_nao_lanca()
    {
        // Horario de verao comecando a meia-noite (como o Brasil ate 2019): 00:00 nao existe.
        var regra = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2000, 1, 1), new DateTime(2099, 12, 31), TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 11, 1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 2, 15));
        var comVerao = TimeZoneInfo.CreateCustomTimeZone("BRST-test", TimeSpan.FromHours(-3), "BRST-test", "BRT", "BRST", [regra]);

        var (from, to) = ArpaResyncJob.ResolvePeriod(
            "custom", new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 1), Agora, comVerao);

        // 01/11 comeca as 01:00 local (= 03:00Z com UTC-2 no verao).
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 3, 0, 0, TimeSpan.Zero), from);
        Assert.True(to > from);
    }

    [Fact]
    public void Estado_ilegivel_vira_null()
    {
        Assert.Null(ArpaResyncJob.FromState(null));
        Assert.Null(ArpaResyncJob.FromState(new System.Text.Json.Nodes.JsonObject { ["generation"] = "x" }));
    }
}
