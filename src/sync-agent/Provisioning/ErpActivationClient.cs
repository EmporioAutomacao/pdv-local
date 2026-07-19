using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SyncAgent.Configuration;
using SyncAgent.Security;

namespace SyncAgent.Provisioning;

public sealed class ErpActivationClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<SyncAgentOptions> _syncOptions;
    private readonly IOptionsMonitor<SyncAgentProvisioningOptions> _provisioningOptions;
    private readonly ProvisioningStore _provisioningStore;

    public ErpActivationClient(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<SyncAgentOptions> syncOptions,
        IOptionsMonitor<SyncAgentProvisioningOptions> provisioningOptions,
        ProvisioningStore provisioningStore)
    {
        _httpClientFactory = httpClientFactory;
        _syncOptions = syncOptions;
        _provisioningOptions = provisioningOptions;
        _provisioningStore = provisioningStore;
    }

    public async Task<ActivationResult> ActivateAsync(
        string erpApiBaseUrl,
        string activationCode,
        CancellationToken cancellationToken)
    {
        var existingCredentials = _provisioningStore.TryRead();
        if (existingCredentials is not null && !existingCredentials.NeedsReactivation(DateTimeOffset.UtcNow))
        {
            return new ActivationResult(false, "already_provisioned", "Esta instalacao ja esta ativada.", null);
        }

        if (!Uri.TryCreate(erpApiBaseUrl, UriKind.Absolute, out var erpApiBaseUri)
            || (erpApiBaseUri.Scheme != Uri.UriSchemeHttp && erpApiBaseUri.Scheme != Uri.UriSchemeHttps))
        {
            return new ActivationResult(false, "invalid_erp_url", "URL do ERP invalida.", null);
        }

        try
        {
            ErpCredentialProvider.EnsureHttpsOutsideLocalDevelopment(erpApiBaseUri);
        }
        catch (Exception ex)
        {
            return new ActivationResult(false, "invalid_erp_url", ex.Message, null);
        }

        if (string.IsNullOrWhiteSpace(activationCode))
        {
            return new ActivationResult(false, "activation_code_required", "Codigo de ativacao obrigatorio.", null);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _provisioningOptions.CurrentValue.ActivationTimeoutSeconds)));

        var client = _httpClientFactory.CreateClient(ErpActivationHttpClient.Name);
        client.BaseAddress = erpApiBaseUri;

        var validateRequest = new ActivationValidateRequest(
            activationCode.Trim(),
            _syncOptions.CurrentValue.AgentVersion,
            Guid.NewGuid().ToString("N"));

        using var validateHttpRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/sync/activation:validate")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(validateRequest, JsonOptions),
                Encoding.UTF8,
                "application/json")
        };

        using var validateResponse = await client.SendAsync(validateHttpRequest, timeout.Token);

        if (!validateResponse.IsSuccessStatusCode)
        {
            var error = await ReadErrorAsync(validateResponse, timeout.Token);
            return new ActivationResult(false, error.Error, error.Message, null);
        }

        var completeRequest = new ActivationCompleteRequest(
            activationCode.Trim(),
            _syncOptions.CurrentValue.AgentVersion,
            Environment.MachineName,
            null,
            ["events", "heartbeat", "reconciliation", "arpa_collector"]);

        using var completeHttpRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/sync/activation:complete")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(completeRequest, JsonOptions),
                Encoding.UTF8,
                "application/json")
        };

        using var completeResponse = await client.SendAsync(completeHttpRequest, timeout.Token);
        if (completeResponse.StatusCode != System.Net.HttpStatusCode.Created)
        {
            var error = await ReadErrorAsync(completeResponse, timeout.Token);
            return new ActivationResult(false, error.Error, error.Message, null);
        }

        var body = await completeResponse.Content.ReadFromJsonAsync<ActivationCompleteResponse>(JsonOptions, timeout.Token);
        if (body is null)
        {
            return new ActivationResult(false, "invalid_response", "ERP retornou resposta invalida.", null);
        }

        var effectiveErpApiBaseUrl = string.IsNullOrWhiteSpace(body.ErpApiBaseUrl)
            ? erpApiBaseUri.ToString().TrimEnd('/')
            : body.ErpApiBaseUrl.TrimEnd('/');

        if (!Uri.TryCreate(effectiveErpApiBaseUrl, UriKind.Absolute, out var effectiveErpApiBaseUri)
            || (effectiveErpApiBaseUri.Scheme != Uri.UriSchemeHttp && effectiveErpApiBaseUri.Scheme != Uri.UriSchemeHttps))
        {
            return new ActivationResult(false, "invalid_response", "ERP retornou URL de API invalida.", null);
        }

        try
        {
            ErpCredentialProvider.EnsureHttpsOutsideLocalDevelopment(effectiveErpApiBaseUri);
        }
        catch (Exception ex)
        {
            return new ActivationResult(false, "invalid_response", ex.Message, null);
        }

        var credentials = new ProvisionedAgentCredentials(
            body.InstanceId,
            body.TenantId,
            effectiveErpApiBaseUrl,
            body.AccessToken,
            body.AccessTokenExpiresAtUtc,
            body.RefreshToken,
            body.RefreshTokenExpiresAtUtc,
            body.RequiredMtls,
            DateTimeOffset.UtcNow);

        await _provisioningStore.SaveAsync(credentials, timeout.Token);
        return new ActivationResult(true, "activated", "Instalacao ativada com sucesso.", credentials);
    }

    public async Task<TokenRefreshResult> RefreshTokenIfNeededAsync(CancellationToken cancellationToken)
    {
        var credentials = _provisioningStore.TryRead();
        if (credentials is null)
        {
            return new TokenRefreshResult(false, false, null);
        }

        if (credentials.AccessTokenExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return new TokenRefreshResult(true, false, null);
        }

        if (string.IsNullOrWhiteSpace(credentials.RefreshToken))
        {
            return new TokenRefreshResult(true, false, "Refresh token ausente.");
        }

        if (credentials.RefreshTokenExpiresAtUtc is not null
            && credentials.RefreshTokenExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            return new TokenRefreshResult(true, false, "Refresh token expirado.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _provisioningOptions.CurrentValue.ActivationTimeoutSeconds)));

        var erpApiBaseUri = new Uri(credentials.ErpApiBaseUrl, UriKind.Absolute);
        ErpCredentialProvider.EnsureHttpsOutsideLocalDevelopment(erpApiBaseUri);

        var client = _httpClientFactory.CreateClient(ErpActivationHttpClient.Name);
        client.BaseAddress = erpApiBaseUri;

        var request = new TokenRefreshRequest(credentials.RefreshToken, _syncOptions.CurrentValue.AgentVersion);
        var escapedInstanceId = Uri.EscapeDataString(credentials.InstanceId);
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/v1/sync/agents/{escapedInstanceId}/token:refresh")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(request, JsonOptions),
                Encoding.UTF8,
                "application/json")
        };

        using var response = await client.SendAsync(httpRequest, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            var error = await ReadErrorAsync(response, timeout.Token);
            return new TokenRefreshResult(true, false, error.Message);
        }

        var body = await response.Content.ReadFromJsonAsync<TokenRefreshResponse>(JsonOptions, timeout.Token);
        if (body is null || string.IsNullOrWhiteSpace(body.AccessToken))
        {
            return new TokenRefreshResult(true, false, "ERP retornou resposta invalida ao renovar token.");
        }

        var updatedCredentials = credentials with
        {
            AccessToken = body.AccessToken,
            AccessTokenExpiresAtUtc = body.AccessTokenExpiresAtUtc,
            RefreshToken = string.IsNullOrWhiteSpace(body.RefreshToken) ? credentials.RefreshToken : body.RefreshToken,
            RefreshTokenExpiresAtUtc = body.RefreshTokenExpiresAtUtc ?? credentials.RefreshTokenExpiresAtUtc
        };

        await _provisioningStore.SaveAsync(updatedCredentials, timeout.Token);
        return new TokenRefreshResult(true, true, null);
    }

    private static async Task<ActivationError> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                var error = JsonSerializer.Deserialize<ActivationError>(body, JsonOptions);
                if (error is not null && !string.IsNullOrWhiteSpace(error.Error))
                {
                    return error;
                }
            }
            catch (JsonException)
            {
                // Fall through to generic error.
            }
        }

        return new ActivationError(
            $"http_{(int)response.StatusCode}",
            $"ERP retornou HTTP {(int)response.StatusCode}.");
    }
}

