namespace GameFlowStudio.Workflow.Synchronization;

internal static class PausableDelay
{
    public static async Task WaitAsync(int durationMs, Func<CancellationToken, Task> waitIfPausedAsync, CancellationToken cancellationToken)
    {
        var remaining = durationMs;
        while (remaining > 0)
        {
            await waitIfPausedAsync(cancellationToken).ConfigureAwait(false);
            var slice = Math.Min(remaining, 100);
            await Task.Delay(slice, cancellationToken).ConfigureAwait(false);
            remaining -= slice;
        }
    }
}
