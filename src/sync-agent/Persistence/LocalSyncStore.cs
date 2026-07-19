using Npgsql;
using NpgsqlTypes;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SyncAgent.Contracts;
using SyncAgent.Pdv;
using SyncAgent.Utilities;

namespace SyncAgent.Persistence;

public sealed class LocalSyncStore
{
    private readonly NpgsqlDataSource _dataSource;

    public LocalSyncStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<LocalSyncStoreStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                current_database(),
                COALESCE((SELECT extversion FROM pg_extension WHERE extname = 'vector'), ''),
                (SELECT count(*) FROM sync_agent.outbox_events WHERE status = 'pending'),
                (SELECT count(*) FROM sync_agent.outbox_events WHERE status = 'dead_letter'),
                COALESCE((
                    SELECT state_value ->> 'timestamp_utc'
                    FROM sync_agent.agent_state
                    WHERE state_key = 'agent.heartbeat'
                ), ''),
                COALESCE((
                    SELECT state_value ->> 'succeeded'
                    FROM sync_agent.agent_state
                    WHERE state_key = 'agent.heartbeat'
                ), ''),
                COALESCE((
                    SELECT state_value ->> 'connectivity'
                    FROM sync_agent.agent_state
                    WHERE state_key = 'agent.heartbeat'
                ), ''),
                COALESCE(
                    GREATEST(0, EXTRACT(EPOCH FROM (now() - (
                        SELECT min(captured_at_utc)
                        FROM sync_agent.outbox_events
                        WHERE status = 'pending'
                    )))::integer),
                    0
                ),
                COALESCE((
                    SELECT reconciliation_id::text
                    FROM sync_agent.reconciliation_runs
                    ORDER BY started_at_utc DESC
                    LIMIT 1
                ), ''),
                COALESCE((
                    SELECT status
                    FROM sync_agent.reconciliation_runs
                    ORDER BY started_at_utc DESC
                    LIMIT 1
                ), ''),
                COALESCE((
                    SELECT completed_at_utc::text
                    FROM sync_agent.reconciliation_runs
                    ORDER BY started_at_utc DESC
                    LIMIT 1
                ), ''),
                COALESCE((
                    SELECT summary::text
                    FROM sync_agent.reconciliation_runs
                    ORDER BY started_at_utc DESC
                    LIMIT 1
                ), '{}'),
                COALESCE((
                    SELECT jsonb_object_agg(status_counts.sync_status, status_counts.total)
                    FROM (
                        SELECT sync_status, count(*) AS total
                        FROM pdv.sales
                        GROUP BY sync_status
                    ) status_counts
                ), '{}'::jsonb)::text
            """;

        await using var command = _dataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Unable to read local sync store status.");
        }

        return new LocalSyncStoreStatus(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            ParseOptionalDateTimeOffset(reader.GetString(4)),
            ParseOptionalBoolean(reader.GetString(5)),
            string.IsNullOrWhiteSpace(reader.GetString(6)) ? null : reader.GetString(6),
            reader.GetInt32(7),
            string.IsNullOrWhiteSpace(reader.GetString(8)) ? null : Guid.Parse(reader.GetString(8)),
            string.IsNullOrWhiteSpace(reader.GetString(9)) ? null : reader.GetString(9),
            ParseOptionalDateTimeOffset(reader.GetString(10)),
            JsonNode.Parse(reader.GetString(11)) ?? new JsonObject(),
            JsonNode.Parse(reader.GetString(12)) ?? new JsonObject());
    }

    public async Task<IReadOnlyList<LocalTaskLogEntry>> GetRecentTaskLogsAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH dispatch_logs AS (
                SELECT
                    max(COALESCE(completed_at_utc, attempted_at_utc)) AS occurred_at_utc,
                    'dispatcher'::text AS task_type,
                    ('Lote ' || batch_id::text) AS title,
                    CASE
                        WHEN bool_or(status <> 'accepted') THEN string_agg(DISTINCT status, ', ' ORDER BY status)
                        ELSE 'accepted'
                    END AS status,
                    jsonb_build_object(
                        'batch_id', batch_id,
                        'events', count(*),
                        'accepted', count(*) FILTER (WHERE status = 'accepted'),
                        'rejected', count(*) FILTER (WHERE status = 'rejected'),
                        'transient_error', count(*) FILTER (WHERE status = 'transient_error'),
                        'permanent_error', count(*) FILTER (WHERE status = 'permanent_error'),
                        'http_status', max(response_status_code),
                        'error', max(error_message),
                        'affected_records', (
                            SELECT jsonb_agg(
                                jsonb_build_object(
                                    'entity_type', affected.entity_type,
                                    'entity_key', affected.entity_key,
                                    'event_type', affected.event_type,
                                    'status', affected.dispatch_status,
                                    'occurred_at_utc', affected.occurred_at_utc,
                                    'trace_id', affected.trace_id
                                )
                                ORDER BY affected.entity_type, affected.entity_key
                            )
                            FROM (
                                SELECT
                                    events.entity_type,
                                    events.entity_key,
                                    events.event_type,
                                    attempts.status AS dispatch_status,
                                    events.occurred_at_utc,
                                    events.trace_id
                                FROM sync_agent.dispatch_attempts attempts
                                JOIN sync_agent.outbox_events events
                                  ON events.event_id = attempts.event_id
                                WHERE attempts.batch_id = dispatch_attempts.batch_id
                                ORDER BY events.entity_type, events.entity_key
                                LIMIT 50
                            ) affected
                        ),
                        'affected_records_limit', 50
                    ) AS details
                FROM sync_agent.dispatch_attempts
                GROUP BY batch_id
            ),
            reconciliation_logs AS (
                SELECT
                    COALESCE(completed_at_utc, started_at_utc) AS occurred_at_utc,
                    'reconciliation'::text AS task_type,
                    ('Reconciliacao ' || reconciliation_id::text) AS title,
                    status,
                    jsonb_build_object(
                        'reconciliation_id', reconciliation_id,
                        'window_start_utc', window_start_utc,
                        'window_end_utc', window_end_utc,
                        'remote_matched', summary -> 'remote_matched',
                        'pending', summary -> 'pending_outbox_events',
                        'dead_letter', summary -> 'dead_letter_events',
                        'accepted_in_window', summary -> 'accepted_in_window'
                    ) AS details
                FROM sync_agent.reconciliation_runs
            ),
            pdv_operator_snapshot_logs AS (
                SELECT
                    updated_at_utc AS occurred_at_utc,
                    'pdv_operator_snapshot'::text AS task_type,
                    'Importacao de operadores PDV'::text AS title,
                    CASE
                        WHEN (state_value ->> 'succeeded')::boolean THEN 'succeeded'
                        ELSE 'failed'
                    END AS status,
                    state_value AS details
                FROM sync_agent.agent_state
                WHERE state_key = 'pdv.operators.snapshot'
            ),
            pdv_product_snapshot_logs AS (
                SELECT
                    updated_at_utc AS occurred_at_utc,
                    'pdv_product_snapshot'::text AS task_type,
                    'Importacao de produtos PDV'::text AS title,
                    CASE
                        WHEN (state_value ->> 'succeeded')::boolean THEN 'succeeded'
                        ELSE 'failed'
                    END AS status,
                    state_value AS details
                FROM sync_agent.agent_state
                WHERE state_key = 'pdv.products.snapshot'
            ),
            pdv_payment_methods_snapshot_logs AS (
                SELECT
                    updated_at_utc AS occurred_at_utc,
                    'pdv_payment_methods_snapshot'::text AS task_type,
                    'Importacao de pagamentos PDV'::text AS title,
                    CASE
                        WHEN (state_value ->> 'succeeded')::boolean THEN 'succeeded'
                        ELSE 'failed'
                    END AS status,
                    state_value AS details
                FROM sync_agent.agent_state
                WHERE state_key = 'pdv.payment_methods.snapshot'
            )
            SELECT occurred_at_utc, task_type, title, status, details::text
            FROM (
                SELECT * FROM dispatch_logs
                UNION ALL
                SELECT * FROM reconciliation_logs
                UNION ALL
                SELECT * FROM pdv_operator_snapshot_logs
                UNION ALL
                SELECT * FROM pdv_product_snapshot_logs
                UNION ALL
                SELECT * FROM pdv_payment_methods_snapshot_logs
            ) logs
            ORDER BY occurred_at_utc DESC
            LIMIT @limit
            """;

