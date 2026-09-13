# Networked scene MVP — implementation and validation

September 13, 2026. Steps 1–4 of the plan are implemented. A Windows development build and reproducible standalone measurements are delivered. **Full MVP acceptance remains open** for the manual and transport limitations listed below; this is not an Editor-only completion claim.

## Deliverables

- `Assets/Scenes/MainMenu.unity` and `Assets/Scenes/Game.unity` are the only enabled build scenes, in that order.
- `Assets/Game` contains the session/player prefabs, shared tuning, materials, UI Toolkit documents/styles, session services, predicted Rigidbody motor, input reader, presentation adapter, and editor configuration checks.
- [Windows executable](../../Builds/Windows/TwoBirds.exe). Keep its adjacent data/DLL folders with it. Settings and Quit are deliberately unwired; close the window normally when finished.
- [Implementation guide](../../Assets/Game/README.md), [two-process runner](../Validation/run-pair.ps1), [UDP shaper](../Validation/udp_profile.py), and [measurement summarizer](../Validation/summarize.py).
- Raw CSVs, session logs, summaries, and the successful build log are under `Builds/Validation`. Older failed prototype runs are not included in the acceptance data.
- Rendered captures: [main menu](NetworkedSceneMVP/menu.png), [Host](NetworkedSceneMVP/host.png), [Join](NetworkedSceneMVP/join.png), [two-player game](NetworkedSceneMVP/game.png).

Imported FishNet source was not edited. The original sample/demo scenes remain outside the enabled build list. The user's open Editor was left undisturbed; CLI builds ran in a sibling validation copy, and the finished assets/build were copied into this workspace.

## Plan review and implementation decisions

The installed APIs support the selected authority/prediction approach. The implementation retains 60 Hz TimeManager physics, inserted prediction states, state forwarding, per-tick reconciliation, local fallback states, no tick dropping, no frame-rate reconcile reduction, and a single graphics smoother (owner 1 tick, spectator 2). Rigidbody interpolation and the legacy graphical smoother are disabled.

The motor uses 5 m/s walking speed, 25 m/s² ground acceleration, 12 m/s² braking, 4 m/s² air acceleration, a 6 m/s jump velocity change, an 80 kg capsule, and a -15 m fall boundary. Impulses use a server-only queue and participate in reconciled state. Public state contains only the spawn-slot descriptor and revision consumed by this MVP. Attachment/inventory/animation gameplay was not added.

Installed-source details required these adjustments:

1. `Tugboat.GetPort()` reads the actual bound socket port while the server is started. Solo binds port zero, waits for startup, then connects to that returned port.
2. A scene object's spawn callback can precede the connection's initial-scene acknowledgement. The spawner begins from `OnSpawnServer` and, when necessary, waits for `OnLoadedStartScenes` before spawning. Both paths share the same guarded registry; subscriptions are removed on disconnect/shutdown.
3. FishNet's throttled editor validation can skip scene IDs in batch-generated scenes. The sole scene NetworkObject receives an explicit stable nonzero ID.
4. Unity scene loading cannot be canceled midway. Shutdown lets the pending load settle, stops networking, drains socket states, then loads MainMenu. A new attempt is blocked until this finishes.
5. **IPv6 limitation:** the prefab disables Tugboat's IPv6 server option, and the application accepts only IPv4 destinations. FishNet 4.7.3's internal `ClientSocket.StartSocket()` still creates a default dual-stack outgoing client socket; it exposes no public client-side IPv6 switch. Completely removing that client IPv6 socket needs an upstream transport change or a maintained transport fork. No reflection patch or vendor-source modification was introduced. The Solo game server itself binds only IPv4 loopback.

Asset generation also exposed and fixed unloaded settings references. The generator now constructs assets/scenes without losing those references, and the build checks reject missing settings. Runtime checks exposed two teardown/spawn warnings, both corrected before the accepted runs.

## Test environment and method

