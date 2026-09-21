# Held Item and Hand IK Implementation Plan

## Objective and constraints

Implement [Held_Item_Hand_IK_Spec.md](Held_Item_Hand_IK_Spec.md): remote avatars hold inventory items in the solved right hand, charging moves an independent procedural target, and a successful throw releases immediately from the calculated grip pose. One follow/pause/return sequence controls replacement visibility and item-use readiness.

Keep the owner's current camera-relative, half-scale viewmodel. Calculate its world release pose without an avatar instance. Preserve charge strength, HUD, aim direction, velocity inheritance, spin, inventory prediction, simulation ownership, and ordinary drops. Do not add first-person arms, throw animation clips, another IK package, a cooldown, or a general pose framework.

Follow AGENTS.md. Inform the user before authoring the player component/prefab and existing assets described below. Use Unity CLI at `C:\Users\spenc\AppData\Local\Unity\bin\unity.exe` where possible, then Unity MCP for unsupported authoring. Let Unity generate metadata. Do not edit game scenes or create migration tools. Do not run tests, builds, Play Mode, or other validation without a separate user request.

## Required corrections and agreed decisions

These decisions resolve gaps in the specification and are part of the implementation contract:

1. **Bake right-hand calibration.** `AvatarSettings.GeneratedSkeleton` currently has shoulder positions and arm lengths, but no wrist-to-follow transform. `AvatarHumanoidIK` measures its translation from live bones, which the owner lacks. Extend the existing avatar processor/settings with source-scale wrist-to-follow position and rotation. The user explicitly selected this approach. Both current avatar settings use a non-wrist right-hand follow bone, so a zero-offset assumption is unsuitable.
2. **Keep wrist-oriented poses.** The existing IK API takes a follow-point position but a wrist rotation. It does not interpret target rotation as follow-bone rotation. The user explicitly selected preserving wrist-oriented pose settings and converting to the follow-bone frame when composing the item. Do not silently change the existing hand-target rotation contract.
3. **Order presentation explicitly.** `WorldItemRegistry` and `AvatarPresentationSystem` both currently run at order 200. In addition, `AvatarPresentation.UpdateInput` updates the rendered torso inside the avatar system, after `PlayerAvatarPresentation.LateUpdate`. Merely adding a controller at order 150 would leave both dependencies ambiguous. Use the ordered hooks below.
4. **Resolve remote equipment from lifecycle data.** Remote clients do not receive the owner's inventory slot array. The controller must use equipped held records for remote selection, and the predicted inventory view for owner selection.
5. **Keep use and equip permission distinct.** A carrying player can still satisfy `PlayerInventory.CanEquip`, but `PlayerEquipment` routes use to `PlayerCarry`, and item charge permission requires the free carry role. Reset this item action when that role becomes incompatible; preserve existing inventory/equipment permissions and carried-player use behavior.

No remaining product decision is required for this plan. The numeric pose/grip defaults below are initial authoring values for user visual tuning, not a claim of exact hand contact on every avatar.

## File and responsibility map

Paths below are relative to the repository. Runtime filenames without a path prefix are under `Assets/Game/Runtime/`.

| File | Change |
| --- | --- |
| `Items/ItemDefinition.cs` | Add one serializable item-hand settings value, with the defaults below. |
| New `Items/HeldItemPose.cs` | Shared settings/data and pure shoulder, follow-point, wrist, grip, and reach calculations. No inventory or networking. |
| New `Player/PlayerHeldItemPresentation.cs` | One MonoBehaviour coordinator for action presentation, local recovery stages, readiness, visibility suppression, attachment, and rebinding. Cache existing player components; do not add another NetworkBehaviour. |
| New `Items/ItemReleaseClearance.cs` | Isolated side-effect-free clearance query and bounded correction policy. |
| `Avatars/AvatarSettings.cs`, `Assets/Game/Editor/AvatarProcessor.cs` | Bake and consume right wrist-to-follow calibration. Extend existing content-format checks. |
| `Avatars/AvatarHumanoidIK.cs`, `Avatars/AvatarPresentation.cs` | Share baked right-hand translation with gameplay, add the narrowly scoped item reach constraint, and expose a pre-evaluation target hook. |
| `Avatars/AvatarPresentationSystem.cs` | Invoke that hook after input/body placement and before graph evaluation. |
| `Player/PlayerAvatarPresentation.cs` | Expose a side-effect-free current placement input for owner/fallback calculations; retain existing look replication. |
| `Player/PlayerEquipment.cs` | Exact use-target routing, explicit throw success, distinct charge cancel/full reset, and shared attachment/visibility access. |
| `Items/ThrowableItemUse.cs` | Keep strength calculation; start pickup cooldown only after a successful release initiation. |
| `Inventory/PlayerInventory.cs` | Prepare throw pose before submission; start recovery before replacement presentation; publish accepted server recovery before lifecycle changes; reconcile matching recovery. |
| `Player/PlayerNetworkState.cs` | Compact buffered item-action snapshot, transition ordering, initial-snapshot readiness, local change event. |
| `Player/PlayerControlTransition.cs`, `Player/PlayerSeating.cs` | Replace the charging-only context baseline with the full action snapshot; coordinate context reset and future-revision actions. |
| `Player/PlayerCarry.cs`, `Player/PlayerInputReader.cs` | Narrow reset/cancellation integration where required; retain separate carry behavior and press/release input. |
| `Items/WorldItem.cs` | One held-attachment/visibility path for both existing entry points; cache world-size clearance data; expose release availability/presented origin. |
| `Items/WorldItemRegistry.cs` | Deterministic presentation order and lifecycle notification after item presentation is applied. |

