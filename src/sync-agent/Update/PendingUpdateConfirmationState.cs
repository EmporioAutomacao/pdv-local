using System.Text.Json;

namespace SyncAgent.Update;

/// <summary>
/// Persistencia local (fora de <c>PDV\</c>/<c>Sync\</c>, sobrevive a troca de
/// binarios - mesmo raciocinio do <c>update-status.json</c> escrito pelo
/// <c>self-update.ps1</c>) do andamento da confirmacao de um
/// <c>pending_update</c> em modo <c>confirm</c>. O ERP e a fonte de verdade;
/// isto e so um cache para nao reperguntar ao usuario a cada heartbeat (30s)
/// e para o prazo de 5 min continuar valendo mesmo se o ERP ficar
/// inalcancavel por alguns instantes.
/// </summary>
public sealed class PendingUpdateConfirmationState
{
    private readonly ILogger<PendingUpdateConfirmationState> _logger;
    private readonly object _lock = new();

    public PendingUpdateConfirmationState(ILogger<PendingUpdateConfirmationState> logger)
    {
        _logger = logger;
    }

    private static string ResolveStateFilePath()
    {
        // SyncAgent.exe roda em <InstallRoot>\Sync\Agent\, dois niveis abaixo da raiz
        // (mesmo calculo usado em SelfUpdater.DownloadVerifyAndApplyAsync).
        var installRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
        return Path.Combine(installRoot, "pending-update-confirmation.json");
    }

    /// <summary>
    /// Devolve o estado salvo se ainda for do mesmo pedido de atualizacao
    /// (mesma versao); caso contrario (versao nova, ou nada salvo ainda),
    /// comeca um estado limpo e ja o persiste.
    /// </summary>
    public PendingUpdateConfirmationSnapshot LoadOrReset(string version)
    {
        lock (_lock)
        {
            var existing = TryLoad();
            if (existing is not null && existing.Version == version)
            {
                return existing;
            }

            var fresh = new PendingUpdateConfirmationSnapshot(version, null, false, null, null, "none");
            SaveLocked(fresh);
            return fresh;
        }
    }

    public void Save(PendingUpdateConfirmationSnapshot snapshot)
    {
        lock (_lock)
        {
            SaveLocked(snapshot);
        }
    }

    private void SaveLocked(PendingUpdateConfirmationSnapshot snapshot)
    {
        try
        {
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            File.WriteAllText(ResolveStateFilePath(), json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao salvar o estado local de confirmacao de atualizacao pendente.");
        }
    }

    private PendingUpdateConfirmationSnapshot? TryLoad()
    {
        try
        {
            var path = ResolveStateFilePath();
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<PendingUpdateConfirmationSnapshot>(
                File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao ler o estado local de confirmacao de atualizacao pendente.");
            return null;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            try
            {
                var path = ResolveStateFilePath();
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao limpar o estado local de confirmacao de atualizacao pendente.");
            }
        }
    }
}

/// <param name="UserChoice">"none" ou "confirmed".</param>
public sealed record PendingUpdateConfirmationSnapshot(
    string Version,
    DateTimeOffset? PresentedAtUtc,
    bool PresentedAckSent,
    DateTimeOffset? LocalDeadlineUtc,
    DateTimeOffset? LocalScheduledAtUtc,
    string UserChoice);
