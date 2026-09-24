---
name: profiler
description: Profile the standalone Unity player, either targeted at a named system or as a broad hitch survey. Coordinates gameplay actions with the user, captures and analyzes profiler data, and reports measured findings with next steps. Use when asked to profile, find hitches/stutters, or measure performance.
---

# Profiler

Measure first, then report. Do not implement optimizations unless asked.

Environment, tool locations, and hardware are in AGENTS.md. Recording, export, and summary commands are in `ProfilerCaptures/.tools/README.md`. Follow it and do not restate it.

## 1. Scope

- **Targeted:** the user names a system, action, or regression. Capture only that workload, plus an idle baseline to compare against.
- **Survey (default):** no target given. Run the hitch survey below. Tell the user it is a survey, then narrow to whatever the data shows is expensive.

## 2. Setup

1. Create `ProfilerCaptures/YYYY-MM-DD_HH-mm-ss/` from the real local time. All session output goes here, and prior sessions are never modified.
2. Use a local **Development** build of the player with Script Debugging and Deep Profiling off. Do not profile Editor Play mode except as a labeled aid. Rebuild only if no suitable build exists or the source changed.
3. Verify the connections with `unity status --json` and `unity command --query profiler|capture|eval`. Attach the Profiler to the player itself, not the Editor. Before cueing the user, confirm that main-thread frames are actually arriving.
4. Record once: commit hash, graphics API, resolution, vSync/frame cap, and network role (host/client/solo). Only record values you actually read back.

## 3. Capture

Each capture covers exactly one workload. Name it `<workload>-<pass>-NN`.

| Pass | Settings | Purpose |
|---|---|---|
| timing | call stacks off | Frame times, spikes |
| alloc | `allocationCallstacks: true`, short | Attribute GC.Alloc |
| deep | Deep Profiling, a few seconds | Only if a spike can't be attributed otherwise |

Do not compare timing numbers taken from alloc or deep passes.

**Working with the user:**
- Recording stays off while the user sets up. Ask up front about anything you need: consumables like ammo and potions, targets, controller, second player.
- Once the recorder is running and frames are confirmed, **end your turn** with a self-contained cue: what to do, how many times, how long, and what to report back. Example: *"Recording. Wait 5 s, throw 3 rocks at the cart ~3 s apart, wait 5 s, reply `done, N throws` plus anything that looked wrong."*
- When the user replies, stop the recorder and wait for the status file to show `finished: true`. Log what the user reports actually happened. If they didn't perform the action, label the capture idle.
- Frames from alt-tabbing back to chat show up as focus and device-change noise. Exclude them from the analyzed interval.

## 4. Hitch survey (default scope)

Capture these workloads in order, cutting any the game lacks: **idle (30 s) → cold start / first scene load → first use of each action → repeated use (warm) → dense activity (many physics objects, effects, players) → menu/UI open-close → scene exit / return to menu.** The difference between first use and warm use is where most hitches show up.

In each capture, look for these common Unity culprits:

| Symptom | Markers to look for | Usual cause |
|---|---|---|
| Periodic spike, no action | `GC.Collect` | Per-frame `GC.Alloc`: LINQ, boxing, string concat/format, closures, `new` collections, `foreach` over non-struct enumerators, `GetComponents`/`FindObjectsOfType` returning arrays, `Physics.*` non-`NonAlloc` queries |
| Spike on first use of an effect, material, or object | `Shader.CreateGPUProgram`, `CreateGraphicsGraphicsPipelineImpl` | Shader variant or PSO compiled on demand. Fix with PSO tracing and warm-up (`GraphicsStateCollection`) |
| Spike on spawn | `Instantiate`, `Awake`/`OnEnable` under it, `Loading.AwakeFromLoad`, `Mesh.*`, physics mesh bake | Instantiating mid-gameplay with heavy init. Fix with pooling, prewarming, or baked colliders |
| Spike on load/transition | `Loading.ReadObject`, `Application.Integrate Assets in Background`, `Resources.UnloadUnusedAssets`, `GC.Collect` | Synchronous loads, unload/GC at a transition, large assets integrated on the main thread |
| Several physics steps in one frame | many `FixedUpdate.PhysicsFixedUpdate` / `Physics.Simulate` in a single frame | Spiral of death: a slow frame forces catch-up steps. Look for expensive `OnCollision*`/`OnTrigger*`, contact counts, `Physics.SyncTransforms` |
| High but steady main thread | `BehaviourUpdate`, `LateUpdate`, networking tick (e.g. FishNet `TimeManager`) | Update-loop polling, repeated `GetComponent`/`Camera.main`/`Find`, per-frame work that should be event-driven |
| Main thread waiting on render/GPU | `Gfx.WaitForPresentOnGfxThread`, `Gfx.WaitForRenderThread`, `WaitForLastPresent` | Render thread or GPU bound. Check the render thread, draw calls, batching, shadows, post-processing, and overdraw. **Waiting on vSync or `WaitForTargetFPS` is not a cost** |
| Spike on UI open/change | `UIElements.*`, `UIR.*`, layout/style markers | Relayout, restyle, or rebuilding large visual trees in UI Toolkit |
| Animation cost scaling with characters | `Animators.*`, `Director.PrepareFrame`, skinning | Too many animators or bones, culling mode set to Always Animate |
| Allocation or spike when focus or device changes | `InputSystem.onDeviceChange`, `Application.InvokeFocusChanged` | Input rebinding or UI rebuild triggered by device or focus events |
| Spikes near log output | `Debug.Log`, `StackTraceUtility` | Logging inside hot paths |

Missing data is not zero cost. If a module (GPU, render thread) was not captured, write it down as a gap.

## 5. Analyze

- `Stop` writes `<run>-hitches.json` (tagged long frames, periodic/first-use spikes, per-marker segments). Start there. The culprit table above is its rule set.
- Use `analyze.cs` and `summarize.py` from `.tools` (see README). Write a focused helper in the session folder only if the shared ones can't answer the question, for example about another thread, a specific counter, or a single marker.
- For each workload report **median, p99, max, count of frames over 20 ms, and allocation B/s**. Also note the frame budget: at 60 Hz, a 16.7 ms median only means the game is capped at 60, not that it has headroom.
- Attribute each long frame to its most expensive *self-time* samples. Never add parent and child inclusive times together. Just because a marker appears in the same frame as a hitch doesn't mean it caused the hitch.
- Rolling capture parts overlap. Deduplicate them (summarize.py does this). Do not sum them.

## 6. Report

Write `Findings.md` in the session folder. Keep it short: a reader should get the picture in under a minute.

```markdown
# Profiling <date> — <scope>
Build: <commit> · <API> · <res> · <vsync/cap> · <role>

## Results
| Workload | Median | p99 | Max | >20ms | Alloc B/s | Capture |
|---|---|---|---|---|---|---|

## Findings (ranked by player-visible impact)
### 1. <Problem> — <measured cost>
Evidence: <marker, frames, capture link>. Cause: <attributed or suspected, with confidence>.
Proposed fix: <smallest change>. Must preserve: <behavior>. Verify by: <before/after capture>.

## Not measured
- <workload>: <why / what's needed>
```

Only recommend fixes that are backed by measured cost. If the cause can't be attributed, recommend the specific capture that would pin it down.

## 7. Finish

Stop the recorder, restore the profiler settings, and leave the player running if the user still needs it. Link `Findings.md` in your reply along with the top findings, then list anything left unmeasured and what you need from the user to measure it.