Windows, Unity 6000.5.7f1, FishNet 4.7.3; AMD Ryzen 9 3900X (12 cores), NVIDIA RTX 3080 Ti for rendered captures. Authority/movement tests used two separate standalone processes with `-batchmode -nographics`; rendered captures used the same game with graphics enabled and explicit offscreen rendering. Some independent runs/build work overlapped on this machine, so CPU figures are indicative rather than isolated benchmarking.

Walking runs used nominal 125-second sessions and excluded the first five seconds of diagnostics. The route walks a square, jumps on ticked edges, and includes resting intervals. Automated input enters at the movement-intent boundary; it does not submit positions. The camera/input ownership and scene components are the production components.

Reconcile error compares a body's saved/replayed position against the authoritative position **at the same FishNet-mapped simulation tick**. Locally generated fallback reconciles are excluded. Owner and spectator results are separate. CSV positions/errors have six decimal places in metres; a reported zero means below the logging resolution, not proof of deterministic cross-machine physics. `postReplayM` records the last completed replay's displacement at the current simulation time, rather than comparing remote screen positions.

The final diagnostics also record the explicit authoritative server tick, server-state age, local/server tick mapping, RTT, replay count/cost, graphics offset, and payload bytes per connection. Payload accounting excludes UDP/LiteNetLib headers. Tick CPU measures pre-tick through each motor's post-tick callback; use FishNet's existing Profiler markers for complete frame/serialization cost. Sustained over-budget motor tick processing emits a development warning.

The external UDP shaper uses a fixed random seed, with delay/jitter applied **one way in both directions**, and loss applied to all datagrams. The normal profile is 50 ±10 ms each way with 1% loss; stress is 100 ±25 ms each way with 3% loss. Observed RTT includes scheduling and tick processing, so it is higher than configured wire delay.

## Measured movement results

Owner measurements; distances below are centimetres. A zero percentile is rounded at the CSV precision. Max pre-reconcile error and current-time replay displacement are different measurements.

| Profile / render cap | Samples | p95 error | p99 error | Max error | Max sampled replay displacement | Median reported RTT |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Loopback, 60 FPS | 6,321 | 0.00 | 0.00 | 8.00 | 8.00 | 17 ms |
| Loopback, 30 FPS | 6,109 | 0.00 | 0.00 | 20.83 | 3.47 | 0 ms* |
| Loopback, 144 FPS | 5,896 | 0.00 | 0.00 | 8.27 | 8.53 | 20 ms |
| Normal profile, 60 FPS | 5,308 | 0.00 | 0.00 | 9.10 | 10.91 | 130 ms |
| Stress profile, 60 FPS | 4,061 | 0.00 | 6.76 | 177.30 | 41.67 | 233 ms |

*This is the value reported by FishNet's RTT estimator in that run; it does not assert zero network latency.

The loopback and normal-profile percentile targets passed on this hardware. Stress produced the expected larger transient corrections and completed without session failure or accumulating drift. The final half-second of owned resting-state samples had zero error at logging precision in all five runs. This establishes settled convergence; it is not an independent proof of the exact 500 ms convergence deadline for every stop/impact.

Server tick rates were 59.995–60.006 Hz across these runs, with no tick dropping. Host motor tick CPU p95 was approximately 0.45–0.62 ms, depending on callback/actor order. Client tick CPU p95 reached 4.22 ms and replay CPU p95 reached 4.18 ms in stress. The server sustained the 60 Hz baseline; no 120 Hz experiment was warranted.

## Contacts and impulses

The initial collision driver was rejected because its participants started too far apart in time. Accepted contact runs wait for two players before starting their warm-up. Authoritative body samples confirm contact (minimum centre separation about 0.94 m for two 0.5 m-radius capsules), followed by separation and settled convergence. PhysX can slide capsules around each other; the test does not constrain lateral motion to force a permanent head-on lock.