Keep `WorldItemMessages.cs` and `WorldItemRegistry.Motion.cs` unchanged unless a directly required API extraction is necessary. `ItemRecord` already has world identity, releaser, operation, and launch tick; no throw/drop field or new motion stream is needed. `AvatarInstance.cs` retains its graph/IK/VRM pipeline. `PlayerPresentation.cs` remains the source of the existing final aim pose.

## 1. Authoring data and avatar calibration

### Item settings

Add a `[Serializable]` `HeldItemPoseSettings` value on each `ItemDefinition`, not a separate asset. Expose concise inspector labels/tooltips identifying units and frames. Use these common starting values:

| Field | Default and convention |
| --- | --- |
| `GripPosition` | Item-origin offset from the configured right follow bone, in world metres along its axes. Not multiplied by avatar scale or item prefab scale. |
| `GripEuler` | Item-origin rotation relative to that follow bone; includes the item's required model/root alignment. |
| `HoldPosition` | `(0.15, -0.40, 0.50)`; shoulder-relative follow-point position in arm-length units. Axes: right, up, torso forward. |
| `HoldWristEuler` | `(0, 0, 0)` relative to torso orientation. |
| `ChargeControlPosition` | `(0.35, 0.25, 0.45)` in the same units/frame. |
| `ChargedPosition` | `(0.20, 0.65, -0.25)` in the same units/frame. |
| `ChargedWristEuler` | `(-70, 0, 15)` relative to torso orientation. |
| `ChargePoseDuration` | `0.35` seconds, independent of `ThrowChargeTime`. |
| `FollowReachFraction` | `0.85`, constrained to greater than zero and at most `0.85`. |
| `MaximumFollowDuration` | `0.20` seconds. |
| `EndPosePauseDuration` | `0` seconds. |
| `ReturnBlendDuration` | `0.20` seconds, also usable for unlocked equip/cancel smoothing. |

Allow zero visual-stage durations and handle them immediately. Keep existing throw and physics values unchanged. Store grip rotations as authorable Euler angles and cache quaternions on definition changes.

Initial grip authoring on existing assets: start rock and basketball at zero position with their existing prefab root rotation as `GripEuler`. For the mushroom, start with `(0, -0.03, 0)` metres and its existing root rotation, placing the stem through the follow point. These are explicit starting values; user visual acceptance determines final contact tuning. Do not multiply by prefab root rotation a second time during release.

### Calibration data

Extend `GeneratedSkeleton` with `RightHandFollowBone`, `RightWristToFollowPosition`, and `RightWristToFollowRotation`. The bone field records which authored mapping was baked. For wrist-follow itself, position is zero and rotation is identity.

In `AvatarProcessor.Measure`, accept the settings needed to resolve the configured follow bone. The existing processor normalizes the source root to unit scale before measurement. From source transforms `wrist` and `follow`, calculate:

```text
offset = inverse(wrist.rotation) * (follow.position - wrist.position)
relativeRotation = inverse(wrist.rotation) * follow.rotation
```

Reject a missing configured bone through the existing processing diagnostics. Increment `CurrentFormatVersion` and require reprocessing for older settings or a changed follow-bone mapping. Extend the existing content checks for the new finite position/valid rotation and mapping. Add a concise inspector note that changing the hand-follow mapping requires Save and Process Avatar; do not introduce another processing tool.

Reprocess the two existing sources through `AvatarProcessor.Process`/the existing Save and Process Avatar workflow, preserving IDs, authored offsets, spring chains, and animation assets:

