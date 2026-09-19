# AI Logging Specification

Provide an `AILogger` entry point that records what happened, which client observed it, and the relevant simulation and presentation state. An agent should be able to diagnose a reproduction by reading its local files and opening linked screenshots.

## Scope

- Enable automatically in the Editor during Play Mode and in development builds. Compile all logging code and calls out of release builds.
- Analyze files after a reproduction. Each process writes locally; collect the relevant host and client folders for multiplayer diagnosis.
- Record structured events continuously, small baseline state samples, and denser samples around relevant events.
- Instrument player movement and item impacts first, alongside session lifecycle, Unity logs, and markers.
- Preserve the existing marker controls and console message when the diagnostic wiring is temporarily enabled. Also emit a structured marker. Markers do not take screenshots.
- Provide a separate screenshot action when targeted diagnostic wiring is temporarily enabled. Record every request and its outcome, including the saved filename and path.

The first version is a file recorder, with no telemetry backend, automatic upload, replay engine, live command server, or general scene crawler. New gameplay coverage comes from explicit logging calls and small, concrete state snapshots.

## Public API

Place `TwoBirds.AILogger` in `Assets/Game/Runtime/Diagnostics` as a static class. Its gameplay-facing API is:

```csharp
public static bool Enabled { get; }
public static void Log(string eventName, object data,
    AILogContext context = default, AILogLevel level = AILogLevel.Info,
    int eventVersion = 1);
public static void Screenshot();
```

`AILogContext` contains optional subject, related subject, operation identity, and simulation timing/role fields. Omitted context is valid for startup, menu, and general events. `AILogLevel` has `Info`, `Warning`, and `Error`.

For example, a movement call records an outcome and its actual values:

```csharp
AILogger.Log("player.impact.applied", new
{
    generation = impactGeneration,
    request_id = impact.RequestId,
    sequence = impact.Sequence,
    velocity_before = new[] { before.x, before.y, before.z },
    velocity_after = new[] { after.x, after.y, after.z },
    replay = PredictionManager.IsReconciling
}, context);
```

Use anonymous objects for short events and named data types for repeated payloads. Pass copied values, arrays, strings, and plain data objects. Do not serialize `GameObject`, `Component`, `Transform`, or another live Unity object graph. Represent Unity vectors and quaternions as numeric arrays explicitly.

The normal API runs on the main thread so it can stamp the observed frame and network context. The Unity log callback has a separate thread-safe ingress using only strings and copied scalar context. Freeze payloads before returning to callers; later gameplay mutations must not change queued records.

Mark the void entry points with both development conditional attributes. Guard any expensive state gathering outside the call with the same compilation conditions and `Enabled`. The disabled path must not construct payloads or start capture services.

## File format and record contract

Write compact UTF-8 JSON Lines: one complete JSON object per line, without pretty printing or a byte-order mark. JSON preserves named fields, native numbers, and escaped stack traces while allowing ordinary file search and streaming parsing.

Use Unity's Newtonsoft JSON package, already resolved at `3.2.2` in `Packages/packages-lock.json`. Declare that version as a direct dependency when implementing the logger. Use JSON serialization rather than manually escaping strings or introducing another format. [Unity Newtonsoft package documentation](https://docs.unity3d.com/Packages/com.unity.nuget.newtonsoft-json@3.2/manual/index.html).

| Field | Contract |
| --- | --- |
| `v` | Envelope schema version, initially `1`. |
| `run` | New GUID for every process launch or Editor Play session. |
| `seq` | Increasing record sequence assigned at queue admission; file order follows it. |
| `t_us` | Monotonic microseconds since run start, measured at observation. |
| `frame` | Observed Unity frame; `null` when unavailable, including background Unity logs. |
| `event` | Stable dotted name, such as `player.impact.applied`. |
| `event_v` | Payload version for this event, initially `1`. |
| `level` | `info`, `warning`, or `error`. |
| `ctx` | Captured session, peer, entity, operation, and simulation context. |
| `data` | Event-specific JSON object. |

Illustrative record:

```json
{"v":1,"run":"550e8400-e29b-41d4-a716-446655440000","seq":421,"t_us":18240000,"frame":1094,"event":"player.impact.applied","event_v":1,"level":"info","ctx":{"attempt":2,"network_session":684721,"connection":1,"subject":"player:7","generation":3,"operation":"impact:7:3:12","side":"client","is_owner":true,"movement_tick":1052,"server_tick":1047},"data":{"request_id":12,"sequence":9,"velocity_before":[0,0,0],"velocity_after":[2.1,0.4,0],"replay":false}}
```

