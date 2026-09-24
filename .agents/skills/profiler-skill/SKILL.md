---
name: profiler
description: Profile a Unity player using targeted scenarios or a broad performance survey. Coordinate gameplay actions with the user, collect and analyze captures, and produce evidence-based findings and proposed next steps.
---

1. **Choose the scope.**
   - Read the user's request and AGENTS.md. Use AGENTS.md for the environment, tool locations and preference, stack, hardware, and repository rules; do not repeat discovery of information it already supplies. Read a supplied profiling checklist or scenario document when explicitly requested.
   - For a targeted run, investigate the requested systems, regressions, or workloads. Include adjacent work only when the capture implicates it.
   - For a generic run, survey representative startup, scene transitions, idle gameplay, movement, interactions, combat/projectiles, dense physics, rendering, memory, UI/input, networking, and session exit. Adapt this list to the game; "generic" does not mean every possible code path is covered.
   - If no scope was specified, begin with a broad survey. State the assumption and narrow the investigation using observed costs.
   - Profiling authorizes the capture and analysis work. Produce findings and proposed fixes; do not treat a request to profile as a request to implement optimizations.

2. **Inspect the project and available controls.**
   - Read `ProfilerCaptures/.tools/README.md` and reuse its recording, capture-export, call-stack, and statistics helpers when they fit. Invoke the C# snippets with `Invoke-Profiler.ps1` and an explicit JSON configuration; keep each configuration and generated evaluation file in the dated session folder.
   - Creating one-off capture and analysis tools is still allowed for specialized questions or gaps in the shared helpers. Keep them in the session folder with documented inputs and limitations. Only promote reusable functionality into `.tools`; this allowance does not authorize gameplay harnesses or migration tools.
   - Identify run-specific information not supplied by AGENTS.md: enabled scenes, build routes, active transport selection, diagnostics, frame caps, and any existing gameplay automation.
   - Discover the connected editor, command schemas, and profiler/capture/evaluation capabilities rather than assuming commands exist. Request user-operated editor windows when available tooling cannot perform a required operation.
   - For this repository, start with the configured Unity CLI's `status --json` and `command --query profiler`, `command --query capture`, and `command --query eval`.
   - A CLI connection to the editor is separate from a Profiler connection to the player. Verify both and identify the player explicitly.
   - If there is no dedicated profiler command, use an available editor C# evaluation command to inspect and operate the installed profiling APIs. Discover signatures through reflection or version-matched official documentation. Missing dedicated commands alone do not mean profiling is impossible.
   - Prefer `eval_file` for nontrivial C# snippets: nested quotes passed through PowerShell and CLI argument parsing can be lost. Keep capture helpers outside Assets so they do not create game components or trigger project compilation.
   - Reuse existing automation where it reproduces the workload. Account for logging, CSV output, screenshots, and other harness overhead. Do not add a gameplay harness just to avoid asking the user to perform a short action sequence.
   - Respect filesystem access restrictions. If a capability is blocked, state the specific limitation and use a permitted alternative; do not bypass the restriction.

3. **Define comparable scenarios and identify user assistance.**
   - Separate idle, first use, warmed repeated use, dense activity, loading/transitions, and teardown. Define what counts as an action and which interval answers the question.
   - Ask early for missing scenario information: available equipment/ammunition, how to reach targets, controller availability, multiplayer participants, and whether the user can perform the required actions.
   - Confirm consumable supplies before planning repeats. Do not assume the user can reproduce a potion throw, shot, or destructive interaction indefinitely. Use smaller labeled samples when supplies are limited.
   - Specify counts, targets, host/client role, approximate duration, and visual observations. Keep unrelated actions in separate captures so their costs can be distinguished.
   - Distinguish local loopback or multiple local processes from representative Steam multiplayer. Use a second machine/account where needed; record contention when processes share a machine.
   - Treat manual help as part of the workflow. Do setup and analysis yourself, but request gameplay input, physical devices, or window operations when available tools cannot perform them.

4. **Prepare the player and record its configuration.**
   - At the start of each new profiling session, create a new folder named `ProfilerCaptures/YYYY-MM-DD_HH-mm-ss/` using the actual local date and time. Add a suffix if that name already exists; never overwrite or reuse a previous session's folder. Continue in the same folder across turns of that session.
   - Keep that session's Findings.md, captures, logs, action notes, configurations, one-off helpers, and analysis exports inside its folder. Shared reusable helpers belong in `ProfilerCaptures/.tools`; never put session output there. Link prior sessions as evidence without modifying their captures or reusable procedures.
   - Reference the environment documented in AGENTS.md. Record source revision and relevant working-tree changes, build time, graphics API, Graphics Jobs, quality, requested and confirmed resolution, frame cap/vSync, player count, active transport, role, diagnostics, and enabled Profiler modules. Distinguish verified values from assumptions or launch requests; note observed differences from the documented environment.
   - Inspect the actual build route. Use a local Development build with profiling support; avoid routes that also upload or publish. Explicitly record the managed code variant and Script Debugging state.
   - Use Checked as the starting diagnostic variant where supported. Keep Script Debugging and Debug optimization off for timing. Use Instrumented for a separate timing pass if appropriate, keeping variants identical across a comparison. Do not assume the Development flag alone defines the desired managed variant.
   - Build only when necessary to obtain a known, suitable executable. Preserve build results and provenance. Record cache conditions, duration, and size when comparing build-affecting settings.
   - Preserve prior editor/profiler settings before changing them. If profiling requires a missing package or other prerequisite, explain what it enables and resolve it within the user's authorized scope.

