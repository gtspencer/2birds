# Golf Cart Review and Implementation Plan

## Scope

Implement [Golf_Cart_Spec.md](Golf_Cart_Spec.md) with one gameplay cart, four seat targets, the existing two-player sessions, and the six requested components. Keep player spawning and session UI unchanged. Use the imported model as an art source and implement the gameplay in `TwoBirds`.

Exit uses a single **F** press or **right-stick click (R3)**. This control requirement supersedes the spec's hold-to-exit instructions. Entering, switching seats, and recovery remain on **E / north button** and activate on press.

This plan includes required spec clarifications, code integration points, implementation order, and visual acceptance checks. Proposed behavior below is an explicit assumption where the spec leaves a choice open.

## Spec review: ambiguities and likely bugs

### High priority

1. **Ownership transfer needs a protocol, not just a velocity RPC.** The cart-authority section requires one simulator and momentum-preserving handoffs, but does not define when the old simulator stops or the new one starts. Ownership, occupancy, and motion can arrive separately. The installed [NetworkTransform](Assets/FishNet/Runtime/Generated/Component/NetworkTransform/NetworkTransform.cs) clears only some interpolation queues on ownership changes; its packets do not carry the cart's transition revision. `Teleport()` marks a subsequent update and `ForceSend()` requests a resend; neither is a complete receiver-side handoff reset. Define a stop/commit/start handoff and reject motion from previous authority periods, including when ownership returns to the same client. A brief handoff pause must be allowed: interpret the requirement as **at most one active simulator during transfer, exactly one in steady state**.

2. **The server's displayed cart pose is not a suitable handoff snapshot.** `NetworkTransform.TransformData` carries transforms, not Rigidbody linear/angular velocity. The host can also be interpolating behind the received trajectory. Retain one coherent pose/velocity/angular-velocity sample from the simulator, with its epoch and sample tick. Never combine a delayed displayed position with newer velocity. A disconnect can preserve the latest received momentum, but cannot reconstruct motion that the departed driver never reported.

3. **Seating must fence off all movement and impact history.** [PlayerMotor](Assets/Game/Runtime/Player/PlayerMotor.cs), particularly `SetExternalControl`, `ReplicateMove`, and `ReconcileState`, still simulates forces and accepts reconciliation in external mode. Its current generation changes cover ownership/respawn, not seating. An old walking input, reconcile, fall reset, or impact confirmation could otherwise displace a rider or apply after exit. Put a seating revision in movement data, reject mismatched revisions before applying state, clear prediction history at transitions, and start a fresh impact generation.

4. **Exit must run independently of targeted interaction.** [PlayerInteraction.LateUpdate](Assets/Game/Runtime/Player/PlayerInteraction.cs) returns early without a target. Handle the dedicated F / R3 exit action in `PlayerInputReader` so looking at empty space still allows exit. Keep existing press-based interaction for E / north button; no hold timer, tap classification, or release-triggered action is needed. If exit and interact are pressed together while seated, exit takes priority and suppresses the targeted action for that press.

5. **Driver unequip can be undone by inventory prediction or auto-selection.** [PlayerInventory](Assets/Game/Runtime/Inventory/PlayerInventory.cs) automatically selects pickups and replays pending selection operations in `RebuildView`. `HudController` also calls inventory APIs directly. Hiding an item or submitting `SelectSlot(-1)` alone is insufficient. The server's seat commit must force selection to `-1`, update held-item records, and fence stale equip/throw operations; local predicted inventory must enforce the same restriction. The user clarified that drivers may pick up items and drop them directly from inventory. Both drops and throws currently share `ReleaseSlot`/`InventoryOperation.Release`, so add an explicit release intent to preserve that distinction. Leaving the driver's seat restores permission, but the spec does not say whether to automatically re-equip.

6. **The separate item hitbox survives movement suspension.** [PlayerInventory.OnStartNetwork](Assets/Game/Runtime/Inventory/PlayerInventory.cs) detaches `PlayerItemHitbox`; [WorldItemRegistry.BeforePhysics](Assets/Game/Runtime/Items/WorldItemRegistry.cs) keeps moving it, and `WorldItem.SamplePlayerContact` performs contact sweeps independently of collision callbacks. Merely disabling the walking capsule leaves this path active. Define whether thrown items affect riders. Recommended first implementation: riders cannot receive walking impulses while seated; clear pending item impacts and suppress both physical and swept rider contacts until exit.

