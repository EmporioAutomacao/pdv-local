using Microsoft.Extensions.Options;
using Npgsql;
using SyncAgent.Configuration;
using SyncAgent.Security;

namespace SyncAgent.Provisioning;

/// <summary>
/// Resolve a configuracao efetiva do ArpaCollector: configuracao local
/// estatica (appsettings), ou configuracao remota obtida do ERP
/// (ArpaCollectorOptions.UseRemoteConfig=true) com cache local protegido por
/// DPAPI para sobreviver a indisponibilidade temporaria do ERP.
/// </summary>
public sealed class EffectiveArpaCollectorConfigurationProvider
{
    private readonly ILogger<EffectiveArpaCollectorConfigurationProvider> _logger;
    private readonly IOptionsMonitor<ArpaCollectorOptions> _options;
    private readonly ArpaConnectionStringProvider _localConnectionStringProvider;
    private readonly ArpaConnectionConfigClient _remoteClient;
    private readonly ArpaRemoteConfigCache _remoteCache;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private ArpaConnectionConfig? _cachedRemoteConfig;
    private DateTimeOffset _lastRefreshUtc = DateTimeOffset.MinValue;

    public EffectiveArpaCollectorConfigurationProvider(
        ILogger<EffectiveArpaCollectorConfigurationProvider> logger,
        IOptionsMonitor<ArpaCollectorOptions> options,
        ArpaConnectionStringProvider localConnectionStringProvider,
        ArpaConnectionConfigClient remoteClient,
        ArpaRemoteConfigCache remoteCache)
    {
        _logger = logger;
        _options = options;
        _localConnectionStringProvider = localConnectionStringProvider;
        _remoteClient = remoteClient;
        _remoteCache = remoteCache;
    }

    public async Task<EffectiveArpaCollectorOptions> GetCurrentAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled)
        {
            return EffectiveArpaCollectorOptions.Disabled;
        }

        if (!options.UseRemoteConfig)
        {
            return new EffectiveArpaCollectorOptions(
                true,
                _localConnectionStringProvider.GetConnectionString(),
                options.BatchSize,
                options.Entities);
        }

        await RefreshRemoteConfigIfNeededAsync(options, cancellationToken);

        var remote = _cachedRemoteConfig;
        if (remote is null)
        {
            _logger.LogWarning(
                "ArpaCollector: configuracao remota indisponivel e nenhuma copia em cache foi encontrada; coleta sera pulada nesta execucao.");
            return EffectiveArpaCollectorOptions.Disabled;
        }

        var connectionStringBuilder = new NpgsqlConnectionStringBuilder
        {
            Host = remote.Host,
            Port = remote.Port,
            Username = remote.Username,
            Password = remote.Password,
            Database = remote.Database,
        };

        var entities = remote.Entities
            .Select(entity => new ArpaEntityCollectorOptions
            {
                Name = entity.Name,
                EntityType = entity.EntityType,
                Query = entity.Query,
            })
            .ToList();

        return new EffectiveArpaCollectorOptions(
            true,
            connectionStringBuilder.ConnectionString,
            options.BatchSize,
            entities);
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

public sealed record EffectiveArpaCollectorOptions(
    bool Enabled,
    string ConnectionString,
    int BatchSize,
    IReadOnlyList<ArpaEntityCollectorOptions> Entities)
{
    public static EffectiveArpaCollectorOptions Disabled { get; } = new(false, string.Empty, 0, []);
}
