# GameFlow Studio

GameFlow Studio 是一个 Windows 无代码键盘鼠标自动化 MVP。桌面外壳使用 .NET 10 + WPF，流程画布使用 WebView2 承载 React + TypeScript + React Flow；流程验证、执行、Windows API 自动化、项目存储和日志均在 C# 进程内完成，运行时不需要 Node.js 服务。

## 当前能力

- 脚本内逻辑判断：色检测、图检测；OCR/AI 识别保留 TODO。检测器只属于当前脚本，逻辑节点到达时检测并通过 `true/false` 分支继续执行。

- 十八种原子动作：开始、点击、鼠标拖动、鼠标滑动、键盘按键、输入文本、等待、色判断、图判断、联合判断、输出日志、启动程序、关闭程序、设置窗口大小、最小化窗口、窗口整理、设置窗口分辨率、结束。启用脚本窗口作用域后，针对窗口的操作会自动激活对应窗口；匹配多个窗口时支持只操作第一个、窗口优先和动作优先三种执行方式。
- React Flow 节点添加、拖动、连接、选择、Delete 删除、Ctrl+C/Ctrl+V 复制粘贴、缩放和平移。
- WPF 属性面板、坐标拾取、项目文件模块中的新建/打开/安全保存/另存为、单项目多脚本、示例项目、最近项目记录和主题/快捷键设置。
- 顺序执行与联合判断分支；类型化变量与 `${variableName}` 日志插值。
- 异步暂停、恢复、停止、防重复运行、10,000 节点安全上限、取消传播和节点级异常状态。
- F8 运行、F9 暂停/继续、F10 停止；可在设置中改为其他 F1–F24 键。
- 节点运行/成功/失败状态同步到画布，底部实时日志及 `%LOCALAPPDATA%\GameFlowStudio\logs\yyyy-MM-dd.log` 文件日志。
- 运行前图验证：开始/结束节点、孤立和不可达节点、句柄、参数、坐标、分支出口和非法环路。

> 自动化节点会真实控制当前 Windows 桌面。运行示例前请确认鼠标移动、点击和按键不会影响未保存的数据。

## 环境

本项目创建和验证时使用：

- .NET SDK 10.0.302，目标框架 `net10.0-windows`
- Node.js 24.18.0 LTS
- React 19.2.7
- `@xyflow/react` 12.11.2
- WebView2 SDK 1.0.4078.44；本机需要安装 Microsoft Edge WebView2 Runtime
- x64 Windows 10/11

## 首次构建

PowerShell 的执行策略可能阻止 `npm.ps1`，此时使用下列 `npm.cmd`；普通命令提示符可直接使用 `npm`。

```powershell
cd C:\auto\GameFlowStudio\src\GameFlowStudio.Editor
npm.cmd install
npm.cmd run lint
npm.cmd run build

cd C:\auto\GameFlowStudio
dotnet restore GameFlowStudio.sln
dotnet build GameFlowStudio.sln -c Release
```

Vite 会直接将资源生成到 `src\GameFlowStudio.Desktop\EditorAssets`。WPF 构建会把它们复制到应用输出目录，不会启动本地 Web 服务器。

## 启动和测试

```powershell
cd C:\auto\GameFlowStudio
dotnet run --project src\GameFlowStudio.Desktop\GameFlowStudio.Desktop.csproj
dotnet test GameFlowStudio.sln -c Release
```

首次启动可点击“打开示例项目”。创建新项目时会得到一个已连接的开始和结束节点；从节点编辑区下方的步骤面板添加节点，重新连接后即可保存和运行。

## 目录结构

```text
GameFlowStudio/
├── GameFlowStudio.sln
├── src/
│   ├── GameFlowStudio.Contracts/       JSON 模型、枚举、公共接口和事件
│   ├── GameFlowStudio.Workflow/        验证、执行引擎、同步门和节点执行器
│   ├── GameFlowStudio.Automation/      SendInput、窗口、快捷键、坐标与集中 P/Invoke
│   ├── GameFlowStudio.Storage/         项目安全写入、本地设置和路径策略
│   ├── GameFlowStudio.Infrastructure/  DI 注册与 UI/文件日志提供程序
│   ├── GameFlowStudio.Desktop/         WPF MVVM、WebView2、属性面板和坐标选择器
│   └── GameFlowStudio.Editor/          React/TypeScript/React Flow 编辑器
├── tests/
│   ├── GameFlowStudio.Workflow.Tests/
│   └── GameFlowStudio.Storage.Tests/
├── samples/basic-example.gflow
└── docs/
```

## 执行模型

运行前，C# 从编辑器请求最新项目快照并进行权威验证。运行器从唯一开始节点出发，调用节点类型对应的 `INodeExecutor`，根据结果中的输出句柄选择下一条边。联合判断返回 `true` 或 `false`。每个节点事件经 WPF 转成 WebView2 JSON 消息，从而更新 React 画布状态。

暂停使用异步手动重置事件，不占用线程；停止取消关联 `CancellationTokenSource`。等待、重复点击/按键和文本输入均响应取消。自动化接口可替换，因此测试不会实际发送键鼠输入。

## `.gflow` 保存策略

保存先在目标目录创建临时文件，完整序列化后再反序列化校验；已有文件使用 Windows 原子替换，新文件使用同卷移动。任何失败都会删除临时文件并保留原项目。项目 JSON 协议见 [workflow-schema.md](docs/workflow-schema.md)。

## 已知限制

- 鼠标移动的 `durationMs` 已保留，但 MVP 当前执行瞬时定位。
- 快捷键设置支持 F1–F24，尚未提供带 Ctrl/Shift/Alt 的全局组合键编辑器。
- 浅色主题切换 WPF 主背景；React 编辑器保持适合流程调试的深色画布。
- 未提供 MSI/MSIX、自动更新、工作流并行或崩溃恢复。
- 不包含 OCR、找图、录制、云同步、登录、插件、脚本市场、游戏内存、注入、驱动输入或反作弊能力。

架构细节见 [architecture.md](docs/architecture.md)，消息协议见 [editor-communication.md](docs/editor-communication.md)。