- `Assets/Game/Settings/Avatars/44816e438f2e4775.asset`
- `Assets/Game/Settings/Avatars/461d9592f370666a.asset`

The existing generated avatar prefabs may be rewritten by that processor. Do not create extra avatar assets or a hidden owner skeleton. Bake only the right-hand data required by this feature. In `AvatarHumanoidIK`, replace the right-hand live translation measurement with the baked translation times `Settings.Scale`; leave the unrelated left-hand behavior alone. Both gameplay and rendered right-hand targets then use the same calibration, independent of the animation sampled during initialization. Animated finger differences remain within the spec's allowed approximation.

## 2. Shared pose calculation

Represent a calculation result with explicit follow-point position, wrist rotation, wrist position, and composed item pose. Do not overload one `Pose` to sometimes mean follow orientation and sometimes wrist orientation.

Let `B` be the torso frame, `Q = B * Euler(0, settings.YawOffset, 0)`, `s = settings.Scale`, and `L = (Generated.RightArm.x + Generated.RightArm.y) * s`.

For a rendered avatar, use the `AvatarPresentation` torso rotation after `UpdateInput`. For the owner/unbound fallback, use the current graphics-facing placement, with yaw-only rotation when free and attachment-facing rotation when seated. This uses no camera pitch and does not require running the owner's animation state. Differences from remote body-yaw smoothing are accepted by the spec. Capture input values through `PlayerAvatarPresentation` without invoking the look-smoothing `CaptureInput` a second time.

Calculate the shoulder as:

```text
standingShoulder = SolePosition
                 + B * (StandingOffset + (Carried ? CarriedOffset : zero))
                 + Q * (Generated.RightShoulder * s)

seatedShoulder = Facing.position + Facing.rotation * SeatedPelvisOffset
               + Q * ((Generated.RightShoulder - Generated.Hips) * s)
```

Use the avatar's existing seated blend weight for rendered presentation, and the seated context for the skeleton-free owner approximation. Do not subtract `Generated.SolePlane` again: shoulder and hips already contain that normalization. Carried placement is supported by the calculation, but item-use permission still takes precedence.

For hold/charge, `F = shoulder + Q * (authoredPosition * L)` and `Rw = Q * authoredWristRotation`. With the baked calibration:

```text
W = F - Rw * (RightWristToFollowPosition * s)
Rf = Rw * RightWristToFollowRotation
item.position = F + Rf * GripPosition
item.rotation = Rf * Euler(GripEuler)
```

Clamp `W - shoulder` to `L * FollowReachFraction` and reconstruct `F` before composing the item. Reuse the same conversion in follow/reach logic. Grip offsets remain in metres so remote holding and release retain world item size.

Charge progress is `u = clamp01(elapsedVisualTime / ChargePoseDuration)`, with zero duration giving one. Evaluate the quadratic curve using `SmoothStep(0, 1, u)` as its parameter; slerp wrist orientations with that parameter. Start from the actual current procedural pose when charging interrupts equip/cancel smoothing to avoid a snap. At release, evaluate immediately using the current time, even if no presentation frame occurred after the press. Never use `Charge01` to evaluate this curve.

### Reach and the existing fade

The procedural `0.85` limit keeps the intended wrist inside the solver's `0.88` full-weight range. Animation can nevertheless move the actual shoulder away from the generated shoulder. Address that discrepancy explicitly:

- Add an optional maximum reach fraction to `AvatarPresentation.HandTarget`/`SetHandTarget`, with zero meaning existing behavior. Only item targets supply their bounded fraction.
- In `AvatarHumanoidIK.ApplyHand`, for a constrained item target, clamp the wrist delta against the actual animated shoulder to that fraction **before calculating reach fade**. Preserve the existing final `0.98` safety clamp and existing behavior for all other targets.
- Continue applying requested position/rotation weights during pause and return. Do not globally remove the reach fade or change feet.

This small solver change handles shoulder-animation discrepancies without allowing an end-pose pause to lose all IK influence. Gameplay follow termination still uses the shared generated shoulder; the visual solver correction is local presentation only.

## 3. One action/presentation coordinator

`PlayerHeldItemPresentation` owns the logical current item action and its local presentation stages. Networking carries Idle/Charging/Recovering; Follow, Pause, Return, and locally finished-but-awaiting-owner-completion are presentation stages within Recovering, not another gameplay state machine.

Cache equipment, inventory, network state, avatar presentation, graphics/input placement, registry, current definition, settings, target, and binding references. Refresh definition/selection on inventory or lifecycle changes, settings on `IdentityResolved`, and bones on binding events. Use per-frame work only for moving poses and active recovery. Register hand-target ownership on state/bind changes; update target transforms every frame when needed and update weights only while they change.

