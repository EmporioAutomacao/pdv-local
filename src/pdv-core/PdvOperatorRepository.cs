using Npgsql;

namespace PdvLocal.Core;

public sealed class PdvOperatorRepository
{
    private readonly string _connectionString;

    public PdvOperatorRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<PdvOperator?> FindActiveByLoginAsync(
        string login,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(login))
        {
            throw new ArgumentException("Login e obrigatorio.", nameof(login));
        }

        const string sql = """
            SELECT
                operator_id,
                COALESCE(external_operator_id, ''),
                login,
                display_name,
                role,
                active
            FROM pdv.operators
            WHERE login = @login
              AND active = true
            LIMIT 1
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("login", login);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new PdvOperator(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetBoolean(5));
    }
}
