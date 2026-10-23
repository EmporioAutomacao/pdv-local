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

    private readonly object _cacheLock = new();
    private ArpaLojaListFetchResult? _lastSuccess;
    private DateTimeOffset _lastSuccessAtUtc;

    /// <summary>
    /// Tabelas de preco do ERP para a regra de bloqueio do coletor, sem uma
    /// chamada HTTP a cada ciclo de 30s: reaproveita o ultimo resultado bom por
    /// ate <paramref name="maxAge"/>. ERP inacessivel agora = ultimo resultado
    /// conhecido; nunca consultado com sucesso = null (nao bloqueia).
    /// </summary>
    public async Task<IReadOnlyList<ArpaTabelaPreco>?> GetTabelasPrecoAsync(TimeSpan maxAge, CancellationToken cancellationToken)
    {
        lock (_cacheLock)
        {
            if (_lastSuccess is not null && DateTimeOffset.UtcNow - _lastSuccessAtUtc < maxAge)
            {
                return _lastSuccess.TabelasPreco;
            }
        }

        try
        {
            var fresh = await FetchAsync(cancellationToken);
            if (fresh.Succeeded)
            {
                return fresh.TabelasPreco;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Configuracao local incompleta (ex.: mTLS) - mesma regra de ERP offline.
        }

        lock (_cacheLock)
        {
            return _lastSuccess?.TabelasPreco;
        }
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

            var result = ArpaLojaListFetchResult.Ok(body.Lojas, body.TabelasPreco);
            lock (_cacheLock)
            {
                _lastSuccess = result;
                _lastSuccessAtUtc = DateTimeOffset.UtcNow;
            }

            return result;
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

/// <param name="TabelasPreco">Null quando o ERP e anterior ao contrato 2.17.0 (nao envia a lista).</param>
public sealed record ArpaLojaListFetchResult(
    bool Succeeded,
    string? Error,
    IReadOnlyList<ArpaLoja>? Lojas,
    IReadOnlyList<ArpaTabelaPreco>? TabelasPreco = null)
{
    public static ArpaLojaListFetchResult Ok(IReadOnlyList<ArpaLoja> lojas, IReadOnlyList<ArpaTabelaPreco>? tabelasPreco = null) =>
        new(true, null, lojas, tabelasPreco);

    public static ArpaLojaListFetchResult Failed(string error) => new(false, error, null);
}

public sealed record ArpaLojaListResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("lojas")] IReadOnlyList<ArpaLoja>? Lojas,
    [property: JsonPropertyName("tabelas_preco")] IReadOnlyList<ArpaTabelaPreco>? TabelasPreco = null);

public sealed record ArpaTabelaPreco(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("nome")] string Nome,
    [property: JsonPropertyName("empresa_nome")] string? EmpresaNome = null);

public sealed record ArpaLoja(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("nome")] string Nome,
    [property: JsonPropertyName("empresa_nome")] string EmpresaNome,
    [property: JsonPropertyName("empresa_cnpj")] string? EmpresaCnpj = null,
    [property: JsonPropertyName("em_uso_por")] ArpaLojaEmUsoPor? EmUsoPor = null);

/// <summary>
/// Conexao Arpa de OUTRO SyncAgent que ja usa a Loja/Estoque (o ERP so deixa
/// um Estoque vinculado a uma conexao). Null = livre para este agente.
/// </summary>
public sealed record ArpaLojaEmUsoPor(
    [property: JsonPropertyName("conexao_nome")] string ConexaoNome,
    [property: JsonPropertyName("installation_label")] string InstallationLabel);
