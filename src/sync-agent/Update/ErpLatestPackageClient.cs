using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SyncAgent.Provisioning;
using SyncAgent.Security;

namespace SyncAgent.Update;

/// <summary>
/// Busca em GET /v1/sync/agents/{instanceId}/available-packages a lista de
/// versoes permitidas para esta instalacao (curadoria do ERP), para o botao
/// "Atualizar App" da bandeja poder mostrar as opcoes e deixar o dono da loja
/// escolher — independente de qualquer atualizacao ja agendada
/// especificamente para esta instalacao via heartbeat (pending_update), que
/// continua existindo em paralelo sem alteracao.
///
/// Contrato 2.6.0+: o endpoint antigo GET .../latest-package (1 pacote so,
/// baseado no extinto SyncPackage.is_current) fica deprecated mas continua
/// existindo do lado erp. <see cref="FetchAvailableAsync"/> tenta o endpoint
/// novo primeiro e cai pro antigo (adaptando a resposta para uma lista de 1
/// item) se o ERP dessa instalacao ainda nao tiver sido atualizado.
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

    /// <summary>
    /// Lista de versoes disponiveis (contrato 2.6.0+), com fallback automatico
    /// para o endpoint antigo (1 pacote so) quando o ERP desta instalacao
    /// ainda nao expoe available-packages.
    /// </summary>
    public async Task<AvailablePackagesFetchResult> FetchAvailableAsync(CancellationToken cancellationToken)
    {
        var syncOptions = _effectiveConfigProvider.GetCurrent();
        if (!syncOptions.IsProvisioned || string.IsNullOrWhiteSpace(syncOptions.InstanceId))
        {
            return AvailablePackagesFetchResult.Failed("not_provisioned");
        }

        var erpApiBaseUri = new Uri(syncOptions.ErpApiBaseUrl, UriKind.Absolute);
        ErpCredentialProvider.EnsureHttpsOutsideLocalDevelopment(erpApiBaseUri);
        _credentialProvider.ValidateProvisionedForRemoteEndpoint(erpApiBaseUri);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        var escapedInstanceId = Uri.EscapeDataString(syncOptions.InstanceId);
        var client = _httpClientFactory.CreateClient(ErpLatestPackageHttpClient.Name);
        client.BaseAddress = erpApiBaseUri;
        client.Timeout = TimeSpan.FromSeconds(20);
        var authorization = _credentialProvider.CreateAuthorizationHeader();

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"/v1/sync/agents/{escapedInstanceId}/available-packages");
            if (authorization is not null)
            {
                request.Headers.Authorization = authorization;
            }

            using var response = await client.SendAsync(request, timeout.Token);

            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadFromJsonAsync<AvailablePackagesResponse>(JsonOptions, timeout.Token);
                if (body?.Packages is null)
                {
                    return AvailablePackagesFetchResult.Failed("invalid_response");
                }

                return AvailablePackagesFetchResult.Ok(body.CurrentVersion, body.Packages);
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                var errorCode = await TryReadErrorCodeAsync(response, timeout.Token);
                if (errorCode == "no_package_published")
                {
                    // Endpoint existe e respondeu de verdade: so nao ha pacote
                    // permitido pra esta instalacao. Nao e motivo pra cair no
                    // fallback antigo (que provavelmente daria o mesmo 404).
                    return AvailablePackagesFetchResult.Failed("no_package_published");
                }

                // 404 de rota inexistente (erp desta instalacao anterior ao
                // contrato 2.6.0) — cai pro endpoint antigo abaixo.
            }
            else
            {
                // Qualquer outro status (401/403/5xx/etc.) e um erro de verdade,
                // nao "endpoint nao existe" — nao faz sentido tentar o fallback
                // (provavelmente falharia pelo mesmo motivo).
                return AvailablePackagesFetchResult.Failed($"http_{(int)response.StatusCode}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Falha de rede/parse no endpoint novo — tenta o fallback antes de desistir.
        }

        return await FetchLegacyAsSingleItemListAsync(cancellationToken);
    }

    private async Task<AvailablePackagesFetchResult> FetchLegacyAsSingleItemListAsync(CancellationToken cancellationToken)
    {
        var legacy = await FetchAsync(cancellationToken);
        if (!legacy.Succeeded || legacy.Package is null)
        {
            return AvailablePackagesFetchResult.Failed(legacy.Error ?? "unknown");
        }

        var item = new AvailablePackageItem(
            legacy.Package.Version,
            legacy.Package.DownloadUrl,
            legacy.Package.Sha256,
            legacy.Package.ReleaseNotes,
            ErpMinimo: null,
            Blocked: false,
            BlockedReason: null);

        return AvailablePackagesFetchResult.Ok(currentVersion: null, new[] { item });
    }

    private static async Task<string?> TryReadErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<ErrorResponse>(JsonOptions, cancellationToken);
            return body?.Error;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Endpoint antigo (deprecated no contrato 2.6.0+, mantido para
    /// compatibilidade): 1 pacote so. Usado diretamente pelo fallback de
    /// <see cref="FetchAvailableAsync"/>.
    /// </summary>
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
            if (response.StatusCode == HttpStatusCode.NotFound)
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

public sealed record AvailablePackagesFetchResult(bool Succeeded, string? Error, string? CurrentVersion, IReadOnlyList<AvailablePackageItem>? Packages)
{
    public static AvailablePackagesFetchResult Ok(string? currentVersion, IReadOnlyList<AvailablePackageItem> packages) =>
        new(true, null, currentVersion, packages);

    public static AvailablePackagesFetchResult Failed(string error) => new(false, error, null, null);
}

public sealed record AvailablePackagesResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("current_version")] string? CurrentVersion,
    [property: JsonPropertyName("packages")] IReadOnlyList<AvailablePackageItem> Packages);

public sealed record AvailablePackageItem(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("download_url")] string DownloadUrl,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("release_notes")] string? ReleaseNotes,
    [property: JsonPropertyName("erp_minimo")] string? ErpMinimo,
    [property: JsonPropertyName("blocked")] bool Blocked,
    [property: JsonPropertyName("blocked_reason")] string? BlockedReason);

file sealed record ErrorResponse([property: JsonPropertyName("error")] string? Error);
