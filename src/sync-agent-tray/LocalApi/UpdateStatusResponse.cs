using System.Text.Json.Serialization;

namespace SyncAgent.Tray.LocalApi;

public sealed record UpdateStatusResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("percent")] int Percent,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("target_version")] string? TargetVersion,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("updated_at_utc")] DateTimeOffset UpdatedAtUtc);
