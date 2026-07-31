# GameFlow Studio

GameFlow Studio 是一个 Windows 无代码键盘鼠标自动化 MVP。桌面外壳使用 .NET 10 + WPF，流程画布使用 WebView2 承载 React + TypeScript + React Flow；流程验证、执行、Windows API 自动化、项目存储和日志均在 C# 进程内完成，运行时不需要 Node.js 服务。

## 当前能力

- 脚本内逻辑判断：色检测、图检测、Gemini OCR 文本检测；OCR 默认通过 Gemini 云端接口识别截图，模型为 `gemini-flash-lite-latest`；AI 识别仍保留 TODO。检测器只属于当前脚本，逻辑节点到达时检测并通过 `true/false` 分支继续执行。

- 十六种操作卡片：开始、点击、鼠标拖动、鼠标滑动、键盘按键、输入文本、等待、色判断、图判断、文本判断、启动程序、关闭程序、最小化窗口、窗口整理、设置窗口分辨率、结束。每个操作卡片执行成功或失败都会自动写入运行日志。窗口整理始终按卡片标题正则处理整组窗口；后台关闭、最小化和设置分辨率在存在唯一脚本窗口上下文时只处理当前窗口，否则按卡片标题正则处理匹配窗口。后台并行时，启动程序和窗口整理这类全局动作由所有窗口轨道共享一次执行。
- 操作录制：点击工具栏“录制”后，先选择基于整个屏幕或指定窗口，再捕获单击、拖动、滚轮和可回放键盘组合；窗口模式复用坐标拾取的窗口选择并只记录目标窗口，自动将当前脚本的选择作用域设置为该窗口，鼠标坐标保存为窗口相对坐标。按 F7 或点击“停止录制”后，带“录制开始/录制结束”标记的步骤会追加到当前脚本，不再新建脚本；单次最多录制 4,000 个操作，达到上限时自动停止。
- 脚本属性可配置窗口匹配、前后台执行方式及操作流程。窗口匹配先用“正则匹配”规定可操作窗口，再用“排除正则匹配”从结果中移除不需要的目标。前台可选择“伪并行”，依次切换窗口并让各窗口的操作进度保持一致，也可选择逐窗口完整执行的“串行”；后台固定使用各窗口同时执行的“并行”。后台方式会让点击、移动、拖动、滚轮、按键、文本输入、色判断、图判断、文本判断以及可后台执行的窗口动作都不激活目标窗口；后台键鼠通过传统 Win32 消息投递，后台读取通过 `PrintWindow` 请求唯一目标窗口绘制。游戏、硬件加速窗口、高权限或受保护程序可能忽略消息或返回黑图。
- React Flow 节点添加、拖动、连接、选择、Delete 删除、Ctrl+C/Ctrl+V 复制粘贴、缩放和平移；“整理”会按连接层级和分支重心生成紧凑的多行布局。
- WPF 属性面板、坐标拾取、项目文件模块中的新建/打开/安全保存/另存为、单项目多脚本、示例项目和主题设置。
- 顺序执行与色判断、图判断、Gemini OCR 判断分支；类型化变量和脚本级窗口匹配。
- 异步暂停、恢复、停止、防重复运行、10,000 节点安全上限、取消传播和节点级异常状态。
- F8 运行、F9 暂停/继续、F10 停止；可在设置中改为其他 F1–F24 键。
- 编辑完成后可点击“悬浮运行”：尚未保存或有修改的项目会先要求保存，再导出到临时文件；编辑器进程退出并释放 WebView2 内存，随后启动只包含脚本列表、进度和运行开关的轻量悬浮窗。从悬浮窗切回的临时快照按未保存项目打开，首次保存会要求选择正式路径。
- 节点运行/成功/失败状态同步到画布，日志面板默认显示底部实时日志；检测动作会记录识别文本、颜色值、图像置信度和实际坐标，并写入 `%LOCALAPPDATA%\GameFlowStudio\logs\yyyy-MM-dd.log` 文件。
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
npm.cmd run test
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

## 发布 Windows 安装包

项目使用 WiX Toolset 生成包含 .NET 运行时的 x64 MSI，构建机只需要当前项目要求的 .NET SDK 和 Node.js：

```powershell
cd C:\auto\GameFlowStudio
powershell -NoProfile -ExecutionPolicy Bypass -File .\build\Publish-Installer.ps1
```

