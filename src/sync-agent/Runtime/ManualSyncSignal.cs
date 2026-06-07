using System.Threading.Channels;

namespace SyncAgent.Runtime;

public sealed class ManualSyncSignal
{
    private readonly Channel<DateTimeOffset> _signals = Channel.CreateBounded<DateTimeOffset>(
        new BoundedChannelOptions(capacity: 1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    public bool TrySignal()
    {
        return _signals.Writer.TryWrite(DateTimeOffset.UtcNow);
    }

    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var readTask = _signals.Reader.WaitToReadAsync(cancellationToken).AsTask();
        var delayTask = Task.Delay(timeout, cancellationToken);

        var completedTask = await Task.WhenAny(readTask, delayTask);
        if (completedTask != readTask || !await readTask)
        {
            return false;
        }

        while (_signals.Reader.TryRead(out _))
        {
        }

        return true;
    }
}
