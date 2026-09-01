using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SyncAgent.Configuration;
using SyncAgent.Security;

namespace SyncAgent.Provisioning;

/// <summary>
/// Busca em GET /v1/sync/agents/{instanceId}/arpa-connection a configuracao
/// de conexao Arpa Control (ArpaControlConexao) cadastrada no ERP para esta
/// instalacao, para uso pelo ArpaCollector no lugar de configuracao local
/// estatica. Requer a capability `arpa_collector` concedida na ativacao.
/// </summary>
public sealed class ArpaConnectionConfigClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly EffectiveSyncAgentConfigurationProvider _effectiveConfigProvider;
    private readonly IOptionsMonitor<ArpaCollectorOptions> _collectorOptions;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ErpCredentialProvider _credentialProvider;

    public ArpaConnectionConfigClient(
        EffectiveSyncAgentConfigurationProvider effectiveConfigProvider,
        IOptionsMonitor<ArpaCollectorOptions> collectorOptions,
        IHttpClientFactory httpClientFactory,
        ErpCredentialProvider credentialProvider)
    {
        _effectiveConfigProvider = effectiveConfigProvider;
        _collectorOptions = collectorOptions;
        _httpClientFactory = httpClientFactory;
        _credentialProvider = credentialProvider;
    }

    public async Task<ArpaConnectionConfigFetchResult> FetchAsync(CancellationToken cancellationToken)
    {
        var syncOptions = _effectiveConfigProvider.GetCurrent();
        if (!syncOptions.IsProvisioned || string.IsNullOrWhiteSpace(syncOptions.InstanceId))
        {
            return ArpaConnectionConfigFetchResult.Failed("not_provisioned");
        }

        var erpApiBaseUri = new Uri(syncOptions.ErpApiBaseUrl, UriKind.Absolute);
        ErpCredentialProvider.EnsureHttpsOutsideLocalDevelopment(erpApiBaseUri);
        _credentialProvider.ValidateProvisionedForRemoteEndpoint(erpApiBaseUri);

        var timeoutSeconds = Math.Max(1, _collectorOptions.CurrentValue.RemoteConfigTimeoutSeconds);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var escapedInstanceId = Uri.EscapeDataString(syncOptions.InstanceId);
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/v1/sync/agents/{escapedInstanceId}/arpa-connection");

        var authorization = _credentialProvider.CreateAuthorizationHeader();
        if (authorization is not null)
        {
            httpRequest.Headers.Authorization = authorization;
        }

        var client = _httpClientFactory.CreateClient(ArpaConnectionConfigHttpClient.Name);
        client.BaseAddress = erpApiBaseUri;
        client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);

        try
        {
            using var response = await client.SendAsync(httpRequest, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return ArpaConnectionConfigFetchResult.Failed($"http_{(int)response.StatusCode}");
            }

            var body = await response.Content.ReadFromJsonAsync<ArpaConnectionConfigResponse>(
                JsonOptions,
                timeout.Token);
            if (body?.Connection is null)
            {
                return ArpaConnectionConfigFetchResult.Failed("invalid_response");
            }

            return ArpaConnectionConfigFetchResult.Ok(body.Connection);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ArpaConnectionConfigFetchResult.Failed(ex.GetType().Name);
        }
    }
}

public static class ArpaConnectionConfigHttpClient
{
    public const string Name = "ArpaConnectionConfig";
}

public sealed record ArpaConnectionConfigFetchResult(bool Succeeded, string? Error, ArpaConnectionConfig? Connection)
{
    public static ArpaConnectionConfigFetchResult Ok(ArpaConnectionConfig connection) => new(true, null, connection);

    public static ArpaConnectionConfigFetchResult Failed(string error) => new(false, error, null);
}

public sealed record ArpaConnectionConfigResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("connection")] ArpaConnectionConfig? Connection);

public sealed record ArpaConnectionConfig(
    [property: JsonPropertyName("connection_id")] long ConnectionId,
    [property: JsonPropertyName("nome")] string Nome,
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("database")] string Database,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("controla_estoque")] bool ControlaEstoque,
    [property: JsonPropertyName("loja_erp_id")] long? LojaErpId,
    [property: JsonPropertyName("entities")] IReadOnlyList<ArpaConnectionEntity> Entities);

public sealed record ArpaConnectionEntity(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("entity_type")] string EntityType,
    [property: JsonPropertyName("query")] string Query);
