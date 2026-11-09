# GameFlow Studio architecture

## Project and process boundaries

```text
GameFlowStudio.Desktop (WPF, WebView2)
  ├── GameFlowStudio.Protocol ── JSON Named Pipe ──┐
  ├── GameFlowStudio.Contracts                   │
  ├── GameFlowStudio.Storage                      │
  └── Desktop-only interaction services          │
                                                  ▼
                                      GameFlowStudio.Runtime (worker EXE)
                                        ├── Protocol
                                        ├── Contracts
                                        ├── Storage
                                        └── core execution projects
                                              ├── Workflow
                                              ├── Automation
                                              └── Infrastructure

GameFlowStudio.Editor (React Flow) ── WebView2 JSON ── Desktop
```

`src/` contains the desktop app, React editor, shared contracts, storage, and the IPC protocol. `core/` contains all execution implementations. `GameFlowStudio.Desktop.csproj` has a build-only reference to Runtime so it can package the worker; it does not reference the Runtime assembly or the Workflow and Automation projects.

The dependency direction is:

| Project | References |
| --- | --- |
| Desktop | Protocol, Contracts, Storage |
| Runtime | Protocol, Contracts, Storage, Workflow, Automation, Infrastructure |
| Workflow | Contracts |
| Automation | Contracts |
| Infrastructure | Contracts, Workflow, Automation, Storage |
| Protocol | .NET base libraries only |

Desktop owns editor windows, project dialogs, the pure-run window, window and coordinate pickers, screenshot previews, and low-level input recording. Those user interactions remain in the interactive WPF process. Runtime owns project loading, validation, scheduling, node execution, desktop automation, image detection, OCR, and execution logs. Runtime has no WPF, WebView2, React, or Desktop reference.

## Project snapshots and execution flow

1. The editor sends its current workflow document to Desktop through the existing WebView2 bridge.
2. Before validation and execution, Desktop writes a new temporary `.gflow` snapshot with the existing storage serializer. It does not pass a live model object across the process boundary.
3. `RuntimeProcessManager` starts `Runtime/GameFlowStudio.Runtime.exe` in the current user session and connects to its private named pipe.
4. Desktop negotiates the protocol version, asks Runtime to load the snapshot with the active script ID, and requests validation for that same script.
5. Desktop sends `start_run` with a fresh `runId`. Runtime executes the unchanged WorkflowRunner and Automation implementations.
6. Runtime sends state, node, progress, log, and terminal events. Desktop maps node IDs to React Flow status messages and renders the returned logs and state.
7. Desktop removes the temporary snapshot when that run ends. The worker keeps the loaded project in its own memory until another snapshot is loaded or it shuts down.

The file format remains the existing `.gflow` schema. Each run uses a unique snapshot and `runId`, so edits made after Run is pressed cannot change the in-flight project. Runtime can also load a `.gflow` directly with its `--project` option.

## Desktop interactions and recording

Window selection, coordinate/color/region picking, screenshot previews, and global input recording require the user's interactive desktop and remain in Desktop. The Desktop project contains small UI interaction adapters for those operations; it does not contain or instantiate `WorkflowRunner`, workflow validators, node executors, or Runtime automation services.

Recording still locks the editor, captures input in Desktop, and appends the resulting action snapshot using the shared `RecordedScriptFactory`. Existing recording rules, node types, UI, `.gflow` data, and window/coordinate semantics are retained.

## Worker security and lifecycle

- Runtime is a normal user-session process, not a Windows Service.
- Desktop generates a random pipe name and 256-bit handshake token for its worker. The token is checked with a fixed-time comparison.
- The server creates a single-instance named pipe with `PipeOptions.CurrentUserOnly`; other Windows users cannot connect.
- Runtime remains alive across a temporary pipe disconnect so a task is not cancelled just because UI communication was interrupted. Desktop reconnects, renegotiates, and requests `get_status` and logs.
- Runtime crashes do not terminate Desktop. The manager reports the lost run and restarts the worker for the next request.
- On orderly Desktop exit, Runtime receives `shutdown` and stops the active task. If Desktop disappears, Runtime gives the parent five minutes to return, then requests a graceful stop for an active orphaned task and exits after the connection grace period. A managed worker that never completes its initial handshake exits after one minute.
- The manager waits for graceful worker exit and terminates only its owned worker if it does not exit within three seconds of the shutdown request.

See [IPC protocol](ipc-protocol.md) and [Runtime lifecycle](runtime.md) for message and startup details.

## Preserved execution behavior

WorkflowRunner remains the existing engine. It still enforces its node execution budget, pause gate, validation, serial/pseudo-parallel/parallel multi-window modes, and cross-script compatibility. Automation still handles foreground/background input, window actions, screen and window capture, and recording rules. Runtime raises existing runner events as process-safe DTOs; it never serializes `WorkflowRunner`, `WorkflowNode`, or other live execution objects into IPC.

Desktop picker coordinates and execution coordinates remain virtual desktop physical pixels, including negative coordinates on multi-monitor layouts. The WPF picker converts to DIPs only when drawing its overlay. Foreground input continues through `SendInput`; background input uses the existing window-message path.
