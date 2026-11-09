# Runtime worker

## Build and deployment

The worker is `core/GameFlowStudio.Runtime/GameFlowStudio.Runtime.csproj`, a `net10.0-windows` executable with no WPF, WebView2, or React dependency. Desktop has a build-only project reference so the worker is built alongside it. Desktop publish additionally publishes Runtime for the same RID and copies its executable and dependencies beside `GameFlowStudio.Desktop.exe`.

Runtime is published to a separate `Runtime/` subdirectory so its self-contained framework files cannot overwrite Desktop's WPF files. The installer harvests the published directory, including `Runtime/GameFlowStudio.Runtime.exe` and its dependencies. Desktop resolves the worker from `AppContext.BaseDirectory/Runtime`.

## Launch modes

Desktop-managed workers are started in the current interactive user session with a private named pipe and token:

```text
GameFlowStudio.Runtime.exe --pipe <pipe-name> --token <random-token>
```

Runtime can also load a project without Desktop, or run it immediately:

```text
GameFlowStudio.Runtime.exe --project "C:\path\project.gflow"
GameFlowStudio.Runtime.exe --project "C:\path\project.gflow" --script 1 --run
```

The standalone process exposes the same JSON named-pipe command interface. Supply `--pipe` and `--token` when a client needs to connect. The Runtime entry point creates the existing execution services, loads the `.gflow` file with `IProjectStorageService`, validates it with the existing `IWorkflowValidator`, and starts the existing `IWorkflowRunner` when `--run` is present.

## Lifecycle

Desktop starts one Runtime worker for its process on the first operation, connects, negotiates protocol version `1.0`, and initializes the worker. Project load, validate, execution, pause/resume/stop, status, logs, heartbeat, and shutdown all use commands. Runtime events flow over the same connection.

Runtime runs independently of the WPF views and holds the loaded snapshot in its own process. A lost pipe does not cancel a task. Desktop reconnects and asks for current status. If Runtime exits, Desktop reports the lost run without exiting and starts a replacement worker on its next operation.

On normal shutdown Desktop sends `shutdown`; Runtime stops an active run and exits. If Desktop disappears, a managed Runtime waits five minutes for reconnect before it stops an active orphaned task. It then exits once idle and disconnected. A worker started by Desktop that never receives an authenticated initialize handshake exits after one minute. Desktop waits three seconds for graceful shutdown and then terminates only the worker process it owns.

The pure-run window remains a WPF control surface launched from Desktop. It opens the existing snapshot, shows scripts and progress, and sends controls to a separate Runtime worker. It does not instantiate WorkflowRunner or execute nodes inside the UI process.
