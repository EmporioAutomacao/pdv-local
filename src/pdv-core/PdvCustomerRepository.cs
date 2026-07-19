using Npgsql;

namespace PdvLocal.Core;

public sealed class PdvCustomerRepository
{
    private readonly string _connectionString;

    public PdvCustomerRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <summary>
    /// Busca um cliente ativo pelo CPF/CNPJ (somente digitos). A comparacao
    /// ignora a pontuacao gravada em pdv.customers.document.
    /// </summary>
    public async Task<PdvCustomer?> FindActiveByDocumentAsync(
        string documentDigits,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT customer_id, name, document, external_key
            FROM pdv.customers
            WHERE active
              AND document IS NOT NULL
              AND regexp_replace(document, '\D', '', 'g') = @document
            ORDER BY updated_at_utc DESC
            LIMIT 1
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("document", documentDigits);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadCustomer(reader);
    }

    /// <summary>
    /// Busca um cliente ativo pelo codigo interno do ERP (external_key exato).
    /// </summary>
    public async Task<PdvCustomer?> FindActiveByCodeAsync(
        string externalKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(externalKey))
        {
            return null;
        }

        const string sql = """
            SELECT customer_id, name, document, external_key
            FROM pdv.customers
            WHERE active
              AND external_key = @external_key
            ORDER BY updated_at_utc DESC
            LIMIT 1
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("external_key", externalKey.Trim());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadCustomer(reader);
    }

    /// <summary>
    /// Busca clientes ativos por nome (parcial), documento (digitos) ou codigo
    /// interno (exato), com matches exatos primeiro.
    /// </summary>
    public async Task<IReadOnlyList<PdvCustomer>> SearchActiveAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        if (limit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Limite deve ser maior que zero.");
        }

        const string sql = """
            SELECT customer_id, name, document, external_key
            FROM pdv.customers
            WHERE active
              AND (
                    external_key = @query
                 OR (document IS NOT NULL AND regexp_replace(document, '\D', '', 'g') = @query_digits)
                 OR name ILIKE @name_pattern
              )
            ORDER BY
                CASE
                    WHEN external_key = @query THEN 0
                    WHEN document IS NOT NULL AND regexp_replace(document, '\D', '', 'g') = @query_digits THEN 1
                    WHEN lower(name) = @query_lower THEN 2
                    ELSE 3
                END,
                name
            LIMIT @limit
            """;

        var normalizedQuery = query.Trim();
        var digits = new string(normalizedQuery.Where(char.IsDigit).ToArray());

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("query", normalizedQuery);
        command.Parameters.AddWithValue("query_digits", digits.Length > 0 ? digits : normalizedQuery.ToLowerInvariant());
        command.Parameters.AddWithValue("query_lower", normalizedQuery.ToLowerInvariant());
        command.Parameters.AddWithValue("name_pattern", $"%{EscapeLike(normalizedQuery)}%");
        command.Parameters.AddWithValue("limit", limit);

        var customers = new List<PdvCustomer>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            customers.Add(ReadCustomer(reader));
        }

        return customers;
    }

    private static PdvCustomer ReadCustomer(NpgsqlDataReader reader)
    {
        return new PdvCustomer(
            CustomerId: reader.GetGuid(0),
            Name: reader.GetString(1),
            Document: reader.IsDBNull(2) ? null : reader.GetString(2),
            ExternalKey: reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    private static string EscapeLike(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }
}
