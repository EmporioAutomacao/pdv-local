using System.Reflection;
using System.Text.RegularExpressions;
using Npgsql;

namespace SyncAgent.Provisioning;

/// <summary>
/// Executa DDL de preparacao do Arpa (schema/views <c>sync_export</c> e usuario
/// read-only) a partir da aba Configuracoes &gt; Arpa. Usa uma credencial DBA
/// **transitoria** informada no formulario - nunca gravada, nunca logada.
/// </summary>
public sealed partial class ArpaDdlRunner
{
    private const string ViewsResourceName = "SyncAgent.Arpa.sync-export-views.sql";
    private static readonly string[] AllViewNames = ["produtos", "clientes", "estoque", "vendas", "financeiro"];

    private readonly ILogger<ArpaDdlRunner> _logger;

    public ArpaDdlRunner(ILogger<ArpaDdlRunner> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Script unico e generico (<c>infra/arpa/sync-export-views.sql</c>, embutido)
    /// que introspecta o schema real do Arpa e cria as views sync_export.
    /// </summary>
    private static string LoadViewsScript()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ViewsResourceName)
            ?? throw new InvalidOperationException($"Recurso embutido nao encontrado: {ViewsResourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public async Task<ArpaTestResult> TestConnectionAsync(
        string host, int port, string database, string username, string password,
        bool needProdutos, bool needClientes, bool needEstoque, bool needVendas, bool needFinanceiro,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var dataSource = NpgsqlDataSource.Create(BuildConnectionString(host, port, database, username, password));
            await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);

            var views = new Dictionary<string, bool>();
            foreach (var (view, needed) in new[]
            {
                ("produtos", needProdutos), ("clientes", needClientes), ("estoque", needEstoque),
                ("vendas", needVendas), ("financeiro", needFinanceiro),
            })
            {
                if (!needed)
                {
                    continue;
                }

                await using var cmd = new NpgsqlCommand("SELECT to_regclass(@v) IS NOT NULL", conn);
                cmd.Parameters.AddWithValue("v", $"sync_export.{view}");
                views[view] = (bool)(await cmd.ExecuteScalarAsync(cancellationToken) ?? false);
            }

