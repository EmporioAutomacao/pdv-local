using SyncAgent.Provisioning;

namespace SyncAgent.Tests;

public class LicenseAndRefreshTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private static ProvisionedAgentCredentials Credentials(
        string refreshToken = "refresh",
        DateTimeOffset? refreshExpiresAt = null,
        DateTimeOffset? refreshRejectedAt = null,
        DateTimeOffset? licenseBlockedSince = null) =>
        new(
            "instance", "tenant", "https://erp.example.com",
            "access", Now.AddHours(1),
            refreshToken, refreshExpiresAt,
            false, Now.AddDays(-40),
            refreshRejectedAt, licenseBlockedSince);

    [Fact]
    public void ExpiredLocalRefreshDateDoesNotRequireReactivation()
    {
        // Contrato 2.16.0: o ERP rotaciona o refresh e nao o expira enquanto o cliente esta ativo.
        var credentials = Credentials(refreshExpiresAt: Now.AddDays(-5));

        Assert.False(credentials.NeedsReactivation(Now));
    }

    [Fact]
    public void MissingRefreshTokenRequiresReactivation()
    {
        Assert.True(Credentials(refreshToken: "").NeedsReactivation(Now));
    }

    [Fact]
    public void RefreshRejectedByErpRequiresReactivation()
    {
        Assert.True(Credentials(refreshRejectedAt: Now).NeedsReactivation(Now));
    }

    [Fact]
    public void LicenseIsActiveWhenErpNeverRefusedForPlan()
    {
        Assert.Equal(LicenseState.Active, Credentials().GetLicenseState(Now, 3));
    }

    [Theory]
    [InlineData(0, 3, LicenseState.Grace)]
    [InlineData(2, 3, LicenseState.Grace)]
    [InlineData(3, 3, LicenseState.Blocked)]
    [InlineData(10, 3, LicenseState.Blocked)]
    [InlineData(0, 0, LicenseState.Blocked)]
    public void LicenseMovesFromGraceToBlockedAfterGraceDays(int daysSinceFirstRefusal, int graceDays, LicenseState expected)
    {
        var credentials = Credentials(licenseBlockedSince: Now.AddDays(-daysSinceFirstRefusal));

        Assert.Equal(expected, credentials.GetLicenseState(Now, graceDays));
    }

    [Theory]
    [InlineData(403, "installation_revoked", TokenRefreshOutcome.Revoked)]
    [InlineData(403, "tenant_invalid", TokenRefreshOutcome.Revoked)]
    [InlineData(401, "refresh_token_invalid", TokenRefreshOutcome.Invalid)]
    [InlineData(401, "unauthorized", TokenRefreshOutcome.Transient)]
    [InlineData(403, "other", TokenRefreshOutcome.Transient)]
    [InlineData(500, null, TokenRefreshOutcome.Transient)]
    [InlineData(502, "http_502", TokenRefreshOutcome.Transient)]
    [InlineData(429, null, TokenRefreshOutcome.Transient)]
    public void RefreshFailuresAreClassifiedSoOnlyExplicitRefusalsAffectLicense(
        int statusCode, string? errorCode, TokenRefreshOutcome expected)
    {
        Assert.Equal(expected, ErpActivationClient.ClassifyRefreshFailure(statusCode, errorCode));
    }

    [Fact]
    public void CredentialsWithoutNewFieldsStillDeserialize()
    {
        // Arquivo gravado por versao anterior (sem refresh_rejected_at_utc / license_blocked_since_utc).
        const string json = """
            {"instance_id":"i","tenant_id":"t","erp_api_base_url":"https://e","access_token":"a",
             "access_token_expires_at_utc":"2026-10-03T13:00:00+00:00","refresh_token":"r",
             "refresh_token_expires_at_utc":"2026-10-01T00:00:00+00:00","required_mtls":false,
             "activated_at_utc":"2026-09-10T00:00:00+00:00"}
            """;

        var credentials = System.Text.Json.JsonSerializer.Deserialize<ProvisionedAgentCredentials>(
            json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.NotNull(credentials);
        Assert.Null(credentials!.LicenseBlockedSinceUtc);
        Assert.False(credentials.NeedsReactivation(Now));
    }
}
