# `.gflow` 1.3 协议

项目是 UTF-8 JSON 文件，扩展名 `.gflow`，根对象如下：

```json
{
  "schemaVersion": "1.3",
  "project": { "id": "project-id", "name": "项目名", "createdAt": "ISO-8601", "updatedAt": "ISO-8601" },
  "scripts": [
    {
      "id": "script-id",
      "name": "主流程",
      "windowScope": { "enabled": false, "titlePattern": ".*", "outputVariable": "windowHandles" },
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

每个脚本独立保存 `detectors`。检测器支持 `color`、`image`，`ocr` 与 `aiRecognition` 当前返回 TODO 未支持结果。检测器只供当前脚本的逻辑判断节点使用。

`color` 参数使用 `x`、`y`、`width`、`height`、`targetColor`、`tolerance` 和可选 `minMatchRatio`；`image` 使用 `templatePath`、可选搜索区域、`tolerance` 与 `matchThreshold`。

`stateCondition`（联合判断）到达节点时检测一次；只有一个规则时直接作为单一判断，多个规则时按 AND 联合判断，并按 `true`/`false` 输出继续流程。`colorCondition` 和 `imageCondition` 直接在节点参数中保存检测内容，不引用脚本检测器。

一个项目文件可以包含多个脚本，每个脚本独立保存窗口作用域、节点默认等待时间、变量、检测器、节点和连接。运行时执行当前脚本的节点图。启用脚本的窗口作用域后，运行开始时会按 `titlePattern` 查找窗口，并将句柄数组写入 `outputVariable`。`defaultNodeDelayMs` 只对当前脚本的普通节点间隔生效，显式等待节点仍使用自身等待时间。节点包含 `id`、稳定的 `type`、React Flow `position` 以及 `data.displayName`、`data.enabled`、`data.parameters`。边包含 `source`、`sourceHandle`、`target`、`targetHandle`。

## 类型和参数

| type | 主要参数 | 输出 |
|---|---|---|
| `start` | 无 | `output` |
| `mouseClick` | `coordinateMode`, `x`, `y`, `button`, `clickCount`, `intervalMs`, `windowHandleVariable`, `windowHandleIndex` | `output` |

`mouseClick.coordinateMode` 默认为 `screen`，表示使用屏幕绝对坐标。设置为 `windowRelative` 后，执行器会从 `windowHandleVariable` 指定的窗口句柄变量中按 `windowHandleIndex` 取句柄，读取窗口左上角，再加上 `x`/`y` 相对偏移后执行点击。旧版本鼠标点击节点缺少这些字段时仍按屏幕坐标执行。
| `mouseMove` | `x`, `y`, `durationMs` | `output` |
| `mouseDrag` | `startX`, `startY`, `endX`, `endY`, `button`, `durationMs` | `output` |
| `mouseScroll` | `x`, `y`, `delta` | `output` |
| `keyPress` | `key`, `durationMs`, `repeatCount`, `intervalMs` | `output` |
| `textInput` | `text`, `intervalMs` | `output` |
| `wait` | `durationMs`，可选 `randomMinMs`/`randomMaxMs` | `output` |
| `colorCondition` | `x`, `y`, `width`, `height`, `targetColor`, `tolerance`, `minMatchRatio`, `state` | `true`, `false` |
| `imageCondition` | `templatePath`, `x`, `y`, 可选 `width`/`height`、`tolerance`、`matchThreshold`、`state` | `true`, `false` |
| `stateCondition` | `detectorRules`（多个检测器 ID 与期望 `state` 0/1 的数组） | `true`, `false` |
| `log` | `message`, `level` | `output` |
| `runProgram` | `path`, `arguments`, `workingDirectory`, `waitForExit` | `output` |
| `closeProgram` | `windowHandleVariable`, `windowHandleIndex` | `output` |
| `resizeWindow` | `windowHandleVariable`, `windowHandleIndex`, `width`, `height` | `output` |
| `minimizeWindow` | `windowHandleVariable`, `windowHandleIndex` | `output` |
| `arrangeWindows` | `windowHandleVariable`, `x`, `y`, `width`, `height`, `columns`, `gapX`, `gapY` | `output` |
| `setWindowResolution` | `windowHandleVariable`, `windowHandleIndex`, `width`, `height` | `output` |
| `end` | 无 | 无 |

变量类型为 `string`、`number`、`boolean`。鼠标按钮为 `left`、`right`、`middle`；日志级别为 `debug`、`information`、`warning`、`error`。窗口相对点击会在执行前自动激活对应句柄的窗口。

结构节点不能禁用。禁用的普通动作节点沿唯一 `output` 直接跳过。工作流不允许出现环路。

完整示例见 `samples/basic-example.gflow`。
