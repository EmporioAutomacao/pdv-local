namespace SyncAgent.Update;

public enum UpdateStatus
{
    Idle,
    CheckingForUpdate,
    UpToDate,
    Downloading,
    Verifying,
    Applying,
    Failed,
}

/// <summary>
/// Estado compartilhado (singleton) do progresso de uma atualizacao em
/// andamento, disparada sob demanda via POST /update-now. Consumido por
/// GET /update-status, que a bandeja faz polling enquanto mostra a barra de
/// progresso. Thread-safe: escrito pelo SelfUpdater em background, lido
/// pela requisicao HTTP do LocalStatusServer.
/// </summary>
public sealed class UpdateProgressState
{
    private readonly object _lock = new();
    private UpdateStatus _status = UpdateStatus.Idle;
    private int _percent;
    private string _message = "";
    private string? _targetVersion;
    private string? _error;
    private DateTimeOffset _updatedAtUtc = DateTimeOffset.UtcNow;

    public bool IsInProgress
    {
        get
        {
            lock (_lock)
            {
                return _status is UpdateStatus.CheckingForUpdate or UpdateStatus.Downloading
                    or UpdateStatus.Verifying or UpdateStatus.Applying;
            }
        }
    }

    public void Set(UpdateStatus status, int percent, string message, string? targetVersion = null, string? error = null)
    {
        lock (_lock)
        {
            _status = status;
            _percent = Math.Clamp(percent, 0, 100);
            _message = message;
            if (targetVersion is not null)
            {
                _targetVersion = targetVersion;
            }
            _error = error;
            _updatedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    public UpdateProgressSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new UpdateProgressSnapshot(_status, _percent, _message, _targetVersion, _error, _updatedAtUtc);
        }
    }
}

public sealed record UpdateProgressSnapshot(
    UpdateStatus Status,
    int Percent,
    string Message,
    string? TargetVersion,
    string? Error,
    DateTimeOffset UpdatedAtUtc);
