# Held Item and Hand IK Specification

## Purpose

Make equipped inventory items appear in an avatar's right hand and give throws a procedural wind-up and follow-through using the existing hand IK and item physics.

This document defines the complete behavior and code integration requirements for an implementation plan. The implementation should remain small: reuse the working IK, animation, inventory prediction, and projectile simulation; add the shared pose calculation, action presentation, and recovery coordination they need.

## Scope and design decisions

| Branch | Required behavior |
| --- | --- |
| Items | All existing inventory throwables: rocks, mushrooms, and basketballs. Use the same implementation with per-item settings. |
| Visible hands | Remote third-person avatars use the existing right-hand IK. First-person arms are deferred. |
| Local player | Retain the current camera-relative item display, apply the same replacement/input recovery, and calculate a world-space hand pose for release without instantiating a hidden avatar. |
| Animation | Layer item posing over any underlying animation. Running, walking, standing, jumping, and sitting do not require separate throw implementations. Existing gameplay permission determines whether an item can be thrown. |
| Holding | Attach the item to the solved right hand with an item-specific grip offset. Drive an independent hand target toward the default hold pose. |
| Charging | Move the target through a configurable torso-relative arc to above the shoulder/head, then hold there. Visual arc progress is independent of throw strength. |
| Throw | Release immediately when use is released, including early release during the arc. Launch from the calculated world-space hand/item pose. |
| Recovery | Follow the departing projectile, optionally pause at the end pose, then blend toward holding or the underlying animation. |
| Replacement and input | Hide the next item and ignore use presses until that sequence finishes. Require a fresh press afterward. |
| Timing | Actual follow time + end-pose pause + return blend. There is no additional cooldown, replacement-delay timer, or unused-follow-time wait. |
| Extensibility | Separate action/pose selection from animation state and rendering. Future local hands or other player-state poses can consume the same system. |

Two-handed weapons, steering-wheel grips, throwing carried players, finger posing, collision-aware arm animation, and a new IK package are outside this implementation. Do not build placeholder implementations for them.

## Existing code and integration baseline

### Project stack

- Unity 6000.5.7f1.
- Unity CLI 1.0.0-beta.9 at C:\Users\spenc\AppData\Local\Unity\bin\unity.exe.
- FishNet 4.7.3, Unity UI Toolkit, and the New Input System.
- Steamworks.NET 2025.164.1; Steam is the target platform.

### Relevant source files

