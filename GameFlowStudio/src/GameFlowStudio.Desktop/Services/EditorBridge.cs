using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace GameFlowStudio.Desktop.Services;

public sealed class EditorBridge(ILogger<EditorBridge> logger) : IEditorBridge
{
    private readonly ConcurrentQueue<string> _queue = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new(StringComparer.Ordinal);
    private WebView2? _webView;
    public bool IsReady { get; private set; }
    public event EventHandler<EditorMessage>? MessageReceived;

    public async Task InitializeAsync(WebView2 webView, CancellationToken cancellationToken = default)
    {
        _webView = webView;
        await webView.EnsureCoreWebView2Async();
        webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
        webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        var assets = Path.Combine(AppContext.BaseDirectory, "EditorAssets");
        var index = Path.Combine(assets, "index.html");
        if (!File.Exists(index)) throw new FileNotFoundException("找不到 React 编辑器资源。请先运行 npm run build。", index);
        webView.CoreWebView2.SetVirtualHostNameToFolderMapping("app.gameflow", assets, CoreWebView2HostResourceAccessKind.DenyCors);
        webView.Source = new("https://app.gameflow/index.html");
    }

    public async Task SendAsync(string type, object? payload = null, string? requestId = null)
    {
        var json = JsonSerializer.Serialize(new { type, requestId, payload }, Contracts.Workflows.ProjectJson.Options);
        if (!IsReady || _webView?.CoreWebView2 is null) { _queue.Enqueue(json); return; }
        await _webView.Dispatcher.InvokeAsync(() => _webView.CoreWebView2.PostWebMessageAsJson(json));
    }

    public async Task<T> RequestAsync<T>(string type, object? payload = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var source = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(requestId, source)) throw new InvalidOperationException("无法创建编辑器请求。");
        try
        {
            await SendAsync(type, payload, requestId);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));
            var element = await source.Task.WaitAsync(timeoutCts.Token);
            return element.Deserialize<T>(Contracts.Workflows.ProjectJson.Options) ?? throw new InvalidDataException("编辑器返回了空响应。");
        }
        finally { _pending.TryRemove(requestId, out _); }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var message = ParseMessage(e.WebMessageAsJson);
            CompletePendingRequest(message);
            if (message.Type == "editor_ready") MarkReadyAndFlushQueue();
            MessageReceived?.Invoke(this, message);
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
        IsReady = true;
        while (_queue.TryDequeue(out var message)) _webView?.CoreWebView2.PostWebMessageAsJson(message);
    }
}
