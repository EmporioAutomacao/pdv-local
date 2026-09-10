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
///    para o store no primeiro uso;
/// 3. configuracao remota do ERP (UseRemoteConfig=true) com cache DPAPI (legado).
/// </summary>
public sealed class EffectiveArpaCollectorConfigurationProvider
{
    private readonly ILogger<EffectiveArpaCollectorConfigurationProvider> _logger;
    private readonly IOptionsMonitor<ArpaCollectorOptions> _options;
    private readonly ArpaConnectionStringProvider _localConnectionStringProvider;
    private readonly ArpaConnectionsStore _connectionsStore;
    private readonly ArpaCollectorSettingsStore _settingsStore;
    private readonly ArpaConnectionConfigClient _remoteClient;
    private readonly ArpaRemoteConfigCache _remoteCache;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private ArpaConnectionConfig? _cachedRemoteConfig;
    private DateTimeOffset _lastRefreshUtc = DateTimeOffset.MinValue;
    private bool _legacyMigrationChecked;

    public EffectiveArpaCollectorConfigurationProvider(
        ILogger<EffectiveArpaCollectorConfigurationProvider> logger,
        IOptionsMonitor<ArpaCollectorOptions> options,
        ArpaConnectionStringProvider localConnectionStringProvider,
        ArpaConnectionsStore connectionsStore,
        ArpaCollectorSettingsStore settingsStore,
        ArpaConnectionConfigClient remoteClient,
        ArpaRemoteConfigCache remoteCache)
    {
        _logger = logger;
        _options = options;
        _localConnectionStringProvider = localConnectionStringProvider;
        _connectionsStore = connectionsStore;
        _settingsStore = settingsStore;
        _remoteClient = remoteClient;
        _remoteCache = remoteCache;
    }

    public async Task<IReadOnlyList<EffectiveArpaCollectorConnection>> GetCurrentAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        if (!_settingsStore.IsEffectivelyEnabled())
        {
            return [];
        }

        MigrateLegacyStaticConnectionIfNeeded(options);

        var stored = _connectionsStore.ReadAll();
        if (stored.Count > 0)
        {
            return stored
                .Where(c => c.Enabled)
                .Select(ToEffective)
                .Where(c => c is not null)
                .Select(c => c!)
                .ToList();
        }

        if (!options.UseRemoteConfig)
        {
            if (string.IsNullOrWhiteSpace(options.ConnectionString))
            {
                return [];
            }

            return
            [
                new EffectiveArpaCollectorConnection(
                    "static",
                    "Estatica (appsettings)",
                    _localConnectionStringProvider.GetConnectionString(),
                    options.BatchSize,
                    string.Empty,
                    options.Entities),
            ];
        }

        await RefreshRemoteConfigIfNeededAsync(options, cancellationToken);

        var remote = _cachedRemoteConfig;
        if (remote is null)
        {
            _logger.LogWarning(
                "ArpaCollector: configuracao remota indisponivel e nenhuma copia em cache foi encontrada; coleta sera pulada nesta execucao.");
            return [];
        }

        var remoteConnectionString = new NpgsqlConnectionStringBuilder
        {
            Host = remote.Host,
            Port = remote.Port,
            Username = remote.Username,
            Password = remote.Password,
            Database = remote.Database,
        }.ConnectionString;

        var remoteEntities = remote.Entities
            .Select(entity => new ArpaEntityCollectorOptions
            {
                Name = entity.Name,
                EntityType = entity.EntityType,
                Query = entity.Query,
            })
            .ToList();

        return
        [
            new EffectiveArpaCollectorConnection(
                $"remote-{remote.ConnectionId}",
                remote.Nome,
                remoteConnectionString,
                options.BatchSize,
                string.Empty,
                remoteEntities),
        ];
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

        if (options.UseRemoteConfig || string.IsNullOrWhiteSpace(options.ConnectionString))
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

    private async Task RefreshRemoteConfigIfNeededAsync(ArpaCollectorOptions options, CancellationToken cancellationToken)
    {
        var refreshInterval = TimeSpan.FromMinutes(Math.Max(1, options.RemoteConfigRefreshMinutes));
        if (_cachedRemoteConfig is not null && DateTimeOffset.UtcNow - _lastRefreshUtc < refreshInterval)
        {
            return;
        }

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedRemoteConfig is not null && DateTimeOffset.UtcNow - _lastRefreshUtc < refreshInterval)
            {
                return;
            }

            var result = await _remoteClient.FetchAsync(cancellationToken);
            if (result.Succeeded && result.Connection is not null)
            {
                _cachedRemoteConfig = result.Connection;
                _lastRefreshUtc = DateTimeOffset.UtcNow;
                await _remoteCache.SaveAsync(result.Connection, cancellationToken);
                return;
            }

            _logger.LogWarning(
                "ArpaCollector: falha ao obter configuracao remota de conexao ({Error}); usando ultima copia em cache, se houver.",
                result.Error);

            if (_cachedRemoteConfig is null)
            {
                _cachedRemoteConfig = _remoteCache.TryRead();
                _lastRefreshUtc = DateTimeOffset.UtcNow;
            }
        }
        finally
        {
            _refreshLock.Release();
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
