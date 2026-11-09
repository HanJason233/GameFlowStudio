using System.Diagnostics;
using System.IO;
using GameFlowStudio.Protocol;
using Microsoft.Extensions.Logging;

namespace GameFlowStudio.Desktop.Services;

/// <summary>Owns the UI-free Runtime worker, its private pipe, reconnect and shutdown lifecycle.</summary>
public sealed class RuntimeProcessManager : IAsyncDisposable
{
    private readonly RuntimeClient _client;
    private readonly ILogger<RuntimeProcessManager> _logger;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly object _reconnectSync = new();
    private readonly string _pipeName = $"GameFlowStudio.Runtime.{Environment.ProcessId}.{Guid.NewGuid():N}";
    private readonly string _token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    private readonly CancellationTokenSource _lifetime = new();
    private Process? _process;
    private Task? _reconnectTask;
    private Task? _heartbeatTask;
    private string? _snapshotPath;
    private string? _scriptId;
    private int? _runtimeProcessId;
    private bool _disposed;
    private bool _shuttingDown;

    public RuntimeProcessManager(RuntimeClient client, ILogger<RuntimeProcessManager> logger)
    {
        _client = client;
        _logger = logger;
        _client.MessageReceived += OnMessageReceived;
        _client.Disconnected += OnDisconnected;
    }

    public bool IsConnected => _client.IsConnected;
    public event Action<ProtocolMessage>? RuntimeEventReceived;
    public event Action<RuntimeStatusResponse>? StatusRecovered;
    public event Action<Exception?>? ConnectionLost;

    public void ForgetProjectSnapshot(string snapshotPath)
    {
        if (string.Equals(_snapshotPath, Path.GetFullPath(snapshotPath), StringComparison.OrdinalIgnoreCase))
            _snapshotPath = null;
    }

    public async Task<LoadProjectResponse> LoadProjectAsync(string snapshotPath, string? scriptId = null, CancellationToken cancellationToken = default)
    {
        _snapshotPath = Path.GetFullPath(snapshotPath);
        _scriptId = scriptId;
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        return await RequestRequiredAsync<LoadProjectResponse>(RuntimeCommands.LoadProject,
            new LoadProjectRequest(_snapshotPath, _scriptId), cancellationToken).ConfigureAwait(false);
    }

    public async Task<ValidateResponse> ValidateAsync(string? scriptId = null, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        return await RequestRequiredAsync<ValidateResponse>(RuntimeCommands.Validate, new ValidateRequest(scriptId), cancellationToken).ConfigureAwait(false);
    }

    public async Task<RunAcceptedResponse> StartRunAsync(string runId, string? scriptId, CancellationToken cancellationToken = default)
    {
        _scriptId = scriptId ?? _scriptId;
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        return await RequestRequiredAsync<RunAcceptedResponse>(RuntimeCommands.StartRun, new StartRunRequest(runId, scriptId), cancellationToken).ConfigureAwait(false);
    }

