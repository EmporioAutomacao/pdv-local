using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SyncAgent.Configuration;
using SyncAgent.Persistence;
using SyncAgent.Provisioning;
using SyncAgent.Security;

namespace SyncAgent.Pdv;

public sealed class PdvCustomerSnapshotClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ILogger<PdvCustomerSnapshotClient> _logger;
    private readonly EffectiveSyncAgentConfigurationProvider _effectiveConfigProvider;
    private readonly IOptionsMonitor<ErpPdvSnapshotOptions> _snapshotOptions;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly LocalSyncStore _localStore;
    private readonly ErpCredentialProvider _credentialProvider;

    public PdvCustomerSnapshotClient(
        ILogger<PdvCustomerSnapshotClient> logger,
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

    public async Task<PdvCustomerSnapshotSummary> ImportAsync(CancellationToken cancellationToken)
    {
        var options = _snapshotOptions.CurrentValue;
        if (!options.Enabled)
        {
            return PdvCustomerSnapshotSummary.Disabled;
        }

        var syncOptions = _effectiveConfigProvider.GetCurrent();
        var erpApiBaseUri = new Uri(syncOptions.ErpApiBaseUrl, UriKind.Absolute);
        ErpCredentialProvider.EnsureHttpsOutsideLocalDevelopment(erpApiBaseUri);
        _credentialProvider.ValidateProvisionedForRemoteEndpoint(erpApiBaseUri);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

        var requestPath = BuildRequestPath(syncOptions.InstanceId, options.Limit);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, requestPath);

        var authorization = _credentialProvider.CreateAuthorizationHeader();
        if (authorization is not null)
        {
            httpRequest.Headers.Authorization = authorization;
        }

        var client = _httpClientFactory.CreateClient(PdvCustomerSnapshotHttpClient.Name);
        client.BaseAddress = erpApiBaseUri;
        client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);

        try
        {
            using var response = await client.SendAsync(httpRequest, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // ERP antigo sem o endpoint (contrato < 1.11.0): nao e um erro do
                // ciclo — o PDV segue operando sem cadastro local de clientes.
                _logger.LogInformation(
                    "ERP does not support the PDV customers snapshot yet (HTTP 404). Skipping import.");
                await _localStore.UpsertPdvCustomerSnapshotStateAsync(
                    DateTimeOffset.UtcNow,
                    false,
                    0,
                    (int)response.StatusCode,
                    "not_supported",
                    cancellationToken);
                return new PdvCustomerSnapshotSummary(true, false, 0, 0, null);
            }

            if (!response.IsSuccessStatusCode)
            {
                var error = $"ERP PDV customers snapshot returned HTTP {(int)response.StatusCode}.";
                await _localStore.UpsertPdvCustomerSnapshotStateAsync(
                    DateTimeOffset.UtcNow,
                    false,
                    0,
                    (int)response.StatusCode,
                    error,
                    cancellationToken);
                return new PdvCustomerSnapshotSummary(true, false, 0, 0, error);
            }

            var snapshot = await response.Content.ReadFromJsonAsync<PdvCustomersSnapshotResponse>(
                JsonOptions,
                cancellationToken);
            if (snapshot is null || snapshot.InstanceId != syncOptions.InstanceId)
            {
                const string error = "ERP PDV customers snapshot returned an invalid response.";
                await _localStore.UpsertPdvCustomerSnapshotStateAsync(
                    DateTimeOffset.UtcNow,
                    false,
                    0,
                    (int)response.StatusCode,
                    error,
                    cancellationToken);
                return new PdvCustomerSnapshotSummary(true, false, 0, 0, error);
            }

            var imported = await _localStore.UpsertPdvCustomersAsync(snapshot.Customers, cancellationToken);
            var snapshotAtUtc = DateTimeOffset.UtcNow;
            var hasMoreError = snapshot.HasMore
                ? "ERP PDV customers snapshot returned has_more=true; increase the snapshot limit."
                : null;
            await _localStore.UpsertPdvCustomerSnapshotStateAsync(
                snapshotAtUtc,
                hasMoreError is null,
                imported,
                (int)response.StatusCode,
                hasMoreError,
                cancellationToken);

            _logger.LogInformation(
                "PDV customers imported. SnapshotId={SnapshotId}; Received={Received}; Imported={Imported}; HasMore={HasMore}",
                snapshot.SnapshotId,
                snapshot.Customers.Count,
                imported,
                snapshot.HasMore);

            return new PdvCustomerSnapshotSummary(
                true,
                hasMoreError is null,
                snapshot.Customers.Count,
                imported,
                hasMoreError);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await _localStore.UpsertPdvCustomerSnapshotStateAsync(
                DateTimeOffset.UtcNow,
                false,
                0,
                null,
                ex.GetType().Name,
                cancellationToken);

            _logger.LogWarning(ex, "PDV customers snapshot import failed.");
            return new PdvCustomerSnapshotSummary(true, false, 0, 0, ex.GetType().Name);
        }
    }

    private static string BuildRequestPath(string instanceId, int limit)
    {
        // Snapshot completo por contrato (1.11.0): since_utc e ignorado pelo ERP,
        // entao nao ha watermark a enviar.
        return $"/v1/sync/pdv/customers:snapshot?instance_id={Uri.EscapeDataString(instanceId)}&limit={limit}";
    }
}

public static class PdvCustomerSnapshotHttpClient
{
    public const string Name = "PdvCustomerSnapshot";
}

public sealed record PdvCustomerSnapshotSummary(
    bool Enabled,
    bool Succeeded,
    int Received,
    int Imported,
    string? LastError)
{
    public static PdvCustomerSnapshotSummary Disabled { get; } = new(false, false, 0, 0, null);
}

public sealed record PdvCustomersSnapshotResponse(
    [property: JsonPropertyName("snapshot_id")] string SnapshotId,
    [property: JsonPropertyName("tenant_id")] string TenantId,
    [property: JsonPropertyName("instance_id")] string InstanceId,
    [property: JsonPropertyName("generated_at_utc")] DateTimeOffset GeneratedAtUtc,
    [property: JsonPropertyName("customers")] IReadOnlyList<PdvCustomerSnapshotItem> Customers,
    [property: JsonPropertyName("has_more")] bool HasMore);

public sealed record PdvCustomerSnapshotItem(
    [property: JsonPropertyName("customer_id")] string CustomerId,
    [property: JsonPropertyName("document")] string? Document,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("trade_name")] string? TradeName,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("phone")] string? Phone,
    [property: JsonPropertyName("active")] bool Active,
    [property: JsonPropertyName("updated_at_utc")] DateTimeOffset UpdatedAtUtc,
    [property: JsonPropertyName("deleted_at_utc")] DateTimeOffset? DeletedAtUtc,
    [property: JsonPropertyName("payload")] JsonElement? Payload);