Create an independent runtime `RightItemHandTarget` and a unit-scale fallback attachment under the persistent player presentation hierarchy. Never parent either to the item or solved hand. There is no target loop.

Owner selection comes from `inventory.GetEquipped()`. Remote selection comes from `Held && Equipped && Holder == player.ObjectId` lifecycle records. Resolve this on relevant lifecycle events, including initial baseline completion/holder registration, not by scanning inventory or records every frame. Retain logical selection even while its renderer is suppressed.

Expose one visibility decision, such as `CanShowHeldItem`, and one item-use readiness decision. Visibility requires equip permission, an established remote action baseline, and no active recovery. Readiness additionally excludes incompatible carry roles. Do not apply readiness to inventory drops, swaps, or selection.

### Recovery progression

At successful submission cache the released world ID, releaser/player ID, operation, definition/timings, current wrist/body rotation, current procedural target, corrected release pose, and the grip-to-item translation. Never use the replacement's transform as a projectile reference.

1. **Follow.** Start its clock at actual release initiation, not arrival of a lifecycle message. Bind only after the item's presentation has detached and applied the matching world release. Read the visible translated item origin, with existing interpolation/correction applied; do not treat `ItemMotion.Position` as an origin when `PositionIsSphereCenter` is set. Cache the release-time world vector `F - item.position`; follow `presentedItemOrigin + cachedVector`. It stays independent of projectile spin. Set wrist rotation to current body rotation times the release-time body-relative wrist rotation. Derive the intended wrist from the follow point and calibration, and measure reach from the current shared shoulder.
2. End follow at the first reach crossing, elapsed maximum, or release becoming unavailable. Clamp overshoot to the reachable boundary. If the object disappears, preserve the last valid target. Cache body-relative position/rotation at that instant and permanently relinquish projectile tracking for this action.
3. **Pause.** Reconstruct that cached pose using the moving body frame. Skip for zero duration.
4. **Return.** Evaluate destination from the current selection/permission every frame of the blend. If equippable, blend toward its hold pose with IK active; otherwise blend IK weight to zero over the current animation. Use the thrown item's cached return duration. A selection change must not restart elapsed time; retarget continuously over the remaining time, preserving current pose/weight as the new interpolation origin when the destination changes.
5. **Complete.** Owner changes action to Idle and refreshes the normal held presentation. Reveal the selected existing item only after this step. Clear IK only when there is no holding destination. Remote visual completion alone must not clear suppression while the owner's snapshot remains Recovering; it may wait at hold/zero weight until a newer owner transition arrives.

Use actual elapsed times, not the avatar animation driver's `0.05`-second capped delta, for deadlines. When a duration expires during a long frame, carry elapsed excess into later fixed-duration stages. On a newly observed reach crossing or disappearance, start the pause then; do not wait out unused follow time. Advance zero-length stages in the same call with a bounded progression through the three stages.

`(world ID, releaser, operation)` identifies the release. Recheck that identity, active object lifetime, World state, and pickup/removal availability while following. Revision/sequence/path changes from ordinary physics corrections or bounces must not invalidate the same release. Pickup prediction, a new release of the same object, pooling, and removal must end it. Do not reconnect once following has ended.

Ordinary equip and charge cancellation reuse return-duration smoothing without entering Recovering or locking use. Menu/input cancellation during an existing recovery leaves the sequence running. Input release with no active use target is a no-op for this action.

## 4. Explicit throw transaction

### Equipment/use contract

Replace the void release chain with `TryReleaseItem`/`TryReleaseEquipped` returning whether release was initiated. The exact retained active world ID must still be the first ID of the selected stack. Keep `ThrowableItemUse` responsible for charge strength.

Refactor `ClearActiveUse`: clearing routing fields must not publish Idle automatically. In `EndUse`, retain the exact item locally, clear routing before callbacks/rebuilds can cancel it, then run the item's existing end-use callback once. A successful callback enters Recovering through submission; if it does not initiate release and the action is still Charging, cancel that charge to Idle. `CancelUse` only cancels a real charge/use target; it must not clear unrelated Recovering state.

`ThrowableItemUse.EndUse` captures its item ID and speed, clears its own charging fields, then calls `TryReleaseItem`. Call `StartPickupCooldown` only if it returns true and the object still represents that released ID. On immediate host rejection it returns false. Client success means prediction/submission succeeded; later rejection is handled by reconciliation. Keep the existing one-second pickup cooldown separate.

### Prepare and submit

