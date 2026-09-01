using System.IO.Compression;
using System.Reflection;

namespace SyncAgent.Installer;

internal static class EmbeddedPayload
{
    public static string? ExtractIfPresent()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith("payload.zip", StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
        {
            return null;
        }

        using var resourceStream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Recurso embutido nao pode ser lido: {resourceName}");

        var extractRoot = Path.Combine(Path.GetTempPath(), "PdvLocalInstaller", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extractRoot);

        using var archive = new ZipArchive(resourceStream, ZipArchiveMode.Read);
        archive.ExtractToDirectory(extractRoot);

        return extractRoot;
    }
}
