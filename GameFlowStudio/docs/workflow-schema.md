# `.gflow` 1.8 协议

项目是 UTF-8 JSON 文件，扩展名 `.gflow`，根对象如下：

桌面端打开文件时要求显式提供 `schemaVersion`，并且只接受、写入当前支持的 `1.8`；其他版本会被拒绝。`windowScope.enabled`、`windowScope.matchPattern`、`windowScope.excludePattern`、`interactionMode` 与 `windowScope.executionMode` 必须显式提供；枚举不接受数字。字段显式为 `null` 也不会替代必需的对象或集合。

```json
{
  "schemaVersion": "1.8",
  "project": { "id": "project-id", "name": "项目名", "createdAt": "ISO-8601", "updatedAt": "ISO-8601" },
  "scripts": [
    {
      "id": "script-id",
      "name": "主流程",
      "interactionMode": "foreground",
      "windowScope": { "enabled": false, "matchPattern": ".*", "excludePattern": "", "executionMode": "serial" },
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

一个项目文件可以包含多个脚本，每个脚本独立保存窗口作用域、节点默认等待时间、变量、节点和连接。运行时执行当前脚本的节点图。启用脚本窗口匹配后，运行器先用 `matchPattern` 规定可操作窗口，再从结果中移除标题命中 `excludePattern` 的窗口；`excludePattern` 为空字符串表示不排除。筛选后的窗口集合才会成为执行上下文，若集合为空则运行失败。`executionMode` 是多窗口操作流程：`serial` 逐窗口完整执行；`pseudoParallel` 依次切换窗口，每轮让每个窗口执行一个当前动作，使操作进度保持一致；`parallel` 为每个窗口同时运行独立流程。前台 `foreground` 仅允许 `serial` 或 `pseudoParallel`，后台 `background` 仅允许 `parallel`，其他组合会在读取、保存或运行验证时被拒绝。匹配到单个窗口时三者的操作结果相同。`defaultNodeDelayMs` 只对当前脚本的普通节点间隔生效，显式等待节点仍使用自身等待时间。节点包含 `id`、稳定的 `type`、React Flow `position` 以及 `data.displayName`、`data.parameters`。边包含 `source`、`sourceHandle`、`target`、`targetHandle`。

跨脚本跳转沿用当前执行轨道。来源与目标脚本的 `interactionMode`、`executionMode` 和 `windowScope.enabled` 必须一致；窗口匹配启用时，规范化后的 `windowScope.matchPattern` 与 `windowScope.excludePattern` 也必须一致。空白包含规则按 `.*` 处理，空白排除规则按不排除处理。运行器会在跳转前重新执行包含与排除匹配，并确认当前唯一窗口仍在最终结果中，否则在执行目标动作前失败。

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
| `runProgram` | `path`, `arguments` | `output` |
| `closeProgram` | `titlePattern` | `output` |
| `minimizeWindow` | `titlePattern` | `output` |
| `arrangeWindows` | `titlePattern`, `x`, `y`, `offsetY` | `output` |
| `setWindowResolution` | `titlePattern`, `width`, `height` | `output` |
| `end` | 无 | 无 |

`coordinateMode` 默认为 `followScript`（跟随脚本）：脚本使用整个屏幕时按屏幕绝对坐标，脚本使用正则匹配窗口时按当前目标窗口的相对坐标。`absolute`（强制绝对）和 `relative`（强制相对）会覆盖脚本窗口范围。

脚本的 `interactionMode` 支持 `foreground`（默认）和 `background`。该设置统一作用于脚本内所有可后台执行或读取的节点，操作卡片不能单独覆盖。前台通过 `executionMode` 选择伪并行或串行，后台固定使用并行流程。

后台键鼠要求当前执行上下文恰好包含一个窗口，通过 `WM_MOUSE*`、`WM_KEY*` 或 `WM_CHAR` 投递传统窗口消息，不移动真实鼠标或激活窗口。依赖 Raw Input、DirectInput、前台状态、更高进程权限或保护机制的程序可能忽略或拒绝消息。单个后台点击节点的 `clickCount` 上限为 100。

后台检测同样要求唯一窗口，通过 `PrintWindow` 请求目标绘制整窗，再按节点的物理屏幕区域裁剪。硬件加速、受保护或最小化窗口可能拒绝绘制或返回黑图；抓取失败和区域越界会直接令节点失败，不回退到桌面截图。单次后台读取最多等待 5 秒；不可取消的本机调用若仍未返回，会占用唯一抓图闸门，避免继续堆积线程和 GDI 资源。

关闭程序、最小化窗口和设置窗口分辨率在后台存在唯一脚本窗口上下文时只处理当前窗口；没有唯一上下文或使用前台方式时，才使用动作自身的 `titlePattern` 处理全部匹配窗口。窗口整理始终按动作自身的 `titlePattern` 查找整组窗口，并以不激活窗口的方式将首个窗口移动到屏幕坐标 `(x, y)`，后续窗口保持原有大小并按 `offsetY` 像素依次向下堆叠。后台并行流程中，启动程序和窗口整理属于所有窗口轨道共享的全局动作，同一访问轮次只执行一次。

变量类型为 `string`、`number`、`boolean`。鼠标按钮为 `left`、`right`、`middle`；日志级别为 `debug`、`information`、`warning`、`error`。

结构节点不能禁用。禁用的普通动作节点沿唯一 `output` 直接跳过。工作流不允许出现环路。

完整示例见 `samples/basic-example.gflow`。
