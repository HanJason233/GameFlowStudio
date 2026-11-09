# Runtime IPC protocol

## Transport and message envelope

Desktop and Runtime communicate through one local `System.IO.Pipes` named-pipe connection. Frames are UTF-8 JSON preceded by a four-byte little-endian signed length. The current maximum frame is 16 MiB. Both sides read the full frame before JSON parsing and serialize writes through a semaphore.

Every frame has this envelope:

```json
{
  "protocolVersion": "1.1",
  "messageId": "guid",
  "replyTo": null,
  "kind": "Command",
  "type": "initialize",
  "payload": {},
  "sequence": 0
}
```

`kind` is `Command`, `Response`, or `Event`. Requests use a unique `messageId`; a response sets `replyTo` to that ID. Runtime event frames carry a strictly increasing process-wide `sequence`; each event captures its `runId` when created. Message bodies are independent protocol DTOs; no WorkflowRunner, WorkflowNode, or other live engine object crosses the pipe.

## Startup and security

1. Desktop creates a random pipe name and 256-bit token, then launches the worker with `--managed --pipe <name> --token <token>`.
2. Runtime creates a one-instance, asynchronous pipe with `PipeOptions.CurrentUserOnly`.
3. Every new pipe connection must send `initialize` within five seconds, with the token, its PID, and supported protocol version.
4. Runtime compares the token in fixed time, checks the protocol version, and returns its version and PID. A mismatch or bad token is rejected and the connection is closed. Authentication is per connection; a previous successful initialization does not authorize a reconnect.

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
| `get_run_snapshot` | none | full run state, node statuses, progress, sequence, and buffered logs |
| `get_events` | afterSequence | ordered events after the requested sequence, plus a history-truncated flag |
| `ping` | none | runtime PID, state, run ID, latest event sequence, last progress time |
| `shutdown` | none | graceful worker shutdown |

Repeated `start_run` requests with the same `runId` are idempotent. Runtime will not create a second execution for that ID. Desktop uses a different `runId` for each run.

## Events

Runtime publishes `worker_ready`, `state_changed`, `node_started`, `node_completed`, `node_failed`, `progress_changed`, `log_received`, `run_completed`, and `run_failed`. Each event carries its event type in the envelope and a process-safe `RuntimeEventPayload`. Run events include `runId` and `sequence`; log payloads carry their own log sequence. `recording_action_received` is reserved for recording integration and can be added without serializing runtime objects.

Runtime retains the latest 4,096 events in a bounded journal. If the requested sequence has been evicted, `get_events` reports that history was truncated; the client must replace local state from `get_run_snapshot` and continue from its sequence. Desktop takes a snapshot first, replays subsequent journal events, buffers live events during recovery, and only then resumes live delivery. Event sequence numbers are strictly increasing within a worker process. Desktop ignores already-seen events, maps script/node IDs to editor statuses, and deduplicates logs by their own sequence.

## Failure, cancellation, heartbeat, and reconnect

- Requests have a 30-second default timeout (the startup handshake uses five seconds). Callers can cancel requests with a `CancellationToken`.
- Pipe framing rejects zero, negative, oversized, truncated, malformed, or unsupported-version messages. Remote command failures return an error response instead of terminating the reader loop.
- `RuntimeProcessManager` sends a `ping` every five seconds with a three-second response timeout. It distinguishes pipe loss from process exit and repeated heartbeat timeouts; one timeout does not terminate the worker. Three consecutive timeouts trigger a controlled restart of only the owned worker, and reconnect retries use capped exponential backoff.
- Runtime keeps an active task alive if the pipe drops. After reconnect, Desktop repeats `initialize`, obtains `get_run_snapshot`, and calls `get_events` to recover node execution, progress, and log events.
- A restarted worker reloads the latest still-live snapshot. If the previous worker crashed, status reports that the run is no longer active; Desktop shows the interrupted run as failed and remains usable.
- Desktop requests `shutdown` on exit. If the parent disappears, a managed Runtime waits five minutes before stopping a still-active orphaned task, then exits after the connection grace period.
