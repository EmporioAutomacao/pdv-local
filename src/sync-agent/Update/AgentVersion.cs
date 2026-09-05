namespace SyncAgent.Update;

/// <summary>
/// Comparacao semver leve para versoes do SyncAgent/PDV. Todo o projeto usa
/// versoes `X.Y.Z` puras (ver SyncAgent.csproj, PdvLocal.App.csproj,
/// appsettings SyncAgent:AgentVersion, arquivo VERSION pos-update) — sem
/// pre-release/build metadata e sem lib externa, por isso <see cref="Version"/>
/// (BCL) ja cobre o caso real.
///
/// Espelha sync_api.versioning (erp) — a mesma logica de "downgrade nunca
/// aparece" e aplicada dos dois lados (defesa em profundidade: o ERP filtra
/// primeiro, o SyncAgent nunca confia cegamente no que recebeu pela rede).
/// </summary>
public static class AgentVersion
{
    public static bool TryParse(string? raw, out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        // Version.Parse exige ao menos "major.minor" — completa com ".0" se
        // vier so "1" (nao deveria acontecer no projeto, mas nao custa).
        var normalized = raw.Contains('.') ? raw.Trim() : $"{raw.Trim()}.0";
        return Version.TryParse(normalized, out version);
    }

    /// <summary>
    /// True se <paramref name="candidate"/> for igual ou anterior a
    /// <paramref name="current"/>. Se qualquer um dos dois nao for parseavel
    /// (ex.: "0.1.0-dev", instalacao recem-criada em dev, ou instalacao sem
    /// versao conhecida), retorna false — sem referencia confiavel para
    /// comparar, nao ha o que bloquear.
    /// </summary>
    public static bool IsDowngradeOrSame(string? current, string? candidate)
    {
        if (!TryParse(current, out var currentVersion) || !TryParse(candidate, out var candidateVersion))
        {
            return false;
        }

        return candidateVersion! <= currentVersion!;
    }
}
