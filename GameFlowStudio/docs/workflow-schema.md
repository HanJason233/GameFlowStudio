# `.gflow` 1.4 协议

项目是 UTF-8 JSON 文件，扩展名 `.gflow`，根对象如下：

桌面端打开文件时要求显式提供 `schemaVersion`，并只写入当前支持的 `1.4`。更高版本会被拒绝打开，避免未知字段在重新保存时丢失；字段显式为 `null` 也不会替代必需的对象或集合。

```json
{
  "schemaVersion": "1.4",
  "project": { "id": "project-id", "name": "项目名", "createdAt": "ISO-8601", "updatedAt": "ISO-8601" },
  "scripts": [
    {
      "id": "script-id",
      "name": "主流程",
      "windowScope": { "enabled": false, "titlePattern": ".*", "executionMode": "firstOnly" },
      "defaultNodeDelayMs": 0,
      "variables": { "counter": { "type": "number", "value": 0 } },
      "nodes": [],
      "edges": []
    }
  ]
}
```

## 逻辑判断

`colorCondition`、`imageCondition` 和 `ocrCondition` 直接在节点参数中保存检测内容；每个节点命中直接输出 `true`，未命中直接输出 `false`。色判断使用 `coordinateMode`、`x`、`y`、`width`、`height`、`targetColor`、`tolerance` 和可选 `minMatchRatio`；图判断使用 `coordinateMode`、`templatePath`、可选搜索区域、`tolerance` 与 `matchThreshold`；Gemini OCR 判断使用 `coordinateMode`、`x`、`y`、`width`、`height`、`targetText` 与 `matchThreshold`。桌面端 Gemini OCR 由宿主注入的 `IOcrService` 调用云端 `generateContent` 接口，匹配得分达到阈值时命中；API 密钥优先使用全局设置中保存的值，也可使用 `GEMINI_API_KEY` 环境变量。

一个项目文件可以包含多个脚本，每个脚本独立保存窗口作用域、节点默认等待时间、变量、节点和连接。运行时执行当前脚本的节点图。启用脚本的窗口作用域后，运行开始时会按 `titlePattern` 查找窗口，匹配结果由运行器作为当前窗口上下文使用。`executionMode` 支持 `firstOnly`（只操作第一个窗口）、`windowPriority`（窗口优先，逐窗口完整执行）和 `actionPriority`（动作优先，每轮让每个窗口执行一个当前动作）；匹配到单个窗口时三者效果相同。`defaultNodeDelayMs` 只对当前脚本的普通节点间隔生效，显式等待节点仍使用自身等待时间。节点包含 `id`、稳定的 `type`、React Flow `position` 以及 `data.displayName`、`data.parameters`。边包含 `source`、`sourceHandle`、`target`、`targetHandle`。

## 类型和参数

| type | 主要参数 | 输出 |
|---|---|---|
| `start` | 无 | `output` |
| `mouseClick` | `clickMode`, `coordinateMode`, `x`, `y`, `button`, `clickCount`, `intervalMs` | `output` |

| `mouseMove` | `inputMode`, `coordinateMode`, `x`, `y`, `durationMs` | `output` |
| `mouseDrag` | `inputMode`, `coordinateMode`, `startX`, `startY`, `endX`, `endY`, `button`, `durationMs` | `output` |
| `mouseScroll` | `inputMode`, `coordinateMode`, `x`, `y`, `delta` | `output` |
| `keyPress` | `inputMode`, `key`, `durationMs`, `repeatCount`, `intervalMs` | `output` |
| `textInput` | `inputMode`, `text`, `intervalMs` | `output` |
| `wait` | `durationMs`，可选 `randomMinMs`/`randomMaxMs` | `output` |
| `colorCondition` | `captureMode`, `coordinateMode`, `x`, `y`, `width`, `height`, `targetColor`, `tolerance`, `minMatchRatio` | `true`, `false` |
| `imageCondition` | `captureMode`, `coordinateMode`, `templatePath`, `x`, `y`, 可选 `width`/`height`、`tolerance`、`matchThreshold` | `true`, `false` |
| `ocrCondition` | `captureMode`, `coordinateMode`, `x`, `y`, `width`, `height`, `targetText`, `matchThreshold` | `true`, `false` |
| `runProgram` | `path`, `arguments` | `output` |
| `closeProgram` | `windowActionMode`, `titlePattern` | `output` |
| `minimizeWindow` | `windowActionMode`, `titlePattern` | `output` |
| `arrangeWindows` | `titlePattern`, `x`, `y`, `offsetY` | `output` |
| `setWindowResolution` | `windowActionMode`, `titlePattern`, `width`, `height` | `output` |
| `end` | 无 | 无 |

`coordinateMode` 默认为 `followScript`（跟随脚本）：脚本使用整个屏幕时按屏幕绝对坐标，脚本使用正则匹配窗口时按当前目标窗口的相对坐标。`absolute`（强制绝对）和 `relative`（强制相对）会覆盖脚本窗口范围。

`mouseClick.clickMode`、其他键鼠节点的 `inputMode`、检测节点的 `captureMode`，以及关闭/最小化/分辨率节点的 `windowActionMode` 均支持 `foreground`（默认）和 `background`（危险）。旧项目缺少字段时始终按 `foreground` 执行。

后台键鼠要求当前执行上下文恰好包含一个窗口，通过 `WM_MOUSE*`、`WM_KEY*` 或 `WM_CHAR` 投递传统窗口消息，不移动真实鼠标或激活窗口。依赖 Raw Input、DirectInput、前台状态、更高进程权限或保护机制的程序可能忽略或拒绝消息。单个后台点击节点的 `clickCount` 上限为 100。

后台检测同样要求唯一窗口，通过 `PrintWindow` 请求目标绘制整窗，再按节点的物理屏幕区域裁剪。硬件加速、受保护或最小化窗口可能拒绝绘制或返回黑图；抓取失败和区域越界会直接令节点失败，不回退到桌面截图。单次后台读取最多等待 5 秒；不可取消的本机调用若仍未返回，会占用唯一抓图闸门，避免继续堆积线程和 GDI 资源。

关闭程序、最小化窗口、窗口整理和设置窗口分辨率会使用动作自身的 `titlePattern` 查找正在运行的窗口，不使用脚本窗口作用域的匹配结果；这些动作都会对全部匹配窗口句柄执行。`windowActionMode=background` 时关闭、最小化和设置分辨率不先激活匹配窗口。窗口整理本身使用不激活窗口的排列方式：将首个匹配窗口的左上角移动到屏幕坐标 `(x, y)`，后续窗口保持原有大小并按 `offsetY` 像素依次向下堆叠。

变量类型为 `string`、`number`、`boolean`。鼠标按钮为 `left`、`right`、`middle`；日志级别为 `debug`、`information`、`warning`、`error`。

结构节点不能禁用。禁用的普通动作节点沿唯一 `output` 直接跳过。工作流不允许出现环路。

完整示例见 `samples/basic-example.gflow`。