7. **Pedestrian collision response needs one source of launch velocity.** The spec requires a responsive victim-owned `SubmitWorldImpact` report and no duplicate Rigidbody impulse. A dynamic player colliding normally with a cart before receiving that report can receive both. Remote carts are kinematic, so relying exclusively on `OnCollisionEnter` also produces different detection paths. Use cart/player collision filtering plus a victim-local swept contact detector, closing speed at the contact point, and one contact latch per cart/player pair. Re-arm after separation, not after a repeating timer.

8. **Blocked exits and overlapping ejections need different fallback behavior.** The user clarified that a voluntary exit should search farther away if no player capsule fits beside the seat. Define the wider search bounds and the case where that search also fails. Forced ejections cannot leave occupancy behind, especially during recovery; reserve non-overlapping destinations for all ejected riders. Exit velocity must be sampled at the original seat before moving the player. Otherwise rotation produces the wrong velocity at an offset exit point.

9. **Crash, rollover, and recovery can describe the same incident.** A wall impact can cause a rollover and then `Flip cart`. Without event identity and a recovery latch, this can clear seats and launch riders more than once. Collision severity must exclude throttle/braking/suspension forces and aggregate a tick's compound-collider contacts. Specify rollover angle/duration independently from the lower-speed settled condition for offering `Flip cart`. Recovery ejection should still occur if the crash threshold was never crossed.

10. **“Nearest clear, supported” recovery is underspecified.** A center ground ray can accept a ledge, roof, or placement overlapping a player. Define search radius, candidate spacing/order, allowed height change/slope, support beneath the wheels, and full-chassis clearance. Clarify that nearest means nearest acceptable sampled candidate, not an exact geometric optimum. Stuck recovery must remain latched after driver input disappears; it cannot be recomputed solely from current throttle.

### Other decisions to make explicit

- **View and aim:** [PlayerInputReader](Assets/Game/Runtime/Player/PlayerInputReader.cs) stores world yaw, while [PlayerPresentation](Assets/Game/Runtime/Player/PlayerPresentation.cs) and `PlayerInventory.ReleaseSlot` construct aim separately. Adding cart-relative camera rotation alone would make passenger throws miss the crosshair. Define one world aim pose. Also define entry/switch free-look behavior and retain a stable heading when the cart's forward vector is nearly vertical.
- **Reverse steering:** use signed forward speed with the front-wheel steering angle. Do not additionally invert steer input while reversing; doing both reverses twice. Opposite throttle changes to reverse below a small speed deadband to avoid oscillating near zero.
- **Momentum after exit:** the current motor brakes grounded movement and applies air acceleration immediately. Define a short recovery interval so it does not erase inherited exit momentum on the next tick.
- **Rider appearance:** the player prefab contains capsule graphics, not a seated character rig. Anchor placement and orientation are in scope; new seated animations or a character model are not specified.
- **Recovery priority and access:** recovery must be checked before seat availability, and seated recovery entry must eject riders before targets accept recovery requests. Seat trigger volumes must be reachable without interacting through walls or the solid chassis.
- **Off-map cart:** the player has a fall-boundary respawn, but the cart has no specified equivalent. With automatic recovery out of scope, a cart below the world may be permanently inaccessible. Recommended scope: no automatic cart respawn; level containment is a requirement. Add a cart respawn policy separately if that is undesirable.
- **Host departure:** guest-driver disconnect is recoverable within the current session. Host departure ends the session through `SessionController`; host migration is outside this feature.
- **Cleanup order:** `_tempScripts/GolfCart.cs` and `GolfCartInput.cs` reference absent game namespaces/types such as `TwoBirds.Controls`, `GameInput`, and `PlayerController`. They sit under `Assets` without an enclosing assembly definition excluding them. Archive them outside `Assets` before Unity authoring so reference scripts do not block compilation; delete that archive after replacement. The imported `Scenes/GolfCart.unity` references color prefabs, so cleanup must migrate those references as well. Do not delete paint variants by filename alone: `paintBlack.mat` is used for retained trim.

## Proposed decisions

Single-press F / R3 exit, wider voluntary-exit searches, and allowing driver pickups/inventory drops are requirements below. The remaining entries are proposed implementation defaults where the spec is silent.