1. Check existing owner, exact item, inventory, seating-transition, carry, and recovery-readiness requirements.
2. Calculate the instantaneous shared hand/grip release pose and run clearance. On failure, cancel Charging to Idle and smooth back to the current hold pose. Do not allocate an operation, mutate inventory, start cooldown, or start recovery.
3. Construct throw `ItemMotion` with the allowed position and composed item rotation. Set `PositionIsSphereCenter = false`; preserve existing aim-forward launch velocity, seated point velocity or motor velocity inheritance, and aim-rotated initial spin.
4. Leave the ordinary drop branch's placement, sphere casts, spread, rotation, and inventory behavior intact. Do not route drops through clearance or recovery.
5. In `Submit`, assign existing `Operation` and `ControlRevision`, then prepare/apply the predicted Recovering snapshot and visibility suppression **before** `PredictRelease` and `RebuildView`. Add the request to pending and run existing prediction/removal normally. This also covers a last-item throw and host owners, whose `PredictRelease` is currently skipped.
6. Notify the controller of the now-applied physical release after `PredictRelease` finishes `Initialize` and `Launch`. Host owners receive this after the accepted server `Release` applies the record. Suppression may precede physical release; tracking may not.
7. Ensure the release request is sent/processed before publishing any immediate completion transition from zero-duration recovery.

Add only the action transition metadata needed for the accepted release to `InventoryRequest` (Recovering snapshot or its compact constituent fields). Reuse the request's operation, definition, world ID, and control revision rather than transmitting contradictory duplicate identities. Do not send a separate unconditionally accepted recovery RPC ahead of inventory acceptance.

### Server ordering and reconciliation

Inside `Commit`'s Release branch, after all existing release checks pass but before removing IDs or calling `registry.Release`, install/publish the corresponding Recovering action for Throw only. Then retain existing removal, physical release, `UpdateEquipment`, and reply order. Never rerun item-use callbacks on the server. Drops during recovery leave the buffered action untouched.

On host owners, storing the matching server snapshot confirms the already running predicted sequence without restarting it or emitting a second local change. Arrange Submit/ProcessRequest's success return so synchronous rejection cannot start pickup cooldown.

In `AcceptInventory`, associate rejection/confirmation with the pending request before it is removed. A rejected throw cancels only the matching recovery, clears its transient release data, and uses existing `registry.Rollback` plus `RebuildView` to restore held presentation. Publish the corrected Idle state when still relevant. A confirmation does not restart timing. An older acknowledgement/rejection cannot clear a newer action. Apply the same matching cleanup when obsolete pending releases are discarded by control-permission changes.

## 5. Isolated clearance policy

`ItemReleaseClearance.TryResolve` receives desired item pose, a body-side reference origin (the existing aim origin), cached world-size collision envelope, body axes, and `EnvironmentMask`. It returns allowed pose or false. It has no reference to inventory, action state, RPCs, or input. Bypassing this one call permits the desired pose without other throw-transaction changes.

Use an origin-centred conservative sphere enclosing all non-trigger item colliders. `DropDiameter / 2` already expresses that intent, including offset colliders. Ensure the envelope is cached at world prefab scale before attachment: derive it from sphere/box local shape data and their relative transforms for these existing prefabs, so disabled/inactive colliders and the owner's half-scale parent cannot produce zero or undersized bounds. Preserve any larger existing `DropDiameter` value needed by departure drops. Do not resize or temporarily enable held colliders for queries.

Use this bounded initial policy, with private constants rather than new per-item gameplay settings:

1. Inflate the envelope radius by `0.01` m.
2. Find a clear body-side sweep anchor using the aim origin, then up/down/back offsets of `0.10` and `0.20` m if needed. Require a clear sphere there and an environment-clear line from the reference origin to that anchor. If none exists, reject conservatively.
3. Sphere-cast from that anchor toward the desired origin. If obstructed, propose a point just before the contact; only accept a correction within `0.20` m of the desired origin.
4. Always overlap-check the desired/corrected sphere, including zero-length casts and initial overlaps. If necessary, try bounded candidate offsets from the desired origin at `0.05`, `0.10`, and `0.20` m toward the body and along body up/down/right/left/forward/back. Accept the nearest candidate with a clear overlap and clear swept route from the anchor.
5. If none qualify, return blocked. Preserve desired rotation for every positional correction.

Use `QueryTriggerInteraction.Ignore` and the existing environment mask; retain own-player collision grace in the physics path. This conservative envelope may reject tight spaces that the exact collider could fit. It handles the different sizes and offset geometry without creating a general collision solver. The bounded sweep prevents teleporting a release through a thin wall merely because the far-side endpoint is clear.

## 6. Buffered networking and late observers

### Snapshot and ordering

