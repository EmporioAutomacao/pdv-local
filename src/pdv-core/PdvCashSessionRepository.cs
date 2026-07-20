using Npgsql;

namespace PdvLocal.Core;

public sealed class PdvCashSessionRepository
{
    private readonly string _connectionString;

    public PdvCashSessionRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<Guid> OpenAsync(
        OpenCashSessionCommand command,
        CancellationToken cancellationToken)
    {
        PdvValidation.ValidateOpenCashSession(command);

        const string hasOpenSql = """
            SELECT EXISTS (
                SELECT 1
                FROM pdv.cash_sessions
                WHERE operator_id = @operator_id
                  AND status = 'open'
            )
            """;

        const string insertSql = """
            INSERT INTO pdv.cash_sessions (
                cash_session_id,
                operator_id,
                status,
                opened_at_utc,
                opening_amount,
                notes
            )
            VALUES (
                @cash_session_id,
                @operator_id,
                'open',
                @opened_at_utc,
                @opening_amount,
                @notes
            )
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var hasOpenCommand = new NpgsqlCommand(hasOpenSql, connection, transaction))
        {
            hasOpenCommand.Parameters.AddWithValue("operator_id", command.OperatorId);
            var hasOpen = (bool)(await hasOpenCommand.ExecuteScalarAsync(cancellationToken) ?? false);
            if (hasOpen)
            {
                throw new InvalidOperationException("Operador ja possui caixa aberto.");
            }
        }

        var cashSessionId = Guid.NewGuid();
        await using (var insertCommand = new NpgsqlCommand(insertSql, connection, transaction))
        {
            insertCommand.Parameters.AddWithValue("cash_session_id", cashSessionId);
            insertCommand.Parameters.AddWithValue("operator_id", command.OperatorId);
            insertCommand.Parameters.AddWithValue("opened_at_utc", DateTimeOffset.UtcNow);
            insertCommand.Parameters.AddWithValue("opening_amount", command.OpeningAmount);
            insertCommand.Parameters.AddWithValue("notes", (object?)command.Notes ?? DBNull.Value);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return cashSessionId;
    }

    public async Task<PdvCashSession?> FindOpenByOperatorAsync(
        Guid operatorId,
        CancellationToken cancellationToken)
    {
        if (operatorId == Guid.Empty)
        {
            throw new ArgumentException("Operador e obrigatorio.", nameof(operatorId));
        }

        const string sql = """
            SELECT
                cash_session_id,
                operator_id,
                status,
                opened_at_utc,
                closed_at_utc,
                opening_amount,
                closing_amount,
                notes
            FROM pdv.cash_sessions
            WHERE operator_id = @operator_id
              AND status = 'open'
            ORDER BY opened_at_utc DESC
            LIMIT 1
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("operator_id", operatorId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new PdvCashSession(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetFieldValue<DateTimeOffset>(3),
            reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
            reader.GetDecimal(5),
            reader.IsDBNull(6) ? null : reader.GetDecimal(6),
            reader.IsDBNull(7) ? null : reader.GetString(7));
    }

    public async Task CloseAsync(
        CloseCashSessionCommand command,
        CancellationToken cancellationToken)
    {
        PdvValidation.ValidateCloseCashSession(command);

        const string sql = """
            UPDATE pdv.cash_sessions
            SET
                status = 'closed',
                closed_at_utc = @closed_at_utc,
                closing_amount = @closing_amount,
                closing_counts = @closing_counts,
                notes = COALESCE(@notes, notes),
                updated_at_utc = now()
            WHERE cash_session_id = @cash_session_id
              AND status = 'open'
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var updateCommand = new NpgsqlCommand(sql, connection);
        updateCommand.Parameters.AddWithValue("cash_session_id", command.CashSessionId);
        updateCommand.Parameters.AddWithValue("closed_at_utc", DateTimeOffset.UtcNow);
        updateCommand.Parameters.AddWithValue("closing_amount", command.ClosingAmount);
        updateCommand.Parameters.AddWithValue(
            "closing_counts",
            NpgsqlTypes.NpgsqlDbType.Jsonb,
            (object?)SerializeClosingCounts(command.ClosingCounts) ?? DBNull.Value);
        updateCommand.Parameters.AddWithValue("notes", (object?)command.Notes ?? DBNull.Value);

        var affected = await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        if (affected == 0)
        {
            throw new InvalidOperationException("Caixa aberto nao encontrado.");
        }
    }

    private static string? SerializeClosingCounts(IReadOnlyList<PdvClosingCountEntry>? entries)
    {
        if (entries is null || entries.Count == 0)
        {
            return null;
        }

        return System.Text.Json.JsonSerializer.Serialize(new
        {
            counted_at_utc = DateTimeOffset.UtcNow,
            entries = entries.Select(entry => new
            {
                species_name = entry.SpeciesName,
                kind = entry.Kind,
                counted_amount = entry.CountedAmount,
                expected_amount = entry.ExpectedAmount,
                difference = entry.Difference
            })
        });
    }
}