| Topic | Proposed behavior |
| --- | --- |
| Voluntary exit with no clear nearby capsule position | Search progressively farther away for a clear, supported capsule position. Start beside the seat, then expand to `2 m`, `4 m`, and `8 m`; if even the bounded wider search fails, retain the seat and consume that attempt. The wider-search behavior is user-selected; `8 m` is the proposed search bound. |
| Driver inventory | Allow pickup into storage, rearranging slots, and direct inventory drops. Block equipping, equipped use, and throws. Suppress pickup auto-selection. Inventory drops use the existing drop-speed behavior without equipping or charging the item. |
| Leaving the driver's seat | Keep selection at `-1`; the player deliberately equips again. |
| Seated item impacts | Suppress walking impulses and rider item hitboxes; item-triggered rider ejection is outside this scope. |
| Seat requests | Server-confirmed attachment, with immediate local input cancellation while pending. Driving is local once the handoff commits; no speculative second cart simulation. |
| Exit input | A single F / right-stick click (R3) press requests exit while seated, regardless of look target. No hold duration or release delay. Holding the button does not repeat requests; another attempt after refusal requires a fresh press. Ignore exit while on foot, gameplay is blocked, or a seat transition is pending. |
| Targeted interaction | E / north button activates the current valid target on press for entry, switching seats, recovery, and ordinary interactions. Exit wins if both actions are pressed together while seated. |
| Camera entry/switch | Set free-look yaw offset to zero relative to the destination seat; preserve look pitch. Subsequent cart heading changes preserve that offset. On exit keep the resulting world yaw/pitch. |
| Rider orientation | Graphics follow the full seat pose; the camera uses seat position with level yaw plus free-look pitch. Rear anchors face backward. |
| Voluntary exit momentum | Initialize world velocity once from the seat, with `0.2 s` of motor acceleration suppression. |
| Forced exit fallback | Search farther than voluntary exits; if no clear supported local position exists, use the player's existing spawn location after checking capsule clearance. If temporarily blocked, complete unseating into a non-colliding placement-pending state and retry placement; do not leave an occupant in a recovery cart or materialize inside geometry. |
| Recovery placement | Search within `4 m`, sample on a `0.5 m` grid sorted by displacement, preserve the last valid planar heading, and place world-upright. Require suitable wheel support and chassis clearance. Keep recovery available if none fits. |
| Color setter | A server-side setter is the canonical API. A caller on a client forwards its requested color to the server, which replicates the stored color. No customization UI. |

## Code and asset changes

### New files

| Path | Responsibility |
| --- | --- |
| `Assets/Game/Runtime/Vehicles/GolfCartController.cs` | Tick-based forces, suspension samples, collision severity, pedestrian contact sampling, rollover/stuck detection, recovery candidate queries. |
| `Assets/Game/Runtime/Vehicles/GolfCartNetwork.cs` | Seat transactions, simulator handoffs, coherent motion samples, recovery/ejection commits, late-join state. Keep small message structs here. |
| `Assets/Game/Runtime/Vehicles/CartSeat.cs` | Plain `MonoBehaviour` implementing `IInteractable`, with fixed index, rider/eye/exit anchors and trigger target. |
| `Assets/Game/Runtime/Player/PlayerSeating.cs` | Local application of accepted seat state, motor suspension, collision exclusions, driver restrictions, seat attachment and exit placement. No independently replicated occupancy. |
| `Assets/Game/Runtime/Vehicles/GolfCartPresentation.cs` | Displayed pose access, wheel/steering visuals, cached paint-slot property blocks. |
| `Assets/Game/Runtime/Vehicles/GolfCartSettings.cs` | Requested designer-facing physics, impact, ejection, and recovery tuning. |
| `Assets/Game/Prefabs/GolfCart.prefab` | Standalone gameplay prefab built from the blue cart art. |
| `Assets/Game/Settings/GolfCartSettings.asset` | Initial cart tuning assigned to the prefab. |

### Existing integration points

| File | Required changes |
| --- | --- |
| `PlayerMotor.cs` | Add seated state/revision, suspend tick/replicate/reconcile effects, clear history and pending forces, seed exits, reset impact generations. |
| `PlayerInputReader.cs` | Route raw cart-relative move and held handbrake separately from walking; handle untargeted press-to-exit; retain look; clear all context-sensitive input and pending item use on transitions/menu/focus loss. |
| `PlayerInteraction.cs` | Cache actions; resolve seat triggers behind no solid obstruction; retain press-based interaction and suppress it when exit takes priority or a seat transition is pending. |
| `PlayerPresentation.cs` | Cache `NetworkTickSmoother`; stop it while seated; update rider graphics and seated camera from the same cart pose; expose canonical world aim. |
| `PlayerEquipment.cs` | Guard all begin/end/release paths and cancel active use on driver entry. |
| `PlayerInventory.cs` | Deterministic unequip, driver API/commit/prediction restrictions, explicit drop-versus-throw release intent, operation revision, cart point velocity and canonical aim in `ReleaseSlot`. |
| `PlayerNetworkState.cs` | Clear server/public charging state on driver entry and reject stale charge requests using the seating revision. |
| `PlayerItemHitbox.cs`, `WorldItemRegistry.cs`, `WorldItem.cs` | Suppress seated hitbox movement/contact sampling and queued impacts; restore/rebase them on exit. Mask the driver's held-item presentation from delayed records. |
| `HudController.cs` | Disable driver equip controls while retaining inventory rearrangement and drag-out drops; inventory APIs remain the final guard. |
| `Assets/InputSystem_Actions.inputactions` | Add `Player/ExitVehicle` as a Button action bound to `<Keyboard>/f` and `<Gamepad>/rightStickPress`; no Hold/Tap interaction or initial-state activation. |
| `Assets/Game/Prefabs/Player.prefab` | Add and wire `PlayerSeating`; retain the existing player ownership and prediction configuration. |
| `ProjectSettings/TagManager.asset`, `ProjectSettings/DynamicsManager.asset` | Cart and seat-query layers plus explicit collision rules. Extend affected ground/environment query masks only where needed. |
| `Assets/Scenes/Game.unity` | Place one gameplay cart as a scene network object. Do not add spawn points or change session capacity. |
| FishNet `NetworkTransform.cs` | A narrow opt-in motion-epoch/reset extension described below; preserve default behavior for other transforms. |