| Route / observed role | p95 error | p99 error | Max pre-reconcile error | Max sampled replay displacement |
| --- | ---: | ---: | ---: | ---: |
| Collision, loopback, guest owner | 0.51 cm | 0.51 cm | 11.92 cm | 16.29 cm |
| Collision, normal profile, guest owner | 0.51 cm | 1.54 cm | 8.00 cm | 15.24 cm |
| Collision, normal profile, spectator | 1.00 cm | 2.00 cm | 17.00 cm | 27.94 cm |
| Scripted host impulse, normal profile, spectator | 1.03 cm | 5.50 cm | 19.21 cm | 235.42 cm |

These were 35-second client sessions. Collision runs ended with zero error at logging precision for both client bodies. Impulse observer error in the final half-second was at most 0.67 cm. The server-issued impulse persists against bounded locomotion acceleration and is represented on observers, but its large transient observer displacement remains a tuning limitation. Do not treat that effect as visually polished. Host-only extra runtime after a contact client leaves can carry the scripted route off the plane; this is excluded from the shared contact interval and exercises fall recovery.

## Functional checks

| Check | Result |
| --- | --- |
| Endpoint and asset configuration assertions | Passed during the successful CLI build. Invalid IPv4 forms, multicast/unspecified/broadcast addresses, invalid ports, missing settings, duplicate colliders/smoothers, and incorrect scene/prefab configuration are checked. |
| Solo and OS-selected loopback port | Passed; one owned capsule. OS socket inspection confirmed the game listener at an ephemeral `127.0.0.1` endpoint. Client/development sockets are separate from this listener. |
| Separate-process Host/Join and late join | Passed; one owned capsule per connection, two visible capsules, one camera/listener per gameplay process. |
| Capacity and occupied host port | Passed; a third client was rejected without spawning a third player; an occupied port returned a recoverable menu error. |
| Invalid/unreachable destination | Passed; inline state/error returned to Idle with no players. |
| Ten Solo start/leave cycles | Passed; one root and zero players after every cycle. |
| Ten cancellations during client connection | Passed; one root, zero players, no orphan session after every cycle. |
| Ten cancellations during server startup | Passed; one root, zero players after every cycle. |
| Ten cancellations while loading Game | Passed; queued loading drained, then returned to MainMenu after every cycle. |
| Remote disconnect/replacement | Passed; the same remote process joined three successive times while the host remained available. |
| Host leaves with remote connected | Passed; remote returned to MainMenu with a connection-lost message. |
| Fall boundary | Passed; crossing the edge produced a server reset from below -15 m to the spawn marker, incremented the reset revision once, and cleared the fall trajectory. |
| Rendered UI/game | Main, Host, Join, and two-player game captures inspected at 1280×720. Field contrast corrected. Host/Join pages were opened through focused UI Toolkit navigation-submit events. |
| Final build telemetry smoke | Separate-process Host/Join completed with the final explicit server-tick CSV format. |

Expected bind failure/rejection messages occur in negative-test logs. Accepted normal lifecycle runs do not contain game-code exceptions or the earlier spawn/teardown warnings.

## Remaining acceptance work

- Two physical machines: adapter selection, actual LAN reachability, UDP firewall behavior, and independently running PhysX on different hardware.
- A physically disconnected network during Solo. The implemented session path is entirely loopback and requires no discovery/service, but adapters were not disabled on the user's machine.
- Hands-on keyboard/mouse/gamepad navigation, text editing, clipboard, focus loss/restoration, and input edges at each render cap. Scripted tick input and focused submit events do not substitute for these.
- The exact 500 ms convergence deadline for every collision/impulse, a deliberate complete packet outage followed by recovery, and extended isolated profiling. Current logs demonstrate settled convergence and continuous-loss stress recovery, not all of those stronger assertions.
- The outgoing-client IPv6 socket limitation above, and impulse observer presentation tuning.

Example reproduction from the repository root (Python 3 is needed only for shaping/summarizing):

```powershell
.\Specs\Validation\run-pair.ps1 -Executable .\Builds\Windows\TwoBirds.exe `
  -Output .\Builds\Validation\retest-normal -Profile normal -Fps 60 -Seconds 125
```
