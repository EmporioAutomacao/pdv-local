using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SyncAgent.Configuration;
using SyncAgent.Persistence;
using SyncAgent.Provisioning;
using SyncAgent.Security;

namespace SyncAgent.Pdv;

public sealed class PdvPaymentMethodsSnapshotClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ILogger<PdvPaymentMethodsSnapshotClient> _logger;
    private readonly EffectiveSyncAgentConfigurationProvider _effectiveConfigProvider;
    private readonly IOptionsMonitor<ErpPdvSnapshotOptions> _snapshotOptions;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly LocalSyncStore _localStore;
    private readonly ErpCredentialProvider _credentialProvider;

    public PdvPaymentMethodsSnapshotClient(
        ILogger<PdvPaymentMethodsSnapshotClient> logger,
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

    public async Task<PdvPaymentMethodsSnapshotSummary> ImportAsync(CancellationToken cancellationToken)
    {
        var options = _snapshotOptions.CurrentValue;
        if (!options.Enabled)
        {
            return PdvPaymentMethodsSnapshotSummary.Disabled;
        }

        var syncOptions = _effectiveConfigProvider.GetCurrent();
        var erpApiBaseUri = new Uri(syncOptions.ErpApiBaseUrl, UriKind.Absolute);
        ErpCredentialProvider.EnsureHttpsOutsideLocalDevelopment(erpApiBaseUri);
        _credentialProvider.ValidateProvisionedForRemoteEndpoint(erpApiBaseUri);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

        var sinceUtc = await _localStore.GetDateTimeOffsetStateOrNullAsync(
            "pdv.payment_methods.last_snapshot_at_utc",
            cancellationToken);

        var requestPath = BuildRequestPath(syncOptions.InstanceId, sinceUtc, options.Limit);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, requestPath);

        var authorization = _credentialProvider.CreateAuthorizationHeader();
        if (authorization is not null)
        {
            httpRequest.Headers.Authorization = authorization;
        }

        var client = _httpClientFactory.CreateClient(PdvPaymentMethodsSnapshotHttpClient.Name);
        client.BaseAddress = erpApiBaseUri;
        client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);

        try
        {
            using var response = await client.SendAsync(httpRequest, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var error = $"ERP PDV payment methods snapshot returned HTTP {(int)response.StatusCode}.";
                await _localStore.UpsertPdvPaymentMethodsSnapshotStateAsync(
                    DateTimeOffset.UtcNow,
                    false,
                    0,
                    (int)response.StatusCode,
                    error,
                    cancellationToken);
                return new PdvPaymentMethodsSnapshotSummary(true, false, 0, 0, error);
            }

            var snapshot = await response.Content.ReadFromJsonAsync<PdvPaymentMethodsSnapshotResponse>(
                JsonOptions,
                cancellationToken);
            if (snapshot is null || snapshot.InstanceId != syncOptions.InstanceId)
            {
                const string error = "ERP PDV payment methods snapshot returned an invalid response.";
                await _localStore.UpsertPdvPaymentMethodsSnapshotStateAsync(
                    DateTimeOffset.UtcNow,
                    false,
                    0,
                    (int)response.StatusCode,
                    error,
                    cancellationToken);
                return new PdvPaymentMethodsSnapshotSummary(true, false, 0, 0, error);
            }

            var imported = await _localStore.UpsertPdvPaymentMethodsAsync(
                snapshot.PaymentSpecies,
                snapshot.PaymentConditions,
                cancellationToken);
            var snapshotAtUtc = DateTimeOffset.UtcNow;
            var hasMoreError = snapshot.HasMore
                ? "ERP PDV payment methods snapshot returned has_more=true; watermark was not advanced."
                : null;
            await _localStore.UpsertPdvPaymentMethodsSnapshotStateAsync(
                snapshotAtUtc,
                hasMoreError is null,
                imported,
                (int)response.StatusCode,
                hasMoreError,
                cancellationToken);
            if (hasMoreError is null)
            {
                await _localStore.SetDateTimeOffsetStateAsync(
                    "pdv.payment_methods.last_snapshot_at_utc",
                    snapshotAtUtc,
                    cancellationToken);
            }

            var received = snapshot.PaymentSpecies.Count + snapshot.PaymentConditions.Count;
            _logger.LogInformation(
                "PDV payment methods imported. SnapshotId={SnapshotId}; Species={Species}; Conditions={Conditions}; Imported={Imported}; HasMore={HasMore}",
                snapshot.SnapshotId,
                snapshot.PaymentSpecies.Count,
                snapshot.PaymentConditions.Count,
                imported,
                snapshot.HasMore);

            return new PdvPaymentMethodsSnapshotSummary(
                true,
                hasMoreError is null,
                received,
                imported,
                hasMoreError);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await _localStore.UpsertPdvPaymentMethodsSnapshotStateAsync(
                DateTimeOffset.UtcNow,
                false,
                0,
                null,
                ex.GetType().Name,
                cancellationToken);

            _logger.LogWarning(ex, "PDV payment methods snapshot import failed.");
            return new PdvPaymentMethodsSnapshotSummary(true, false, 0, 0, ex.GetType().Name);
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

        return $"/v1/sync/pdv/payment-methods:snapshot?{string.Join("&", query)}";
    }
}

public static class PdvPaymentMethodsSnapshotHttpClient
{
    public const string Name = "PdvPaymentMethodsSnapshot";
}

public sealed record PdvPaymentMethodsSnapshotSummary(
    bool Enabled,
    bool Succeeded,
    int Received,
    int Imported,
    string? LastError)
{
    public static PdvPaymentMethodsSnapshotSummary Disabled { get; } = new(false, false, 0, 0, null);
}

public sealed record PdvPaymentMethodsSnapshotResponse(
    [property: JsonPropertyName("snapshot_id")] string SnapshotId,
    [property: JsonPropertyName("tenant_id")] string TenantId,
    [property: JsonPropertyName("instance_id")] string InstanceId,
    [property: JsonPropertyName("generated_at_utc")] DateTimeOffset GeneratedAtUtc,
    [property: JsonPropertyName("payment_species")] IReadOnlyList<PdvPaymentSpeciesSnapshotItem> PaymentSpecies,
    [property: JsonPropertyName("payment_conditions")] IReadOnlyList<PdvPaymentConditionSnapshotItem> PaymentConditions,
    [property: JsonPropertyName("has_more")] bool HasMore);

public sealed record PdvPaymentSpeciesSnapshotItem(
    [property: JsonPropertyName("species_id")] string SpeciesId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("requires_tef")] bool RequiresTef,
    [property: JsonPropertyName("allows_change")] bool AllowsChange,
    [property: JsonPropertyName("active")] bool Active,
    [property: JsonPropertyName("updated_at_utc")] DateTimeOffset UpdatedAtUtc,
    [property: JsonPropertyName("deleted_at_utc")] DateTimeOffset? DeletedAtUtc,
    [property: JsonPropertyName("payload")] JsonElement? Payload);

public sealed record PdvPaymentConditionSnapshotItem(
    [property: JsonPropertyName("condition_id")] string ConditionId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("installments")] int Installments,
    [property: JsonPropertyName("first_due_days")] int FirstDueDays,
    [property: JsonPropertyName("interval_days")] int IntervalDays,
    [property: JsonPropertyName("active")] bool Active,
    [property: JsonPropertyName("updated_at_utc")] DateTimeOffset UpdatedAtUtc,
    [property: JsonPropertyName("deleted_at_utc")] DateTimeOffset? DeletedAtUtc,
    [property: JsonPropertyName("payload")] JsonElement? Payload);
