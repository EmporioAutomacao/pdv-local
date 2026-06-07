using Npgsql;
using NpgsqlTypes;
using System.Text.Json;

namespace PdvLocal.Core;

public sealed class PdvCashMovementRepository
{
    private readonly string _connectionString;

    public PdvCashMovementRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE SCHEMA IF NOT EXISTS pdv;

            CREATE TABLE IF NOT EXISTS pdv.cash_movements (
                movement_id uuid PRIMARY KEY,
                cash_session_id uuid NOT NULL REFERENCES pdv.cash_sessions (cash_session_id),
                operator_id uuid NOT NULL REFERENCES pdv.operators (operator_id),
                supervisor_operator_id uuid NOT NULL REFERENCES pdv.operators (operator_id),
                movement_type text NOT NULL,
                amount numeric(14, 2) NOT NULL,
                reason text NOT NULL,
                occurred_at_utc timestamptz NOT NULL DEFAULT now(),
                created_at_utc timestamptz NOT NULL DEFAULT now(),
                CONSTRAINT cash_movements_type_check CHECK (movement_type IN ('supply', 'withdrawal')),
                CONSTRAINT cash_movements_amount_check CHECK (amount > 0)
            );

            CREATE INDEX IF NOT EXISTS ix_cash_movements_session
                ON pdv.cash_movements (cash_session_id, occurred_at_utc DESC);

            CREATE INDEX IF NOT EXISTS ix_cash_movements_type
                ON pdv.cash_movements (movement_type, occurred_at_utc DESC);
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<Guid> InsertAsync(
        PdvCashMovementCommand movement,
        CancellationToken cancellationToken)
    {
        PdvValidation.ValidateCashMovement(movement);

        var movementId = Guid.NewGuid();
        var occurredAt = DateTimeOffset.UtcNow;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await EnsureOpenCashSessionAsync(connection, transaction, movement, cancellationToken);
        await InsertMovementAsync(connection, transaction, movementId, occurredAt, movement, cancellationToken);
        await InsertAuditAsync(connection, transaction, movementId, occurredAt, movement, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return movementId;
    }

    public async Task<PdvCashSessionSummary> GetSummaryAsync(
        Guid cashSessionId,
        CancellationToken cancellationToken)
    {
        if (cashSessionId == Guid.Empty)
        {
            throw new ArgumentException("Caixa e obrigatorio.", nameof(cashSessionId));
        }

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var openingAmount = await ReadOpeningAmountAsync(connection, cashSessionId, cancellationToken);
        var paymentSpecies = await ReadPaymentSpeciesAsync(connection, cashSessionId, cancellationToken);
        var (supplyAmount, withdrawalAmount) = await ReadMovementTotalsAsync(connection, cashSessionId, cancellationToken);
        var (saleCount, totalSalesAmount) = await ReadSaleTotalsAsync(connection, cashSessionId, cancellationToken);

        var cashSalesAmount = paymentSpecies
            .Where(item => item.Kind.Equals("cash", StringComparison.Ordinal))
            .Sum(item => item.Amount);
        var expectedCashAmount = openingAmount + cashSalesAmount + supplyAmount - withdrawalAmount;

        return new PdvCashSessionSummary(
            cashSessionId,
            openingAmount,
            cashSalesAmount,
            supplyAmount,
            withdrawalAmount,
            expectedCashAmount,
            totalSalesAmount,
            saleCount,
            paymentSpecies);
    }

    private static async Task EnsureOpenCashSessionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PdvCashMovementCommand movement,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1
                FROM pdv.cash_sessions
                WHERE cash_session_id = @cash_session_id
                  AND operator_id = @operator_id
                  AND status = 'open'
            )
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("cash_session_id", movement.CashSessionId);
        command.Parameters.AddWithValue("operator_id", movement.OperatorId);

        var exists = (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
        if (!exists)
        {
            throw new InvalidOperationException("Caixa aberto nao encontrado para este operador.");
        }
    }