5. **Configure and verify capture delivery.**
   - Launch the standalone player with graphics enabled. Profile Editor Play mode only as a labeled investigation aid, not a substitute for deciding player measurements.
   - Attach the Profiler to the intended player. Explicitly check CPU Usage and recording state; saved editor settings can disable CPU capture or stop a newly attached player.
   - Enable CPU Usage, Memory, and Physics for gameplay; add Rendering and supported GPU Usage for rendering questions. Record other enabled modules and keep them consistent. Missing GPU data is not zero GPU cost.
   - Keep Deep Profiling and GC.Alloc call stacks off for baseline timing. Collect a separate short allocation-attribution pass with GC.Alloc call stacks enabled. Use Deep Profiling or narrow markers only when attribution still requires them, clearly separating their overhead.
   - Verify that player frames and expected samples actually arrive before asking the user to consume resources. A launched process or a nonempty capture file does not establish a useful recording.
   - Ensure history can hold the workload. Prefer disk streaming or bounded rolling saves for uncertain manual start times. Give every capture a fresh name and preserve original files.
   - For the shared recorder, use operation `Record` with explicit `outputDirectory`, `runName`, duration, save interval, and `allocationCallstacks`. It records immediately and restores temporary settings at completion. Check saved frame ranges and any gap/error metadata; a successful start response is not proof that useful frames arrived.
   - Startup launch arguments such as `-profiler-enable`, `-profiler-log-file`, `-profiler-capture-frame-count`, `-connection-id`, and `-logFile` may be useful; verify support for the installed version and build. The frame limit starts at launch. Editor attachment can change recording behavior, so verify the resulting frames rather than trusting the flags.

6. **Coordinate interactive captures explicitly.**
   - Leave recording off while the user enters gameplay, gathers equipment, or positions targets. Tell them exactly what to prepare and wait for their readiness before dependent work.
   - Once ready, start and confirm the appropriate recording, then give the action cue in a **final response**. Commentary-only cues may be missed. End the turn so the user can act and reply.
   - Make the cue self-contained, for example: "Recording is active. Throw one potion at a time for 20 seconds, waiting for each impact. Keep the game foreground, count throws, then stop and reply 'done, X throws' with any flight/impact problems."
   - Allow time for the user to read the cue and return to the game. For manual workloads, use a bounded window, such as two minutes, with rolling saves often enough to preserve earlier frames. Do not end a short fixed capture before the user has a practical chance to act.
   - A recorder that continues after the agent turn must have a duration limit, a completion/cancellation path, durable capture files, and timestamps. Remove its callbacks when it stops. Save before the history overwrites useful frames, and account for save-induced contention.
   - On "done," stop promptly, confirm the stop, preserve the capture, and record actual counts, targets, timing information, and visual observations. Returning to chat can introduce focus/device-change costs; exclude or label those intervals separately.
   - With the shared recorder, use operation `Stop` with the same configuration. Wait for its status file to report `finished: true`, inspect save success/errors, and only then begin analysis or another recording.
   - If the user did not act, label the capture idle or unknown, regardless of its filename. If supplies run out or the user asks to stop, stop recording and document the missing measurement. Never classify an empty action run as a successful scenario.
   - Keep known unfinished features separate from regressions. Ask only for relevant visual checks, such as flight, impact, targeting, ricochet, obstruction, cart contacts, or UI appearance.

7. **Collect timing, allocation, and specialized passes.**
   - As a starting protocol, warm up for roughly 10 seconds and record 20–30 seconds per workload, repeated three times when practical. Startup and first-use passes deliberately include initial work. Label single runs and incomplete repeats honestly.
   - Keep camera path, resolution, quality, populations, diagnostics, and roles comparable. Test one candidate change at a time only when such a comparison is authorized and the candidate exists.
   - Separate first projectile/asset use from warmed reuse. Record actual concurrent object counts in dense scenarios; do not infer load from requested actions alone.
   - For rendering, use Frame Debugger briefly to inspect actual draw/batch paths, then disable it before timing. Measure render-thread work as well as main-thread work; obtain GPU data only under supported conditions.
   - For memory, broad counters establish trends, not retained sizes of particular assets. Use matched Memory Profiler snapshots for object/reference attribution when the package is available. Snapshot collection belongs outside timing runs.
   - For loading, distinguish process startup, first scene entry, later entries, and menu return. A fresh process does not imply a cold OS disk cache. Keep unrelated shader, asset, physics, and UI initialization costs separate.
   - For networked gameplay, record owner/simulator and receiver roles, host/client captures, corrections, and consistency alongside CPU cost.