        var entries = new List<LocalTaskLogEntry>();

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 200));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new LocalTaskLogEntry(
                reader.GetFieldValue<DateTimeOffset>(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                JsonNode.Parse(reader.GetString(4)) ?? new JsonObject()));
        }

        return entries;
    }

    public async Task<ReconciliationRunResult> RunLocalReconciliationAsync(
        DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc,
        CancellationToken cancellationToken)
    {
        var reconciliationId = Guid.NewGuid();

        const string insertStartedSql = """
            INSERT INTO sync_agent.reconciliation_runs (
                reconciliation_id,
                entity_type,
                window_start_utc,
                window_end_utc,
                status,
                summary
            )
            VALUES (
                @reconciliation_id,
                NULL,
                @window_start_utc,
                @window_end_utc,
                'started',
                '{}'::jsonb
            )
            """;

        const string summarySql = """
            SELECT
                count(*) FILTER (WHERE captured_at_utc >= @window_start_utc AND captured_at_utc < @window_end_utc),
                count(*) FILTER (WHERE status = 'pending'),
                count(*) FILTER (WHERE status = 'in_flight'),
                count(*) FILTER (WHERE status = 'accepted' AND updated_at_utc >= @window_start_utc AND updated_at_utc < @window_end_utc),
                count(*) FILTER (WHERE status = 'rejected' AND updated_at_utc >= @window_start_utc AND updated_at_utc < @window_end_utc),
                count(*) FILTER (WHERE status = 'dead_letter'),
                COALESCE(
                    GREATEST(0, EXTRACT(EPOCH FROM (now() - min(captured_at_utc) FILTER (WHERE status = 'pending')))::integer),
                    0
                )
            FROM sync_agent.outbox_events
            """;

        const string byEntitySql = """
            SELECT entity_type, status, count(*)
            FROM sync_agent.outbox_events
            WHERE captured_at_utc >= @window_start_utc
              AND captured_at_utc < @window_end_utc
            GROUP BY entity_type, status
            ORDER BY entity_type, status
            """;

        const string deadLetterReasonSql = """
            SELECT entity_type, reason, count(*)
            FROM sync_agent.dead_letter_events
            WHERE failed_at_utc >= @window_start_utc
              AND failed_at_utc < @window_end_utc
            GROUP BY entity_type, reason
            ORDER BY count(*) DESC, entity_type, reason
            LIMIT 10
            """;

        const string updateCompletedSql = """
            UPDATE sync_agent.reconciliation_runs
            SET
                status = 'completed',
                summary = @summary,
                completed_at_utc = now()
            WHERE reconciliation_id = @reconciliation_id
            """;

        const string updateFailedSql = """
            UPDATE sync_agent.reconciliation_runs
            SET
                status = 'failed',
                summary = @summary,
                completed_at_utc = now()
            WHERE reconciliation_id = @reconciliation_id
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var insertCommand = new NpgsqlCommand(insertStartedSql, connection, transaction))
        {
            insertCommand.Parameters.AddWithValue("reconciliation_id", reconciliationId);
            insertCommand.Parameters.AddWithValue("window_start_utc", windowStartUtc);
            insertCommand.Parameters.AddWithValue("window_end_utc", windowEndUtc);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        try
        {
            await using var summaryCommand = new NpgsqlCommand(summarySql, connection, transaction);
            summaryCommand.Parameters.AddWithValue("window_start_utc", windowStartUtc);
            summaryCommand.Parameters.AddWithValue("window_end_utc", windowEndUtc);

            await using var summaryReader = await summaryCommand.ExecuteReaderAsync(cancellationToken);
            if (!await summaryReader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Unable to build reconciliation summary.");
            }

            var summary = new JsonObject
            {
                ["window_start_utc"] = windowStartUtc.ToUniversalTime().ToString("O"),
                ["window_end_utc"] = windowEndUtc.ToUniversalTime().ToString("O"),
                ["events_captured_in_window"] = summaryReader.GetInt64(0),
                ["pending_outbox_events"] = summaryReader.GetInt64(1),
                ["in_flight_outbox_events"] = summaryReader.GetInt64(2),
                ["accepted_in_window"] = summaryReader.GetInt64(3),
                ["rejected_in_window"] = summaryReader.GetInt64(4),
                ["dead_letter_events"] = summaryReader.GetInt64(5),
                ["oldest_pending_age_seconds"] = summaryReader.GetInt32(6)
            };
            await summaryReader.CloseAsync();

            var byEntity = new JsonArray();
            await using (var byEntityCommand = new NpgsqlCommand(byEntitySql, connection, transaction))
            {
                byEntityCommand.Parameters.AddWithValue("window_start_utc", windowStartUtc);
                byEntityCommand.Parameters.AddWithValue("window_end_utc", windowEndUtc);
                await using var byEntityReader = await byEntityCommand.ExecuteReaderAsync(cancellationToken);
                while (await byEntityReader.ReadAsync(cancellationToken))
                {
                    byEntity.Add(new JsonObject
                    {
                        ["entity_type"] = byEntityReader.GetString(0),
                        ["status"] = byEntityReader.GetString(1),
                        ["count"] = byEntityReader.GetInt64(2)
                    });
                }
            }

            var deadLetterReasons = new JsonArray();
            await using (var reasonsCommand = new NpgsqlCommand(deadLetterReasonSql, connection, transaction))
            {
                reasonsCommand.Parameters.AddWithValue("window_start_utc", windowStartUtc);
                reasonsCommand.Parameters.AddWithValue("window_end_utc", windowEndUtc);
                await using var reasonsReader = await reasonsCommand.ExecuteReaderAsync(cancellationToken);
                while (await reasonsReader.ReadAsync(cancellationToken))
                {
                    deadLetterReasons.Add(new JsonObject
                    {
                        ["entity_type"] = reasonsReader.GetString(0),
                        ["reason"] = reasonsReader.GetString(1),
                        ["count"] = reasonsReader.GetInt64(2)
                    });
                }
            }

            summary["by_entity_status"] = byEntity;
            summary["dead_letter_reasons"] = deadLetterReasons;

            await using (var updateCommand = new NpgsqlCommand(updateCompletedSql, connection, transaction))
            {
                updateCommand.Parameters.AddWithValue("reconciliation_id", reconciliationId);
                updateCommand.Parameters.AddWithValue("summary", NpgsqlDbType.Jsonb, ToCanonicalJson(summary));
                await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return new ReconciliationRunResult(reconciliationId, "completed", summary);
        }
        catch (Exception ex)
        {
            var summary = new JsonObject
            {
                ["window_start_utc"] = windowStartUtc.ToUniversalTime().ToString("O"),
                ["window_end_utc"] = windowEndUtc.ToUniversalTime().ToString("O"),
                ["error"] = Truncate(ex.Message, 1000)
            };

            await using var updateCommand = new NpgsqlCommand(updateFailedSql, connection, transaction);
            updateCommand.Parameters.AddWithValue("reconciliation_id", reconciliationId);
            updateCommand.Parameters.AddWithValue("summary", NpgsqlDbType.Jsonb, ToCanonicalJson(summary));
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new ReconciliationRunResult(reconciliationId, "failed", summary);
        }
    }

    public async Task<ReconciliationSummaryPayload> BuildReconciliationSummaryPayloadAsync(
        Guid reconciliationId,
        string sourceInstanceId,
        DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                entity_type,
                count(*) FILTER (WHERE captured_at_utc >= @window_start_utc AND captured_at_utc < @window_end_utc),
                count(*) FILTER (WHERE status = 'accepted' AND captured_at_utc >= @window_start_utc AND captured_at_utc < @window_end_utc),
                count(*) FILTER (WHERE status = 'rejected' AND captured_at_utc >= @window_start_utc AND captured_at_utc < @window_end_utc),
                count(*) FILTER (WHERE status = 'dead_letter' AND captured_at_utc >= @window_start_utc AND captured_at_utc < @window_end_utc),
                COALESCE(
                    string_agg(payload_hash, E'\n' ORDER BY event_id)
                        FILTER (WHERE captured_at_utc >= @window_start_utc AND captured_at_utc < @window_end_utc),
                    ''
                )
            FROM sync_agent.outbox_events
            GROUP BY entity_type
            """;

        var summaries = SyncAgent.Contracts.SyncContractValues.EntityTypes
            .ToDictionary(
                entityType => entityType,
                entityType => new ReconciliationEntitySummary(
                    entityType,
                    0,
                    0,
                    0,
                    0,
                    AggregateHash("")));

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("window_start_utc", windowStartUtc);
        command.Parameters.AddWithValue("window_end_utc", windowEndUtc);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var entityType = reader.GetString(0);
            summaries[entityType] = new ReconciliationEntitySummary(
                entityType,
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                AggregateHash(reader.GetString(5)));
        }

        return new ReconciliationSummaryPayload(
            reconciliationId,
            sourceInstanceId,
            windowStartUtc,
            windowEndUtc,
            DateTimeOffset.UtcNow,
            summaries.Values.OrderBy(item => item.EntityType, StringComparer.Ordinal).ToArray());
    }

    public async Task UpdateReconciliationRemoteResultAsync(
        Guid reconciliationId,
        bool matched,
        JsonNode response,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE sync_agent.reconciliation_runs
            SET
                summary = jsonb_set(
                    jsonb_set(summary, '{remote_matched}', to_jsonb(@matched::boolean), true),
                    '{remote_response}',
                    @response::jsonb,
                    true
                ),
                completed_at_utc = now()
            WHERE reconciliation_id = @reconciliation_id
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("reconciliation_id", reconciliationId);
        command.Parameters.AddWithValue("matched", matched);
        command.Parameters.AddWithValue("response", NpgsqlDbType.Jsonb, response.ToJsonString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertAgentIdentityAsync(
        string instanceId,
        string erpTenantId,
        string erpApiBaseUrl,
        string agentVersion,
        CancellationToken cancellationToken)
    {
        var stateValue = new JsonObject
        {
            ["instance_id"] = instanceId,
            ["erp_tenant_id"] = erpTenantId,
            ["erp_api_base_url"] = erpApiBaseUrl,
            ["agent_version"] = agentVersion
        };

        const string sql = """
            INSERT INTO sync_agent.agent_state (state_key, state_value)
            VALUES ('agent.identity', @state_value)
            ON CONFLICT (state_key) DO UPDATE
            SET
                state_value = EXCLUDED.state_value,
                updated_at_utc = now()
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("state_value", NpgsqlDbType.Jsonb, ToCanonicalJson(stateValue));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<DateTimeOffset> GetDateTimeOffsetStateAsync(
        string stateKey,
        DateTimeOffset defaultValue,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT state_value ->> 'value'
            FROM sync_agent.agent_state
            WHERE state_key = @state_key
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("state_key", stateKey);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is not string text || string.IsNullOrWhiteSpace(text))
        {
            return defaultValue;
        }

        return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);
    }

    public async Task<DateTimeOffset?> GetDateTimeOffsetStateOrNullAsync(
        string stateKey,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT state_value ->> 'value'
            FROM sync_agent.agent_state
            WHERE state_key = @state_key
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("state_key", stateKey);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is not string text || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);
    }

    public async Task SetDateTimeOffsetStateAsync(
        string stateKey,
        DateTimeOffset value,
        CancellationToken cancellationToken)
    {
        var stateValue = new JsonObject
        {
            ["value"] = value.ToUniversalTime().ToString("O")
        };

        const string sql = """
            INSERT INTO sync_agent.agent_state (state_key, state_value)
            VALUES (@state_key, @state_value)
            ON CONFLICT (state_key) DO UPDATE
            SET
                state_value = EXCLUDED.state_value,
                updated_at_utc = now()
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("state_key", stateKey);
        command.Parameters.AddWithValue("state_value", NpgsqlDbType.Jsonb, ToCanonicalJson(stateValue));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertAgentHeartbeatAsync(
        DateTimeOffset timestampUtc,
        bool succeeded,
        string connectivity,
        int? responseStatusCode,
        string? error,
        CancellationToken cancellationToken)
    {
        var stateValue = new JsonObject
        {
            ["timestamp_utc"] = timestampUtc.ToUniversalTime().ToString("O"),
            ["succeeded"] = succeeded,
            ["connectivity"] = connectivity,
            ["response_status_code"] = responseStatusCode,
            ["error"] = error
        };

        const string sql = """
            INSERT INTO sync_agent.agent_state (state_key, state_value)
            VALUES ('agent.heartbeat', @state_value)
            ON CONFLICT (state_key) DO UPDATE
            SET
                state_value = EXCLUDED.state_value,
                updated_at_utc = now()
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("state_value", NpgsqlDbType.Jsonb, ToCanonicalJson(stateValue));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> UpsertPdvOperatorsAsync(
        IReadOnlyCollection<PdvOperatorSnapshotItem> operators,
        CancellationToken cancellationToken)
    {
        if (operators.Count == 0)
        {
            return 0;
        }

        const string sql = """
            INSERT INTO pdv.operators (
                operator_id,
                external_operator_id,
                login,
                display_name,
                password_hash,
                role,
                active,
                permissions,
                updated_at_utc
            )
            VALUES (
                @operator_id,
                @external_operator_id,
                @login,
                @display_name,
                @password_hash,
                @role,
                @active,
                @permissions,
                @updated_at_utc
            )
            ON CONFLICT (operator_id) DO UPDATE
            SET
                external_operator_id = EXCLUDED.external_operator_id,
                login = EXCLUDED.login,
                display_name = EXCLUDED.display_name,
                password_hash = EXCLUDED.password_hash,
                role = EXCLUDED.role,
                active = EXCLUDED.active,
                permissions = EXCLUDED.permissions,
                updated_at_utc = EXCLUDED.updated_at_utc
            """;

        var imported = 0;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var item in operators)
        {
            var operatorId = DeterministicGuid.Create("erp-pdv-operator", item.OperatorId);
            var role = NormalizeOperatorRole(item.Role);
            var permissions = JsonSerializer.Serialize(item.Permissions);

            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("operator_id", operatorId);
            command.Parameters.AddWithValue("external_operator_id", item.OperatorId);
            command.Parameters.AddWithValue("login", item.Login);
            command.Parameters.AddWithValue("display_name", item.DisplayName);
            command.Parameters.AddWithValue("password_hash", item.PasswordHash);
            command.Parameters.AddWithValue("role", role);
            command.Parameters.AddWithValue("active", item.Active && item.DeletedAtUtc is null);
            command.Parameters.AddWithValue("permissions", NpgsqlDbType.Jsonb, permissions);
            command.Parameters.AddWithValue("updated_at_utc", item.UpdatedAtUtc);
            imported += await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return imported;
    }

    public async Task UpsertPdvOperatorSnapshotStateAsync(
        DateTimeOffset timestampUtc,
        bool succeeded,
        int imported,
        int? responseStatusCode,
        string? error,
        CancellationToken cancellationToken)
    {
        var stateValue = new JsonObject
        {
            ["timestamp_utc"] = timestampUtc.ToUniversalTime().ToString("O"),
            ["succeeded"] = succeeded,
            ["imported"] = imported,
            ["response_status_code"] = responseStatusCode,
            ["error"] = error
        };

        const string sql = """
            INSERT INTO sync_agent.agent_state (state_key, state_value)
            VALUES ('pdv.operators.snapshot', @state_value)
            ON CONFLICT (state_key) DO UPDATE
            SET
                state_value = EXCLUDED.state_value,
                updated_at_utc = now()
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("state_value", NpgsqlDbType.Jsonb, ToCanonicalJson(stateValue));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> UpsertPdvProductsAsync(
        IReadOnlyCollection<PdvProductSnapshotItem> products,
        CancellationToken cancellationToken)
    {
        if (products.Count == 0)
        {
            return 0;
        }

        const string ensureSchemaSql = """
            ALTER TABLE pdv.products ADD COLUMN IF NOT EXISTS factory_code text NULL
            """;

        const string sql = """
            INSERT INTO pdv.products (
                product_id,
                source_system,
                external_key,
                sku,
                name,
                barcode,
                unit,
                price,
                active,
                payload,
                factory_code,
                updated_at_utc
            )
            VALUES (
                @product_id,
                'erp',
                @external_key,
                @sku,
                @name,
                @barcode,
                @unit,
                @price,
                @active,
                @payload,
                NULLIF(trim(@payload::jsonb ->> 'codigo_fabrica'), ''),
                @updated_at_utc
            )
            ON CONFLICT (product_id) DO UPDATE
            SET
                source_system = EXCLUDED.source_system,
                external_key = EXCLUDED.external_key,
                sku = EXCLUDED.sku,
                name = EXCLUDED.name,
                barcode = EXCLUDED.barcode,
                unit = EXCLUDED.unit,
                price = EXCLUDED.price,
                active = EXCLUDED.active,
                payload = EXCLUDED.payload,
                factory_code = EXCLUDED.factory_code,
                updated_at_utc = EXCLUDED.updated_at_utc
            """;

        var imported = 0;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var ensureSchema = new NpgsqlCommand(ensureSchemaSql, connection, transaction))
        {
            await ensureSchema.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var item in products)
        {
            var productId = DeterministicGuid.Create("erp-pdv-product", item.ProductId);
            var externalKey = FirstNonEmpty(item.ProductId);
            var payload = item.Payload.HasValue ? item.Payload.Value.GetRawText() : "{}";

            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("product_id", productId);
            command.Parameters.AddWithValue("external_key", externalKey);
            command.Parameters.AddWithValue("sku", (object?)FirstNonEmpty(item.Sku, item.ExternalKey, item.ProductId) ?? DBNull.Value);
            command.Parameters.AddWithValue("name", FirstNonEmpty(item.Name, $"Produto {item.ProductId}"));
            command.Parameters.AddWithValue("barcode", (object?)NormalizeOptionalText(item.Barcode) ?? DBNull.Value);
            command.Parameters.AddWithValue("unit", FirstNonEmpty(item.Unit, "UN"));
            command.Parameters.AddWithValue("price", Math.Max(0, item.Price));
            command.Parameters.AddWithValue("active", item.Active && item.DeletedAtUtc is null);
            command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payload);
            command.Parameters.AddWithValue("updated_at_utc", item.UpdatedAtUtc);
            imported += await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return imported;
    }

    public async Task UpsertPdvProductSnapshotStateAsync(
        DateTimeOffset timestampUtc,
        bool succeeded,
        int imported,
        int? responseStatusCode,
        string? error,
        CancellationToken cancellationToken)
    {
        var stateValue = new JsonObject
        {
            ["timestamp_utc"] = timestampUtc.ToUniversalTime().ToString("O"),
            ["succeeded"] = succeeded,
            ["imported"] = imported,
            ["response_status_code"] = responseStatusCode,
            ["error"] = error
        };

        const string sql = """
            INSERT INTO sync_agent.agent_state (state_key, state_value)
            VALUES ('pdv.products.snapshot', @state_value)
            ON CONFLICT (state_key) DO UPDATE
            SET
                state_value = EXCLUDED.state_value,
                updated_at_utc = now()
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("state_value", NpgsqlDbType.Jsonb, ToCanonicalJson(stateValue));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> UpsertPdvPaymentMethodsAsync(
        IReadOnlyCollection<PdvPaymentSpeciesSnapshotItem> species,
        IReadOnlyCollection<PdvPaymentConditionSnapshotItem> conditions,
        CancellationToken cancellationToken)
    {
        if (species.Count == 0 && conditions.Count == 0)
        {
            return 0;
        }

        const string speciesSql = """
            INSERT INTO pdv.payment_species (
                payment_species_id,
                source_system,
                external_key,
                name,
                kind,
                requires_tef,
                allows_change,
                active,
                payload,
                updated_at_utc
            )
            VALUES (
                @payment_species_id,
                'erp',
                @external_key,
                @name,
                @kind,
                @requires_tef,
                @allows_change,
                @active,
                @payload,
                @updated_at_utc
            )
            ON CONFLICT (payment_species_id) DO UPDATE
            SET
                source_system = EXCLUDED.source_system,
                external_key = EXCLUDED.external_key,
                name = EXCLUDED.name,
                kind = EXCLUDED.kind,
                requires_tef = EXCLUDED.requires_tef,
                allows_change = EXCLUDED.allows_change,
                active = EXCLUDED.active,
                payload = EXCLUDED.payload,
                updated_at_utc = EXCLUDED.updated_at_utc
            """;

        const string conditionsSql = """
            INSERT INTO pdv.payment_conditions (
                payment_condition_id,
                source_system,
                external_key,
                name,
                installments,
                first_due_days,
                interval_days,
                active,
                payload,
                updated_at_utc
            )
            VALUES (
                @payment_condition_id,
                'erp',
                @external_key,
                @name,
                @installments,
                @first_due_days,
                @interval_days,
                @active,
                @payload,
                @updated_at_utc
            )
            ON CONFLICT (payment_condition_id) DO UPDATE
            SET
                source_system = EXCLUDED.source_system,
                external_key = EXCLUDED.external_key,
                name = EXCLUDED.name,
                installments = EXCLUDED.installments,
                first_due_days = EXCLUDED.first_due_days,
                interval_days = EXCLUDED.interval_days,
                active = EXCLUDED.active,
                payload = EXCLUDED.payload,
                updated_at_utc = EXCLUDED.updated_at_utc
            """;

        var imported = 0;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var item in species)
        {
            var paymentSpeciesId = DeterministicGuid.Create("erp-pdv-payment-species", item.SpeciesId);
            var payload = item.Payload.HasValue ? item.Payload.Value.GetRawText() : "{}";

            await using var command = new NpgsqlCommand(speciesSql, connection, transaction);
            command.Parameters.AddWithValue("payment_species_id", paymentSpeciesId);
            command.Parameters.AddWithValue("external_key", FirstNonEmpty(item.SpeciesId));
            command.Parameters.AddWithValue("name", FirstNonEmpty(item.Name, $"Especie {item.SpeciesId}"));
            command.Parameters.AddWithValue("kind", NormalizePaymentKind(item.Kind));
            command.Parameters.AddWithValue("requires_tef", item.RequiresTef);
            command.Parameters.AddWithValue("allows_change", item.AllowsChange);
            command.Parameters.AddWithValue("active", item.Active && item.DeletedAtUtc is null);
            command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payload);
            command.Parameters.AddWithValue("updated_at_utc", item.UpdatedAtUtc);
            imported += await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var item in conditions)
        {
            var paymentConditionId = DeterministicGuid.Create("erp-pdv-payment-condition", item.ConditionId);
            var payload = item.Payload.HasValue ? item.Payload.Value.GetRawText() : "{}";

            await using var command = new NpgsqlCommand(conditionsSql, connection, transaction);
            command.Parameters.AddWithValue("payment_condition_id", paymentConditionId);
            command.Parameters.AddWithValue("external_key", FirstNonEmpty(item.ConditionId));
            command.Parameters.AddWithValue("name", FirstNonEmpty(item.Name, $"Condicao {item.ConditionId}"));
            command.Parameters.AddWithValue("installments", Math.Max(1, item.Installments));
            command.Parameters.AddWithValue("first_due_days", Math.Max(0, item.FirstDueDays));
            command.Parameters.AddWithValue("interval_days", Math.Max(0, item.IntervalDays));
            command.Parameters.AddWithValue("active", item.Active && item.DeletedAtUtc is null);
            command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payload);
            command.Parameters.AddWithValue("updated_at_utc", item.UpdatedAtUtc);
            imported += await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return imported;
    }

    public async Task UpsertPdvPaymentMethodsSnapshotStateAsync(
        DateTimeOffset timestampUtc,
        bool succeeded,
        int imported,
        int? responseStatusCode,
        string? error,
        CancellationToken cancellationToken)
    {
        var stateValue = new JsonObject
        {
            ["timestamp_utc"] = timestampUtc.ToUniversalTime().ToString("O"),
            ["succeeded"] = succeeded,
            ["imported"] = imported,
            ["response_status_code"] = responseStatusCode,
            ["error"] = error
        };

        const string sql = """
            INSERT INTO sync_agent.agent_state (state_key, state_value)
            VALUES ('pdv.payment_methods.snapshot', @state_value)
            ON CONFLICT (state_key) DO UPDATE
            SET
                state_value = EXCLUDED.state_value,
                updated_at_utc = now()
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("state_value", NpgsqlDbType.Jsonb, ToCanonicalJson(stateValue));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<OutboxEnqueueResult> EnqueueOutboxEventAsync(
        OutboxEventDraft draft,
        CancellationToken cancellationToken)
    {
        ValidateDraft(draft);

        var payloadJson = ToCanonicalJson(draft.Payload);
        var payloadHash = ComputeSha256Hash(payloadJson);

        const string sql = """
            INSERT INTO sync_agent.outbox_events (
                event_id,
                source_instance_id,
                source_system,
                entity_type,
                entity_key,
                event_type,
                occurred_at_utc,
                captured_at_utc,
                schema_version,
                payload,
                payload_hash,
                trace_id
            )
            VALUES (
                @event_id,
                @source_instance_id,
                @source_system,
                @entity_type,
                @entity_key,
                @event_type,
                @occurred_at_utc,
                @captured_at_utc,
                @schema_version,
                @payload,
                @payload_hash,
                @trace_id
            )
            ON CONFLICT (event_id) DO NOTHING
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("event_id", draft.EventId);
        command.Parameters.AddWithValue("source_instance_id", draft.SourceInstanceId);
        command.Parameters.AddWithValue("source_system", draft.SourceSystem);
        command.Parameters.AddWithValue("entity_type", draft.EntityType);
        command.Parameters.AddWithValue("entity_key", draft.EntityKey);
        command.Parameters.AddWithValue("event_type", draft.EventType);
        command.Parameters.AddWithValue("occurred_at_utc", draft.OccurredAtUtc);
        command.Parameters.AddWithValue("captured_at_utc", draft.CapturedAtUtc);
        command.Parameters.AddWithValue("schema_version", draft.SchemaVersion);
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payloadJson);
        command.Parameters.AddWithValue("payload_hash", payloadHash);
        command.Parameters.AddWithValue("trace_id", (object?)draft.TraceId ?? DBNull.Value);

        var insertedRows = await command.ExecuteNonQueryAsync(cancellationToken);
        return new OutboxEnqueueResult(draft.EventId, payloadHash, insertedRows == 1);
    }

    public async Task<IReadOnlyList<PdvSalePendingPublishRecord>> GetPendingPdvSalesAsync(
        int batchSize,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                sales.sale_id,
                sales.sale_number,
                sales.cash_session_id,
                sales.operator_id,
                operators.external_operator_id,
                sales.customer_id,
                COALESCE(sales.completed_at_utc, sales.cancelled_at_utc, sales.updated_at_utc) AS occurred_at_utc,
                sales.status,
                sales.subtotal_amount,
                sales.discount_amount,
                sales.total_amount,
                COALESCE((
                    SELECT jsonb_agg(
                        jsonb_build_object(
                            'sale_item_id', items.sale_item_id,
                            'product_id', items.product_id,
                            'product_erp_id', CASE
                                WHEN products.source_system = 'erp' THEN products.external_key
                                ELSE NULL
                            END,
                            'product_external_key', products.external_key,
                            'product_name', products.name,
                            'line_number', items.line_number,
                            'quantity', items.quantity,
                            'unit_price', items.unit_price,
                            'discount_amount', items.discount_amount,
                            'total_amount', items.total_amount
                        )
                        ORDER BY items.line_number
                    )
                    FROM pdv.sale_items items
                    JOIN pdv.products products ON products.product_id = items.product_id
                    WHERE items.sale_id = sales.sale_id
                ), '[]'::jsonb) AS items,
                COALESCE((
                    SELECT jsonb_agg(
                        jsonb_build_object(
                            'payment_id', payments.payment_id,
                            'payment_method', payments.payment_method,
                            'amount', payments.amount,
                            'status', CASE payments.status
                                WHEN 'captured' THEN 'approved'
                                WHEN 'cancelled' THEN 'cancelled'
                                WHEN 'failed' THEN 'failed'
                                ELSE 'pending'
                            END,
                            'authorization_code', payments.authorization_code,
                            'payment_species_external_key', payments.payload ->> 'payment_species_external_key',
                            'payment_species_kind', payments.payload ->> 'payment_species_kind',
                            'payment_condition_external_key', payments.payload ->> 'payment_condition_external_key',
                            'installments', payments.payload -> 'installments',
                            'requires_tef', payments.payload -> 'requires_tef',
                            'allows_change', payments.payload -> 'allows_change',
                            'tef_metadata', CASE
                                WHEN payments.payload ? 'tef_metadata' THEN payments.payload -> 'tef_metadata'
                                WHEN COALESCE((payments.payload ->> 'requires_tef')::boolean, false)
                                     OR payments.authorization_code IS NOT NULL THEN
                                    jsonb_strip_nulls(jsonb_build_object(
                                        'authorization_code', payments.authorization_code,
                                        'installments', payments.payload -> 'installments',
                                        'simulated', COALESCE((payments.payload ->> 'requires_tef')::boolean, false)
                                    ))
                                ELSE NULL
                            END
                        )
                        ORDER BY payments.created_at_utc, payments.payment_id
                    )
                    FROM pdv.payments payments
                    WHERE payments.sale_id = sales.sale_id
                ), '[]'::jsonb) AS payments,
                sales.customer_document
            FROM pdv.sales sales
            JOIN pdv.operators operators ON operators.operator_id = sales.operator_id
            WHERE sales.sync_status = 'pending_sync'
              AND sales.status IN ('completed', 'cancelled')
            ORDER BY COALESCE(sales.completed_at_utc, sales.cancelled_at_utc, sales.updated_at_utc), sales.sale_id
            LIMIT @batch_size
            """;

        var records = new List<PdvSalePendingPublishRecord>();
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("batch_size", batchSize);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new PdvSalePendingPublishRecord(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetGuid(2),
                reader.GetGuid(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetGuid(5),
                reader.GetFieldValue<DateTimeOffset>(6),
                reader.GetString(7),
                reader.GetDecimal(8),
                reader.GetDecimal(9),
                reader.GetDecimal(10),
                JsonNode.Parse(reader.GetString(11))?.AsArray() ?? [],
                JsonNode.Parse(reader.GetString(12))?.AsArray() ?? [],
                reader.IsDBNull(13) ? null : reader.GetString(13)));
        }

        return records;
    }

    public async Task MarkPdvSalePublishedAsync(
        Guid saleId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE pdv.sales
            SET
                sync_status = 'sent',
                updated_at_utc = now()
            WHERE sale_id = @sale_id
              AND sync_status = 'pending_sync'
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sale_id", saleId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PdvRejectedSaleRecord>> GetRejectedPdvSalesAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                sales.sale_id,
                sales.sale_number,
                sales.total_amount,
                sales.updated_at_utc,
                COALESCE(events.last_error, dead.reason, '') AS rejection_reason
            FROM pdv.sales sales
            LEFT JOIN sync_agent.outbox_events events
              ON events.event_id = sales.sale_id
             AND events.source_system = 'pdv_local'
             AND events.entity_type = 'venda'
            LEFT JOIN sync_agent.dead_letter_events dead
              ON dead.event_id = events.event_id
            WHERE sales.sync_status = 'rejected'
            ORDER BY sales.updated_at_utc DESC, sales.sale_id
            LIMIT @limit
            """;

        var records = new List<PdvRejectedSaleRecord>();
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 100));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new PdvRejectedSaleRecord(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetDecimal(2),
                reader.GetFieldValue<DateTimeOffset>(3),
                reader.GetString(4)));
        }

        return records;
    }

    public async Task<bool> RequeueRejectedPdvSaleAsync(
        Guid saleId,
        CancellationToken cancellationToken)
    {
        const string updateOutboxSql = """
            UPDATE sync_agent.outbox_events events
            SET
                status = 'pending',
                last_error = NULL,
                next_attempt_at_utc = now(),
                updated_at_utc = now()
            FROM pdv.sales sales
            WHERE events.event_id = sales.sale_id
              AND events.event_id = @sale_id
              AND events.source_system = 'pdv_local'
              AND events.entity_type = 'venda'
              AND events.status = 'dead_letter'
              AND sales.sync_status = 'rejected'
            """;

        const string deleteDeadLetterSql = """
            DELETE FROM sync_agent.dead_letter_events
            WHERE event_id = @sale_id
            """;

        const string updateSaleSql = """
            UPDATE pdv.sales
            SET
                sync_status = 'sent',
                updated_at_utc = now()
            WHERE sale_id = @sale_id
              AND sync_status = 'rejected'
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var updatedOutbox = 0;
        await using (var updateOutbox = new NpgsqlCommand(updateOutboxSql, connection, transaction))
        {
            updateOutbox.Parameters.AddWithValue("sale_id", saleId);
            updatedOutbox = await updateOutbox.ExecuteNonQueryAsync(cancellationToken);
        }

        if (updatedOutbox == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await using (var deleteDeadLetter = new NpgsqlCommand(deleteDeadLetterSql, connection, transaction))
        {
            deleteDeadLetter.Parameters.AddWithValue("sale_id", saleId);
            await deleteDeadLetter.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var updateSale = new NpgsqlCommand(updateSaleSql, connection, transaction))
        {
            updateSale.Parameters.AddWithValue("sale_id", saleId);
            await updateSale.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<OutboxEventRecord>> ClaimPendingOutboxEventsAsync(
        int batchSize,
        TimeSpan inFlightRecoveryAge,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE sync_agent.outbox_events
            SET
                status = 'pending',
                next_attempt_at_utc = now(),
                last_error = 'in_flight_recovered',
                updated_at_utc = now()
            WHERE status = 'in_flight'
              AND updated_at_utc <= now() - @in_flight_recovery_age;

            WITH claimed AS (
                SELECT event_id
                FROM sync_agent.outbox_events
                WHERE status = 'pending'
                  AND next_attempt_at_utc <= now()
                ORDER BY captured_at_utc, event_id
                LIMIT @batch_size
                FOR UPDATE SKIP LOCKED
            )
            UPDATE sync_agent.outbox_events events
            SET
                status = 'in_flight',
                attempt_count = events.attempt_count + 1,
                updated_at_utc = now()
            FROM claimed
            WHERE events.event_id = claimed.event_id
            RETURNING
                events.event_id,
                events.source_instance_id,
                events.source_system,
                events.entity_type,
                events.entity_key,
                events.event_type,
                events.occurred_at_utc,
                events.captured_at_utc,
                events.schema_version,
                events.payload::text,
                events.payload_hash,
                events.trace_id,
                events.attempt_count
            """;

        var records = new List<OutboxEventRecord>();

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("batch_size", batchSize);
        command.Parameters.AddWithValue("in_flight_recovery_age", inFlightRecoveryAge);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new OutboxEventRecord(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetFieldValue<DateTimeOffset>(6),
                reader.GetFieldValue<DateTimeOffset>(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.GetInt32(12)));
        }

        await reader.CloseAsync();
        await transaction.CommitAsync(cancellationToken);

        return records;
    }

    public async Task RecordDispatchStartedAsync(
        Guid batchId,
        IReadOnlyCollection<OutboxEventRecord> events,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO sync_agent.dispatch_attempts (
                attempt_id,
                batch_id,
                event_id,
                attempt_number,
                status
            )
            VALUES (
                @attempt_id,
                @batch_id,
                @event_id,
                @attempt_number,
                'started'
            )
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var record in events)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("attempt_id", Guid.NewGuid());
            command.Parameters.AddWithValue("batch_id", batchId);
            command.Parameters.AddWithValue("event_id", record.EventId);
            command.Parameters.AddWithValue("attempt_number", record.AttemptCount);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task MarkDispatchAcceptedAsync(
        Guid batchId,
        IReadOnlyCollection<Guid> eventIds,
        CancellationToken cancellationToken)
    {
        const string updateEventsSql = """
            UPDATE sync_agent.outbox_events
            SET
                status = 'accepted',
                last_error = NULL,
                updated_at_utc = now()
            WHERE event_id = ANY(@event_ids)
            """;

        const string updateAttemptsSql = """
            UPDATE sync_agent.dispatch_attempts
            SET
                status = 'accepted',
                completed_at_utc = now(),
                response_status_code = 202
            WHERE batch_id = @batch_id
              AND event_id = ANY(@event_ids)
              AND status = 'started'
            """;

        await UpdateDispatchResultAsync(
            batchId,
            eventIds,
            updateEventsSql,
            updateAttemptsSql,
            null,
            cancellationToken);

        await MarkPdvSalesByOutboxStatusAsync(eventIds, "accepted", cancellationToken);
    }

    public async Task MarkDispatchRejectedAsync(
        Guid batchId,
        IReadOnlyDictionary<Guid, string> rejectedEvents,
        CancellationToken cancellationToken)
    {
        const string updateEventSql = """
            UPDATE sync_agent.outbox_events
            SET
                status = 'dead_letter',
                last_error = @reason,
                updated_at_utc = now()
            WHERE event_id = @event_id
            """;

        const string insertDeadLetterSql = """
            INSERT INTO sync_agent.dead_letter_events (
                event_id,
                source_instance_id,
                source_system,
                entity_type,
                entity_key,
                event_type,
                occurred_at_utc,
                captured_at_utc,
                schema_version,
                payload,
                payload_hash,
                trace_id,
                attempt_count,
                reason
            )
            SELECT
                event_id,
                source_instance_id,
                source_system,
                entity_type,
                entity_key,
                event_type,
                occurred_at_utc,
                captured_at_utc,
                schema_version,
                payload,
                payload_hash,
                trace_id,
                attempt_count,
                @reason
            FROM sync_agent.outbox_events
            WHERE event_id = @event_id
            ON CONFLICT (event_id) DO UPDATE
            SET
                attempt_count = EXCLUDED.attempt_count,
                reason = EXCLUDED.reason,
                failed_at_utc = now()
            """;

        const string updateAttemptSql = """
            UPDATE sync_agent.dispatch_attempts
            SET
                status = 'rejected',
                completed_at_utc = now(),
                response_status_code = 202,
                error_classification = 'permanent_error',
                error_message = @reason
            WHERE batch_id = @batch_id
              AND event_id = @event_id
              AND status = 'started'
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var rejectedEvent in rejectedEvents)
        {
            var reason = Truncate(rejectedEvent.Value, 1000);

            await using (var insertDeadLetterCommand = new NpgsqlCommand(insertDeadLetterSql, connection, transaction))
            {
                insertDeadLetterCommand.Parameters.AddWithValue("event_id", rejectedEvent.Key);
                insertDeadLetterCommand.Parameters.AddWithValue("reason", reason);
                await insertDeadLetterCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var updateEventCommand = new NpgsqlCommand(updateEventSql, connection, transaction))
            {
                updateEventCommand.Parameters.AddWithValue("event_id", rejectedEvent.Key);
                updateEventCommand.Parameters.AddWithValue("reason", reason);
                await updateEventCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var updateAttemptCommand = new NpgsqlCommand(updateAttemptSql, connection, transaction))
            {
                updateAttemptCommand.Parameters.AddWithValue("batch_id", batchId);
                updateAttemptCommand.Parameters.AddWithValue("event_id", rejectedEvent.Key);
                updateAttemptCommand.Parameters.AddWithValue("reason", reason);
                await updateAttemptCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);

        await MarkPdvSalesByOutboxStatusAsync(rejectedEvents.Keys.ToArray(), "rejected", cancellationToken);
    }

    private async Task MarkPdvSalesByOutboxStatusAsync(
        IReadOnlyCollection<Guid> eventIds,
        string syncStatus,
        CancellationToken cancellationToken)
    {
        if (eventIds.Count == 0)
        {
            return;
        }

        const string sql = """
            UPDATE pdv.sales sales
            SET
                sync_status = @sync_status,
                updated_at_utc = now()
            FROM sync_agent.outbox_events events
            WHERE events.event_id = sales.sale_id
              AND events.event_id = ANY(@event_ids)
              AND events.source_system = 'pdv_local'
              AND events.entity_type = 'venda'
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sync_status", syncStatus);
        command.Parameters.AddWithValue("event_ids", eventIds.ToArray());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ReleaseDispatchedEventsAsync(
        Guid batchId,
        IReadOnlyCollection<OutboxEventRecord> events,
        string classification,
        string errorMessage,
        int? responseStatusCode,
        string? responseBody,
        int maxAttempts,
        TimeSpan initialBackoff,
        TimeSpan maxBackoff,
        CancellationToken cancellationToken)
    {
        if (events.Count == 0)
        {
            return;
        }

        var retryEvents = classification == "permanent_error"
            ? []
            : events.Where(item => item.AttemptCount < maxAttempts).ToArray();

        var deadLetterEvents = classification == "permanent_error"
            ? events.ToArray()
            : events.Where(item => item.AttemptCount >= maxAttempts).ToArray();

        const string updateRetryEventSql = """
            UPDATE sync_agent.outbox_events
            SET
                status = 'pending',
                next_attempt_at_utc = now() + @retry_delay,
                last_error = @error_message,
                updated_at_utc = now()
            WHERE event_id = @event_id
            """;

        const string updateAttemptsSql = """
            UPDATE sync_agent.dispatch_attempts
            SET
                status = @classification,
                completed_at_utc = now(),
                response_status_code = @response_status_code,
                response_body = @response_body,
                error_classification = @classification,
                error_message = @error_message
            WHERE batch_id = @batch_id
              AND event_id = ANY(@event_ids)
              AND status = 'started'
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var updateAttemptsCommand = new NpgsqlCommand(updateAttemptsSql, connection, transaction))
        {
            updateAttemptsCommand.Parameters.AddWithValue("batch_id", batchId);
            updateAttemptsCommand.Parameters.AddWithValue("event_ids", events.Select(item => item.EventId).ToArray());
            updateAttemptsCommand.Parameters.AddWithValue("classification", classification);
            updateAttemptsCommand.Parameters.AddWithValue("response_status_code", (object?)responseStatusCode ?? DBNull.Value);
            updateAttemptsCommand.Parameters.AddWithValue(
                "response_body",
                NpgsqlDbType.Jsonb,
                (object?)ToResponseBodyJson(responseBody) ?? DBNull.Value);
            updateAttemptsCommand.Parameters.AddWithValue("error_message", Truncate(errorMessage, 1000));
            await updateAttemptsCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var retryEvent in retryEvents)
        {
            await using var updateRetryCommand = new NpgsqlCommand(updateRetryEventSql, connection, transaction);
            updateRetryCommand.Parameters.AddWithValue("event_id", retryEvent.EventId);
            updateRetryCommand.Parameters.AddWithValue(
                "retry_delay",
                CalculateBackoff(retryEvent.AttemptCount, initialBackoff, maxBackoff));
            updateRetryCommand.Parameters.AddWithValue("error_message", Truncate(errorMessage, 1000));
            await updateRetryCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var deadLetterEvent in deadLetterEvents)
        {
            await MoveEventToDeadLetterAsync(
                connection,
                transaction,
                deadLetterEvent.EventId,
                Truncate(errorMessage, 1000),
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task UpdateDispatchResultAsync(
        Guid batchId,
        IReadOnlyCollection<Guid> eventIds,
        string updateEventsSql,
        string updateAttemptsSql,
        DispatchFailureParameters? failure,
        CancellationToken cancellationToken)
    {
        if (eventIds.Count == 0)
        {
            return;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var updateEventsCommand = new NpgsqlCommand(updateEventsSql, connection, transaction))
        {
            updateEventsCommand.Parameters.AddWithValue("event_ids", eventIds.ToArray());
            if (failure is not null)
            {
                updateEventsCommand.Parameters.AddWithValue("retry_delay", failure.RetryDelay);
                updateEventsCommand.Parameters.AddWithValue("error_message", failure.ErrorMessage);
            }

            await updateEventsCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var updateAttemptsCommand = new NpgsqlCommand(updateAttemptsSql, connection, transaction))
        {
            updateAttemptsCommand.Parameters.AddWithValue("batch_id", batchId);
            updateAttemptsCommand.Parameters.AddWithValue("event_ids", eventIds.ToArray());
            if (failure is not null)
            {
                updateAttemptsCommand.Parameters.AddWithValue("classification", failure.Classification);
                updateAttemptsCommand.Parameters.AddWithValue("error_message", failure.ErrorMessage);
            }

            await updateAttemptsCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static void ValidateDraft(OutboxEventDraft draft)
    {
        if (draft.EventId == Guid.Empty)
        {
            throw new ArgumentException("EventId is required.", nameof(draft));
        }

        ValidateAllowedValue(draft.SourceSystem, SyncContractValues.SourceSystems, nameof(draft.SourceSystem));
        ValidateAllowedValue(draft.EntityType, SyncContractValues.EntityTypes, nameof(draft.EntityType));
        ValidateAllowedValue(draft.EventType, SyncContractValues.EventTypes, nameof(draft.EventType));

        if (string.IsNullOrWhiteSpace(draft.SourceInstanceId))
        {
            throw new ArgumentException("SourceInstanceId is required.", nameof(draft));
        }

        if (string.IsNullOrWhiteSpace(draft.EntityKey))
        {
            throw new ArgumentException("EntityKey is required.", nameof(draft));
        }

        if (string.IsNullOrWhiteSpace(draft.SchemaVersion))
        {
            throw new ArgumentException("SchemaVersion is required.", nameof(draft));
        }
    }

    private static void ValidateAllowedValue(string value, IReadOnlyCollection<string> allowedValues, string fieldName)
    {
        if (!allowedValues.Contains(value, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"{fieldName} '{value}' is not allowed by sync contract v1.",
                fieldName);
        }
    }

    private static string ToCanonicalJson(JsonObject payload)
    {
        var normalizedPayload = NormalizeJsonNode(payload);

        return JsonSerializer.Serialize(normalizedPayload, new JsonSerializerOptions
        {
            WriteIndented = false
        });
    }

    private static string NormalizeOperatorRole(string role)
    {
        return role switch
        {
            "admin" => "admin",
            "supervisor" => "supervisor",
            "operator" => "operator",
            _ => "operator"
        };
    }

    private static string NormalizePaymentKind(string kind)
    {
        return kind switch
        {
            "cash" or "card" or "pix" or "voucher" or "credit" or "other" => kind,
            _ => "other"
        };
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.Select(NormalizeOptionalText).FirstOrDefault(value => value is not null) ?? string.Empty;
    }

    private static string? NormalizeOptionalText(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static JsonNode? NormalizeJsonNode(JsonNode? node)
    {
        if (node is JsonObject jsonObject)
        {
            var normalizedObject = new JsonObject();

            foreach (var property in jsonObject.OrderBy(property => property.Key, StringComparer.Ordinal))
            {
                normalizedObject[property.Key] = NormalizeJsonNode(property.Value?.DeepClone());
            }

            return normalizedObject;
        }

        if (node is JsonArray jsonArray)
        {
            var normalizedArray = new JsonArray();

            foreach (var item in jsonArray)
            {
                normalizedArray.Add(NormalizeJsonNode(item?.DeepClone()));
            }

            return normalizedArray;
        }

        return node?.DeepClone();
    }

    private static string ComputeSha256Hash(string payloadJson)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson));
        return $"sha256:{Convert.ToHexString(bytes).ToLowerInvariant()}";
    }

    private static string AggregateHash(string joinedPayloadHashes)
    {
        var normalized = string.IsNullOrEmpty(joinedPayloadHashes)
            ? []
            : joinedPayloadHashes.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var builder = new StringBuilder();
        foreach (var payloadHash in normalized)
        {
            builder.Append(payloadHash).Append('\n');
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return $"sha256:{Convert.ToHexString(bytes).ToLowerInvariant()}";
    }

    private static TimeSpan CalculateBackoff(int attemptCount, TimeSpan initialBackoff, TimeSpan maxBackoff)
    {
        var exponent = Math.Min(Math.Max(0, attemptCount - 1), 20);
        var seconds = initialBackoff.TotalSeconds * Math.Pow(2, exponent);
        return TimeSpan.FromSeconds(Math.Min(seconds, maxBackoff.TotalSeconds));
    }

    private static string? ToResponseBodyJson(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        return JsonSerializer.Serialize(new JsonObject
        {
            ["body"] = Truncate(responseBody, 4000)
        });
    }

    private static async Task MoveEventToDeadLetterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid eventId,
        string reason,
        CancellationToken cancellationToken)
    {
        const string insertDeadLetterSql = """
            INSERT INTO sync_agent.dead_letter_events (
                event_id,
                source_instance_id,
                source_system,
                entity_type,
                entity_key,
                event_type,
                occurred_at_utc,
                captured_at_utc,
                schema_version,
                payload,
                payload_hash,
                trace_id,
                attempt_count,
                reason
            )
            SELECT
                event_id,
                source_instance_id,
                source_system,
                entity_type,
                entity_key,
                event_type,
                occurred_at_utc,
                captured_at_utc,
                schema_version,
                payload,
                payload_hash,
                trace_id,
                attempt_count,
                @reason
            FROM sync_agent.outbox_events
            WHERE event_id = @event_id
            ON CONFLICT (event_id) DO UPDATE
            SET
                attempt_count = EXCLUDED.attempt_count,
                reason = EXCLUDED.reason,
                failed_at_utc = now()
            """;

        const string updateOutboxSql = """
            UPDATE sync_agent.outbox_events
            SET
                status = 'dead_letter',
                last_error = @reason,
                updated_at_utc = now()
            WHERE event_id = @event_id
            """;

        await using (var insertCommand = new NpgsqlCommand(insertDeadLetterSql, connection, transaction))
        {
            insertCommand.Parameters.AddWithValue("event_id", eventId);
            insertCommand.Parameters.AddWithValue("reason", reason);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var updateCommand = new NpgsqlCommand(updateOutboxSql, connection, transaction))
        {
            updateCommand.Parameters.AddWithValue("event_id", eventId);
            updateCommand.Parameters.AddWithValue("reason", reason);
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength
            ? value
            : value[..maxLength];
    }

    private static DateTimeOffset? ParseOptionalDateTimeOffset(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    }

    private static bool? ParseOptionalBoolean(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : bool.Parse(value);
    }
}

public sealed record LocalSyncStoreStatus(
    string DatabaseName,
    string PgVectorVersion,
    long PendingOutboxEvents,
    long DeadLetterEvents,
    DateTimeOffset? LastHeartbeatAtUtc,
    bool? LastHeartbeatSucceeded,
    string? LastHeartbeatConnectivity,
    int OldestPendingAgeSeconds,
    Guid? LastReconciliationId,
    string? LastReconciliationStatus,
    DateTimeOffset? LastReconciliationCompletedAtUtc,
    JsonNode LastReconciliationSummary,
    JsonNode PdvSalesSummary);

public sealed record LocalTaskLogEntry(
    DateTimeOffset OccurredAtUtc,
    string TaskType,
    string Title,
    string Status,
    JsonNode Details);

public sealed record ReconciliationRunResult(
    Guid ReconciliationId,
    string Status,
    JsonNode Summary);

public sealed record ReconciliationSummaryPayload(
    Guid ReconciliationId,
    string SourceInstanceId,
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<ReconciliationEntitySummary> Entities);

public sealed record ReconciliationEntitySummary(
    string EntityType,
    long Captured,
    long Accepted,
    long Rejected,
    long DeadLetter,
    string AggregateHash);

public sealed record OutboxEnqueueResult(
    Guid EventId,
    string PayloadHash,
    bool Inserted);

public sealed record PdvSalePendingPublishRecord(
    Guid SaleId,
    string SaleNumber,
    Guid CashSessionId,
    Guid OperatorId,
    string? OperatorExternalId,
    Guid? CustomerId,
    DateTimeOffset OccurredAtUtc,
    string Status,
    decimal SubtotalAmount,
    decimal DiscountAmount,
    decimal TotalAmount,
    JsonArray Items,
    JsonArray Payments,
    string? CustomerDocument = null);

public sealed record PdvRejectedSaleRecord(
    Guid SaleId,
    string SaleNumber,
    decimal TotalAmount,
    DateTimeOffset UpdatedAtUtc,
    string RejectionReason);

public sealed record OutboxEventRecord(
    Guid EventId,
    string SourceInstanceId,
    string SourceSystem,
    string EntityType,
    string EntityKey,
    string EventType,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset CapturedAtUtc,
    string SchemaVersion,
    string PayloadJson,
    string PayloadHash,
    string? TraceId,
    int AttemptCount);

internal sealed record DispatchFailureParameters(
    string ErrorMessage,
    string Classification,
    TimeSpan RetryDelay);
