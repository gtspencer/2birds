# Carry and throw players

## Objective and decisions

Let players pick up, carry, and throw other players. The carried player's upright model follows in front of the carrier, suggesting a bear hug without animation or IK. The carried player retains camera look but cannot move, interact, use items, or manipulate inventory. Hold Use to charge a throw; release Use to throw. Item impacts and qualifying cart hits cause a drop.

Trust client-reported release poses and velocities. The server arbitrates conflicting transitions and distributes consistent state. Derive attachment poses locally from already-networked carrier movement.

- **Carrier movement**: Normal walk, jump, and stamina-limited sprint. No carry penalty.
- **Drop**: Nearest clear capsule position in front of or beside the carrier, with zero initial velocity. Gravity and subsequent collisions still apply.
- **Impact drop**: Capture placement before the relevant movement and detach local presentation promptly. Do not transfer the carrier's impact impulse to the carried player.
- **Cart entry**: Seat both players atomically. Prefer the paired front/rear seat, otherwise another free seat. If two seats are unavailable, preserve carry and show feedback.
- **Pickup immunity**: Approximately two seconds after a world release.
- **Air pickup**: Allowed during jumps and launches. Airborne releases must not snap to the ground.
- **No stacking**: Both participants must be free and unseated before pickup. Conflicting transitions and pending placement block pickup.
- **Menus**: Normal menu access remains available. Gameplay inventory actions are blocked while carried, including actions from an already-open inventory.

No ragdoll, animation, IK, damage, scoring, progression, or player kill-volume system is in scope. Preserve existing item, bird, and cart physics. All paths are relative to the repository root; follow `AGENTS.md`.

## Repository map

| Area | Files and relevant behavior |
| --- | --- |
| Motor | `Assets/Game/Runtime/Player/PlayerMotor.cs`: prediction, replay, reconciliation, impact generations, seating placement, recovery, and fall reset. |
| Cart contacts | `Assets/Game/Runtime/Player/PlayerMotor.CartContacts.cs`: contact detection, `pendingCartLift`, and `cartRecovery`; cart launches bypass `ApplyImpacts`. |
| Interaction | `Assets/Game/Runtime/Player/PlayerInteraction.cs`: obstruction raycast, combined seat/horn target query, and camera-origin overlap query. |
| Input | `Assets/Game/Runtime/Player/PlayerInputReader.cs`: look, movement, Use/Drop, and `ClearContext` held-button suppression. |
| Inventory input | `Assets/Game/Runtime/Player/InventoryInputHandler.cs`: inventory toggle and hotbar selection/cycling outside movement input. |
| Equipment | `Assets/Game/Runtime/Player/PlayerEquipment.cs`: Begin/End/Cancel use and local charge UI properties. |
| Inventory | `Assets/Game/Runtime/Inventory/PlayerInventory.cs`: direct UI drop/swap, predicted requests, permissions, and held-item presentation. |
| Seating | `Assets/Game/Runtime/Player/PlayerSeating.cs`: requests, snapshots, unresolved references, physical/visual attachment, and capsule clearance. |
| Presentation | `Assets/Game/Runtime/Player/PlayerPresentation.cs`: smoother, graphics, and camera. Normal `AimPose` already provides the carried camera behavior. |
| Public state | `Assets/Game/Runtime/Player/PlayerNetworkState.cs`: item charging state currently guarded by seating revision. |
| Item hitbox | `Assets/Game/Runtime/Player/PlayerItemHitbox.cs`: suspension and owner-reported item impacts. |
| Charge pattern | `Assets/Game/Runtime/Items/ThrowableItemUse.cs`: charge timing and cancellation. |
| Cart networking | `Assets/Game/Runtime/Vehicles/GolfCartNetwork.cs`: queued `PendingChange`, commit-time checks, multiple seat transitions, driver handoff, and recovery. |
| Cart seats | `Assets/Game/Runtime/Vehicles/CartSeat.cs`: targeting and recovery prompts. |
| UI/settings | `Assets/Game/Runtime/UI/HudController.cs`, `InteractionTooltip.cs`, and `Assets/Game/Runtime/GameSettings.cs`. |

