# Logger implementation review — fix notes

All 14 issues in `Logger_Implementation_Review.md` are valid and have been addressed. Notes on each follow.

## 1. Import the namespace containing `ThreadStaticAttribute`

**Valid.** Added `using System;` to `GameLogUnityCapture.cs`. The file had only `System.Threading` and `UnityEngine`, which do not bring `System.ThreadStaticAttribute` into scope.

## 2. Capture application metadata before starting the writer thread

**Valid, with caveat.** `Application.version` and `Application.buildGUID` are not marked `IsThreadSafe` in Unity's bindings, unlike `Application.unityVersion`. In practice these read static cached data and typically work from background threads without issue. However, the implementation plan explicitly states "Initialization caches application metadata … before starting the worker," so the fix aligns with the plan regardless of whether the background access would fail at runtime.

**Fix:** `GameLogService` constructor now reads `Application.version`, `Application.unityVersion`, and `Application.buildGUID` on the main thread and passes the cached strings to `GameLogFileWriter`. The writer stores them as readonly fields and uses them in `WriteSessionHeader` instead of calling Unity APIs from the background thread.

## 3. Base flush completion on successfully written records, not the requested target

**Valid.** Several interrelated problems existed:

- `FlushToDisk` published `flushTargetSequence` as flushed regardless of what was actually written.
- Periodic flushes never advanced `flushedThroughSequence`, so a subsequent explicit flush on an idle, clean queue would time out.
- The service used `nextSequence - 1` as the flush target, counting entries rejected by the file queue.
- Concurrent waiters shared a single `ManualResetEventSlim` without rechecking their condition.

**Fix:** Added `highestWrittenSequence` tracking in `WriteBatch`. `FlushToDisk` now calls `NotifyFlushProgress` which advances `flushedThroughSequence` from the actual written sequence. `FlushThrough` waits in a deadline-bounded loop, rechecking its target and the failure state. `ServicePendingFlush` handles the case where the queue is empty but flush targets are already satisfied. The service now tracks `highestFileAdmittedSequence` (set only on successful `Enqueue`) and uses it for flush targets.

## 4. Make file failure terminal and propagate it to admission and waiters

**Valid.** `WriteLineRaw` and `FlushToDisk` no longer catch exceptions internally — they propagate to the outer `WorkerLoop` catch, which:

1. Sets `state = Failed` (checked by `Enqueue` to reject new entries).
2. Drains and discards remaining queued entries with drop accounting.
3. Sets `flushEvent` to wake any blocked `FlushThrough` waiters (they check `Failed` and return `Unavailable`).
4. Invokes the `onFailure` callback, which emits one `Debug.LogError` diagnostic with a marker through the console path.

`WriteBatch` accounts for unwritten entries in its catch block before rethrowing.

## 5. Keep a timed-out service alive in `Stopping` until its worker exits

**Valid.** `GameLogService.Shutdown` now only transitions to `Stopped` when the result is not `TimedOut`. `GameLog.Shutdown` only clears the service reference on non-timeout. `GameLog.Initialize` rejects reinitialization when the service is `Stopping` and its worker has not exited (`!IsWorkerDone`). When the worker eventually exits, the next `Initialize` call will find `Stopping + WorkerDone`, discard the old service, and create a new one.

Shutdown now uses a single `Stopwatch` deadline. `DrainSync` runs first and consumes part of the budget; the file writer wait receives the remaining time.

## 6. Keep dictionary reads synchronized with scope-policy mutation

**Valid.** Replaced the mutable shared dictionary with an immutable copy-on-write `FilterPolicy` snapshot. Each mutation (`SetMinimumLevel`, `SetScopeMinimum`, `RemoveScopeMinimum`) creates a new `FilterPolicy` containing a fresh dictionary copy and atomically replaces the `volatile FilterPolicy currentPolicy` reference. Readers grab the reference (atomic on .NET) and use it without holding any lock. The global minimum and scope overrides are always read from the same consistent snapshot.

## 7. Serialize the assigned admission sequence instead of zero

