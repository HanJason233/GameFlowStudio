using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace GameFlowStudio.Desktop.Services;

public sealed class EditorBridge(ILogger<EditorBridge> logger) : IEditorBridge, IDisposable
{
    private readonly object _stateSync = new();
    private readonly Queue<string> _queue = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new(StringComparer.Ordinal);
    private WebView2? _webView;
    private CoreWebView2? _subscribedCore;
    private bool _isReady;
    private bool _disposed;

    public bool IsReady
    {
        get
        {
            lock (_stateSync)
                return _isReady && !_disposed;
        }
    }

    public event EventHandler<EditorMessage>? MessageReceived;

    public async Task InitializeAsync(WebView2 webView, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(webView);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_stateSync)
        {
            ThrowIfDisposed();
            if (_webView is not null) throw new InvalidOperationException("编辑器桥接已经初始化。");
            _webView = webView;
        }

        CoreWebView2? core = null;
        try
        {
            await webView.EnsureCoreWebView2Async();
            cancellationToken.ThrowIfCancellationRequested();
            core = webView.CoreWebView2 ?? throw new InvalidOperationException("WebView2 初始化失败。");

            var assets = Path.Combine(AppContext.BaseDirectory, "EditorAssets");
            var index = Path.Combine(assets, "index.html");
            if (!File.Exists(index)) throw new FileNotFoundException("找不到 React 编辑器资源。请先运行 npm run build。", index);
            cancellationToken.ThrowIfCancellationRequested();

            lock (_stateSync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                if (!ReferenceEquals(_webView, webView)) throw new InvalidOperationException("编辑器桥接初始化状态已改变。");
                core.Settings.AreDevToolsEnabled = true;
                core.WebMessageReceived += OnWebMessageReceived;
                _subscribedCore = core;
                core.SetVirtualHostNameToFolderMapping("app.gameflow", assets, CoreWebView2HostResourceAccessKind.DenyCors);
                webView.Source = new("https://app.gameflow/index.html");
            }
        }
        catch
        {
            ResetFailedInitialization(webView, core);
            throw;
        }
    }

    public async Task SendAsync(string type, object? payload = null, string? requestId = null)
    {
        lock (_stateSync)
            ThrowIfDisposed();

        var json = JsonSerializer.Serialize(new { type, requestId, payload }, Contracts.Workflows.ProjectJson.Options);
        WebView2 webView;
        lock (_stateSync)
        {
            ThrowIfDisposed();
            if (!_isReady || _webView is null)
            {
                _queue.Enqueue(json);
                return;
            }

            webView = _webView;
        }

        await webView.Dispatcher.InvokeAsync(() =>
        {
            lock (_stateSync)
            {
                ThrowIfDisposed();
                if (!_isReady || !ReferenceEquals(_webView, webView) || webView.CoreWebView2 is null)
                    throw new InvalidOperationException("编辑器尚未就绪。");
                webView.CoreWebView2.PostWebMessageAsJson(json);
            }
        });
    }

    public async Task<T> RequestAsync<T>(string type, object? payload = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requestId = Guid.NewGuid().ToString("N");
        var source = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_stateSync)
        {
            ThrowIfDisposed();
            if (!_pending.TryAdd(requestId, source)) throw new InvalidOperationException("无法创建编辑器请求。");
        }

        try
        {
            await SendAsync(type, payload, requestId);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));
            var element = await source.Task.WaitAsync(timeoutCts.Token);
            return element.Deserialize<T>(Contracts.Workflows.ProjectJson.Options) ?? throw new InvalidDataException("编辑器返回了空响应。");
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
            if (source.Task.IsFaulted) _ = source.Task.Exception;
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            lock (_stateSync)
            {
                if (_disposed) return;
            }

            var message = ParseMessage(e.WebMessageAsJson);
            CompletePendingRequest(message);
            if (message.Type == "editor_ready") MarkReadyAndFlushQueue();
            EventHandler<EditorMessage>? handler;
            lock (_stateSync)
            {
                if (_disposed) return;
                handler = MessageReceived;
            }
            handler?.Invoke(this, message);
        }
        catch (Exception ex) { logger.LogError(ex, "无法解析编辑器消息"); }
    }

    private static EditorMessage ParseMessage(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var type = root.GetProperty("type").GetString() ?? string.Empty;
        var requestId = root.TryGetProperty("requestId", out var idElement) && idElement.ValueKind == JsonValueKind.String ? idElement.GetString() : null;
        var payload = root.TryGetProperty("payload", out var payloadElement) ? payloadElement.Clone() : default;
        return new(type, requestId, payload);
    }

    private void CompletePendingRequest(EditorMessage message)
    {
        if (message.RequestId is not null && _pending.TryGetValue(message.RequestId, out var pending)) pending.TrySetResult(message.Payload);
    }

    private void MarkReadyAndFlushQueue()
    {
        lock (_stateSync)
        {
            if (_disposed) return;
            var core = _webView?.CoreWebView2;
            if (core is null) return;

            _isReady = true;
            while (_queue.Count > 0)
                core.PostWebMessageAsJson(_queue.Dequeue());
        }
    }

    public void Dispose()
    {
        CoreWebView2? subscribedCore;
        TaskCompletionSource<JsonElement>[] pending;
        lock (_stateSync)
        {
            if (_disposed) return;
            _disposed = true;
            _isReady = false;
            _queue.Clear();
            subscribedCore = _subscribedCore;
            _subscribedCore = null;
            _webView = null;
            pending = _pending.Values.ToArray();
            _pending.Clear();
            MessageReceived = null;

            foreach (var request in pending)
                request.TrySetException(new ObjectDisposedException(nameof(EditorBridge)));
        }

        if (subscribedCore is not null)
        {
            try { subscribedCore.WebMessageReceived -= OnWebMessageReceived; }
            catch (Exception exception) { logger.LogWarning(exception, "释放编辑器消息订阅失败"); }
        }

        GC.SuppressFinalize(this);
    }

    private void ResetFailedInitialization(WebView2 webView, CoreWebView2? core)
    {
        var shouldUnsubscribe = false;
        lock (_stateSync)
        {
            if (_disposed || !ReferenceEquals(_webView, webView)) return;
            _isReady = false;
            _webView = null;
            if (core is not null && ReferenceEquals(_subscribedCore, core))
            {
                _subscribedCore = null;
                shouldUnsubscribe = true;
            }
        }

        if (shouldUnsubscribe)
        {
            try { core!.WebMessageReceived -= OnWebMessageReceived; }
            catch (Exception exception) { logger.LogWarning(exception, "回滚编辑器消息订阅失败"); }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