## 1. PlayerCarry and control state

Add `Assets/Game/Runtime/Player/PlayerCarry.cs` as a `NetworkBehaviour` on the player prefab root. One component handles `Free`, `Carrying`, and `Carried` roles.

Cache the motor, input, seating, presentation, inventory, equipment, network state, hitbox, and capsule references at initialization. Cache the partner when installing a relationship; do not look it up every frame. Maintain an ObjectId lookup and owner-local reference through network start, ownership changes, and teardown, following `PlayerSeating`.

Local state consists of role, partner, immunity deadline, an explicit charge flag/start time, and one pending request ID. Keep speculative release presentation separate from committed gameplay state.

### Revisions

Use one server-assigned per-player **control revision** for carry start/end, seating changes, and forced placement/reset. Keep seat/cart revisions for occupancy transactions; do not pass independent carry counters into `SeatingRevision`.

Use the control revision in movement/reconcile guards and action-request guards currently tied only to seating. Seat transitions include the resulting control revision alongside their seat revision. This prevents delayed pre-carry movement, inventory actions, or item-charge messages from becoming valid after release.

A carry transition identifies both participants and each resulting control revision. Preserve the carrier ID on release and use an explicit transition kind. Include the carried player's server-assigned impact generation when suspending/restoring its motor. Do not confuse that generation with the carrier's.

Updating a mobile carrier's action context must preserve its velocity, pending impacts, and cart contacts. Keep context revision changes separate from the motor suspension/placement operation applied to the carried player.

## 2. Interaction detection

Use the existing player root `CapsuleCollider`. No carry trigger, new layer, or collision-matrix change is required.

Keep Player excluded from the obstruction raycast. Include Player in the existing combined target-query mask alongside CartSeat and GolfCart. Reuse the non-alloc raycast and overlap buffers; rename the trigger-only helper/mask to reflect that it also accepts player capsules.

Preserve seat and horn selection, camera-origin overlap handling, and full-buffer behavior. Accept a player only through its physical capsule and `PlayerCarry`. Exclude the local capsule and ineligible players before choosing the nearest target, so an unusable player does not hide another interaction.

Implement `IInteractable` with `ActionText = "Pick up"` and `InputActionPath = "Player/Interact"`. Consider both target and local carrier eligibility when selecting the tooltip:

- Both are different, active, spawned players.
- Both are free, unseated, and not awaiting placement or conflicting transitions.
- Target immunity has expired.
- Requester has active gameplay input.

Repeat state/revision checks on the server for race resolution. Do not add anti-cheat distance reconstruction.

## 3. Requests, commits, and current-state snapshots

### Pickup

1. Owner sends request ID, target ID, and both expected control revisions through its own carry component. Carrier identity is implicit in the owned RPC.
2. Server checks eligibility, advances both control revisions and the carried player's impact generation, and applies the pair transition locally.
3. Broadcast the committed transition reliably. Host application happens once; an ordinary `ObserversRpc` is not server-side application.
4. Return success/failure to the owner, following the seating result pattern. Ignore stale request completions.

Pickup waits for server arbitration; do not speculatively attach another player. Keep conflicting local requests blocked while pending, but allow carrier movement.

### Release

Owner sends request ID, partner ID, both expected control revisions, and captured release data. Trust finite client placement/velocity; reject a stale relationship or conflicting committed transition. Server assigns revisions/generation, applies release, then broadcasts.

Use separate pickup/release payloads. Pickup needs no pose. Release needs position, upright yaw, velocity, and recovery duration or ticks, not a full quaternion.

Allow only one pending release per relationship. Repeated Drop, Use release, or impact notification must not send more requests. Duplicate commits must not apply another launch. Rejection or a newer seat/reset transition resolves pending presentation against the latest committed state.

### Initial state and late join

Consolidate current carry/seating state with `PlayerSeating.OnSpawnServer`'s current-state delivery. Independent initial seating and carry messages must not each overwrite the motor.

Send current control revision, attachment mode/partner or cart, relevant generation, and free/pending placement. Include remaining immunity when nonzero. Active carry requires no continuously updated attachment pose.

