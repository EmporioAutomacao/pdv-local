using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using System.Linq;
using SyncAgent.Configuration;
using SyncAgent.Heartbeat;

namespace SyncAgent.Update;

public sealed class SelfUpdater
{
    private static readonly Regex Sha256HexPattern = new("^[0-9A-Fa-f]{64}$", RegexOptions.Compiled);

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
    /// GET /update-check): retorna a versao instalada e a lista de versoes
    /// disponiveis para esta instalacao, sem baixar nem aplicar nada. A
    /// bandeja usa isso pra mostrar as opcoes e deixar o dono da loja
    /// escolher qual aplicar (nunca a instalacao decide sozinha "a mais
    /// recente" — quem decide e o usuario, dentro do conjunto permitido pelo
    /// ERP) antes de chamar POST /update-now com a versao escolhida.
    ///
    /// Defesa em profundidade (nunca confia cegamente no que veio pela
    /// rede, mesmo que sync_api.get_available_packages, do lado erp, ja
    /// devesse ter filtrado): refiltra downgrade aqui de novo, e descarta
    /// (nao so ignora) qualquer pacote com Sha256 fora do formato esperado.
    /// </summary>
    public async Task<UpdatePreview> PreviewAvailableAsync(CancellationToken cancellationToken)
    {
        var currentVersion = _options.CurrentValue.AgentVersion;

        if (!_options.CurrentValue.SelfUpdateEnabled)
        {
            return new UpdatePreview(currentVersion, Array.Empty<AvailablePackageItem>(), "self_update_disabled");
        }

        var result = await _latestPackageClient.FetchAvailableAsync(cancellationToken);
        if (!result.Succeeded || result.Packages is null)
        {
            return new UpdatePreview(currentVersion, Array.Empty<AvailablePackageItem>(), result.Error ?? "unknown");
        }

        var packages = new List<AvailablePackageItem>();
        foreach (var package in result.Packages)
        {
            if (AgentVersion.IsDowngradeOrSame(currentVersion, package.Version))
            {
                continue;
            }

            if (string.IsNullOrEmpty(package.Sha256) || !Sha256HexPattern.IsMatch(package.Sha256))
            {
                _logger.LogWarning(
                    "PreviewAvailableAsync: pacote v{Version} tem Sha256 invalido no ERP ({Sha256Length} chars) — ocultado da lista.",
                    package.Version, package.Sha256?.Length ?? 0);
                continue;
            }

            packages.Add(package);
        }

        return new UpdatePreview(currentVersion, packages, null);
    }

