using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SyncAgent.Security;

namespace SyncAgent.Provisioning;

/// <summary>
/// Busca em GET /v1/sync/agents/{instanceId}/lojas a lista de Lojas/Estoque
/// cadastradas no ERP, para a aba Configuracoes > Arpa oferecer como um
/// dropdown ao vincular cada conexao local a uma Loja/Estoque real (em vez de
/// o operador digitar o nome a mao e arriscar nao bater com o cadastro do
/// ERP, criando sem querer uma Loja nova por typo).
/// </summary>
public sealed class ArpaLojaListClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly EffectiveSyncAgentConfigurationProvider _effectiveConfigProvider;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ErpCredentialProvider _credentialProvider;

    public ArpaLojaListClient(
        EffectiveSyncAgentConfigurationProvider effectiveConfigProvider,
        IHttpClientFactory httpClientFactory,
        ErpCredentialProvider credentialProvider)
    {
        _effectiveConfigProvider = effectiveConfigProvider;
        _httpClientFactory = httpClientFactory;
        _credentialProvider = credentialProvider;
    }

    public async Task<ArpaLojaListFetchResult> FetchAsync(CancellationToken cancellationToken)
    {
        var syncOptions = _effectiveConfigProvider.GetCurrent();
        if (!syncOptions.IsProvisioned || string.IsNullOrWhiteSpace(syncOptions.InstanceId))
        {
            return ArpaLojaListFetchResult.Failed("not_provisioned");
        }

        var erpApiBaseUri = new Uri(syncOptions.ErpApiBaseUrl, UriKind.Absolute);
        ErpCredentialProvider.EnsureHttpsOutsideLocalDevelopment(erpApiBaseUri);
        _credentialProvider.ValidateProvisionedForRemoteEndpoint(erpApiBaseUri);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        var escapedInstanceId = Uri.EscapeDataString(syncOptions.InstanceId);
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/v1/sync/agents/{escapedInstanceId}/lojas");

        var authorization = _credentialProvider.CreateAuthorizationHeader();
        if (authorization is not null)
        {
            httpRequest.Headers.Authorization = authorization;
        }

        var client = _httpClientFactory.CreateClient(ArpaLojaListHttpClient.Name);
        client.BaseAddress = erpApiBaseUri;
        client.Timeout = TimeSpan.FromSeconds(20);

        try
        {
            using var response = await client.SendAsync(httpRequest, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return ArpaLojaListFetchResult.Failed($"http_{(int)response.StatusCode}");
            }

            var body = await response.Content.ReadFromJsonAsync<ArpaLojaListResponse>(
                JsonOptions,
                timeout.Token);
            if (body?.Lojas is null)
            {
                return ArpaLojaListFetchResult.Failed("invalid_response");
            }

            return ArpaLojaListFetchResult.Ok(body.Lojas);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ArpaLojaListFetchResult.Failed(ex.GetType().Name);
        }
    }
}

public static class ArpaLojaListHttpClient
{
    public const string Name = "ArpaLojaList";
}

public sealed record ArpaLojaListFetchResult(bool Succeeded, string? Error, IReadOnlyList<ArpaLoja>? Lojas)
{
    public static ArpaLojaListFetchResult Ok(IReadOnlyList<ArpaLoja> lojas) => new(true, null, lojas);

    public static ArpaLojaListFetchResult Failed(string error) => new(false, error, null);
}

public sealed record ArpaLojaListResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("lojas")] IReadOnlyList<ArpaLoja>? Lojas);

public sealed record ArpaLoja(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("nome")] string Nome,
    [property: JsonPropertyName("empresa_nome")] string EmpresaNome);
