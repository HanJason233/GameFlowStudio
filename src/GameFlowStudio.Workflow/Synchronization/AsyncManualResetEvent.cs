namespace GameFlowStudio.Workflow.Synchronization;

internal sealed class AsyncManualResetEvent(bool initialState)
{
    private volatile TaskCompletionSource _source = Create(initialState);

    public Task WaitAsync(CancellationToken cancellationToken) => _source.Task.WaitAsync(cancellationToken);
    public void Set() => _source.TrySetResult();
    public void Reset()
    {
        while (true)
        {
            var current = _source;
            if (!current.Task.IsCompleted) return;
            var replacement = Create(false);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _source, replacement, current), current)) return;
        }
    }

    private static TaskCompletionSource Create(bool completed)
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (completed) source.SetResult();
        return source;
    }
}