Defer attachment until referenced objects exist, retain only the newest applicable state, and keep simulation suspended while unresolved. Resolve on registration rather than polling. Repeated snapshots must not overwrite newer state or restart release velocity.

Use transition broadcasts for existing observers and snapshots for new observers. Buffering the last pair event alone cannot represent subsequent partner changes or seating correctly.

## 4. Shared motor suspension and placement

Extract the existing seating suspension/placement behavior into a small shared operation used by carry and seating. Keep attachment relationships in their existing components; do not build a generic attachment framework.

Suspension must clear obsolete replicate history, predicted forces, impacts, recovery, and cart contacts for the carried player. Install its control revision/generation, make its body kinematic, disable its capsule, suspend its item hitbox, stop its smoother, and clear input/action context.

Extend all motor guards currently based only on `Seated`, including tick simulation, replicate sending, reconcile creation/application, replay rigidbody pause/unpause, impacts, and cart contact processing. Default input and `SetExternalControl` alone are insufficient.

Restoration installs position, yaw, velocity, generation, revision, and recovery together, clears obsolete prediction state, and restores physics/hitbox/smoothing. Apply velocity once. Carry-to-seat stays suspended throughout, without a transient drop or smoother restart.

## 5. Attachment presentation and camera

Keep the graphics hierarchy unchanged. Manually follow the carrier while carried; do not both reparent graphics and assign their world pose.

- Physical body placement derives from the carrier's physical body plus offset.
- Graphics placement derives from the carrier's smoothed graphics plus offset.
- Update attachment before the presentation camera, following seating's execution order.
- Keep the carried smoother stopped until normal movement resumes.
- Limit frame updates to active attachment or pending release presentation.

Initial offset is `(0, -0.1, 0.7)`. This is an attachment offset, not safe release clearance: the disabled capsule can overlap the carrier or floor while held.

Reuse normal `PlayerPresentation.AimPose`, which already uses graphics position plus eye height and independent input yaw/pitch. No carried camera branch is needed. Preserve local-body hiding; assess carrier visibility while tuning the offset before adding any camera-specific renderer handling.

## 6. Throw, drop, and safe placement

### Charge and cancellation

Route equipment Begin/End/Cancel use through `PlayerCarry` while carrying, before item-equipping guards. Expose local carry charge through `PlayerEquipment.IsCharging` and `Charge01` for the existing HUD.

```
charge = clamp01(elapsed / PlayerThrowChargeTime)
speed = lerp(PlayerThrowMinSpeed, PlayerThrowMaxSpeed, charge)
velocity = aimForward * speed + carrierVelocity * PlayerThrowVelocityInheritance
```

Cancel on Drop, impact, seating, reset, menu/gameplay suppression, ownership loss, and disconnect. Selection changes must also cancel carry charge through the common equipment path. Cancellation must never throw a player or release an item. Ignore repeated BeginUse while charging/pending release.

Do not replicate carry charge start/progress/end: the HUD is local and no remote charge animation is in scope. Clear previous item charging on carry entry and reject stale item-charge messages using the control revision.

Drop cancels charge and releases the player, not the selected item. Its initial velocity is zero.

### Capsule clearance

Reuse only the capsule geometry/query portions of seating clearance. The item's `0.12`-radius sphere cast is too small for a player, and seating's ground-supported exit search would incorrectly ground airborne throws.

Check the full capsule at the destination and along its placement path. The initial carrier overlap is intentional: exclude it from the path sweep but require final separation from the carrier and clearance from world geometry. Use a small fixed set of front/side fallback candidates, spaced by capsule dimensions plus a clearance margin. Preserve airborne height; clear the floor when grounded.

If an ordinary release has no clear candidate, retain carry, cancel charge, and show blocked feedback. Forced release must end the relationship: try nearby placement, then existing spawn fallback. If neither is clear, use shared pending-placement behavior and periodic retry with physics suspended. Do not search for clearance every frame.

### Immediate release presentation

