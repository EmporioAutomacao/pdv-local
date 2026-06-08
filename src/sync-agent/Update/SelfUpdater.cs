using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using SyncAgent.Configuration;
using SyncAgent.Heartbeat;

namespace SyncAgent.Update;

public sealed class SelfUpdater
{
    private readonly ILogger<SelfUpdater> _logger;
    private readonly IOptionsMonitor<SyncAgentOptions> _options;
    private readonly IHostApplicationLifetime _lifetime;

    public SelfUpdater(
        ILogger<SelfUpdater> logger,
        IOptionsMonitor<SyncAgentOptions> options,
        IHostApplicationLifetime lifetime)
    {
        _logger = logger;
        _options = options;
        _lifetime = lifetime;
    }

    // Returns true if an update was triggered (agent will stop shortly after).
    public async Task<bool> ApplyIfNeededAsync(PendingUpdateCommand update, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;

        if (!options.SelfUpdateEnabled)
        {
            _logger.LogInformation("Self-update skipped: SelfUpdateEnabled=false.");
            return false;
        }

        var currentVersion = options.AgentVersion;
        if (string.Equals(currentVersion, update.Version, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "Self-update skipped: already at version {Version}.", update.Version);
            return false;
        }

        _logger.LogInformation(
            "Self-update triggered: {CurrentVersion} → {TargetVersion}. Downloading from {Url}.",
            currentVersion, update.Version, update.DownloadUrl);

        var downloadDir = Path.Combine(Path.GetTempPath(), "pdv-update", update.Version);
        Directory.CreateDirectory(downloadDir);
        var zipPath = Path.Combine(downloadDir, "payload.zip");

        try
        {
            await DownloadAsync(update.DownloadUrl, zipPath, cancellationToken);
            VerifySha256(zipPath, update.Sha256);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Self-update download/verification failed. Update aborted.");
            return false;
        }

        var scriptPath = Path.Combine(AppContext.BaseDirectory, "self-update.ps1");
        if (!File.Exists(scriptPath))
        {
            _logger.LogError("self-update.ps1 not found at {Path}. Update aborted.", scriptPath);
            return false;
        }

        var installRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        LaunchUpdateScript(scriptPath, zipPath, update.Version, installRoot);

        _logger.LogInformation("Update script launched. Stopping agent to allow binary replacement.");
        _lifetime.StopApplication();
        return true;
    }

    private static async Task DownloadAsync(string url, string destination, CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(10);
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var file = File.Create(destination);
        await stream.CopyToAsync(file, cancellationToken);
    }

    private static void VerifySha256(string filePath, string expectedHex)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha.ComputeHash(stream);
        var actual = Convert.ToHexString(hash);
        if (!string.Equals(actual, expectedHex, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"SHA256 mismatch: expected {expectedHex}, got {actual}.");
    }

    private static void LaunchUpdateScript(string scriptPath, string zipPath, string version, string installRoot)
    {
        var args = $"-NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"" +
                   $" -ZipPath \"{zipPath}\"" +
                   $" -Version \"{version}\"" +
                   $" -InstallRoot \"{installRoot}\"" +
                   $" -ServiceName \"PDV Local Sync Agent\"";

        var psi = new ProcessStartInfo("powershell.exe", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        Process.Start(psi);
    }
}
