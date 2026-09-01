using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SyncAgent.Provisioning;
using SyncAgent.Security;

namespace SyncAgent.Update;

/// <summary>
/// Busca em GET /v1/sync/agents/{instanceId}/latest-package a versao mais
/// recente publicada no ERP (SyncPackage.is_current), para o botao
/// "Atualizar App" da bandeja poder buscar sob demanda a ultima versao —
/// independente de qualquer atualizacao ja agendada especificamente para
/// esta instalacao via heartbeat (pending_update), que continua existindo
/// em paralelo sem alteracao.
/// </summary>
public sealed class ErpLatestPackageClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly EffectiveSyncAgentConfigurationProvider _effectiveConfigProvider;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ErpCredentialProvider _credentialProvider;

    public ErpLatestPackageClient(
        EffectiveSyncAgentConfigurationProvider effectiveConfigProvider,
        IHttpClientFactory httpClientFactory,
        ErpCredentialProvider credentialProvider)
    {
        _effectiveConfigProvider = effectiveConfigProvider;
        _httpClientFactory = httpClientFactory;
        _credentialProvider = credentialProvider;
    }

    public async Task<LatestPackageFetchResult> FetchAsync(CancellationToken cancellationToken)
    {
        var syncOptions = _effectiveConfigProvider.GetCurrent();
        if (!syncOptions.IsProvisioned || string.IsNullOrWhiteSpace(syncOptions.InstanceId))
        {
            return LatestPackageFetchResult.Failed("not_provisioned");
        }

        var erpApiBaseUri = new Uri(syncOptions.ErpApiBaseUrl, UriKind.Absolute);
        ErpCredentialProvider.EnsureHttpsOutsideLocalDevelopment(erpApiBaseUri);
        _credentialProvider.ValidateProvisionedForRemoteEndpoint(erpApiBaseUri);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        var escapedInstanceId = Uri.EscapeDataString(syncOptions.InstanceId);
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/v1/sync/agents/{escapedInstanceId}/latest-package");

        var authorization = _credentialProvider.CreateAuthorizationHeader();
        if (authorization is not null)
        {
            httpRequest.Headers.Authorization = authorization;
        }

        var client = _httpClientFactory.CreateClient(ErpLatestPackageHttpClient.Name);
        client.BaseAddress = erpApiBaseUri;
        client.Timeout = TimeSpan.FromSeconds(20);

        try
        {
            using var response = await client.SendAsync(httpRequest, timeout.Token);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return LatestPackageFetchResult.Failed("no_package_published");
            }

            if (!response.IsSuccessStatusCode)
            {
                return LatestPackageFetchResult.Failed($"http_{(int)response.StatusCode}");
            }

            var body = await response.Content.ReadFromJsonAsync<LatestPackageResponse>(
                JsonOptions,
                timeout.Token);
            if (body is null || string.IsNullOrWhiteSpace(body.Version) || string.IsNullOrWhiteSpace(body.DownloadUrl))
            {
                return LatestPackageFetchResult.Failed("invalid_response");
            }

            return LatestPackageFetchResult.Ok(body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return LatestPackageFetchResult.Failed(ex.GetType().Name);
        }
    }
}

public static class ErpLatestPackageHttpClient
{
    public const string Name = "ErpLatestPackage";
}

public sealed record LatestPackageFetchResult(bool Succeeded, string? Error, LatestPackageResponse? Package)
{
    public static LatestPackageFetchResult Ok(LatestPackageResponse package) => new(true, null, package);

    public static LatestPackageFetchResult Failed(string error) => new(false, error, null);
}

public sealed record LatestPackageResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("download_url")] string DownloadUrl,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("release_notes")] string? ReleaseNotes = null);