8. **Analyze the original samples and confirm attribution.**
   - Preserve original `.data`/`.raw` files and player logs. Export per-frame data and selected sample/call-stack evidence to readable files where tooling permits.
   - Use shared operation `Analyze` with a capture path and fresh output name. Its exports cover only the main thread, not GPU/render-thread work or broad memory/rendering counters. For call stacks, explicitly select a small frame range and optional stack filter. Use one-off helpers when other threads or specialized evidence are required.
   - Use shared `summarize.py` with explicit CSV paths and a chosen time interval. Combine only parts of the same recording; it deduplicates overlapping frames by recording ID and timestamp. Summarize independent repeats separately and compare their results. Inspect gap/coverage information before interpreting allocation rates or percentiles.
   - Through editor evaluation, inspect APIs such as ProfilerDriver and RawFrameDataView for loading/saving profiles, frame ranges, samples, metadata, and call stacks. Dispose frame views, keep analysis batches within tool limits, and never modify game state to read a capture.
   - Check completion after a command timeout before rerunning it: the editor operation may still finish and write its output. Narrow expensive extraction to relevant frames rather than repeatedly processing the entire capture.
   - Saved captures may rebase displayed frame indices. Record file-local frame ranges and timestamps. Rolling files can overlap; deduplicate by frame timestamp/session instead of adding totals across clips.
   - Report median, p95/p99 where meaningful, maximum, long-frame counts, and variation across repeats. Explain interval selection and exclusions. A capped 60 FPS average can conceal useful changes.
   - Distinguish frame-cap waits, graphics/presentation waits, main-thread execution, render-thread execution, physics solver work, and gameplay callbacks. Subtracting two named waits does not yield pure active CPU time. Do not add inclusive parent and child timings as independent costs.
   - Attribute allocations using GC.Alloc metadata and call stacks. Record which threads were analyzed. Zero allocations nested inside a native query marker does not imply zero allocations in its managed caller. Per-action/physics-step estimates require actual action/step counts and attributable samples.
   - Separate automation, logging, profiler call-stack collection, and capture-saving overhead from normal gameplay. Do not assign a hitch to a physics query simply because it occurs in the same frame.
   - Treat absent counters, zero-filled unsupported summaries, unresolved stack frames, and missed scenarios as limitations. Do not interpret them as zero memory, zero GPU use, or proof of safety.
   - Inspect unexpected expensive samples too: session shutdown, menu rebuilding, input/focus changes, logging, animation/skinning, repeated collection allocation, or growing retained memory. Confirm their triggering workload and confidence before recommending changes.

9. **Write a useful investigation handoff.**
   - Maintain one Findings.md entry point, linking detailed scenario reports and captures. Update its coverage summary as later evidence arrives so old "idle only" statements do not contradict completed action runs.
   - Include configuration/provenance, actual workloads, capture paths and frame ranges, sample/call sites, measured costs, repeat variation, attribution confidence, and all material limitations.
   - Clearly distinguish completed scenarios, partial scenarios, and unmeasured scenarios. A broad initial survey is not completion of every profiling check.
   - Prioritize additional findings by measured player-visible cost and recurrence. For each, state the next investigation and what must remain correct in a proposed fix.
   - Recommend an optimization only when the measured opportunity supports it. Otherwise recommend a specific missing measurement. Do not prescribe nonalloc buffers, remove callbacks, replace thread-safe collections, or change render settings merely because the source looks expensive.
   - Give the next agent a clear task: identify causes, propose the smallest suitable fixes, estimate benefit from evidence, and specify a focused before/after comparison. Explain whether captures are available locally or must accompany a shared report.

10. **Finish or hand control back cleanly.**
    - Stop recording and expensive diagnostic modes, save remaining useful frames, and remove timed/rolling recorder callbacks. Restore temporary profiler/editor configuration when no further capture is pending.
    - Close only processes launched for unattended runs when their work is complete. Leave an interactive player available when the user still needs it; communicate its state.
    - Report what the evidence establishes, recommended priorities, what remains incomplete, and exactly what assistance or supplies are needed next. Link the main findings file.
    - Describe the relevant visual checks the user must make for any remaining scenario or candidate change. If no implementation changed and the relevant checks were already reported, acknowledge those observations rather than inventing another validation task.
