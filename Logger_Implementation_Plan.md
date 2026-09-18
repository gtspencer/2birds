# Scoped logger implementation plan

## 1. Objective and delivery boundary

Build a small, project-owned logger with explicit message scopes, automatic build defaults, Unity Console output, and reliable, bounded file output combining game messages and captured Unity/third-party messages. The first implementation includes the Unity capture adapter and duplicate prevention, without migrating gameplay call sites or enabling the service automatically.

This document is the implementation specification. It does not authorize implementation as part of the planning task.

Decisions and assumptions:

- Scope design: an explicit category such as `Networking.Session`, `Items.Throw`, or `Birds.Spawning`, supplied with each message. This is the selected API; ambient/nested `BeginScope` blocks are outside the initial scope.
- Production means a standalone player with Development Build disabled. An Editor running with a release build target still uses Editor defaults.
- Each local process writes its own logs. Logs are not replicated through FishNet or uploaded anywhere.
- The first supported target is the Windows/Steam game. Do not add WebGL/mobile worker alternatives.
- File output and Unity Console output are enabled by default when the service is explicitly initialized.
- Explicit initialization attaches Unity log capture when file output is enabled; shutdown detaches it. Merely loading the code does not activate capture.
- Leave the global Unity handler and Steam/FishNet logging calls/settings unchanged. Their messages enter our file only when emitted through Unity while capture is active.
- Unity retains ownership of `Player.log`/`Editor.log`; the service owns separate structured files beneath `Logs/<session>/`.
- There are no scene, prefab, component, ScriptableObject, package, or `.meta` changes required.

Use a static `GameLog` facade, two concrete output implementations, one Unity capture adapter, and ordinary .NET threading/I/O. Avoid a logging framework dependency, dependency-injection container, generic sink/plugin system, or custom Editor window.

## 2. Project placement and boundaries

Follow the existing `TwoBirds` namespace, brace style, and private-field conventions. Add runtime files below `Assets/Game/Runtime/Diagnostics/Logging/`. Keep them in the existing default script assembly; do not introduce an assembly definition or restructure game assemblies for this feature.

Relevant existing boundaries:

| File | Implementation constraint |
| --- | --- |
| `Assets/Game/Runtime/Diagnostics/DevConsole.cs` | Command UI with its own output labels. Do not replace its output method or add commands/subscriptions in the foundation implementation. |
| `Assets/Game/Runtime/Diagnostics/MvpValidation.cs` | Separate development diagnostics. Do not migrate its logging or output files. |
| `Assets/Game/Runtime/Networking/SessionBootstrap.cs` | Session bootstrap, not logger ownership. Leave it unchanged. |
| `Assets/Game/Editor/SteamDevelopmentBuild.cs` | Build postprocessing. Logger defaults must not require modifications here. |
| `Packages/manifest.json` | Do not add a logging or JSON package. |

The logger itself must compile into release players. Do not wrap the logger in the `UNITY_EDITOR || DEVELOPMENT_BUILD` guard used by development-only diagnostics. Do not replace `Debug.unityLogger.logHandler`, alter global Unity log filtering, or modify FishNet/Steam logging settings.

Global-handler wrapping is outside this implementation. Consider it separately only if pre-output filtering or rewriting of third-party Unity messages becomes a requirement; combined file capture does not require it.

## 3. Levels and filtering contract

Define ordered `GameLogLevel` values: `Trace`, `Debug`, `Info`, `Warning`, `Error`, `Fatal`, `Off`. `Off` is a threshold only, never an emitted severity. `Fatal` records severity; it does not quit the application.

| Execution environment | Default minimum | Messages admitted by default |
| --- | --- | --- |
| Editor Play mode | `Info` | Info, Warning, Error, Fatal |
| Development player | `Info` | Info, Warning, Error, Fatal |
| Non-development player | `Error` | Error, Fatal |

These thresholds govern direct `GameLog` messages for both of our sinks and captured Unity messages for our file sink only. Capture happens after Unity processes the original message; it cannot suppress the original Console or `Player.log` output. For example, a third-party Warning may remain in Unity's log while being excluded from our production JSONL file. `ConsoleEnabled` controls our forwarding, not third-party output.

