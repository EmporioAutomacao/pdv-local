using System.Text.Json.Serialization;

namespace SyncAgent.Provisioning;

public sealed record ProvisionedAgentCredentials(
    [property: JsonPropertyName("instance_id")] string InstanceId,
    [property: JsonPropertyName("tenant_id")] string TenantId,
    [property: JsonPropertyName("erp_api_base_url")] string ErpApiBaseUrl,
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("access_token_expires_at_utc")] DateTimeOffset AccessTokenExpiresAtUtc,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("refresh_token_expires_at_utc")] DateTimeOffset? RefreshTokenExpiresAtUtc,
    [property: JsonPropertyName("required_mtls")] bool RequiredMtls,
    [property: JsonPropertyName("activated_at_utc")] DateTimeOffset ActivatedAtUtc);

public sealed record EffectiveSyncAgentConfiguration(
    bool IsProvisioningEnabled,
    bool IsProvisioned,
    string InstanceId,
    string TenantId,
    string ErpApiBaseUrl,
    string AgentVersion,
    int LocalStatusPort,
    string? AccessToken,
    DateTimeOffset? AccessTokenExpiresAtUtc,
    string? RefreshToken,
    DateTimeOffset? RefreshTokenExpiresAtUtc,
    bool RequiredMtls);
