using Npgsql;

namespace PdvLocal.Core;

public sealed class PdvDatabaseStatusReader
{
    private readonly string _connectionString;

    public PdvDatabaseStatusReader(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<PdvDatabaseStatus> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            const string sql = """
                SELECT
                    current_database(),
                    current_user,
                    current_setting('TimeZone'),
                    COALESCE((SELECT extversion FROM pg_extension WHERE extname = 'vector'), ''),
                    EXISTS (SELECT 1 FROM information_schema.schemata WHERE schema_name = 'pdv'),
                    (
                        SELECT count(*)
                        FROM information_schema.tables
                        WHERE table_schema = 'pdv'
                          AND table_type = 'BASE TABLE'
                    ),
                    CASE
                        WHEN EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'pdv' AND table_name = 'operators')
                        THEN (SELECT count(*) FROM pdv.operators)
                        ELSE 0
                    END,
                    CASE
                        WHEN EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'pdv' AND table_name = 'products')
                        THEN (SELECT count(*) FROM pdv.products)
                        ELSE 0
                    END,
                    CASE
                        WHEN EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'pdv' AND table_name = 'sales')
                        THEN (SELECT count(*) FROM pdv.sales)
                        ELSE 0
                    END,
                    CASE
                        WHEN EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'pdv' AND table_name = 'cash_sessions')
                        THEN (SELECT count(*) FROM pdv.cash_sessions WHERE status = 'open')
                        ELSE 0
                    END
                """;

            await using var command = new NpgsqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                return PdvDatabaseStatus.Unavailable("Consulta de status do banco local nao retornou dados.");
            }

            return new PdvDatabaseStatus(
                IsAvailable: true,
                DatabaseName: reader.GetString(0),
                UserName: reader.GetString(1),
                TimeZone: reader.GetString(2),
                PgVectorVersion: EmptyToNull(reader.GetString(3)),
                PdvSchemaExists: reader.GetBoolean(4),
                PdvTableCount: reader.GetInt64(5),
                OperatorCount: reader.GetInt64(6),
                ProductCount: reader.GetInt64(7),
                SaleCount: reader.GetInt64(8),
                OpenCashSessionCount: reader.GetInt64(9),
                Error: null);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            return PdvDatabaseStatus.Unavailable(ex.Message);
        }
    }

    private static string? EmptyToNull(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

public sealed record PdvDatabaseStatus(
    bool IsAvailable,
    string? DatabaseName,
    string? UserName,
    string? TimeZone,
    string? PgVectorVersion,
    bool PdvSchemaExists,
    long PdvTableCount,
    long OperatorCount,
    long ProductCount,
    long SaleCount,
    long OpenCashSessionCount,
    string? Error)
{
    public static PdvDatabaseStatus Unavailable(string error)
    {
        return new PdvDatabaseStatus(false, null, null, null, null, false, 0, 0, 0, 0, 0, error);
    }
}