| Source | Existing responsibility and integration point |
| --- | --- |
| [PlayerEquipment.cs](Assets/Game/Runtime/Player/PlayerEquipment.cs) | Routes begin/end/cancel use to the exact active item, exposes HeldTransform, creates the owner's camera-relative ViewmodelSlot, and reports charging. Integrate recovery gating and distinguish charge cancellation from clearing an entire recovery. |
| [ThrowableItemUse.cs](Assets/Game/Runtime/Items/ThrowableItemUse.cs) | Owns charge time and launch-speed calculation. Keep strength calculation here. Its current EndUse starts pickup cooldown after requesting release; make successful release explicit so a blocked throw does not start that cooldown. |
| [ItemDefinition.cs](Assets/Game/Runtime/Items/ItemDefinition.cs) | Existing per-item ScriptableObject with throw speeds, charge time, drop settings, and physics data. Add the item pose/recovery settings here. |
| [PlayerInventory.cs](Assets/Game/Runtime/Inventory/PlayerInventory.cs) | Selects the first world ID in the selected stack, computes release motion, predicts inventory operations, and rebuilds equipped presentation immediately. Contains ItemReleaseIntent.Drop/Throw and existing operation IDs. |
| [WorldItem.cs](Assets/Game/Runtime/Items/WorldItem.cs) | Owns held/world presentation, physics transitions, pickup cooldown, pooling, and attachment. Both PresentHeld and AttachHolder must respect replacement suppression. |
| [WorldItemMessages.cs](Assets/Game/Runtime/Items/WorldItemMessages.cs) | Defines ItemRecord and ItemMotion. ItemRecord does not currently identify throw versus drop. |
| [WorldItemRegistry.cs](Assets/Game/Runtime/Items/WorldItemRegistry.cs) and [WorldItemRegistry.Motion.cs](Assets/Game/Runtime/Items/WorldItemRegistry.Motion.cs) | Existing item lifecycle, release prediction, simulation ownership, and motion replication. Keep these physics paths. |
| [PlayerNetworkState.cs](Assets/Game/Runtime/Player/PlayerNetworkState.cs) | Replicates a buffered charging boolean with a control revision. Extend this action channel to represent item recovery and distinguish it from cancellation. |
| [PlayerInputReader.cs](Assets/Game/Runtime/Player/PlayerInputReader.cs) | Existing press/release edges and cancellation on input-context interruptions. Preserve the fresh-press behavior. |
| [PlayerAvatarPresentation.cs](Assets/Game/Runtime/Player/PlayerAvatarPresentation.cs) | Configures rendered avatars for nonowners and supplies avatar identity, movement, seating, and look context. |
| [AvatarPresentation.cs](Assets/Game/Runtime/Avatars/AvatarPresentation.cs) | Exposes SetHandTarget, ClearHandTarget, resolved avatar settings, cached bone bindings, and IdentityResolved/WillUnbind/DidBind events. |
| [AvatarHumanoidIK.cs](Assets/Game/Runtime/Avatars/AvatarHumanoidIK.cs) | Existing hand position/orientation IK with per-avatar follow-bone offsets and reach handling. Reuse it. |
| [AvatarSettings.cs](Assets/Game/Runtime/Avatars/AvatarSettings.cs) | Contains avatar scale, standing/seated offsets, hand-follow bones, and generated shoulder/hips/limb measurements. |
| [AvatarInstance.cs](Assets/Game/Runtime/Avatars/AvatarInstance.cs) and [AvatarPresentationSystem.cs](Assets/Game/Runtime/Avatars/AvatarPresentationSystem.cs) | Manually evaluate the animation graph and hand IK, followed by VRM processing, from LateUpdate. Target updates must be ordered correctly around this evaluation. |
| [PlayerPresentation.cs](Assets/Game/Runtime/Player/PlayerPresentation.cs) | Provides the owner camera and gameplay aim pose. Preserve the existing final aim direction and inherited movement behavior. |
| [PlayerControlTransition.cs](Assets/Game/Runtime/Player/PlayerControlTransition.cs), [PlayerSeating.cs](Assets/Game/Runtime/Player/PlayerSeating.cs), and [PlayerCarry.cs](Assets/Game/Runtime/Player/PlayerCarry.cs) | Control-context changes, equip permission, and separate carried-player use behavior. Integrate cleanup without applying this item throw cycle to carried players. |

### Existing behavior that matters

WorldItem is a MonoBehaviour replicated through the registry's item lifecycle and motion messages. It is not an individual FishNet NetworkObject.

Held items currently attach to PlayerEquipment.HeldTransform. The owner's ViewmodelSlot is camera-relative, offset approximately (0.35, -0.3, 0.6), at half scale. Remote items use the player's EquipSlot instead of an avatar bone. PresentHeld and AttachHolder currently restore visibility based on equipment state.

A release removes the first item ID from the selected stack and immediately presents the next one. Preserve that inventory transaction. Delay the visible replacement and use eligibility, not removal of the thrown item from inventory.

Current projectile placement is the aim origin plus 0.65 metres forward, shortened by a fixed-radius environment sphere cast. It is independent of the displayed held position. Replace that placement for throws; retain ordinary drop behavior.

Throw strength already uses elapsed charge time, interpolating between MinThrowSpeed and MaxThrowSpeed. A quick release throws weakly; full charge waits for release. Keep that behavior and the charge HUD. The rock's current speed range is 3–14 m/s with a one-second full-charge time.

The current charge boolean becomes false on both cancellation and throw. It cannot identify successful release, which projectile to follow, or replacement suppression. Do not infer a throw from that boolean becoming false or from projectile speed.

The local owner has resolved avatar settings but no live animated hand bones. Disabling avatar visuals currently releases the avatar instance. A hidden owner skeleton would require additional animation/runtime work and is not part of this design.

## Pose model and attachment

### Shared pose calculation

Use one shared calculation for the desired world-space hold/charge pose. It must be usable without a rendered skeleton, so the owner can calculate launch placement and recovery while remote clients use the same pose definition for hand IK.

Express pose positions in a torso-facing coordinate frame, relative to the right shoulder and scaled by arm reach. Arm reach comes from the generated upper/lower arm lengths multiplied by AvatarSettings.Scale. Account for the existing standing, seated, carried, and yaw-offset placement conventions where those contexts permit item use.

