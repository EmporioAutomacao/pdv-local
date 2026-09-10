using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SyncAgent.Collectors;
using SyncAgent.Provisioning;
using SyncAgent.Security;

namespace SyncAgent.Heartbeat;

/// <summary>
/// Confirma no ERP (<c>POST /v1/sync/agents/{id}/resyncs:ack</c>) o resultado dos
/// <c>pending_resyncs</c> processados pelo <see cref="ArpaResyncProcessor"/>.
/// </summary>
public sealed class ErpResyncAckClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ILogger<ErpResyncAckClient> _logger;
    private readonly EffectiveSyncAgentConfigurationProvider _configProvider;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ErpCredentialProvider _credentialProvider;

    public ErpResyncAckClient(
        ILogger<ErpResyncAckClient> logger,
        EffectiveSyncAgentConfigurationProvider configProvider,
        IHttpClientFactory httpClientFactory,
        ErpCredentialProvider credentialProvider)
    {
        _logger = logger;
        _configProvider = configProvider;
        _httpClientFactory = httpClientFactory;
        _credentialProvider = credentialProvider;
    }

    public async Task AckAsync(IReadOnlyList<ResyncResult> results, CancellationToken cancellationToken)
    {
        if (results.Count == 0)
        {
            return;
        }

        var syncOptions = _configProvider.GetCurrent();
        var erpApiBaseUri = new Uri(syncOptions.ErpApiBaseUrl, UriKind.Absolute);
        ErpCredentialProvider.EnsureHttpsOutsideLocalDevelopment(erpApiBaseUri);
        _credentialProvider.ValidateProvisionedForRemoteEndpoint(erpApiBaseUri);

        var instanceId = Uri.EscapeDataString(syncOptions.InstanceId);
        var body = new AckRequest(results
            .Select(r => new AckItem(r.Id, r.Status, string.IsNullOrEmpty(r.Detail) ? null : r.Detail))
            .ToList());

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post, $"/v1/sync/agents/{instanceId}/resyncs:ack")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json")
        };

        var authorization = _credentialProvider.CreateAuthorizationHeader();
        if (authorization is not null)
        {
            httpRequest.Headers.Authorization = authorization;
        }

        var client = _httpClientFactory.CreateClient(ErpHeartbeatHttpClient.Name);
        client.BaseAddress = erpApiBaseUri;

        try
        {
            using var response = await client.SendAsync(httpRequest, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "resyncs:ack retornou HTTP {Status} para {Count} resultado(s) - serao reenviados no proximo heartbeat.",
                    (int)response.StatusCode, results.Count);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao enviar resyncs:ack - serao reenviados no proximo heartbeat.");
        }
    }

    private sealed record AckRequest(
        [property: JsonPropertyName("results")] IReadOnlyList<AckItem> Results);

    private sealed record AckItem(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("detail")] string? Detail);
}
