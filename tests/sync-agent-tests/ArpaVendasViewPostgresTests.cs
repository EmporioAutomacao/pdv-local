using System.Text.Json;
using Npgsql;
using SyncAgent.Provisioning;
using Xunit;

namespace SyncAgent.Tests;

/// <summary>
/// Script de views embutido (infra/arpa/sync-export-views.sql) contra um Postgres de
/// verdade: a venda do fallback (cabecalho_ordem_servico) tem que levar data E hora.
/// So roda com SYNCAGENT_TEST_PG (o usuario precisa poder criar banco); cada caso cria
/// e apaga um banco proprio, porque o script mexe no schema public.
/// </summary>
public sealed class ArpaVendasViewPostgresTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("SYNCAGENT_TEST_PG");

    private static string ViewsScript()
    {
        using var stream = typeof(ArpaDdlRunner).Assembly.GetManifestResourceStream("SyncAgent.Arpa.sync-export-views.sql")
            ?? throw new InvalidOperationException("script de views nao embutido");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void Script_embutido_esta_na_versao_que_junta_data_e_hora_da_venda()
    {
        var script = ViewsScript();
        Assert.Equal("2026.11.28.1", ArpaDdlRunner.ParseViewsVersion(script));
        Assert.Contains("sync_export._venda_ts_expr('v', v_t, v_date, v_time", script);
    }

    /// <summary>Cria o banco, aplica o DDL do caso e o script; devolve (entity_key -> data, occurred_at UTC, data_abertura).</summary>
    private static async Task<Dictionary<string, (DateTimeOffset Data, DateTime Occurred, DateTimeOffset? Abertura)>> VendasAsync(string ddl, List<string>? notices = null)
    {
        var db = "views_" + Guid.NewGuid().ToString("N")[..12];
        await using (var admin = new NpgsqlConnection(ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {db}", admin);
            await create.ExecuteNonQueryAsync();
        }
        try
        {
            var csb = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = db, Pooling = false };
            await using var conn = new NpgsqlConnection(csb.ConnectionString);
            if (notices is not null) conn.Notice += (_, e) => notices.Add(e.Notice.MessageText);
            await conn.OpenAsync();
            await using (var cmd = new NpgsqlCommand(ddl, conn)) await cmd.ExecuteNonQueryAsync();
            await using (var cmd = new NpgsqlCommand(ViewsScript(), conn)) await cmd.ExecuteNonQueryAsync();

            var result = new Dictionary<string, (DateTimeOffset, DateTime, DateTimeOffset?)>();
            await using var read = new NpgsqlCommand("SELECT entity_key, occurred_at_utc, payload_json FROM sync_export.vendas", conn);
            await using var reader = await read.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                using var payload = JsonDocument.Parse(reader.GetString(2));
                var data = DateTimeOffset.Parse(payload.RootElement.GetProperty("data").GetString()!);
                var ab = payload.RootElement.GetProperty("data_abertura"); // sempre presente (pode ser null)
                DateTimeOffset? abertura = ab.ValueKind == JsonValueKind.Null ? null : DateTimeOffset.Parse(ab.GetString()!);
                result[reader.GetString(0)] = (data, reader.GetDateTime(1).ToUniversalTime(), abertura);
            }
            return result;
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(ConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {db}", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    // 14:32:10 em Brasilia (UTC-3) = 17:32:10 UTC.
    private static readonly DateTimeOffset Esperado = new(2026, 11, 25, 14, 32, 10, TimeSpan.FromHours(-3));
    private static readonly DateTimeOffset MeiaNoite = new(2026, 11, 25, 0, 0, 0, TimeSpan.FromHours(-3));

    [Fact]
    public async Task Data_date_com_coluna_de_hora_time_leva_a_hora_ao_payload_e_ao_occurred_at()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;

        var vendas = await VendasAsync(
            "CREATE TABLE cabecalho_ordem_servico(ordem int, dataordem date, horaordem time, cliente int); " +
            "INSERT INTO cabecalho_ordem_servico VALUES (1, '2026-11-25', '14:32:10', NULL), (2, '2026-11-25', NULL, NULL);");

        Assert.Equal(Esperado, vendas["1"].Data);
        Assert.Equal(Esperado.UtcDateTime, vendas["1"].Occurred);
        Assert.Equal(MeiaNoite, vendas["2"].Data); // sem hora: data pura, como antes
        // sem colunas de fechamento: abertura e data sao a mesma
        Assert.Equal(Esperado, vendas["1"].Abertura);
        Assert.Equal(MeiaNoite, vendas["2"].Abertura);
    }

    [Fact]
    public async Task Hora_em_texto_invalido_nao_derruba_a_view()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;

        var vendas = await VendasAsync(
            "CREATE TABLE cabecalho_ordem_servico(ordem int, dataordem date, hora varchar(12), cliente int); " +
            "INSERT INTO cabecalho_ordem_servico VALUES (1, '2026-11-25', ' 14:32:10 ', NULL), (2, '2026-11-25', '25:99', NULL), (3, '2026-11-25', '', NULL);");

        Assert.Equal(Esperado, vendas["1"].Data);
        Assert.Equal(MeiaNoite, vendas["2"].Data);
        Assert.Equal(MeiaNoite, vendas["3"].Data);
    }

    [Fact]
    public async Task Hora_timestamp_estilo_delphi_e_coluna_time_com_outro_nome_sao_aproveitadas()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;

        var delphi = await VendasAsync(
            "CREATE TABLE cabecalho_ordem_servico(ordem int, dataordem date, hora timestamp, cliente int); " +
            "INSERT INTO cabecalho_ordem_servico VALUES (1, '2026-11-25', '1899-12-30 14:32:10', NULL);");
        Assert.Equal(Esperado, delphi["1"].Data);

        var porTipo = await VendasAsync(
            "CREATE TABLE cabecalho_ordem_servico(ordem int, dataordem date, momento time, cliente int); " +
            "INSERT INTO cabecalho_ordem_servico VALUES (1, '2026-11-25', '14:32:10', NULL);");
        Assert.Equal(Esperado, porTipo["1"].Data);
    }

    [Fact]
    public async Task Venda_fechada_usa_data_e_hora_do_fechamento_como_em_anapolis()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;

        // colunas reais de cabecalho_ordem_servico em Anapolis
        var notices = new List<string>();
        var vendas = await VendasAsync(
            "CREATE TABLE cabecalho_ordem_servico(ordem int, dataordem date, cliente int, data_fechamento date, " +
            "hora_abertura time, data_cancelamento date, hora_cancelamento time, hora_fechamento time); " +
            "INSERT INTO cabecalho_ordem_servico VALUES " +
            "(1, '2026-11-24', NULL, '2026-11-25', '09:00', NULL, NULL, '14:32:10'), " + // fechada no dia seguinte
            "(2, '2026-11-25', NULL, NULL, '14:32:10', NULL, NULL, NULL), " +            // aberta: abertura
            "(3, '2026-11-25', NULL, '2026-11-25', '14:32:10', NULL, NULL, NULL), " +    // fechamento sem hora: abertura
            "(4, '2026-11-25', NULL, '2026-11-25', '08:00', '2026-11-25', NULL, '09:00'), " + // cancelada: fora
            "(5, '2026-11-25', NULL, '2026-11-25', NULL, NULL, NULL, NULL), " +              // sem hora nenhuma
            "(6, '2026-11-25', NULL, '2026-11-25', '09:00', NULL, NULL, '14:32:10.589777');", notices); // microssegundos

        Assert.Equal(5, vendas.Count);
        Assert.Equal(Esperado, vendas["1"].Data);
        Assert.Equal(Esperado.UtcDateTime, vendas["1"].Occurred);
        // abertura vai separada (Venda.data_abertura no ERP), sempre dataordem + hora_abertura
        Assert.Equal(new DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.FromHours(-3)), vendas["1"].Abertura);
        Assert.Equal(Esperado, vendas["2"].Abertura);
        Assert.Equal(Esperado, vendas["3"].Abertura);
        Assert.Equal(MeiaNoite, vendas["5"].Abertura);
        Assert.Equal(new DateTimeOffset(2026, 11, 25, 9, 0, 0, TimeSpan.FromHours(-3)), vendas["6"].Abertura);
        Assert.Equal(Esperado, vendas["2"].Data);
        Assert.Equal(Esperado.UtcDateTime, vendas["2"].Occurred);
        Assert.Equal(Esperado, vendas["3"].Data);
        Assert.False(vendas.ContainsKey("4"));
        Assert.Equal(MeiaNoite, vendas["5"].Data);
        Assert.Equal(Esperado.AddTicks(5897770), vendas["6"].Data);
        Assert.Equal(Esperado.AddTicks(5897770).UtcDateTime, vendas["6"].Occurred);
        Assert.Contains(notices, n => n.Contains("data dataordem, hora hora_abertura, fechamento data_fechamento + hora_fechamento"));
    }

    [Fact]
    public async Task Hora_de_fechamento_em_texto_vazio_ou_invalido_cai_na_abertura()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;

        var vendas = await VendasAsync(
            "CREATE TABLE cabecalho_ordem_servico(ordem int, dataordem date, hora_abertura time, data_fechamento date, hora_fechamento varchar(12), cliente int); " +
            "INSERT INTO cabecalho_ordem_servico VALUES " +
            "(1, '2026-11-24', '09:00', '2026-11-25', ' 14:32:10 ', NULL), " +
            "(2, '2026-11-25', '14:32:10', '2026-11-26', '', NULL), " +
            "(3, '2026-11-25', '14:32:10', '2026-11-26', '99:99', NULL);");

        Assert.Equal(Esperado, vendas["1"].Data);
        Assert.Equal(new DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.FromHours(-3)), vendas["1"].Abertura);
        Assert.Equal(Esperado, vendas["2"].Data);
        Assert.Equal(Esperado, vendas["3"].Data);
        Assert.Equal(Esperado, vendas["3"].Abertura);
    }

    [Fact]
    public async Task Data_de_fechamento_sem_coluna_de_hora_de_fechamento_nao_e_usada()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;

        var notices = new List<string>();
        var vendas = await VendasAsync(
            "CREATE TABLE cabecalho_ordem_servico(ordem int, dataordem date, hora_abertura time, data_fechamento date, cliente int); " +
            "INSERT INTO cabecalho_ordem_servico VALUES (1, '2026-11-25', '14:32:10', '2026-11-26', NULL);", notices);

        Assert.Equal(Esperado, vendas["1"].Data);
        Assert.Contains(notices, n => n.Contains("fechamento nao usado"));
    }

    [Fact]
    public async Task Data_timestamp_que_ja_traz_hora_e_mantida()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;

        var vendas = await VendasAsync(
            "CREATE TABLE cabecalho_ordem_servico(ordem int, dataordem timestamp, horaordem time, cliente int); " +
            "INSERT INTO cabecalho_ordem_servico VALUES (1, '2026-11-25 00:00', '14:32:10', NULL), (2, '2026-11-25 14:32:10', '08:00', NULL);");

        Assert.Equal(Esperado, vendas["1"].Data);
        Assert.Equal(Esperado, vendas["2"].Data);
    }
}
