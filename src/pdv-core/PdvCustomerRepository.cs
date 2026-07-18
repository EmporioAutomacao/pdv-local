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
            SELECT customer_id, name, document
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

        return new PdvCustomer(
            CustomerId: reader.GetGuid(0),
            Name: reader.GetString(1),
            Document: reader.IsDBNull(2) ? null : reader.GetString(2));
    }
}
