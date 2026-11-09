using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using GameFlowStudio.Protocol;
using Microsoft.Extensions.Logging;

namespace GameFlowStudio.Desktop.Services;

/// <summary>Owns the UI-free Runtime worker, its private pipe, reconnect and shutdown lifecycle.</summary>
public sealed class RuntimeProcessManager : IAsyncDisposable
{
    private const int HeartbeatRestartThreshold = 3;
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(3);
    private readonly RuntimeClient _client;
    private readonly ILogger<RuntimeProcessManager> _logger;
    private readonly string? _workerExecutablePath;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly SemaphoreSlim _recoveryGate = new(1, 1);
    private readonly object _backgroundSync = new();
    private readonly object _eventSync = new();
    private readonly object _shutdownSync = new();
    private readonly SortedDictionary<long, ProtocolMessage> _eventsDuringRecovery = new();
    private readonly string _pipeName = $"GameFlowStudio.Runtime.{Environment.ProcessId}.{Guid.NewGuid():N}";
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly CancellationTokenSource _lifetime = new();
    private Process? _process;
    private Task? _reconnectTask;
    private Task? _eventRecoveryTask;
    private Task? _heartbeatTask;
    private Task? _shutdownTask;
    private Task? _disposeTask;
    private string? _snapshotPath;
    private string? _scriptId;
    private int? _runtimeProcessId;
    private long _lastEventSequence;
    private int _heartbeatFailures;
    private int _disposed;
    private int _shuttingDown;
    private bool _recovering;

    public RuntimeProcessManager(RuntimeClient client, ILogger<RuntimeProcessManager> logger, string? workerExecutablePath = null)
    {
        _client = client;
        _logger = logger;
        _workerExecutablePath = workerExecutablePath;
        _client.MessageReceived += OnMessageReceived;
        _client.Disconnected += OnDisconnected;
    }

    public bool IsConnected => _client.IsConnected;
    public int? RuntimeProcessId => _runtimeProcessId;
    public event Action<ProtocolMessage>? RuntimeEventReceived;
    public event Action<RuntimeStatusResponse>? StatusRecovered;
    public event Action<RuntimeRunSnapshot>? RunSnapshotRecovered;
    public event Action<Exception?>? ConnectionLost;

    public void ForgetProjectSnapshot(string snapshotPath)
    {
        if (string.Equals(_snapshotPath, Path.GetFullPath(snapshotPath), StringComparison.OrdinalIgnoreCase))
            _snapshotPath = null;
    }

