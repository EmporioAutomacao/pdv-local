using Npgsql;
using SyncAgent.Contracts;

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
///
/// <see cref="Routines"/> cobre reparos conhecidos e repetiveis entre clientes
/// (ex.: bases criadas antes de um contrato Sync novo) - selecionaveis no
/// dashboard sem copiar/colar SQL. O campo de SQL livre continua existindo
/// para casos novos que ainda nao viraram rotina.
/// </summary>
public sealed class LocalDbMaintenanceRunner
{
    private readonly IConfiguration _configuration;

    public LocalDbMaintenanceRunner(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    /// <summary>
    /// Rotinas conhecidas, montadas a partir das mesmas constantes de
    /// contrato que o resto do agente usa (<see cref="SyncContractValues"/>) -
    /// nunca uma lista hardcoded separada que pode ficar desatualizada.
    /// </summary>
    public static IReadOnlyList<MaintenanceRoutine> Routines { get; } = BuildRoutines();

    private static IReadOnlyList<MaintenanceRoutine> BuildRoutines()
    {
        var allowedEntityTypes = string.Join(", ", SyncContractValues.EntityTypes.Select(t => $"'{t}'"));

        return new[]
        {
            new MaintenanceRoutine(
                "fix_entity_type_check",
                "Corrigir outbox_events_entity_type_check",
                "Atualiza a lista de entity_type permitidos na fila local (outbox_events) para a "
                    + "atual do contrato Sync: " + string.Join(", ", SyncContractValues.EntityTypes) + ". "
                    + "Necessario em qualquer instalacao criada antes do contrato ganhar cobranca/"
                    + "plano_historico (2.10.0/2.11.0) - sem isso, eventos dessas entidades nunca sao "
                    + "enfileirados (erro 23514 no log). Seguro re-rodar.",
                $"""
                ALTER TABLE sync_agent.outbox_events DROP CONSTRAINT IF EXISTS outbox_events_entity_type_check;
                ALTER TABLE sync_agent.outbox_events ADD CONSTRAINT outbox_events_entity_type_check
                    CHECK (entity_type IN ({allowedEntityTypes}));
                """),
        };
    }

    /// <summary>
    /// Testa a credencial admin sem executar nenhum SQL de efeito - so
    /// <c>SELECT current_user</c>. Existe para o operador conferir usuario/senha
    /// do Postgres local pelo dashboard antes de rodar uma rotina ou SQL livre.
    /// </summary>
    public async Task<LocalDbMaintenanceResult> TestConnectionAsync(
        string adminUser,
        string adminPassword,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(adminUser))
        {
            return new LocalDbMaintenanceResult(false, "Informe o usuario admin do Postgres local.");
        }

        var baseConnectionString = _configuration.GetConnectionString("SyncAgentDb");
        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            return new LocalDbMaintenanceResult(false, "Connection string 'SyncAgentDb' nao configurada.");
        }

        try
        {
            var connectionStringBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString)
            {
                Username = adminUser,
                Password = adminPassword,
            };

            await using var dataSource = NpgsqlDataSource.Create(connectionStringBuilder.ConnectionString);
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT current_user", connection) { CommandTimeout = 15 };
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return new LocalDbMaintenanceResult(true, $"Conexao OK. Autenticado como '{result}'.");
        }
        catch (Exception ex)
        {
            return new LocalDbMaintenanceResult(false, Describe(ex));
        }
    }

    public async Task<LocalDbMaintenanceResult> ExecuteRoutineAsync(
        string routineKey,
        string adminUser,
        string adminPassword,
        CancellationToken cancellationToken)
    {
        var routine = Routines.FirstOrDefault(r => r.Key == routineKey);
        if (routine is null)
        {
            return new LocalDbMaintenanceResult(false, "Rotina desconhecida.");
        }

        return await ExecuteAsync(adminUser, adminPassword, routine.Sql, cancellationToken);
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

public sealed record MaintenanceRoutine(string Key, string Label, string Description, string Sql);
