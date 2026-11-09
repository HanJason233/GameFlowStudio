using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;

namespace GameFlowStudio.Protocol;

/// <summary>Framed, correlated, duplex JSON transport over one named-pipe stream.</summary>
public sealed class PipeMessageChannel : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _closed = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<ProtocolMessage>> _pending = new();
    private Task? _readerTask;
    private int _isClosed;
    private int _disposeStarted;

    public PipeMessageChannel(Stream stream) => _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    public bool IsConnected => Volatile.Read(ref _isClosed) == 0 && (_stream is not NamedPipeClientStream client || client.IsConnected);
    public event Action<ProtocolMessage>? MessageReceived;
    public event Action<Exception?>? Closed;

    public void Start()
    {
        if (_readerTask is not null) throw new InvalidOperationException("Pipe reader is already running.");
        _readerTask = ReadLoopAsync(_closed.Token);
    }

    public Task<T?> RequestAsync<T>(string type, object? payload, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        RequestAsync<T>(ProtocolMessage.Command(type, payload), timeout, cancellationToken);

    public async Task<T?> RequestAsync<T>(ProtocolMessage request, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Kind != MessageKind.Command) throw new ArgumentException("Only command messages can be sent as requests.", nameof(request));
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isClosed) != 0, this);
        var completion = new TaskCompletionSource<ProtocolMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(request.MessageId, completion)) throw new InvalidOperationException("Duplicate protocol message id.");
        try
        {
            await WriteAsync(request, cancellationToken).ConfigureAwait(false);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
            timeoutSource.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
            ProtocolMessage response;
            try { response = await completion.Task.WaitAsync(timeoutSource.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_closed.IsCancellationRequested)
            {
                throw new TimeoutException($"Runtime 命令 {request.Type} 等待响应超时。");
            }

            var result = response.Payload.Deserialize<RpcResponse>(ProtocolJson.Options)
                ?? throw new InvalidDataException("Runtime returned an empty response.");
            if (!result.Success) throw new RemoteCommandException(result.Error ?? "Runtime 命令失败。");
            return result.Data is { } data && data.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
                ? data.Deserialize<T>(ProtocolJson.Options)
                : default;
        }
        finally { _pending.TryRemove(request.MessageId, out _); }
    }

    public Task SendEventAsync(string type, object? payload, long sequence, CancellationToken cancellationToken = default) =>
        WriteAsync(ProtocolMessage.Event(type, payload, sequence), cancellationToken);

    public Task RespondAsync(ProtocolMessage request, object? data = null, string? error = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = error is null ? RpcResponse.Ok(data) : RpcResponse.Fail(error);
        var response = new ProtocolMessage
        {
            Kind = MessageKind.Response,
            Type = request.Type,
            ReplyTo = request.MessageId,
            Payload = ProtocolMessage.SerializePayload(result)
        };
        return WriteAsync(response, cancellationToken);
    }

    private async Task WriteAsync(ProtocolMessage message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, ProtocolJson.Options);
        if (json.Length is <= 0 or > ProtocolConstants.MaximumMessageBytes)
            throw new InvalidDataException($"Protocol message length {json.Length} is outside the supported range.");
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, json.Length);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync(json, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var header = new byte[sizeof(int)];
                if (!await ReadExactlyAsync(_stream, header, allowCleanEof: true, cancellationToken).ConfigureAwait(false)) break;
                var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length is <= 0 or > ProtocolConstants.MaximumMessageBytes)
                    throw new InvalidDataException($"Invalid protocol frame length: {length}.");
                var bytes = new byte[length];
                if (!await ReadExactlyAsync(_stream, bytes, allowCleanEof: false, cancellationToken).ConfigureAwait(false))
                    throw new EndOfStreamException("Pipe disconnected in the middle of a protocol frame.");
                var message = JsonSerializer.Deserialize<ProtocolMessage>(bytes, ProtocolJson.Options)
                    ?? throw new InvalidDataException("Pipe frame did not contain a JSON protocol message.");
                if (message.Kind == MessageKind.Response && message.ReplyTo is { } replyTo && _pending.TryGetValue(replyTo, out var pending))
                    pending.TrySetResult(message);
                else if (MessageReceived is { } received)
                {
                    foreach (Action<ProtocolMessage> handler in received.GetInvocationList())
                        try { handler(message); } catch { }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) { failure = exception; }
        finally
        {
            if (Interlocked.Exchange(ref _isClosed, 1) == 0)
            {
                _closed.Cancel();
                foreach (var pending in _pending.Values) pending.TrySetException(failure ?? new EndOfStreamException("Named pipe disconnected."));
                if (Closed is { } closed)
                    foreach (Action<Exception?> handler in closed.GetInvocationList())
                        try { handler(failure); } catch { }
            }
        }
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, Memory<byte> buffer, bool allowCleanEof, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (allowCleanEof && offset == 0) return false;
                throw new EndOfStreamException("Named pipe disconnected while reading a message.");
            }
            offset += read;
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        Interlocked.Exchange(ref _isClosed, 1);
        _closed.Cancel();
        await _stream.DisposeAsync().ConfigureAwait(false);
        if (_readerTask is not null)
        {
            try { await _readerTask.ConfigureAwait(false); }
            catch { }
        }
        _closed.Dispose();
        _writeLock.Dispose();
    }
}