    public async Task<LoadProjectResponse> LoadProjectAsync(string snapshotPath, string? scriptId = null, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(snapshotPath);
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var response = await RequestRequiredAsync<LoadProjectResponse>(RuntimeCommands.LoadProject,
            new LoadProjectRequest(fullPath, scriptId), TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        _snapshotPath = fullPath;
        _scriptId = scriptId;
        return response;
    }

    public async Task<ValidateResponse> ValidateAsync(string? scriptId = null, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        return await RequestRequiredAsync<ValidateResponse>(RuntimeCommands.Validate, new ValidateRequest(scriptId), TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
    }

    public async Task<RunAcceptedResponse> StartRunAsync(string runId, string? scriptId, CancellationToken cancellationToken = default)
    {
        _scriptId = scriptId ?? _scriptId;
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        return await RequestRequiredAsync<RunAcceptedResponse>(RuntimeCommands.StartRun, new StartRunRequest(runId, scriptId), TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
    }

    public async Task<RuntimeStatusResponse> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        return await RequestRequiredAsync<RuntimeStatusResponse>(RuntimeCommands.GetStatus, null, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
    }

    public async Task<RuntimeRunSnapshot> GetRunSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        return await RequestRequiredAsync<RuntimeRunSnapshot>(RuntimeCommands.GetRunSnapshot, null, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
    }

    public async Task<RuntimeEventsResponse> GetEventsAsync(long afterSequence, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        return await RequestRequiredAsync<RuntimeEventsResponse>(RuntimeCommands.GetEvents,
            new GetEventsRequest(afterSequence), TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RuntimeLogInfo>> GetLogsAsync(long afterSequence = 0, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        return await RequestRequiredAsync<IReadOnlyList<RuntimeLogInfo>>(RuntimeCommands.GetLogs,
            new { afterSequence }, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
    }

    public Task PauseRunAsync(CancellationToken cancellationToken = default) => SendLifecycleCommandAsync(RuntimeCommands.PauseRun, cancellationToken);
    public Task ResumeRunAsync(CancellationToken cancellationToken = default) => SendLifecycleCommandAsync(RuntimeCommands.ResumeRun, cancellationToken);
    public Task StopRunAsync(CancellationToken cancellationToken = default) => SendLifecycleCommandAsync(RuntimeCommands.StopRun, cancellationToken);

    private async Task SendLifecycleCommandAsync(string command, CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        _ = await RequestRequiredAsync<RuntimeStatusResponse>(command, null, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> RequestRequiredAsync<T>(string command, object? payload, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var response = await _client.RequestAsync<T>(command, payload, timeout, cancellationToken).ConfigureAwait(false);
        return response ?? throw new InvalidDataException($"Runtime returned no data for {command}.");
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _shuttingDown) != 0) throw new ObjectDisposedException(nameof(RuntimeProcessManager));
        using var connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var connectToken = connectionLifetime.Token;
        if (_client.IsConnected && _process is { HasExited: false }) return;
        await _connectGate.WaitAsync(connectToken).ConfigureAwait(false);
        try
        {
            if (_client.IsConnected && _process is { HasExited: false }) return;
            // 进程退出或快照同步失败后，管道可能短暂仍显示连接；重试握手前先释放旧管道。
            if (_client.IsConnected) await _client.DisposeAsync().ConfigureAwait(false);
            var changedProcess = _process is null || _process.HasExited;
            if (changedProcess)
            {
                _process?.Dispose();
                _process = StartWorker();
                ResetEventSequence();
            }
            var process = _process ?? throw new InvalidOperationException("Runtime worker process was not started.");
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            var delay = TimeSpan.FromMilliseconds(150);
            Exception? lastError = null;

            while (DateTime.UtcNow < deadline && !connectToken.IsCancellationRequested)
            {
                if (process.HasExited)
                    throw new InvalidOperationException($"Runtime exited during startup (code {process.ExitCode}).");
                BeginEventRecovery();
                try
                {
                    var initialized = await _client.ConnectAsync(_pipeName, _token, connectToken).ConfigureAwait(false);
                    if (_runtimeProcessId != initialized.RuntimeProcessId)
                    {
                        changedProcess = true;
                        ResetEventSequence();
                    }
                    _runtimeProcessId = initialized.RuntimeProcessId;
                    if (changedProcess && !string.IsNullOrWhiteSpace(_snapshotPath))
                        _ = await RequestRequiredAsync<LoadProjectResponse>(RuntimeCommands.LoadProject,
                            new LoadProjectRequest(_snapshotPath, _scriptId), TimeSpan.FromSeconds(5), connectToken).ConfigureAwait(false);

                    await RecoverConnectionStateAsync(connectToken).ConfigureAwait(false);
                    StartHeartbeat();
                    Interlocked.Exchange(ref _heartbeatFailures, 0);
                    return;
                }
                catch (Exception exception) when (exception is IOException or TimeoutException or InvalidDataException or RemoteCommandException)
                {
                    lastError = exception;
                    AbortEventRecovery();
                    await _client.DisposeAsync().ConfigureAwait(false);
                    await Task.Delay(delay, connectToken).ConfigureAwait(false);
                    delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 2000));
                }
            }
            throw new TimeoutException("Timed out connecting to the GameFlowStudio Runtime worker.", lastError);
        }
        finally { _connectGate.Release(); }
    }

    private Process StartWorker()
    {
        var executable = _workerExecutablePath ?? Path.Combine(AppContext.BaseDirectory, "Runtime", "GameFlowStudio.Runtime.exe");
        if (!File.Exists(executable))
            throw new FileNotFoundException("Runtime worker executable was not found beside Desktop.", executable);
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--managed");
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(_pipeName);
        startInfo.ArgumentList.Add("--token");
        startInfo.ArgumentList.Add(_token);
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start Runtime worker process.");
        process.EnableRaisingEvents = true;
        process.Exited += OnWorkerExited;
        return process;
    }

    private void OnMessageReceived(ProtocolMessage message)
    {
        if (message.Kind != MessageKind.Event) return;
        RuntimeEventPayload? payload;
        try { payload = message.Payload.Deserialize<RuntimeEventPayload>(ProtocolJson.Options); }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "Unable to parse Runtime event {EventType}", message.Type);
            return;
        }
        if (payload is null || payload.Sequence <= 0 || message.Sequence != payload.Sequence)
        {
            _logger.LogWarning("Discarding malformed Runtime event {EventType} with inconsistent sequence.", message.Type);
            return;
        }

        var publish = false;
        var recover = false;
        lock (_eventSync)
        {
            if (_recovering)
            {
                _eventsDuringRecovery.TryAdd(message.Sequence, message);
                return;
            }
            if (message.Sequence <= _lastEventSequence) return;
            if (message.Sequence != _lastEventSequence + 1)
            {
                _eventsDuringRecovery.TryAdd(message.Sequence, message);
                _recovering = true;
                recover = true;
            }
            else
            {
                _lastEventSequence = message.Sequence;
                publish = true;
            }
        }
        if (publish) RuntimeEventReceived?.Invoke(message);
        if (recover) ScheduleRecovery();
    }

    private void BeginEventRecovery()
    {
        lock (_eventSync)
        {
            _recovering = true;
            _eventsDuringRecovery.Clear();
        }
    }

    private void AbortEventRecovery()
    {
        lock (_eventSync)
        {
            _recovering = false;
            _eventsDuringRecovery.Clear();
        }
    }

    private void ResetEventSequence()
    {
        lock (_eventSync)
        {
            _lastEventSequence = 0;
            _eventsDuringRecovery.Clear();
        }
    }

    private async Task RecoverConnectionStateAsync(CancellationToken cancellationToken)
    {
        await _recoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = await RequestRequiredAsync<RuntimeRunSnapshot>(RuntimeCommands.GetRunSnapshot, null,
                TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            ApplyRecoveredSnapshot(snapshot);

            while (!cancellationToken.IsCancellationRequested)
            {
                long after;
                lock (_eventSync) after = _lastEventSequence;
                var response = await RequestRequiredAsync<RuntimeEventsResponse>(RuntimeCommands.GetEvents,
                    new GetEventsRequest(after), TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                if (response.HistoryTruncated)
                {
                    snapshot = await RequestRequiredAsync<RuntimeRunSnapshot>(RuntimeCommands.GetRunSnapshot, null,
                        TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                    ApplyRecoveredSnapshot(snapshot);
                    continue;
                }

                foreach (var record in response.Events.OrderBy(item => item.Sequence))
                    BufferRecoveredEvent(ProtocolMessage.Event(record.Type, record.Payload, record.Sequence));

                List<ProtocolMessage> pending;
                lock (_eventSync)
                {
                    pending = _eventsDuringRecovery.Values.ToList();
                    _eventsDuringRecovery.Clear();
                }
                foreach (var message in pending.OrderBy(item => item.Sequence))
                    BufferRecoveredEvent(message);

                if (response.Events.Count == 0 && pending.Count == 0)
                {
                    lock (_eventSync)
                    {
                        if (_eventsDuringRecovery.Count == 0)
                        {
                            _recovering = false;
                            return;
                        }
                    }
                }
                else if (response.Events.Count == 0 && pending.Count > 0)
                {
                    // 实时事件序号出现缺口时重新取快照，避免等待已从有界历史中清理的事件。
                    snapshot = await RequestRequiredAsync<RuntimeRunSnapshot>(RuntimeCommands.GetRunSnapshot, null,
                        TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                    ApplyRecoveredSnapshot(snapshot);
                }
            }
        }
        finally { _recoveryGate.Release(); }
    }

    private void ApplyRecoveredSnapshot(RuntimeRunSnapshot snapshot)
    {
        lock (_eventSync)
        {
            _lastEventSequence = snapshot.Sequence;
            foreach (var oldSequence in _eventsDuringRecovery.Keys.Where(sequence => sequence <= snapshot.Sequence).ToArray())
                _eventsDuringRecovery.Remove(oldSequence);
        }
        RunSnapshotRecovered?.Invoke(snapshot);
        StatusRecovered?.Invoke(new RuntimeStatusResponse(snapshot.State, snapshot.RunId, snapshot.CurrentNodeId,
            snapshot.CurrentNodeName, true));
    }

    private void BufferRecoveredEvent(ProtocolMessage message)
    {
        var shouldPublish = false;
        lock (_eventSync)
        {
            if (message.Sequence <= _lastEventSequence) return;
            if (message.Sequence == _lastEventSequence + 1)
            {
                _lastEventSequence = message.Sequence;
                shouldPublish = true;
            }
            else
            {
                _eventsDuringRecovery.TryAdd(message.Sequence, message);
            }
        }
        if (shouldPublish) RuntimeEventReceived?.Invoke(message);
    }

    private void ScheduleRecovery()
    {
        lock (_backgroundSync)
        {
            if (_eventRecoveryTask is { IsCompleted: false } || _client.IsConnected == false) return;
            _eventRecoveryTask = Task.Run(async () =>
            {
                try { await RecoverConnectionStateAsync(_lifetime.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
                catch (Exception exception) { _logger.LogWarning(exception, "Runtime event recovery failed"); AbortEventRecovery(); }
                finally { lock (_backgroundSync) _eventRecoveryTask = null; }
            });
        }
    }

    private void OnDisconnected(Exception? exception)
    {
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _shuttingDown) != 0) return;
        ConnectionLost?.Invoke(exception);
        lock (_backgroundSync)
        {
            _reconnectTask ??= Task.Run(async () =>
            {
                var attempt = 0;
                try
                {
                    while (!_lifetime.IsCancellationRequested && Volatile.Read(ref _shuttingDown) == 0)
                    {
                        try
                        {
                            await EnsureConnectedAsync(_lifetime.Token).ConfigureAwait(false);
                            return;
                        }
                        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
                        catch (Exception reconnectException)
                        {
                            attempt++;
                            _logger.LogWarning(reconnectException, "Runtime reconnect attempt {Attempt} failed", attempt);
                            var delay = TimeSpan.FromMilliseconds(Math.Min(250 * Math.Pow(2, Math.Min(attempt - 1, 5)), 5000));
                            await Task.Delay(delay, _lifetime.Token).ConfigureAwait(false);
                        }
                    }
                }
                finally { lock (_backgroundSync) _reconnectTask = null; }
            });
        }
    }

    private void StartHeartbeat()
    {
        lock (_backgroundSync)
            _heartbeatTask ??= Task.Run(async () =>
            {
                while (!_lifetime.IsCancellationRequested && Volatile.Read(ref _shuttingDown) == 0)
                {
                    try
                    {
                        await Task.Delay(HeartbeatInterval, _lifetime.Token).ConfigureAwait(false);
                        if (!_client.IsConnected) continue;
                        var ping = await _client.RequestAsync<RuntimePingResponse>(RuntimeCommands.Ping, null,
                            HeartbeatTimeout, _lifetime.Token).ConfigureAwait(false);
                        Interlocked.Exchange(ref _heartbeatFailures, 0);
                        if (ping is { } && ping.EventSequence > Interlocked.Read(ref _lastEventSequence))
                            ScheduleRecovery();
                    }
                    catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
                    catch (TimeoutException exception)
                    {
                        var failures = Interlocked.Increment(ref _heartbeatFailures);
                        _logger.LogWarning(exception, "Runtime heartbeat timed out ({FailureCount}/{FailureLimit})", failures, HeartbeatRestartThreshold);
                        if (failures >= HeartbeatRestartThreshold)
                            await RestartUnresponsiveWorkerAsync(exception).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is IOException or ObjectDisposedException)
                    {
                        _logger.LogWarning(exception, "Runtime IPC disconnected during heartbeat");
                        OnDisconnected(exception);
                    }
                    catch (Exception exception)
                    {
                        _logger.LogWarning(exception, "Runtime heartbeat failed");
                    }
                }
            });
    }

    private async Task RestartUnresponsiveWorkerAsync(Exception cause)
    {
        var process = _process;
        if (process is null || process.HasExited) return;
        _logger.LogError(cause, "Runtime stopped answering three consecutive heartbeats; restarting the owned worker.");
        try
        {
            await _client.DisposeAsync().ConfigureAwait(false);
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            _logger.LogWarning(exception, "Unable to stop the unresponsive owned Runtime worker cleanly");
        }
        Interlocked.Exchange(ref _heartbeatFailures, 0);
        OnDisconnected(cause);
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        Task shutdown;
        lock (_shutdownSync) shutdown = _shutdownTask ??= ShutdownCoreAsync();
        return shutdown.WaitAsync(cancellationToken);
    }

    private async Task ShutdownCoreAsync()
    {
        if (Interlocked.Exchange(ref _shuttingDown, 1) != 0) return;
        _lifetime.Cancel();
        Task? heartbeat;
        Task? reconnect;
        Task? eventRecovery;
        lock (_backgroundSync) { heartbeat = _heartbeatTask; reconnect = _reconnectTask; eventRecovery = _eventRecoveryTask; }
        try { await Task.WhenAll(new[] { heartbeat, reconnect, eventRecovery }.Where(task => task is not null).Cast<Task>()).ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        await _connectGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_client.IsConnected)
            {
                using var shutdownTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { _ = await _client.RequestAsync<JsonElement>(RuntimeCommands.Shutdown, null, TimeSpan.FromSeconds(5), shutdownTimeout.Token).ConfigureAwait(false); }
                catch (Exception exception) { _logger.LogDebug(exception, "Runtime shutdown request failed"); }
            }
            if (_process is { HasExited: false } process)
            {
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
                catch (TimeoutException)
                {
                    _logger.LogWarning("Runtime did not stop after the graceful shutdown request; terminating the owned worker.");
                    try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
                    catch (TimeoutException exception) { _logger.LogError(exception, "Owned Runtime process did not exit after termination"); }
                }
            }
            await _client.DisposeAsync().ConfigureAwait(false);
        }
        finally { _connectGate.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        Task dispose;
        lock (_shutdownSync) dispose = _disposeTask ??= DisposeCoreAsync();
        return new ValueTask(dispose);
    }

    private async Task DisposeCoreAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await ShutdownAsync().ConfigureAwait(false); }
        finally
        {
            _client.MessageReceived -= OnMessageReceived;
            _client.Disconnected -= OnDisconnected;
            if (_process is not null)
            {
                _process.Exited -= OnWorkerExited;
                _process.Dispose();
            }
            _connectGate.Dispose();
            _recoveryGate.Dispose();
            _lifetime.Dispose();
        }
    }

    private void OnWorkerExited(object? sender, EventArgs args)
    {
        _ = args;
        if (sender is not Process process || (_process is not null && !ReferenceEquals(process, _process))) return;
        var code = -1;
        try { code = process.ExitCode; } catch { }
        _logger.LogError("Runtime process exited with code {ExitCode}", code);
        var error = new InvalidOperationException($"Runtime process exited with code {code}.");
        ConnectionLost?.Invoke(error);
        OnDisconnected(error);
    }
}
