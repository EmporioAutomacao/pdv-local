using System.Security.Cryptography;
using System.Text;
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

    public async Task<PdvOperator?> AuthenticateAsync(
        string login,
        string password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrEmpty(password))
        {
            return null;
        }

        const string sql = """
            SELECT
                operator_id,
                COALESCE(external_operator_id, ''),
                login,
                display_name,
                role,
                active,
                password_hash
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

        var storedHash = reader.GetString(6);
        if (string.IsNullOrEmpty(storedHash) || !VerifyDjangoPbkdf2(password, storedHash))
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

    // Verifica hash no formato Django: pbkdf2_sha256$<iterations>$<salt>$<hash_base64>
    // O salt eh uma string UTF-8 (nao base64); o hash eh base64 do resultado PBKDF2-SHA256.
    private static bool VerifyDjangoPbkdf2(string password, string storedHash)
    {
        var parts = storedHash.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2_sha256")
        {
            return false;
        }

        if (!int.TryParse(parts[1], out var iterations) || iterations <= 0)
        {
            return false;
        }

        var salt = Encoding.UTF8.GetBytes(parts[2]);
        byte[] expectedHash;
        try
        {
            expectedHash = Convert.FromBase64String(parts[3]);
        }
        catch
        {
            return false;
        }

        var passwordBytes = Encoding.UTF8.GetBytes(password);
        using var deriveBytes = new Rfc2898DeriveBytes(
            passwordBytes, salt, iterations, HashAlgorithmName.SHA256);
        var actualHash = deriveBytes.GetBytes(expectedHash.Length);

        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }
}
