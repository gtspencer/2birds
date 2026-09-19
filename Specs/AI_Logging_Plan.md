# AI Logging Implementation Plan

Implement the contract in [AI_Logging_Spec.md](AI_Logging_Spec.md). The first useful result is a reproduction folder that connects session context, player/item impact events, body versus displayed state, markers, and separately requested screenshots.

## 1. Recorder and lifecycle

Create `Assets/Game/Runtime/Diagnostics/AILogger.cs` with the development-only public facade, record/context types, startup/reset hooks, and Unity log subscription. Keep writer implementation in `AILogWriter.cs` if separating disk/threading code makes these responsibilities clearer. Do not introduce a sink interface, dependency container, provider discovery, or backend configuration framework.

Declare the already-resolved `com.unity.nuget.newtonsoft-json` version `3.2.2` directly in `Packages/manifest.json`; let Unity resolve its lockfile. Implement typed JSON serialization, bounded admission, ordered writing, run metadata, rotation, retention, and explicit loss accounting together. File names include a GUID so Editor and standalone processes can share an output root safely.

Bootstrap the file recorder before scenes load. Use an Editor lifecycle hook for Play exit and assembly reload, plus the runtime quit hook. All shutdown/reset paths are idempotent and operate on a particular run instance. Add the output path to the existing development console's startup text for easy discovery.

Deliverable: every Editor Play Mode and development run produces parseable local records, captures ordinary Unity logs, and has explicit closure/loss information; release builds contain no logger code or calls.

## 2. Session identity and markers

Instrument these existing points in `Assets/Game/Runtime/Networking/SessionController.cs`:

- `BeginAttempt`: local attempt, selected mode, transport, and initial context.
- Host `wireSession` assignment and `ReceiveAdmission`: publish the shared session identity into the cached logging context.
- `SetPhase`, connection callbacks, local-player binding, readiness, and scene transitions: log meaningful state changes at their source.
- Leave/shutdown: log the reason and old session context before fields are cleared; then clear the cached identity.

Update `Assets/Game/Runtime/Diagnostics/Log.cs` so `Marker()` preserves its number and console text while also calling `AILogger` with a `marker` record. Keep P and gamepad D-pad left as the marker controls. Do not take a screenshot from this path.

Keep `LogMarkerService` available for temporary targeted wiring. Share only its input lifecycle; keep the two actions and their output events independent.

Deliverable: markers can be located by number within a run, and related host/client logs can be matched without confusing local attempt counters with the shared session ID.

## 3. Player and item instrumentation

Add structured calls at the existing impact decision points in `Assets/Game/Runtime/Player/PlayerMotor.cs`: request, accept, confirm, application, pending velocity application, rejected/accepted reconcile, reset, and generation change. Include actual before/after vectors, relevant tick domains, replay status, and the existing request/sequence identities. Log ordinary movement-mode changes once when they occur.

Add item release/lifecycle and hit reporting calls in `Assets/Game/Runtime/Items/WorldItemRegistry.cs`, `WorldItemRegistry.Motion.cs`, `WorldItem.cs`, and the actual player/item hit detection call sites. Preserve item epoch, release operation, simulator, revision, and motion sequence/path. Emit rejection reasons where those decisions are made; do not infer them later from absence of movement.

Use the existing impact trace call sites as a guide to useful values, but emit typed values directly. Do not parse its formatted strings. Keep `MvpValidation` and its `ImpactTraced` consumers compatible; this recorder does not depend on launching that harness or changing its CSV contracts.

Create one concrete `AILogCapture` helper for cached player/camera/focused-item snapshots. Have the existing `SessionController` own its coroutine and lifecycle. Bind references when the local player becomes ready; update focused references on interaction/impact/lifetime changes. Add read-only internal accessors only for values a defined event/sample actually needs.

