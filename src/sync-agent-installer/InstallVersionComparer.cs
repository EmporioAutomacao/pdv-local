using System.Diagnostics;

namespace SyncAgent.Installer;

/// <summary>
/// Versao de uma instalacao (ou do pacote embutido no .exe): o rotulo do
/// arquivo <c>Sync\Agent\VERSION</c> (tag da release) e o ProductVersion do
/// <c>SyncAgent.exe</c> (<c>&lt;Version&gt;+&lt;commit&gt;</c>).
/// </summary>
internal sealed record InstallVersionSnapshot(string? VersionFile, string? AgentProductVersion)
{
    /// <summary>Commit do build ("+a3b4994..." do ProductVersion), ou null se nao houver.</summary>
    public string? BuildCommit
    {
        get
        {
            var plus = AgentProductVersion?.IndexOf('+') ?? -1;
            if (plus < 0)
            {
                return null;
            }

            var commit = AgentProductVersion![(plus + 1)..].Trim();
            return commit.Length == 0 ? null : commit;
        }
    }

    public string Describe()
    {
        var label = VersionFile ?? "desconhecida";
        var commit = BuildCommit;
        return commit is null ? label : $"{label} (build {commit[..Math.Min(7, commit.Length)]})";
    }

    public static InstallVersionSnapshot Read(string agentDirectory) => new(
        ReadVersionFile(Path.Combine(agentDirectory, "VERSION")),
        ReadProductVersion(Path.Combine(agentDirectory, "SyncAgent.exe")));

    private static string? ReadVersionFile(string path)
    {
        try
        {
            var value = File.Exists(path) ? File.ReadAllText(path).Trim() : null;
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadProductVersion(string path)
    {
        try
        {
            var value = File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).ProductVersion?.Trim() : null;
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }
}

internal enum InstallComparison
{
    /// <summary>Pacote traz outro build: atualizar.</summary>
    Different,

    /// <summary>Mesmo build ja instalado: so faz sentido reinstalar (reparo).</summary>
    SameBuild
}

internal static class InstallVersionComparer
{
    /// <summary>
    /// O arquivo VERSION sozinho nao identifica o que esta instalado: ele ja
    /// ficou dizendo uma versao com os binarios de outra (backup "1.6.40" com
    /// SyncAgent.exe de 1.6.46), e o numero do ProductVersion tambem atrasa
    /// quando o csproj nao e bumpado junto da tag. O commit embutido no
    /// SyncAgent.exe e a identidade real do build; o VERSION so decide quando
    /// algum dos dois lados nao tem commit (build antigo/dev).
    /// </summary>
    public static InstallComparison Compare(InstallVersionSnapshot installed, InstallVersionSnapshot package)
    {
        var installedCommit = installed.BuildCommit;
        var packageCommit = package.BuildCommit;
        if (installedCommit is not null && packageCommit is not null)
        {
            return string.Equals(installedCommit, packageCommit, StringComparison.OrdinalIgnoreCase)
                ? InstallComparison.SameBuild
                : InstallComparison.Different;
        }

        if (installed.VersionFile is null || package.VersionFile is null)
        {
            return InstallComparison.Different;
        }

        return string.Equals(installed.VersionFile, package.VersionFile, StringComparison.OrdinalIgnoreCase)
            ? InstallComparison.SameBuild
            : InstallComparison.Different;
    }
}
