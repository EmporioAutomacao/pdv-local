using System.Text.Json.Serialization;

namespace SyncAgent.Tray.LocalApi;

public sealed record PendingUpdateConfirmationInfo(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("release_notes")] string? ReleaseNotes,
    [property: JsonPropertyName("deadline_at_utc")] DateTimeOffset? DeadlineAtUtc,
    [property: JsonPropertyName("scheduled_at_utc")] DateTimeOffset? ScheduledAtUtc,
    [property: JsonPropertyName("awaiting_choice")] bool AwaitingChoice);
