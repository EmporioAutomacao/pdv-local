namespace SyncAgent.Runtime;

/// <summary>
/// Log em memoria da ultima sincronizacao (coleta Arpa + envio ao ERP), para a
/// aba Configuracoes &gt; Arpa mostrar ao vivo o que esta sendo feito quando o
/// operador clica em "Sincronizar" - quantos Clientes/Produtos/Estoque/Vendas/
/// Financeiro foram lidos e enviados. Nao persiste (o /logs ja guarda o
/// historico estruturado); e so a "fita" da execucao corrente.
/// </summary>
public sealed class ArpaSyncRunLog
{
    private const int MaxLines = 400;

    private readonly object _lock = new();
    private readonly List<ArpaSyncLogLine> _lines = new();
    private long _runId;
    private bool _running;
    private DateTimeOffset? _startedAtUtc;
    private DateTimeOffset? _finishedAtUtc;
    private string? _trigger;

    public long BeginRun(string trigger)
    {
        lock (_lock)
        {
            _lines.Clear();
            _running = true;
            _startedAtUtc = DateTimeOffset.UtcNow;
            _finishedAtUtc = null;
            _trigger = trigger;
            _runId++;
            AddLocked("info", $"Sincronizacao iniciada ({trigger}).");
            return _runId;
        }
    }

    public void EndRun(string summary)
    {
        lock (_lock)
        {
            if (!_running)
            {
                return;
            }

            AddLocked("info", summary);
            _running = false;
            _finishedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    public void Add(string level, string message)
    {
        lock (_lock)
        {
            AddLocked(level, message);
        }
    }

    private void AddLocked(string level, string message)
    {
        _lines.Add(new ArpaSyncLogLine(DateTimeOffset.UtcNow, level, message));
        if (_lines.Count > MaxLines)
        {
            _lines.RemoveRange(0, _lines.Count - MaxLines);
        }
    }

    public ArpaSyncLogSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new ArpaSyncLogSnapshot(
                _runId,
                _running,
                _startedAtUtc,
                _finishedAtUtc,
                _trigger,
                _lines.ToArray());
        }
    }
}

public sealed record ArpaSyncLogLine(DateTimeOffset AtUtc, string Level, string Message);

public sealed record ArpaSyncLogSnapshot(
    long RunId,
    bool Running,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    string? Trigger,
    IReadOnlyList<ArpaSyncLogLine> Lines);
