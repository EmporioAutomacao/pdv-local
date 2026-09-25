using System.Threading.Channels;

namespace SyncAgent.Runtime;

public sealed class ManualSyncSignal
{
    private readonly Channel<ManualSyncRequest> _signals = Channel.CreateBounded<ManualSyncRequest>(
        new BoundedChannelOptions(capacity: 1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    /// <summary>Sinaliza "rode o proximo ciclo agora". <paramref name="entityTypes"/>
    /// nulo/vazio (o default de todos os call-sites existentes) mantem o
    /// comportamento historico: processa tudo. Um filtro so vale para o ciclo que
    /// ele mesmo disparar - nunca e persistido.</summary>
    public bool TrySignal(IReadOnlyCollection<string>? entityTypes = null)
    {
        return _signals.Writer.TryWrite(new ManualSyncRequest(DateTimeOffset.UtcNow, Normalize(entityTypes)));
    }

    public async Task<ManualSyncWaitResult> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var readTask = _signals.Reader.WaitToReadAsync(cancellationToken).AsTask();
        var delayTask = Task.Delay(timeout, cancellationToken);

        var completedTask = await Task.WhenAny(readTask, delayTask);
        if (completedTask != readTask || !await readTask)
        {
            return ManualSyncWaitResult.NotTriggered;
        }

        ManualSyncRequest? last = null;
        while (_signals.Reader.TryRead(out var request))
        {
            last = request; // mantem so o mais recente, mesmo coalescimento de antes
        }

        return new ManualSyncWaitResult(true, last?.EntityTypes);
    }

    private static IReadOnlySet<string>? Normalize(IReadOnlyCollection<string>? entityTypes)
    {
        if (entityTypes is null || entityTypes.Count == 0)
        {
            return null;
        }

        var set = new HashSet<string>(
            entityTypes.Where(entityType => !string.IsNullOrWhiteSpace(entityType)),
            StringComparer.OrdinalIgnoreCase);
        return set.Count == 0 ? null : set;
    }
}

public sealed record ManualSyncRequest(DateTimeOffset RequestedAtUtc, IReadOnlySet<string>? EntityTypes);

public sealed record ManualSyncWaitResult(bool Triggered, IReadOnlySet<string>? EntityTypes)
{
    public static readonly ManualSyncWaitResult NotTriggered = new(false, null);
}