Capture the release pose/velocity before sending the request and detach graphics immediately on the carrier owner's client. A short presentation-only preview can advance from the captured pose using velocity and gravity while pending. Do not take ownership of the other motor or run another collision simulation.

The carried owner restores its motor when the commit arrives. Immediate feedback applies to the initiating client; other clients receive the transition after network delivery. Confirmation hands presentation back to motor smoothing without a second launch. Rejection restores the latest committed attachment; newer seating/reset supersedes the preview rather than reattaching it.

### Recovery

Install recovery in the same motor operation as launch velocity:

```
recoverySeconds = clamp(speed / gravityMagnitude, PlayerThrowRecoveryMin, PlayerThrowRecoveryMax)
```

Use the existing `0.2`-second restoration recovery for drops. Existing recovery suppresses both ground and air acceleration, not jumping; retain that behavior.

Do not call `SubmitWorldImpact` after installing launch velocity. It requires the carried owner's participation, adds traffic, and can apply velocity twice.

## 7. Impact auto-drop

Use separate world-impact and cart-contact hooks feeding the same guarded release path.

The carrier owner reports locally detected impacts. A server-originated queued world impact may initiate the server commit path. Observers do not submit release requests. Relationship/revision checks make later duplicate reports harmless.

Never issue requests or mutate carry relationships during prediction replay. `predictedAlready` inside `ApplyImpacts` is not a once-only gameplay-event guard across reconciliations. Require live execution plus pending-request/relationship deduplication.

For item/world impacts, notify at first live application of a valid nonzero impact before applying its velocity change. Capture pre-impact placement and detach local presentation. Updating carrier context must preserve its pending impact.

Cart launches bypass `ApplyImpacts`. Capture pre-contact pose alongside the existing pre-physics velocity, then notify on a new qualifying launch contact in the cart contact path outside replay. Use the existing launch qualification so low-speed pushing does not repeatedly drop players.

Horizontal collision response has already occurred by post-physics processing. Do not promise a hook before every cart impulse. Require prompt detachment at captured pre-contact placement without transferring carrier collision velocity or pending lift. Subsequent direct contact with the released player can still move them.

## 8. Atomic cart entry

Extend `PlayerSeating.Request` and the existing `GolfCartNetwork.PendingChange`/`CommitPending` transaction; broadcasts already support multiple `SeatTransition` entries.

1. Preserve flipped/stuck cart recovery: recovery does not seat either player or end carry.
2. Choose the second seat, preferring `0 <-> 2` and `1 <-> 3`, otherwise another available seat. Seats must be distinct.
3. Queue both participants, relationship, expected control/seat revisions, destinations, and existing cart revision/epoch guards.
4. Recheck participants, relationship, revisions, and both seats at commit. Reject the complete request if any condition changed.
5. Commit both occupants and end carry within the same broadcast/application transaction, moving directly to seated suspension.
6. Handle driver ownership/baseline changes for either participant, including a carried player automatically placed in seat zero.
7. Complete once. If there is no second seat, retain carry and show "No seat for carried player."

Do not send an ordinary drop before seating. Carry termination identity/revisions must be applied with the pair's seat transaction; no intermediate frame may expose one player free and the other seated.

An impact/release invalidates an older queued seat request. Seating committed first supersedes stale release. Clear pending charge/presentation accordingly. Pickup and seating must check each other's pending state too.

## 9. Input and inventory permissions

Call `PlayerInputReader.ClearContext()` on carry entry/exit. Clear queued jumps and block held buttons until released. Stop collecting jump edges while carried. `Consume()` returns default, but motor suspension is what stops simulation/traffic.

Process look, then suppress carried gameplay actions. Clear interaction target before an early return so stale tooltips disappear.

Gate direct action entry points and server requests, not just buttons:

- Block carried item pickup, use, drop, selection, and swaps, including HUD drag/drop and an already-open inventory.
- Update inventory input availability and direct inventory methods. `CanEquip` alone is insufficient because existing inventory permits some drops while equipping is disabled.
- Reject/discard stale pre-carry requests and predicted presentation using control revisions. Do not replay an old blocked action after release.
- Cancel both participants' active item use on pickup and refresh held presentation when permissions change, preserving contents and intended selection.
- Carrier Use/Drop act on the held player. Preserve other permitted carrier inventory behavior, with selection/menu changes cancelling charge.
- Clear old replicated item charging and reject late charge messages.

