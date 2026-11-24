using Npgsql;
using SyncAgent.Collectors;
using SyncAgent.Configuration;
using Xunit;

namespace SyncAgent.Tests;

/// <summary>
/// QueryTemplate + ArpaCollector.ReadPageAsync + ArpaCollectionPager contra um
/// Postgres de verdade. So roda com SYNCAGENT_TEST_PG (connection string de um banco
/// descartavel); sem a variavel o teste passa sem fazer nada. Cria e apaga as
/// proprias tabelas em sync_export.
/// </summary>
public sealed class ArpaCollectionPostgresTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("SYNCAGENT_TEST_PG");

    private static async Task<(NpgsqlDataSource Source, string Table)> CriarTabelaAsync(string collation)
    {
        var source = NpgsqlDataSource.Create(ConnectionString!);
        var table = "t_" + Guid.NewGuid().ToString("N")[..12];
        await using var cmd = source.CreateCommand(
            $"CREATE SCHEMA IF NOT EXISTS sync_export; " +
            $"CREATE TABLE sync_export.{table} (entity_key text COLLATE \"{collation}\", occurred_at_utc timestamptz, payload_json text, trace_id text)");
        await cmd.ExecuteNonQueryAsync();
        return (source, table);
    }

    private static async Task InserirAsync(NpgsqlDataSource source, string table, string key, DateTimeOffset occurred)
    {
        await using var cmd = source.CreateCommand($"INSERT INTO sync_export.{table} VALUES (@k, @t, '{{}}', NULL)");
        cmd.Parameters.AddWithValue("k", key);
        cmd.Parameters.AddWithValue("t", occurred);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task DroparAsync(NpgsqlDataSource source, string table)
    {
        await using var cmd = source.CreateCommand($"DROP TABLE IF EXISTS sync_export.{table}");
        await cmd.ExecuteNonQueryAsync();
        await source.DisposeAsync();
    }

    private static ArpaEntityCollectorOptions Entidade(string table) => new()
    {
        Name = "financeiro",
        EntityType = "financeiro",
        Query = ArpaStandardEntities.Build(false, false, false, financeiro: true)
            .Single(e => e.EntityType == "financeiro").Query
            .Replace("sync_export.financeiro", $"sync_export.{table}"),
    };

    private static async Task<string?> CollationQueIgnoraPontuacaoAsync()
    {
        var source = NpgsqlDataSource.Create(ConnectionString!);
        await using var cmd = source.CreateCommand(
            "SELECT collname FROM pg_collation WHERE collname IN ('en_US.utf8','pt_BR.utf8','en-US-x-icu','und-x-icu') " +
            "AND collencoding IN (-1, (SELECT encoding FROM pg_database WHERE datname = current_database())) ORDER BY collname LIMIT 1");
        var name = (string?)await cmd.ExecuteScalarAsync();
        await source.DisposeAsync();
        return name;
    }

    [Fact]
    public async Task Pagina_com_empate_e_collation_do_banco_le_tudo_sem_repetir()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            return;
        }

        var collation = await CollationQueIgnoraPontuacaoAsync() ?? "default";
        var (source, table) = await CriarTabelaAsync(collation);
        try
        {
            var agora = DateTimeOffset.UtcNow;
            var dia = new DateTimeOffset(agora.UtcDateTime.Date, TimeSpan.Zero).AddHours(3); // meia-noite UTC-3
            if (dia > agora)
            {
                dia = dia.AddDays(-1);
            }

            // Chaves cuja ordem muda entre "C" e uma collation linguistica.
            string[] chaves = ["pag-1", "pag1x", "pag_2", "PAG-0", "a-b", "ab", "receber:999", "receber:1000", "Zeta", "zeta", "10", "9"];
            foreach (var k in chaves)
            {
                await InserirAsync(source, table, k, dia);
            }

            await InserirAsync(source, table, "futuro", agora.AddYears(2));

            var entidade = Entidade(table);
            var lidas = new List<string>();
            var result = await ArpaCollectionPager.RunAsync(
                ArpaCollectionCursor.Initial,
                agora,
                2,
                (after, to, toKey, limit, ct) => ArpaCollector.ReadPageAsync(source, entidade, after, to, toKey, limit, ct),
                row => { lidas.Add(row.EntityKey); return Task.FromResult(true); },
                CancellationToken.None);

            Assert.True(result.CaughtUp);
            Assert.Equal(chaves.OrderBy(k => k, StringComparer.Ordinal), lidas.Take(chaves.Length));
            Assert.DoesNotContain("futuro", lidas);
            Assert.False(result.Position.IsInFuture(agora));

            // Linha nova com a mesma data e chave menor: so a releitura pega.
            await InserirAsync(source, table, "AAA-novo", dia);
            var novas = new List<string>();
            var depois = await ArpaCollectionPager.RunAsync(
                result.Position,
                agora,
                2,
                (after, to, toKey, limit, ct) => ArpaCollector.ReadPageAsync(source, entidade, after, to, toKey, limit, ct),
                row => { novas.Add(row.EntityKey); return Task.FromResult(true); },
                CancellationToken.None);

            Assert.Contains("AAA-novo", novas);
            Assert.Equal(result.Position, depois.Position);
        }
        finally
        {
            await DroparAsync(source, table);
        }
    }

    [Fact]
    public async Task Query_antiga_do_appsettings_continua_rodando()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            return;
        }

        var (source, table) = await CriarTabelaAsync("default");
        try
        {
            var agora = DateTimeOffset.UtcNow;
            await InserirAsync(source, table, "1", agora.AddDays(-5));
            await InserirAsync(source, table, "2", agora.AddDays(90));
            var legado = new ArpaEntityCollectorOptions
            {
                Name = "financeiro",
                EntityType = "financeiro",
                Query = $"SELECT entity_key, occurred_at_utc, payload_json, trace_id FROM sync_export.{table} WHERE occurred_at_utc > @watermark_utc ORDER BY occurred_at_utc, entity_key LIMIT @limit",
            };

            var lidas = new List<string>();
            var result = await ArpaCollectionPager.RunAsync(
                ArpaCollectionCursor.Initial,
                agora,
                10,
                (after, to, toKey, limit, ct) => ArpaCollector.ReadPageAsync(source, legado, after, to, toKey, limit, ct),
                row => { lidas.Add(row.EntityKey); return Task.FromResult(true); },
                CancellationToken.None);

            Assert.Equal(["1"], lidas);
            Assert.False(result.Position.IsInFuture(agora));
        }
        finally
        {
            await DroparAsync(source, table);
        }
    }
}