    public async Task<RuntimeStatusResponse> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        return await RequestRequiredAsync<RuntimeStatusResponse>(RuntimeCommands.GetStatus, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RuntimeLogInfo>> GetLogsAsync(long afterSequence = 0, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        return await RequestRequiredAsync<IReadOnlyList<RuntimeLogInfo>>(RuntimeCommands.GetLogs, new { afterSequence }, cancellationToken).ConfigureAwait(false);
    }

    public Task PauseRunAsync(CancellationToken cancellationToken = default) => SendLifecycleCommandAsync(RuntimeCommands.PauseRun, cancellationToken);
    public Task ResumeRunAsync(CancellationToken cancellationToken = default) => SendLifecycleCommandAsync(RuntimeCommands.ResumeRun, cancellationToken);
    public Task StopRunAsync(CancellationToken cancellationToken = default) => SendLifecycleCommandAsync(RuntimeCommands.StopRun, cancellationToken);

    private async Task SendLifecycleCommandAsync(string command, CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        _ = await RequestRequiredAsync<RuntimeStatusResponse>(command, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> RequestRequiredAsync<T>(string command, object? payload, CancellationToken cancellationToken)
    {
        var response = await _client.RequestAsync<T>(command, payload, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        return response ?? throw new InvalidDataException($"Runtime returned no data for {command}.");
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_client.IsConnected) return;
        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client.IsConnected) return;
            var changedProcess = _process is null || _process.HasExited;
            if (changedProcess)
            {
                _process?.Dispose();
                _process = StartWorker();
            }
            var process = _process ?? throw new InvalidOperationException("Runtime worker process was not started.");

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            Exception? lastError = null;
            while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
            {
                if (process.HasExited)
                    throw new InvalidOperationException($"Runtime exited during startup (code {process.ExitCode}).");
                try
                {
                    var initialized = await _client.ConnectAsync(_pipeName, _token, cancellationToken).ConfigureAwait(false);
                    if (_runtimeProcessId != initialized.RuntimeProcessId) changedProcess = true;
                    _runtimeProcessId = initialized.RuntimeProcessId;
                    if (changedProcess && !string.IsNullOrWhiteSpace(_snapshotPath))
                        _ = await RequestRequiredAsync<LoadProjectResponse>(RuntimeCommands.LoadProject,
                            new LoadProjectRequest(_snapshotPath, _scriptId), cancellationToken).ConfigureAwait(false);
                    var status = await RequestRequiredAsync<RuntimeStatusResponse>(RuntimeCommands.GetStatus, null, cancellationToken).ConfigureAwait(false);
                    StatusRecovered?.Invoke(status);
                    StartHeartbeat();
                    return;
                }
                catch (Exception exception) when (exception is IOException or TimeoutException or InvalidDataException)
                {
                    lastError = exception;
                    await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                }
            }
            throw new TimeoutException("Timed out connecting to the GameFlowStudio Runtime worker.", lastError);
        }
        finally { _connectGate.Release(); }
    }

    private Process StartWorker()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "Runtime", "GameFlowStudio.Runtime.exe");
        if (!File.Exists(executable))
            throw new FileNotFoundException("找不到独立 Runtime 程序。请确认 Runtime 与 Desktop 一起构建或发布。", executable);
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(_pipeName);
        startInfo.ArgumentList.Add("--token");
        startInfo.ArgumentList.Add(_token);
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 Runtime 执行进程。");
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            var code = -1;
            try { code = process.ExitCode; } catch { }
            _logger.LogError("Runtime process exited with code {ExitCode}", code);
            ConnectionLost?.Invoke(new InvalidOperationException($"Runtime 进程已退出，代码 {code}。"));
        };
        return process;
    }

    private void OnMessageReceived(ProtocolMessage message)
    {
        if (message.Kind == MessageKind.Event) RuntimeEventReceived?.Invoke(message);
    }

    private void OnDisconnected(Exception? exception)
    {
        if (_disposed || _shuttingDown) return;
        ConnectionLost?.Invoke(exception);
        lock (_reconnectSync)
            _reconnectTask ??= Task.Run(async () =>
            {
                try
                {
                    while (!_lifetime.IsCancellationRequested)
                    {
                        try
                        {
                            await EnsureConnectedAsync(_lifetime.Token).ConfigureAwait(false);
                            return;
                        }
                        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
                        catch (Exception reconnectException)
                        {
                            _logger.LogWarning(reconnectException, "Runtime reconnect failed; retrying");
                            await Task.Delay(TimeSpan.FromSeconds(2), _lifetime.Token).ConfigureAwait(false);
                        }
                    }
                }
                finally { lock (_reconnectSync) _reconnectTask = null; }
            });
    }

    private void StartHeartbeat()
    {
        lock (_reconnectSync)
            _heartbeatTask ??= Task.Run(async () =>
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), _lifetime.Token).ConfigureAwait(false);
                        if (!_client.IsConnected) continue;
                        _ = await _client.RequestAsync<object>(RuntimeCommands.Ping, null, TimeSpan.FromSeconds(3), _lifetime.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
                    catch (Exception exception)
                    {
                        _logger.LogWarning(exception, "Runtime heartbeat failed");
                        await _client.DisposeAsync().ConfigureAwait(false);
                        OnDisconnected(exception);
                    }
                }
            });
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        _shuttingDown = true;
        if (_client.IsConnected)
        {
            try { _ = await _client.RequestAsync<object>(RuntimeCommands.Shutdown, null, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) { _logger.LogDebug(exception, "Runtime shutdown request failed"); }
        }
        if (_process is { HasExited: false } process)
        {
            try { await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                _logger.LogWarning("Runtime did not stop after the graceful shutdown request; terminating the owned worker.");
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            }
            catch (OperationCanceledException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        Task? heartbeat;
        Task? reconnect;
        lock (_reconnectSync) { heartbeat = _heartbeatTask; reconnect = _reconnectTask; }
        try { await Task.WhenAll(new[] { heartbeat, reconnect }.Where(task => task is not null).Cast<Task>()).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        await ShutdownAsync().ConfigureAwait(false);
        await _client.DisposeAsync().ConfigureAwait(false);
        _process?.Dispose();
        _connectGate.Dispose();
        _lifetime.Dispose();
    }
}
