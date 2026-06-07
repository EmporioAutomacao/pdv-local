using System.Text.Json.Nodes;

namespace SyncAgent.Contracts;

public sealed record OutboxEventDraft(
    Guid EventId,
    string SourceInstanceId,
    string SourceSystem,
    string EntityType,
    string EntityKey,
    string EventType,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset CapturedAtUtc,
    string SchemaVersion,
    JsonObject Payload,
    string? TraceId);
