using Microsoft.Extensions.Options;
using Npgsql;
using SyncAgent.Collectors;
using SyncAgent.Configuration;
using SyncAgent.Security;

namespace SyncAgent.Provisioning;

/// <summary>
/// Resolve a lista de conexoes Arpa efetivas do coletor. Precedencia:
/// 1. store local (aba Configuracoes > Arpa) - fonte de verdade;
/// 2. ConnectionString/Entities estaticos do appsettings (legado) - migrados
///    para o store no primeiro uso.
/// </summary>
public sealed class EffectiveArpaCollectorConfigurationProvider
{
    private readonly ILogger<EffectiveArpaCollectorConfigurationProvider> _logger;
    private readonly IOptionsMonitor<ArpaCollectorOptions> _options;
    private readonly ArpaConnectionStringProvider _localConnectionStringProvider;
    private readonly ArpaConnectionsStore _connectionsStore;
    private readonly ArpaCollectorSettingsStore _settingsStore;

    private bool _legacyMigrationChecked;

    public EffectiveArpaCollectorConfigurationProvider(
        ILogger<EffectiveArpaCollectorConfigurationProvider> logger,
        IOptionsMonitor<ArpaCollectorOptions> options,
        ArpaConnectionStringProvider localConnectionStringProvider,
        ArpaConnectionsStore connectionsStore,
        ArpaCollectorSettingsStore settingsStore)
    {
        _logger = logger;
        _options = options;
        _localConnectionStringProvider = localConnectionStringProvider;
        _connectionsStore = connectionsStore;
        _settingsStore = settingsStore;
    }

    public Task<IReadOnlyList<EffectiveArpaCollectorConnection>> GetCurrentAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        if (!_settingsStore.IsEffectivelyEnabled())
        {
            return Task.FromResult<IReadOnlyList<EffectiveArpaCollectorConnection>>([]);
        }

        MigrateLegacyStaticConnectionIfNeeded(options);

        var stored = _connectionsStore.ReadAll();
        if (stored.Count > 0)
        {
            IReadOnlyList<EffectiveArpaCollectorConnection> effective = stored
                .Where(c => c.Enabled)
                .Select(ToEffective)
                .Where(c => c is not null)
                .Select(c => c!)
                .ToList();
            return Task.FromResult(effective);
        }

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return Task.FromResult<IReadOnlyList<EffectiveArpaCollectorConnection>>([]);
        }

        IReadOnlyList<EffectiveArpaCollectorConnection> result =
        [
            new EffectiveArpaCollectorConnection(
                "static",
                "Estatica (appsettings)",
                _localConnectionStringProvider.GetConnectionString(),
                options.BatchSize,
                string.Empty,
                options.Entities),
        ];
        return Task.FromResult(result);
    }

    private EffectiveArpaCollectorConnection? ToEffective(ArpaLocalConnection connection)
    {
        if (string.IsNullOrWhiteSpace(connection.Host) || string.IsNullOrWhiteSpace(connection.Database))
        {
            _logger.LogWarning("ArpaCollector: conexao '{Nome}' ignorada (host/database vazios).", connection.Nome);
            return null;
        }

        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = connection.Host,
            Port = connection.Port <= 0 ? 5432 : connection.Port,
            Database = connection.Database,
            Username = connection.Username,
            Password = connection.Password,
        }.ConnectionString;

        var entities = ArpaStandardEntities.Build(
            connection.SyncProdutos,
            connection.SyncClientes,
            connection.SyncEstoque,
            connection.SyncVendas,
            connection.SyncFinanceiro,
            connection.SyncCompra,
            connection.SyncCobranca,
            connection.SyncPlanoHistorico);

        return new EffectiveArpaCollectorConnection(
            connection.Id,
            string.IsNullOrWhiteSpace(connection.Nome) ? connection.Id : connection.Nome,
            connectionString,
            connection.BatchSize <= 0 ? 5000 : connection.BatchSize,
            connection.LojaCodigo,
            entities);
    }

    private void MigrateLegacyStaticConnectionIfNeeded(ArpaCollectorOptions options)
    {
        if (_legacyMigrationChecked)
        {
            return;
        }

        _legacyMigrationChecked = true;

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return;
        }

        if (_connectionsStore.ReadAll().Count > 0)
        {
            return;
        }

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(_localConnectionStringProvider.GetConnectionString());
            var entityTypes = options.Entities.Select(e => e.EntityType).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var hasAny = entityTypes.Count > 0;

            var migrated = new ArpaLocalConnection
            {
                Id = Guid.NewGuid().ToString("N"),
                Nome = "Padrao",
                Host = builder.Host ?? string.Empty,
                Port = builder.Port <= 0 ? 5432 : builder.Port,
                Database = builder.Database ?? string.Empty,
                Username = builder.Username ?? string.Empty,
                Password = builder.Password ?? string.Empty,
                LojaCodigo = string.Empty,
                ControlaEstoque = false,
                SyncProdutos = !hasAny || entityTypes.Contains("produto"),
                SyncClientes = !hasAny || entityTypes.Contains("cliente"),
                SyncEstoque = hasAny && entityTypes.Contains("estoque"),
                SyncVendas = hasAny && entityTypes.Contains("venda"),
                SyncFinanceiro = hasAny && entityTypes.Contains("financeiro"),
                BatchSize = options.BatchSize <= 0 ? 5000 : options.BatchSize,
                Enabled = true,
            };

            _connectionsStore.WriteAll([migrated]);
            _logger.LogInformation(
                "ArpaCollector: conexao estatica do appsettings migrada para o store local (aba Configuracoes > Arpa).");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ArpaCollector: falha ao migrar a conexao estatica do appsettings para o store local.");
        }
    }

}

public sealed record EffectiveArpaCollectorConnection(
    string Id,
    string Nome,
    string ConnectionString,
    int BatchSize,
    string LojaCodigo,
    IReadOnlyList<ArpaEntityCollectorOptions> Entities);