    /// <summary>
    /// Disparado sob demanda (botao "Atualizar App" da bandeja, via
    /// POST /update-now com a versao escolhida pelo usuario): consulta o ERP
    /// de novo com dado fresco (nunca confia na lista que a UI ja tinha
    /// mostrado antes) e recusa aplicar se a versao pedida nao estiver mais
    /// disponivel, for downgrade/igual a atual, ou estiver marcada
    /// `blocked` (incompatibilidade de ERP) — ultima linha de defesa,
    /// independente de qualquer curadoria ou UI ja terem filtrado. Reporta
    /// progresso em UpdateProgressState para a bandeja acompanhar via
    /// GET /update-status.
    /// </summary>
    public async Task CheckAndApplyVersionAsync(string targetVersion, CancellationToken cancellationToken)
    {
        if (_progress.IsInProgress)
        {
            _logger.LogInformation("Atualizacao sob demanda ignorada: ja existe uma em andamento.");
            return;
        }

        if (string.IsNullOrWhiteSpace(targetVersion))
        {
            _progress.Set(UpdateStatus.Failed, 0, "Nenhuma versao foi selecionada.", error: "missing_version");
            return;
        }

        _progress.Set(UpdateStatus.CheckingForUpdate, 0, $"Consultando o ERP sobre a versao {targetVersion}...");

        var result = await _latestPackageClient.FetchAvailableAsync(cancellationToken);
        if (!result.Succeeded || result.Packages is null)
        {
            var message = result.Error == "no_package_published"
                ? "Nenhuma versao publicada foi encontrada no ERP."
                : $"Nao foi possivel consultar o ERP ({result.Error}).";
            _logger.LogWarning("CheckAndApplyVersionAsync: falha ao buscar pacotes disponiveis ({Error}).", result.Error);
            _progress.Set(UpdateStatus.Failed, 0, message, error: result.Error);
            return;
        }

        var package = result.Packages.FirstOrDefault(
            p => string.Equals(p.Version, targetVersion, StringComparison.OrdinalIgnoreCase));
        if (package is null)
        {
            _progress.Set(
                UpdateStatus.Failed,
                0,
                $"A versao {targetVersion} nao esta mais disponivel para esta instalacao.",
                targetVersion,
                "version_not_available");
            return;
        }

        var currentVersion = _options.CurrentValue.AgentVersion;
        if (AgentVersion.IsDowngradeOrSame(currentVersion, package.Version))
        {
            _logger.LogWarning(
                "CheckAndApplyVersionAsync: recusando downgrade/versao igual ({Current} -> {Target}).",
                currentVersion, package.Version);
            _progress.Set(UpdateStatus.UpToDate, 100, $"Ja esta na versao {currentVersion}.", package.Version);
            return;
        }

        if (package.Blocked)
        {
            var minimo = string.IsNullOrWhiteSpace(package.ErpMinimo) ? "uma versao mais recente" : package.ErpMinimo;
            _progress.Set(
                UpdateStatus.Failed,
                0,
                $"A versao {package.Version} exige o ERP na versao {minimo} ou superior. Atualize o ERP antes de aplicar esta versao.",
                package.Version,
                package.BlockedReason ?? "blocked");
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

        if (!Sha256HexPattern.IsMatch(sha256))
        {
            _logger.LogError(
                "Self-update aborted: Sha256 cadastrado no ERP para v{Version} nao e um hash valido ({Length} chars): {Sha256}",
                targetVersion, sha256?.Length ?? 0, sha256);
            _progress.Set(
                UpdateStatus.Failed,
                0,
                "O Sha256 cadastrado no ERP para essa versao nao e um hash valido (parece ser uma URL ou texto incompleto). "
                    + "Corrija o pacote em API de Sincronizacao > Pacotes de atualizacao antes de tentar de novo.",
                targetVersion,
                "invalid_package_sha256");
            return false;
        }

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
        catch (InvalidDataException ex)
        {
            // VerifySha256 falhou: o SHA256 calculado do ZIP baixado nao bate com o
            // cadastrado no ERP. Mensagem completa (hash esperado x obtido) vai pro
            // log; a bandeja mostra um resumo acionavel.
            _logger.LogError(ex, "Self-update SHA256 mismatch. Update aborted.");
            _progress.Set(
                UpdateStatus.Failed,
                0,
                "O SHA256 do pacote baixado nao confere com o cadastrado no ERP (pacote corrompido ou "
                    + "cadastrado com o hash errado). Detalhes no log do servico.",
                targetVersion,
                "sha256_mismatch");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Self-update download/verification failed. Update aborted.");
            _progress.Set(UpdateStatus.Failed, 0, "Falha ao baixar ou verificar o pacote de atualizacao.", targetVersion, ex.GetType().Name);
            return false;
        }

        // Preferir o self-update.ps1 que veio DENTRO do pacote baixado ao que
        // esta instalado: assim uma correcao no script ja vale na propria
        // atualizacao que a entrega, sem depender de um "hop" extra. Cai para
        // o instalado se o pacote nao trouxer o script (formatos antigos).
        var scriptPath = ExtractUpdateScriptFromPackage(zipPath, downloadDir)
            ?? Path.Combine(AppContext.BaseDirectory, "self-update.ps1");
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

    /// <summary>
    /// Extrai <c>payload/Sync/Agent/self-update.ps1</c> do pacote baixado para
    /// <paramref name="targetDir"/> e devolve o caminho. Retorna null (e o
    /// chamador usa o script instalado) se o pacote nao tiver o script ou a
    /// extracao falhar.
    /// </summary>
    private string? ExtractUpdateScriptFromPackage(string zipPath, string targetDir)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var entry = archive.Entries.FirstOrDefault(e =>
                e.FullName.Replace('\\', '/').EndsWith("payload/Sync/Agent/self-update.ps1", StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                return null;
            }

            var destination = Path.Combine(targetDir, "self-update.ps1");
            entry.ExtractToFile(destination, overwrite: true);
            _logger.LogInformation("Usando self-update.ps1 do proprio pacote baixado ({Path}).", destination);
            return destination;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao extrair self-update.ps1 do pacote; usando o script instalado.");
            return null;
        }
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
/// Resultado de <see cref="SelfUpdater.PreviewAvailableAsync"/>: versao
/// instalada e a lista de versoes que podem ser oferecidas (ja sem
/// downgrade/mesma versao, ja sem Sha256 invalido). <see cref="Packages"/>
/// pode conter itens com <see cref="AvailablePackageItem.Blocked"/> = true
/// (incompatibilidade de ERP) — a UI deve mostrar o motivo e nunca deixar
/// aplicar. <see cref="Error"/> preenchido quando nao foi possivel consultar
/// o ERP (ou o self-update esta desabilitado); nesse caso <see cref="Packages"/>
/// vem vazio.
/// </summary>
public sealed record UpdatePreview(
    string CurrentVersion,
    IReadOnlyList<AvailablePackageItem> Packages,
    string? Error);