Do not treat carrying as cart-driver permissions: the carrier remains mobile and may interact with seats. Keep player-throw availability separate from item-equipping availability.

## 10. Immunity and cleanup

Set immunity on committed world release. Server checks its deadline; clients use local deadlines for targeting. Minor delivery differences can cause a rejected early request. Send no timer updates; include remaining immunity only in current snapshots. Unconfirmed preview does not permanently grant immunity, and carry-to-seat is not a world drop.

Explicitly break carry before the carrier's server fall-reset teleport. Generation changes alone do not clear relationships. Restore the carried player at clear pre-reset placement and let its motor handle the boundary, using forced-placement fallback when necessary. Reset/forced placement of either participant supersedes outstanding requests; a suspended motor cannot independently execute its normal fall reset.

`ItemKillVolume` is not a player reset path. Adding player kill volumes is outside this feature.

Use server disconnect/despawn handling before participant references disappear, following cart pre-destruction cleanup. Carrier departure forces survivor release; carried-player departure clears carrier state/charge. Deliver survivor state through an object that remains spawned, not an RPC from a disappearing carrier. Clear references, previews, pending requests, and unresolved snapshots on teardown. Do not issue new requests during session shutdown.

## 11. Settings and asset setup

Add to `GameSettings`:

| Setting | Initial value | Purpose |
| --- | --- | --- |
| `CarryOffset` | `(0, -0.1, 0.7)` | Attachment offset, not release clearance |
| `PlayerThrowChargeTime` | `1.0` | Seconds to full charge |
| `PlayerThrowMinSpeed` | `3.0` | Tap throw speed |
| `PlayerThrowMaxSpeed` | `15.0` | Full-charge speed |
| `PlayerThrowVelocityInheritance` | `0.5` | Inherited carrier velocity fraction |
| `PlayerThrowRecoveryMin` | `0.2` | Minimum recovery seconds |
| `PlayerThrowRecoveryMax` | `1.5` | Maximum recovery seconds |
| `CarryImmunityDuration` | `2.0` | Immunity after world release |

The user must add `PlayerCarry` to the player prefab root and assign its settings reference in Unity. Notify the user before implementation requires that change. Reuse capsule and graphics; no scene edit, carry trigger, new layer, collision-matrix change, or new settings asset is required. Let Unity generate `.meta` files.

## 12. Performance and traffic

- Derive attachment from carrier movement; no carry transform stream or added `NetworkTransform`.
- Suspend carried replicate/reconcile traffic. The session runs at 60 ticks per second; default movement while kinematic still wastes work and bandwidth.
- Send reliable transitions, request outcomes, and current snapshots. No charge-progress or immunity-timer stream.
- Omit implicit carrier identity, unused pickup placement fields, and full release quaternion.
- One pending release per relationship; no extra throw-impact or pre-seat drop message.
- Reuse non-alloc query buffers and cached references. Clearance runs on transitions or bounded placement retries, not every frame.
- Limit frame work to attachment/pending presentation. Retain finite/revision checks for consistency without anti-cheat reconstruction or another prediction framework.

## 13. Implementation sequence and scope

1. Establish shared control revision, suspension/placement, prediction/action guards, and current-state initialization.
2. Add `PlayerCarry`, request/commit/result flow, and existing-capsule targeting. Have the user configure its prefab component.
3. Add physical/visual following, smoother/hitbox suspension, and input/inventory cancellation.
4. Add charge, capsule clearance, local release presentation, committed velocity/recovery, deduplication, and rejection handling.
5. Connect live world-impact and cart-contact notifications with replay guards and captured placement.
6. Extend compound seating, recovery preservation, and driver handoff.
7. Complete snapshot resolution, immunity, forced placement, fall reset, disconnect, and despawn cleanup.

Expected scope:

