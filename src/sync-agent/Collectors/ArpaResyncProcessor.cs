using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Npgsql;
using SyncAgent.Contracts;
using SyncAgent.Normalizers;
using SyncAgent.Persistence;
using SyncAgent.Provisioning;
using SyncAgent.Runtime;
using SyncAgent.Utilities;

namespace SyncAgent.Collectors;

/// <summary>
/// Processa os <c>pending_resyncs</c> que o ERP entrega no heartbeat: para cada
/// pedido, re-le a linha na <c>sync_export.&lt;view&gt;</c> do Arpa (ignorando o
/// watermark), re-enfileira o evento com um <c>event_id</c> novo (para o ERP
/// re-aplicar) e devolve o resultado para o <c>resyncs:ack</c>.
/// Substitui o acesso direto do ERP ao banco do cliente nos fluxos de reparo.
/// </summary>
public sealed class ArpaResyncProcessor
{
    private static readonly Regex KeyFieldPattern = new("^[a-z_][a-z0-9_]*$", RegexOptions.Compiled);

    private readonly ILogger<ArpaResyncProcessor> _logger;
    private readonly EffectiveArpaCollectorConfigurationProvider _connectionsProvider;
    private readonly EffectiveSyncAgentConfigurationProvider _agentConfigProvider;
    private readonly LocalSyncStore _localStore;
    private readonly ArpaPayloadNormalizerRegistry _normalizers;

    public ArpaResyncProcessor(
        ILogger<ArpaResyncProcessor> logger,
        EffectiveArpaCollectorConfigurationProvider connectionsProvider,
        EffectiveSyncAgentConfigurationProvider agentConfigProvider,
        LocalSyncStore localStore,
        ArpaPayloadNormalizerRegistry normalizers)
    {
        _logger = logger;
        _connectionsProvider = connectionsProvider;
        _agentConfigProvider = agentConfigProvider;
        _localStore = localStore;
        _normalizers = normalizers;
    }

    private static string ViewName(string entityType) => entityType switch
    {
        "produto" => "produtos",
        "cliente" => "clientes",
        "estoque" => "estoque",
        "venda" => "vendas",
        "financeiro" => "financeiro",
        "cobranca" => "cobranca",
        "plano_historico" => "plano_historico",
        _ => entityType,
    };

    public async Task<IReadOnlyList<ResyncResult>> ProcessAsync(
        IReadOnlyList<ResyncItem> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return Array.Empty<ResyncResult>();
        }

        var connections = await _connectionsProvider.GetCurrentAsync(cancellationToken);
        var instanceId = _agentConfigProvider.GetCurrent().InstanceId;
        var results = new List<ResyncResult>(items.Count);

