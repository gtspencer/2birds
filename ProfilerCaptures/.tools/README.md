# Shared profiler helpers

Run these from the repository root through the connected editor's `eval_file` command. The C# files are evaluation snippets, not Unity project scripts; keep them outside Assets. `Invoke-Profiler.ps1` supplies their `options` object from a JSON configuration and saves the evaluated source in the session folder. Use the Unity CLI location from AGENTS.md if `unity` is not on PATH.

| Helper | Purpose |
| --- | --- |
| `record.cs` | Record a connected player for a bounded duration, saving overlapping parts and metadata. |
| `stop.cs` | Request completion of a run using the same configuration. |
| `analyze.cs` | Load an existing capture and export main-thread frames, sample costs, direct-parent allocation groups, and optional allocation call stacks. |
| `summarize.py` | Select an explicit time interval, deduplicate overlapping frame exports from one recording, and calculate statistics. |

Start each session in a new `ProfilerCaptures/YYYY-MM-DD_HH-mm-ss/` folder. Keep configurations, evaluated snippets, captures, exports, and findings there. Never use `.tools` for session output. The examples use `SESSION` as a placeholder for that folder, and `$unityCli` for the executable specified in AGENTS.md.

## Record and stop

Connect the Profiler to the intended standalone player first. Save this as `ProfilerCaptures/SESSION/record.json`:

```json
{
  "outputDirectory": "ProfilerCaptures/SESSION",
  "runName": "rocks-timing-01",
  "durationSeconds": 120,
  "saveIntervalSeconds": 20,
  "allocationCallstacks": false
}
```

```powershell
& 'ProfilerCaptures/.tools/Invoke-Profiler.ps1' -Operation Record -Config 'ProfilerCaptures/SESSION/record.json' -UnityCli $unityCli
```

Recording starts immediately. Confirm frames arrive before giving the user the final-message action cue. `durationSeconds` bounds the user's recording window; it is not the required action duration. Use a new run name and `allocationCallstacks: true` for a separate diagnostic pass.

On the user's completion report:

```powershell
& 'ProfilerCaptures/.tools/Invoke-Profiler.ps1' -Operation Stop -Config 'ProfilerCaptures/SESSION/record.json' -UnityCli $unityCli
```

Stop writes a sentinel that the recorder handles on the next editor update. Read `rocks-timing-01-status.json` and confirm `finished: true` and a saved capture; also check for `rocks-timing-01-error.txt`. Do not start another run while completion is pending. A duration limit or manually disabled recording also finishes the run. An editor stall can delay both saves and completion; actual timestamps are recorded. A crash cannot guarantee cleanup.

The recorder temporarily enables CPU, Memory, Physics, and Rendering, disables Deep Profiling/editor profiling, and sets the explicit allocation-call-stack mode. GPU and other modules remain as configured. It restores the changed settings and removes callbacks after completion, failure, or assembly reload. It refuses an existing recording or reused run name and stops if the target changes. It clears current profiler history at startup, so save any existing history you need first.

Parts are saved at the requested interval or after roughly half the configured history capacity has advanced, whichever comes first. They overlap intentionally. `gapBeforePart` flags a history gap relative to the previously saved part. Saving competes for resources; exclude or label save-related disturbances when choosing timing intervals. Sidecars preserve recording ID, target, original frame indices, timestamps, and completion reason. Loaded capture indices can differ from the recorded indices.

## Export a capture

Save a separate `analysis.json`:

```json
{
  "outputDirectory": "ProfilerCaptures/SESSION",
  "capturePath": "ProfilerCaptures/SESSION/rocks-timing-01-part-01.data",
  "outputName": "rocks-timing-01-part-01"
}
```

```powershell
& 'ProfilerCaptures/.tools/Invoke-Profiler.ps1' -Operation Analyze -Config 'ProfilerCaptures/SESSION/analysis.json' -UnityCli $unityCli
```

The exporter refuses active recording, loads the specified capture without changing the original file, and refuses existing export filenames. Its final `*-analysis.json` is the completion record. A timeout may leave work running or partial output: check for completion before retrying, then use a fresh output name or a smaller frame range. Loading replaces the editor's displayed profiler history.

Optional analysis fields:

| Field | Meaning |
| --- | --- |
| `firstFrame`, `lastFrame` | Inclusive file-local frame range. Omit both to export the loaded capture's full range. |
| `sampleFilter` | Case-insensitive substring filter for `*-samples.json` only. Frame totals and direct-parent allocations remain unfiltered. |
| `includeCallstacks` | Default false. Requires explicit first/last frames; start with a few frames to avoid editor command timeouts. |
| `callstackFilter` | Optional case-insensitive substring in resolved call-stack data; only stack groups are filtered. |
| `recordingId` | Override only when matching older captures that lack sidecars. Use the same ID for parts of one recording and different IDs for independent recordings. |

For example, a separate analysis config with `outputName: "rocks-flight-stacks"`, `firstFrame: 100`, `lastFrame: 110`, `includeCallstacks: true`, and `callstackFilter: "WorldItem"` extracts allocations whose resolved stack matches WorldItem. Choose actual flight frames from the timing trace; the example frame numbers have no scenario meaning. GC.Alloc call stacks must have been enabled when recording. Unresolved native methods retain their addresses, and allocations without a stack or size are counted explicitly.

Outputs cover thread 0 only and require it to be named Main Thread. They do not supply GPU, render-thread, detailed memory, rendering counters, or physics contact counts. Use other tooling or a focused helper for those measurements. Missing data is not zero cost. Sample self time subtracts direct child samples; inclusive sample totals must not be added together as independent work. `-allocations.json` groups allocations by their immediate profiler parent, which need not be the allocating C# method.

## Summarize selected frames

```powershell
python 'ProfilerCaptures/.tools/summarize.py' 'ProfilerCaptures/SESSION/rocks-timing-01-part-01-frames.csv' 'ProfilerCaptures/SESSION/rocks-timing-01-part-02-frames.csv' --output 'ProfilerCaptures/SESSION/rocks-timing-01-summary.json' --start-seconds 10 --duration-seconds 20
```

Start time is relative to the earliest frame in the supplied exports. Omit duration to include the remaining frames. Pass explicit CSV paths, all from the same recording; summarize separate repeats separately. The helper removes overlapping frames by recording ID and exact nanosecond timestamp. It refuses a mix of recording IDs and refuses to overwrite output. Legacy exports must be regenerated with `analyze.cs` to supply these fields.

The summary includes percentiles, maxima, allocation rate over captured frame time, elapsed versus captured time, gap candidates, source references for long frames, and a configurable `--long-frame-ms` threshold (default 20). Inspect gaps and actual interval coverage before using the result. The main-loop remainder subtracts two named waits and still includes other waits. The long-frame threshold is not a precise missed-frame-deadline count.

## Focused extensions

Use these shared tools where they fit. One-off capture and analysis helpers remain allowed for a specific marker, thread, counter, snapshot workflow, or unsupported API. Put them in the dated session folder, document their inputs and limitations, and reference useful evidence in Findings.md. Promote them into `.tools` only when they have a reusable purpose. These tools do not automate gameplay or replace user readiness, supply checks, action counts, and visual observations.