Generated shoulders and hips are available without live bones. Their positions are relative to the avatar's sole plane; do not apply sole-plane subtraction twice. Seated placement must account for the shoulder-to-hips relationship and SeatedPelvisOffset. The rendered avatar additionally samples seated animation hips, so the calculated pose is an approximation, not an exact animated bone reconstruction.

Hold/charge poses do not tilt with camera pitch. The charging arc remains around the torso/shoulder when the player looks sharply up or down. Projectile velocity still follows the existing aim direction at release. Do not add crosshair convergence or ballistic aim compensation.

Small differences between the calculated release pose and a remotely solved hand are acceptable. Exact bone agreement across clients is not required; their presentation and motion timing already differ. Keep the shared calculation consistent rather than duplicating separate gameplay and visual trajectories.

### Item in hand

Define per-item position and rotation offsets that place the item's origin relative to the avatar's configured right-hand follow bone. AvatarHumanoidIK already converts that follow-bone target into a wrist target; respect this convention instead of assuming every avatar targets its wrist origin.

While held, the remote item follows the solved hand/grip. The hand target must be independent of the solved hand and held item. Do not create a dependency loop where the item drives the hand target while also being attached to that hand.

On ordinary equip, attach the item at the hand and move toward the hold pose. Reuse the return-blend duration for ordinary equip/cancel smoothing where useful; do not introduce another gameplay lockout for equipping.

Use world item scale for remote holding and released physics. Retain the existing local camera-relative item presentation and its scale until first-person arms are implemented. Consequently, the local displayed item hands off from its camera-relative position to the world release pose when thrown. This temporary visual discontinuity is accepted.

### Charge trajectory

Use a small authorable arc representation: a default hold position, an arc control point, a charged endpoint above the shoulder/head, and a visual traversal duration. A quadratic curve with smooth interpolation is sufficient; do not build a trajectory editor.

Provide hold and charged wrist orientations and interpolate them during the arc. Keep the trajectory within reachable space for the avatar. The visible item follows the solved hand throughout.

Start the trajectory when item charging begins. Traverse using its own elapsed visual time, independent of ThrowChargeTime or Charge01. At the endpoint, hold until release or cancellation. An early release throws from the currently calculated pose immediately; it must not wait for the arc to finish.

## Throw transaction and clearance

### Successful throw

1. Resolve the exact equipped item retained by the current use action.
2. Calculate its desired world release pose from the current procedural hand pose and item grip offset.
3. Apply the isolated environment-clearance policy.
4. If allowed, submit the existing predicted inventory release with that pose.
5. Begin recovery and replacement suppression before the inventory view can reveal the next stack item.
6. Detach the released item from held presentation and let the existing item simulation take over.

Keep the existing launch-speed calculation, final aim direction, velocity inheritance, initial spin calculation, simulation ownership, and own-player collision grace. Compose the item's initial release orientation from its posed grip so the change to world physics does not unnecessarily reset its visible orientation. This does not change the aim-based velocity/spin rules.

Keep the thrown item's existing pickup cooldown separate from this feature. Start it only when a release is actually initiated successfully. If inventory reconciliation later rejects that operation, cancel the matching predicted recovery and restore the current equipment presentation through the existing inventory/lifecycle reconciliation.

### Removable clearance policy

Place the new environment-placement correction and rejection decision behind one small shared boundary. It receives the desired release pose and required item/player/environment data and returns either an allowed release pose or a blocked result.

The policy must not mutate inventory, start recovery, send network messages, or own charge/input state. The throw coordinator interprets its result. Removing its call or making it always allow the desired pose must remove these new obstacle restrictions without rewriting the throw transaction.

Existing equip permission, use routing, and recovery gating remain separate concerns.

Clearance must account for the lateral/overhead hand release position, initial overlap, and the item's world-sized collision geometry. The current fixed 0.12-metre forward cast is insufficient for all items: the basketball is larger, and some items have offset colliders. WorldItem.DropDiameter provides an existing conservative size reference. Do not base release clearance on the owner's half-scale viewmodel.