    private static async Task InsertMovementAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid movementId,
        DateTimeOffset occurredAt,
        PdvCashMovementCommand movement,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO pdv.cash_movements (
                movement_id,
                cash_session_id,
                operator_id,
                supervisor_operator_id,
                movement_type,
                amount,
                reason,
                occurred_at_utc
            )
            VALUES (
                @movement_id,
                @cash_session_id,
                @operator_id,
                @supervisor_operator_id,
                @movement_type,
                @amount,
                @reason,
                @occurred_at_utc
            )
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("movement_id", movementId);
        command.Parameters.AddWithValue("cash_session_id", movement.CashSessionId);
        command.Parameters.AddWithValue("operator_id", movement.OperatorId);
        command.Parameters.AddWithValue("supervisor_operator_id", movement.SupervisorOperatorId);
        command.Parameters.AddWithValue("movement_type", movement.MovementType);
        command.Parameters.AddWithValue("amount", movement.Amount);
        command.Parameters.AddWithValue("reason", movement.Reason.Trim());
        command.Parameters.AddWithValue("occurred_at_utc", occurredAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid movementId,
        DateTimeOffset occurredAt,
        PdvCashMovementCommand movement,
        CancellationToken cancellationToken)
    {
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
                NULL,
                @operator_id,
                @supervisor_operator_id,
                @reason,
                @payload,
                @occurred_at_utc
            )
            """;

        var payload = JsonSerializer.Serialize(new
        {
            movement_id = movementId,
            movement_type = movement.MovementType,
            amount = movement.Amount
        });

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("audit_id", Guid.NewGuid());
        command.Parameters.AddWithValue("operation_type", GetAuditOperationType(movement.MovementType));
        command.Parameters.AddWithValue("cash_session_id", movement.CashSessionId);
        command.Parameters.AddWithValue("operator_id", movement.OperatorId);
        command.Parameters.AddWithValue("supervisor_operator_id", movement.SupervisorOperatorId);
        command.Parameters.AddWithValue("reason", movement.Reason.Trim());
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payload);
        command.Parameters.AddWithValue("occurred_at_utc", occurredAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<decimal> ReadOpeningAmountAsync(
        NpgsqlConnection connection,
        Guid cashSessionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT opening_amount
            FROM pdv.cash_sessions
            WHERE cash_session_id = @cash_session_id
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("cash_session_id", cashSessionId);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null)
        {
            throw new InvalidOperationException("Caixa nao encontrado.");
        }

        return (decimal)value;
    }

    private static async Task<IReadOnlyList<PdvPaymentSpeciesSummary>> ReadPaymentSpeciesAsync(
        NpgsqlConnection connection,
        Guid cashSessionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                COALESCE(ps.name, NULLIF(p.payment_method, ''), 'sem especie') AS species_name,
                COALESCE(
                    NULLIF(p.payload ->> 'payment_species_kind', ''),
                    ps.kind,
                    CASE
                        WHEN lower(p.payment_method) LIKE '%dinheiro%' THEN 'cash'
                        WHEN lower(p.payment_method) LIKE '%pix%' THEN 'pix'
                        WHEN lower(p.payment_method) LIKE '%cart%' OR lower(p.payment_method) LIKE '%tef%' THEN 'card'
                        ELSE 'other'
                    END
                ) AS kind,
                COALESCE(SUM(p.amount), 0) AS amount
            FROM pdv.payments p
            INNER JOIN pdv.sales s
                ON s.sale_id = p.sale_id
            LEFT JOIN pdv.payment_species ps
                ON ps.payment_species_id::text = p.payload ->> 'payment_species_id'
            WHERE s.cash_session_id = @cash_session_id
              AND s.status = 'completed'
              AND p.status = 'captured'
            GROUP BY 1, 2
            ORDER BY species_name
            """;

        var summaries = new List<PdvPaymentSpeciesSummary>();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("cash_session_id", cashSessionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            summaries.Add(new PdvPaymentSpeciesSummary(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetDecimal(2)));
        }

        return summaries;
    }

    private static async Task<(decimal SupplyAmount, decimal WithdrawalAmount)> ReadMovementTotalsAsync(
        NpgsqlConnection connection,
        Guid cashSessionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                COALESCE(SUM(amount) FILTER (WHERE movement_type = 'supply'), 0) AS supply_amount,
                COALESCE(SUM(amount) FILTER (WHERE movement_type = 'withdrawal'), 0) AS withdrawal_amount
            FROM pdv.cash_movements
            WHERE cash_session_id = @cash_session_id
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("cash_session_id", cashSessionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (0, 0);
        }

        return (reader.GetDecimal(0), reader.GetDecimal(1));
    }

    private static async Task<(long SaleCount, decimal TotalSalesAmount)> ReadSaleTotalsAsync(
        NpgsqlConnection connection,
        Guid cashSessionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                COUNT(*) AS sale_count,
                COALESCE(SUM(total_amount), 0) AS total_sales_amount
            FROM pdv.sales
            WHERE cash_session_id = @cash_session_id
              AND status = 'completed'
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("cash_session_id", cashSessionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (0, 0);
        }

        return (reader.GetInt64(0), reader.GetDecimal(1));
    }

    private static string GetAuditOperationType(string movementType)
    {
        return movementType switch
        {
            "supply" => "cash_supply_recorded",
            "withdrawal" => "cash_withdrawal_recorded",
            _ => "cash_movement_recorded"
        };
    }
}
