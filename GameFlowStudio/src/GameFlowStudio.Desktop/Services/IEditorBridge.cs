using System.Text.Json;
using Microsoft.Web.WebView2.Wpf;

namespace GameFlowStudio.Desktop.Services;

public sealed class EditorMessage(string type, string? requestId, JsonElement payload) : EventArgs
{
    public string Type { get; } = type;
    public string? RequestId { get; } = requestId;
    public JsonElement Payload { get; } = payload;
}
public interface IEditorBridge
{
    bool IsReady { get; }
    event EventHandler<EditorMessage>? MessageReceived;
    Task InitializeAsync(WebView2 webView, CancellationToken cancellationToken = default);
    Task SendAsync(string type, object? payload = null, string? requestId = null);
    Task<T> RequestAsync<T>(string type, object? payload = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default);
}
