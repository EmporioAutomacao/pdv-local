using System.Text.Json;
using Npgsql;
using SyncAgent.Provisioning;

namespace SyncAgent.Tests;

public sealed class ArpaComprasFreteViewPostgresTests
{
    [Fact]
    public async Task View_exporta_transportadora_e_frete_quando_existem()
    {
        var connectionString = Environment.GetEnvironmentVariable("SYNCAGENT_TEST_PG");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        var db = "compras_frete_" + Guid.NewGuid().ToString("N")[..12];
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
            await using (var setup = new NpgsqlCommand("CREATE TABLE cabecalho_nota_compra(codigo integer, nota integer, transportadora integer, frete_por_conta text); CREATE TABLE transportadoras(codigo integer, nome text, cnpj text); INSERT INTO transportadoras VALUES (7, 'Frete Exemplo', '12345678000199'); INSERT INTO cabecalho_nota_compra VALUES (1, 10, 7, 'FOB'), (2, 11, NULL, 'outro'), (3, 12, NULL, 'remetente');", conn)) await setup.ExecuteNonQueryAsync();
            using var stream = typeof(ArpaDdlRunner).Assembly.GetManifestResourceStream("SyncAgent.Arpa.sync-export-views.sql")!;
            using var reader = new StreamReader(stream);
            await using (var script = new NpgsqlCommand(await reader.ReadToEndAsync(), conn)) await script.ExecuteNonQueryAsync();
            await using var query = new NpgsqlCommand("SELECT payload_json FROM sync_export.compras ORDER BY entity_key", conn);
            await using var rows = await query.ExecuteReaderAsync();
            Assert.True(await rows.ReadAsync());
            using var first = JsonDocument.Parse(rows.GetString(0));
            Assert.Equal("12345678000199", first.RootElement.GetProperty("transportadora_documento").GetString());
            Assert.Equal("Frete Exemplo", first.RootElement.GetProperty("transportadora_nome").GetString());
            Assert.Equal("fob", first.RootElement.GetProperty("frete_por_conta").GetString());
            Assert.True(await rows.ReadAsync());
            using var second = JsonDocument.Parse(rows.GetString(0));
            Assert.Equal(JsonValueKind.Null, second.RootElement.GetProperty("frete_por_conta").ValueKind);
            Assert.True(await rows.ReadAsync());
            using var third = JsonDocument.Parse(rows.GetString(0));
            Assert.Equal("cif", third.RootElement.GetProperty("frete_por_conta").GetString());
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
}