public static class ErpActivationHttpClient
{
    public const string Name = "ErpActivation";
}

public sealed record ActivationResult(
    bool Succeeded,
    string Code,
    string Message,
    ProvisionedAgentCredentials? Credentials);

public sealed record ActivationValidateRequest(
    [property: JsonPropertyName("activation_code")] string ActivationCode,
    [property: JsonPropertyName("agent_version")] string AgentVersion,
    [property: JsonPropertyName("setup_nonce")] string SetupNonce);

public sealed record ActivationCompleteRequest(
    [property: JsonPropertyName("activation_code")] string ActivationCode,
    [property: JsonPropertyName("agent_version")] string AgentVersion,
    [property: JsonPropertyName("installation_label")] string InstallationLabel,
    [property: JsonPropertyName("machine_fingerprint_hash")] string? MachineFingerprintHash,
    [property: JsonPropertyName("requested_capabilities")] IReadOnlyCollection<string> RequestedCapabilities);

public sealed record ActivationCompleteResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("instance_id")] string InstanceId,
    [property: JsonPropertyName("tenant_id")] string TenantId,
    [property: JsonPropertyName("erp_api_base_url")] string ErpApiBaseUrl,
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("access_token_expires_at_utc")] DateTimeOffset AccessTokenExpiresAtUtc,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("refresh_token_expires_at_utc")] DateTimeOffset? RefreshTokenExpiresAtUtc,
    [property: JsonPropertyName("required_mtls")] bool RequiredMtls);

public sealed record ActivationError(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("message")] string Message);

public sealed record TokenRefreshRequest(
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("agent_version")] string AgentVersion);

public sealed record TokenRefreshResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("access_token_expires_at_utc")] DateTimeOffset AccessTokenExpiresAtUtc,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("refresh_token_expires_at_utc")] DateTimeOffset? RefreshTokenExpiresAtUtc);

public sealed record TokenRefreshResult(
    bool Provisioned,
    bool Refreshed,
    string? Error);