Correct the release position to a nearby clear position when possible. A small correction near an obstacle is acceptable. If no clear release placement is available, keep the item in inventory, cancel the charge, and return toward its holding pose. Do not launch, consume an item, start throw recovery, or start pickup cooldown for that blocked attempt. A later throw requires a fresh press.

Held arms/items may still intersect environment geometry before release. Collision-aware arm posing is not required. Retain existing ordinary drop placement and collision behavior.

## Recovery sequence

The sequence itself controls replacement visibility and the ability to begin another item use. There is no separate throw cooldown or replacement deadline.

    Successful release
        -> Follow departing projectile
        -> Optional end-pose pause
        -> Return blend
        -> Show currently selected item, or remain unequipped
        -> Accept a fresh use press

### 1. Follow

Follow the projectile's translated position using the released item's cached grip relationship. Preserve the wrist orientation relative to the player's body at release. Do not follow projectile spin or rotate the tracked grip offset around the item using its changing physics rotation.

End following at the first of:

- The configured arm reach limit is reached.
- MaximumFollowDuration expires.
- The matching projectile is picked up, removed, or no longer represents that release.

Measure reach from the shoulder to the intended wrist position, accounting for the hand-follow offset. It is not distance travelled by the rock or distance from its release point.

Clamp the end target to the reachable boundary when a fast projectile crosses it between frames. Retain the last valid target when the projectile disappears. Track world item identity and release operation as well as any cached Transform: pooled objects can be reused for unrelated items.

Ordinary bounces may continue being followed within these bounds. A rock that settles within reach reaches the maximum follow duration and proceeds to the next stage. Do not add first-impact networking or wait indefinitely for a distance threshold.

Follow duration is the actual elapsed time before the first ending condition. If reach is reached early, begin the pause immediately. Do not wait out the unused maximum duration, slow down the projectile, or stretch the animation to create a fixed firing interval. At high throw speeds, following can be only a few frames long.

### 2. End-pose pause

Capture the reachable end pose relative to the player's body and hold it for EndPosePauseDuration. The hand moves with a running or turning player instead of remaining at a fixed world point. Stop tracking the projectile permanently for this action; do not reconnect if it returns within reach.

A zero duration skips this stage.

### 3. Return blend

Over ReturnBlendDuration:

- If an item is currently selected and can be equipped, move the empty hand toward that item's default hold pose. Keep the replacement hidden until the blend completes.
- Otherwise, blend out the hand override into the currently playing underlying animation.

Use the current selection, not a remembered promise to equip the next item from the original stack. Selection or inventory changes during return update its destination without restarting the recovery sequence. Use the thrown item's cached recovery timing for the ongoing action; selecting a different item does not retime the existing throw.

When returning toward holding, keep the appropriate IK target active. Do not clear IK to locomotion and then immediately recreate the holding override. When returning to the underlying animation, finish by clearing the hand target.

A zero return duration completes immediately. Zero-duration stages must advance without introducing artificial frame delays.

### Completion and input

Only after the return completes may the next selected item appear and a new item use begin. No item is spawned merely to animate a replacement: reveal the next existing inventory item through the normal held presentation path.

Ignore use presses during recovery. Do not buffer them or start charging when a held button outlasts recovery. A fresh press is required. A release edge without an active use target must not clear an ongoing recovery.

For example, an actual 0.04-second follow, 0.10-second pause, and 0.20-second return produce 0.34 seconds of recovery. MaximumFollowDuration does not replace the actual follow time in this calculation.

## Per-item configuration

Store settings on the existing ItemDefinition assets, using the same defaults initially and allowing individual item tuning. Do not create per-avatar/per-item asset combinations or a separate pose-asset library for this feature.

| Setting | Meaning | Initial guidance |
| --- | --- | --- |
| Right-hand grip position/rotation | Item-origin offset relative to the configured hand-follow bone | Author for each existing item; wrist positioning only, no finger curls. |
| Hold position/orientation | Default target in the torso/shoulder frame | Position scales with arm reach. |
| Charge arc control point | Shapes the path from holding to the charged endpoint | Use a simple arc above/toward the shoulder. |
| Charged position/orientation | Endpoint held while charging continues | Keep within the avatar's reachable space. |
| Charge pose duration | Time to traverse the visual arc | Independent of gameplay charge duration; choose an initial value during implementation planning. |
| Follow reach fraction | Shoulder-to-wrist limit as a fraction of arm reach | Coordinate with the solver's reach fade; a value around 0.85 starts inside its existing full-weight range. |
| Maximum follow duration | Upper bound on following a released item | 0.20 seconds. |
| End-pose pause duration | Time held at the final body-relative pose | 0 seconds; increase when a visible pause is desired. |
| Return blend duration | Time to reach holding or fade to the underlying animation | 0.20 seconds. |

