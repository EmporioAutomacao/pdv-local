using Npgsql;

namespace SyncAgent.Provisioning;

/// <summary>
/// Executa SQL de manutencao no banco local do SyncAgent (schemas sync_agent e
/// pdv) a partir do dashboard, usando uma credencial admin do Postgres local
/// **transitoria** informada na hora - nunca gravada, nunca logada. Mesmo
/// padrao do <see cref="ArpaDdlRunner"/> para o Arpa.
///
/// Existe porque o runtime do agente conecta com um usuario so-DML de
/// proposito (sem ALTER TABLE) - reparos de schema (ex.: uma CHECK constraint
/// que ficou presa numa lista antiga apos uma extensao de contrato) exigem
/// uma acao administrativa explicita. Isso e essa acao: sem ela, o unico
/// caminho era psql direto na VM.
/// </summary>
public sealed class LocalDbMaintenanceRunner
{
    private readonly IConfiguration _configuration;

    public LocalDbMaintenanceRunner(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task<LocalDbMaintenanceResult> ExecuteAsync(
        string adminUser,
        string adminPassword,
        string sql,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(adminUser))
        {
            return new LocalDbMaintenanceResult(false, "Informe o usuario admin do Postgres local.");
        }

        if (string.IsNullOrWhiteSpace(sql))
        {
            return new LocalDbMaintenanceResult(false, "Informe o SQL a executar.");
        }

        var baseConnectionString = _configuration.GetConnectionString("SyncAgentDb");
        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            return new LocalDbMaintenanceResult(false, "Connection string 'SyncAgentDb' nao configurada.");
        }

        try
        {
            // Reaproveita host/porta/database do appsettings.json - so troca o
            // usuario/senha pela credencial admin transitoria informada agora.
            var connectionStringBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString)
            {
                Username = adminUser,
                Password = adminPassword,
            };

            await using var dataSource = NpgsqlDataSource.Create(connectionStringBuilder.ConnectionString);
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 60 };
            await command.ExecuteNonQueryAsync(cancellationToken);
            return new LocalDbMaintenanceResult(true, "Comando executado com sucesso.");
        }
        catch (Exception ex)
        {
            return new LocalDbMaintenanceResult(false, Describe(ex));
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        PostgresException pg => $"{pg.SqlState}: {pg.MessageText}",
        NpgsqlException { InnerException: { } inner } => inner.Message,
        _ => ex.Message,
    };
}

public sealed record LocalDbMaintenanceResult(bool Ok, string Message);