Use existing `Player/Move`, `Player/Look`, `Player/Jump`, and `Player/Interact` bindings, plus the new `Player/ExitVehicle` action in the same map. Space/south is interpreted as handbrake in the driving context; a new action map is unnecessary. Reuse `InteractionTooltip` and its existing glyph handling.

Right-stick click is unassigned in the current Player/UI bindings and direct HUD input handlers, and `SteamInputGlyphs` already maps its glyph. It is separate from the right-stick look axes. Keep east button available for Crouch/UI Cancel, west for Attack, north for Interact, south for Jump/handbrake, shoulders for hotbar cycling, select for inventory, right trigger for Use, and D-pad down for Drop.

## State and networking contract

### Seat state

- Fix seat indices: `0` driver, `1` front passenger, `2` rear left, `3` rear right. Replicate player `NetworkObject` identities, not client IDs; derive driving owner from seat `0`.
- Keep one four-entry occupancy snapshot on `GolfCartNetwork`, with a monotonically increasing state revision and recovery enum (`None`, `Flipped`, `Stuck`). `PlayerSeating` holds a local derived reference and last applied player transition revision, not a second synchronized occupancy list.
- Send seat requests through the player's owned network object, carrying request ID, expected player transition revision, cart reference, and destination index or exit action. Server processing resolves one request at a time and rechecks destination availability/recovery state before committing.
- Apply a reliable transaction containing the whole resulting occupancy and all affected player transitions. Each transition contains its new player revision and impact generation; exits additionally carry chosen pose, initial velocity, and any ejection velocity change. Publish current occupancy/state to joining observers through `OnSpawnServer`; do not buffer and replay old ejection impulses.
- Keep transition IDs after exit so an obsolete occupied snapshot cannot re-seat an on-foot player. Initialize the revision of an already on-foot player for a joining client through `PlayerSeating.OnSpawnServer` as well.
- Resolve cart/player spawn order by retaining the latest unresolved current snapshot and applying it when referenced objects register. Unregister on despawn. Reapplying an equal revision is a no-op, including on the host.
- A failed switch retains the source seat and restores its input permissions. Serialize an active authority handoff with further requests; reject stale requests with the current state instead of granting two transitions against one source seat.

### Motion and authority

- `SessionRoot.prefab` already configures FishNet for `60 Hz` and TimeManager-driven physics. Apply forces through normal tick/pre-physics callbacks and collect post-physics samples; never call a second `Physics.Simulate` or drive from `FixedUpdate`.
- Configure one client-authoritative `NetworkTransform`: position and rotation only, no parenting/scale synchronization, no extrapolated cart simulation. Disable its automatic Rigidbody configuration so `GolfCartNetwork` alone controls when the body becomes dynamic. Add `OfflineRigidbody` for the cart Rigidbody only and bind it to the session's PredictionManager.
- Use one display path per peer: the simulator's Rigidbody interpolation on the controlling peer, and `NetworkTransform` interpolation on followers. Do not stack a cart tick smoother on top. All displayed art, seat targets, riders, and the camera read the resulting same pose.
- Store motion as `{epoch, simulatorTick, position, rotation, linearVelocity, angularVelocity}` from a single post-physics sample. Use simulation values for server takeover, and presentation-aligned samples for passenger velocity/visuals. An interpolated kinematic body's `linearVelocity` is not the data source.
- Extend the installed sealed `NetworkTransform` with an opt-in epoch and receiver reset facility, plus a small optional motion payload for the cart. A subclass cannot implement this because the class is sealed and receive/reset internals are private. Write/read the epoch before accepting transform deltas, and provide same-sample velocity/angular velocity through the optional payload. This keeps one trajectory stream instead of introducing a second cart position stream.
- The extension must cover owner-to-server, server-to-observers, initial targeted snapshots, cached owner-data relay, and teleport/full-state sends. Default transforms retain the existing format and behavior. Install a reliable epoch baseline before applying its deltas; hold only the newest future-epoch full sample if it precedes the baseline. Drop older epochs before changing delta baselines or interpolation queues.
- Resetting an epoch clears the current interpolation goal, queued goals, cached relay data, rate/delta baselines, and previous-owner tick filters, then seeds them from the full handoff snapshot. Old data arriving after this reset is rejected by epoch. `ForceSend()` and `Teleport()` supplement this operation; they do not replace it.
- Initially send changing cart motion at `20 Hz` through the existing tick interval, with change thresholds and a reliable final resting sample, including zero velocities. Include compact steering/handbrake/suspension presentation values with the same sample only as needed. Wheel spin can be derived from signed traveled distance; no per-rider transform streams.

