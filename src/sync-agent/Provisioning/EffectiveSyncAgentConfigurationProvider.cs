using Microsoft.Extensions.Options;
using SyncAgent.Configuration;

namespace SyncAgent.Provisioning;

public sealed class EffectiveSyncAgentConfigurationProvider
{
    private readonly IOptionsMonitor<SyncAgentOptions> _syncOptions;
    private readonly ProvisioningStore _provisioningStore;

    public EffectiveSyncAgentConfigurationProvider(
        IOptionsMonitor<SyncAgentOptions> syncOptions,
        ProvisioningStore provisioningStore)
    {
        _syncOptions = syncOptions;
        _provisioningStore = provisioningStore;
    }

    public EffectiveSyncAgentConfiguration GetCurrent()
    {
        var options = _syncOptions.CurrentValue;
        if (!_provisioningStore.IsEnabled)
        {
            return new EffectiveSyncAgentConfiguration(
                false,
                true,
                options.InstanceId,
                options.ErpTenantId,
                options.ErpApiBaseUrl,
                options.AgentVersion,
                options.LocalStatusPort,
                null,
                null,
                null,
                null,
                false);
        }

        var credentials = _provisioningStore.TryRead();
        if (credentials is null)
        {
            return new EffectiveSyncAgentConfiguration(
                true,
                false,
                string.Empty,
                string.Empty,
                string.Empty,
                options.AgentVersion,
                options.LocalStatusPort,
                null,
                null,
                null,
                null,
                false);
        }

        return new EffectiveSyncAgentConfiguration(
            true,
            true,
            credentials.InstanceId,
            credentials.TenantId,
            credentials.ErpApiBaseUrl,
            options.AgentVersion,
            options.LocalStatusPort,
            credentials.AccessToken,
            credentials.AccessTokenExpiresAtUtc,
            credentials.RefreshToken,
            credentials.RefreshTokenExpiresAtUtc,
            credentials.RequiredMtls);
    }
}
