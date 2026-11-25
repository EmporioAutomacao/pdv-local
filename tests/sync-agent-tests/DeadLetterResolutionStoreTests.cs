using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SyncAgent.Persistence;
using SyncAgent.Provisioning;
using Xunit;

namespace SyncAgent.Tests;

/// <summary>
/// Encerramento de dead-letter superado (1.6.59) contra um Postgres de verdade.
/// So roda com SYNCAGENT_TEST_PG (usuario com CREATE DATABASE). Cada teste usa um
/// banco proprio, criado e apagado aqui: o teste simula um schema antigo e nao
/// pode interferir nos outros testes que rodam em paralelo.
/// </summary>
public sealed class DeadLetterResolutionStoreTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("SYNCAGENT_TEST_PG");

    private sealed class TestDb : IAsyncDisposable
    {
        private readonly string _name;

        private TestDb(string name, NpgsqlDataSource source)
        {
            _name = name;
            Source = source;
            Store = new LocalSyncStore(source, NullLogger<LocalSyncStore>.Instance);
        }

        public NpgsqlDataSource Source { get; }

        public LocalSyncStore Store { get; }

        public static async Task<TestDb> CreateAsync()
        {
            var name = "dl_" + Guid.NewGuid().ToString("N")[..12];
            await using (var admin = NpgsqlDataSource.Create(ConnectionString!))
            await using (var cmd = admin.CreateCommand($"CREATE DATABASE {name}"))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            var builder = new NpgsqlConnectionStringBuilder(ConnectionString!) { Database = name };
            var source = NpgsqlDataSource.Create(builder.ConnectionString);
            await using (var cmd = source.CreateCommand(await File.ReadAllTextAsync(FindSchemaScript())))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            return new TestDb(name, source);
        }

        public async Task ExecAsync(string sql)
        {
            await using var cmd = Source.CreateCommand(sql);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<object?> ScalarAsync(string sql)
        {
            await using var cmd = Source.CreateCommand(sql);
            return await cmd.ExecuteScalarAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Source.DisposeAsync();
            NpgsqlConnection.ClearAllPools();
            await using var admin = NpgsqlDataSource.Create(ConnectionString!);
            await using var cmd = admin.CreateCommand($"DROP DATABASE IF EXISTS {_name} WITH (FORCE)");
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static string FindSchemaScript()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "infra", "postgres", "init", "02-create-sync-agent-schema.sql");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new FileNotFoundException("02-create-sync-agent-schema.sql nao encontrado.");
    }

    /// <summary>Base criada antes da 1.6.59: CHECK sem 'resolved' e sem as colunas de auditoria.</summary>
    private static Task SimularSchemaAntigoAsync(TestDb db) => db.ExecAsync("""
        ALTER TABLE sync_agent.outbox_events DROP CONSTRAINT outbox_events_status_check;
        ALTER TABLE sync_agent.outbox_events ADD CONSTRAINT outbox_events_status_check
            CHECK (status IN ('pending', 'in_flight', 'accepted', 'rejected', 'dead_letter'));
        ALTER TABLE sync_agent.dead_letter_events DROP COLUMN resolved_at_utc;
        ALTER TABLE sync_agent.dead_letter_events DROP COLUMN resolved_by_event_id;
        """);

    private static Task RodarRotinaAsync(TestDb db) =>
        db.ExecAsync(LocalDbMaintenanceRunner.Routines.Single(r => r.Key == LocalDbMaintenanceRunner.EnableDeadLetterResolutionKey).Sql);

    private static async Task<Guid> EventoAsync(
        TestDb db, string status, string entityType, string key, DateTimeOffset captured,
        string? loja = null, string source = "arpa", string reason = "motivo de teste")
    {
        var id = Guid.NewGuid();
        await using (var cmd = db.Source.CreateCommand("""
            INSERT INTO sync_agent.outbox_events (
                event_id, source_instance_id, source_system, entity_type, entity_key, event_type,
                occurred_at_utc, captured_at_utc, schema_version, payload, payload_hash, status, last_error)
            VALUES (@id, 'inst-test', @source, @type, @key, 'upsert', @captured, @captured, 'v1', @payload::jsonb, 'h', @status,
                    CASE WHEN @status = 'dead_letter' THEN @reason ELSE NULL END)
            """))
        {
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("source", source);
            cmd.Parameters.AddWithValue("type", entityType);
            cmd.Parameters.AddWithValue("key", key);
            cmd.Parameters.AddWithValue("captured", captured);
            cmd.Parameters.AddWithValue("payload", loja is null ? "{}" : $$"""{"loja_codigo":"{{loja}}"}""");
            cmd.Parameters.AddWithValue("status", status);
            cmd.Parameters.AddWithValue("reason", reason);
            await cmd.ExecuteNonQueryAsync();
        }

        if (status == "dead_letter")
        {
            await using var dl = db.Source.CreateCommand("""
                INSERT INTO sync_agent.dead_letter_events (
                    event_id, source_instance_id, source_system, entity_type, entity_key, event_type,
                    schema_version, payload, payload_hash, reason)
                SELECT event_id, source_instance_id, source_system, entity_type, entity_key, event_type,
                       schema_version, payload, payload_hash, last_error
                FROM sync_agent.outbox_events WHERE event_id = @id
                """);
            dl.Parameters.AddWithValue("id", id);
            await dl.ExecuteNonQueryAsync();
        }

        return id;
    }

    private static async Task<string> StatusAsync(TestDb db, Guid id) =>
        (string)(await db.ScalarAsync($"SELECT status FROM sync_agent.outbox_events WHERE event_id = '{id}'"))!;

    [Fact]
    public void Rotina_de_schema_esta_catalogada_com_todos_os_status()
    {
        var rotina = LocalDbMaintenanceRunner.Routines.Single(r => r.Key == LocalDbMaintenanceRunner.EnableDeadLetterResolutionKey);
        foreach (var status in new[] { "pending", "in_flight", "accepted", "rejected", "dead_letter", "resolved" })
        {
            Assert.Contains($"'{status}'", rotina.Sql);
        }

        Assert.Contains("resolved_at_utc", rotina.Sql);
        Assert.Contains("resolved_by_event_id", rotina.Sql);
    }

    [Fact]
    public async Task Base_antiga_so_mostra_previa_e_a_rotina_habilita()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            return;
        }

        await using var db = await TestDb.CreateAsync();
        Assert.True(await db.Store.SupportsDeadLetterResolutionAsync(CancellationToken.None)); // init SQL novo ja nasce pronto

        await SimularSchemaAntigoAsync(db);
        Assert.False(await db.Store.SupportsDeadLetterResolutionAsync(CancellationToken.None));

        var agora = DateTimeOffset.UtcNow;
        var dead = await EventoAsync(db, "dead_letter", "produto", "P1", agora.AddHours(-2));
        await EventoAsync(db, "accepted", "produto", "P1", agora.AddHours(-1));

        var resumo = await db.Store.GetDeadLetterSummaryAsync(CancellationToken.None);
        var linha = Assert.Single(resumo.Rows);
        Assert.Equal((1L, 1L, 0L), (linha.Open, linha.Superseded, linha.Resolved));

        var result = await db.Store.ResolveSupersededDeadLettersAsync(CancellationToken.None);
        Assert.False(result.Supported);
        Assert.Equal("dead_letter", await StatusAsync(db, dead));

        await RodarRotinaAsync(db);
        await RodarRotinaAsync(db); // idempotente
        Assert.True(await db.Store.SupportsDeadLetterResolutionAsync(CancellationToken.None));
        Assert.Equal(1, (await db.Store.ResolveSupersededDeadLettersAsync(CancellationToken.None)).Resolved);
        Assert.Equal("resolved", await StatusAsync(db, dead));
    }

    [Fact]
    public async Task So_encerra_o_que_tem_envio_posterior_aceito_da_mesma_entidade_e_loja()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            return;
        }

        await using var db = await TestDb.CreateAsync();
        var t0 = DateTimeOffset.UtcNow.AddHours(-10);

        // Encerram:
        var produto = await EventoAsync(db, "dead_letter", "produto", "A", t0, reason: "ERP returned HTTP 404.");
        await EventoAsync(db, "accepted", "produto", "A", t0.AddHours(1));
        var superadorMaisRecente = await EventoAsync(db, "accepted", "produto", "A", t0.AddHours(2));
        var vendaMesmaLoja = await EventoAsync(db, "dead_letter", "venda", "11", t0, loja: "ANP");
        await EventoAsync(db, "accepted", "venda", "11", t0.AddHours(1), loja: "ANP");

        // Ficam abertos:
        var vendaOutraLoja = await EventoAsync(db, "dead_letter", "venda", "10", t0, loja: "ANP");
        await EventoAsync(db, "accepted", "venda", "10", t0.AddHours(1), loja: "BSB");
        var aceitoAntes = await EventoAsync(db, "dead_letter", "cliente", "B", t0.AddHours(5));
        await EventoAsync(db, "accepted", "cliente", "B", t0.AddHours(4));
        var outroTipo = await EventoAsync(db, "dead_letter", "estoque", "C", t0);
        await EventoAsync(db, "accepted", "produto", "C", t0.AddHours(1));
        var pdv = await EventoAsync(db, "dead_letter", "venda", "D", t0, source: "pdv_local");
        await EventoAsync(db, "accepted", "venda", "D", t0.AddHours(1), source: "pdv_local");
        var semAceito = await EventoAsync(db, "dead_letter", "catalogo", "vendedor:7", t0, reason: "catalogo payload requires tipo");

        var antes = await db.Store.GetDeadLetterSummaryAsync(CancellationToken.None);
        Assert.Equal(7, antes.Open);
        Assert.Equal(2, antes.Superseded);
        Assert.Equal(antes.Open, antes.Rows.Sum(r => r.Open));

        // Reconciliacao enviada ao ERP antes do encerramento.
        var janelaInicio = t0.AddHours(-1);
        var janelaFim = DateTimeOffset.UtcNow.AddMinutes(1);
        var recAntes = await db.Store.BuildReconciliationSummaryPayloadAsync(Guid.NewGuid(), "inst-test", janelaInicio, janelaFim, CancellationToken.None);

        var result = await db.Store.ResolveSupersededDeadLettersAsync(CancellationToken.None);
        Assert.True(result.Supported);
        Assert.Equal(2, result.Resolved);
        Assert.Equal(1, result.ByEntity["produto"]);
        Assert.Equal(1, result.ByEntity["venda"]);

        Assert.Equal("resolved", await StatusAsync(db, produto));
        Assert.Equal("resolved", await StatusAsync(db, vendaMesmaLoja));
        foreach (var aberto in new[] { vendaOutraLoja, aceitoAntes, outroTipo, pdv, semAceito })
        {
            Assert.Equal("dead_letter", await StatusAsync(db, aberto));
        }

        // Auditoria: quem superou (o aceito mais recente) e quando; nada apagado.
        Assert.Equal(superadorMaisRecente, (Guid)(await db.ScalarAsync($"SELECT resolved_by_event_id FROM sync_agent.dead_letter_events WHERE event_id = '{produto}'"))!);
        Assert.NotNull(await db.ScalarAsync($"SELECT resolved_at_utc FROM sync_agent.dead_letter_events WHERE event_id = '{produto}'"));
        Assert.Equal(DBNull.Value, await db.ScalarAsync($"SELECT resolved_at_utc FROM sync_agent.dead_letter_events WHERE event_id = '{semAceito}'"));
        Assert.Equal(7L, await db.ScalarAsync("SELECT count(*) FROM sync_agent.dead_letter_events"));
        Assert.Equal("ERP returned HTTP 404.", await db.ScalarAsync($"SELECT last_error FROM sync_agent.outbox_events WHERE event_id = '{produto}'"));

        // Contador do painel (so abertos) cai; o resumo separa abertos e encerrados.
        Assert.Equal(5L, await db.ScalarAsync("SELECT count(*) FROM sync_agent.outbox_events WHERE status = 'dead_letter'"));
        var depois = await db.Store.GetDeadLetterSummaryAsync(CancellationToken.None);
        Assert.Equal((5L, 0L, 2L), (depois.Open, depois.Superseded, depois.Resolved));
        Assert.Equal(1, depois.Rows.Single(r => r.EntityType == "produto").Resolved);
        Assert.Equal("ERP returned HTTP 404.", depois.Rows.Single(r => r.EntityType == "produto").Reason);

        // O numero enviado ao ERP na reconciliacao nao muda.
        var recDepois = await db.Store.BuildReconciliationSummaryPayloadAsync(Guid.NewGuid(), "inst-test", janelaInicio, janelaFim, CancellationToken.None);
        Assert.Equal(
            recAntes.Entities.ToDictionary(e => e.EntityType, e => (e.Captured, e.Accepted, e.DeadLetter)),
            recDepois.Entities.ToDictionary(e => e.EntityType, e => (e.Captured, e.Accepted, e.DeadLetter)));
        Assert.Equal(1, recDepois.Entities.Single(e => e.EntityType == "produto").DeadLetter);

        // Idempotente.
        Assert.Equal(0, (await db.Store.ResolveSupersededDeadLettersAsync(CancellationToken.None)).Resolved);
    }

    [Fact]
    public async Task Encerramentos_simultaneos_nao_encerram_duas_vezes()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            return;
        }

        await using var db = await TestDb.CreateAsync();
        var t0 = DateTimeOffset.UtcNow.AddHours(-5);
        for (var i = 0; i < 40; i++)
        {
            await EventoAsync(db, "dead_letter", "produto", $"K{i}", t0);
            await EventoAsync(db, "accepted", "produto", $"K{i}", t0.AddHours(1));
        }

        var resultados = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => db.Store.ResolveSupersededDeadLettersAsync(CancellationToken.None)));

        Assert.Equal(40, resultados.Sum(r => r.Resolved));
        Assert.Equal(40L, await db.ScalarAsync("SELECT count(*) FROM sync_agent.dead_letter_events WHERE resolved_at_utc IS NOT NULL"));
    }

    [Fact]
    public async Task Resumo_limita_as_linhas_mas_os_totais_contam_tudo()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            return;
        }

        await using var db = await TestDb.CreateAsync();
        var t0 = DateTimeOffset.UtcNow.AddHours(-3);
        var grupos = DeadLetterSummary.MaxRows + 20;
        for (var i = 0; i < grupos; i++)
        {
            // Motivo com id no texto: um grupo por evento.
            await EventoAsync(db, "dead_letter", "produto", $"X{i}", t0, reason: $"produto {i} nao encontrado");
        }

        await EventoAsync(db, "accepted", "produto", "X0", t0.AddHours(1));

        var resumo = await db.Store.GetDeadLetterSummaryAsync(CancellationToken.None);
        Assert.Equal(DeadLetterSummary.MaxRows, resumo.Rows.Count);
        Assert.Equal(grupos, resumo.TotalGroups);
        Assert.Equal(grupos, resumo.Open);
        Assert.Equal(1, resumo.Superseded);
    }

    [Fact]
    public async Task Automatico_comeca_desligado_e_guarda_a_escolha()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            return;
        }

        await using var db = await TestDb.CreateAsync();
        Assert.False(await db.Store.GetDeadLetterAutoResolveAsync(CancellationToken.None));
        await db.Store.SetDeadLetterAutoResolveAsync(true, CancellationToken.None);
        Assert.True(await db.Store.GetDeadLetterAutoResolveAsync(CancellationToken.None));
        await db.Store.SetDeadLetterAutoResolveAsync(false, CancellationToken.None);
        Assert.False(await db.Store.GetDeadLetterAutoResolveAsync(CancellationToken.None));
    }
}