### Handoff sequence

1. The server reserves the requested driver transition while keeping the currently committed occupancy. Ask the old simulator to stop at a normal physics boundary and return its final full motion sample. Freeze only after capturing that sample.
2. On the final response, the server commits the new epoch, occupancy, affected player transitions, and coherent motion baseline; changes ownership; and distributes the baseline reliably. When the server is the old simulator, this is a local stop/capture operation.
3. The new simulator remains kinematic until it has both matching ownership and the committed epoch/baseline. Seed pose and both velocities, clear old input, then enable physics at its next normal tick. A peer never infers permission solely from occupancy arriving first.
4. When there is no driver, the server follows the same sequence and seeds its Rigidbody from retained simulation data, not from its interpolated transform. Passenger-only seat changes do not restart cart motion.
5. Enable `NetworkObject`'s **Prevent Despawn On Disconnect** setting. Handle `ServerManager.Objects.OnPreDestroyClientObjects` to clear the departing rider before player destruction and reclaim a departing driver's cart. Use the newest retained sample if no final response exists. Passenger disconnects free only that seat.
6. For an unresponsive but still connected simulator, abort the pending voluntary switch and restore the committed state; do not start a competing simulator while the old one can still legitimately run. Disconnect cleanup uses the server's normal disconnected-peer boundary. Session shutdown cancels pending operations and unsubscribes callbacks.

## Implementation order

### 1. Prepare the art and tuning definition

- Archive the disposable reference scripts outside `Assets` without creating a migration tool. Do not port their missing interaction/input/player systems or out-of-scope horn behavior.
- Create `GolfCartSettings` with fields grouped by suspension, drive/steer, handbrake, pedestrian launch, ejection, and recovery. Use units in Inspector labels/tooltips; collision/ejection velocity changes are in `m/s`, distances in meters, delays in seconds.
- Create the standalone gameplay prefab through Unity, retaining the imported mesh and necessary shared materials. Do not make the gameplay prefab depend on a color prefab scheduled for deletion.
- Add one root Rigidbody/NetworkObject/NetworkTransform/OfflineRigidbody, the controller/network/presentation components, simple compound chassis colliders, four suspension probes, and four seat components with separate rider/eye/exit anchors. Cache collider/render/anchor arrays during initialization.
- Use named imported wheel transforms `wheel_f_l`, `wheel_f_r`, `wheel_b_l`, `wheel_b_r` and `steeringWheel`; cache their rest rotations. The blue paint GUID maps to `paintBlue.mat`, used on the cart body and storage tray; assign exact renderer/material indices in the prefab.
- Set cart collision rules so chassis contacts environment/items but has no ordinary physical response against walking-player or player-item-hitbox colliders. Seat trigger targets are query-only. Exclude all cart self-colliders, seat triggers, players, and items from suspension support queries.

### 2. Implement driving and network motion

- At each grounded probe, apply a non-negative spring/damper load at the suspension point. Use point velocity along the suspension axis for damping. Clamp lateral/longitudinal tire force by that wheel's load; apply no wheel drive/grip force when unsupported.
- Apply drive/braking along wheel headings. Opposite throttle brakes until signed forward speed is within the reverse deadband. Use speed-dependent front steering and signed travel speed; do not apply parked yaw torque.
- Apply handbrake braking/grip reduction to rear wheels, and recover grip progressively after release. Neutral input stops applying drive/handbrake forces; it does not erase existing cart velocity. Keep the unoccupied cart able to coast and roll downhill.
- Implement the motion-epoch extension and cart network lifecycle before seat-driven ownership changes. Cache the simulator predicate at state transitions and prevent physics/event reporting during prediction replay.
- Collect suspension compression and steering state for presentation. Followers consume reported samples; do not raycast independently to choose a different remote wheel pose or recovery state.

