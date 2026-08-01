<div align="center">

# GameFlow Studio

### Windows 可视化脚本 · 无代码桌面自动化 · 键鼠录制

用流程图搭建自动化脚本，把键盘、鼠标、窗口和图像识别组合成可复用的 Windows 桌面自动化流程。

[立即下载 v0.1.5](https://github.com/HanJason233/GameFlowStudio/releases/tag/v0.1.5) · [查看全部版本](https://github.com/HanJason233/GameFlowStudio/releases) · [浏览文档](docs/architecture.md)

**Windows 10/11 · x64 · 可视化编排 · 不需要编写代码**

</div>

GameFlow Studio 是一款面向中国用户的 **Windows 可视化脚本工具** 和 **无代码桌面自动化工具**。通过拖拽节点、连接流程和填写参数，即可制作重复点击、表单填写、多窗口操作、键鼠回放和视觉判断脚本；运行时不需要 Node.js 服务。

> 自动化脚本会真实控制当前 Windows 桌面。运行流程前，请确认鼠标、键盘和窗口操作不会影响未保存的数据。

## 30 秒开始制作可视化脚本

1. 下载并运行 [Windows x64 MSI 安装包](https://github.com/HanJason233/GameFlowStudio/releases/download/v0.1.5/GameFlowStudio-0.1.5-win-x64.msi)。
2. 打开示例项目，或新建一个带有“开始”和“结束”节点的自动化流程。
3. 从步骤面板添加节点、拖动连线，按 `F8` 运行，按 `F9` 暂停/继续，按 `F10` 停止。

安装包自带 .NET 运行时；运行电脑需要 Microsoft Edge WebView2 Runtime。

## 适合哪些自动化场景？

| 使用场景 | 可视化脚本能力 |
| --- | --- |
| 办公自动化 / RPA | 重复点击、文本输入、键盘操作、等待和流程分支 |
| 网页或桌面软件操作 | 窗口匹配、鼠标拖动、滚轮、程序启动与关闭 |
| 多窗口批量处理 | 标题正则匹配、排除规则、窗口整理、前后台执行 |
| 键鼠操作回放 | 屏幕/窗口录制，自动生成可编辑的自动化步骤 |
| 需要识别后再操作 | 色判断、图判断、OCR 文本判断和 `true/false` 分支 |
| 可维护的自动化项目 | `.gflow` 项目文件、流程验证、运行日志和节点状态 |

## 以用户体验为中心的可视化工作台

整个界面围绕“编辑脚本 → 检查流程 → 运行自动化”设计：

- **流程画布**：像画流程图一样拖拽节点、连接分支、缩放和平移；“整理”自动生成紧凑布局。
- **步骤面板**：按类别选择操作，不需要记忆脚本语法。
- **属性面板**：直接填写文本、坐标、延迟、窗口规则、变量和执行模式。
- **录制工具**：选择整个屏幕或指定窗口，捕获点击、拖动、滚轮和可回放的键盘组合。
- **运行反馈**：节点实时显示等待、成功和失败状态，日志面板同步展示执行过程。
- **悬浮运行**：编辑完成后切换到轻量运行窗口，只保留脚本列表、进度和运行控制。

## 核心功能

### 可视化流程编排

- 基于 React Flow 的流程图编辑器，支持添加、拖动、连接、选择、删除、缩放和平移。
- 支持条件分支、自动布局、流程校验和最多 10,000 个节点的安全上限。
- 节点运行状态直接回写画布，失败步骤一眼可见。

### 无代码键鼠录制

- 支持屏幕模式和窗口模式录制。
- 可捕获单击、拖动、滚轮和常见键盘组合。
- 窗口模式自动保存窗口相对坐标，适合制作可复用的自动化脚本。
- 录制结果追加到当前脚本；单次最多录制 4,000 个操作。

### 多窗口桌面自动化

- 用窗口标题正则包含目标窗口，用排除正则移除不需要的窗口。
- 前台支持串行和伪并行；后台支持多窗口并行。
- 支持启动、关闭、最小化程序，以及统一整理窗口布局和设置窗口分辨率。

### 视觉识别与自动化判断

- **色判断**：根据屏幕颜色决定下一步流程。
- **图判断**：根据图像匹配结果决定下一步流程。
- **OCR 文本判断**：识别截图中的文字后进入不同分支。
- 判断节点可以直接接入流程连线，不需要额外编写条件代码。

### 稳定运行与项目管理

- 运行前检查开始/结束节点、孤立节点、参数、坐标、分支出口和非法环路。
- 支持异步暂停、恢复、停止和防重复运行。
- `.gflow` 项目保存采用临时文件、序列化校验和原子替换，降低项目损坏风险。
- 日志保存在 `%LOCALAPPDATA%\GameFlowStudio\logs`，方便排查自动化脚本问题。

## 支持的自动化节点

| 类别 | 节点 |
| --- | --- |
| 流程控制 | 开始、等待、结束 |
| 鼠标键盘 | 点击、鼠标拖动、鼠标滑动、键盘按键、输入文本 |
| 视觉判断 | 色判断、图判断、文本判断 |
| 窗口与程序 | 启动程序、关闭程序、最小化窗口、窗口整理、设置窗口分辨率 |

## 快捷键

| 快捷键 | 操作 |
| --- | --- |
| `F8` | 运行自动化脚本 |
| `F9` | 暂停 / 继续 |
| `F10` | 停止运行 |
| `F7` | 停止录制 |

快捷键可以在设置中改为其他 `F1`–`F24` 按键。

## 下载、配置与安全提示

最新 Windows 安装包和 SHA-256 校验文件见 [GitHub Releases](https://github.com/HanJason233/GameFlowStudio/releases/latest)。

使用 OCR 文本判断时，可在“全局设置 → Gemini OCR API 密钥”中保存 API 密钥，也可以使用 `GEMINI_API_KEY` 环境变量。API 地址、模型、提示词和超时时间均可通过设置覆盖。

自动化脚本可能操作真实窗口、输入文字或启动程序。建议先使用示例项目和低风险窗口验证流程，再处理重要数据。

## 从源码构建

<details>
<summary>展开构建、测试和打包命令</summary>

开发环境：.NET SDK 10.0.302、Node.js 24.18.0 LTS。

```powershell
cd .\src\GameFlowStudio.Editor
npm.cmd ci
npm.cmd run lint
npm.cmd run test
npm.cmd run build

cd ..\..
dotnet restore GameFlowStudio.sln
dotnet test GameFlowStudio.sln -c Release
dotnet build GameFlowStudio.sln -c Release
```

生成 Windows x64 MSI：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build\Publish-Installer.ps1 -Version 0.1.5
```

</details>

## 项目结构

```text
src/        C# 核心、Windows 自动化、WPF 桌面端和 React 可视化编辑器
tests/      .NET 单元测试
installer/  WiX Windows 安装包工程
build/      发布和打包脚本
docs/       架构、通信协议和工作流格式文档
samples/    示例自动化流程
```

更多信息： [架构设计](docs/architecture.md) · [编辑器通信协议](docs/editor-communication.md) · [工作流格式](docs/workflow-schema.md)

## 当前限制

<details>
<summary>展开查看</summary>

- 当前版本不提供自动更新、云同步、登录、插件市场或崩溃恢复。
- 录制不采集连续鼠标轨迹、触摸、手柄或 IME 文本提交。
- 锁屏、UAC 安全桌面、部分游戏和受保护窗口可能无法捕获或响应后台输入。
- 后台自动化依赖目标窗口对传统 Win32 消息和 `PrintWindow` 的支持。

</details>

## 许可证

当前仓库未声明开源许可证。使用或再分发前，请先联系项目作者确认授权范围。
