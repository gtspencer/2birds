---
name: hitch-survey
description: Script-driven Unity hitch survey. One marker-segmented capture of the standalone player, automatic hitch classification (hitches.cs), and a short ranked Findings.md. Use for "find hitches/stutters", a quick perf check, or a targeted action when you want the standard report rather than open-ended investigation (use the profiler skill for that).
---

# Hitch survey

The tools do the detection. Your job: set up, cue the user, read `<run>-hitches.json`, and explain it. Do not write one-off analysis scripts for anything the report already covers. Do not implement fixes.

Commands and report fields: `ProfilerCaptures/.tools/README.md`. Environment: AGENTS.md.

## 1. Prepare

1. Create `ProfilerCaptures/YYYY-MM-DD_HH-mm-ss/` from the real local time.
2. Check that the player is a Development build with `UNITY_INCLUDE_INSTRUMENTATION`. Without it, marker presses do nothing and the report has a single segment. Rebuild only if needed.
3. Once the player is running, attach the Profiler to it (not the Editor) and ask the user to get into gameplay. Ask up front for anything the survey needs: consumables, targets, controller.

## 2. Record and cue

Start `Record` with `runName: "survey-01"`, `durationSeconds: 300`, `saveIntervalSeconds: 20`, and `allocationCallstacks: false`. Confirm that frames are arriving, then **end your turn** with the cue. Drop any step the game doesn't have. For a targeted run, keep step 1 and replace the rest with the named action (first use, then repeats).

> Recording. Press **P** (D-pad left) after each step:
> 1. Stand still 20 s → **P**
> 2. Do each main action once (first use) and wait 3 s after each → **P**
> 3. Repeat those actions for 20 s → **P**
> 4. Create the densest scene you can (many objects/effects/players) for 20 s → **P**
> 5. Open and close each menu/UI once → **P**
> 6. Return to the main menu and re-enter the game → **P**
>
> Press P last, *before* leaving the game window. Reply `done` with any step you skipped or anything that looked wrong.

The segment after the final marker is the alt-tab back to chat. Ignore it. Marker numbers continue from earlier presses in the same game session, so map segments to steps by order, not by number.

## 3. Stop and read

Run `Stop` with the same config. It waits for the final save, then writes `survey-01-hitches.json`. Read **only that file** first.

- **Missing or extra markers:** use the user's reply and segment durations to line up steps. If they can't be lined up, say so and treat the capture as a single run.
- **`unknownLongFrames` > 0 or an ambiguous worst frame:** run `analyze.cs` on that capture's file-local frame range (±2 frames) with `sampleFilter` as needed.
- **High `gc` count or alloc B/s:** do one short second run (`survey-02-alloc`, `allocationCallstacks: true`) of only the offending segment's action, then use `analyze.cs` with `includeCallstacks` on a few of its frames.
- **Same pattern shows up every survey but gets no tag:** add a rule to `classify` in `hitches.cs`. Don't write a one-off script.

## 4. Interpret

| Tag / field | Usual cause | Next step to propose |
|---|---|---|
| `gc` (periodic) | Steady per-frame allocation | Alloc pass → top stack → remove allocation (cache, pool, NonAlloc) |
| `shader`, `firstUse` on render markers | Variant/PSO compiled on first draw | PSO tracing + `GraphicsStateCollection` warm-up |
| `spawn`, `firstUse` on scripts | Instantiate + heavy Awake/OnEnable | Pool or prewarm; move init off the hot path |
| `load` | Sync load, unload, or GC at a transition | Async load, hide the transition, split the work |
| `physics-catchup` | Slow frame forces extra fixed steps | Find the first slow frame's cause; check contact callbacks |
| `render-wait` with low `renderThreadBusyMs` | GPU- or present-bound, or a vSync miss | GPU capture; don't blame main-thread code |
| `ui` | UI Toolkit relayout or restyle | Reduce tree rebuilds and style changes on update |
| `focus-device` | Focus or input-device callbacks | Only matters if players can trigger it in play |
| `untracked` | OS, window, driver, or profiler-save stall | Check it recurs away from rolling saves before reporting |
| high steady segment median | Update-loop cost | `topSelfMs` → the named scripts |

Compare segments: first-use vs. repeat shows warm-up cost, and idle vs. dense shows scaling. A 16.7 ms median only means the game is capped at 60 fps, not that it has headroom.

## 5. Report

Write `Findings.md` in the session folder:

```markdown
# Hitch survey <date>
Build: <commit> · <API> · <res> · <vsync/cap> · <role>

| Segment | Median | p99 | Max | >20ms | Alloc B/s | GCs |
|---|---|---|---|---|---|---|

## Findings (ranked by player-visible impact)
### 1. <Problem> — <worst ms, how often>
Evidence: <tag, segment, capture + frame>. Cause: <attributed / suspected>.
Proposed fix: <smallest change>. Verify by: re-running this survey step.

## Not covered
- <skipped steps, unknown frames left unattributed>
```

Reply with the link, the top three findings, and anything the user needs to do next.