            var missing = views.Where(v => !v.Value).Select(v => $"sync_export.{v.Key}").ToList();
            return missing.Count == 0
                ? new ArpaTestResult(true, "Conexao OK. Views necessarias presentes.", views)
                : new ArpaTestResult(true, $"Conexao OK, mas faltam views: {string.Join(", ", missing)}. Use \"Preparar views\".", views);
        }
        catch (Exception ex)
        {
            return new ArpaTestResult(false, Describe(ex), new Dictionary<string, bool>());
        }
    }

    /// <summary>
    /// Roda o script unico de views (introspectivo) e, se <paramref name="runtimeUser"/>
    /// for um identificador valido, concede leitura das views a ele - uma passada
    /// = views + permissao. Requer credencial DBA transitoria.
    /// </summary>
    public async Task<ArpaDdlResult> PrepareViewsAsync(
        string host, int port, string database, string dbaUser, string dbaPassword,
        string? runtimeUser,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var dataSource = NpgsqlDataSource.Create(BuildConnectionString(host, port, database, dbaUser, dbaPassword));
            await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);

            await using (var cmd = new NpgsqlCommand(LoadViewsScript(), conn) { CommandTimeout = 120 })
            {
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // GRANT para o usuario runtime da conexao (o script nao o conhece).
            var grantedTo = string.Empty;
            if (!string.IsNullOrWhiteSpace(runtimeUser) && RoleNameRegex().IsMatch(runtimeUser))
            {
                var grants = $"""
                    GRANT USAGE ON SCHEMA sync_export TO {runtimeUser};
                    GRANT SELECT ON ALL TABLES IN SCHEMA sync_export TO {runtimeUser};
                    ALTER DEFAULT PRIVILEGES IN SCHEMA sync_export GRANT SELECT ON TABLES TO {runtimeUser};
                    """;
                await using var grantCmd = new NpgsqlCommand(grants, conn);
                await grantCmd.ExecuteNonQueryAsync(cancellationToken);
                grantedTo = $" Leitura concedida a '{runtimeUser}'.";
            }

            var created = await ListExistingViewsAsync(conn, cancellationToken);
            var createdList = created.Count > 0 ? string.Join(", ", created) : "(nenhuma)";
            var missing = AllViewNames.Where(v => !created.Contains(v)).ToList();
            var missingNote = missing.Count == 0 ? string.Empty
                : $" Nao criadas: {string.Join(", ", missing)} (schema do Arpa nao tem as tabelas — deixe esses toggles desmarcados).";

            return new ArpaDdlResult(
                created.Contains("produtos") && created.Contains("clientes"),
                $"Views sync_export: {createdList}.{grantedTo}{missingNote} Rode \"Testar conexao\".");
        }
        catch (Exception ex)
        {
            return new ArpaDdlResult(false, Describe(ex));
        }
    }

    private static async Task<HashSet<string>> ListExistingViewsAsync(NpgsqlConnection conn, CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = new NpgsqlCommand(
            "SELECT table_name FROM information_schema.views WHERE table_schema = 'sync_export'", conn);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    public async Task<ArpaDdlResult> CreateReadonlyUserAsync(
        string host, int port, string database, string dbaUser, string dbaPassword,
        string roleName, string rolePassword,
        CancellationToken cancellationToken)
    {
        if (!RoleNameRegex().IsMatch(roleName))
        {
            return new ArpaDdlResult(false, "Nome de usuario invalido. Use letras, numeros e _ (comecando por letra ou _).");
        }

        if (string.IsNullOrEmpty(rolePassword))
        {
            return new ArpaDdlResult(false, "Informe a senha do novo usuario read-only.");
        }

        var pwdLiteral = "'" + rolePassword.Replace("'", "''") + "'";
        var dbIdent = "\"" + database.Replace("\"", "\"\"") + "\"";

        try
        {
            await using var dataSource = NpgsqlDataSource.Create(BuildConnectionString(host, port, database, dbaUser, dbaPassword));
            await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);

            try
            {
                await using var create = new NpgsqlCommand(
                    $"CREATE ROLE {roleName} LOGIN PASSWORD {pwdLiteral} NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION",
                    conn);
                await create.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (PostgresException ex) when (ex.SqlState == "42710")
            {
                await using var alter = new NpgsqlCommand(
                    $"ALTER ROLE {roleName} WITH LOGIN PASSWORD {pwdLiteral}",
                    conn);
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }

            var grants = $"""
                CREATE SCHEMA IF NOT EXISTS sync_export;
                GRANT CONNECT ON DATABASE {dbIdent} TO {roleName};
                GRANT USAGE ON SCHEMA sync_export TO {roleName};
                GRANT SELECT ON ALL TABLES IN SCHEMA sync_export TO {roleName};
                ALTER DEFAULT PRIVILEGES IN SCHEMA sync_export GRANT SELECT ON TABLES TO {roleName};
                REVOKE CREATE ON SCHEMA sync_export FROM {roleName};
                REVOKE CREATE ON SCHEMA public FROM {roleName};
                """;
            await using var grantCmd = new NpgsqlCommand(grants, conn);
            await grantCmd.ExecuteNonQueryAsync(cancellationToken);

            return new ArpaDdlResult(true, $"Usuario '{roleName}' criado/atualizado com acesso read-only a sync_export.");
        }
        catch (Exception ex)
        {
            return new ArpaDdlResult(false, Describe(ex));
        }
    }

    private static string BuildConnectionString(string host, int port, string database, string username, string password)
    {
        // Senha vazia e valida (Arpa com trust/peer). Mas username vazio faz o
        // Npgsql cair para o usuario do Windows do processo (o servico roda como
        // LocalSystem => "MAQUINA$"), gerando um erro confuso - isso sim e barrado.
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new InvalidOperationException("Usuario nao informado para a conexao com o Postgres do Arpa.");
        }

        return new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port <= 0 ? 5432 : port,
            Database = database,
            Username = username,
            Password = password ?? string.Empty,
            Timeout = 10,
            CommandTimeout = 30,
        }.ConnectionString;
    }

    private static string Describe(Exception ex) => ex switch
    {
        PostgresException { SqlState: "28P01" } => "Senha incorreta para o usuario informado (ou o servidor exige senha e nenhuma foi informada).",
        // Auth integrada do Windows: senha vazia + o Postgres do Arpa pediu
        // SSPI/GSS no pg_hba -> o Npgsql conectou como a conta da maquina do
        // servico (LocalSystem => "MAQUINA$"), que nao e um role no Postgres.
        PostgresException { SqlState: "28000" } pg when pg.MessageText.Contains('$')
            => "O Postgres do Arpa pediu autenticacao integrada do Windows e rejeitou o servico (que roda como LocalSystem). "
               + "Defina uma senha para esse usuario no Postgres do Arpa, ou ajuste o pg_hba.conf desse acesso para 'trust' ou 'scram-sha-256'.",
        PostgresException pg => $"{pg.SqlState}: {pg.MessageText}",
        _ => ex.Message,
    };

    [GeneratedRegex("^[a-zA-Z_][a-zA-Z0-9_]{0,62}$")]
    private static partial Regex RoleNameRegex();
}

public sealed record ArpaTestResult(bool Ok, string Message, IReadOnlyDictionary<string, bool> Views);

public sealed record ArpaDdlResult(bool Ok, string Message);