- **New**: `Assets/Game/Runtime/Player/PlayerCarry.cs`.
- **Motor**: `PlayerMotor.cs`, `PlayerMotor.CartContacts.cs`.
- **Input/equipment/inventory**: `PlayerInputReader.cs`, `PlayerEquipment.cs`, `InventoryInputHandler.cs`, `PlayerInventory.cs`, `PlayerNetworkState.cs`.
- **Interaction/presentation**: `PlayerInteraction.cs`, `PlayerPresentation.cs`; no new camera mode.
- **Seating/cart**: `PlayerSeating.cs`, `GolfCartNetwork.cs`; `CartSeat.cs` only if eligibility/feedback requires it.
- **UI/settings**: `HudController.cs` where needed for feedback/inventory restrictions, and `GameSettings.cs`.
- **Prefab, applied by user**: Root carry component and its settings reference only.

Keep changes limited to carrying and its required shared guards. No migration tools or unrelated refactors. Do not run automated or Unity validation unless the user asks; the following checks are for the user after implementation.

## 14. Edge cases and failure modes

| Scenario | Behavior / first check |
| --- | --- |
| Competing pickups | Commit one relationship and reject stale requests. |
| Self-targeting | Exclude local capsule in ray and overlap paths. |
| Jitter or independent carried movement | Check tick/replay/reconcile suspension, smoother, and physical/visual pose separation. |
| Double launch | Check release deduplication and absence of extra world-impact submission. |
| Item drops work but cart drops do not | Check separate cart contact hook. |
| Replay generates drops | Exclude replay and guard relationship/pending request. |
| Overlapping release | Check full capsule, carrier/floor clearance, and fallback. |
| Same item overlaps both players | Carried hitbox is suspended; only post-release direct contact may hit the released player. |
| Blocked placement | Ordinary release retains carry with feedback; forced release uses fallback/pending placement. |
| UI bypasses restrictions | Gate direct inventory operations and pending requests. |
| Jump/Use fires after release | Clear context and stop collecting carried jump edges. |
| Release races seating/reset | Newer control revision wins; discard old preview without reattaching. |
| Seat fills before commit | Reject the whole pair transaction and preserve carry. |
| Late observer sees wrong state | Check consolidated snapshot and deferred reference resolution. |
| Participant disappears | Resolve survivor state before references are lost. |

## 15. User visual acceptance criteria

After implementation, use a host, remote client, and third spectator. Swap carrier/carried roles and repeat releases with noticeable latency.

| Scenario | Expected result |
| --- | --- |
| Target players, seats, and horns | Nearest eligible target wins; no self-targeting or broken existing interactions. |
| Grounded/airborne pickup | Consistent upright attachment; target stops independent movement. |
| Walk, sprint, jump, turn | Normal carrier motion, smooth attachment, usable cameras. |
| Carried look/movement/Use/Drop | Look works; gameplay actions do not; no queued action after release. |
| Inventory open before pickup | Drag/drop, swaps, selection, and use cannot bypass restrictions. |
| Tap/full-charge throw | Different throw strengths, correct charge bar, one launch. |
| Menu/selection change during charge | Cancel without throw or item release. |
| Release under latency | Immediate carrier-side detachment; no duplicate launch or persistent reattachment. |
| Walls, floors, low ceilings | Clear release or blocked feedback; no explosive overlap. |
| Airborne release | Preserve height without ground snapping. |
| Item strikes carrier | Detach before applied world impulse; no transferred impulse. |
| Cart strikes carrier | Qualifying hit drops once near pre-contact pose; low-speed pushing does not repeatedly drop. |
| Immediate re-pickup | Unavailable for approximately two seconds. |
| Two free cart seats | Both seat together; driver control correct whichever participant occupies seat zero. |
| One free seat or seat race | Carry retained with feedback. |
| Flipped/stuck cart interaction | Existing recovery behavior; no automatic seating. |
| Join during carry/release | Correct current state despite referenced-object spawn ordering. |
| Fall/disconnect | Relationship ends; survivor reaches valid free/seated/placement state with no lingering charge. |
| Multiple pairs | No cross-pair state or camera changes. |
