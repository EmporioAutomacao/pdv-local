using Npgsql;

namespace PdvLocal.Core;

public sealed class PdvPaymentCatalogRepository
{
    private readonly string _connectionString;

    public PdvPaymentCatalogRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<IReadOnlyList<PdvPaymentSpecies>> GetActiveSpeciesAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                payment_species_id,
                source_system,
                external_key,
                name,
                kind,
                requires_tef,
                allows_change,
                active
            FROM pdv.payment_species
            WHERE active = true
            ORDER BY
                CASE kind
                    WHEN 'cash' THEN 0
                    WHEN 'pix' THEN 1
                    WHEN 'card' THEN 2
                    WHEN 'voucher' THEN 3
                    WHEN 'credit' THEN 4
                    ELSE 5
                END,
                name
            """;

        var items = new List<PdvPaymentSpecies>();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new PdvPaymentSpecies(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetBoolean(5),
                reader.GetBoolean(6),
                reader.GetBoolean(7)));
        }

        return items;
    }

    public async Task<IReadOnlyList<PdvPaymentCondition>> GetActiveConditionsAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                payment_condition_id,
                source_system,
                external_key,
                name,
                installments,
                first_due_days,
                interval_days,
                active
            FROM pdv.payment_conditions
            WHERE active = true
            ORDER BY installments, first_due_days, interval_days, name
            """;

        var items = new List<PdvPaymentCondition>();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new PdvPaymentCondition(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetBoolean(7)));
        }

        return items;
    }
}
