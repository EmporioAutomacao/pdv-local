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
    // Versao "colunas comuns" do contrato infra/arpa/sync-export-views-contract.sql.
    // Se o schema real do Arpa usar outros nomes de coluna, o CREATE VIEW falha e
    // devolvemos a mensagem apontando o fluxo por diagnostico.
    private const string PrepareViewsSql = """
        CREATE SCHEMA IF NOT EXISTS sync_export;

        CREATE OR REPLACE VIEW sync_export.produtos AS
        SELECT
            p.codigo::text AS entity_key,
            COALESCE(p.updated_at_utc, p.created_at_utc, now())::timestamptz AS occurred_at_utc,
            jsonb_build_object(
                'codigo', p.codigo,
                'descricao', p.descricao,
                'codigodefabrica', p.codigo_fabrica,
                'cod_ncm', p.ncm,
                'codigodebarras', p.codigo_barras,
                'ativo', CASE
                    WHEN UPPER(CAST(p.ativo AS TEXT)) IN ('0', 'FALSE', 'F', 'A', 'ATIVO') THEN true
                    WHEN UPPER(CAST(p.ativo AS TEXT)) IN ('1', 'TRUE', 'T', 'I', 'INATIVO') THEN false
                    ELSE true
                END,
                'precocusto', p.precocusto,
                'precovenda', p.precovenda
            )::text AS payload_json,
            concat('arpa-produto-', p.codigo)::text AS trace_id
        FROM public.produtos p
        WHERE p.codigo IS NOT NULL;

        CREATE OR REPLACE VIEW sync_export.clientes AS
        SELECT
            c.codigo::text AS entity_key,
            COALESCE(c.updated_at_utc, c.created_at_utc, now())::timestamptz AS occurred_at_utc,
            jsonb_build_object(
                'codigo', c.codigo,
                'nome', c.nome,
                'documento', c.cnpj_cpf,
                'email', c.email,
                'telefone', c.telefone,
                'ativo', CASE
                    WHEN UPPER(CAST(c.status AS TEXT)) IN ('A', 'ATIVO', '1', 'TRUE', 'T', 'SIM', 'S') THEN true
                    WHEN UPPER(CAST(c.status AS TEXT)) IN ('I', 'INATIVO', '0', 'FALSE', 'F', 'NAO', 'N') THEN false
                    ELSE true
                END
            )::text AS payload_json,
            concat('arpa-cliente-', c.codigo)::text AS trace_id
        FROM public.clientes c
        WHERE c.codigo IS NOT NULL;

        CREATE OR REPLACE VIEW sync_export.estoque AS
        SELECT
            p.codigo::text AS entity_key,
            COALESCE(p.updated_at_utc, p.created_at_utc, now())::timestamptz AS occurred_at_utc,
            jsonb_build_object(
                'codigo', p.codigo,
                'quantidade', p.quantidade,
                'estoqueminimo', p.estoque_minimo,
                'estoquemaximo', p.estoque_maximo,
                'localizacao', p.localizacao
            )::text AS payload_json,
            concat('arpa-estoque-', p.codigo)::text AS trace_id
        FROM public.produtos p
        WHERE p.codigo IS NOT NULL;
        """;

    private readonly ILogger<ArpaDdlRunner> _logger;

    public ArpaDdlRunner(ILogger<ArpaDdlRunner> logger)
    {
        _logger = logger;
    }

    public async Task<ArpaTestResult> TestConnectionAsync(
        string host, int port, string database, string username, string password,
        bool needProdutos, bool needClientes, bool needEstoque,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var dataSource = NpgsqlDataSource.Create(BuildConnectionString(host, port, database, username, password));
            await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);

            var views = new Dictionary<string, bool>();
            foreach (var (view, needed) in new[] { ("produtos", needProdutos), ("clientes", needClientes), ("estoque", needEstoque) })
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

    public async Task<ArpaDdlResult> PrepareViewsAsync(
        string host, int port, string database, string dbaUser, string dbaPassword,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var dataSource = NpgsqlDataSource.Create(BuildConnectionString(host, port, database, dbaUser, dbaPassword));
            await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var cmd = new NpgsqlCommand(PrepareViewsSql, conn);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            return new ArpaDdlResult(true, "Views sync_export criadas/atualizadas. Rode \"Testar conexao\".");
        }
        catch (PostgresException ex) when (ex.SqlState is "42703" or "42P01")
        {
            return new ArpaDdlResult(false,
                $"O schema do Arpa deste cliente usa outros nomes de coluna ({ex.MessageText}). " +
                "Gere as views pelo diagnostico: export_arpa_schema_diagnostics no ERP + " +
                "new-arpa-sync-export-views-from-diagnostics.ps1 + apply-arpa-sync-export-views.ps1.");
        }
        catch (Exception ex)
        {
            return new ArpaDdlResult(false, Describe(ex));
        }
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
        => new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port <= 0 ? 5432 : port,
            Database = database,
            Username = username,
            Password = password,
            Timeout = 10,
            CommandTimeout = 30,
        }.ConnectionString;

    private static string Describe(Exception ex) => ex switch
    {
        PostgresException pg => $"{pg.SqlState}: {pg.MessageText}",
        _ => ex.Message,
    };

    [GeneratedRegex("^[a-zA-Z_][a-zA-Z0-9_]{0,62}$")]
    private static partial Regex RoleNameRegex();
}

public sealed record ArpaTestResult(bool Ok, string Message, IReadOnlyDictionary<string, bool> Views);

public sealed record ArpaDdlResult(bool Ok, string Message);
