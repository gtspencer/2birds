**Networked scene MVP implementation plan**

Prepared September 13, 2026. This document specifies implementation work; it does not implement the scenes or gameplay.

**Outcome and technical direction.** Deliver a desktop build with exactly two enabled game scenes: `MainMenu` and `Game`. Solo starts a private local session. Host starts a server inside the player's application plus its local client. Join connects a client directly to an IPv4 address and UDP port. A session supports two players total, including the host. Each player controls a visible grey capsule on a large plane.

Use a dynamic `Rigidbody` and `CapsuleCollider` with FishNet's server-authoritative client prediction, reconciliation, and state forwarding. Unity UI Toolkit supplies all runtime UI; Unity Input System supplies all input. Keep simulation, network state, and presentation separate so the capsule's renderer can be replaced without changing movement authority.

**Verified project baseline.** The installed project uses Unity `6000.5.7f1`, URP `17.5.0`, Input System `1.20.0`, and FishNet `4.7.3`. Input handling is already set to the new Input System. `Assets/InputSystem_Actions.inputactions` is registered as the project-wide action asset and already contains `Player` and UI actions. The current build scene is `Assets/Scenes/SampleScene.unity`; `Specs/Generated` exists and is empty at planning time. No additional networking or controller package is required.

These details come from `ProjectSettings/ProjectVersion.txt`, `Packages/manifest.json`, `ProjectSettings/ProjectSettings.asset`, `ProjectSettings/EditorBuildSettings.asset`, and `Assets/FishNet/Runtime/Managing/NetworkManager.cs`. Use installed source as the API reference when web documentation differs. FishNet demos are useful references, but the Rigidbody demo uses legacy input and should not be copied wholesale.

**Scenes and persistent services.**

| Asset | Contents and responsibility |
| --- | --- |
| `Assets/Scenes/MainMenu.unity` | Build index 0. Menu `UIDocument`, UXML/USS, shared `PanelSettings`, menu camera if needed, and a bootstrap reference to the persistent session prefab. Host and Join are pages within this scene. |
| `Assets/Scenes/Game.unity` | Build index 1. A 100 × 100 metre static plane with collider, basic lighting, two separated spawn markers, scene player spawner, and a small UI Toolkit session overlay. |
| `Assets/Game/Prefabs/SessionRoot.prefab` | One persistent root containing FishNet `NetworkManager`, Tugboat, timing/prediction/scene/observer managers, and application session services. Explicitly prevent duplicates when returning to the menu. |
| `Assets/Game/Prefabs/Player.prefab` | Networked physics root, grey capsule graphics child, movement/state components, and a local camera target. Registered in FishNet's spawnable prefab collection. |

Only `MainMenu` and `Game` belong in the enabled build scene list. FishNet demo scenes and the original sample scene remain outside that list. A third bootstrap or lobby scene is unnecessary. Persistent services contain no player objects; a scene's network behaviours must not be children of the persistent NetworkManager.

Use a simple third-person orbit camera as the MVP assumption so the local capsule and other player are visible. Exactly one camera and one `AudioListener` are active per client. The camera follows the local player's smoothed presentation target in `LateUpdate`; its transform is local. Movement sends a world-space direction derived from camera yaw, so the server does not need the camera.

**Menu and connection behavior.**

| Page or action | Required behavior |
| --- | --- |
| Main page | Show Solo, Host, Join, Settings, and Quit. Settings and Quit are visible placeholders with no callbacks or application actions. |
| Solo | Bind an IPv4-only server to `127.0.0.1`, allow one client, then connect the local client through the normal session flow. Prefer an OS-selected free port, read back the actual bound port after server startup, and connect to that port. Solo requires no internet and does not accept LAN clients. |
| Host page | Show detected LAN IPv4 addresses with adapter labels, a selectable address to share, an editable port defaulting to `7770`, Copy Address, Start Hosting, and Back. Display the shareable endpoint before starting. |
| Start Hosting | Bind IPv4 to `0.0.0.0` at the selected port with a two-client limit. After confirmed server startup, load the game through FishNet and connect the host's local client to `127.0.0.1`. Enter gameplay when the local owned player is ready. |
| Join page | Separate Host IP and Port fields, Connect, Back, and an inline status/error area. Default to `127.0.0.1:7770` for same-machine testing. Starting Join starts only the client. |
| Game overlay | Display mode and player count; for Host, also retain the shareable IP/port and Copy Address. Escape opens a small session panel with Resume and Leave Session. This leaves the main menu Quit placeholder unwired. |

