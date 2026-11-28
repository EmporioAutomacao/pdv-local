using Npgsql;
using SyncAgent.Provisioning;

namespace SyncAgent.Tests;

/// <summary>
/// occurred_at_utc das views de financeiro e compras inclui quando a linha foi
/// gravada no Arpa. Em Anapolis a nota de compra, o titulo a pagar e a baixa sao
/// lancados com data retroativa (emissao/pagamento dias antes do lancamento); so
/// pela data de negocio a linha ficava atras da posicao do coletor e nunca era lida.
/// Tabelas no formato real de Anapolis (conferido em 2026-11-28).
/// </summary>
public sealed class ArpaGravacaoOccurredViewPostgresTests
{
    private const string Setup = """
        CREATE TABLE parcelas(ordem integer, parcela integer, valor numeric, vencimento date, codigo integer,
            usuario integer, data date, hora time);
        CREATE TABLE parcelas_quitadas(parcela integer, usuario integer, valor_pago numeric, data_pgto date,
            data date, hora time, codigo_id integer);
        CREATE TABLE apagar(codigo integer, vencimento date, valor numeric, tipo_documento varchar, documento varchar,
            data_emissao date, data_lancamento date, hora_lancamento time, historico integer, fornecedor integer);
        CREATE TABLE apagar_quitadas(parcela integer, usuario integer, valor_pago numeric, data_pgto date, data date, hora time);
        CREATE TABLE apagar_nota_compra(apagar integer, nota integer);
        CREATE TABLE cabecalho_nota_compra(codigo integer, nota integer, data date, fornecedor integer,
            data_processamento date, hora_processamento time);

        -- receber: parcela criada ha 10 dias; baixa paga ha 9 dias e gravada ontem 12:00
        INSERT INTO parcelas VALUES (500, 1, 100, current_date + 20, 1, 1, current_date - 10, '08:00');
        INSERT INTO parcelas_quitadas VALUES (1, 1, 100, current_date - 9, current_date - 1, '12:00', 1);
        -- receber sem baixa: so a criacao da parcela
        INSERT INTO parcelas VALUES (501, 1, 50, current_date + 30, 2, 1, current_date - 3, '15:30');

        -- nota de compra emitida ha 20 dias e lancada ontem 09:00
        INSERT INTO cabecalho_nota_compra VALUES (335, 677862, current_date - 20, 10, current_date - 1, '09:00');
        -- pagar da nota: emitido ha 20 dias, lancado ontem 10:00, em aberto
        INSERT INTO apagar VALUES (568, current_date + 25, 465, 'NF', '677862/001', current_date - 20, current_date - 1, '10:00', 1, 10);
        INSERT INTO apagar_nota_compra VALUES (568, 335);
        -- pagar lancado ha 5 dias e pago ha 15, baixa gravada ontem 11:00
        INSERT INTO apagar VALUES (569, current_date - 15, 80, 'NF', 'X', current_date - 30, current_date - 5, '14:00', 1, 10);
        INSERT INTO apagar_quitadas VALUES (569, 1, 80, current_date - 15, current_date - 1, '11:00');
        -- lancamento no futuro nao vale: fica a emissao
        INSERT INTO apagar VALUES (570, current_date + 40, 10, 'NF', 'Y', current_date - 7, current_date + 5, '10:00', 1, 10);
        """;

    [Fact]
    public async Task Occurred_usa_o_lancamento_e_a_gravacao_da_baixa_quando_sao_depois_da_data_de_negocio()
    {
        var connectionString = Environment.GetEnvironmentVariable("SYNCAGENT_TEST_PG");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        var db = "arpa_gravacao_" + Guid.NewGuid().ToString("N")[..12];
        await using (var admin = new NpgsqlConnection(connectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {db}", admin);
            await create.ExecuteNonQueryAsync();
        }
        try
        {
            var cs = new NpgsqlConnectionStringBuilder(connectionString) { Database = db, Pooling = false };
            await using var conn = new NpgsqlConnection(cs.ConnectionString);
            await conn.OpenAsync();
            await using (var setup = new NpgsqlCommand(Setup, conn)) await setup.ExecuteNonQueryAsync();
            using var stream = typeof(ArpaDdlRunner).Assembly.GetManifestResourceStream("SyncAgent.Arpa.sync-export-views.sql")!;
            using var reader = new StreamReader(stream);
            await using (var script = new NpgsqlCommand(await reader.ReadToEndAsync(), conn)) await script.ExecuteNonQueryAsync();

            // horario local do Arpa (Etc/GMT+3), mesmo fuso das views
            Assert.True(await Igual(conn, "sync_export.financeiro", "1", 1, "12:00"), "baixa a receber gravada depois do pagamento");
            Assert.True(await Igual(conn, "sync_export.financeiro", "2", 3, "15:30"), "parcela a receber sem baixa");
            Assert.True(await Igual(conn, "sync_export.financeiro", "pag-568", 1, "10:00"), "titulo a pagar lancado depois da emissao");
            Assert.True(await Igual(conn, "sync_export.financeiro", "pag-569", 1, "11:00"), "baixa a pagar gravada depois do pagamento");
            Assert.True(await Igual(conn, "sync_export.financeiro", "pag-570", 7, "00:00"), "lancamento no futuro e ignorado");
            Assert.True(await Igual(conn, "sync_export.compras", "677862", 1, "09:00"), "nota lancada depois da emissao");

            await using var vinculo = new NpgsqlCommand(
                "SELECT payload_json::jsonb->>'compra_externa_id' FROM sync_export.financeiro WHERE entity_key = 'pag-568'", conn);
            Assert.Equal("677862", (string?)await vinculo.ExecuteScalarAsync());

            await using var futuro = new NpgsqlCommand(
                "SELECT (SELECT count(*) FROM sync_export.financeiro WHERE occurred_at_utc > now()) + (SELECT count(*) FROM sync_export.compras WHERE occurred_at_utc > now())", conn);
            Assert.Equal(0L, (long)(await futuro.ExecuteScalarAsync())!);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(connectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {db}", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task<bool> Igual(NpgsqlConnection conn, string view, string key, int diasAtras, string hora)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT occurred_at_utc = ((current_date - @d) + @h::time) AT TIME ZONE 'Etc/GMT+3' FROM {view} WHERE entity_key = @k", conn);
        cmd.Parameters.AddWithValue("d", diasAtras);
        cmd.Parameters.AddWithValue("h", hora);
        cmd.Parameters.AddWithValue("k", key);
        return await cmd.ExecuteScalarAsync() is true;
    }
}
