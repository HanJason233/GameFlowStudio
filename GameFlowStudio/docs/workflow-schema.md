# `.gflow` 1.4 协议

项目是 UTF-8 JSON 文件，扩展名 `.gflow`，根对象如下：

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
      "detectors": [],
      "nodes": [],
      "edges": []
    }
  ]
}
```

## 脚本检测器与逻辑判断

每个脚本独立保存 `detectors`。检测器支持 `color`、`image`、`ocr`；`aiRecognition` 当前返回 TODO 未支持结果。检测器只供当前脚本的逻辑判断节点使用。

`color` 参数使用 `coordinateMode`、`x`、`y`、`width`、`height`、`targetColor`、`tolerance` 和可选 `minMatchRatio`；`image` 使用 `coordinateMode`、`templatePath`、可选搜索区域、`tolerance` 与 `matchThreshold`；`ocr` 使用 `coordinateMode`、`x`、`y`、`width`、`height`、`targetText` 与 `matchThreshold`。桌面端 OCR 由宿主注入的 `IOcrService` 调用 Gemini 云端 `generateContent` 接口，匹配得分达到阈值时命中；API 密钥优先使用全局设置中保存的值，也可使用 `GEMINI_API_KEY` 环境变量。

`stateCondition`（联合判断）到达节点时检测一次，所有检测器都命中才输出 `true`，任一检测器未命中或不受支持则输出 `false`。`colorCondition`、`imageCondition` 和 `ocrCondition` 直接在节点参数中保存检测内容，不引用脚本检测器；每个节点命中直接输出 `true`，未命中直接输出 `false`。

一个项目文件可以包含多个脚本，每个脚本独立保存窗口作用域、节点默认等待时间、变量、检测器、节点和连接。运行时执行当前脚本的节点图。启用脚本的窗口作用域后，运行开始时会按 `titlePattern` 查找窗口，匹配结果由运行器作为当前窗口上下文使用。`executionMode` 支持 `firstOnly`（只操作第一个窗口）、`windowPriority`（窗口优先，逐窗口完整执行）和 `actionPriority`（动作优先，每轮让每个窗口执行一个当前动作）；匹配到单个窗口时三者效果相同。`defaultNodeDelayMs` 只对当前脚本的普通节点间隔生效，显式等待节点仍使用自身等待时间。节点包含 `id`、稳定的 `type`、React Flow `position` 以及 `data.displayName`、`data.parameters`。边包含 `source`、`sourceHandle`、`target`、`targetHandle`。

## 类型和参数

| type | 主要参数 | 输出 |
|---|---|---|
| `start` | 无 | `output` |
| `mouseClick` | `coordinateMode`, `x`, `y`, `button`, `clickCount`, `intervalMs` | `output` |

| `mouseMove` | `coordinateMode`, `x`, `y`, `durationMs` | `output` |
| `mouseDrag` | `coordinateMode`, `startX`, `startY`, `endX`, `endY`, `button`, `durationMs` | `output` |
| `mouseScroll` | `coordinateMode`, `x`, `y`, `delta` | `output` |
| `keyPress` | `key`, `durationMs`, `repeatCount`, `intervalMs` | `output` |
| `textInput` | `text`, `intervalMs` | `output` |
| `wait` | `durationMs`，可选 `randomMinMs`/`randomMaxMs` | `output` |
| `colorCondition` | `coordinateMode`, `x`, `y`, `width`, `height`, `targetColor`, `tolerance`, `minMatchRatio` | `true`, `false` |
| `imageCondition` | `coordinateMode`, `templatePath`, `x`, `y`, 可选 `width`/`height`、`tolerance`、`matchThreshold` | `true`, `false` |
| `ocrCondition` | `coordinateMode`, `x`, `y`, `width`, `height`, `targetText`, `matchThreshold` | `true`, `false` |
| `stateCondition` | `detectors`（多个检测器定义，全部命中才算命中） | `true`, `false` |
| `runProgram` | `path`, `arguments`, `workingDirectory`, `waitForExit` | `output` |
| `closeProgram` | `windowHandleVariable`, `windowHandleIndex` | `output` |
| `resizeWindow` | `windowHandleVariable`, `windowHandleIndex`, `width`, `height` | `output` |
| `minimizeWindow` | `windowHandleVariable`, `windowHandleIndex` | `output` |
| `arrangeWindows` | `windowHandleVariable`, `x`, `y`, `width`, `height`, `columns`, `gapX`, `gapY` | `output` |
| `setWindowResolution` | `windowHandleVariable`, `windowHandleIndex`, `width`, `height` | `output` |
| `end` | 无 | 无 |

`coordinateMode` 默认为 `followScript`（跟随脚本）：脚本使用整个屏幕时按屏幕绝对坐标，脚本使用正则匹配窗口时按当前目标窗口的相对坐标。`absolute`（强制绝对）和 `relative`（强制相对）会覆盖脚本窗口范围。

变量类型为 `string`、`number`、`boolean`。鼠标按钮为 `left`、`right`、`middle`；日志级别为 `debug`、`information`、`warning`、`error`。

结构节点不能禁用。禁用的普通动作节点沿唯一 `output` 直接跳过。工作流不允许出现环路。

完整示例见 `samples/basic-example.gflow`。