### 3. Implement seat transactions and motor suspension

- Add `PlayerSeating` to the player prefab and cache motor, body/colliders, input, presentation/smoother, equipment, inventory, and detached item hitbox references.
- Add a distinct `MovementMode.Seated` and player transition revision to `MoveInput` and `MotorState`. While seated, skip sending walking inputs/reconciles and guard received replicate/reconcile methods. Do not let a future movement packet establish a seat transition; wait for the reliable transaction.
- On accepted entry/switch, clear jump/drive/use/interaction/exit state; require a fresh exit press after entry; clear `PredictionRigidbody` forces/velocities, replicate/reconcile history via `ClearReplicateCache()`, queued impacts, and item contact samples. Establish the transition's impact generation on the server and clients so old impact messages cannot revive themselves.
- Make the walking body kinematic, suspend its collision participation, and stop independent graphics smoothing. Keep the player's NetworkObject at scene root; set its local rider pose from the cart rather than network-parenting it beneath the cart. Preserve player ownership.
- Guard replay pauser callbacks so an old `Unpause()` cannot restore dynamic physics while seated. Guard `PlayerPresentation`'s reset-revision path so it cannot restart the smoother until the player is on foot.
- Query exit capsule clearance using the actual player collider dimensions and the displayed/sampled seat pose. Search nearest candidates first and widen the search when the seat-adjacent exits are blocked. Check support and avoid selecting a roof or the far side of a solid wall merely because the destination capsule fits. Send the owner-selected voluntary exit candidate and seat point velocity with the request; the server accepts the client's report, serializes the occupancy change, and distributes one resulting pose. Still handle a fully blocked wider search and competing ejection placements for consistency.
- On exit, seed pose and initial velocity once on server/clients, restore motor simulation and the item hitbox, rebase the graphics to the exit pose, restart its smoother, and apply the short momentum-preservation interval. Clear prediction history before accepting new walking input.
- Ignore the source cart's pedestrian-contact detector until the player's full capsule has separated from it. Use the same exclusion for forced exits; clear it on despawn/respawn. This is in addition to chassis/player physical collision filtering.

### 4. Integrate input, targeting, camera, and equipment

- Route `Move` directly to cart throttle/steer for the driver; walking continues using world-facing movement. Passengers consume no walk/jump commands. Read handbrake as held state, not the existing one-shot `jumpPending` edge.
- Cache `Player/ExitVehicle` with the other actions and consume its press edge (`WasPressedThisFrame`) while seated and gameplay-active. Request exit without waiting for release or inspecting an interaction target. Suppress further exit requests while the transition is pending, and suppress targeted interaction in the frame exit is requested. Clear pending item use on exit through the existing transition path.
- Clear drive values when `InventoryOpen` becomes true, `SetGameplay(false)` runs, focus/device is lost, ownership changes, or the player leaves the driver seat. Clear pending exit presses and cancel/re-arm item use; require release and a new press for exit/interact buttons already held when gameplay resumes. Re-read continuous movement from the current device state when gameplay resumes.
- For targeting, find the nearest solid obstruction separately from seat triggers. Select the closest eligible seat trigger before that obstruction, while preserving ordinary item interactions. Handle ray origins inside a seat target with a small overlap query. Use finite cached query buffers and treat overflow as blocked, not permission to target through an omitted wall.
- Keep E / north-button interaction on its existing press edge against the current target. Do not add tap/hold classification or release dispatch. Exit input is handled independently and takes priority over a simultaneous seat/item interaction.
- Update cart display first, rider attachment and canonical camera aim next, targeting next, then tooltip rendering. Cache the `InputAction` references instead of looking them up each frame.
- Compute seat heading from its planar forward direction, retaining the previous valid heading near vertical. Set camera position from the eye anchor and orientation from heading plus yaw offset/pitch with zero roll. Make `PlayerInventory.ReleaseSlot` use this same aim pose even if invoked earlier than camera `LateUpdate`.
- Provide one explicit inventory unequip operation that sets selection to `-1` without changing slot contents. Apply it as part of the server seat transaction and locally as soon as that transaction applies. Cancel charging and call `WorldItemRegistry.UpdateEquipment(holder, 0)` so all clients' held records show empty hands.
- Include player transition revision in inventory/charging operations that can equip or release an item. Reject stale equip/release requests and reconcile their predicted effects through the existing inventory acknowledgment/rollback path; a new inventory-drop request using the current revision remains allowed while driving. Rebuilding a predicted driver inventory must keep effective selection `-1`, including pending pickups and swaps.
- Add a compact release-intent field (`Drop` or `Throw`) to the existing release request. `DropSlot` selects `Drop` and the configured drop speed; `ReleaseEquipped` selects `Throw`. Enforce the driver predicate in both inventory and equipment public APIs and server commit logic: driver drops from a named inventory slot are allowed, driver equipped releases/throws are refused. Keep stack-drop behavior and quantities unchanged.
- Apply a presentation guard in `WorldItem.AttachHolder`/`PresentHeld` so an older equipped record cannot make a seated driver's hands visible while the unequip update is in flight.
- For all seated releases, including driver inventory drops, use `v_point = v_centerOfMass + cross(angularVelocity, riderPosition - centerOfMass)` from the presentation-aligned motion sample. Preserve the item's existing `VelocityInheritance` multiplier. Transform the configured center of mass with the matching sample pose; do not substitute the cart root pivot. Only passengers can take the throwing path.