Define the snapshot alongside `PlayerNetworkState`:

| Field | Purpose |
| --- | --- |
| `State` byte enum | Idle, Charging, Recovering. |
| `DefinitionId` byte | Settings when the world item is unavailable. |
| `WorldId` uint | Exact charge/release item. |
| `Operation` uint | Existing inventory release identity for Recovering and its completion; zero for an ordinary charge/cancel. |
| `ControlRevision` uint | Existing context ordering. |
| `TransitionSequence` ushort | Orders action transitions, including several in one tick. This is an action-channel sequence, not a separate throw identity. |
| `StartedTick` uint plus fractional-tick byte | Phase start in FishNet's shared `TimeManager.Tick` domain. Never compare another client's LocalTick or Time.time. |
| `ReleaseArcProgress` byte | Recovering-only quantized visual arc parameter at release; lets late observers approximate wrist orientation without a hand-pose stream or dependence on a missed Charging message. |

Use the existing `TimeManager.GetPreciseTick(TickType.Tick)`/fraction helpers, with tick delta, for shared timestamps. Locally maintain monotonic elapsed phase time so a time-sync adjustment cannot rewind a phase. Owner charge strength remains on its existing clock. Cache exact owner release pose/progress locally; quantization affects observers only.

Keep owner-local and replicated snapshots and expose `ActionChanged` plus `HasActionSnapshot`. `IsChargingUse` becomes a derived property with existing permission gating. Do not send percentage/targets each frame. Compare control revision first, then wrap-aware transition sequence; inventory operation identity is for matching release effects, not ordering unrelated charge/cancel transitions.

- Owner begins Charging immediately and sends a reliable transition.
- Cancellation or clearance rejection sends Idle only when cancelling the matching charge.
- Successful release predicts Recovering locally; the inventory request carries the transition metadata, and server acceptance publishes it before release/equipment lifecycle messages.
- Owner completion sends matching Idle, retaining the release operation for correlation. The server need not simulate recovery; trust owner completion. For same-tick begin/release/complete, sequence ordering remains unambiguous.
- The host calls the shared store/apply path once. Observer echoes never overwrite newer owner prediction or restart matching recovery.
- Preserve a pending newer-control-revision snapshot until the matching context is installed; discard older revisions. Do not silently lose it because it arrived ahead of seating/carry context.
- Reset sequences/action on ownership/session lifetime changes together with existing control initialization. Reject messages from the prior owner using existing RPC ownership checks and clear buffered state.

### Initial observation and context baseline

Replace `PlayerControlTransition.ChargingUse` with the complete item action snapshot. `PlayerSeating.OnSpawnServer` captures the current full snapshot for `TargetCurrent`. Real context transitions construct an explicit Idle/reset or intentionally preserved same-action snapshot according to the context rules, never an unidentified boolean.

Apply the initial action snapshot only after installing its control context, but keep remote held visibility closed until that snapshot is known. If a newer action RPC has already arrived, do not overwrite it with an older baseline; if a future-revision action is pending, apply it after context installation. Rejected duplicate baselines must not reset presentation. `ClearBuffedRpcs` is reserved for actual reset/lifetime boundaries; do not erase a valid recovery just to install a late-observer baseline.

Refresh this player's held attachments/visibility on snapshot readiness/change. Item baseline records can arrive before the player, before the action baseline, or in an order unrelated to held/released role. The gate handles all three without requiring a global inventory baseline reorder. A missing holder remains hidden; holder registration refreshes it through the same path.

### Remote recovery catch-up

Suppress replacement as soon as Recovering is applied. Resolve the projectile by the full release identity from **applied item presentation**, not just the registry's authoritative record: during prediction those can differ. If its matching lifecycle has not arrived, retain a bounded pending-follow phase tied to the original release timestamp, rather than following its still-held transform.

For a matching live projectile within reach and before the maximum elapsed follow time, enter at its current translated position. Derive wrist orientation from cached charge presentation or `ReleaseArcProgress`. If gone/out of reach, capture a clamped approximate current end pose and advance; never replay the charging arc. If snapshot age already exceeds maximum follow plus pause, enter return with the corresponding elapsed remainder. Exact historical early reach-stop time and pause position are not reconstructable and need not be.

Any locally reconstructed sequence may finish early, but replacement remains suppressed until owner Idle/newer Charging. Conversely, owner completion/newer action supersedes a lagging reconstruction immediately: discard old tracking, establish the new hold/charge target, and refresh visibility. This is network convergence to the owner's single recovery sequence, not a second replacement timer.

## 7. Attachment, lifecycle events, and frame order

### Shared attachment path

