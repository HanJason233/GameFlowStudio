# GameFlow Studio 架构

## 分层

```text
WPF Desktop ── WebView2 JSON ── React Flow Editor
     │
     ├── Workflow（验证、逻辑判断、执行器、变量、暂停门）
     ├── Automation（SendInput、低级键鼠录制、窗口、坐标、Interop）
     ├── Storage（.gflow 原子保存、settings.json）
     └── Infrastructure（DI、ILogger UI/每日文件输出）
            │
         Contracts（唯一共享模型与接口）
```

Desktop 负责窗口生命周期、文件对话框、运行命令和可视状态，不绘制流程。Editor 负责图形编辑，不执行自动化。Workflow 只依赖 Contracts 中的接口，因此测试可注入假的输入或节点执行器。Automation 是唯一允许调用 `user32.dll` 的项目。

## 防御边界与后端纯度

- React Editor 把 WebView 消息视为不可信输入：先校验消息信封、请求 ID、节点类型、payload 和工作流结构，再更新画布；畸形消息只记录并隔离，渲染异常由前端错误边界提供恢复入口。
- Desktop 是 UI 适配层，负责把异常记录为日志或转换为用户提示。Workflow、Storage 和 Automation 不依赖对话框、Dispatcher 或前端状态。
- 后端仍必须在存储、执行和系统调用边界验证领域不变量；这类校验保证数据正确性，不应下放给前端。领域内部不重复猜测或修补非法输入，也不吞掉业务异常。
- 后端服务不得隐式修改调用方传入的数据。项目保存使用独立快照更新时间并落盘，调用方对象保持不变。

## 屏幕坐标契约

- Automation、Workflow 和项目参数中的屏幕坐标统一使用虚拟桌面物理像素；`CursorPositionService`、`CaptureRegion`、`CapturedFrame` 和鼠标输入必须保持同一坐标系，并允许多显示器负坐标。
- 拾取遮罩 HWND 使用 PMv2 `SetWindowPos` 按物理虚拟桌面边界放置，并在 DPI/显示配置变化后重新应用；WPF Canvas 仍使用 DIP，只能在绘制边界通过 `PointFromScreen` 把物理屏幕像素转换为窗口本地 DIP，不能直接用物理坐标减 `Window.Left`。
- 窗口相对坐标继续以 `GetWindowRect` 的窗口外框左上角为原点，以兼容已有项目；取点和执行必须使用同一原点语义。
- 取色窗口通过 `WDA_EXCLUDEFROMCAPTURE` 排除于现代 Windows 捕获；不支持时仅隐藏与采样区域相交的拾取 UI。颜色确认会在遮罩关闭并完成 DWM 刷新后重采样所选物理像素。模板截图会在主窗口恢复前冻结为 `CapturedFrame`，保存阶段不得再次从桌面捕获。
- React Flow 的画布坐标属于 WebView2/CSS 世界坐标，不与桌面物理像素互换；WebView2 页面缩放固定为 100%，系统 DPI 仍由 WPF/WebView2 自身处理。

## 生命周期和数据流

1. WPF Host 创建服务容器、日志提供程序、MainViewModel 和主窗口。
2. WebView2 将 `EditorAssets` 映射为 `https://app.gameflow` 并加载本地 `index.html`。
3. React 发出 `editor_ready`；C# 发送 `initialize_editor`，此前消息按 FIFO 缓存。
4. 保存或运行前，C# 发出带 `requestId` 的 `request_workflow_export`，等待相同 ID 的 `workflow_exported`。
5. 运行器验证快照，逐节点执行并发出 NodeStarted/NodeCompleted/NodeFailed。
6. ViewModel 把事件映射为画布高亮/成功/失败消息，同时 ILogger 写入 UI 和每日文件。

录制是独立于运行器的状态：Desktop 先导出并锁定当前画布，Automation 在当前交互桌面安装 `WH_MOUSE_LL`/`WH_KEYBOARD_LL`，过滤本进程窗口和注入输入，再把消息归并为操作快照。停止后 Workflow 将快照转换为只包含现有节点类型的新线性脚本，Desktop 通过既有 `import_workflow` 消息显示它。F7 是录制期间保留的全局停止键。

## 执行状态

`Idle → Running → Paused → Running → Completed` 是正常路径。停止时进入 `Stopping` 并取消关联令牌，清理后回到 `Idle`。验证或执行异常进入 `Failed`。只有非 Running/Paused/Stopping 状态可以开始新任务。

异步暂停门在节点边界和长等待切片处检查；停止会同时打开暂停门并取消令牌，避免暂停状态无法退出。最大节点计数为最后一道安全保护。

## 扩展点

- 新节点：新增稳定 NodeType、参数验证、`INodeExecutor` 和 React 默认参数/显示信息。
- 新输入方式：替换 `IInputAutomationService`，不修改运行器。
- 新录制后端：替换 `IInputRecordingService`；录制快照与工作流转换不依赖 WPF 或原生钩子。
- 项目文件使用 1.4 结构，每个脚本独立保存检测器和节点图；逻辑判断只在当前脚本内执行，编辑器通过活动脚本视图与 React 编辑器通信。
- 日志后端：增加 ILoggerProvider，不改变执行器。