Resolve the environment once during explicit initialization on the main thread: use `UNITY_EDITOR` to identify the Editor, otherwise use the cached value of `UnityEngine.Debug.isDebugBuild`. Unity documents this value as true for development players and always true in the Editor. Do not use an attached debugger, Steam connectivity, or the .NET `DEBUG` symbol to classify production. See [Unity's build flag documentation](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Debug-isDebugBuild.html).

Allow these programmatic controls:

- Optional global minimum override; clearing it restores the automatic build default.
- Per-scope minimum overrides, including `Off` to mute a scope.
- Independent enable/disable choices for console and file output at initialization.

Filtering precedence is the longest matching scope override, then the explicit global override, then the build default. Scope overrides replace the inherited threshold; they are not clamped by the global threshold. This allows temporarily enabling `Debug` for `Networking` in a release build without enabling it everywhere.

Scope matching is ordinal and case-sensitive. `Networking` matches itself and `Networking.Session`, but not `NetworkingExtra`. Walk dot-separated ancestors; do not implement wildcards, regexes, or an unbounded scope cache. A more specific override wins even when its value is `Off`.

Normalize a null/empty/whitespace scope to `General`, otherwise trim it. Cap it at 128 UTF-16 code units without splitting surrogate pairs. Use the same normalization for filters and entries. Keep object IDs, player IDs, and changing values in messages rather than category names. A null message becomes an empty string.

Capture the effective policy when admitting a record. A later threshold change affects subsequent calls, not records already queued. Publish filter changes as immutable snapshots using a lock or atomic reference replacement; never mutate a dictionary while producer threads read it.

Filtering happens before our timestamps, entry allocation, exception rendering, and queue work. Captured Unity messages have already incurred their original formatting/output costs. Do not compile out low-level calls with `[Conditional]`: production overrides must still be able to enable them.

## 4. Public API

Use these signatures as the API contract; convenience methods all delegate to `Write`.

```csharp
public static class GameLog
{
    public static void Initialize(GameLogOptions options = null);
    public static bool IsEnabled(GameLogLevel level, string scope);
    public static void Write(GameLogLevel level, string scope, string message,
        Exception exception = null);

    public static void Trace(string scope, string message);
    public static void Debug(string scope, string message);
    public static void Info(string scope, string message);
    public static void Warning(string scope, string message);
    public static void Error(string scope, string message, Exception exception = null);
    public static void Fatal(string scope, string message, Exception exception = null);

    public static void SetMinimumLevel(GameLogLevel? minimum);
    public static void SetScopeMinimum(string scope, GameLogLevel minimum);
    public static void RemoveScopeMinimum(string scope);
    public static GameLogStatus GetStatus();
    public static GameLogFlushResult Flush(TimeSpan timeout);
    public static GameLogFlushResult Shutdown(TimeSpan timeout);
}
```

Example future use; do not insert these calls into gameplay during foundation work:

```csharp
GameLog.Info("Networking.Session", "Joining lobby.");
GameLog.Error("Items.Throw", "Failed to apply throw.", exception);
GameLog.SetScopeMinimum("Networking", GameLogLevel.Debug);

if (GameLog.IsEnabled(GameLogLevel.Debug, "Birds.Spawning"))
    GameLog.Debug("Birds.Spawning", $"Candidates: {BuildCandidateSummary()}");
```

The explicit guard is required for expensive interpolation: C# evaluates arguments before calling the logger. Do not add custom interpolated-string handlers or lazy delegate overloads initially.

`GameLogOptions` is a plain configuration object, copied at initialization. Public choices are nullable global minimum, initial scope overrides, `ConsoleEnabled`, `FileEnabled`, and an optional absolute `DirectoryPath`. Unity capture follows `FileEnabled`; it is a core behavior rather than a separate opt-in feature. No live disk configuration, PlayerPrefs, Inspector asset, or command-line parser is needed. Limit override entries to 128 to keep policy storage bounded.

`GameLogStatus` is an immutable snapshot: lifecycle state, resolved build kind/default/global override, file state (`Disabled`, `Opening`, `Ready`, `Failed`), whether Unity capture is attached, current file path, generation/session ID, pending counts, per-sink drop counts by severity, truncated-entry count, calls rejected outside a running session, and the latest file failure summary. Do not expose mutable queues or write a log whenever status is read.

`GameLogFlushResult` distinguishes `Completed`, `CompletedWithLoss`, `TimedOut`, and `Unavailable`. Loss includes file-queue drops or truncation during the current generation. A timeout never means the remaining entries reached disk. Console delivery is not part of the file-flush guarantee.

`Initialize` and `Shutdown` are main-thread lifecycle operations. Logging, filtering, status, policy updates, and `Flush` are callable from ordinary managed threads. No Burst/job logging support is implied. Invalid configuration may throw a clear argument exception at initialization; runtime output failures must not escape from log calls into gameplay.

## 5. Record and output formats

Create one immutable entry snapshot after filtering. It contains only strings, enums, and primitive values: no retained `UnityEngine.Object`, GameObject, exception object, scene object, or mutable dictionary.

Fields for each JSON Lines log record:

| Field | Contract |
| --- | --- |
| `schemaVersion` | Integer `1`. |
| `kind` | `log`, `session`, or `diagnostic`. |
| `source` | `GameLog` for direct calls or `Unity` for captured messages. |
| `timestampUtc` | UTC ISO-8601 round-trip timestamp using invariant culture. |
| `elapsedMs` | Monotonic milliseconds from initialization, from `Stopwatch`. |
| `sequence` | Increasing per-generation admission order for log records. |
| `sessionId` | New GUID for each initialization/Play session. |
| `threadId` | Producer's managed thread ID, not the writer thread ID. |
| `level` | Severity name for log records. |
| `scope` | Normalized explicit category. |
| `message` | Message text. |
| `exception` | Rendered exception text, including inner exceptions and existing stack, or empty string. |
| `stackTrace` | Stack text supplied by the Unity callback, or empty string for direct calls. |
| `truncated` | True if any retained message/exception text was shortened. |

Snapshot `exception.ToString()` on the caller after filtering; do not manufacture a new stack for every Info/Debug call. For captured Unity messages, retain the supplied condition as `message`, supplied stack as `stackTrace`, and leave `exception` empty; do not fabricate an exception object. Bound retained message plus exception/stack text to 16,384 UTF-16 code units, with up to half reserved for exception/stack text when present. Mark truncation and do not split surrogate pairs. Handle a throwing custom exception formatter with a short fallback containing the exception type.

Use UTF-8 without BOM, one compact JSON object plus `\n` per physical line. JSON escaping must preserve embedded line breaks as escaped text, so messages cannot create fake records. Serialize a private `[Serializable]` DTO with public primitive/string fields using `JsonUtility.ToJson(dto, false)`. This avoids another dependency and a hand-written JSON encoder. Unity explicitly permits this API on background threads provided the input is not mutated during serialization. See [JsonUtility.ToJson](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/JsonUtility.ToJson.html).

Example log line, with abbreviated timestamp for readability only:

```json
{"schemaVersion":1,"kind":"log","source":"GameLog","timestampUtc":"2026-09-17T14:30:00.0000000Z","elapsedMs":1250,"sequence":12,"sessionId":"abc123","threadId":1,"level":"Info","scope":"Networking.Session","message":"Joining lobby.","exception":"","stackTrace":"","truncated":false}
```

Write a `session` metadata record at the start of every segment: actual session ID, segment number, UTC start, application version, Unity version, build GUID, build kind, process ID, automatic/effective global minimum, and initial scope overrides represented as an array. These values are cached during initialization. Headers are metadata, not Info messages, and must exist even in an Error-only release file. A rotation header reflects current policy. Record later policy changes as small `diagnostic` records so raised/lowered thresholds can be understood.

Console text starts with `[Info] [Networking.Session] Joining lobby.`, includes the emission marker specified in section 9, and is followed by exception text when present. The marker is Console/Unity-log transport metadata and is not added to the structured file's message. Map Trace/Debug/Info to Unity Log, Warning to Unity Warning, and Error/Fatal to Unity Error. Use an alias for `UnityEngine.Debug` if necessary. Do not call `Debug.LogException` on a fabricated exception or globally change stack-trace settings. Deferred console output has a logger dispatch call site; exact double-click navigation to the original caller is not a foundation requirement.

## 6. Threading, admission, and overload behavior

Use one service instance per initialization generation, one bounded file queue, one bounded console queue, one background file thread, and a cached main-thread `SynchronizationContext`. No MonoBehaviour or per-frame polling loop is required.

There are two entry paths into the same service and file queue: direct `GameLog` calls target file plus console, and captured Unity messages target file only. Use an internal admission method with explicit routing; the capture callback must not invoke the public console-forwarding path.

On an accepted call or captured message:

1. Read the current running service and policy; reject filtered messages immediately.
2. Snapshot bounded text and metadata outside the queue lock.
3. Under a short admission lock, recheck that this generation is still running, allocate the sequence, and attempt each enabled, targeted sink's queue independently. Direct and captured entries share the same file sequence/order.
4. Release the lock, signal the file worker, and schedule a console drain if needed.

No file I/O, serialization, Unity calls, exception formatting, or waiting on another thread while holding the admission lock. Concurrent calls are ordered by admission, not their wall-clock timestamps. A slow disk must not block console delivery.

Initial limits are named internal constants, not a large user configuration surface:

| Limit | Initial value |
| --- | --- |
| File queue | 4,096 entries and 8 MiB retained text payload |
| Console queue | 512 entries and 1 MiB retained text payload |
| Critical reserve | Last 10% of each count/byte budget reserved for Error/Fatal |
| File worker batch | At most 128 entries outside the queue at once |
| Console drain | At most 64 entries per posted callback |
| Normal file flush interval | 1 second while dirty |
| Shutdown wait budget | 2 seconds by default at future lifecycle call sites |

Count UTF-16 retained text bytes consistently, plus a fixed per-entry overhead estimate. Also bound in-flight batches using the entry-count/text limits; queue limits alone must not hide an unlimited local batch. Shared immutable strings may be conservatively counted separately for each sink.

Reject new sub-Error entries when they would enter the critical reserve. Error/Fatal entries may use the reserve, but must also be rejected if the hard count or byte cap would be exceeded. Do not block the simulation, evict older records, or synchronously write to disk on overflow. An unlimited error storm cannot be lossless with bounded memory and non-blocking callers.

Increment per-sink/per-level drop counters. Each sink emits a compact, rate-limited `diagnostic` summary of accumulated drops when it can next make progress, at most once per second; file summaries bypass the public facade and do not re-enter its queue. Console loss summaries obey the console's own bounded scheduling. Counters remain queryable if a sink cannot recover. Infrastructure diagnostics are metadata, not user messages bypassing severity filtering.

The file thread is a background thread, signaled with an event. Drain bounded batches and use timed waits only when buffered data needs its next flush. When idle and clean, wait for a signal indefinitely. Check flush deadlines between batches so continuous traffic cannot postpone flushing forever.

Console draining uses `SynchronizationContext.Post`, never `Send`. Keep at most one outstanding drain per generation; drain a bounded batch, then repost only if entries remain. Protect the empty-check and scheduled-flag transition with the queue lock to avoid a lost wake-up. No Unity API is invoked while holding that lock. Posted callbacks capture their service instance and become no-ops after it closes; they must never use a newer generation's queues. If initialization has no usable main-thread context, mark console output unavailable and keep file output functional.

## 7. File ownership, rotation, and retention

The default base directory is `Path.Combine(Application.persistentDataPath, "Logs")`, resolved on the main thread. Use the actual Unity path, not a hard-coded user/company directory. Windows usually places this beneath LocalLow. See [Application.persistentDataPath](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Application-persistentDataPath.html).

Keep these files separate from Unity's own logs. With default Windows player paths, the layout is:

```text
AppData/LocalLow/<Company>/<Product>/
    Player.log
    Logs/
        twobirds-<timestamp>-p<pid>-<guid>/
            0001.jsonl
            0002.jsonl
```

Forwarded `GameLog` messages also reach Unity's own log through Unity's output mechanism. This intentional presence in two different outputs is not a duplicate within either output. Never open, append to, rotate, or delete `Player.log`/`Editor.log` ourselves. Command-line overrides can move Unity's log independently, and the Editor uses its own log location. The JSONL files retain our structured fields, filtering, and retention; Unity's files retain its original diagnostics. See [Unity log-file locations and output behavior](https://docs.unity3d.com/6000.0/Documentation/Manual/log-files.html).

Create a unique session subdirectory such as `twobirds-20260917T143000000Z-p12345-<guid>`, containing `0001.jsonl`, `0002.jsonl`, and so on. Use exclusive creation and never append a new process/session into an existing segment. Editor and multiple local clients must be able to run simultaneously.

The file worker exclusively owns directory creation, stream opening, writing, rotation, flushing, retention, and closing. Open the active segment with read sharing so a person can tail it while the game runs. Never let the main thread dispose a stream while its worker may still be using it.

Rotate before writing a record that would exceed 10 MiB, counting actual UTF-8 bytes including newline and header. Encode each record once. The bounded input size keeps individual encoded entries below a segment limit; if a malformed/oversized metadata record would exceed it, shorten that record and mark it rather than repeatedly rotating an empty file.

Keep at most 10 segments for the current session, deleting its oldest closed segments during rotation. Retain completed session directories for at most 7 days and at most 100 MiB in total; delete oldest completed sessions first until both conditions hold. These are defaults/internal constants. Retention intentionally removes old logs and is distinct from queue-drop accounting.

For concurrent-process safety:

- Hold an exclusive `active.lock` file in the session directory for the worker's lifetime. A process crash releases the OS handle even if the file remains.
- Retention only considers directories matching the logger's exact session naming scheme under the configured log base. It must never recursively clean an arbitrary supplied directory or unrelated files.
- A cleaner must acquire the candidate session's lease exclusively before treating it as inactive. A lease cannot be acquired for a live session; skip it.
- Serialize cleanup with a short-lived exclusive base-directory cleanup lock. If another process holds it, skip this cleanup pass. Avoid waiting on cleanup locks.
- Hold the candidate lease during deletion of its segments, then close it and remove the marker/directory. Never delete another live session or its active segment.

Run retention on worker startup and rotation. If deletion fails, preserve the affected files and continue logging. Active sessions are exempt from the completed-session budget, so total disk usage can exceed 100 MiB while several instances are active; each active instance still has the 10-segment bound. Files locked by external tools may also prevent the target budget from being reached. Expose cleanup failures through status/one diagnostic rather than retrying every frame.

## 8. Flush, failures, and lifecycle

### File failure behavior

If directory creation, open, write, rotation, or flush fails, mark file output `Failed`, save a bounded failure summary, account for unwritten queued records, and close what the worker can close. Leave console output available. Report the failure once to the console using its internal path, not `GameLog.Error`. If console is disabled/unavailable, status is the diagnostic surface. Do not invent a second fallback directory or retry indefinitely; recovery is a new explicitly initialized session after shutdown.

Catch expected I/O, access, and serialization failures at the sink boundary, with a final worker boundary that prevents ordinary output exceptions escaping the thread. Do not claim recovery from out-of-memory, stack overflow, or process termination. Retention deletion failures are nonfatal and must not disable a healthy writer.

### Flush semantics

`Flush(timeout)` captures the highest file-admitted sequence under the admission lock and requests a worker flush through that sequence. The control request is outside the data-queue capacity, so a saturated queue cannot drop it. Coalesce concurrent requests by the highest target and wake all waiters when their target is flushed or the sink fails. Use monotonic deadlines and bounded waits.

The worker writes all admitted records through the target, flushes its managed buffer, then publishes completion. Requests do not require the queue to become empty or wait for later records. Never wait while holding the admission lock, and do not wait for console callbacks from `Flush` or `Shutdown`. Prevent self-wait if a lifecycle/control method is accidentally invoked on the writer thread.

Error/Fatal admission requests an expedited worker flush after the corresponding batch; it is not synchronous durability. Normal managed-buffer flushing makes data available to the OS. An explicit flush/shutdown may request `FileStream.Flush(true)` on the worker after flushing text buffers; caller timeouts still apply. No option promises survival of a forced kill, native crash, power loss, or permanently blocked filesystem.

### Lifecycle contract for the foundation

States are `Uninitialized`, `Running`, `Stopping`, and `Stopped`. File state is separate, so file failure does not stop the console. Initialization caches application metadata, build classification, main-thread context, and the absolute base path, publishes a ready-to-admit service, attaches its Unity capture adapter once if file output is enabled, then starts the worker. Disk readiness may complete asynchronously. Initialization failure must detach any installed subscription and clean up owned resources. Capture begins when the subscription is attached; there is no replay of earlier Unity messages.

Repeated initialization while running is idempotent and does not replace settings, open another file, or start another thread. Copy the caller's options so later mutation cannot change the active service. After a successful shutdown, explicit initialization starts a fresh generation with fresh defaults, counters, filters, and session ID.

Before initialization and after shutdown, writes are dropped and counted; `IsEnabled` is false. They must not start a worker or create files from a static constructor or arbitrary producer thread. Policy mutation while not running has no effect; initial policy belongs in `Initialize` options. An in-flight write must recheck its generation under the admission lock before enqueueing.

`Shutdown(timeout)` first stops admission, detaches this generation's Unity capture adapter, drains pending console entries on the main thread within the same deadline, and asks the worker to drain, flush, and close. In-flight capture callbacks retain their original service and must recheck its state before admission. Report any discarded console entries. Wait for the worker only within the remaining deadline. On timeout, return `TimedOut`; the background worker retains its resources and eventually closes them. Do not abort the thread or dispose its event/stream from another thread. Remain `Stopping` until it exits, and reject reinitialization during that interval to prevent accumulating abandoned workers.

### Future automatic activation, separate from foundation delivery

When integration is requested, add runtime callbacks to reset stale state at `SubsystemRegistration` and initialize at `BeforeSceneLoad`. No scene component is necessary. Other runtime initializer ordering at the same phase is unspecified; do not promise capture before initialization completes.

Subscribe once to `Application.quitting`. Add an Editor-only lifecycle adapter for `EditorApplication.playModeStateChanged` (`ExitingPlayMode`), `AssemblyReloadEvents.beforeAssemblyReload`, and Editor shutdown. These invoke bounded shutdown and unsubscribe deterministically. Reset static state and subscriptions between Play sessions even when domain reload is disabled. Unity requires explicit handling of persistent static state/event subscriptions in this mode; see [domain reloading](https://docs.unity3d.com/6000.0/Documentation/Manual/domain-reloading.html).

Do not add runtime initialization attributes, Editor initialization attributes, quitting subscriptions, or automatic gameplay integration during foundation delivery. Lifecycle entry points must remain manually callable until activation is requested. The Unity log subscription created by an explicit `Initialize` call is part of foundation delivery; automatic startup/shutdown hooks are the deferred work. Whoever explicitly initializes the foundation service must explicitly shut it down.

## 9. Core Unity log capture and duplicate prevention

Implement `GameLogUnityCapture` in the foundation alongside both sinks. Explicit initialization attaches it when file output is enabled. It subscribes only to `Application.logMessageReceivedThreaded`, which includes main-thread messages and can run concurrently on other threads. Do not additionally subscribe to `Application.logMessageReceived`. See [Unity's threaded log callback](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Application-logMessageReceivedThreaded.html).

Capture applies to existing `Debug.Log` calls and Steam/FishNet/engine messages delivered through that callback while attached. It does not recover messages suppressed by their producer, emitted before initialization, or sent to native/SDK outputs outside Unity's callback. Do not claim complete crash capture. No third-party source modifications or global-handler replacement are needed.

### Capture routing and mapping

- Map Unity Log to Info, Warning to Warning, and Error/Assert/Exception to Error. Preserve callback message and stack strings using the record contract and size limits.
- Set `source=Unity` and scope `Unity`. The callback does not reliably identify the originating library; do not guess `Steam`/`FishNet` from text or inspect stacks to assign categories.
- Apply the same global/build defaults and the `Unity` scope override before allocating an entry. `Unity=Off` mutes external messages in our file without altering their original Unity output.
- Admit to the shared file queue only; never forward a captured message to the console or invoke `Debug.Log` from the callback.
- Return promptly when file output is disabled/failed or the service is stopping. Use the same bounded queue, critical reserve, counters, and flush semantics as direct calls.
- Retain the owning service generation, not a lookup of the current global service. Retain no Unity objects and access no main-thread-only Unity API from the callback.

### Preventing our own Console output from re-entering the file

Direct `GameLog` entries already enter the file queue with their original scope and level. Their later Unity Console echo must be discarded by capture even if the original file admission was dropped, or filters changed before Console dispatch. Do not use that echo as a second attempt at file admission.

Use a recognizable Console-only marker, for example `[GameLog:<full-session-guid>:<emission-id>]`, on every Console emission from the service, including file-failure and drop diagnostics. Allocate monotonically increasing emission IDs separately from log sequences because internal diagnostics also need markers. Publish the ID before calling Unity. Place the marker at a fixed position after the level/scope prefix so capture can parse it without searching arbitrary message contents.

The capture adapter skips markers matching its full session GUID and an issued positive emission ID. This does not need an ever-growing set of emitted messages: a generation's highest issued emission ID is sufficient. It also tolerates a callback delivered after the forwarding call returns. A marker from another generation is not automatically suppressed. Exact session/marker matching is for echo identification, not a security boundary.

Never deduplicate unmarked messages by text, timestamp, or stack trace. Two genuine identical warnings must become two records. Add a thread-local capture reentrancy guard released in `finally`; it must not suppress simultaneous callbacks on other threads. Echo markers remain necessary because a call-stack guard alone does not cover deferred echoes. Unexpected adapter failures update bounded status/counters without emitting a Unity log from inside the callback.

Repeated initialization cannot attach duplicate handlers. Shutdown detaches the exact delegate once; callbacks already in flight reject admission after the owning generation stops. Reinitialization creates a fresh delegate/session marker and cannot route old entries into the new file.

## 10. File-by-file implementation sequence

Keep closely related internal DTOs/enums in their owning files; do not create a file or interface for every small concept.

| Order | Proposed file | Responsibility |
| --- | --- | --- |
| 1 | `GameLogLevel.cs` | Ordered severities and threshold semantics. |
| 2 | `GameLogOptions.cs` | Public initialization options and immutable copied policy/default resolution helpers. |
| 3 | `GameLogEntry.cs` | Bounded immutable entry, internal serialization DTO, console formatting. |
| 4 | `GameLogService.cs` | Generation state, policy snapshots, admission lock, status/counters, flush coordination. |
| 5 | `GameLogFileWriter.cs` | Bounded file queue/worker, JSONL, session leases, rotation, retention, stream ownership. |
| 6 | `GameLogConsoleWriter.cs` | Bounded console queue, coalesced main-thread posting, Unity severity mapping. |
| 7 | `GameLogUnityCapture.cs` | Threaded Unity callback, external severity/scope mapping, file-only admission, emission-marker exclusion, subscription ownership. |
| 8 | `GameLog.cs` | Static facade, initialization metadata capture, convenience methods, explicit capture attachment and shutdown. |

Implement in this order:

1. Define the API, level policy, scope normalization, immutable options, and uninitialized behavior.
2. Implement bounded entry creation and the JSON/console formats without adding game call sites.
3. Implement the file worker, admission limits, drop counters, sequence barriers, and failure state.
4. Add unique session ownership, rotation, retention, and bounded shutdown.
5. Add independent console dispatch and emission markers for all Console output; keep the file path functional without a console context.
6. Implement Unity capture, shared file admission, mapping, duplicate prevention, and status reporting. Leave Steam/FishNet calls and the global Unity handler unchanged.
7. Complete the static facade and explicit lifecycle, including capture attachment/detachment. Keep automatic activation hooks absent.
8. Document API examples, separate Unity/custom file ownership, filtering boundaries, limits, and activation steps beside the implementation if implementation documentation is requested.

Do not build a temporary migration tool, generate scenes, add a demo component, or convert existing logs to demonstrate the feature. For future checks, drive the explicit API through the existing Unity CLI capabilities when available, falling back to Unity MCP if required.

## 11. Acceptance criteria for a future authorized validation pass

These are requirements for later verification, not instructions to run validation during this planning task or automatically during implementation. Follow the repository rule requiring an explicit validation request. Prefer focused behavior checks over tests that merely mirror private methods. If adding test assembly infrastructure becomes necessary, address that separately rather than restructuring the runtime assembly for this logger.

| Area | Required observable outcome |
| --- | --- |
| Build defaults | Editor and development player admit Info+; release admits Error/Fatal. Warning is absent from the custom release file by default. |
| Unity capture | A third-party-style Unity log enters the same JSONL file with scope/source `Unity`; existing Steam/FishNet messages that reach Unity are captured without modifying those packages. |
| Filtering boundary | Captured Warning is absent from the release JSONL file but its original Unity output is unaffected. `Unity=Off` mutes only capture; direct scoped entries retain their own policy. |
| Exceptions | A Unity exception/assert is recorded as Error with its supplied message and stack; direct exceptions retain their rendered inner exceptions and stack. |
| Duplicate prevention | A direct log appears once in each enabled output, with its original scope/level in JSONL. Its Unity echo creates no second entry, including delayed echoes, changed filters, and original file-queue drops. |
| Repeated external messages | Two identical unmarked Unity messages produce two entries; concurrent callbacks are not suppressed by another thread's reentrancy guard. |
| Internal diagnostics | File failures and overflow summaries cannot recursively re-enter capture. All logger-owned Console emissions carry markers. |
| Overrides | `Networking=Debug` admits Debug only within that scope tree; child override wins; removing overrides restores inherited/build settings. |
| Scope identity | `NetworkingExtra` does not inherit `Networking`; case rules, whitespace fallback, and truncation are consistent. |
| Filtering cost | Filtered entries never enter queues or render exceptions; expensive message construction can be guarded with `IsEnabled`. |
| Output format | Quotes, backslashes, tabs, multiline exceptions, Unicode, and surrogate pairs yield independently parseable JSON lines. Truncation is visible. |
| File behavior | Records appear while running, tailing is possible, segments rotate at the byte limit, and metadata identifies each segment/session. Only our session files are managed; Unity retains ownership of its Player/Editor log. |
| Producer concurrency | Multiple producer threads preserve unique admission sequences and complete record boundaries without touching main-thread-only Unity APIs. |
| Sustained load | Count/byte budgets and in-flight batches stay bounded; the critical reserve works; continuous traffic does not starve flush deadlines. |
| Overload | Drops are counted/reported, file and console overload independently, and producers do not wait on filesystem I/O. |
| Flush races | Full queues cannot lose control requests; a flush completes through its captured target despite later traffic; timeout/loss/failure results remain distinct. |
| Faults | Unwritable directory, disk-full/write failure, and locked old segments produce the specified sink/status behavior without recursive logs or gameplay exceptions. |
| Multiple processes | Simultaneous clients create different sessions; retention cannot delete another client's active logs. |
| Lifecycle | Explicit initialize attaches capture once and shutdown detaches it. Repeated cycles cannot duplicate handlers; stale posts/capture callbacks cannot enter a new generation; shutdown races reject late admission; timed-out workers cannot be multiplied. |
| Future activation | Repeated Play sessions, disabled domain reload, and assembly reload do not duplicate subscriptions or leave file handles held after worker exit. |
| Foundation boundary | Merely compiling/loading the service creates no files, subscriptions, components, or workers. Existing gameplay remains unwired. |

After implementation is explicitly activated for a requested validation pass, the user should visually inspect the Unity Console, Unity's Player/Editor log, and an opened JSONL file. Confirm one direct scoped message and one third-party Unity message each appear once per intended output, with correct severity, readable exception stacks, and `Unity` scope on the captured entry. Check that production filtering excludes external Warning from JSONL without suppressing its original Unity output, and inspect the separate session paths and rotation behavior. Confirm the existing in-game dev console UI remains unchanged.