Enumerate active network adapters rather than assuming the first DNS result is the correct LAN address. Exclude loopback and link-local addresses from the normal LAN list; label VPN/virtual candidates by adapter so the player can choose. Offer refresh if adapters change. Never label `0.0.0.0` or `127.0.0.1` as an address for another machine to join. If no LAN address exists, state that only same-machine access is available.

Limit the MVP join form to valid IPv4 literals and ports `1–65535`; trim input, reject unspecified/broadcast/multicast destinations, and validate before starting a connection. Loopback remains valid. A LAN address is not a public internet address: the host page should briefly explain that internet joining requires a reachable public IPv4 address and UDP forwarding/firewall configuration. Public IP discovery, relay, matchmaking, and automatic router configuration are outside this delivery. Tugboat provides the direct UDP transport. [FishNet Tugboat documentation](https://fish-networking.gitbook.io/docs/fishnet-building-blocks/transports/tugboat).

**Session lifecycle and spawning.** A single `SessionController` owns the asynchronous state machine: `Idle → StartingServer/Connecting → LoadingGame → InGame → Stopping → Idle`, with an error result preserved for the menu. UI sends intentions to this controller and renders its state; it does not directly orchestrate FishNet callbacks.

1. Validate configuration and disable duplicate start actions. Reset transport settings between modes, including bind address, IPv6 support, client address, port, and capacity. Explicitly disable IPv6 for this IPv4 MVP, including Solo.
2. For Host/Solo, wait for a successful server-start event before reading its bound port or starting the local client. Reserve the host's player slot during startup so a remote connection cannot consume it. Require successful local connection before marking startup complete; unwind the server if it fails.
3. The server loads `Game` once with FishNet `SceneManager.LoadGlobalScenes` and `SceneLoadData` configured to replace the menu scene. Persistent session services survive. FishNet then synchronizes that global scene to connected and late-joining clients. A joining client never independently loads `Game` using Unity SceneManager.
4. Place a `NetworkObject` with a `GamePlayerSpawner` network behaviour in `Game`. Use its server-side `OnSpawnServer(connection)` callback to spawn when that connection can observe the game scene. Guard with a connection-to-player registry, session identity, and scene checks so duplicate callbacks cannot create another player. Do not also enable the sample/default `PlayerSpawner`.
5. Server-instantiates the registered prefab at a free spawn marker, assigns initial state, and calls `ServerManager.Spawn` with the connecting owner and the game scene. Keep scene observation configured so both players and the scene spawner are visible to both connections. The local owner enables gameplay input and camera only after initialization.
6. On remote disconnection, despawn its player and free its slot/marker; the host continues and accepts a replacement. Configure owner-disconnect destruction and registry cleanup consistently. Late joins receive the current spawned objects and persistent state.
7. Leave Session disables local gameplay input, stops the client, and also stops the server for Host/Solo. Wait for shutdown, clear registries and global-scene session state, then return locally to `MainMenu`. Host termination returns the remote client to its menu with a connection-lost message; there is no host migration.

Track connection attempts so callbacks from a canceled attempt cannot transition a new session. Permit cancellation while connecting/loading, use a connection deadline (initially 10 seconds) and a longer scene-load deadline (initially 30 seconds), and clean up partial startup. Show invalid input, bind failure, timeout, and disconnection errors inline. If transport rejection supplies no specific reason, report “Unable to connect; check the address, port, and available player slot” rather than asserting that the server is full. Repeated start/leave/join cycles must not accumulate event subscriptions or persistent roots.

Scene loading completion and observer readiness are distinct; FishNet recommends scene presence callbacks or a scene network behaviour's spawn callback for this purpose. [FishNet scene events](https://fish-networking.gitbook.io/docs/guides/features/scene-management/scene-events), [scene-load player spawning](https://fish-networking.gitbook.io/docs/tutorials/simple/making-a-custom-player-spawner/on-scene-load).

**Why the controller uses a dynamic Rigidbody.** Cars, rocks, and other players need to exchange impulses with a player's body. A non-kinematic Rigidbody naturally participates in contacts, gravity, and momentum. Unity's built-in `CharacterController` would require custom force integration and interaction rules to reproduce those behaviors; a transform-driven or permanently kinematic controller would also require substantial custom physical response. The dynamic body is the appropriate starting point for this game's requirements.

Use an approximately 2 m tall, 0.5 m radius capsule at unit root scale, gravity enabled, a tunable mass initially around 80 kg, low-friction/no-bounce contact material, and continuous collision detection appropriate for a dynamic capsule. Keep the body upright with pitch/roll constraints; yaw is controlled through the prediction motor. The graphics child has a mesh renderer and grey URP material, with no duplicate collider. Use layers for Player and Ground and a short downward ground probe that ignores the player's own body and triggers.

Movement applies bounded horizontal acceleration toward a target walking speed, initially 5 m/s, while preserving vertical velocity and allowing external momentum to decay over time. Do not overwrite total velocity with desired input each tick or clamp collision-generated speed to walk speed: either would cancel knockback. Braking, air control, and ground acceleration are tunable. Jump consumes a buffered input edge only when the authoritative ground rules permit it. Ground contact checks and any timers that affect simulation are tick-based and replayable. Add a server-authoritative reset below the plane's fall boundary, with zeroed velocities and prediction/smoothing history reset.

Expose a narrow internal, server-authorized impulse/movement-mode boundary. Walking, airborne, externally controlled, and attached movement must have one clear physics authority; grabbing or seating must not create competing transform writers. Attachment state should identify the controlling network object, attachment point/seat, relative pose, and effective tick. It must not require transferring player identity or input ownership to the carrier. These are design constraints, not additional gameplay interactions in the MVP.

**Prediction and position accuracy.** Clients submit movement intent; the server decides accepted movement and authoritative physics. The owner immediately predicts its input. Reconciliation restores a tick's complete authoritative simulation state and replays newer buffered inputs. State forwarding lets observers participate in prediction, which matters when two players collide. No peer submits an authoritative position.

| Setting | Initial decision |
| --- | --- |
| Simulation clock | FishNet TimeManager, 60 ticks/second on every peer; `PhysicsMode.TimeManager`. The installed defaults are 30 Hz and Unity-driven physics, so explicitly change both. |
| Physics ownership | FishNet drives physics steps. Apply motor forces in replicate callbacks; do not also integrate movement in `Update`, `FixedUpdate`, or a separate `Physics.Simulate` loop. |
| PredictionManager | Use `ReplicateStateOrder.Inserted`, which installed `StateOrder.cs` describes as aligning replayed states to server ticks. Enable local states; begin with state interpolation of 2 ticks. |
| Reconciliation | Build state each `OnPostTick` and send server reconciliation every tick. Disable automatic reduction of reconciles at low client frame rates for the accuracy baseline. |
| NetworkObject | Enable prediction, Rigidbody prediction type, and state forwarding for both players. Keep both relevant objects observed; do not reduce update frequency by distance. |
| Movement transport | FishNet's ticked unreliable replicate/reconcile path with its existing redundancy and bounded history. Retain excessive-input protections. Do not send reliable per-frame transform RPCs. |
| Physics state | Use `PredictionRigidbody` for force/velocity operations and reconciliation. Keep full float state initially; introduce no application-level coarse position quantization. |
| Visual smoothing | One `NetworkTickSmoother` on the graphics child, initially 1 tick for owner and 2 ticks for spectators. Disable Rigidbody interpolation and leave the legacy NetworkObject graphical smoother unassigned to avoid stacking smoothers. |
| Root synchronization | Prediction/reconciliation is the sole writer. Do not add a competing NetworkTransform to the player root. |
| Runtime timing | Run in background. Begin with tick dropping disabled for the two-body baseline; profile catch-up cost and flag sustained overload instead of silently assuming accuracy at low frame rates. |

The installed `PredictionManager.cs`, `Managing/Prediction/StateOrder.cs`, `TimeManager.cs`, and `Object/NetworkObject/NetworkObject.Prediction.cs` establish these available settings. `NetworkTickSmoother` is shipped under `FishNet.Component.Transforming.Beta` in this version; isolate its configuration and validate it in a standalone build. FishNet's older graphical smoother API is marked for removal. Official configuration guidance describes state forwarding and separates physics timing from graphical interpolation. [NetworkObject prediction configuration](https://fish-networking.gitbook.io/docs/guides/features/prediction/configuring-networkobject), [TimeManager prediction configuration](https://fish-networking.gitbook.io/docs/guides/features/prediction/configuring-timemanager).

`PlayerMotor` uses a compact ticked input record containing normalized movement, facing intent, and jump edge. `[Replicate]` validates and applies that record using `PredictionRigidbody`, then calls its `Simulate()` to apply queued operations. That call is not a second world-physics step. `CreateReconcile()` builds the Rigidbody state plus every motor variable needed for replay, such as jump cooldown, grounded decision where necessary, movement mode, and scheduled impulse state. `[Reconcile]` restores them together. Never read live input or a camera during replay. Host mode must follow FishNet's prediction dispatch without manually executing a second movement step. [FishNet predicted-object implementation](https://fish-networking.gitbook.io/docs/guides/features/prediction/creating-code/controlling-an-object).

For gameplay forces produced by collision/trigger callbacks, use FishNet `NetworkCollision`/`NetworkTrigger` where replay-aware events are required and route forces through `PredictionRigidbody`. Natural PhysX contact impulses still come from the physics simulation. External scripted effects need a defined effective tick and replay-safe state; an ordinary RPC calling `Rigidbody.AddForce` is insufficient. Simulation effects must reproduce on replay, while sounds and other one-shot presentation effects must not duplicate. Dynamic objects participating in a predicted interaction need a compatible simulation/reconciliation policy; interpolation alone does not reconstruct historical collisions. [FishNet PredictionRigidbody guidance](https://fish-networking.gitbook.io/docs/guides/features/prediction/predictionrigidbody).

A 60 Hz simulation is a starting configuration, not a guarantee of identical positions across machines. PhysX is not deterministic across peers, and observations arrive after network delay. Correctness means authoritative, tick-aligned state convergence with bounded visible corrections. Smoothing only changes rendering; it must never move the collision root or hide a persistent simulation error. Retain collision corrections even if they exceed the normal smoothing range, and snap presentation for explicit teleports/resets. Avoid indefinite extrapolation or repeating missing one-shot inputs; define and test bounded missing-input behavior using FishNet replicate states.

**State and presentation contract.** Keep these responsibilities distinct:

| State category | Authority and representation |
| --- | --- |
| Simulation | Ticked input plus reconciled body/motor state. Any state that changes movement participates in reconciliation at the tick it takes effect. |
| Persistent public player state | Server-written typed state with stable network references, a revision, and explicit empty/default values. It can describe movement mode, carried/equipped object identity, stance, and an attachment or vehicle/seat relationship. Current values must initialize late joiners. |
| Private player data | Owner-visible state where appropriate; public presentation should not depend on receiving an entire inventory. Possession and equipment changes are validated by the server. |
| Presentation | Read-only snapshot built from resolved simulation and public state: local-space velocity, speed, facing, grounded status, movement mode, and equipment/attachment descriptors. |

Use FishNet SyncTypes for ordinary persistent state. A movement-affecting field must not rely solely on an unticked SyncVar arriving whenever the network delivers it: reconcile its effective state as part of simulation. Avoid independent booleans that allow contradictory movement modes, and resolve temporarily unavailable object references gracefully during spawning/despawning. Implement only the fields the capsule MVP actually consumes; preserve the boundaries above in component interfaces.

The presentation consumer can eventually drive a `PlayableGraph` using these snapshots. Keep animation state-machine parameters, clip time, and graph internals out of the network protocol. Locomotion remains physics-driven, with no animation root-motion authority. This delivery creates no animation graph or avatar rig.

**Input and UI implementation.** Reuse the existing action asset and preserve its required `UI` action names/types. Enable the `Player` map only for local gameplay; menu navigation stays available through the project-wide UI map. MVP gameplay uses Move, Look, Jump, and a pause/session-panel action. Unused template actions remain unbound to gameplay behavior. Keyboard/mouse controls are WASD, mouse look, Space, and Escape; support the corresponding existing gamepad actions where available.

Collect input during the normal Input System update. Cache continuous movement and accumulate mouse look once per rendered input update; buffer button edges until a network tick consumes them. Multiple ticks in a frame must not reuse the same mouse delta or fire a jump edge repeatedly. Clear cached input on focus loss, menu opening, ownership loss, and despawn. Only the local owner reads gameplay input; UI navigation must not drive the capsule while a text field or session panel is active.

Use UXML for page structure, USS for layout/style, and a `MenuPresenter` for UI bindings. Build with Unity 6's UI Toolkit/Input System runtime integration and the project-wide UI actions; no uGUI Canvas or legacy input module is needed. Support pointer navigation, keyboard/gamepad focus, text entry, submit/cancel, and readable scaling at common desktop resolutions. Register/unregister UI callbacks with the document lifecycle. [Unity 6.5 runtime UI input handling](https://docs.unity3d.com/6000.5/Documentation/Manual/UIE-Runtime-Event-System.html).

**Implementation sequence and ownership.** Keep game code beneath `Assets/Game`, leaving the imported FishNet source unchanged.

1. **Establish assets and configuration.** Create the two scenes, session/player prefabs, grey material, shared UI assets, and spawn registration. Configure the physics layers and baseline network settings. Put shared tunable settings in a small configuration asset. Completion: a cleanly importing project with exactly two enabled build scenes and no automatic demo networking UI.
2. **Implement session services and menu.** Add `SessionController`, endpoint discovery/validation, and `MenuPresenter`. Wire Solo/Host/Join plus cancel/error/leave behavior. Completion: server/client start and stop reliably, selected endpoints are visible, and Settings/Quit remain unwired.
3. **Implement scene synchronization and player lifecycle.** Add `GamePlayerSpawner`, spawn registry, owner-only camera/input setup, and game overlay. Completion: Host and Join enter the same global scene with one correctly owned capsule each, including a client that joins after the host is already playing.
4. **Implement the predicted physics motor.** Add `PlayerInputReader`, `PlayerMotor`, compact prediction records, minimal `PlayerNetworkState`, and the read-only presentation adapter. Configure one visual smoother. Completion: responsive local movement, observer movement, grounded jumping, body contacts, and authoritative fall reset work in separate processes.
5. **Validate and tune accuracy.** Add development-only diagnostics, run the checks below, and adjust documented tuning values based on measured errors. Completion: a reproducible two-process build demonstration and recorded accuracy/performance results. Do not declare this step complete from an Editor-host test alone.

**Acceptance and measurement.** Test with Editor plus standalone for iteration, two standalone processes for authority verification, and two machines for LAN address/firewall behavior. Use FishNet's available transport latency simulator or equivalent external packet shaping; record whether configured delay is one-way or RTT.

| Scenario | Required result |
| --- | --- |
| Solo with network disconnected | One owned capsule; movement works; server binds only loopback; a LAN client cannot join. |
| Host plus Join | Two grey capsules on the plane; each peer controls only its own; both see the other moving, jumping, and colliding. |
| Late join and capacity | Joining after host gameplay initializes both players correctly. A third connection never creates a third player or replaces the host's slot. |
| Input and UI | Mouse, keyboard focus, submit/cancel, and gamepad navigation work; typing into IP/port fields does not move a player; only one local camera/listener exists. |
| Failures and cancellation | Invalid endpoint, occupied host port, unreachable host, cancel during load, and repeated clicks leave a recoverable menu with no orphan server/player. |
| Disconnect/reconnect | Remote exit frees its slot; a new remote client can join. Host exit returns the remote to menu. Ten start/leave cycles show no duplicated roots, players, or callbacks. |
| Physics authority | Walk head-on and across each other's paths; a development-only server-issued impulse moves the affected player on every peer and is not immediately erased by locomotion. Repeat under latency; no permanent interpenetration or growing divergence. |
| Fall reset | Walking off the plane produces one server-authorized respawn, with no replay pulling the player back to the old fall trajectory. |
| Timing | Repeat at 30, 60, and 144 FPS with 60 Hz network ticks, including an unfocused host window. Confirm no duplicate jump edges, inconsistent speed, or double host simulation. |

Development diagnostics should record role/owner, local/server tick mapping, RTT, actual tick rate, authoritative state age, replay count, pre-reconcile error, post-replay correction, graphics-to-body offset, and per-connection bytes/second. Compare states at matching simulation ticks using FishNet's client/server tick mapping; record frame-time and network-age differences separately. Remote screen positions at the same wall-clock instant are not a valid simulation-error measurement.

Use these initial acceptance targets for a two-minute repeatable walking/jumping route after a five-second warm-up. They are proposed targets to verify and tune, not claims about already measured performance:

| Network profile | Target |
| --- | --- |
| Loopback/LAN, no injected loss | Tick-matched pre-reconcile position error p95 ≤ 2 cm and p99 ≤ 5 cm during movement without player-player contact; no uncommanded correction over 10 cm in that route. |
| 100 ms RTT, ±10 ms one-way jitter, 1% packet loss | Same route: tick-matched error p95 ≤ 10 cm; no growing drift; after both players stop, converge within 2 cm of corresponding authoritative resting state within 500 ms. |
| Collision/impulse route under both profiles | Report error percentiles separately; prioritize correct server outcome and convergence to within 2 cm after motion settles and updates arrive. Scripted impulses must neither disappear nor duplicate during replay. |
| Stress: 200 ms RTT, ±25 ms one-way jitter, 3% loss | Stable session, bounded buffers, no duplicated actions, and recovery after packets resume. Larger transient corrections are expected; record them rather than claiming the normal-profile target. |

Log tick-processing and replay CPU cost on named test hardware; the server must sustain 60 Hz without accumulating backlog. Count visual interpolation delay separately from the PredictionManager state buffer. If targets fail, investigate incomplete reconcile state, contact timing, input buffering, double physics execution, frame overload, and smoother overlap before raising tick rate. A 120 Hz experiment is justified only if measurements identify simulation-step resolution as the remaining bottleneck and its CPU/bandwidth cost is acceptable.

The MVP is complete when these scene, connection, ownership, input, and measured movement checks pass in standalone builds, and the chosen tuning values and any remaining limitations are recorded alongside this plan.

**Implementation record (September 13, 2026).** Steps 1–4 are implemented. Standalone builds, measured movement profiles, lifecycle checks, source-driven adjustments, and remaining acceptance work are recorded in [NetworkedSceneMVPResults.md](NetworkedSceneMVPResults.md). Full acceptance is not yet closed; see that record for physical-machine/input checks and the installed Tugboat client IPv6 limitation.
