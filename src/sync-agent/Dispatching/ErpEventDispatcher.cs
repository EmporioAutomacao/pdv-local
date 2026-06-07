using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SyncAgent.Configuration;
using SyncAgent.Persistence;
using SyncAgent.Provisioning;
using SyncAgent.Security;

namespace SyncAgent.Dispatching;

public sealed class ErpEventDispatcher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ILogger<ErpEventDispatcher> _logger;
    private readonly EffectiveSyncAgentConfigurationProvider _effectiveConfigProvider;
    private readonly IOptionsMonitor<ErpDispatcherOptions> _dispatcherOptions;
    private readonly LocalSyncStore _localStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ErpCredentialProvider _credentialProvider;

    public ErpEventDispatcher(
        ILogger<ErpEventDispatcher> logger,
        EffectiveSyncAgentConfigurationProvider effectiveConfigProvider,
        IOptionsMonitor<ErpDispatcherOptions> dispatcherOptions,
        LocalSyncStore localStore,
        IHttpClientFactory httpClientFactory,
        ErpCredentialProvider credentialProvider)
    {
        _logger = logger;
        _effectiveConfigProvider = effectiveConfigProvider;
        _dispatcherOptions = dispatcherOptions;
        _localStore = localStore;
        _httpClientFactory = httpClientFactory;
        _credentialProvider = credentialProvider;
    }

    public async Task<DispatchSummary> DispatchAsync(CancellationToken cancellationToken)
    {
        var dispatcherOptions = _dispatcherOptions.CurrentValue;
        if (!dispatcherOptions.Enabled)
        {
            return DispatchSummary.Disabled;
        }

        var syncOptions = _effectiveConfigProvider.GetCurrent();
        var erpApiBaseUri = new Uri(syncOptions.ErpApiBaseUrl, UriKind.Absolute);
        ErpCredentialProvider.EnsureHttpsOutsideLocalDevelopment(erpApiBaseUri);
        _credentialProvider.ValidateProvisionedForRemoteEndpoint(erpApiBaseUri);

        var events = await _localStore.ClaimPendingOutboxEventsAsync(
            dispatcherOptions.BatchSize,
            TimeSpan.FromSeconds(dispatcherOptions.InFlightRecoverySeconds),
            cancellationToken);

        if (events.Count == 0)
        {
            return new DispatchSummary(true, 0, 0, 0, 0, null);
        }

        var batchId = Guid.NewGuid();
        await _localStore.RecordDispatchStartedAsync(batchId, events, cancellationToken);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(dispatcherOptions.TimeoutSeconds));

            var request = new EventsBatchRequest(
                batchId,
                DateTimeOffset.UtcNow,
                events.Select(OutboxEventEnvelope.FromRecord).ToArray());

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/sync/events:batch")
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

            var client = _httpClientFactory.CreateClient(ErpEventDispatcherHttpClient.Name);
            client.BaseAddress = erpApiBaseUri;
            client.Timeout = TimeSpan.FromSeconds(dispatcherOptions.TimeoutSeconds);

            using var response = await client.SendAsync(httpRequest, timeout.Token);
            if (response.StatusCode != System.Net.HttpStatusCode.Accepted)
            {
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                var classification = ClassifyHttpFailure(response.StatusCode);
                var errorMessage = $"ERP returned HTTP {(int)response.StatusCode}.";
                await _localStore.ReleaseDispatchedEventsAsync(
                    batchId,
                    events,
                    classification,
                    errorMessage,
                    (int)response.StatusCode,
                    responseBody,
                    dispatcherOptions.MaxAttempts,
                    TimeSpan.FromSeconds(dispatcherOptions.InitialBackoffSeconds),
                    TimeSpan.FromSeconds(dispatcherOptions.MaxBackoffSeconds),
                    cancellationToken);

                return new DispatchSummary(true, events.Count, 0, 0, events.Count, errorMessage);
            }

            var batchResponse = await response.Content.ReadFromJsonAsync<EventsBatchResponse>(
                JsonOptions,
                cancellationToken);

            if (batchResponse is null || batchResponse.BatchId != batchId)
            {
                const string errorMessage = "ERP returned an invalid events batch response.";
                await _localStore.ReleaseDispatchedEventsAsync(
                    batchId,
                    events,
                    "transient_error",
                    errorMessage,
                    202,
                    null,
                    dispatcherOptions.MaxAttempts,
                    TimeSpan.FromSeconds(dispatcherOptions.InitialBackoffSeconds),
                    TimeSpan.FromSeconds(dispatcherOptions.MaxBackoffSeconds),
                    cancellationToken);

                return new DispatchSummary(true, events.Count, 0, 0, events.Count, errorMessage);
            }

            var rejected = batchResponse.RejectedEvents ?? [];
            var rejectedByEventId = rejected.ToDictionary(item => item.EventId, item => item.Reason);
            var acceptedEventIds = events
                .Select(item => item.EventId)
                .Where(eventId => !rejectedByEventId.ContainsKey(eventId))
                .ToArray();

            if (acceptedEventIds.Length > 0)
            {
                await _localStore.MarkDispatchAcceptedAsync(batchId, acceptedEventIds, cancellationToken);
            }

            if (rejectedByEventId.Count > 0)
            {
                await _localStore.MarkDispatchRejectedAsync(batchId, rejectedByEventId, cancellationToken);
            }

            _logger.LogInformation(
                "ERP dispatch completed. BatchId={BatchId}; Sent={Sent}; Accepted={Accepted}; Rejected={Rejected}",
                batchId,
                events.Count,
                acceptedEventIds.Length,
                rejectedByEventId.Count);

            return new DispatchSummary(true, events.Count, acceptedEventIds.Length, rejectedByEventId.Count, 0, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await _localStore.ReleaseDispatchedEventsAsync(
                batchId,
                events,
                "transient_error",
                ex.GetType().Name,
                null,
                null,
                dispatcherOptions.MaxAttempts,
                TimeSpan.FromSeconds(dispatcherOptions.InitialBackoffSeconds),
                TimeSpan.FromSeconds(dispatcherOptions.MaxBackoffSeconds),
                cancellationToken);

            _logger.LogWarning(
                ex,
                "ERP dispatch failed. BatchId={BatchId}; Events={Events}",
                batchId,
                events.Count);

            return new DispatchSummary(true, events.Count, 0, 0, events.Count, ex.GetType().Name);
        }
    }

    private static string ClassifyHttpFailure(System.Net.HttpStatusCode statusCode)
    {
        var numericStatusCode = (int)statusCode;
        return numericStatusCode is 400 or 404 or 409 or 422
            ? "permanent_error"
            : "transient_error";
    }
}