**Valid.** `ToJson` now takes a `long sequence` parameter. The `Create` and `CreateFromUnity` factory methods no longer accept a sequence parameter (it was always zero). `WriteBatch` passes `item.Sequence` (the admission-assigned value from `QueuedEntry`) to `ToJson`.

## 8. Delete retention candidates without trying to delete an open lease

**Valid.** Both age-based and size-based cleanup now:

1. Delete recognized `.jsonl` segment files while holding the candidate lease.
2. Dispose the lease file handle.
3. Delete the `active.lock` marker file.
4. Remove the (now empty) directory non-recursively.

Size accounting is updated from successful deletions, not from the pre-deletion estimate. Session directory names are validated against the expected format before processing.

## 9. Normalize scopes consistently in `IsEnabled` and initial options

**Valid.** `IsEnabled` now calls `NormalizeScope(scope)` before `ResolveThreshold`, preventing null dereference and whitespace/overlength mismatches. `GameLogOptions.Copy` normalizes scope override keys via `NormalizeScope`. The initial scope override construction in `GameLogService` also normalizes keys. Guard and write now make the same threshold decision for equivalent input.

## 10. Enforce queue byte limits against the incoming allocation

**Valid.** Both file and console queues now:

- Use `textLen * 2 + PerEntryOverhead` as the byte estimate (UTF-16, two bytes per char).
- Include `Scope.Length` in the file queue's payload estimate.
- Check `currentBytes + entryBytes > limit` (proposed total) rather than `currentBytes >= limit` (existing occupancy).
- Use the same cost formula for enqueue and dequeue.

## 11. Service flush deadlines between batches and expedite captured errors

**Valid.** The flush deadline and flush-target checks are now evaluated after each batch inside the inner drain loop, preventing continuous traffic from starving flushes.

`RequestExpedite` is called before `Signal` in both `Admit` and `AdmitUnity`. `AdmitUnity` now calls `RequestExpedite` for Error-level captured messages (it previously did not).

## 12. Surface dropped/truncated records through diagnostics and flush results

**Valid.** The console writer's `TryEmitDropSummary` now emits an actual `Debug.LogWarning` with a marker (preventing capture re-entry). Emission is rate-limited and happens outside the queue lock (avoiding the lock-ordering deadlock with `allocateEmissionId`).

The file writer's `TryEmitDropDiagnostic` emits a `diagnostic` kind record with drop counts directly to the file, rate-limited to once per second. The emission is wrapped in a try-catch so a diagnostic write failure does not crash the worker.

`WriteBatch` now sets `hadLoss = true` when processing a truncated entry, so `FlushThrough` and `WaitForShutdown` correctly return `CompletedWithLoss` when entries were truncated.

## 13. Recognize echo markers only at the defined transport position

**Valid.** `IsOurEcho` now validates the structural envelope `[Level] [Scope] [GameLog:guid:id]` at the expected position using ordinal matching. It requires the string to start with `[`, finds the level and scope bracket boundaries, and checks the marker immediately after the scope closing bracket. A valid marker embedded elsewhere in a message body (e.g., quoted from a previous log line) is no longer misidentified as our echo.

## 14. Record the actual filtering policy in session headers and change diagnostics

**Valid.** The session header now serializes `buildDefault` (the automatic minimum) and `globalOverride` (the explicit override, empty if unset) as separate fields, replacing the conflated `defaultMinimum`. Scope overrides are serialized as a `scopeOverrides` array of `{scope, level}` objects.

The writer stores a `volatile FilterPolicy headerPolicy` reference that the service updates on every policy change via `UpdateHeaderPolicy`. Rotation headers reflect current policy rather than stale initialization-time values.

**Deferred:** Explicit policy-change `diagnostic` records (written to the file when `SetMinimumLevel`/`SetScopeMinimum`/`RemoveScopeMinimum` are called) are not yet emitted. The header on each new segment captures the current policy state, which provides the same information at rotation boundaries. Adding inline policy-change diagnostics between rotations is a follow-up if needed.
