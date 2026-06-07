using System.Text.Json.Serialization;

namespace SyncAgent.Tray.LocalApi;

public sealed record AgentStatusResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("timestamp_utc")] DateTimeOffset TimestampUtc,
    [property: JsonPropertyName("instance_id")] string InstanceId,
    [property: JsonPropertyName("erp_tenant_id")] string ErpTenantId,
    [property: JsonPropertyName("erp_api_base_url")] string ErpApiBaseUrl,
    [property: JsonPropertyName("agent_version")] string AgentVersion,
    [property: JsonPropertyName("database")] string Database,
    [property: JsonPropertyName("pgvector_version")] string PgVectorVersion,
    [property: JsonPropertyName("pending_outbox_events")] long PendingOutboxEvents,
    [property: JsonPropertyName("dead_letter_events")] long DeadLetterEvents,
    [property: JsonPropertyName("oldest_pending_age_seconds")] int OldestPendingAgeSeconds,
    [property: JsonPropertyName("last_heartbeat_at_utc")] DateTimeOffset? LastHeartbeatAtUtc,
    [property: JsonPropertyName("last_heartbeat_succeeded")] bool? LastHeartbeatSucceeded,
    [property: JsonPropertyName("last_heartbeat_connectivity")] string? LastHeartbeatConnectivity,
    [property: JsonPropertyName("last_reconciliation_id")] Guid? LastReconciliationId,
    [property: JsonPropertyName("last_reconciliation_status")] string? LastReconciliationStatus,
    [property: JsonPropertyName("last_reconciliation_completed_at_utc")] DateTimeOffset? LastReconciliationCompletedAtUtc,
    [property: JsonPropertyName("runtime_status")] string RuntimeStatus,
    [property: JsonPropertyName("last_cycle_completed_at_utc")] DateTimeOffset? LastCycleCompletedAtUtc,
    [property: JsonPropertyName("last_cycle_trigger")] string? LastCycleTrigger,
    [property: JsonPropertyName("last_error")] string? LastError);
