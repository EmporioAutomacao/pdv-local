using Npgsql;
using System.Text.Json.Nodes;
using SyncAgent.Configuration;
using SyncAgent.Contracts;
using SyncAgent.Normalizers;
using SyncAgent.Persistence;
using SyncAgent.Provisioning;
using SyncAgent.Runtime;
using SyncAgent.Utilities;

namespace SyncAgent.Collectors;

public sealed class ArpaCollector
{
    private static readonly DateTimeOffset InitialWatermark = DateTimeOffset.UnixEpoch;

    private readonly ILogger<ArpaCollector> _logger;
    private readonly EffectiveSyncAgentConfigurationProvider _effectiveConfigProvider;
    private readonly EffectiveArpaCollectorConfigurationProvider _effectiveCollectorConfigProvider;
    private readonly LocalSyncStore _localStore;
    private readonly ArpaPayloadNormalizerRegistry _normalizers;
    private readonly ArpaSyncRunLog _runLog;

    public ArpaCollector(
        ILogger<ArpaCollector> logger,
        EffectiveSyncAgentConfigurationProvider effectiveConfigProvider,
        EffectiveArpaCollectorConfigurationProvider effectiveCollectorConfigProvider,
        LocalSyncStore localStore,
        ArpaPayloadNormalizerRegistry normalizers,
        ArpaSyncRunLog runLog)
    {
        _logger = logger;
        _effectiveConfigProvider = effectiveConfigProvider;
        _effectiveCollectorConfigProvider = effectiveCollectorConfigProvider;
        _localStore = localStore;
        _normalizers = normalizers;
        _runLog = runLog;
    }

    private static string EntityLabel(string entityType) => entityType switch
    {
        "produto" => "Produtos",
        "cliente" => "Clientes",
        "estoque" => "Estoque",
        "venda" => "Vendas",
        "financeiro" => "Financeiro",
        _ => entityType,
    };

    private static string Summarize(Exception ex) => ex switch
    {
        NpgsqlException { InnerException: { } inner } => inner.Message,
        Npgsql.PostgresException pg => $"{pg.SqlState}: {pg.MessageText}",
        _ => ex.Message,
    };

    public async Task<ArpaCollectorRunSummary> CollectAsync(CancellationToken cancellationToken)
    {
        var connections = await _effectiveCollectorConfigProvider.GetCurrentAsync(cancellationToken);
        if (connections.Count == 0)
        {
            _runLog.Add("info", "Coletor Arpa desativado ou sem conexoes - nada a coletar.");
            return ArpaCollectorRunSummary.Disabled;
        }

        var totalCollected = 0;
        var totalInserted = 0;
        var failedEntities = 0;
        var totalEntities = 0;

        foreach (var connection in connections)
        {
            _runLog.Add("info", $"Conexao \"{connection.Nome}\": lendo do Arpa...");

            NpgsqlDataSource dataSource;
            try
            {
                dataSource = NpgsqlDataSource.Create(connection.ConnectionString);
            }
            catch (Exception ex)
            {
                failedEntities += connection.Entities.Count;
                totalEntities += connection.Entities.Count;
                _runLog.Add("warn", $"Conexao \"{connection.Nome}\": nao foi possivel abrir - {ex.Message}");
                _logger.LogWarning(ex, "Arpa collector: conexao '{Nome}' invalida e pulada nesta execucao.", connection.Nome);
                continue;
            }

            await using (dataSource)
            {
                foreach (var entity in connection.Entities)
                {
                    totalEntities++;
                    try
                    {
                        var entitySummary = await CollectEntityAsync(dataSource, connection, entity, cancellationToken);
                        totalCollected += entitySummary.Collected;
                        totalInserted += entitySummary.Inserted;
                        _runLog.Add(
                            "info",
                            $"  {EntityLabel(entity.EntityType)}: {entitySummary.Collected} lido(s), {entitySummary.Inserted} novo(s)/alterado(s).");
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // Uma entidade Arpa quebrada (view sync_export ausente, permissao
                        // negada, schema divergente) nao deve derrubar o ciclo inteiro -
                        // heartbeat, envio de vendas PDV e dispatcher precisam continuar.
                        failedEntities++;
                        _runLog.Add("warn", $"  {EntityLabel(entity.EntityType)}: falhou - {Summarize(ex)}");
                        _logger.LogWarning(
                            ex,
                            "Arpa collector: conexao '{Nome}' entidade '{EntityName}' falhou e foi pulada nesta execucao.",
                            connection.Nome,
                            entity.Name);
                    }
                }
            }
        }

        if (failedEntities > 0)
        {
            _logger.LogWarning(
                "Arpa collector: {Failed} de {Total} entidades falharam. Verifique as views sync_export e as permissoes do usuario read-only no Arpa.",
                failedEntities,
                totalEntities);
        }

        return new ArpaCollectorRunSummary(true, totalCollected, totalInserted);
    }

