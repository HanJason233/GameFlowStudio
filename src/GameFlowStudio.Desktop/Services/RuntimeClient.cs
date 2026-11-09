using System.IO.Pipes;
using System.IO;
using GameFlowStudio.Protocol;

namespace GameFlowStudio.Desktop.Services;

/// <summary>Single-connection client for the Runtime named-pipe protocol.</summary>
public sealed class RuntimeClient : IAsyncDisposable
{
    private PipeMessageChannel? _channel;
    public bool IsConnected => _channel?.IsConnected == true;
    public event Action<ProtocolMessage>? MessageReceived;
    public event Action<Exception?>? Disconnected;

    public async Task<InitializeResponse> ConnectAsync(string pipeName, string token, CancellationToken cancellationToken = default)
    {
        if (IsConnected) throw new InvalidOperationException("RuntimeClient is already connected.");
        if (_channel is { } previous)
        {
            _channel = null;
            await previous.DisposeAsync().ConfigureAwait(false);
        }
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(5000, cancellationToken).ConfigureAwait(false);
            var channel = new PipeMessageChannel(pipe);
            channel.MessageReceived += message => MessageReceived?.Invoke(message);
            channel.Closed += exception => Disconnected?.Invoke(exception);
            _channel = channel;
            channel.Start();
            var initialized = await channel.RequestAsync<InitializeResponse>(RuntimeCommands.Initialize,
                new InitializeRequest(token, ProtocolConstants.CurrentVersion, Environment.ProcessId),
                TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            if (initialized is null || !initialized.Ready || initialized.ProtocolVersion != ProtocolConstants.CurrentVersion)
                throw new InvalidDataException("Runtime protocol handshake failed.");
            return initialized;
        }
        catch
        {
            if (_channel is { } channel) await channel.DisposeAsync().ConfigureAwait(false);
            else await pipe.DisposeAsync().ConfigureAwait(false);
            _channel = null;
            throw;
        }
    }

    public Task<T?> RequestAsync<T>(string command, object? payload = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        (_channel ?? throw new InvalidOperationException("Runtime pipe is not connected."))
        .RequestAsync<T>(command, payload, timeout, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        var channel = Interlocked.Exchange(ref _channel, null);
        if (channel is not null) await channel.DisposeAsync().ConfigureAwait(false);
    }
}
