# Logger implementation review

The implementation needs corrections before integration. The highest-priority issues prevent compilation or file initialization, allow flush/shutdown to report success without the promised writes, and undermine thread-safe lifecycle/filtering behavior.

## 1. [P1] Import the namespace containing `ThreadStaticAttribute`

Location: [GameLogUnityCapture.cs:13](/C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Diagnostics/Logging/GameLogUnityCapture.cs:13).

The file uses `[ThreadStatic]` but imports only `System.Threading` and `UnityEngine`. The attribute belongs to `System`; importing a child namespace does not import its parent. There is no project-wide global using supplying it. This produces a missing-type compiler error and blocks the runtime assembly, even though the logger is not automatically activated.

Add `using System;` or qualify the attribute as `[System.ThreadStatic]`. See Microsoft's [ThreadStaticAttribute definition](https://learn.microsoft.com/en-us/dotnet/api/system.threadstaticattribute?view=netframework-4.8.1).

## 2. [P1] Capture application metadata before starting the writer thread

Locations: [GameLogFileWriter.cs:95](/C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Diagnostics/Logging/GameLogFileWriter.cs:95), [GameLogFileWriter.cs:345](/C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Diagnostics/Logging/GameLogFileWriter.cs:345).

`WorkerLoop` opens the first segment and calls `WriteSessionHeader`, which reads `Application.version` and `Application.buildGUID` on the background thread. These getters are main-thread-bound Unity calls; unlike `unityVersion`, their native bindings are not marked thread-safe. This can fail at the first header, leaving file logging failed before any user entry is written. The same access recurs on rotation. Unity's [Application bindings](https://github.com/Unity-Technologies/UnityCsReference/blob/master/Runtime/Export/Application/Application.bindings.cs) distinguish these getters from the explicitly thread-safe `unityVersion` getter.

Read all application/build metadata during main-thread initialization and pass immutable values into the writer. Construct the service and capture adapter before starting the worker, following the plan's initialization order.

## 3. [P1] Base flush completion on successfully written records, not the requested target

Locations: [GameLogFileWriter.cs:421](/C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Diagnostics/Logging/GameLogFileWriter.cs:421), [GameLogFileWriter.cs:145](/C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Diagnostics/Logging/GameLogFileWriter.cs:145), [GameLogService.cs:329](/C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Diagnostics/Logging/GameLogService.cs:329).

`FlushToDisk` reads the latest `flushTargetSequence` and declares that target complete without tracking which sequence was actually written. For example, if a flush targets the 500th queued entry, an expedited flush after the first 128-entry batch acknowledges all 500 while the remainder is still queued. A second caller can also advance the target during an earlier physical flush and receive a false completion. The service additionally chooses its target from all attempted admissions, including entries rejected by the file queue.

The opposite failure occurs after an ordinary periodic flush with no explicit target: `flushedThroughSequence` is not advanced. A subsequent `Flush` on an empty, clean queue wakes the worker, but no batch is processed and no completion is published, so it times out even though its records were already flushed. Concurrent waiters also wait only once on a shared event instead of rechecking their target until its deadline.

Track the highest sequence actually accepted by the file queue, successfully written, and successfully flushed as separate values. Capture the accepted target under admission synchronization; publish only real flushed progress. Handle pending control requests even when no data batch exists, and wait in a deadline-bounded loop that rechecks progress/failure. Clear satisfied requests so every subsequent batch does not unnecessarily flush forever.

## 4. [P1] Make file failure terminal and propagate it to admission and waiters

Locations: [GameLogFileWriter.cs:384](/C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Diagnostics/Logging/GameLogFileWriter.cs:384), [GameLogFileWriter.cs:429](/C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Diagnostics/Logging/GameLogFileWriter.cs:429), [GameLogFileWriter.cs:177](/C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Diagnostics/Logging/GameLogFileWriter.cs:177), [GameLogService.cs:259](/C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Diagnostics/Logging/GameLogService.cs:259).

