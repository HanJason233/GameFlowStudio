# WPF 与 React 编辑器通信

双方只通过 WebView2 JSON 消息通信：

```json
{ "type": "message_type", "requestId": "optional", "payload": {} }
```

请求响应使用相同 `requestId`。C# 为每个待处理请求保存 `TaskCompletionSource<JsonElement>` 并设置十秒默认超时。编辑器发出 `editor_ready` 前，C# 发送消息会进入 FIFO 队列。无法解析的消息写入日志，不执行动态 JavaScript。

## React → WPF

- `editor_ready`：编辑器已安装消息监听器。
- `workflow_changed`：画布被修改，用于未保存状态。
- `node_selected`：单选节点及其 data；无单选时 payload 为 null。
- `workflow_exported`：最新项目快照，响应导出请求。
- `validation_requested`：保留给编辑器主动验证。
- `coordinate_picker_requested`：保留给 React 内属性 UI。

## WPF → React

- `initialize_editor`、`import_workflow`：载入完整项目。
- `request_workflow_export`：请求最新快照。
- `add_node`：按 nodeType 在视口中心创建节点。
- `update_node_data`：按 nodeId 合并名称、enabled 和 parameters。
- `highlight_node`、`mark_node_success`、`mark_node_error`、`clear_execution_state`：执行状态。
- `set_editor_readonly`：运行期间禁止结构编辑，仍允许选择、缩放和平移。

C# 使用 `CoreWebView2.PostWebMessageAsJson` 与 `CoreWebView2.WebMessageReceived`；React 使用 `window.chrome.webview.postMessage` 和 message 事件。协议不使用 `ExecuteScriptAsync`，也不拼接不可信脚本。