The timing values are starting defaults for visual tuning. Do not add MinimumThrowInterval, ReplacementDelay, or equivalent settings.

### Existing IK reach behavior

AvatarHumanoidIK currently fades hand position and rotation weights between 88% and 98% of arm reach, reaching zero at 98%, while clamping wrist position to 98%. A target paused at that latter limit therefore does not visibly hold the arm extended.

The implementation plan must resolve this explicitly. Prefer a follow limit that preserves effective IK weight; if broader reach tuning needs a solver change, keep it narrowly scoped to these hand targets and retain the existing safety clamp. The hand must stay posed through EndPosePauseDuration and return through ReturnBlendDuration, rather than disappearing into locomotion because the reach fade already removed its influence.

## Selection, cancellation, and context changes

| Event | Required outcome |
| --- | --- |
| Select another slot during recovery | Update selection immediately. Keep the replacement hidden and continue recovery; use the new selection for the eventual return/replacement. |
| Unequip or empty the selected stack | Continue recovery, then return to the underlying animation unless another item becomes selected. |
| Ordinary drop or inventory operation during recovery | Remain available under existing permissions. Modify inventory normally without restarting or cancelling the existing recovery. The logically equipped hidden replacement can be dropped. |
| Cancel charging by menu, focus/input change, selection, or existing gameplay interruption | Do not throw or start recovery. Smoothly restore the appropriate held/unequipped pose and preserve existing fresh-press-after-cancellation behavior. |
| Ordinary drop outside recovery | Preserve current drop behavior; do not add throw follow-through or recovery. |
| Equip-disabling context such as becoming a driver or entering an incompatible carry role | Clear projectile tracking, pose ownership, recovery state, and its timing entirely. Do not retain a cooldown deadline. |
| Return to an equippable context | Present the current selection normally. Never resume the old projectile-follow action. Context changes can shorten recovery; that tradeoff is accepted. |
| Running, walking, jumping, or another ordinary animation change | Continue item posing/recovery over that animation. Do not reset the action because the animation state changed. |
| Avatar identity/skeleton replacement | Rebind and resume the current logical item action using the new avatar measurements. An appearance swap is not an equip-permission reset. |
| Ownership loss, despawn, or disconnect | Clear cached targets, subscriptions, and action state so no later owner or pooled item inherits recovery. |

Keep charge cancellation distinct from full recovery reset. PlayerEquipment.CancelUse is called by slot selection and drops today; making it indiscriminately clear recovery would violate the required behavior.

Do not automatically search other hotbar slots for a replacement. Use whatever remains selected through normal inventory behavior.

## Inventory and item lifetime coordination

Keep immediate predicted inventory removal and the normal logical selection of the next stack item. Suppression is a shared presentation/input rule, not a deferred inventory transaction.

Both WorldItem.PresentHeld and WorldItem.AttachHolder must consult the same suppression state. Reconciliation, lifecycle updates, holder refreshes, and avatar rebinding must not reveal the next item halfway through recovery.

In PlayerInventory.Submit, the operation ID is assigned before PredictRelease and RebuildView. Start owner recovery for a successful throw at that boundary, before RebuildView can show its replacement. Do not start recovery for a drop or a blocked attempt.

On the server, accepted release processing removes IDs and publishes release lifecycle records before ProcessRequest calls UpdateEquipment for the replacement. Publish the corresponding recovery action before those lifecycle/equip notifications. Do not execute throwable use callbacks again in that path.

Follow only the released world object, after its held attachment has actually been removed and the matching release pose applied. Never accidentally follow the hidden replacement or a still-held object because an action message arrived first.

## Networking

Prioritize immediate owner response and consistent action ordering. Trust the existing owner-reported throw motion. Preserve the registry's physics simulation and motion replication rather than adding a new authority scheme.

### Action state

Extend the existing buffered player-use state from a charging boolean to a compact item action snapshot sufficient to represent Idle, Charging, and Recovering. Keep IsChargingUse available as a derived property for existing consumers.

