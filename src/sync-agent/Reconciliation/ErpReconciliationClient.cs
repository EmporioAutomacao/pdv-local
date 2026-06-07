using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SyncAgent.Configuration;
using SyncAgent.Persistence;
using SyncAgent.Provisioning;
using SyncAgent.Security;

namespace SyncAgent.Reconciliation;

public sealed class ErpReconciliationClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ILogger<ErpReconciliationClient> _logger;
    private readonly EffectiveSyncAgentConfigurationProvider _effectiveConfigProvider;
    private readonly IOptionsMonitor<ErpReconciliationOptions> _reconciliationOptions;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly LocalSyncStore _localStore;
    private readonly ErpCredentialProvider _credentialProvider;

    public ErpReconciliationClient(
        ILogger<ErpReconciliationClient> logger,
        EffectiveSyncAgentConfigurationProvider effectiveConfigProvider,
        IOptionsMonitor<ErpReconciliationOptions> reconciliationOptions,
        IHttpClientFactory httpClientFactory,
        LocalSyncStore localStore,
        ErpCredentialProvider credentialProvider)
    {
        _logger = logger;
        _effectiveConfigProvider = effectiveConfigProvider;
        _reconciliationOptions = reconciliationOptions;
        _httpClientFactory = httpClientFactory;
        _localStore = localStore;
        _credentialProvider = credentialProvider;
    }

    public async Task<RemoteReconciliationSummary> SendAsync(
        Guid reconciliationId,
        DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc,
        CancellationToken cancellationToken)
    {
        var options = _reconciliationOptions.CurrentValue;
        if (!options.Enabled)
        {
            return RemoteReconciliationSummary.Disabled;
        }

        var syncOptions = _effectiveConfigProvider.GetCurrent();
        var erpApiBaseUri = new Uri(syncOptions.ErpApiBaseUrl, UriKind.Absolute);
        ErpCredentialProvider.EnsureHttpsOutsideLocalDevelopment(erpApiBaseUri);
        _credentialProvider.ValidateProvisionedForRemoteEndpoint(erpApiBaseUri);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

        var payload = await _localStore.BuildReconciliationSummaryPayloadAsync(
            reconciliationId,
            syncOptions.InstanceId,
            windowStartUtc,
            windowEndUtc,
            cancellationToken);

        var request = ReconciliationSummaryRequest.FromPayload(payload);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/sync/reconciliation:summary")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(request, JsonOptions),
                Encoding.UTF8,
                "application/json")
        };

        var authorization = _credentialProvider.CreateAuthorizationHeader();
        if (authorization is not null)
        {
            httpRequest.Headers.Authorization = authorization;
        }

        var client = _httpClientFactory.CreateClient(ErpReconciliationHttpClient.Name);
        client.BaseAddress = erpApiBaseUri;
        client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);

        try
        {
            using var response = await client.SendAsync(httpRequest, timeout.Token);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = $"ERP reconciliation returned HTTP {(int)response.StatusCode}.";
                await _localStore.UpdateReconciliationRemoteResultAsync(
                    reconciliationId,
                    false,
                    BuildErrorResponse(error, responseBody),
                    cancellationToken);
                return new RemoteReconciliationSummary(true, false, false, error);
            }

            var reconciliationResponse = JsonSerializer.Deserialize<ReconciliationSummaryResponse>(responseBody, JsonOptions);
            if (reconciliationResponse is null || reconciliationResponse.Status != "ok")
            {
                const string error = "ERP reconciliation returned an invalid response.";
                await _localStore.UpdateReconciliationRemoteResultAsync(
                    reconciliationId,
                    false,
                    BuildErrorResponse(error, responseBody),
                    cancellationToken);
                return new RemoteReconciliationSummary(true, false, false, error);
            }

            await _localStore.UpdateReconciliationRemoteResultAsync(
                reconciliationId,
                reconciliationResponse.Matched,
                JsonNode.Parse(responseBody) ?? new JsonObject(),
                cancellationToken);

            _logger.LogInformation(
                "ERP reconciliation completed. ReconciliationId={ReconciliationId}; Matched={Matched}; Mismatches={MismatchCount}",
                reconciliationId,
                reconciliationResponse.Matched,
                reconciliationResponse.Mismatches?.Count ?? 0);

            return new RemoteReconciliationSummary(true, true, reconciliationResponse.Matched, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await _localStore.UpdateReconciliationRemoteResultAsync(
                reconciliationId,
                false,
                BuildErrorResponse(ex.GetType().Name, null),
                cancellationToken);

            _logger.LogWarning(ex, "ERP reconciliation failed.");
            return new RemoteReconciliationSummary(true, false, false, ex.GetType().Name);
        }
    }

    private static JsonObject BuildErrorResponse(string error, string? responseBody)
    {
        return new JsonObject
        {
            ["error"] = error,
            ["response_body"] = responseBody
        };
    }
}

public static class ErpReconciliationHttpClient
{
    public const string Name = "ErpReconciliation";
}

public sealed record RemoteReconciliationSummary(
    bool Enabled,
    bool Succeeded,
    bool Matched,
    string? LastError)
{
    public static RemoteReconciliationSummary Disabled { get; } = new(false, false, false, null);
}

public sealed record ReconciliationSummaryRequest(
    [property: JsonPropertyName("reconciliation_id")] Guid ReconciliationId,
    [property: JsonPropertyName("source_instance_id")] string SourceInstanceId,
    [property: JsonPropertyName("window_start_utc")] DateTimeOffset WindowStartUtc,
    [property: JsonPropertyName("window_end_utc")] DateTimeOffset WindowEndUtc,
    [property: JsonPropertyName("generated_at_utc")] DateTimeOffset GeneratedAtUtc,
    [property: JsonPropertyName("entities")] IReadOnlyList<ReconciliationEntitySummaryRequest> Entities)
{
    public static ReconciliationSummaryRequest FromPayload(ReconciliationSummaryPayload payload)
    {
        return new ReconciliationSummaryRequest(
            payload.ReconciliationId,
            payload.SourceInstanceId,
            payload.WindowStartUtc,
            payload.WindowEndUtc,
            payload.GeneratedAtUtc,
            payload.Entities.Select(ReconciliationEntitySummaryRequest.FromPayload).ToArray());
    }
}

public sealed record ReconciliationEntitySummaryRequest(
    [property: JsonPropertyName("entity_type")] string EntityType,
    [property: JsonPropertyName("captured")] long Captured,
    [property: JsonPropertyName("accepted")] long Accepted,
    [property: JsonPropertyName("rejected")] long Rejected,
    [property: JsonPropertyName("dead_letter")] long DeadLetter,
    [property: JsonPropertyName("aggregate_hash")] string AggregateHash)
{
    public static ReconciliationEntitySummaryRequest FromPayload(ReconciliationEntitySummary payload)
    {
        return new ReconciliationEntitySummaryRequest(
            payload.EntityType,
            payload.Captured,
            payload.Accepted,
            payload.Rejected,
            payload.DeadLetter,
            payload.AggregateHash);
    }
}

public sealed record ReconciliationSummaryResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("matched")] bool Matched,
    [property: JsonPropertyName("mismatches")] IReadOnlyList<ReconciliationMismatch>? Mismatches);

public sealed record ReconciliationMismatch(
    [property: JsonPropertyName("entity_type")] string EntityType,
    [property: JsonPropertyName("field")] string Field,
    [property: JsonPropertyName("local")] string Local,
    [property: JsonPropertyName("remote")] string Remote);
