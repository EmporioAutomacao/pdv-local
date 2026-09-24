using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SyncAgent.Provisioning;
using SyncAgent.Security;

namespace SyncAgent.Heartbeat;

/// <summary>
/// Confirma no ERP (<c>POST /v1/sync/agents/{id}/update:ack</c>) o andamento da
/// confirmacao de um <c>pending_update</c> em modo <c>confirm</c>. Melhor esforco:
/// falha de rede aqui nao bloqueia a decisao local (o gate local e' quem manda),
/// so deixa o ERP sem visibilidade ate o proximo heartbeat/ack bem-sucedido.
/// </summary>
public sealed class ErpUpdateAckClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ILogger<ErpUpdateAckClient> _logger;
    private readonly EffectiveSyncAgentConfigurationProvider _configProvider;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ErpCredentialProvider _credentialProvider;

    public ErpUpdateAckClient(
        ILogger<ErpUpdateAckClient> logger,
        EffectiveSyncAgentConfigurationProvider configProvider,
        IHttpClientFactory httpClientFactory,
        ErpCredentialProvider credentialProvider)
    {
        _logger = logger;
        _configProvider = configProvider;
        _httpClientFactory = httpClientFactory;
        _credentialProvider = credentialProvider;
    }

    public async Task AckAsync(string action, DateTimeOffset? scheduledAt, CancellationToken cancellationToken)
    {
        var syncOptions = _configProvider.GetCurrent();
        var erpApiBaseUri = new Uri(syncOptions.ErpApiBaseUrl, UriKind.Absolute);
        ErpCredentialProvider.EnsureHttpsOutsideLocalDevelopment(erpApiBaseUri);
        _credentialProvider.ValidateProvisionedForRemoteEndpoint(erpApiBaseUri);

        var instanceId = Uri.EscapeDataString(syncOptions.InstanceId);
        var body = new AckRequest(action, scheduledAt);

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post, $"/v1/sync/agents/{instanceId}/update:ack")
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
                    "update:ack ({Action}) retornou HTTP {Status}.", action, (int)response.StatusCode);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao enviar update:ack ({Action}).", action);
        }
    }

    private sealed record AckRequest(
        [property: JsonPropertyName("action")] string Action,
        [property: JsonPropertyName("scheduled_at")] DateTimeOffset? ScheduledAt);
}