The snapshot needs enough information to identify the action and reconstruct presentation:

- Action state.
- Relevant world item identity and enough item-definition information to resolve its settings if the projectile is unavailable.
- Phase start tick/time in an existing shared time domain.
- Existing release operation identity for recovery.
- Existing control revision for context ordering.

Use a local change event so presentation does not poll the replicated action value each frame. Reuse existing operation identities rather than inventing a parallel throw counter. Keep messages to action transitions; do not send charge percentage, bone transforms, hand targets, pose weights, or continuous arm state.

The buffered recovery action identifies an actual throw, while the existing item lifecycle identifies the corresponding physical release. A new throw/drop field on every ItemRecord is not required by this design.

### Transitions

- Begin Charging immediately on the owner when the item begins charging.
- Explicit cancellation or a blocked throw returns Charging to Idle without recovery.
- A successful throw enters Recovering for its item/operation. Do not publish an ambiguous cancellation as the only release signal.
- The owner publishes completion when its follow/pause/return sequence ends. Intermediate visual stages stay local; no separate network cooldown timer is introduced.
- Full context reset clears the buffered action and local recovery. Update integrations that currently copy only a charging boolean so they cannot restore stale or unidentified item actions.

Preserve one execution on host owners, prevent older echoes from undoing newer owner prediction, and associate rejection/confirmation with the matching inventory operation. Quick press/release pairs must still produce a release and recovery even if no rendered frame displayed the charging pose.

### Remote clients and late observers

Remote clients derive their poses locally. Approximate pose timing is acceptable; equipment visibility and action ordering must respect the owner's active recovery and subsequent action transitions.

A recovery snapshot may arrive before the matching item release. Suppress replacement immediately, then follow only when the matching world release is available. If the projectile is already gone or out of reach, advance to an appropriate remaining stage instead of replaying a throw from the holding pose.

A charging start timestamp lets a late observer show the current point of the independent charge arc instead of starting from zero. A recovery completion update prevents the buffered state from indefinitely replaying an old throw.

Item baseline records are not ordered by held/released role. Establish the initial action snapshot before exposing remote equipment; if necessary, keep initial held presentation hidden until that snapshot is known. Refresh held visibility when the action snapshot changes.

Do not let a lagging reconstructed recovery block a newer confirmed holding/charging state. Elapsed stages may be shortened or skipped to catch up. Exact historical reconstruction of an end-pose pause is unnecessary, and no new hand-transform stream is justified.

## Animation ordering and runtime rebinding

Update procedural target transforms before the existing manual graph/IK evaluation. AvatarPresentationSystem evaluates from LateUpdate at execution order 200; PlayerAvatarPresentation currently runs at 100. Specify the new update placement explicitly in the implementation plan.

Held visuals must reflect the solved hand after IK. Parenting to the bound follow bone can supply that movement without a second transform-copy loop. If copying is required, it must happen after the solve. Release calculations use the shared procedural pose and must not require evaluating the entire owner avatar graph.

Cache equipment, registry, settings, camera, item definition, target, and skeleton references at startup or their corresponding bind/change events. Refresh them on avatar identity/binding and equipment changes, not through per-frame searches.

Use AvatarPresentation.WillUnbind to detach a held item from an old skeleton before it is destroyed. Reattach through DidBind and refresh generated measurements through IdentityResolved. Preserve the held world item and its inventory identity; do not recreate it because an avatar changed.

During a temporary lack of an avatar binding, keep action/recovery state independent of the renderer and use the calculated pose for any necessary item presentation. When the binding returns, apply the current pose and visibility gate rather than restarting the action.

Per-frame work is appropriate for active arcs, following, blends, and moving targets. Equipment changes, phase changes, binding, visibility, and target ownership should be event-driven. Avoid repeated no-op setter calls and reference resolution.

## Implementation boundaries and planning deliverable

Use the smallest division that keeps responsibilities clear:

| Boundary | Responsibility |
| --- | --- |
| Shared item pose data/calculation | Avatar-relative target poses and grip composition, usable by the owner without a skeleton and by rendered avatars. |
| Shared held-item action/presentation controller | Follow/pause/return progression, hand target ownership, attachment/rebinding, and replacement suppression. One recovery sequence is the source of readiness. |
| Existing equipment/use code | Input edges, exact active item, charge strength, cancellation, and consulting recovery readiness. |
| Existing inventory/registry | Item identity, predicted release, inventory changes, physics, and lifecycle replication. |
| Existing player network state | Compact buffered action transitions and local notification. |
| Isolated release-clearance policy | Placement correction/rejection with no gameplay side effects. |

