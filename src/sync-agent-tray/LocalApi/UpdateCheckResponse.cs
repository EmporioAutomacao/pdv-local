using System.Text.Json.Serialization;

namespace SyncAgent.Tray.LocalApi;

/// <summary>
/// Resposta de GET /update-check: versao instalada e a lista de versoes
/// disponiveis para esta instalacao (curadoria do ERP), sem baixar nem
/// aplicar nada. A janela "Atualizar App" usa isso pra mostrar as opcoes e
/// deixar o usuario escolher qual aplicar via POST /update-now.
/// </summary>
public sealed record UpdateCheckResponse(
    [property: JsonPropertyName("current_version")] string CurrentVersion,
    [property: JsonPropertyName("packages")] IReadOnlyList<UpdateCheckPackage> Packages,
    [property: JsonPropertyName("error")] string? Error);

public sealed record UpdateCheckPackage(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("download_url")] string DownloadUrl,
    [property: JsonPropertyName("release_notes")] string? ReleaseNotes,
    [property: JsonPropertyName("erp_minimo")] string? ErpMinimo,
    [property: JsonPropertyName("blocked")] bool Blocked,
    [property: JsonPropertyName("blocked_reason")] string? BlockedReason);