World positions use metres, linear velocity uses metres/second, angular velocity uses radians/second, rotations use quaternion `[x,y,z,w]`, and durations include a unit suffix. Local-space values must say so in their names. Preserve useful float precision. Encode non-finite values as `null` with the affected field names in `non_finite_fields`; never silently turn them into zero.

Absent optional fields mean unavailable, not zero or false. Log meaningful outcomes and rejection reasons explicitly, including accepted, stale, duplicate, already incorporated, or invalid state. A replayed application is an observation of replay, not a second gameplay impact.

Additive fields and new events require no writer changes. Increment `event_v` when an existing payload changes meaning or type, and `v` when the envelope changes incompatibly. Keep an event catalog with fields, units, identity scope, and the emitting source file; readers ignore unknown fields and events.

## Identity and multiplayer correlation

Each file describes one process's observations. The logger sends no new gameplay messages and makes no authority decisions.

- Record the local `SessionController.SessionId` as `ctx.attempt`. It is a local attempt counter, not a shared multiplayer identity.
- Record the existing private `wireSession`, established through `SessionAdmission`, as `ctx.network_session`. Capture it at assignment and before reset. Before admission it is unknown.
- Treat that shared integer as an identifier within a reproduction, not a globally unique ID. Match it with run metadata and connection records; do not merge unrelated runs on that integer alone.
- Record connection ID, ownership, and the executing side at each relevant observation. A host can execute both server and client paths; a process-level `host` label cannot replace this distinction.
- For items, reuse world epoch, item ID, revision, simulator/releaser, operation, launch tick, and motion sequence/path where relevant. For impacts, reuse player generation, owner request ID, confirmed sequence, and owner/server application ticks.
- Keep entity identity separate from operation identity. Namespace IDs by kind, record spawn/despawn boundaries, and include existing generations/epochs. If a reused network object ID lacks a shared lifetime identifier, a local incarnation only distinguishes that process's lifetimes; cross-client matching must remain explicit about ambiguity.
- Log send, receive, accept/reject, apply, and reconcile at their actual decision points. A successful send does not establish receipt or application.

Monotonic time and `seq` order a single run. Preserve separately named local, movement, and server ticks where the system supplies them; ticks from different domains are not interchangeable. UTC run start helps locate related runs, but host/client clocks must not be assumed synchronized. Use matching operation IDs and actual network timing to compare clients.

## Events and state capture

| Coverage | Required records |
| --- | --- |
| Recorder | Run start/end, segment boundaries, dropped/truncated records, writer failure, capture availability. |
| Session | Attempt begin, phase/status changes, admission, connection/disconnection, scene change, local-player binding, readiness and shutdown reason. |
| Unity | Original message, log type, and available stack trace. |
| Marker | Existing marker number, observation time/frame, session context. |
| Player | Spawn/reset/generation, movement-mode transitions, impact request/accept/confirm/apply, relevant reconcile decisions and before/after values. |
| Item | Pickup/hold/release/settle/remove transitions; release identity; simulator changes; impact detection/reporting; accepted and rejected relevant motion/lifecycle updates. |
| Presentation | Sampled body pose, displayed graphics pose, camera pose, movement state, and focused item state. |
| Screenshot | Request plus saved, skipped, or failed outcome linked by capture ID. |

Capture all Unity log types through `Application.logMessageReceivedThreaded`. Forward existing `Log.Info/Warning/Error/Exception` output through that callback once; do not also forward each wrapper directly. Structured `AILogger` events write only to their files. Unity's threaded callback can run concurrently and must avoid main-thread-only Unity APIs. [Unity callback contract](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Application-logMessageReceivedThreaded.html).

`Log.Marker()` keeps its current controls and `MARKER (n)` console output and emits one canonical `marker` event using the same number. Its console echo can also appear as `unity.log`; it is not another marker or another capture trigger.

Use these initial sampling constants:

| Mode | Capture |
| --- | --- |
| Baseline while in game | At most 2 samples/second of the local player, camera, and focused entities. |
| Burst | At most 10 samples/second for 5 seconds following a marker, screenshot request, relevant impact, or existing movement anomaly detection. |
| Focus bound | Local player plus at most 7 related entities, selected from current target, held/thrown item, and recent impact participants. |

Write baseline samples directly to the same stream. This supplies limited pre-event history without a second ring buffer or delayed record ordering. A burst temporarily replaces the baseline cadence. Overlapping triggers extend one deadline; they do not start parallel samplers. Generic repeated console errors do not repeatedly extend a burst.

An immediate trigger sample is useful, but only the screenshot-associated sample claims the captured render frame. Ordinary state records identify their sampling phase and actual frame/ticks. Body state is the most recent simulation state; graphics and camera state describe the available presentation state at sampling. They are not interchangeable.

For player/item captures include body and displayed pose, linear velocity, movement mode, grounded state, ownership/simulator, applicable revisions, focused entity IDs, and camera pose/FOV. Include gameplay-input enablement and meaningful input edges; do not log all raw device activity. Inspect the existing UI state flags needed to explain an open inventory, console, or session panel.

Cache references when the session/local player binds and when ownership, target, or entity lifetime changes. Use the existing session component to host one coroutine with real-time waits. Stop it outside gameplay. Add direct event calls at state changes; do not poll every object or run a logger `Update` method.

State sampling cannot reconstruct every collision or every rendered frame. Record exact impact application and reconcile values at the event boundary so a short impulse is not dependent on a 10 Hz sample catching it. Purely visual defects remain dependent on a screenshot or a focused future capture.

## Separate screenshot action

Bind the screenshot action to the `[` keyboard key, independently of markers. This action is keyboard-only and belongs to the existing development input service.

1. Emit `screenshot.requested` immediately with a run-scoped increasing `capture_id` and request frame/time. Request a state burst without emitting a marker.
2. Capture the next available completed game frame, including UI, on the main thread. Record a state sample with the same capture ID and actual capture frame/time.
3. Save `screenshots/shot-<capture_id>-f<frame>.png` inside this run's directory.
4. Emit `screenshot.saved` only after the PNG write succeeds. Include `capture_id`, `file_name`, run-relative `path`, `absolute_path`, `capture_frame`, `capture_t_us`, dimensions, and byte count. The outer event time is completion time; it must not replace capture time.
5. Emit `screenshot.failed` or `screenshot.skipped` with a reason if saving is unavailable, fails, or is throttled. A request alone is never evidence of a saved image.

The relative path is authoritative when a folder is copied to another machine; the absolute path is a convenience on the originating machine. Paths are generated by the recorder, not supplied by gameplay payloads.

Capture at normal game resolution with no supersampling, at most one pending image, and no more than one accepted screenshot per second. Cap a PNG at 16 MiB and record an explicit skipped outcome if it exceeds the cap. Texture capture/encoding stays on the main thread initially; file writing uses the writer worker. Destroy temporary textures after copying their bytes. Manual capture may cause a short hitch; exclude its work from claims about ordinary logging overhead.

Use the actual rendered game view. Capture after rendering as required by `CaptureScreenshotAsTexture`; do not reuse the validation screenshot routine that redirects cameras/UI and can quit the application. [Unity screenshot timing](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/ScreenCapture.CaptureScreenshotAsTexture.html).

In batch/headless mode, record screenshots as unavailable. In the Editor, an inactive Game view can delay an end-of-frame coroutine; preserve the actual capture timestamp and finish pending requests as skipped on shutdown. [Unity end-of-frame behavior](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/WaitForEndOfFrame.html).

## Storage, lifecycle, and failure behavior

Editor output: `<project>/Logs/AI/<UTC-start>-<run-id>/`. Development-player output: `<Application.persistentDataPath>/AILogs/<UTC-start>-<run-id>/`. Allow only an absolute `-aiLogDir <directory>` output override initially. Resolve and cache paths at startup. Unity defines the player data directory by platform. [Unity persistent data path](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Application-persistentDataPath.html). All AI logger code and gameplay call sites are development-only.

```text
<run>/
  run.json
  run.lock
  events-000001.jsonl
  events-000002.jsonl
  screenshots/
    shot-3-f1094.png
  summary.json
```