Shared logic crossing owner gameplay and avatar presentation boundaries belongs in a shared class. Do not duplicate pose or recovery formulas, add a second gameplay throw state machine, build a general pose-priority framework, or replace the working IK/animation pipeline.

The implementation plan should name the concrete file changes, target-update ordering, action snapshot fields and ordering, release success/cancellation contract, visibility gate, and avatar rebind path. It must explicitly resolve the existing hand reach fade so the end-pose pause works.

### Unity authoring

Expect player-prefab wiring for the shared pose/presentation controller and pose settings on the existing item-definition assets. Use runtime target transforms where possible. A new pose asset library, new item prefabs, and hand-authored throw clips are unnecessary.

Identify any required component, prefab, or asset setup before making those changes. Avoid game-scene edits. Use the Unity CLI when possible and Unity MCP when necessary for Unity authoring. Let Unity generate .meta files; do not write them manually. Do not create one-off migration tools.

Keep changes surgical and comments short. Use implicit Unity object lifetime checks. Do not expand into unrelated cleanup, carried-player throwing, or future pose consumers.

Do not run automated tests, builds, Play Mode checks, or other validation unless the user explicitly requests them. The checks below are visual acceptance checks for the user after implementation.

## Acceptance criteria and user visual validation

1. Equip each existing throwable. On another client, the item occupies the right-hand grip instead of the old floating equip position. Compare supported avatar proportions and item sizes.
2. Hold and charge while standing, running, walking, jumping, and sitting wherever gameplay permits throwing. Posing layers over the current animation without selecting a separate throw animation state.
3. Look sharply up/down while charging. The arc stays torso-relative while the release follows the final aim direction.
4. Compare quick taps, partial charges, full charges, and holding beyond full charge. Release always throws immediately at the current pose; strength retains its existing behavior and the visual arc is not driven by charge percentage.
5. Observe hand-to-projectile continuity. Small calculated-pose or obstacle-clearance corrections are acceptable. The existing local camera-relative item handoff remains until first-person arms are implemented.
6. Watch a fast throw cross the reach limit between frames. The hand stops at a reachable end pose, does not overextend, and does not twist with projectile spin.
7. Increase the end-pose pause. The extended arm visibly holds its pose while the player moves/turns, then returns through the separately configurable blend. Existing IK reach fading must not silently remove that pause.
8. Vary maximum follow duration, pause, and return duration independently. An early reach stop begins the pause immediately. Replacement occurs at sequence completion with no hidden extra timer.
9. Throw from a stack. The empty hand returns toward holding before the next item appears. Hold use or press during recovery: no queued charge starts; a fresh press afterward works.
10. Throw the last item or unequip during recovery. The arm returns into the underlying animation without a replacement appearing.
11. Change slots, reorder inventory, or drop the hidden replacement during recovery. Inventory changes normally, the ongoing recovery is not restarted/cancelled, and its final state uses the current selection.
12. Cancel charging and perform ordinary drops. Neither starts throw recovery. Opening a menu or losing input focus does not produce a delayed throw.
13. Throw beside a wall or under low overhead geometry using differently sized items. Clear placement correction works; a fully blocked attempt keeps the item and creates no projectile or recovery. Confirm that the clearance rejection can be removed without changing throw execution.
14. Stop a weak projectile on nearby geometry, have another player pick it up, or remove it during following. Recovery finishes rather than tracking forever or following a pooled replacement object.
15. Enter an equip-disabling context during recovery, then leave it. The old recovery is discarded completely and does not resume. Ordinary locomotion animation changes do not discard it.
16. Swap avatars while holding, charging, and recovering. The item survives the old skeleton's destruction, rebinds, and resumes the current action without restarting inventory or throwing again.
17. In a host/client session, throw from each owner, including rapid taps. Each throw launches once, replacement suppression is visible on observers, and prediction/confirmation does not restart the action.
18. Join or begin observing while another player is charging or recovering. Show the current action and correct replacement visibility rather than replaying stale actions or revealing the next item early.