### 5. Implement pedestrian hits and ejection

- Detect cart crossings on the on-foot victim's client by sweeping the victim capsule relative to the displayed cart chassis between samples, including initial overlap and rotational movement. Subdivide large rotations to avoid missing a swinging corner. Reset contact history on epoch/teleport/revision changes so discontinuities do not count as impacts.
- Compute `closingSpeed = max(0, dot(cartPointVelocity - playerVelocity, intoPlayer))`. Above the minimum hit speed, form a velocity change from closing speed, the collision multiplier, and upward lift. Submit once through `PlayerMotor.SubmitWorldImpact`; never also call `AddForce` on the player or report the same hit from the driver's peer.
- Latch all compound chassis contacts as one cart/player contact. Keep latched until full separation with a small clearance margin. Skip seated players and recent exits still overlapping their source cart. No detection/reporting during prediction replay.
- Accumulate actual collision impulse/severity across the simulator's normal physics step. Use impact-induced `delta-v` (collision impulse divided by cart mass, with a documented aggregation rule), not total pre/post-tick velocity difference that includes braking/suspension. Apply one major-crash threshold decision after all contacts for the tick.
- Detect sustained rollover with angle plus time; separately track settled linear/angular speed for recovery. Ordinary airborne motion neither increments stuck time nor triggers rollover merely because one wheel loses support.
- Report a single incident containing epoch/event sequence, current seat revisions, pre-impact point velocities, and per-rider ejection deltas. The server ignores duplicates/stale incidents and commits all affected seats in one transaction. Preserve pre-impact momentum for a crash launch instead of sampling an already-stopped cart.
- After the accepted transaction makes a rider on-foot and establishes its new impact generation, that rider's owner calls `SubmitWorldImpact` once for the transaction's ejection delta. Base inherited velocity is already seeded by the transition. The server does not additionally `QueueWorldImpact` for the same rider/event; the existing owner report/confirmation path supplies the gameplay impulse. The host executes this once as owner. Track applied transition IDs to suppress duplicate submissions.

### 6. Implement recovery and durable appearance

- Accumulate stuck time only with meaningful drive intent, no handbrake, stable support, no landing grace period, and negligible horizontal displacement over the measurement window. Reset detection on progress; do not confuse wheel spin, speed against a wall, or steering at rest with progress.
- Enter `Stuck` or settled `Flipped` through one server transaction that latches recovery, ejects any remaining riders once, and returns authority to the server. `Flipped` takes precedence. Clearing throttle or losing the driver does not clear the recovery latch. Actual regained motion/upright support can clear it under a separate sustained-clear condition.
- Have `CartSeat` resolve recovery text/action before occupancy. Recovery requests require an empty cart and matching state revision. Disable entry while recovery is available or being applied.
- Search candidate placements nearest-first, starting at the current position for a flip. Reject excessive vertical shifts, roofs reached only by a ray from above, steep/unsupported wheel positions, chassis overlap, and player overlap. Use the authored chassis shapes and suspension geometry instead of a single root-point or oversized bounding-box approximation.
- On success, start a new motion epoch, teleport to the accepted upright pose, clear both velocities, reset suspension/contact/stuck histories, and only then clear recovery. Failure leaves the cart pose and recovery state intact. Do not automatically teleport a cart just because detection fired.
- Apply color with cached `MaterialPropertyBlock` instances and explicit renderer/material indices using the retained paint shader's `_BaseColor`. Preserve other property-block values and shared materials. Replicate current color as a state change and initialize late joiners from the stored value.

### 7. Complete Unity authoring and asset cleanup

