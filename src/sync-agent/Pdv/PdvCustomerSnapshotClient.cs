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

    // 200 paginas * 5000 = 1M clientes: teto de seguranca contra loop infinito
    // se o ERP nunca devolver has_more=false.
    private const int MaxPages = 200;

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

        var client = _httpClientFactory.CreateClient(PdvCustomerSnapshotHttpClient.Name);
        client.BaseAddress = erpApiBaseUri;
        client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);

        var totalReceived = 0;
        var totalImported = 0;
        long afterId = 0;
        var page = 0;
        var lastStatusCode = 0;

        try
        {
            while (true)
            {
                page++;

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

                using var httpRequest = new HttpRequestMessage(
                    HttpMethod.Get,
                    BuildRequestPath(syncOptions.InstanceId, options.Limit, afterId));
                var authorization = _credentialProvider.CreateAuthorizationHeader();
                if (authorization is not null)
                {
                    httpRequest.Headers.Authorization = authorization;
                }

                using var response = await client.SendAsync(httpRequest, timeout.Token);
                lastStatusCode = (int)response.StatusCode;

                if (response.StatusCode == HttpStatusCode.NotFound && page == 1)
                {
                    // ERP antigo sem o endpoint (contrato < 1.11.0): nao e erro do ciclo.
                    _logger.LogInformation("ERP does not support the PDV customers snapshot yet (HTTP 404). Skipping import.");
                    await _localStore.UpsertPdvCustomerSnapshotStateAsync(
                        DateTimeOffset.UtcNow, false, 0, lastStatusCode, "not_supported", cancellationToken);
                    return new PdvCustomerSnapshotSummary(true, false, 0, 0, null);
                }

                if (!response.IsSuccessStatusCode)
                {
                    var error = $"ERP PDV customers snapshot returned HTTP {lastStatusCode} (pagina {page}).";
                    await _localStore.UpsertPdvCustomerSnapshotStateAsync(
                        DateTimeOffset.UtcNow, false, totalImported, lastStatusCode, error, cancellationToken);
                    return new PdvCustomerSnapshotSummary(true, false, totalReceived, totalImported, error);
                }

                var snapshot = await response.Content.ReadFromJsonAsync<PdvCustomersSnapshotResponse>(JsonOptions, cancellationToken);
                if (snapshot is null || snapshot.InstanceId != syncOptions.InstanceId)
                {
                    const string error = "ERP PDV customers snapshot returned an invalid response.";
                    await _localStore.UpsertPdvCustomerSnapshotStateAsync(
                        DateTimeOffset.UtcNow, false, totalImported, lastStatusCode, error, cancellationToken);
                    return new PdvCustomerSnapshotSummary(true, false, totalReceived, totalImported, error);
                }

                var imported = await _localStore.UpsertPdvCustomersAsync(snapshot.Customers, cancellationToken);
                totalImported += imported;
                totalReceived += snapshot.Customers.Count;

                if (!snapshot.HasMore)
                {
                    break;
                }

                // Cursor: next_after_id do ERP; se ausente (ERP mais antigo),
                // deriva do maior customer_id da pagina.
                var nextAfterId = snapshot.NextAfterId
                    ?? snapshot.Customers
                        .Select(c => long.TryParse(c.CustomerId, out var v) ? v : 0L)
                        .DefaultIfEmpty(0L)
                        .Max();

                if (nextAfterId <= afterId)
                {
                    var error = "ERP PDV customers snapshot: cursor de paginacao nao avancou (has_more=true sem next_after_id valido).";
                    await _localStore.UpsertPdvCustomerSnapshotStateAsync(
                        DateTimeOffset.UtcNow, false, totalImported, lastStatusCode, error, cancellationToken);
                    return new PdvCustomerSnapshotSummary(true, false, totalReceived, totalImported, error);
                }

                afterId = nextAfterId;

                if (page >= MaxPages)
                {
                    var error = $"ERP PDV customers snapshot: parou em {MaxPages} paginas com has_more ainda true.";
                    await _localStore.UpsertPdvCustomerSnapshotStateAsync(
                        DateTimeOffset.UtcNow, false, totalImported, lastStatusCode, error, cancellationToken);
                    return new PdvCustomerSnapshotSummary(true, false, totalReceived, totalImported, error);
                }
            }

            await _localStore.UpsertPdvCustomerSnapshotStateAsync(
                DateTimeOffset.UtcNow, true, totalImported, lastStatusCode, null, cancellationToken);

            _logger.LogInformation(
                "PDV customers imported. Pages={Pages}; Received={Received}; Imported={Imported}",
                page, totalReceived, totalImported);

            return new PdvCustomerSnapshotSummary(true, true, totalReceived, totalImported, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await _localStore.UpsertPdvCustomerSnapshotStateAsync(
                DateTimeOffset.UtcNow, false, totalImported, null, ex.GetType().Name, cancellationToken);

            _logger.LogWarning(ex, "PDV customers snapshot import failed.");
            return new PdvCustomerSnapshotSummary(true, false, totalReceived, totalImported, ex.GetType().Name);
        }
    }

    private static string BuildRequestPath(string instanceId, int limit, long afterId)
    {
        // Snapshot completo por contrato (1.11.0): since_utc e ignorado pelo ERP.
        // Paginacao por cursor after_id (contrato 2.8.0).
        var path = $"/v1/sync/pdv/customers:snapshot?instance_id={Uri.EscapeDataString(instanceId)}&limit={limit}";
        return afterId > 0 ? $"{path}&after_id={afterId}" : path;
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
    [property: JsonPropertyName("has_more")] bool HasMore,
    [property: JsonPropertyName("next_after_id")] long? NextAfterId);

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
