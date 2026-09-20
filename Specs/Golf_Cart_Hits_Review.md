# Golf cart hits review

The predicted rigidbody approach is a reasonable fit for carts and players that must physically push each other. Keeping both bodies in the same simulation is more coherent than combining a moving cart proxy with separately reported pedestrian impulses. I would keep that foundation, but address the handoff, presentation, and idle-traffic issues below before extending it.

P2 means a functional or performance issue worth fixing; P3 means a smaller inconsistency. The parking and authority observations are design tradeoffs, rather than assumed requirements violations.

## Findings

### 1. [P2] Sleeping carts never become idle to FishNet

**Location:** [GolfCartNetwork.Prediction.cs:65](Assets/Game/Runtime/Vehicles/GolfCartNetwork.Prediction.cs#L65), especially the unconditional generation assignment at line 70.

Every owner/server-authored `CartInput` contains a nonzero `Generation`, including an empty, sleeping cart. FishNet's generated default comparer includes serializable fields. Its [replication path](Assets/FishNet/Runtime/Object/NetworkBehaviour/NetworkBehaviour.Prediction.cs#L564) refreshes both replicate and reconcile resends whenever the input is not default. Consequently, the sleeping early return in `GolfCartController.Simulate` saves simulation work but does not stop network traffic.

With the session's 60 Hz tick rate, each idle cart continues producing replicate and reconcile traffic for its observers. The previous implementation stopped publishing after its final resting state and limited ordinary moving updates to every third tick. This is an avoidable regression, separate from the legitimate cost of predicting moving carts.

**Recommendation:** Make the input's idle/default comparison depend on actual controls, while retaining generation/revision checks for accepted input and reliable baselines for transitions. Preserve the final release/rest messages and FishNet's transform-change wake-up behavior. Do not simply stop calling replicate/reconcile methods, because clients still need prediction history.

### 2. [P2] A driver handoff can erase a real pedestrian launch

**Locations:** [PlayerMotor.CartContacts.cs:88](Assets/Game/Runtime/Player/PlayerMotor.CartContacts.cs#L88), [PlayerMotor.CartContacts.cs:286](Assets/Game/Runtime/Player/PlayerMotor.CartContacts.cs#L286), [GolfCartNetwork.Prediction.cs:106](Assets/Game/Runtime/Vehicles/GolfCartNetwork.Prediction.cs#L106), [GolfCartNetwork.cs:234](Assets/Game/Runtime/Vehicles/GolfCartNetwork.cs#L234).

An actual collision queues `pendingCartLift` after physics; the player applies it on the following movement tick. If the driver exits or changes seats during that same tick, `CommitPending` installs a new cart generation after contact processing. `CartGenerationChanged` then clears that pending lift and marks the continuing contact as already launched.

This loses an authentic hit even though the cart was not teleported. The horizontal physics impulse remains, and `cartRecovery` remains active, so the victim can lose movement control without receiving the intended upward launch. A client that already predicted the launch can subsequently be corrected to the server's different result. Driver disconnects use the same generation-reset path.

**Recommendation:** Distinguish ownership-only handoffs from recovery teleports when invalidating contacts. Preserve or commit the current physics step's legitimate launch across an ownership-only transition, while continuing to reject stale replay contacts. Keep lift cancellation and recovery cancellation consistent.

### 3. [P2] Wheel travel compensates for a correction that smoothing already removes

**Locations:** [GolfCartNetwork.Prediction.cs:157](Assets/Game/Runtime/Vehicles/GolfCartNetwork.Prediction.cs#L157), [GolfCartPresentation.cs:94](Assets/Game/Runtime/Vehicles/GolfCartPresentation.cs#L94), [GolfCartPresentation.cs:109](Assets/Game/Runtime/Vehicles/GolfCartPresentation.cs#L109).

`AfterReconcile` adds the graphics' reconciliation displacement to `previousPosition`. However, `Graphics` remains parented to the physics root, so its position temporarily changes as reconciliation moves that root. This is not necessarily movement the player will see.

The installed FishNet smoother records graphics position before the tick and [restores that position in `OnPostTick`](Assets/FishNet/Runtime/Generated/Component/TickSmoothing/UniversalTickSmoother.cs#L600). The [TimeManager ordering](Assets/FishNet/Runtime/Managing/Timing/TimeManager.cs#L723) puts reconciliation between those two callbacks. Thus the cart modifies its wheel-travel reference for a displacement the smoother subsequently removes. `LateUpdate` can interpret the resulting difference as backward or extra wheel rotation during network corrections.

**Recommendation:** Accumulate wheel rotation from consecutive final displayed poses. Remove this intermediate reconciliation adjustment, or only adjust for an actual visible teleport after smoothing. The existing baseline reset can still establish a new travel origin.

### 4. [P2] Steam controller association can select another controller's glyphs

**Location:** [SteamInputGlyphs.cs:36](Assets/Game/Runtime/UI/SteamInputGlyphs.cs#L36).

`ResolveHandle` makes two unsafe associations:

- If Steam exposes one handle, it assigns that handle to every eligible Unity XInput gamepad. A native Xbox controller and a Steam-emulated PlayStation controller can therefore both receive PlayStation glyphs.
- With multiple handles, it treats the controller's position in `Gamepad.all` as its XInput slot. Unity appends devices to that list when they are added; the list also includes non-XInput gamepads. Its index is not the Steam/XInput slot. Connection order, slot gaps, and mixed native/emulated controllers can select the wrong device or no device.

Caching makes the mistaken association persist until invalidation; clearing the cache does not fix the underlying mapping.

**Recommendation:** Use an actual device-to-XInput-slot association when available. Restrict the single-device shortcut to an unambiguous eligible-device configuration. When identity is ambiguous, use the native Unity controller-family fallback instead of guessing.

### 5. [P2] Contact restoration repeats global synchronization and per-cart geometry work

**Locations:** [PlayerMotor.cs:216](Assets/Game/Runtime/Player/PlayerMotor.cs#L216), [PlayerMotor.cs:472](Assets/Game/Runtime/Player/PlayerMotor.cs#L472), [PlayerMotor.CartContacts.cs:150](Assets/Game/Runtime/Player/PlayerMotor.CartContacts.cs#L150).

Every reconciliation schedules restoration before replay and again before the next normal movement step. Each eligible player independently calls `Physics.SyncTransforms`, then performs a capsule overlap query. FishNet already synchronizes transforms once [after applying reconciliation states](Assets/FishNet/Runtime/Managing/Prediction/PredictionManager.cs#L670).

The overlap results contain colliders, but each result calls `TouchesCart`, which scans every solid collider on that cart. Several colliders from the same cart therefore repeat the same whole-cart calculation. `RefreshCartSeparation` can additionally scan that cart immediately before the overlap loop. This multiplies cost across players and replay steps, including restores for players nowhere near a cart.

**Recommendation:** Process each cart only once per overlap query. Use the existing post-reconcile synchronization boundary for replay restoration, and synchronize ordinary transforms at a shared boundary only when necessary. Keep the separation margin and replay-aware contact history; removing those would risk repeated launches. These optimizations do not require giving up physical contacts.

### 6. [P3] Fallback glyph images and text can name different buttons

**Location:** [InputPresentation.cs:60](Assets/Game/Runtime/UI/InputPresentation.cs#L60), particularly the name assignment after loading the family-specific texture.

When Steam identifies an emulated PlayStation or Nintendo controller but its PNG is unavailable, the new fallback correctly selects that controller family's bundled texture. It then overwrites the button name with `control.shortDisplayName`/`displayName` from Unity's emulated XInput controller.

That can produce a PlayStation Cross image with the text "A", or a Nintendo image with the Xbox face-button name. It also discards a valid action-origin name returned by Steam when only texture loading failed.

**Recommendation:** Preserve Steam's origin name when available. Otherwise derive the fallback label from the same family and button mapping used for the image.

## Design and tuning tradeoffs

### Parking now means weak rolling resistance

[GolfCartController.cs:150](Assets/Game/Runtime/Vehicles/GolfCartController.cs#L150) replaces the previous holding behavior with a 150 N passive longitudinal resistance. With the configured 450 kg mass, gravity along a roughly two-degree incline already exceeds that force: `450 × 9.81 × sin(2°) ≈ 154 N`. Damping limits rolling speed but does not supply a static holding force at zero speed.

A sleeping cart may appear parked until a collision wakes it; a moving or newly awakened unattended cart can roll down a gentle slope and never meet the new sleep threshold. If unattended carts should roll, this is a coherent choice. If "parked" should mean staying put on ordinary terrain, the behavior needs a separate holding rule.

Simply restoring the old 4,200 N braking force would undermine pushing: an 80 kg player's new 3 m/s² push acceleration supplies approximately 240 N of sustained effort. Choose the intended slope/parking behavior together with push strength, rather than independently adjusting these numbers. Consider a supported rest hold that yields to deliberate external motion if both behaviors are required.

### Prediction is useful, but its costs should be separated

| Choice | Assessment |
| --- | --- |
| Simulate cart/player contacts on clients and server | Keep for reciprocal pushing and immediate local collision response. Expect more physics and replay work than the old cart proxy. |
| Run four suspension rays for each awake cart simulation | Necessary for this controller, but now incurred on every simulating client and during replay. Retain the sleeping fast path. |
| Send active cart prediction at tick frequency | A consistency/responsiveness tradeoff. Recover idle suppression first; changing cadence blindly can increase contact corrections. |
| Repeat scene synchronization and whole-cart overlap checks per player | Remove redundant work as described in finding 5. This is not required for responsiveness. |
| Transmit only populated contact entries | Keep. The custom contact serializer already avoids sending all eight slots. |

The new [server-only incident path](Assets/Game/Runtime/Vehicles/GolfCartNetwork.cs#L295) is also a gameplay authority change: a crash seen only in the driver's prediction no longer supplies an incident report. Near ejection/rollover thresholds, the driver can see an apparent crash without ejection, or receive an ejection based on a different server collision. That is a tradeoff to assess against the project's preference for trusting clients. Preserve a single committed seat transition, but consider accepting a generation-tagged driver incident report if observed crashes should determine gameplay.

## Smaller maintenance concerns

- **Rejected reconcile bodies bypass pooling.** [ReconcileDrive](Assets/Game/Runtime/Vehicles/GolfCartNetwork.Prediction.cs#L129) returns before `PredictionRigidbody.Reconcile` when the generation is invalid, and `CartState.Dispose` is empty. FishNet's [reader](Assets/FishNet/Runtime/Object/Prediction/PredictionRigidbody.cs#L113) obtains a pooled body; its [normal reconcile path](Assets/FishNet/Runtime/Object/Prediction/PredictionRigidbody.cs#L466) returns it. Rejected states lose that reuse and create allocation/GC pressure during handoffs. Return rejected deserialized bodies through the appropriate pool lifecycle without double-returning accepted bodies. The player already has a similar rejection pattern; the cart adds another instance.
- **Every state broadcast carries a full baseline.** [Broadcast](Assets/Game/Runtime/Vehicles/GolfCartNetwork.cs#L262) captures and sends `CartBaseline` even when `resetMotion` is false and observers will ignore it. Passenger-only transitions and recovery-state notifications do not need the physical snapshot. This is a lower-priority bandwidth cleanup because these messages are event driven.
- **Two collider definitions now coexist.** Player contacts and recovery use discovered `Solids`, while bird hits still use the serialized `Chassis` list. The windshield is in the former but not the latter. This was already excluded from the bird chassis list, so it is not a newly introduced bird bug; future collider additions should deliberately specify which interactions they affect.
- **The push-setting tooltip has an incorrect unit.** [GameSettings.cs:15](Assets/Game/Runtime/GameSettings.cs#L15) displays `m/s?`; acceleration should be `m/s²`.

## Visual checks for the user

- Use a host, a remote driver, and a remote pedestrian. Compare pushing, frontal/side hits, airborne hits, and continued contact on all views. Separate and re-contact to check that another launch becomes possible.
- Exit or switch the driver's seat immediately before hitting a pedestrian. Also disconnect the driver during contact. Watch for a missing upward launch, a correction after a locally predicted launch, or a victim temporarily unable to move.
- Watch wheel rotation during remote cart corrections, especially while braking or bumping a player. The wheels should track visible travel without correction-induced reversals.
- Leave an unattended cart on flat ground and a gentle slope, then wake it with a push. Confirm that the resulting parking/rolling behavior is intentional.
- While moving, check seated camera/rider alignment, seat tooltip targeting, horn interaction, recovery placement, and ejection on each client. These now combine physical and smoothed poses.
- Connect a native Xbox controller alongside a Steam-emulated PlayStation/Nintendo controller; reconnect them in a different order and switch the active device. Check both glyph images and button labels, including the bundled-glyph fallback.