    private async Task<ArpaEntityCollectorRunSummary> CollectEntityAsync(
        NpgsqlDataSource dataSource,
        EffectiveArpaCollectorConnection connection,
        ArpaEntityCollectorOptions entity,
        CancellationToken cancellationToken)
    {
        var syncOptions = _effectiveConfigProvider.GetCurrent();
        var watermarkKey = $"collector.arpa.{connection.Id}.{entity.EntityType}.watermark";
        var watermark = await _localStore.GetDateTimeOffsetStateAsync(
            watermarkKey,
            InitialWatermark,
            cancellationToken);

        var collected = 0;
        var inserted = 0;
        var nextWatermark = watermark;

        await using var command = dataSource.CreateCommand(entity.Query);
        command.Parameters.AddWithValue("watermark_utc", watermark);
        command.Parameters.AddWithValue("limit", connection.BatchSize);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = ReadCollectorRow(reader);
            nextWatermark = row.OccurredAtUtc > nextWatermark ? row.OccurredAtUtc : nextWatermark;

            var payload = JsonNode.Parse(row.PayloadJson)?.AsObject()
                ?? throw new InvalidOperationException($"Collector '{entity.Name}' returned invalid payload_json.");

            // Estoque e venda sao por Loja: injeta o loja_codigo da conexao
            // (nome da Loja no ERP) quando a view sync_export nao o traz.
            if ((entity.EntityType == "estoque" || entity.EntityType == "venda")
                && !string.IsNullOrWhiteSpace(connection.LojaCodigo)
                && payload["loja_codigo"] is null)
            {
                payload["loja_codigo"] = connection.LojaCodigo;
            }

            var normalizedPayload = _normalizers.Normalize(entity.EntityType, row.EntityKey, payload);

            var eventId = DeterministicGuid.Create(
                "sync-agent:v1",
                "arpa",
                syncOptions.InstanceId,
                entity.EntityType,
                row.EntityKey,
                row.OccurredAtUtc.ToString("O"));

            var draft = new OutboxEventDraft(
                eventId,
                syncOptions.InstanceId,
                "arpa",
                entity.EntityType,
                row.EntityKey,
                "upsert",
                row.OccurredAtUtc,
                DateTimeOffset.UtcNow,
                SyncContractValues.CurrentSchemaVersion,
                normalizedPayload,
                row.TraceId);

            var result = await _localStore.EnqueueOutboxEventAsync(draft, cancellationToken);
            collected++;

            if (result.Inserted)
            {
                inserted++;
            }
        }

        if (nextWatermark > watermark)
        {
            await _localStore.SetDateTimeOffsetStateAsync(watermarkKey, nextWatermark, cancellationToken);
        }

        _logger.LogInformation(
            "Arpa collector entity completed. Connection={Connection}; Entity={EntityName}; Type={EntityType}; Collected={Collected}; Inserted={Inserted}; Watermark={Watermark}",
            connection.Nome,
            entity.Name,
            entity.EntityType,
            collected,
            inserted,
            nextWatermark);

        return new ArpaEntityCollectorRunSummary(collected, inserted);
    }

    private static ArpaCollectorRow ReadCollectorRow(NpgsqlDataReader reader)
    {
        var entityKey = reader.GetString(reader.GetOrdinal("entity_key"));
        var occurredAtUtc = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("occurred_at_utc"));
        var payloadJson = reader.GetString(reader.GetOrdinal("payload_json"));
        var traceIdOrdinal = TryGetOrdinal(reader, "trace_id");
        var traceId = traceIdOrdinal is null || reader.IsDBNull(traceIdOrdinal.Value)
            ? null
            : reader.GetString(traceIdOrdinal.Value);

        return new ArpaCollectorRow(entityKey, occurredAtUtc, payloadJson, traceId);
    }

    private static int? TryGetOrdinal(NpgsqlDataReader reader, string columnName)
    {
        try
        {
            return reader.GetOrdinal(columnName);
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
    }
}

public sealed record ArpaCollectorRunSummary(
    bool Enabled,
    int Collected,
    int Inserted)
{
    public static ArpaCollectorRunSummary Disabled { get; } = new(false, 0, 0);
}

public sealed record ArpaEntityCollectorRunSummary(
    int Collected,
    int Inserted);

internal sealed record ArpaCollectorRow(
    string EntityKey,
    DateTimeOffset OccurredAtUtc,
    string PayloadJson,
    string? TraceId);