脚本会重新安装锁定版本的前端依赖，执行 lint、前端构建和全部 .NET 测试，然后将自包含应用发布到 `artifacts\publish\win-x64`，最终安装包及 SHA-256 校验文件输出到 `artifacts\installer`。可通过 `-Version 0.2.0` 显式指定三段式版本号；未指定时使用项目的 `VersionPrefix`。

本机测试可以生成自签名代码证书，并仅在当前用户证书存储区信任它：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build\Publish-Installer.ps1 -TestSign -TrustTestCertificate
```

测试证书的公钥会输出到 `artifacts\installer\GameFlowStudio-TestPublisher.cer`，私钥只保存在生成证书的当前 Windows 用户证书存储区，不会写入项目或提交到 Git。自签名证书不具备公共信誉，其他电脑需要先显式信任该测试证书；正式分发必须改用受信任 CA 签发的代码签名证书。

在另一台测试机上信任证书时，以该测试用户运行：

```powershell
Import-Certificate -FilePath .\GameFlowStudio-TestPublisher.cer -CertStoreLocation Cert:\CurrentUser\Root
Import-Certificate -FilePath .\GameFlowStudio-TestPublisher.cer -CertStoreLocation Cert:\CurrentUser\TrustedPublisher
```

## Gemini OCR 配置

桌面端默认使用 `https://gemini.11145149.xyz/` 的 Gemini `generateContent` 接口和 `gemini-flash-lite-latest` 模型，默认提示词为“你当前是ocr识别助手，只输出识别到的文字，字母，数字，符号等”。API 密钥可在“全局设置 → Gemini OCR API 密钥”中保存到本机设置；留空时也可以使用 `GEMINI_API_KEY` 环境变量。也可以使用 `GeminiOcr__ApiBaseUrl`、`GeminiOcr__EndpointPath`、`GeminiOcr__Model`、`GeminiOcr__Prompt` 和 `GeminiOcr__TimeoutSeconds` 覆盖默认配置。

## 目录结构

```text
GameFlowStudio/
├── GameFlowStudio.sln
├── src/
│   ├── GameFlowStudio.Contracts/       JSON 模型、枚举、公共接口和事件
│   ├── GameFlowStudio.Workflow/        验证、执行引擎、同步门和节点执行器
│   ├── GameFlowStudio.Automation/      SendInput、窗口、坐标与集中 P/Invoke
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

运行前，C# 从编辑器请求最新项目快照并进行权威验证。运行器从唯一开始节点出发，调用节点类型对应的 `INodeExecutor`，根据结果中的输出句柄选择下一条边。色判断、图判断和 Gemini OCR 判断返回 `true` 或 `false`。编辑器内运行时，每个节点事件经 WPF 转成 WebView2 JSON 消息；悬浮纯运行模式不加载 WebView2，只在轻量窗口更新脚本进度。

暂停使用异步手动重置事件，不占用线程；停止取消关联 `CancellationTokenSource`。等待、重复点击/按键和文本输入均响应取消。自动化接口可替换，因此测试不会实际发送键鼠输入。

## `.gflow` 保存策略

保存先在目标目录创建临时文件，完整序列化后再反序列化校验；已有文件使用 Windows 原子替换，新文件使用同卷移动。任何失败都会删除临时文件并保留原项目。项目 JSON 协议见 [workflow-schema.md](docs/workflow-schema.md)。

## 已知限制

- 鼠标移动的 `durationMs` 已保留，但 MVP 当前执行瞬时定位。
- 浅色主题切换 WPF 主背景；React 编辑器保持适合流程调试的深色画布。
- 未提供自动更新或崩溃恢复。
- 操作录制不采集连续鼠标移动轨迹、触摸、手柄或 IME 文本提交；低级键盘录制只生成当前按键执行器支持的按键。锁屏、UAC 安全桌面和部分使用受保护输入的游戏可能无法捕获。
- 录制步骤按节点顺序回放，当前不能精确还原“按住一个键的同时点击鼠标”等重叠输入，也不记录单独按下的 Ctrl、Shift 或 Alt。
- 不包含云同步、登录、插件、脚本市场、游戏内存、注入、驱动输入或反作弊能力。

架构细节见 [architecture.md](docs/architecture.md)，消息协议见 [editor-communication.md](docs/editor-communication.md)。