- Use Unity CLI where available, otherwise Unity MCP, for prefab/component/asset authoring. Let Unity create metadata. Do not create a one-off editor migration tool.
- Place one cart prefab instance in `Assets/Scenes/Game.unity` and configure its scene NetworkObject. Keep the existing two-player spawner and session settings. Use Unity's ordinary network-object/prefab registration workflow where required by the authored object.
- Migrate references from obsolete imported color prefabs, including the imported demo scene. Preserve the mesh, paint material chosen for runtime tinting, and shared glass/metal/light/trim materials.
- Remove only replaced, unreferenced variants and the archived temporary scripts. Preserve unrelated local material/project-settings edits; cleanup does not justify reverting them.

## Initial tuning to expose for visual adjustment

Use these as starting design values, not physics acceptance guarantees. Driving spring/load values depend on the authored cart mass and suspension dimensions.

| Setting | Starting value or rule |
| --- | --- |
| Mass/spring balance | Choose cart mass first; each wheel's static spring force supports roughly one quarter of its weight at the intended ride height. |
| Opposite-throttle deadband | `0.25 m/s`. |
| Grip restoration | Approximately `0.35 s` from handbrake grip to normal grip. |
| Pedestrian minimum closing speed | `1.5 m/s`; tune multiplier/lift separately. |
| Major-crash ejection | Start at `7 m/s` collision-induced velocity change; tune against actual routine landings. |
| Rollover | More than `100 degrees` from upright for `0.6 s`. |
| Settled flip | Linear speed below `0.5 m/s` and angular speed below `0.5 rad/s` for `0.5 s`. |
| Stuck | Drive input magnitude at least `0.5`, less than `0.2 m` horizontal progress for `2 s`, excluding `0.75 s` after landing. |
| Recovery-clear hysteresis | Require sustained upright support/progress before clearing; entry and clear predicates must differ. |
| Recovery support | Begin with maximum `30 degree` support slope and require all four wheels within usable suspension reach at the upright candidate pose. |

## User visual acceptance after implementation

Use host and guest sessions, repeat with each as driver, and keep the two-player limit. Exercise each of the four seat indices in successive runs.

- Drive forward/reverse, brake before reversing, steer while parked, take normal corners/bumps/jumps, and recover from a deliberate handbrake drift. Steering and wheel motion should match travel direction.
- Observe moving driver/passenger and outside views across separate runs. Riders, targets, wheels, and camera remain aligned to one cart; rear seats face backward and free look keeps a level horizon even during rollover.
- Press E / north button to enter/switch or recover. Press F / R3 once while aiming at another seat, an item, or empty sky: exit is requested immediately without a hold or release delay. Keeping exit pressed does not repeat requests, and releasing it performs no action. Simultaneous exit/interact chooses exit only. Competing seat requests leave one winner, and a refused switch retains the original seat.
- Check F / R3 from each seat; R3 must not trigger another action, and right-stick look remains available. Exit while on foot has no effect.
- Open inventory/menu, lose focus, and disconnect the controller while holding throttle/handbrake/use/interact/exit. Resume without stale steering, launch, jump, or exit actions; held exit/interact requires a new press.
- Enter the driver seat while charging a throw, then use hotkeys, controller selection, inventory dragging, and pickup. Both clients see empty hands, unequipping preserves quantities, and old pending requests cannot equip or throw after the transition. Pickups enter storage; direct inventory drops release exactly the requested item/stack without equipping or charging it.
- As a passenger, throw while turning/reversing and while looking away from the cart heading. The throw follows the crosshair and inherits point velocity, including rotational motion.
- Exit and change drivers while moving; rapidly re-enter; disconnect the guest driver; join while the host drives. Momentum is retained from the accepted samples, the cart survives guest departure, and old movement does not snap riders/cart backward.
- Hit an on-foot player from the front, side, and a rotating corner. Sustained contact launches once; after separating, another hit works. Exiting/ejected riders are not immediately hit by the source cart.
- Compare ordinary collisions with major crashes and sustained rollovers. Multiple chassis contacts or a crash followed by flip recovery produce only one ejection per rider transition.
- Attempt exits beside walls and force ejections in cramped geometry. Voluntary exits search farther for a clear position when nearby exits are blocked. A fully blocked wider search and the forced-placement fallback behave as specified without overlapping riders or teleporting through walls.
- Check recovery near slopes, low roofs, ledges, terrain edges, walls, and other players. Tooltips agree between clients, remain available after driver exit, respect flip priority, and recovery either places the empty cart clearly or leaves recovery available.
- Change runtime body color and join afterward. Only assigned body panels change; lights, glass, trim, shared assets, and other carts in the imported art scene retain their intended materials.