`run.json` is immutable metadata: schema, run ID, UTC start, process ID, Editor/development mode, Unity/game version, build GUID when available, game protocol, transport configuration, output root, sampling limits, and instrumented event families. Record additional scene, role, and connection metadata as they change. Do not fabricate a source revision if the build does not supply one.

Use one bounded FIFO and one background file writer. Assign sequence numbers atomically with queue admission. Serialize/freeze caller payloads before admission; the writer must not read Unity objects. The worker wakes for work and flush deadlines, rather than requiring a main-thread polling loop.

| Limit | Initial policy |
| --- | --- |
| Pending records | 4,096 records and an 8 MiB serialized-payload budget, plus one separately bounded PNG. |
| Reserved capacity | Reserve 64 record slots and 256 KiB of that budget for errors, markers, and recorder/capture outcomes. Other records cannot consume it. |
| Record size | Maximum 64 KiB encoded JSON; truncate long messages/stacks with original lengths. Replace other oversized payloads with an explicit truncation record. Never cut JSON bytes mid-record. |
| Log retention | Rotate at 16 MiB; retain the newest 8 segments per run. |
| Screenshot retention | Retain newest images within a separate 64 MiB/run budget; record artifact eviction. |
| Run retention | On startup, retain the newest 5 inactive runs plus any active runs. Clean only recorder-owned directories under the selected output root. |
| Flush | Target a maximum one-second interval when data is pending; request an early worker flush for errors, markers, screenshots, and shutdown. Stalled I/O can exceed this target. |

Under pressure, skip state samples first and drop incoming noncritical records when their budget is exhausted. Reserved capacity reduces critical loss but cannot guarantee it. Maintain loss counters outside the queue and have the writer emit `logger.dropped` when it can, with count, event class, reason, and time interval. Repeat cumulative loss counters in segment headers and the final summary. Distinguish deliberately sampled/filtered data from queue loss.

Each segment starts with its index, first retained sequence/time, and cumulative loss/retention information. Never recycle segment numbers. Deletion of old segments is expected retention, not proof that earlier events never occurred. Keep `run.json` outside rotation. Screenshots and event references can be evicted independently; readers must report missing artifacts.

Hold `run.lock` with an exclusive OS file handle for the writer's lifetime so simultaneous Editor/player processes cannot prune an active run. A crashed process releases its handle; a leftover filename is not evidence of activity. Never touch another process's active output.

Initialize before scene load and reset static state for every Play session. Unsubscribe and close on Play exit, application quit, and Editor assembly reload. Handle disabled domain reload explicitly using `SubsystemRegistration` and Editor shutdown hooks. [Unity static-state lifecycle](https://docs.unity3d.com/6000.0/Documentation/Manual/domain-reloading.html).

Shutdown stops producers, completes pending capture outcomes, drains the writer with a two-second maximum wait, and writes `summary.json` on clean completion. The summary contains final sequence, retained range, counts, losses, and screenshot outcomes; it is an index, not a diagnosis. Each worker owns its run state so a late shutdown cannot write into a later run.

A crash can lose queued/unflushed records and leave an incomplete final line. A missing summary means closure is unknown. Readers may ignore an incomplete trailing line while reporting it; malformed complete records are errors to surface. A flush does not guarantee survival of an OS or power failure.

Disk/permission failure disables file recording for that run without interrupting gameplay or retrying every frame. Report the failure once through Unity with a recursion guard; do not claim that a failed file writer successfully recorded its own failure. Serialization failures become small recorder-error records when the sink remains usable.

## Agent reading contract

The agent needs only filesystem access and an ordinary JSON parser:

1. Read `run.json`, any final summary, and segment headers to establish build, coverage, retained time range, and missing data.
2. Find the user's marker or `screenshot.saved` event and inspect its linked image.
3. Read a bounded time window around it; include related subject/operation records outside that window when needed.
4. Compare requested, accepted, applied, reconciled, and displayed state. Pair supplied client folders using existing network identities.
5. Cite evidence by run, segment filename, and sequence, and screenshots by path. Separate observations from inferred causes.

Use `rg` to narrow files, then parse complete JSON records for exact filtering. Stream files rather than loading entire runs. File timestamps and alphabetical run order do not establish which peer is the user's view. No shared mutable `latest.json` is needed.

Logging provides evidence only where instrumentation and retention cover the question. A missing event is not proof of a missing gameplay action if that event family was not instrumented, the interval was evicted, or records were lost.
