using Npgsql;
using System.Text.Json.Nodes;
using SyncAgent.Configuration;
using SyncAgent.Contracts;
using SyncAgent.Normalizers;
using SyncAgent.Persistence;
using SyncAgent.Provisioning;
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

    public ArpaCollector(
        ILogger<ArpaCollector> logger,
        EffectiveSyncAgentConfigurationProvider effectiveConfigProvider,
        EffectiveArpaCollectorConfigurationProvider effectiveCollectorConfigProvider,
        LocalSyncStore localStore,
        ArpaPayloadNormalizerRegistry normalizers)
    {
        _logger = logger;
        _effectiveConfigProvider = effectiveConfigProvider;
        _effectiveCollectorConfigProvider = effectiveCollectorConfigProvider;
        _localStore = localStore;
        _normalizers = normalizers;
    }

    public async Task<ArpaCollectorRunSummary> CollectAsync(CancellationToken cancellationToken)
    {
        var options = await _effectiveCollectorConfigProvider.GetCurrentAsync(cancellationToken);
        if (!options.Enabled)
        {
            return ArpaCollectorRunSummary.Disabled;
        }

        var totalCollected = 0;
        var totalInserted = 0;

        await using var dataSource = NpgsqlDataSource.Create(options.ConnectionString);

        foreach (var entity in options.Entities)
        {
            var entitySummary = await CollectEntityAsync(dataSource, entity, options.BatchSize, cancellationToken);
            totalCollected += entitySummary.Collected;
            totalInserted += entitySummary.Inserted;
        }

        return new ArpaCollectorRunSummary(true, totalCollected, totalInserted);
    }

    private async Task<ArpaEntityCollectorRunSummary> CollectEntityAsync(
        NpgsqlDataSource dataSource,
        ArpaEntityCollectorOptions entity,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var syncOptions = _effectiveConfigProvider.GetCurrent();
        var watermarkKey = $"collector.arpa.{entity.Name}.watermark";
        var watermark = await _localStore.GetDateTimeOffsetStateAsync(
            watermarkKey,
            InitialWatermark,
            cancellationToken);

        var collected = 0;
        var inserted = 0;
        var nextWatermark = watermark;

        await using var command = dataSource.CreateCommand(entity.Query);
        command.Parameters.AddWithValue("watermark_utc", watermark);
        command.Parameters.AddWithValue("limit", batchSize);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = ReadCollectorRow(reader);
            nextWatermark = row.OccurredAtUtc > nextWatermark ? row.OccurredAtUtc : nextWatermark;

            var payload = JsonNode.Parse(row.PayloadJson)?.AsObject()
                ?? throw new InvalidOperationException($"Collector '{entity.Name}' returned invalid payload_json.");
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
            "Arpa collector entity completed. Entity={EntityName}; Type={EntityType}; Collected={Collected}; Inserted={Inserted}; Watermark={Watermark}",
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