public static class ErpEventDispatcherHttpClient
{
    public const string Name = "ErpEventDispatcher";
}

public sealed record DispatchSummary(
    bool Enabled,
    int Sent,
    int Accepted,
    int Rejected,
    int Failed,
    string? LastError)
{
    public static DispatchSummary Disabled { get; } = new(false, 0, 0, 0, 0, null);
}

public sealed record EventsBatchRequest(
    [property: JsonPropertyName("batch_id")] Guid BatchId,
    [property: JsonPropertyName("sent_at_utc")] DateTimeOffset SentAtUtc,
    [property: JsonPropertyName("events")] IReadOnlyCollection<OutboxEventEnvelope> Events);

public sealed record OutboxEventEnvelope(
    [property: JsonPropertyName("event_id")] Guid EventId,
    [property: JsonPropertyName("source_instance_id")] string SourceInstanceId,
    [property: JsonPropertyName("source_system")] string SourceSystem,
    [property: JsonPropertyName("entity_type")] string EntityType,
    [property: JsonPropertyName("entity_key")] string EntityKey,
    [property: JsonPropertyName("event_type")] string EventType,
    [property: JsonPropertyName("occurred_at_utc")] DateTimeOffset OccurredAtUtc,
    [property: JsonPropertyName("captured_at_utc")] DateTimeOffset CapturedAtUtc,
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("payload")] JsonElement Payload,
    [property: JsonPropertyName("payload_hash")] string PayloadHash,
    [property: JsonPropertyName("trace_id")] string? TraceId)
{
    public static OutboxEventEnvelope FromRecord(OutboxEventRecord record)
    {
        using var document = JsonDocument.Parse(record.PayloadJson);
        return new OutboxEventEnvelope(
            record.EventId,
            record.SourceInstanceId,
            record.SourceSystem,
            record.EntityType,
            record.EntityKey,
            record.EventType,
            record.OccurredAtUtc,
            record.CapturedAtUtc,
            record.SchemaVersion,
            document.RootElement.Clone(),
            record.PayloadHash,
            record.TraceId);
    }
}

public sealed record EventsBatchResponse(
    [property: JsonPropertyName("batch_id")] Guid BatchId,
    [property: JsonPropertyName("accepted")] int Accepted,
    [property: JsonPropertyName("rejected")] int Rejected,
    [property: JsonPropertyName("rejected_events")] IReadOnlyCollection<RejectedEventResponse>? RejectedEvents);

public sealed record RejectedEventResponse(
    [property: JsonPropertyName("event_id")] Guid EventId,
    [property: JsonPropertyName("reason")] string Reason);
