# GameFlow Studio 架构

## 分层

```text
WPF Desktop ── WebView2 JSON ── React Flow Editor
     │
     ├── Workflow（验证、逻辑判断、执行器、变量、暂停门）
     ├── Automation（SendInput、窗口、快捷键、坐标、Interop）
     ├── Storage（.gflow 原子保存、settings.json）
     └── Infrastructure（DI、ILogger UI/每日文件输出）
            │
         Contracts（唯一共享模型与接口）
```

Desktop 负责窗口生命周期、文件对话框、运行命令和可视状态，不绘制流程。Editor 负责图形编辑，不执行自动化。Workflow 只依赖 Contracts 中的接口，因此测试可注入假的输入或节点执行器。Automation 是唯一允许调用 `user32.dll` 的项目。

## 生命周期和数据流

1. WPF Host 创建服务容器、日志提供程序、MainViewModel 和主窗口。
2. WebView2 将 `EditorAssets` 映射为 `https://app.gameflow` 并加载本地 `index.html`。
3. React 发出 `editor_ready`；C# 发送 `initialize_editor`，此前消息按 FIFO 缓存。
4. 保存或运行前，C# 发出带 `requestId` 的 `request_workflow_export`，等待相同 ID 的 `workflow_exported`。
5. 运行器验证快照，逐节点执行并发出 NodeStarted/NodeCompleted/NodeFailed。
6. ViewModel 把事件映射为画布高亮/成功/失败消息，同时 ILogger 写入 UI 和每日文件。

## 执行状态

`Idle → Running → Paused → Running → Completed` 是正常路径。停止时进入 `Stopping` 并取消关联令牌，清理后回到 `Idle`。验证或执行异常进入 `Failed`。只有非 Running/Paused/Stopping 状态可以开始新任务。

异步暂停门在节点边界和长等待切片处检查；停止会同时打开暂停门并取消令牌，避免暂停状态无法退出。最大节点计数为最后一道安全保护。

## 扩展点

- 新节点：新增稳定 NodeType、参数验证、`INodeExecutor` 和 React 默认参数/显示信息。
- 新输入方式：替换 `IInputAutomationService`，不修改运行器。
- 项目文件使用 1.3 结构，每个脚本独立保存检测器和节点图；逻辑判断只在当前脚本内执行，编辑器通过活动脚本兼容视图与 React 编辑器通信。
- 日志后端：增加 ILoggerProvider，不改变执行器。
