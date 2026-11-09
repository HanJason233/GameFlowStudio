using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading.Channels;
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
        if (dispatcher.ShutdownRequested || (exitWhenOrphaned && !cancellationToken.IsCancellationRequested)) shutdown();
    }

    private async Task ServeConnectionAsync(Stream pipe, CancellationToken cancellationToken)
    {
        await using var channel = new PipeMessageChannel(pipe);
        var commands = Channel.CreateBounded<ProtocolMessage>(new BoundedChannelOptions(128)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        var authenticated = false;
        var attached = false;
        using var authenticationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        authenticationTimeout.CancelAfter(TimeSpan.FromSeconds(5));

        channel.MessageReceived += message =>
        {
            if (!commands.Writer.TryWrite(message))
            {
                logger.LogWarning("Runtime pipe command queue was full; closing the connection.");
                _ = channel.DisposeAsync();
            }
        };
        channel.Closed += exception =>
        {
            if (exception is not null) logger.LogDebug(exception, "Runtime pipe client disconnected");
            commands.Writer.TryComplete();
        };
        channel.Start();

        try
        {
            while (!cancellationToken.IsCancellationRequested && channel.IsConnected && !dispatcher.ShutdownRequested)
            {
                if (!authenticated && authenticationTimeout.IsCancellationRequested)
                {
                    logger.LogWarning("Runtime pipe client did not authenticate within five seconds.");
                    break;
                }
                ProtocolMessage message;
                try
                {
                    var readToken = authenticated ? cancellationToken : authenticationTimeout.Token;
                    message = await commands.Reader.ReadAsync(readToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !authenticated)
                {
                    logger.LogWarning("Runtime pipe client did not authenticate within five seconds.");
                    break;
                }
                catch (ChannelClosedException) { break; }

                try
                {
                    var result = await dispatcher.DispatchAsync(message, cancellationToken, authenticated).ConfigureAwait(false);
                    if (!authenticated && message.Type == RuntimeCommands.Initialize)
                    {
                        authenticated = true;
                        authenticationTimeout.Cancel();
                        dispatcher.Attach(channel);
                        attached = true;
                    }
                    await channel.RespondAsync(message, result, cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (attached && message.Type == RuntimeCommands.Initialize)
                        await dispatcher.PublishWorkerReadyAsync(cancellationToken).ConfigureAwait(false);
                    if (dispatcher.ShutdownRequested) break;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Runtime command {Command} failed", message.Type);
                    try { await channel.RespondAsync(message, error: exception.Message, cancellationToken: cancellationToken).ConfigureAwait(false); }
                    catch (Exception responseException) when (responseException is IOException or ObjectDisposedException or OperationCanceledException) { }
                    // 未认证管道仅保留至五秒握手期限，保证客户端能读取拒绝响应；认证初始化失败时立即关闭。
                    if (!authenticated && message.Type == RuntimeCommands.Initialize) break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            commands.Writer.TryComplete();
            if (attached) dispatcher.Detach(channel);
        }

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
}