Write and flush exceptions set `Failed` but are swallowed, so the worker continues consuming entries and can still publish successful flush completion. A failed rotation returns from `WriteBatch` without accounting for the unwritten remainder. `WaitForShutdown` checks only whether the worker signaled completion, so a failed write/flush can return `Completed`. The outer worker catch does not account for pending/in-flight records or wake flush waiters with a failure result.

After an opening failure terminates the worker, both direct calls and capture continue enqueueing because admission never checks file state. Those records have no consumer and remain queued until the queue fills. The planned one-time Console failure diagnostic is also absent.

Use one terminal failure transition: stop file admission under appropriate synchronization, account for all unwritten entries including the current batch, wake waiters with `Unavailable`, close worker-owned resources, and emit one marked Console diagnostic through the independent console path. Keep direct Console logging functional. Never advance successful flush progress following an I/O exception.

## 5. [P1] Keep a timed-out service alive in `Stopping` until its worker exits

Locations: [GameLogService.cs:334](/C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Diagnostics/Logging/GameLogService.cs:334), [GameLog.cs:102](/C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Diagnostics/Logging/GameLog.cs:102), [GameLogConsoleWriter.cs:144](/C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Diagnostics/Logging/GameLogConsoleWriter.cs:144).

`Shutdown` always sets the service to `Stopped`, and the facade always clears its service reference, even when the writer wait returns `TimedOut`. If disk I/O is blocked, a following `Initialize` starts another worker while the previous worker still owns its stream and lease. Repeating this cycle accumulates workers and sessions while status loses access to the ones still stopping.

The timeout also excludes Console draining: `DrainSync` emits every queued entry before the writer receives the original full timeout. A slow Console can therefore exceed the caller's requested shutdown budget before the disk wait begins.

Retain the service reference and `Stopping` state until the worker actually exits; reject reinitialization during that interval. Use one monotonic deadline across Console drain and writer wait, account for undelivered Console entries, and dispose synchronization resources only after their users have finished. Clear the service only after final resource ownership is resolved.

## 6. [P1] Keep dictionary reads synchronized with scope-policy mutation

Location: [GameLogService.cs:177](/C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Diagnostics/Logging/GameLogService.cs:177).

`ResolveThreshold` locks only long enough to copy the dictionary reference, then performs `Count`/`TryGetValue` outside the lock. `SetScopeMinimum` and `RemoveScopeMinimum` mutate that same dictionary. Updating scope filters while worker threads or Unity capture log concurrently therefore performs unsupported reads during mutation, risking exceptions or inconsistent filtering. The capture callback has no catch boundary to contain those exceptions. Microsoft's [Dictionary thread-safety contract](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.dictionary-2#thread-safety) requires synchronization for concurrent readers and writers.

Either hold `policyLock` throughout the complete lookup or replace the entire policy with an immutable snapshot on every update. Include the global minimum in the same consistent policy snapshot. Keep callback failures contained without recursively logging them.

## 7. [P2] Serialize the assigned admission sequence instead of zero

Locations: [GameLogService.cs:221](/C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Diagnostics/Logging/GameLogService.cs:221), [GameLogService.cs:274](/C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Diagnostics/Logging/GameLogService.cs:274), [GameLogEntry.cs:148](/C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Diagnostics/Logging/GameLogEntry.cs:148).

Both direct and captured entries are constructed with `sequence=0`. The real sequence is assigned later and retained only in `QueuedEntry.Sequence`; `ToJson` serializes `GameLogEntry.Sequence`. Consequently every log record in the JSONL file has sequence zero, preventing the promised ordered identification of concurrent messages.

Carry the assigned admission sequence into the serialized record. For example, pass the queue item's sequence to serialization, or complete the immutable entry after admission assigns it. Do not move exception rendering under the admission lock to accomplish this.

