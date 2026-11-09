# Runtime IPC protocol

## Transport and message envelope

Desktop and Runtime communicate through one local `System.IO.Pipes` named-pipe connection. Frames are UTF-8 JSON preceded by a four-byte little-endian signed length. The current maximum frame is 16 MiB. Both sides read the full frame before JSON parsing and serialize writes through a semaphore.

Every frame has this envelope:

```json
{
  "protocolVersion": "1.0",
  "messageId": "guid",
  "replyTo": null,
  "kind": "Command",
  "type": "initialize",
  "payload": {},
  "sequence": 0
}
```

`kind` is `Command`, `Response`, or `Event`. Requests use a unique `messageId`; a response sets `replyTo` to that ID. Execution event frames carry a monotonically increasing `sequence` for their `runId`. Message bodies are independent protocol DTOs; no WorkflowRunner, WorkflowNode, or other live engine object crosses the pipe.

## Startup and security

1. Desktop creates a random pipe name and 256-bit token, then launches the worker with `--pipe <name> --token <token>`.
2. Runtime creates a one-instance, asynchronous pipe with `PipeOptions.CurrentUserOnly`.
3. The client sends `initialize` with the token, its PID, and supported protocol version.
4. Runtime compares the token in fixed time, checks the protocol version, and returns its version and PID. A mismatch or bad token is rejected.

The current-user pipe ACL restricts connections to the launching Windows user. The random token binds a client to the worker instance started by its Desktop process. No localhost HTTP endpoint, WebSocket service, or shared memory is used.

## Commands

| Type | Payload | Result |
| --- | --- | --- |
| `initialize` | token, protocolVersion, desktopProcessId | negotiated version, runtime PID, ready flag |
| `load_project` | absolute snapshotPath, optional active scriptId | project ID/name, active script ID, and script metadata |
| `validate` | optional scriptId | validity and validation errors with node IDs |
| `start_run` | runId, optional scriptId | accepted flag |
| `pause_run` | none | current status |
| `resume_run` | none | current status |
| `stop_run` | none | current status |
| `get_status` | none | state, run ID, current node, initialization state |
| `get_logs` | optional afterSequence | buffered runtime log entries |
| `ping` | none | ready flag and runtime PID |
| `shutdown` | none | graceful worker shutdown |

Repeated `start_run` requests with the same `runId` are idempotent. Runtime will not create a second execution for that ID. Desktop uses a different `runId` for each run.

## Events

Runtime publishes `worker_ready`, `state_changed`, `node_started`, `node_completed`, `node_failed`, `progress_changed`, `log_received`, `run_completed`, and `run_failed`. Each event carries its event type in the envelope and a process-safe `RuntimeEventPayload`. Run events include `runId` and `sequence`; log payloads carry their own log sequence. `recording_action_received` is reserved for recording integration and can be added without serializing runtime objects.

Desktop ignores duplicate or out-of-order run events, maps node IDs to editor highlight/success/error messages, and maintains a bounded log view. Runtime keeps a bounded log buffer so Desktop can request missing log entries after reconnecting.

## Failure, cancellation, heartbeat, and reconnect

- Requests have a 30-second default timeout (the startup handshake uses five seconds). Callers can cancel requests with a `CancellationToken`.
- Pipe framing rejects zero, negative, oversized, truncated, malformed, or unsupported-version messages. Remote command failures return an error response instead of terminating the reader loop.
- `RuntimeProcessManager` sends a `ping` every five seconds. A failed heartbeat closes the client channel and starts a reconnect loop.
- Runtime keeps an active task alive if the pipe drops. After reconnect, Desktop repeats `initialize`, then calls `get_status` and `get_logs`.
- A restarted worker reloads the latest still-live snapshot. If the previous worker crashed, status reports that the run is no longer active; Desktop shows the interrupted run as failed and remains usable.
- Desktop requests `shutdown` on exit. If the parent disappears, a managed Runtime waits five minutes before stopping a still-active orphaned task, then exits after the connection grace period.
