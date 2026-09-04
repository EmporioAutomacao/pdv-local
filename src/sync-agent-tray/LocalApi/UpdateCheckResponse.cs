using System.Text.Json.Serialization;

namespace SyncAgent.Tray.LocalApi;

/// <summary>
/// Resposta de GET /update-check: versao instalada x versao mais recente
/// publicada no ERP, sem baixar nem aplicar nada. A janela "Atualizar App"
/// usa isso para mostrar as duas versoes e pedir confirmacao.
/// </summary>
public sealed record UpdateCheckResponse(
    [property: JsonPropertyName("current_version")] string CurrentVersion,
    [property: JsonPropertyName("latest_version")] string? LatestVersion,
    [property: JsonPropertyName("update_available")] bool UpdateAvailable,
    [property: JsonPropertyName("release_notes")] string? ReleaseNotes,
    [property: JsonPropertyName("error")] string? Error);
