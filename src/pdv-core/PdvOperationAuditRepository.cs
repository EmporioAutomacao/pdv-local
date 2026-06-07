using Npgsql;
using NpgsqlTypes;
using System.Text.Json;

namespace PdvLocal.Core;

public sealed class PdvOperationAuditRepository
{
    private readonly string _connectionString;

    public PdvOperationAuditRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS pdv.operation_audits (
                audit_id uuid PRIMARY KEY,
                operation_type text NOT NULL,
                cash_session_id uuid NULL REFERENCES pdv.cash_sessions (cash_session_id),
                sale_id uuid NULL REFERENCES pdv.sales (sale_id) ON DELETE SET NULL,
                operator_id uuid NOT NULL REFERENCES pdv.operators (operator_id),
                supervisor_operator_id uuid NOT NULL REFERENCES pdv.operators (operator_id),
                reason text NULL,
                payload jsonb NOT NULL DEFAULT '{}'::jsonb,
                occurred_at_utc timestamptz NOT NULL DEFAULT now(),
                created_at_utc timestamptz NOT NULL DEFAULT now()
            );

            CREATE INDEX IF NOT EXISTS ix_operation_audits_occurred_at
                ON pdv.operation_audits (occurred_at_utc DESC);

            CREATE INDEX IF NOT EXISTS ix_operation_audits_operation_type
                ON pdv.operation_audits (operation_type, occurred_at_utc DESC);

            CREATE INDEX IF NOT EXISTS ix_operation_audits_sale
                ON pdv.operation_audits (sale_id)
                WHERE sale_id IS NOT NULL;
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<Guid> InsertAsync(
        PdvOperationAuditCommand audit,
        CancellationToken cancellationToken)
    {
        PdvValidation.ValidateOperationAudit(audit);
        var auditId = Guid.NewGuid();
        var payload = NormalizePayload(audit.PayloadJson);

        const string sql = """
            INSERT INTO pdv.operation_audits (
                audit_id,
                operation_type,
                cash_session_id,
                sale_id,
                operator_id,
                supervisor_operator_id,
                reason,
                payload,
                occurred_at_utc
            )
            VALUES (
                @audit_id,
                @operation_type,
                @cash_session_id,
                @sale_id,
                @operator_id,
                @supervisor_operator_id,
                @reason,
                @payload,
                @occurred_at_utc
            )
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("audit_id", auditId);
        command.Parameters.AddWithValue("operation_type", audit.OperationType.Trim());
        command.Parameters.AddWithValue("cash_session_id", (object?)audit.CashSessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("sale_id", (object?)audit.SaleId ?? DBNull.Value);
        command.Parameters.AddWithValue("operator_id", audit.OperatorId);
        command.Parameters.AddWithValue("supervisor_operator_id", audit.SupervisorOperatorId);
        command.Parameters.AddWithValue("reason", (object?)audit.Reason?.Trim() ?? DBNull.Value);
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payload);
        command.Parameters.AddWithValue("occurred_at_utc", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return auditId;
    }

    public static string NormalizePayload(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return "{}";
        }

        using var document = JsonDocument.Parse(payloadJson);
        return JsonSerializer.Serialize(document.RootElement);
    }
}
