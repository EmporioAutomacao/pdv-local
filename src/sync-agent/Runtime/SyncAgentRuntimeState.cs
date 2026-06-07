namespace SyncAgent.Runtime;

public sealed class SyncAgentRuntimeState
{
    private readonly object _lock = new();
    private RuntimeSnapshot _snapshot = new(null, null, "starting", null);

    public RuntimeSnapshot Snapshot
    {
        get
        {
            lock (_lock)
            {
                return _snapshot;
            }
        }
    }

    public void MarkCycleStarted(string trigger)
    {
        lock (_lock)
        {
            _snapshot = _snapshot with
            {
                CurrentStatus = "running",
                LastCycleTrigger = trigger
            };
        }
    }

    public void MarkCycleSucceeded(DateTimeOffset completedAtUtc, string trigger)
    {
        lock (_lock)
        {
            _snapshot = new RuntimeSnapshot(completedAtUtc, trigger, "idle", null);
        }
    }

    public void MarkCycleFailed(DateTimeOffset failedAtUtc, string trigger, string error)
    {
        lock (_lock)
        {
            _snapshot = new RuntimeSnapshot(failedAtUtc, trigger, "degraded", error);
        }
    }

    public void MarkNotProvisioned(DateTimeOffset checkedAtUtc, string trigger)
    {
        lock (_lock)
        {
            _snapshot = new RuntimeSnapshot(checkedAtUtc, trigger, "not_provisioned", "Aguardando ativacao com o ERP.");
        }
    }
}

public sealed record RuntimeSnapshot(
    DateTimeOffset? LastCycleCompletedAtUtc,
    string? LastCycleTrigger,
    string CurrentStatus,
    string? LastError);