## 8. [P2] Delete retention candidates without trying to delete an open lease

Locations: [GameLogFileWriter.cs:464](/C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Diagnostics/Logging/GameLogFileWriter.cs:464), [GameLogFileWriter.cs:480](/C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Diagnostics/Logging/GameLogFileWriter.cs:480), [GameLogFileWriter.cs:505](/C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Diagnostics/Logging/GameLogFileWriter.cs:505).

Both age-based and size-based cleanup acquire `active.lock` with `FileShare.None`, then recursively delete the directory while still holding that file open. On Windows the recursive deletion cannot remove the lease file. Depending on enumeration order, it can leave all or some JSONL files behind, but the directory cleanup always fails. The exception is swallowed, and size accounting does not reflect partial deletion, so cleanup can also proceed to additional sessions using stale totals.

Delete only recognized segment files while holding the candidate lease, then close the lease and remove its marker and empty directory while the base cleanup lock remains held. Update size accounting from successful deletion. Also replace the broad `twobirds-*` ownership check with validation of the exact generated session name before deleting anything; a prefixed directory plus an `active.lock` file is not sufficient proof that every child belongs to this logger. Expose cleanup failures rather than silently swallowing them.

## 9. [P2] Normalize scopes consistently in `IsEnabled` and initial options

Locations: [GameLogService.cs:113](/C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Diagnostics/Logging/GameLogService.cs:113), [GameLogService.cs:171](/C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Diagnostics/Logging/GameLogService.cs:171).

`Write` and scope setters normalize their scopes, but `IsEnabled` passes its argument directly into threshold resolution and initial override keys are copied unchanged. With any override present, `IsEnabled(level, null)` dereferences `candidate.Length` and throws. With `Networking=Debug`, a guard using `" Networking "` returns false under an Info global minimum, although writing that same scope would normalize it and admit the message. Initial whitespace/overlength keys likewise fail to match normalized writes.

Use `NormalizeScope` before every public lookup and when copying initial override keys, with a deterministic rule for keys that normalize to the same category. The guard and actual write must make the same threshold decision for equivalent input.

## 10. [P2] Enforce queue byte limits against the incoming allocation

Locations: [GameLogFileWriter.cs:103](/C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Diagnostics/Logging/GameLogFileWriter.cs:103), [GameLogConsoleWriter.cs:48](/C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Diagnostics/Logging/GameLogConsoleWriter.cs:48).

Both queues treat string character counts as byte counts, although the retained strings use UTF-16. They also compare only existing occupancy with the limit, not occupancy plus the incoming entry. Large entries can therefore roughly double the intended text-memory budget and cross both the critical-reserve boundary and the advertised hard cap. The file estimate additionally omits the retained scope text from its variable payload.

Use a consistent retained-byte estimate (`2 * retained UTF-16 length` plus defined overhead), include all retained per-entry strings, and check the proposed total before accepting. Apply the same cost when dequeuing and preserve the existing bounded batch allocation.

## 11. [P2] Service flush deadlines between batches and expedite captured errors

Locations: [GameLogFileWriter.cs:209](/C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Diagnostics/Logging/GameLogFileWriter.cs:209), [GameLogFileWriter.cs:238](/C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Diagnostics/Logging/GameLogFileWriter.cs:238), [GameLogService.cs:252](/C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Diagnostics/Logging/GameLogService.cs:252), [GameLogService.cs:283](/C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Diagnostics/Logging/GameLogService.cs:283).

The ordinary one-second flush deadline is checked only after the inner drain loop finds the queue empty. A continuous producer stream can prevent that check indefinitely, violating the bounded flush interval even though individual batches are limited. StreamWriter may push full buffers incidentally, but that does not satisfy the promised timed flush behavior.

Captured Error/Assert/Exception messages never call `RequestExpedite`. Direct Error calls set the expedited flag after signaling the worker, allowing the worker to wake, write, and miss that request until its next wake-up.