Implement the 2 Hz baseline and 10 Hz/five-second burst with an eight-entity bound. Log sampling phase, instrumented scope, and explicit capture start/end records. Use direct calls to request a burst from marker, screenshot, impact, and existing anomaly paths. Capture simulation and displayed graphics positions separately.

Deliverable: an impact's request, acceptance, application, reconciliation, and visible result can be traced without reading freeform trace messages or treating replay as another impact.

## 4. Independent screenshot capture

Use a separate keyboard-only `[` development input binding for screenshots. The marker bindings remain P and gamepad D-pad left.

`AILogger.Screenshot()` logs a request and delegates to the concrete capture helper on the main thread. Use the existing session component to host the capture coroutine. Capture the completed game view and matching state, encode PNG bytes, release the temporary texture, and queue the write to the bounded worker.

Use the screenshot naming and path fields from the specification. Log `screenshot.saved` after the worker successfully writes the image; include both request identity and actual capture frame/time. Complete failed, unavailable, throttled, or shutdown-interrupted requests with explicit outcomes. Keep one pending capture and a one-second acceptance interval.

Do not reuse `MvpScreenshot.Start()`: it changes camera/UI render targets and has automation/quit behavior. The new path observes the user's rendered view and runs only when the separate screenshot action is invoked.

Deliverable: the agent can follow an exact log path to the image and compare it with state from that capture, without assuming the input request and image represent the same instant.

## 5. Event catalog and diagnosis workflow

Add `Specs/AI_Logging_Events.md` during implementation. For each implemented event, specify its payload fields, units, versions, identity scope, and source file. Include the sampling/loss events so agents can assess coverage before drawing conclusions.

Add a short usage section to `Assets/Game/README.md`: output locations, marker and screenshot controls, the two launch overrides, and which run folders to supply for multiplayer diagnosis. Include small streaming read/filter examples in the event catalog using ordinary PowerShell or Python. A custom reader executable, database, or Unity editor window is unnecessary for the first version.

Extension sequence: add an event payload and source call, document it, then add a focused sample only when the event alone cannot explain the visual behavior. Bird and cart instrumentation can follow the same contract without expanding the writer API.

## Implementation boundaries

Use the existing `SessionController` and `LogMarkerService` components for coroutines and input. The design requires no new scene object, prefab wiring, serialized component, or Unity asset. Unity generates metadata for new scripts. Runtime log and PNG files belong outside `Assets`.

Use the Unity CLI for relevant Editor operations during implementation, with Unity MCP as fallback. Do not start the validation harness, run tests, or perform visual validation unless the user explicitly requests those actions.

## User acceptance checks after implementation

| User action | Expected visual behavior and recorded evidence |
| --- | --- |
| Start Play, move, jump, and open/close the inventory or session panel | Input and camera behavior remain normal. Samples describe actual body/graphics/camera state and relevant UI/input flags. |
| Press P or gamepad D-pad left | Existing marker behavior remains; one canonical marker record appears. No screenshot is created. |
| Press `[` while moving, then with a UI panel open | Saved PNG matches the game view, including UI. The saved event's filename/path resolves, and its actual capture frame matches the associated state sample. |
| Throw an item and reproduce an impact as host and guest | Movement stays responsive. Each supplied run identifies its local viewpoint and the same operation can be followed through request, application, replay, and presentation. |
| Leave, rejoin, and repeat Play Mode, including with domain reload disabled | No duplicate input callbacks, stale camera references, or duplicated marker/capture actions. Each run and attempt is distinct in the files. |
| Press screenshot repeatedly | Controls remain responsive; accepted images and throttled requests have distinct outcomes rather than silently disappearing. |

If the user later requests automated checks, focus on parseable escaping/non-finite values, concurrent log ordering, payload freezing, bounded overflow with loss reporting, rotation with active-process protection, interrupted writes, failed screenshot writes, and repeated lifecycle initialization. These are proposed acceptance checks, not instructions to execute them as part of writing this design.
