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
    [property: JsonPropertyName("activated_at_utc")] DateTimeOffset ActivatedAtUtc,
    [property: JsonPropertyName("refresh_rejected_at_utc")] DateTimeOffset? RefreshRejectedAtUtc = null,
    [property: JsonPropertyName("license_blocked_since_utc")] DateTimeOffset? LicenseBlockedSinceUtc = null)
{
    // Desde o contrato 2.16.0 o ERP rotaciona o refresh token a cada uso e nao o expira por tempo
    // enquanto o cliente esta ativo no CP, entao a data local de expiracao deixou de ser um motivo
    // para reativar. So exige novo codigo de ativacao quando nao ha refresh token ou quando o ERP
    // nao o reconhece mais (401 refresh_token_invalid -> RefreshRejectedAtUtc).
    public bool NeedsReactivation(DateTimeOffset now) =>
        string.IsNullOrWhiteSpace(RefreshToken) || RefreshRejectedAtUtc is not null;

    public LicenseState GetLicenseState(DateTimeOffset now, int graceDays) =>
        LicenseStateCalculator.Calculate(LicenseBlockedSinceUtc, now, graceDays);
}

public enum LicenseState
{
    Active,
    Grace,
    Blocked
}

public static class LicenseStateCalculator
{
    public const int DefaultGraceDays = 3;

    /// <summary>
    /// Ativo se o ERP nunca recusou por plano (ou ja voltou); carencia ate
    /// <paramref name="graceDays"/> dias apos a primeira recusa; bloqueado depois disso.
    /// Falha de rede nunca chega aqui: so recusa explicita define <paramref name="blockedSinceUtc"/>.
    /// </summary>
    public static LicenseState Calculate(DateTimeOffset? blockedSinceUtc, DateTimeOffset now, int graceDays)
    {
        if (blockedSinceUtc is null)
        {
            return LicenseState.Active;
        }

        var grace = TimeSpan.FromDays(Math.Max(0, graceDays));
        return now < blockedSinceUtc.Value + grace ? LicenseState.Grace : LicenseState.Blocked;
    }
}

public sealed record EffectiveSyncAgentConfiguration(
    bool IsProvisioningEnabled,
    bool IsProvisioned,
    bool NeedsReactivation,
    string InstanceId,
    string TenantId,
    string ErpApiBaseUrl,
    string AgentVersion,
    int LocalStatusPort,
    string? AccessToken,
    DateTimeOffset? AccessTokenExpiresAtUtc,
    string? RefreshToken,
    DateTimeOffset? RefreshTokenExpiresAtUtc,
    bool RequiredMtls,
    LicenseState LicenseState = LicenseState.Active,
    DateTimeOffset? LicenseBlockedSinceUtc = null,
    DateTimeOffset? LicenseGraceEndsAtUtc = null);
