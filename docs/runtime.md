# Runtime worker

## Build and deployment

The worker is `core/GameFlowStudio.Runtime/GameFlowStudio.Runtime.csproj`, a `net10.0-windows` executable with no WPF, WebView2, or React dependency. Desktop has a build-only project reference so the worker is built alongside it. Desktop publish additionally publishes Runtime for the same RID and copies its executable and dependencies beside `GameFlowStudio.Desktop.exe`.

Runtime is published to a separate `Runtime/` subdirectory so its self-contained framework files cannot overwrite Desktop's WPF files. The installer harvests the published directory, including `Runtime/GameFlowStudio.Runtime.exe` and its dependencies. Desktop resolves the worker from `AppContext.BaseDirectory/Runtime`.

## Launch modes

Desktop-managed workers are started in the current interactive user session with a private named pipe and token:

```text
GameFlowStudio.Runtime.exe --managed --pipe <pipe-name> --token <random-token>
```

Standalone mode loads the project and exits after printing its metadata. Add `--run` to validate and execute the selected script, then exit when it finishes. Ctrl+C requests cancellation and returns exit code 130:

```text
GameFlowStudio.Runtime.exe --project "C:\path\project.gflow"
GameFlowStudio.Runtime.exe --project "C:\path\project.gflow" --script 1 --run
```

Pipe mode requires both `--pipe` and `--token`. Without `--managed`, it is External Worker Mode: it accepts an authenticated client and stays alive until that client sends `shutdown`. An optional `--project` loads a snapshot before accepting clients; `--run` also starts the selected script. Standalone mode does not create a pipe. All modes use the existing storage, validator, and runner implementations.

## Lifecycle

Desktop starts one Runtime worker for its process on the first operation, connects, negotiates protocol version `1.1`, and initializes that individual pipe connection. Every reconnect must authenticate again; Runtime's process-wide initialized state does not authorize a new connection. A client that does not authenticate within five seconds is disconnected. Project load, validate, execution, pause/resume/stop, status, logs, heartbeat, snapshot, event replay, and shutdown all use commands. Runtime events flow over the same connection.

Runtime runs independently of the WPF views and holds the loaded snapshot in its own process. A lost pipe does not cancel a task. After reconnect, Desktop first retrieves a full run snapshot, then replays events after the snapshot sequence. It buffers live events during recovery, discards duplicate sequence numbers, and re-syncs from a newer snapshot when the bounded event history no longer contains the requested sequence. The snapshot restores node states, progress, and buffered logs. If Runtime exits, Desktop reports the interrupted run and starts a replacement worker, reloading the last valid project snapshot.

Desktop sends `shutdown` once during asynchronous application shutdown; Runtime stops an active run and exits. If Desktop disappears, a managed Runtime waits five minutes for reconnect before it stops an active orphaned task. It then exits once idle and disconnected. A worker started by Desktop that never receives an authenticated initialize handshake exits after one minute. Desktop sends a heartbeat every five seconds with a three-second response timeout. A single timeout is logged; three consecutive timeouts cause Desktop to restart only its owned worker. Reconnect attempts use exponential backoff capped at five seconds. On close, Desktop waits three seconds for graceful process exit and then terminates only the worker process it owns.

The pure-run window remains a WPF control surface launched from Desktop. It opens the existing snapshot, shows scripts and progress, and sends controls to a separate Runtime worker. It does not instantiate WorkflowRunner or execute nodes inside the UI process.