Factor the common held presentation work used by `WorldItem.PresentHeld` and `AttachHolder`. Both must consult the coordinator's same gate while leaving logical `Record.Equipped`/inventory selection unchanged. Never use hiding to deactivate the item GameObject and trigger unrelated use cancellation.

- Owner: retain `ViewmodelSlot`, zero local position, existing prefab root rotation, and existing half-scale parent.
- Remote with binding: parent to the resolved configured follow bone (fall back to wrist only if the configured mapping is explicitly wrist). Set grip position/rotation according to the agreed follow-bone frame.
- Preserve item **world** scale. Because the bone inherits avatar scaling, calculate local scale from cached prefab world scale divided by the bone's world scale, and convert grip metres to parent-local coordinates. Current avatars use uniform scale; do not just assign `defaultScale` under a scaled avatar.
- Remote temporarily unbound: parent to the independent unit-scale fallback, updated from the calculated follow pose. Apply the same grip and visibility gate.
- When hidden/unequipped, detach from any disposable skeleton to the persistent fallback or world root. Clear the item's existing visual correction offset through its existing helper on attachment/release transitions.

Do not subscribe every item to player action events. The controller refreshes its current held item(s) through the registry/equipment path when visibility or binding changes. Add a holder-scoped refresh if necessary instead of refreshing every player's item each frame.

Add a registry lifecycle/presentation change event containing identity and old/new held ownership as needed. Emit after `SetHeld`, accepted `Release`, completed `PredictRelease` launch, applied `ReceiveLifecycle`, rollback, and removal/pooling. It must mean the scene object's attachment/pose has been applied. Motion samples do not need action events. The controller uses this event to cache current remote equipment and release references; active following reads cached moving transforms and checks lifetime/identity.

Expose a read-only release availability/presented-origin API from `WorldItem` so optimistic pickup/removal and its existing visual offset/centred-motion presentation are handled in one place. Do not expose raw sphere-centre motion as the item origin, and do not follow the rotating grip using physics rotation.

### Explicit frame order

1. Keep `PlayerPresentation` at 50 and `PlayerAvatarPresentation` at 100.
2. Move `WorldItemRegistry` presentation to execution order 150 so interpolated/corrected projectile visuals are current before follow targets. Physics tick processing remains on its existing callbacks.
3. Run `PlayerHeldItemPresentation.LateUpdate` at 175 for owner/unbound recovery and fallback presentation only.
4. Keep `AvatarPresentationSystem` at 200. After `host.UpdateInput` and any preparation, invoke a pre-evaluation callback once per host; the bound controller advances recovery and updates its target there. Then evaluate the graph, IK, VRM, springs, and binding commit in their current order.
5. A controller is advanced by exactly one of step 3 or step 4 per frame. Rendering-disabled/failed/unbound avatars still advance their logical action. Candidate warmup must not advance it again. A bind committed at frame end adopts the current target/action immediately without resetting time.

Parenting the held item to the follow bone supplies post-IK movement without a second copying loop. A release triggered during input evaluates the shared pose synchronously; it does not wait for LateUpdate or evaluate an owner avatar graph.

### Rebinding and teardown

On `IdentityResolved`, cache new measurements/calibration; preserve charge/recovery identity and elapsed times. During overlap between the old active binding and new candidate, use the active binding's settings for its visual target/attachment, and the newly resolved settings for skeleton-free gameplay/fallback; switch atomically at `DidBind`.

On `WillUnbind`, detach any held item from the old skeleton before `AvatarInstance.Release` deactivates/destroys it; clear cached bones and old target ownership. Do not clear the logical action. On `DidBind`, cache the new wrist/follow bones, restore target/weights/constraint, and reattach through the visibility gate. Recalculate reach and current hold/charge pose; for follow/pause/return, retain timing and re-clamp the retained body-relative pose for the new arm lengths.

On ownership loss, stop/despawn, disable/destroy, or disconnect cleanup, unsubscribe every event, release target ownership, detach surviving items before destroying helper transforms, discard release references, and clear local action/timing. A replacement owner starts from a clean snapshot. Existing registry departure-drop and pooling behavior remains responsible for the actual item lifecycle.

## 8. Input, cancellation, and context integration

Use these rules at the existing call sites; do not redefine all cancellation as full reset:

| Trigger | Required implementation |
| --- | --- |
| Press during recovery | `PlayerEquipment.BeginUse` ignores it before starting an inventory item callback. Retain carried-player routing independently. No queue or held-button polling to auto-start later. |
| Release without active item | No action-state change; in particular no Idle publication over recovery. |
| Menu, focus, input-device/map interruption | Cancel Charging through existing `PlayerInputReader` context path and retain its held-button blocking. Ongoing recovery continues. Ensure the suppressed-input early return also cancels inventory-item charging, not only `carry.IsCharging`. |
| Selection, swap, drop, stack change | Cancel an affected active charge; update current selection. Keep an existing recovery and its cached timing. Hidden replacement remains logically available for ordinary drops. |
| Driver, carried role, incompatible carrying role, placement/context invalidation | Explicit full item-action reset: tracking, stage, timers, target ownership, buffered action. Do not retain a deadline. Reuse existing control/context events; add notifications for pending-permission transitions if they currently only mutate booleans. |
| Return to permitted context | Establish ordinary current holding presentation; do not resume discarded action. Preserve existing slot selection rules, including seat-driven deselection. |
| Movement/animation change | No action reset. |
| Avatar swap | Rebind as above; no logical action reset. |

Use `ControlPermissionsChanged`, seating/carry presentation-context events, and explicit reset calls at their mutation boundaries. Do not add a no-op context poll in Update. `ApplyControlPermissions` must reconcile stale pending operations, but a generic call to `CancelUse` alone must not erase recovery. Any actual context revision invalidating a pending throw clears the matching prediction and cannot leave an unidentified buffered recovery.

## 9. Implementation order and Unity setup

Implement in this dependency order so each layer has a defined consumer:

1. Add settings, baked calibration, and shared pose/clearance calculations. Update the existing processor and right-hand IK calibration contract together.
2. Add snapshot data/change notifications and the controller's single recovery sequence/visibility gate. Implement context baseline handling and owner/host transition deduplication before hooking throws.
3. Connect equipment success/cancel semantics and inventory submission/acceptance ordering. Preserve the drop branch. Connect rollback by operation.
4. Add lifecycle-applied events, owner/remote equipment resolution, and the shared held attachment path. Wire baseline visibility readiness and release identity checks.
5. Add explicit frame ordering, item-only reach constraint, binding hooks, input/context reset handling, and lifetime cleanup.
6. In Unity, add `PlayerHeldItemPresentation` once to `Assets/Game/Prefabs/Player.prefab`. Cache sibling references in Awake and use the existing `PlayerAvatarPresentation.Presentation` reference. Runtime target/fallback transforms require no serialized scene objects.
7. Populate the existing `Rock.asset`, `Mushroom_Amanita.asset`, and `Basketball.asset` under `Assets/Game/ScriptableObjects/Items/`. Use the shared initial settings and item-specific grip seeds above.
8. Reprocess the two existing avatar sources using the existing processor so their generated settings/prefabs carry calibration. Do not generate throw clips, new item prefabs, or new pose assets.

Use Unity authoring operations directly rather than writing a one-off editor migration. Preserve unrelated user changes. Keep comments concise and use implicit Unity object lifetime checks in new code.

## User visual acceptance after implementation

Use the specification's complete 18-item acceptance list. The following checks emphasize the integration risks that must be visible to the user:

- With host and client, equip rock, mushroom, and basketball on both current avatars; compare grip alignment and world size. Swap avatars while holding, charging, and recovering; the same inventory object must survive.
- Charge while moving, jumping, and in permitted passenger seats; look straight up/down. The arc stays body-relative, while quick/partial/full-charge throws immediately use the existing aim and strength.
- Set a visible pause, such as `0.15` seconds. Fast throws must stop at reach, remain visibly posed while moving/turning, and return without spinning with the item or losing IK to the reach fade.
- Throw a stack, change slots, reorder inventory, drop the hidden replacement, and throw the last item. Replacement appears only at owner recovery completion and reflects current selection. Press/hold during recovery; only a later fresh press starts use.
- Set follow/pause/return durations to zero individually and together. There must be no extra frame-stage waits or hidden replacement timer.
- Try walls, corners, and low ceilings with the larger basketball and offset mushroom/rock colliders. Small corrections are permitted; blocked throws keep the inventory item and do not start recovery or pickup cooldown. Ordinary drops keep their existing behavior.
- Cancel through a menu/focus change and enter/leave driver or carry contexts. Cancellation never produces a delayed throw; incompatible contexts discard the old recovery.
- Have another player pick up a followed item, let a weak throw settle nearby, and observe removal/reuse. The hand must stop following that release and never attach to a pooled replacement.
- Rapid-tap throws from both host and client, and join/observe during charging/recovery. There must be one release per action, no early replacement flash, no stale recovery restart, and prompt adoption of newer owner completion/charging.

Automated rejection/network-delay scenarios, builds, and Play Mode checks require a separate explicit request; do not add or run an unsolicited validation harness.