        // Uma fonte por connection string, reaproveitada entre os itens do lote.
        var dataSources = new Dictionary<string, NpgsqlDataSource>();
        try
        {
            foreach (var item in items)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    results.Add(await ProcessItemAsync(item, connections, dataSources, instanceId, cancellationToken));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Resync {Id} ({EntityType}/{EntityKey}) falhou.", item.Id, item.EntityType, item.EntityKey);
                    results.Add(new ResyncResult(item.Id, "failed", Truncate(ex.Message, 240)));
                }
            }
        }
        finally
        {
            foreach (var ds in dataSources.Values)
            {
                await ds.DisposeAsync();
            }
        }

        return results;
    }

    private async Task<ResyncResult> ProcessItemAsync(
        ResyncItem item,
        IReadOnlyList<EffectiveArpaCollectorConnection> connections,
        Dictionary<string, NpgsqlDataSource> dataSources,
        string instanceId,
        CancellationToken cancellationToken)
    {
        if (connections.Count == 0)
        {
            return new ResyncResult(item.Id, "failed", "Sem conexoes Arpa configuradas no agente.");
        }

        var keyField = string.IsNullOrWhiteSpace(item.KeyField) ? "entity_key" : item.KeyField.Trim();
        if (keyField != "entity_key" && !KeyFieldPattern.IsMatch(keyField))
        {
            return new ResyncResult(item.Id, "failed", $"key_field invalido: {keyField}");
        }

        var view = ViewName(item.EntityType);
        var whereClause = keyField == "entity_key"
            ? "entity_key = @key"
            : $"payload_json::jsonb->>'{keyField}' = @key";
        var query =
            $"SELECT entity_key, occurred_at_utc, payload_json, trace_id " +
            $"FROM sync_export.{view} WHERE {whereClause} LIMIT 500";

        var lastError = "";
        foreach (var connection in connections)
        {
            if (!dataSources.TryGetValue(connection.ConnectionString, out var dataSource))
            {
                try
                {
                    dataSource = NpgsqlDataSource.Create(connection.ConnectionString);
                    dataSources[connection.ConnectionString] = dataSource;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    continue;
                }
            }

            int enqueued;
            try
            {
                enqueued = await ReemitAsync(dataSource, connection, item, query, instanceId, cancellationToken);
            }
            catch (Exception ex)
            {
                lastError = Summarize(ex);
                continue;
            }

            if (enqueued > 0)
            {
                _logger.LogInformation(
                    "Resync {Id}: {Count} evento(s) {EntityType} re-enfileirado(s) da conexao '{Nome}'.",
                    item.Id, enqueued, item.EntityType, connection.Nome);
                return new ResyncResult(item.Id, "done", $"{enqueued} evento(s) re-emitido(s).");
            }
        }

        return string.IsNullOrEmpty(lastError)
            ? new ResyncResult(item.Id, "not_found", "Nenhuma linha na sync_export para essa chave.")
            : new ResyncResult(item.Id, "failed", Truncate(lastError, 240));
    }

    private async Task<int> ReemitAsync(
        NpgsqlDataSource dataSource,
        EffectiveArpaCollectorConnection connection,
        ResyncItem item,
        string query,
        string instanceId,
        CancellationToken cancellationToken)
    {
        var enqueued = 0;

        await using var command = dataSource.CreateCommand(query);
        command.Parameters.AddWithValue("key", item.EntityKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var entityKey = reader.GetString(reader.GetOrdinal("entity_key"));
            var occurredAtUtc = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("occurred_at_utc"));
            var payloadJson = reader.GetString(reader.GetOrdinal("payload_json"));
            var traceOrdinal = TryGetOrdinal(reader, "trace_id");
            var traceId = traceOrdinal is null || reader.IsDBNull(traceOrdinal.Value)
                ? null
                : reader.GetString(traceOrdinal.Value);

            var payload = JsonNode.Parse(payloadJson)?.AsObject()
                ?? throw new InvalidOperationException($"sync_export.{ViewName(item.EntityType)} retornou payload_json invalido.");

            if ((item.EntityType == "estoque" || item.EntityType == "venda")
                && !string.IsNullOrWhiteSpace(connection.LojaCodigo)
                && payload["loja_codigo"] is null)
            {
                payload["loja_codigo"] = connection.LojaCodigo;
            }

            var normalizedPayload = _normalizers.Normalize(item.EntityType, entityKey, payload);

            // event_id inclui o id do resync -> o ERP trata como evento NOVO e
            // re-aplica o apply_* (idempotente), em vez de deduplicar.
            var eventId = DeterministicGuid.Create(
                "sync-agent:v1", "arpa-resync", instanceId, item.EntityType, entityKey,
                occurredAtUtc.ToString("O"), item.Id.ToString());

            var draft = new OutboxEventDraft(
                eventId,
                instanceId,
                "arpa",
                item.EntityType,
                entityKey,
                "upsert",
                occurredAtUtc,
                DateTimeOffset.UtcNow,
                SyncContractValues.CurrentSchemaVersion,
                normalizedPayload,
                traceId);

            await _localStore.EnqueueOutboxEventAsync(draft, cancellationToken);
            enqueued++;
        }

        return enqueued;
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

    private static string Summarize(Exception ex) => ex switch
    {
        Npgsql.PostgresException pg => $"{pg.SqlState}: {pg.MessageText}",
        NpgsqlException { InnerException: { } inner } => inner.Message,
        _ => ex.Message,
    };

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];
}

public sealed record ResyncItem(int Id, string SourceSystem, string EntityType, string EntityKey, string KeyField);

public sealed record ResyncResult(int Id, string Status, string Detail);
