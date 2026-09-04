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
    private readonly ErpLatestPackageClient _latestPackageClient;
    private readonly UpdateProgressState _progress;

    public SelfUpdater(
        ILogger<SelfUpdater> logger,
        IOptionsMonitor<SyncAgentOptions> options,
        IHostApplicationLifetime lifetime,
        ErpLatestPackageClient latestPackageClient,
        UpdateProgressState progress)
    {
        _logger = logger;
        _options = options;
        _lifetime = lifetime;
        _latestPackageClient = latestPackageClient;
        _progress = progress;
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

        return await DownloadVerifyAndApplyAsync(
            update.Version,
            update.DownloadUrl,
            update.Sha256,
            cancellationToken);
    }

    /// <summary>
    /// Consulta sem efeito colateral (botao "Atualizar App" da bandeja, via
    /// GET /update-check): retorna a versao instalada e a versao mais recente
    /// publicada no ERP, sem baixar nem aplicar nada. A bandeja usa isso para
    /// mostrar "versao atual x versao disponivel" e pedir confirmacao antes de
    /// chamar POST /update-now.
    /// </summary>
    public async Task<UpdatePreview> PreviewLatestAsync(CancellationToken cancellationToken)
    {
        var currentVersion = _options.CurrentValue.AgentVersion;

        if (!_options.CurrentValue.SelfUpdateEnabled)
        {
            return new UpdatePreview(currentVersion, null, false, null, "self_update_disabled");
        }

        var result = await _latestPackageClient.FetchAsync(cancellationToken);
        if (!result.Succeeded || result.Package is null)
        {
            return new UpdatePreview(currentVersion, null, false, null, result.Error ?? "unknown");
        }

        var latestVersion = result.Package.Version;
        var updateAvailable = !string.Equals(currentVersion, latestVersion, StringComparison.OrdinalIgnoreCase);
        return new UpdatePreview(currentVersion, latestVersion, updateAvailable, result.Package.ReleaseNotes, null);
    }

    /// <summary>
    /// Disparado sob demanda (botao "Atualizar App" da bandeja, via
    /// POST /update-now): busca a versao mais recente publicada no ERP
    /// (independente de qualquer pending_update ja agendado via heartbeat
    /// para esta instalacao especificamente) e aplica se for diferente da
    /// versao atual. Reporta progresso em UpdateProgressState para a
    /// bandeja acompanhar via GET /update-status.
    /// </summary>
    public async Task CheckAndApplyLatestAsync(CancellationToken cancellationToken)
    {
        if (_progress.IsInProgress)
        {
            _logger.LogInformation("Atualizacao sob demanda ignorada: ja existe uma em andamento.");
            return;
        }

        _progress.Set(UpdateStatus.CheckingForUpdate, 0, "Consultando o ERP pela versao mais recente...");

        var result = await _latestPackageClient.FetchAsync(cancellationToken);
        if (!result.Succeeded || result.Package is null)
        {
            var message = result.Error == "no_package_published"
                ? "Nenhuma versao publicada foi encontrada no ERP."
                : $"Nao foi possivel consultar o ERP ({result.Error}).";
            _logger.LogWarning("CheckAndApplyLatestAsync: falha ao buscar versao mais recente ({Error}).", result.Error);
            _progress.Set(UpdateStatus.Failed, 0, message, error: result.Error);
            return;
        }

        var package = result.Package;
        var currentVersion = _options.CurrentValue.AgentVersion;
        if (string.Equals(currentVersion, package.Version, StringComparison.OrdinalIgnoreCase))
        {
            _progress.Set(UpdateStatus.UpToDate, 100, $"Ja esta na versao mais recente ({currentVersion}).", package.Version);
            return;
        }

        await DownloadVerifyAndApplyAsync(package.Version, package.DownloadUrl, package.Sha256, cancellationToken);
    }

    private async Task<bool> DownloadVerifyAndApplyAsync(
        string targetVersion,
        string downloadUrl,
        string sha256,
        CancellationToken cancellationToken)
    {
        var currentVersion = _options.CurrentValue.AgentVersion;
        _logger.LogInformation(
            "Self-update triggered: {CurrentVersion} -> {TargetVersion}. Downloading from {Url}.",
            currentVersion, targetVersion, downloadUrl);

        var downloadDir = Path.Combine(Path.GetTempPath(), "pdv-update", targetVersion);
        Directory.CreateDirectory(downloadDir);
        var zipPath = Path.Combine(downloadDir, "payload.zip");

        try
        {
            _progress.Set(UpdateStatus.Downloading, 0, $"Baixando versao {targetVersion}...", targetVersion);
            await DownloadAsync(downloadUrl, zipPath, targetVersion, cancellationToken);

            _progress.Set(UpdateStatus.Verifying, 100, "Verificando integridade do pacote...", targetVersion);
            VerifySha256(zipPath, sha256);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Self-update download/verification failed. Update aborted.");
            _progress.Set(UpdateStatus.Failed, 0, "Falha ao baixar ou verificar o pacote de atualizacao.", targetVersion, ex.GetType().Name);
            return false;
        }

        var scriptPath = Path.Combine(AppContext.BaseDirectory, "self-update.ps1");
        if (!File.Exists(scriptPath))
        {
            _logger.LogError("self-update.ps1 not found at {Path}. Update aborted.", scriptPath);
            _progress.Set(UpdateStatus.Failed, 0, "Script de atualizacao nao encontrado.", targetVersion, "script_not_found");
            return false;
        }

        // SyncAgent.exe roda em <InstallRoot>\Sync\Agent\, dois niveis abaixo da raiz.
        var installRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));

        _progress.Set(
            UpdateStatus.Applying,
            0,
            "Aplicando atualizacao - o Sync e o PDV serao reiniciados em instantes...",
            targetVersion);
        LaunchUpdateScript(scriptPath, zipPath, targetVersion, installRoot);

        _logger.LogInformation("Update script launched. Stopping agent to allow binary replacement.");
        _lifetime.StopApplication();
        return true;
    }

    private async Task DownloadAsync(string url, string destination, string targetVersion, CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(10);
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var file = File.Create(destination);

        var buffer = new byte[81920];
        long bytesRead = 0;
        int read;
        var lastReportedPercent = -1;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            bytesRead += read;

            if (totalBytes is > 0)
            {
                var percent = (int)Math.Min(100, bytesRead * 100 / totalBytes.Value);
                if (percent != lastReportedPercent)
                {
                    lastReportedPercent = percent;
                    _progress.Set(UpdateStatus.Downloading, percent, $"Baixando versao {targetVersion}... ({percent}%)", targetVersion);
                }
            }
        }
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
                   $" -ServiceName \"AraraSuiteSync\"";

        var psi = new ProcessStartInfo("powershell.exe", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        Process.Start(psi);
    }
}

/// <summary>
/// Resultado de <see cref="SelfUpdater.PreviewLatestAsync"/>: versao instalada,
/// versao publicada no ERP e se ha diferenca. <see cref="Error"/> preenchido
/// quando nao foi possivel consultar o ERP (ou o self-update esta desabilitado).
/// </summary>
public sealed record UpdatePreview(
    string CurrentVersion,
    string? LatestVersion,
    bool UpdateAvailable,
    string? ReleaseNotes,
    string? Error);
