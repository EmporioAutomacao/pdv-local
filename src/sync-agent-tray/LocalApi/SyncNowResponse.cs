using System.Text.Json.Serialization;

namespace SyncAgent.Tray.LocalApi;

public sealed record SyncNowResponse(
    [property: JsonPropertyName("accepted")] bool Accepted,
    [property: JsonPropertyName("message")] string Message);
