using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SyncAgent.Configuration;
using SyncAgent.Persistence;
using SyncAgent.Provisioning;
using SyncAgent.Security;

namespace SyncAgent.Pdv;

public sealed class PdvProductSnapshotClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ILogger<PdvProductSnapshotClient> _logger;
    private readonly EffectiveSyncAgentConfigurationProvider _effectiveConfigProvider;
    private readonly IOptionsMonitor<ErpPdvSnapshotOptions> _snapshotOptions;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly LocalSyncStore _localStore;
    private readonly ErpCredentialProvider _credentialProvider;

    public PdvProductSnapshotClient(
        ILogger<PdvProductSnapshotClient> logger,
        EffectiveSyncAgentConfigurationProvider effectiveConfigProvider,
        IOptionsMonitor<ErpPdvSnapshotOptions> snapshotOptions,
        IHttpClientFactory httpClientFactory,
        LocalSyncStore localStore,
        ErpCredentialProvider credentialProvider)
    {
        _logger = logger;
        _effectiveConfigProvider = effectiveConfigProvider;
        _snapshotOptions = snapshotOptions;
        _httpClientFactory = httpClientFactory;
        _localStore = localStore;
        _credentialProvider = credentialProvider;
    }

    public async Task<PdvProductSnapshotSummary> ImportAsync(CancellationToken cancellationToken)
    {
        var options = _snapshotOptions.CurrentValue;
        if (!options.Enabled)
        {
            return PdvProductSnapshotSummary.Disabled;
        }

        var syncOptions = _effectiveConfigProvider.GetCurrent();
        var erpApiBaseUri = new Uri(syncOptions.ErpApiBaseUrl, UriKind.Absolute);
        ErpCredentialProvider.EnsureHttpsOutsideLocalDevelopment(erpApiBaseUri);
        _credentialProvider.ValidateProvisionedForRemoteEndpoint(erpApiBaseUri);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

        var sinceUtc = await _localStore.GetDateTimeOffsetStateOrNullAsync(
            "pdv.products.last_snapshot_at_utc",
            cancellationToken);

        var requestPath = BuildRequestPath(syncOptions.InstanceId, sinceUtc, options.Limit);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, requestPath);

        var authorization = _credentialProvider.CreateAuthorizationHeader();
        if (authorization is not null)
        {
            httpRequest.Headers.Authorization = authorization;
        }

        var client = _httpClientFactory.CreateClient(PdvProductSnapshotHttpClient.Name);
        client.BaseAddress = erpApiBaseUri;
        client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);

        try
        {
            using var response = await client.SendAsync(httpRequest, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var error = $"ERP PDV products snapshot returned HTTP {(int)response.StatusCode}.";
                await _localStore.UpsertPdvProductSnapshotStateAsync(
                    DateTimeOffset.UtcNow,
                    false,
                    0,
                    (int)response.StatusCode,
                    error,
                    cancellationToken);
                return new PdvProductSnapshotSummary(true, false, 0, 0, error);
            }

            var snapshot = await response.Content.ReadFromJsonAsync<PdvProductsSnapshotResponse>(
                JsonOptions,
                cancellationToken);
            if (snapshot is null || snapshot.InstanceId != syncOptions.InstanceId)
            {
                const string error = "ERP PDV products snapshot returned an invalid response.";
                await _localStore.UpsertPdvProductSnapshotStateAsync(
                    DateTimeOffset.UtcNow,
                    false,
                    0,
                    (int)response.StatusCode,
                    error,
                    cancellationToken);
                return new PdvProductSnapshotSummary(true, false, 0, 0, error);
            }

            var imported = await _localStore.UpsertPdvProductsAsync(snapshot.Products, cancellationToken);
            var snapshotAtUtc = DateTimeOffset.UtcNow;
            var hasMoreError = snapshot.HasMore
                ? "ERP PDV products snapshot returned has_more=true; watermark was not advanced."
                : null;
            await _localStore.UpsertPdvProductSnapshotStateAsync(
                snapshotAtUtc,
                hasMoreError is null,
                imported,
                (int)response.StatusCode,
                hasMoreError,
                cancellationToken);
            if (hasMoreError is null)
            {
                await _localStore.SetDateTimeOffsetStateAsync(
                    "pdv.products.last_snapshot_at_utc",
                    snapshotAtUtc,
                    cancellationToken);
            }

            _logger.LogInformation(
                "PDV products imported. SnapshotId={SnapshotId}; Imported={Imported}; HasMore={HasMore}",
                snapshot.SnapshotId,
                imported,
                snapshot.HasMore);

            return new PdvProductSnapshotSummary(
                true,
                hasMoreError is null,
                snapshot.Products.Count,
                imported,
                hasMoreError);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await _localStore.UpsertPdvProductSnapshotStateAsync(
                DateTimeOffset.UtcNow,
                false,
                0,
                null,
                ex.GetType().Name,
                cancellationToken);

            _logger.LogWarning(ex, "PDV products snapshot import failed.");
            return new PdvProductSnapshotSummary(true, false, 0, 0, ex.GetType().Name);
        }
    }

    private static string BuildRequestPath(string instanceId, DateTimeOffset? sinceUtc, int limit)
    {
        var query = new List<string>
        {
            $"instance_id={Uri.EscapeDataString(instanceId)}",
            $"limit={limit}"
        };

        if (sinceUtc is not null)
        {
            query.Add($"since_utc={Uri.EscapeDataString(sinceUtc.Value.ToUniversalTime().ToString("O"))}");
        }

        return $"/v1/sync/pdv/products:snapshot?{string.Join("&", query)}";
    }
}

public static class PdvProductSnapshotHttpClient
{
    public const string Name = "PdvProductSnapshot";
}

public sealed record PdvProductSnapshotSummary(
    bool Enabled,
    bool Succeeded,
    int Received,
    int Imported,
    string? LastError)
{
    public static PdvProductSnapshotSummary Disabled { get; } = new(false, false, 0, 0, null);
}

public sealed record PdvProductsSnapshotResponse(
    [property: JsonPropertyName("snapshot_id")] string SnapshotId,
    [property: JsonPropertyName("tenant_id")] string TenantId,
    [property: JsonPropertyName("instance_id")] string InstanceId,
    [property: JsonPropertyName("generated_at_utc")] DateTimeOffset GeneratedAtUtc,
    [property: JsonPropertyName("products")] IReadOnlyList<PdvProductSnapshotItem> Products,
    [property: JsonPropertyName("has_more")] bool HasMore);

public sealed record PdvProductSnapshotItem(
    [property: JsonPropertyName("product_id")] string ProductId,
    [property: JsonPropertyName("external_key")] string? ExternalKey,
    [property: JsonPropertyName("sku")] string? Sku,
    [property: JsonPropertyName("barcode")] string? Barcode,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("price")] decimal Price,
    [property: JsonPropertyName("active")] bool Active,
    [property: JsonPropertyName("updated_at_utc")] DateTimeOffset UpdatedAtUtc,
    [property: JsonPropertyName("deleted_at_utc")] DateTimeOffset? DeletedAtUtc,
    [property: JsonPropertyName("payload")] JsonElement? Payload);
