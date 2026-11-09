using System.IO.Pipes;
using System.Diagnostics;
using GameFlowStudio.Protocol;
using Microsoft.Extensions.Logging;

namespace GameFlowStudio.Runtime;

public sealed class RuntimePipeServer(
    string pipeName,
    RuntimeCommandDispatcher dispatcher,
    Action shutdown,
    bool exitWhenOrphaned,
    ILogger<RuntimePipeServer> logger)
{
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private DateTimeOffset? _lastDisconnectedAt;
    private bool _orphanStopRequested;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !dispatcher.ShutdownRequested)
        {
            await using var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                64 * 1024,
                64 * 1024);
            try
            {
                using var connectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectionTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                try { await pipe.WaitForConnectionAsync(connectionTimeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    if (await ShouldShutdownAsOrphanAsync().ConfigureAwait(false)) break;
                    continue;
                }
                _lastDisconnectedAt = null;
                await ServeConnectionAsync(pipe, cancellationToken).ConfigureAwait(false);
                if (!dispatcher.ShutdownRequested) _lastDisconnectedAt = DateTimeOffset.UtcNow;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (IOException exception)
            {
                logger.LogWarning(exception, "Runtime named pipe connection ended");
            }
        }
        if (dispatcher.ShutdownRequested) shutdown();
        else if (exitWhenOrphaned && !cancellationToken.IsCancellationRequested) shutdown();
    }

    private async Task ServeConnectionAsync(Stream pipe, CancellationToken cancellationToken)
    {
        await using var channel = new PipeMessageChannel(pipe);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.MessageReceived += message => _ = HandleMessageAsync(channel, message, closed, cancellationToken);
        channel.Closed += _ => closed.TrySetResult();
        dispatcher.Attach(channel);
        channel.Start();
        try { await closed.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { dispatcher.Detach(channel); }
        if (dispatcher.ShutdownRequested) shutdown();
    }

    private async Task<bool> ShouldShutdownAsOrphanAsync()
    {
        if (!exitWhenOrphaned) return false;
        if (!dispatcher.IsInitialized)
        {
            if (dispatcher.IsRunActive) return false;
            return DateTimeOffset.UtcNow - _startedAt > TimeSpan.FromSeconds(60);
        }
        if (dispatcher.ParentProcessId is not { } parentId || IsProcessAlive(parentId)) return false;
        var orphanAge = DateTimeOffset.UtcNow - (_lastDisconnectedAt ?? _startedAt);
        if (dispatcher.IsRunActive)
        {
            if (orphanAge > TimeSpan.FromMinutes(5) && !_orphanStopRequested)
            {
                _orphanStopRequested = true;
                logger.LogWarning("Parent Desktop exited; stopping orphaned Runtime task after a five-minute grace period.");
                await dispatcher.StopOrphanedRunAsync().ConfigureAwait(false);
            }
            return false;
        }
        return orphanAge > TimeSpan.FromSeconds(30);
    }

    private static bool IsProcessAlive(int processId)
    {
        try { using var process = Process.GetProcessById(processId); return !process.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private async Task HandleMessageAsync(PipeMessageChannel channel, ProtocolMessage message, TaskCompletionSource closed, CancellationToken cancellationToken)
    {
        if (message.Kind != MessageKind.Command) return;
        try
        {
            var response = await dispatcher.DispatchAsync(message, cancellationToken).ConfigureAwait(false);
            await channel.RespondAsync(message, response, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (dispatcher.ShutdownRequested) closed.TrySetResult();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Runtime command {Command} failed", message.Type);
            try { await channel.RespondAsync(message, error: exception.Message, cancellationToken: cancellationToken).ConfigureAwait(false); }
            catch (IOException) { }
        }
    }
}