Check the monotonic flush deadline after each batch and wait only for the remaining interval. Set expedited requests before signaling, and use the same error-flush path for direct and captured entries. Coordinate requests so clearing a flag cannot erase an error request made during an in-flight batch.

## 12. [P2] Surface dropped/truncated records through diagnostics and flush results

Locations: [GameLogConsoleWriter.cs:119](/C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Diagnostics/Logging/GameLogConsoleWriter.cs:119), [GameLogFileWriter.cs:31](/C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Diagnostics/Logging/GameLogFileWriter.cs:31), [GameLogService.cs:230](/C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Diagnostics/Logging/GameLogService.cs:230), [GameLogFileWriter.cs:155](/C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Diagnostics/Logging/GameLogFileWriter.cs:155).

The Console drop-summary method only updates a timestamp; it emits nothing. The file writer never emits its planned drop diagnostics either. Per-level counters are recorded privately but are absent from status. After overload, the log itself therefore gives no explanation for missing records.

Truncation increments the service counter but never sets the writer's loss state. A session that shortens a message or exception can consequently return `Completed` instead of the specified `CompletedWithLoss`, even though the entry's text was lost.

Complete the rate-limited diagnostic path in both sinks, with emission markers on Console diagnostics and no callback recursion. Expose per-level loss counters and feed file-entry truncation into the file flush result. Keep these counters accessible when output itself is unavailable.

## 13. [P2] Recognize echo markers only at the defined transport position

Location: [GameLogUnityCapture.cs:55](/C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Diagnostics/Logging/GameLogUnityCapture.cs:55).

`IsOurEcho` searches the entire condition string for a matching marker. An external error that quotes an earlier formatted logger message, such as `Debug.LogError("Failed while processing: " + previousConsoleLine)`, is silently discarded because the quoted marker contains a valid issued ID. That external error is a new event, not the logger's own Console echo.

Parse the complete leading level/scope/marker envelope at the documented fixed position using ordinal matching. Leave markers embedded in arbitrary external message bodies alone. Keep the generation/issued-ID checks and preserve repeated genuine external messages.

## 14. [P2] Record the actual filtering policy in session headers and change diagnostics

Locations: [GameLogFileWriter.cs:51](/C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Diagnostics/Logging/GameLogFileWriter.cs:51), [GameLogFileWriter.cs:345](/C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Diagnostics/Logging/GameLogFileWriter.cs:345), [GameLogService.cs:143](/C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Diagnostics/Logging/GameLogService.cs:143).

The writer retains the initialization-time global override and writes it as `defaultMinimum`, conflating the automatic default with the effective minimum. Scope overrides are never serialized. Later policy setters emit no diagnostic and cannot update the writer's readonly global override, so rotation headers continue describing the initial configuration.

For example, enabling Debug for Networking in production produces Debug records without metadata explaining the exception to the Error default. Lowering the global minimum later leaves subsequent segment headers reporting the old minimum.

Serialize automatic default and effective global minimum separately, include a copied array of current scope overrides, and enqueue bounded policy-change diagnostics. Obtain rotation metadata from a consistent immutable policy snapshot rather than retaining the mutable dictionary reference.

## Visual checks after corrections

- Confirm Unity imports the scripts without compiler errors, then explicitly initialize the logger and open its first JSONL file.
- Compare one direct scoped message and one external Unity message across the Unity Console, Unity's own log, and JSONL. Each should appear once per intended output; JSONL sequences should increase and captured entries should use scope `Unity`.
- Inspect exception stacks, production Error-only filtering, scope overrides, and visible drop/failure diagnostics.
- Repeat explicit shutdown/reinitialization and inspect session files and handles. When automatic lifecycle integration is later added, repeat Play sessions with domain reload disabled.
- Inspect rotated files and completed-session cleanup, including an active second client whose files must remain intact.
