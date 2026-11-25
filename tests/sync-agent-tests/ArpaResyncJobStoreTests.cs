using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SyncAgent.Collectors;
using SyncAgent.Persistence;
using Xunit;

namespace SyncAgent.Tests;

/// <summary>
/// Trava do "Sincronizar tudo" (job por conexao em agent_state) contra um Postgres
/// de verdade. So roda com SYNCAGENT_TEST_PG (banco descartavel); cria o schema
/// sync_agent com o script de infra se ele nao existir.
/// </summary>
public sealed class ArpaResyncJobStoreTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("SYNCAGENT_TEST_PG");

    private static async Task<(NpgsqlDataSource Source, LocalSyncStore Store)> CriarAsync()
    {
        var source = NpgsqlDataSource.Create(ConnectionString!);
        var dir = AppContext.BaseDirectory;
        string? script = null;
        while (dir is not null && script is null)
        {
            var candidate = Path.Combine(dir, "infra", "postgres", "init", "02-create-sync-agent-schema.sql");
            script = File.Exists(candidate) ? candidate : null;
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(script);
        await using (var cmd = source.CreateCommand(await File.ReadAllTextAsync(script!)))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        return (source, new LocalSyncStore(source, NullLogger<LocalSyncStore>.Instance));
    }

    private static ArpaResyncJob Job(int generation, DateTimeOffset started) => new()
    {
        Generation = generation,
        Preset = "custom",
        FromUtc = started.AddDays(-10),
        ToUtc = started.AddDays(-5),
        EntityTypes = ["venda", "financeiro"],
        StartedAtUtc = started,
    };

    private static async Task InserirEventoAsync(NpgsqlDataSource source, string entityType, string status, DateTimeOffset captured)
    {
        await using var cmd = source.CreateCommand("""
            INSERT INTO sync_agent.outbox_events (
                event_id, source_instance_id, source_system, entity_type, entity_key, event_type,
                occurred_at_utc, captured_at_utc, schema_version, payload, payload_hash, status)
            VALUES (@id, 'inst-test', 'arpa', @type, @key, 'upsert', @captured, @captured, 'v1', '{}', 'h', @status)
            """);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("type", entityType);
        cmd.Parameters.AddWithValue("key", Guid.NewGuid().ToString("N"));
        cmd.Parameters.AddWithValue("captured", captured);
        cmd.Parameters.AddWithValue("status", status);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Segundo_Sincronizar_tudo_na_mesma_conexao_e_recusado()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            return;
        }

        var (source, store) = await CriarAsync();
        await using (source)
        {
            var conn = "test-" + Guid.NewGuid().ToString("N");
            var outra = "test-" + Guid.NewGuid().ToString("N");
            var agora = DateTimeOffset.UtcNow;
            try
            {
                Assert.Null(await store.TryStartArpaResyncJobAsync(conn, Job(1, agora), CancellationToken.None));

                var existente = await store.TryStartArpaResyncJobAsync(conn, Job(2, agora), CancellationToken.None);
                Assert.NotNull(existente);
                Assert.Equal(1, existente!.Generation);

                // Outra conexao nao e afetada.
                Assert.Null(await store.TryStartArpaResyncJobAsync(outra, Job(1, agora), CancellationToken.None));

                // Progresso grava so no mesmo job; cancelado, o coletor nao recria.
                var job = (await store.GetArpaResyncJobAsync(conn, CancellationToken.None))!;
                job.DoneEntities.Add("venda");
                Assert.True(await store.SaveArpaResyncJobAsync(conn, job, CancellationToken.None));
                Assert.Contains("venda", (await store.GetArpaResyncJobAsync(conn, CancellationToken.None))!.DoneEntities);

                Assert.False(await store.DeleteArpaResyncJobAsync(conn, CancellationToken.None, generation: 99));
                Assert.True(await store.DeleteArpaResyncJobAsync(conn, CancellationToken.None));
                Assert.False(await store.SaveArpaResyncJobAsync(conn, job, CancellationToken.None));
                Assert.Null(await store.GetArpaResyncJobAsync(conn, CancellationToken.None));

                // Liberado: novo reenvio aceito.
                Assert.Null(await store.TryStartArpaResyncJobAsync(conn, Job(3, agora), CancellationToken.None));
            }
            finally
            {
                await store.DeleteArpaResyncJobAsync(conn, CancellationToken.None);
                await store.DeleteArpaResyncJobAsync(outra, CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task Ultimo_resultado_guarda_o_que_nao_foi_reenviado()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            return;
        }

        var (source, store) = await CriarAsync();
        await using (source)
        {
            var conn = "test-" + Guid.NewGuid().ToString("N");
            try
            {
                var job = Job(5, DateTimeOffset.UtcNow);
                job.RegisterFailure("financeiro", "view ausente", DateTimeOffset.UtcNow.AddHours(-1));
                job.RegisterFailure("financeiro", "view ausente", DateTimeOffset.UtcNow.AddMinutes(-40));
                Assert.True(job.RegisterFailure("financeiro", "view ausente", DateTimeOffset.UtcNow));

                await store.SaveArpaResyncLastResultAsync(conn, job, DateTimeOffset.UtcNow, CancellationToken.None);
                var last = await store.GetArpaResyncLastResultAsync(conn, CancellationToken.None);
                Assert.Equal("view ausente", last!["failed_entities"]!["financeiro"]!.GetValue<string>());

                await store.DeleteArpaResyncLastResultAsync(conn, CancellationToken.None);
                Assert.Null(await store.GetArpaResyncLastResultAsync(conn, CancellationToken.None));
            }
            finally
            {
                await store.DeleteArpaResyncLastResultAsync(conn, CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task Geracao_da_coleta_incremental_fica_fixa_quando_o_reenvio_incrementa()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            return;
        }

        var (source, store) = await CriarAsync();
        await using (source)
        {
            var conn = "test-" + Guid.NewGuid().ToString("N");
            var nova = "test-" + Guid.NewGuid().ToString("N");
            try
            {
                // Instalacao que ja tinha feito "Sincronizar tudo" antes da 1.6.59: a coleta
                // incremental continua com a geracao que ja usava (nenhum event_id muda).
                Assert.Equal(1, await store.BumpArpaResyncGenerationAsync(conn, CancellationToken.None));
                Assert.Equal(2, await store.BumpArpaResyncGenerationAsync(conn, CancellationToken.None));
                Assert.Equal(2, await store.GetArpaIncrementalGenerationAsync(conn, CancellationToken.None));

                // Reenvios seguintes nao mexem nela.
                Assert.Equal(3, await store.BumpArpaResyncGenerationAsync(conn, CancellationToken.None));
                Assert.Equal(4, await store.BumpArpaResyncGenerationAsync(conn, CancellationToken.None));
                Assert.Equal(2, await store.GetArpaIncrementalGenerationAsync(conn, CancellationToken.None));
                Assert.Equal(2, await store.GetArpaIncrementalGenerationAsync(conn, CancellationToken.None));

                // Instalacao nova: 0, e o primeiro reenvio (1) sai com IDs diferentes.
                Assert.Equal(0, await store.GetArpaIncrementalGenerationAsync(nova, CancellationToken.None));
                Assert.Equal(1, await store.BumpArpaResyncGenerationAsync(nova, CancellationToken.None));
                Assert.Equal(0, await store.GetArpaIncrementalGenerationAsync(nova, CancellationToken.None));
            }
            finally
            {
                await using var cleanup = source.CreateCommand(
                    "DELETE FROM sync_agent.agent_state WHERE state_key LIKE 'collector.arpa.' || @a || '.%' OR state_key LIKE 'collector.arpa.' || @b || '.%'");
                cleanup.Parameters.AddWithValue("a", conn);
                cleanup.Parameters.AddWithValue("b", nova);
                await cleanup.ExecuteNonQueryAsync();
            }
        }
    }

    [Fact]
    public async Task Pendentes_do_reenvio_ignoram_dead_letter_evento_antigo_e_outra_entidade()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            return;
        }

        var (source, store) = await CriarAsync();
        await using (source)
        {
            // Inicio no futuro distante isola este teste de outros dados do banco.
            var inicio = new DateTimeOffset(2200, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(Random.Shared.Next(1_000_000));
            await InserirEventoAsync(source, "venda", "pending", inicio.AddMinutes(1));
            await InserirEventoAsync(source, "financeiro", "in_flight", inicio.AddMinutes(2));
            await InserirEventoAsync(source, "venda", "dead_letter", inicio.AddMinutes(3));
            await InserirEventoAsync(source, "venda", "accepted", inicio.AddMinutes(4));
            await InserirEventoAsync(source, "produto", "pending", inicio.AddMinutes(5));
            await InserirEventoAsync(source, "venda", "pending", inicio.AddMinutes(-1));

            var pendentes = await store.CountPendingArpaResyncEventsAsync(["venda", "financeiro"], inicio, CancellationToken.None);
            Assert.Equal(2, pendentes);

            await using var cleanup = source.CreateCommand("DELETE FROM sync_agent.outbox_events WHERE source_instance_id = 'inst-test'");
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
