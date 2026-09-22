# Slingshot Polish Implementation Plan

## Objective and scope

Implement [Slingshot_Polish_Spec.md](Slingshot_Polish_Spec.md), addressing every reported bug in [Slingshot_Polish.md](Slingshot_Polish.md). The specification governs details that refine the original notes: Environment collisions apply globally to ItemWorld; pebbles omit rotation on every transport path without a new tumble system; first-person charging preserves camera-relative height and depth; remote charging uses existing replicated look angles.

Retain the existing projectile registry, held-item presentation, avatar IK, action replication, anchor transforms, and two band LineRenderers. Extend their specific calculation and serialization boundaries. No replacement gameplay system, new network message type, new pose payload, model import, prefab creation, component addition, or game-scene edit is required. The existing slingshot definition asset needs grip changes; the existing prefab's assigned anchors remain the model contract.

Interpret horizontal centering as the fork midpoint reaching viewport X = 0.5, with its uncharged camera-space Y and Z preserved. It does not mean moving the forks to the screen's vertical midpoint. Judge the unobstructed pose separately from existing wall-clearance corrections: collision-safe release still takes precedence near geometry.

## Bug-to-source map

Paths below are relative to the project root. Method names are the navigation anchors; line numbers in the original stack traces may shift.

| Reported problem | Source and causal path | Targeted correction |
| --- | --- | --- |
| Pebbles pass through Environment geometry | `ProjectSettings/TagManager.asset` assigns ItemWorld = 9 and Environment = 13. `ProjectSettings/DynamicsManager.asset:20` disables their pair. `PebbleProjectile.Awake` does not override this pair. | Enable the global pair, preserving every other matrix bit and collider override. |
| `Physics.ClosestPoint` error on contacts | `Projectiles/PebbleProjectile.cs`: `BeforePhysics` and `UpdateShooter` call `Overlaps`, which calls `collider.ClosestPoint(body.position)` without restricting collider types. `SeedContacts` already uses a sphere-overlap query. | Replace these membership checks with complete sphere-overlap results, preserving the separate shooter and contact radii. |
| Holding palm faces upward and handle crosses its center | `ScriptableObjects/Items/Slingshot.asset`: both pose sets have zero hold/charged wrist Euler angles and identity grip rotation. `HeldItemPose` converts palm orientation through generated wrist measurements. `Editor/AvatarProcessor.MeasurePalm` defines palm +Y as the normal and +Z along the fingers. | Rotate the holding palm +90 degrees around its local Z; compensate grip rotation and position in the definition. |
| Charge raises the slingshot and does not center it | The definition raises first-person Y from -0.40 toward -0.19 and remote Y from -0.45 toward -0.12. Both control points also rise. `HeldItemPoseCalculation.Charge` evaluates a shoulder-relative Bezier with overshoot. `PlayerHeldItemPresentation.ChargeProgress` completes the slingshot pose in 0.15 seconds despite its 1-second strength charge. | Give slingshot charging an anchor-aware pose calculation, driven by actual normalized charge. Compute local centering from the rendered camera and remote centering from shoulder measurements. |
| Remote holding arm stays bent and aim does not follow pitch | `HeldItemPoseData.Reach` caps reach at 0.85. `HeldItemPoseCalculation.Resolve` softens reach before that cap. `PlayerHeldItemPresentation.SetTarget` passes the capped value to IK. Remote body frames use torso rotation rather than look pitch. `AvatarHandIK.Apply` already supports reach up to 0.98. | Supply a slingshot-specific reachable target and effective reach through calculation and IK; derive aim orientation from existing look yaw/pitch. Preserve ordinary item limits. |
| Relaxed bands are angular; firing barely moves them | `SlingshotPresentation.Band` writes only two endpoints. `Evaluate` always clamps the pouch to left-arm reach, even at `LeftWeight == 0`. Recovery adds only `0.008 * sin(age * 85) * exp(-age * 22)` and ignores release charge. | Sample smooth curves, leave an unengaged pouch at its authored anchor, and add charge-scaled finite-duration release motion. |
| Standalone remote client reports non-unit quaternions | `PebbleRegistry.Tick` sets omission only for periodic snapshots. `PebbleProjectile.Capture` delegates to shared capture, which creates supplied-rotation states; separation, impact and terminal transitions use that capture. Fire has no custom serializer, while `ItemMotion.RotationOmitted` is excluded from generated serialization. `ReadPackedMotion` leaves omitted rotations as a zero quaternion. `RigidbodyMotionState.Sample` unconditionally Slerps both rotations and copies only the destination omission flag; `Apply` then assigns a mixed invalid result if that flag is false. | Establish omission at creation/capture and fire serialization, retain valid local rotations, and make shared interpolation respect both samples' rotation presence. |
| Temporary mesh replacement may break presentation | `SlingshotPresentation` uses explicit transform/LineRenderer references and caches anchor positions in `Awake`; it does not find meshes or renderers by name. `WorldItem.ClearVisualOffset` resets its visual wrapper's local position/rotation. | Preserve this contract. Derive new centering/band calculations from assigned anchors; put model import corrections beneath `visualRoot`. |

Runtime paths in this table are under `Assets/Game/Runtime`; asset and editor paths are under `Assets/Game`.

## 1. Enable world collisions and repair contact membership

**Change:** `ProjectSettings/DynamicsManager.asset` and `Assets/Game/Runtime/Projectiles/PebbleProjectile.cs`.

1. In **Project Settings > Physics > Layer Collision Matrix**, enable **ItemWorld / Environment**. Persist the project setting, including its symmetric representation. Do not use `sphere.includeLayers` or a startup override as the fix.
2. For a surgical serialized edit, the current little-endian matrix row 9 is `9f8ef8ff`; enabling bit 13 makes it `9faef8ff`. Row 13 is `dfe8f8ff`; enabling bit 9 makes it `dfeaf8ff`. Locate rows by layer index and preserve the remainder of the matrix. Keep Default (0) and Ground (7) enabled. The existing `Assets/Game/Editor/WorldItemSetup.cs` layer setup already permits this pair when run; invoking that broad setup is unnecessary.
3. Replace `Overlaps(Collider, float)` with a small sphere-query helper inside `PebbleProjectile`. Reuse a collider buffer with `Physics.OverlapSphereNonAlloc`, all layers, and `QueryTriggerInteraction.Ignore`. If the result fills the buffer, grow it and retry until results are complete. A truncated result cannot safely establish separation. This preserves the existing sphere-query approach without allocating on every ordinary physics step. Unity documents both [sphere overlap membership](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Physics.OverlapSphere.html) and the [fixed-buffer behavior of NonAlloc](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Physics.OverlapSphereNonAlloc.html).
4. In `UpdateShooter`, query only while shooter separation is pending, at exactly `radius`. Retain ignore pairs until no live, enabled, non-trigger shooter collider appears in the results. Queries must include shooter geometry even while the pebble's collision pairs are ignored. Keep `ClearShooter`, the one-time transition, and restoration of ignore pairs.
5. In `BeforePhysics`, query at `radius + 0.002f` only when existing contacts require separation checks. Remove missing/disabled/destroyed colliders from `touching`. Do not populate new contacts from this query: `Contact` remains responsible for accepting physical impacts. Preserve `contactEnded`, sequence advancement and its transition.
6. Reuse the query for `SeedContacts`, retaining its self/projectile exclusions. Keep shooter and touching queries distinct because their tolerances differ. Remove the unsupported `ClosestPoint` helper; Unity explicitly [restricts the closest-point physics operation to primitives and convex meshes](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Physics.ClosestPoint.html).

**Preserve:** `OnCollisionEnter/Stay/Exit`, contact sorting, simultaneous-contact deduplication, reflected velocity and `ReboundRetention`, first-impact continuation, removal at `Impact >= 2`, kill-volume handling, and bird/player damage. Do not change world colliders to convex, estimate contact using bounds, or alter CCD. Keep held items on their current disabled-collider/ItemHeld path, world-item temporary suppression, and pebble shooter immunity.

## 2. Make pebble rotation omission an end-to-end contract

**Change:** `Player/PlayerEquipment.cs`, `Projectiles/PebbleMessages.cs`, `Projectiles/PebbleProjectile.cs`, `Projectiles/PebbleRegistry.cs`, `Items/WorldItemMessages.cs`, and `Items/RigidbodyMotionState.cs` under `Assets/Game/Runtime`.

### Creation and transport

1. In `PlayerEquipment.TryFireSlingshot`, initialize fire motion with `RotationOmitted = true`, a valid identity placeholder, and zero angular velocity. Retain sphere-center position, launch tick, velocity inheritance, charge/speed calculation, prediction and acceptance order.
2. Add explicit `WritePebbleFire` / `ReadPebbleFire` extension serializers in the existing `PebbleSerializers` class. Write/read `Epoch`, `Lifetime`, `Shot`, `Weapon`, `Motion`, and `Action` in matching order. Use `WritePackedMotion` / `ReadPackedMotion` for motion and FishNet's existing `Write<ItemActionSnapshot>` / `Read<ItemActionSnapshot>` support for the action. For fire only, force the packed format's existing `FullVelocity` encoding through a small optional writer argument, defaulting to current behavior for all other callers. This preserves the generated fire message's full-precision launch velocity instead of introducing snapshot quantization at launch; the reader already supports that flag. Explicit fire serialization is necessary because generated `ItemMotion` serialization omits the omission flag while still including rotation fields. Do not add a global custom `ItemMotion` serializer or remove `ExcludeSerialization`: those would change unrelated transport paths.
3. In `PebbleRegistry.Create`, establish the same omission invariant for its initial record, covering host-local acceptance, prediction, rejection and spawn. In `PebbleProjectile.Capture`, set omission on every captured pebble motion and use valid identity/zero placeholders for unused rotation fields. Keep this pebble-specific; shared `RigidbodyMotionState.Capture` must still capture ordinary items' rotations.
4. Remove `PebbleRegistry.Tick`'s now-redundant snapshot-only omission assignment. `WritePebbleRecord` already uses packed motion, so all record messages should inherit the flag without new fields or per-event rotation special cases.
5. Preserve the invariant through the following routes:

| Path | Producers and consumers to cover |
| --- | --- |
| Fire | `TryFireSlingshot` -> prediction and `CmdFireSlingshot` -> `AcceptShot` |
| Spawn/reject | `Create`, `Predict`, `Accept`, `Reject`, `Receive(PebbleSpawn)`, `Initialize` |
| Periodic motion | `Capture`, `Tick`, `QueueMotion`, packed batch relay, `ReceiveMotion`, `Receive` |
| Shooter separation/contact end | `UpdateShooter` / `BeforePhysics` -> `Capture` -> `Transition` -> `ContactState` |
| First impact | `AfterPhysics` -> `Capture` -> reliable transition -> `Boundary` |
| Terminal | Second impact, `End` for kill volume/lifetime/bounds, and host timeout using stored motion |
| Baseline/takeover | `Baseline`, `TakeOver`, respawn, `EarlyMotion`, and queued pre-acceptance transitions |

Both endpoints must use the updated build because fire serialization changes. Preserve message type, reliability, identifiers, revision/sequence/path ordering, epoch/lifetime checks, and shooter-side simulation.

### Shared decoding, interpolation and application

Set a valid identity placeholder when `ReadPackedMotion` decodes omitted rotation, retaining `RotationOmitted = true`. A valid placeholder does not make that rotation supplied.

Change `RigidbodyMotionState.Sample` according to this table, without changing position interpolation, extrapolation, velocity output, boundary timing or collision casts:

| From rotation | To rotation | Sample behavior |
| --- | --- | --- |
| Supplied | Supplied | Existing Slerp and supplied result. |
| Omitted | Supplied | Use the supplied destination rotation directly; do not blend in the omitted placeholder. |
| Supplied | Omitted | Mark result omitted; use an identity placeholder and let the body retain its local orientation. |
| Omitted | Omitted | Mark result omitted; use identity and skip rotation interpolation. |

`RigidbodyMotionState.Apply` already skips both rotation and angular-velocity assignment when omitted; preserve that behavior. Do not introduce normalization as the primary repair or reinterpret an omitted quaternion as a rotation update. Ordinary world items retain their existing filled-in motion history and supplied boundary rotations; keep `WorldItem.ReceiveMotion`, cosmetic spin, and world-item `SyncRotation` policy intact.

### Pooled bodies

In `PebbleProjectile.Initialize`, establish identity body rotation for each rental before applying omitted motion. Reset angular velocity for each shot at a point where the body is dynamic, avoiding kinematic velocity writes; `StopPhysics` already zeros dynamic velocities before making returned bodies kinematic. Preserve zero spin on observer rentals and zero initial spin when an observer becomes simulator. Do not reset a live simulator's rotation on later motion/contact messages. Shooter-side physics may rotate naturally; no cosmetic tumble or observer rotation inference is added.

**Regression gates:** mixed omission samples must never feed a zero quaternion into Slerp or `Rigidbody.rotation`; every pebble packet omits rotation and angular velocity, including fire and reliable transitions. Dropped slingshots and other ordinary world items that synchronize rotation must continue doing so.

## 3. Correct the holding grip through the existing definition

**Change:** `Assets/Game/ScriptableObjects/Items/Slingshot.asset`.

1. Set both `FirstPersonPose` and `HandPose` hold and charged wrist Euler values to `(0, 0, 90)`. In the generated palm frame, this maps palm +Y from up toward avatar-local left. Keep all four values consistent so recovery cannot interpolate back to a palm-up grip.
2. Set `GripEuler` to `(0, 0, -90)`, compensating the palm rotation so the item remains upright in its pose frame. Use existing `RightWristToPalmRotation` conversion; do not rotate raw bone axes or change avatar calibration.
3. Re-express the current grip position in the corrected palm frame before adjusting seating: inverse-rotating `(0, 0.025, 0)` gives `(0.025, 0, 0)`. Add a small offset along corrected palm +Y to seat the handle on the finger side of the palm. For the temporary handle's approximately 0.034-m width, start around 0.02 m and tune visually on both avatars. Preserve the desired item placement through the pose calculation rather than hiding a mesh-specific correction in code.
4. Preserve `PullingPalmEuler = (0, 0, -90)` and `PullingPalmOffset` in the prefab. The pulling hand retains its existing orientation relative to the slingshot.
5. Align the authored charge control/end Y and Z with hold values for first-person pose and remove the authored upward charge lift in `HandPose`. Actual centered endpoints come from the calculation below; fixed authored X values must not decide screen/body centering.

Keep projectile tuning, held-item collision settings, `SyncRotation`, finger behavior, and recovery duration unchanged. The slingshot asset's `SyncRotation` describes the loose weapon, not its pebbles.

## 4. Calculate centered, reachable slingshot charge poses

**Change:** `Assets/Game/Runtime/Items/HeldItemPose.cs`, `Items/SlingshotPresentation.cs`, `Player/PlayerHeldItemPresentation.cs`, and `Player/PlayerHandPresentation.cs`.

### Shared calculation and inputs

Extend the existing `HeldItemPoseCalculation` with a slingshot charge calculation rather than duplicating pose math in local/remote presenters. Use `FromItem` to convert the solved item frame into calibrated wrist/palm targets. Return/use one effective holding reach for both pose resolution and IK. Keep ordinary `Hold`/`Charge`, the 0.85 default cap, and their easing unchanged for other items.

Expose only the cached item-local anchors needed from `SlingshotPresentation`: fork midpoint, rest/draw pouch positions, and existing pulling-palm settings. Cache them from assigned transforms in `Awake`, as today; retain scale conversion. Never use renderer bounds, mesh names, temporary model dimensions, or fixed shoulder widths for centering.

Expose the existing rendered `CameraPose` from `PlayerHandPresentation` to the holding calculation. Initialize its camera reference at presentation start and handle the existing `PlayerPresentation.LocalCameraChanged` event if needed for creation/recreation; use the same frame for immediate sampling and normal late presentation. The owner uses the actual rendered camera, with the existing aim fallback only while a camera is unavailable. Remote calculations use `avatar.Input` or `playerAvatar.CurrentPlacement` for unbound presentation, including existing `LookYaw` and `LookPitch`.

Drive the slingshot pose with `clamp(age / ThrowChargeTime, 0, 1)`, consistent with strength and band draw. Replace the slingshot-only hardcoded 0.15-second `ChargeProgress` behavior; leave other items' `ChargePoseDuration` behavior intact. A clamped smooth easing without overshoot may shape the pose blend. Recovery reconstruction uses `ReleaseArcProgress / 255f` as the stored release charge.

### First-person solve

1. Obtain the corrected uncharged hold item pose for the current avatar/body/camera frame, including the existing first-person hold offset, generated shoulder/palm measurements, grip position, and item scale. Use the same uncharged reference throughout a charge; never feed the previous frame's already-centered result back into the calculation. Preserve an existing equip/cancel transition's starting pose if charge begins during a blend.
2. Let `F0` be the fork midpoint transformed by that uncharged item pose, `C` the rendered camera position, and `R` the camera's right vector. The required full-charge translation is `delta = -dot(F0 - C, R) * R`.
3. Blend item position from the uncharged pose to `uncharged.position + delta` using normalized charge. Preserve its rotation relative to the camera, with the corrected grip. This changes only camera-space X; item and fork Y/Z remain constant at fixed view orientation.
4. Compute the required wrist reach from the solved item pose using `FromItem`. Retain ordinary holding reach when sufficient; allow only the necessary slingshot reach increase, within the existing IK ceiling of 0.98. Pass this same effective reach into owner clearance and the final right-hand target so a later 0.85 clamp cannot reintroduce vertical/depth movement or undo centering. Do not apply ordinary soft reach compression to an already solved reachable target.
5. Treat a requested sideways path beyond physical 0.98 reach as an incompatible grip/hold configuration to resolve in the slingshot settings, not permission to silently change camera-relative height/depth or globally stretch arms. Both current avatar proportions are acceptance targets.

### Remote solve

1. Derive the left shoulder from the current right-shoulder body frame and scaled generated shoulder difference. Their midpoint supplies the body center; retain the uncharged fork's vertical offset relative to that center for a level aim.
2. Build the aim orientation from `Quaternion.Euler(LookPitch, LookYaw, 0)`. Blend the charged item orientation into this frame with the existing grip compensation. Keep actual shoulder positions in the torso/body frame; applying pitch to the measured shoulder offset would invent moving shoulders.
3. Place the charged fork midpoint on the center-based forward aim ray. Solve forward distance using the holding wrist position implied by fork offset, grip and wrist-to-palm measurements, with a target reach of 0.98 of the holding arm. This uses the existing IK safety ceiling and leaves a small elbow bend. Do not multiply the current shoulder-relative Z value or clamp a faraway centered target radially: either approach can lose centering.
4. Include the pulling wrist constraint in the same distance solve. Its target comes from the authored pouch at the current draw amount, `PullingPalmOffset`, and left wrist-to-palm measurements, with the existing 0.85 pulling reach. The arm can be more bent; preserve the entire authored `RestCenter` -> `DrawCenter` displacement. Do not shorten draw by moving the pouch independently to the left arm's reach boundary.
5. A compact analytic solution is sufficient: along the desired fork ray, each implied wrist has form `A + d * forward`; intersect each wrist's allowed distance interval with its shoulder-centered reach sphere, then choose the furthest forward feasible distance up to the holding target. Blend from the uncharged pose toward that solution and retain feasibility at intermediate charge/pitch. If the left constraint limits extension, move the shared item frame along the ray; do not detach the pulling hand or flatten pitch. No generic IK solver is needed.
6. At fixed level look, this adds centering and forward extension without a scripted upward arc. At nonzero pitch, the pose follows the existing replicated look direction. It need not converge on the owner's camera raycast target.

### Integrate every presentation entry point

Route slingshot pose creation through the same calculation in `ActionChanged`, `RefreshPose`, `PrepareCandidate`, and recovery reconstruction when charging was not observed. Preserve current binding measurements for avatar replacement candidates. Carry the effective reach through `SetTarget`, `ResolveClearance`, committed correction and the return blend; ease it back with recovery/cancellation so a switch to the default cap cannot snap the hand inward.

`CommitHands` remains the source of actual held placement. Keep `TryPrepareRelease`, `TryPreparePebble`, owner wall clearance, `CommitCorrection`, `CommitPalm`, and `TryPebbleDeparture` coherent with the final solved hands. Keep charging pouch attachment to the committed pulling palm, but design reach constraints so this correction does not erase the authored draw distance. Do not replace this commit path with visual-only item positioning.

No changes to `AvatarHandIK` should be necessary: it already honors the target reach up to 0.98. Free hands, steering/contact targets, throwable windups and follow-through retain their current settings. Near-wall clearance may constrain the pose or reject a release as before; do not loosen that behavior to force centering.

## 5. Draw smooth bands and finite release motion

**Change:** `Assets/Game/Runtime/Items/SlingshotPresentation.cs`; retain the two assigned LineRenderers in the existing prefab.

1. Set a small fixed sample count, initially 9 points per band, in initialization. Reuse the existing line renderers and their local coordinate spaces. Initialize the point count before `ResetPose`, and update all sampled points in `Band`, including calls from `Evaluate`, `CommitPalm`, and `ResetPose`.
2. Sample each fork-to-pouch span as a continuous curve: `lerp(fork, pouch, t) + 4*t*(1-t)*sagOffset`. Choose a gentle rest sag proportional to authored fork/rest span, directed down in the item frame. Scale sag by `1 - drawAmount`, with exact endpoints and zero slack at full draw. Use the same curve routine after committed-palm correction so that phase cannot replace the curve with straight or stale points.
3. Separate pouch geometry from hand targeting. At idle with zero pulling weight, place `Center` exactly at the authored `RestCenter` in the evaluated item frame. Do not apply left-arm reach correction. During cancellation/hand disengagement, fade any necessary hand-driven correction with hand weight so the pouch smoothly returns to its anchor. During charging, use the jointly reachable pose from step 4 and keep the loaded pebble/pulling palm attached.
4. Replace the tiny existing recovery sine with deterministic release motion based on action `age` and the already passed release charge (`ReleaseArcProgress / 255f`). Use a roughly 0.35-second duration within the existing 0.5-second recovery. Drive a short pouch recoil from its charged position into rest and a visible damped transverse wave through the intermediate band samples. Scale amplitude using a nonzero weak-shot floor, for example `lerp(0.2, 1, releaseCharge)` times a small fraction of authored draw length. Tune constants visually; no new inspector settings are required.
5. Use an envelope that becomes exactly zero at the settling deadline, such as `(1 - clamp(age / 0.35, 0, 1))^2`, with several oscillations over the interval. A spatial `sin(pi*t)` factor pins the fork and pouch endpoints for the additional line wobble. At/after the deadline, assign the exact relaxed curve; an exponential tail alone never completely settles.
6. Keep `Center` as pouch position and leave line-only displacement cosmetic. Update `DepartureCenter` only from the loaded/committed charging pose, retaining that value during recovery for delayed projectile spawn matching. Release motion must not feed back into an already submitted projectile's position, direction or velocity.
7. Preserve the existing charging-to-idle cancellation return over approximately 0.2 seconds, without firing oscillation. New charging, selection changes, rejection, disable/pooling and `ResetPose` must not carry old wobble or loaded visibility into a later use.
8. Derive recovery phase from action age, not accumulated per-call delta or local observation time. `PrepareLeft` can run several times during a render frame/IK correction, and remote clients may first observe recovery partway through it. Neither case should restart or amplify wobble.

**Preserve:** pulling-palm orientation, charge attachment, loaded-pebble visibility only while charging, existing recovery deadline, and all shot gameplay parameters. The two bands share the same pouch endpoint and settle to the same authored rest center.

## 6. Preserve model replacement compatibility

`SlingshotPresentation` has no mesh-name dependency to remove. Keep `LeftFork`, `RightFork`, `RestCenter`, `DrawCenter`, `LoadedPebble`, `LeftBand` and `RightBand` as explicitly assigned references. New midpoint, span and draw-distance calculations must be derived from those anchors.

For a later model replacement, retain the `WorldItem.visualRoot` wrapper and place model-specific translation, rotation and scale on a child beneath it. `WorldItem` owns and resets the wrapper's presentation offsets. Preserve/reassign anchors and renderer references, position anchors to match the new forks/pouch, and tune definition grip settings. Reinitialize normally after authoring changes so cached anchor geometry reflects the prefab. Runtime model swapping and mesh discovery remain outside scope.

Do not import or replace the model as part of this plan. The existing prefab needs no structural edit for sampled lines; point count is initialized by presentation code. If authoring later requires changing anchor positions, edit the existing prefab references directly without scene changes.

## Implementation sequence and regression gates

Complete steps 1 and 2 together before exercising newly enabled Environment contacts: enabling those collisions exposes the existing unsupported contact query and more rotation transition paths. Apply grip changes before finalizing pose math. Complete pose/commit integration before band motion so idle pouch position and draw distance have a stable definition. Preserve the model contract throughout.

Use the following cases as explicit implementation review criteria and user acceptance checks. They describe required outcomes, not a claim that an unimplemented plan guarantees regression freedom. Runtime and visual acceptance belongs in Unity and a host/client session with a remote standalone build; both sides need the same updated serialization.

### Data and behavior regression cases

| Area | Cases and required result |
| --- | --- |
| Matrix | Only the symmetric 9/13 pair changes. Ground/Default still collide with ItemWorld; held-item and temporary ignore behavior remain intact. |
| Contact membership | Non-convex wall/ramp, primitive collider, sustained contact, separation and recontact, simultaneous seam contacts, disabled/destroyed contact, and more overlaps than initial buffer capacity. No unsupported closest-point call, phantom separation, duplicate bounce, or shortened shooter immunity. |
| Fire serialization | Round-trip distinct values for every `PebbleFire` header/action field and packed motion flags. Omission survives, rotation/angular bytes are absent, full-precision launch velocity is unchanged, and following fields remain aligned. Exercise the actual client RPC path; host-local calls bypass serialization. |
| Pebble record serialization | Spawn/baseline, separation, first impact, second-impact terminal and timeout retain omission and existing position/velocity/tick/path/sequence data. Compare small velocities with packed precision; exercise full-velocity fallback too. |
| Shared interpolation | All four rotation-presence combinations above, single sample, equal tick, interval endpoints and intermediate time. Omitted endpoints may deliberately contain a zero quaternion: no Slerp may consume it. Supplied rotations still interpolate normally; positional sampling/extrapolation is unchanged. |
| Pooling | Repeated simulator rentals, observer rentals, rejection after prediction and observer-to-simulator takeover begin with valid local rotation and reset spin. No stale touching/shooter ignore/terminal state affects the next shot. |
| Network ordering | Delayed spawn, transition before acceptance, `EarlyMotion` crossing an impact path, late-join baseline, terminal boundary and shooter disconnect/takeover preserve one shot, one first rebound, one terminal removal and normal departure smoothing. |
| Ordinary world motion | A rotation-synchronized loose item still rotates on observers. Items with omitted periodic rotation still receive supplied lifecycle/boundary rotation correctly. Their position, sleeping and cosmetic rotation handling do not regress. |
| Pose math | Both avatar measurement sets, nonzero grip/anchor offsets, partial/full charge, level/up/down aim, first-person rendered-camera offset, avatar rebinding and recovery observed without charging. Full-charge fork placement follows its camera/body constraint after final IK commitment. |
| Item/action integration | Weak tap, full charge, cancel, re-equip, selection change mid-charge, action rejection, seat/carry/ragdoll control transitions, and avatar replacement do not leave a hand target, loaded pebble, recovery pose or band oscillation attached to the wrong item. |

These are narrowly scoped regression cases suitable for focused automated checks if that validation is requested, plus the manual checks below. Do not introduce a new test architecture or migration tool to implement the feature.

### User visual acceptance

Run with the host and a remote standalone client; reverse the shooter/observer roles. Repeat proportion-sensitive checks with both avatar types.

1. In Physics settings, inspect the enabled ItemWorld/Environment pair. Shoot pebbles and drop a rock, basketball and slingshot onto Environment walls/ramps; also use Ground and Default surfaces. Pebbles rebound once and disappear after their second impact. Inspect standalone logs for the original closest-point and unit-quaternion errors during launches, separation and impacts.
2. Inspect holding palms from the player's view and an observer. They face the avatar's left; the handle is upright and seated through idle, charge and recovery. The pulling hand retains its orientation and follows the pouch.
3. With a fixed, unobstructed camera, compare uncharged and full-charge fork midpoints. Full charge reaches the horizontal screen center; camera-relative height and depth stay fixed throughout the charge. Repeat while looking level/up/down and with both avatars. Near a wall, existing clearance/rejected-release behavior remains functional.
4. Observe remote charging from front and side. The fork midpoint centers on the body/aim frame, the holding elbow is nearly straight with a small bend, and the pulling arm stays more bent without reducing band draw. Look up/down and turn the view; the pose follows visible look direction without new aim data.
5. Inspect bands idle, partly drawn, fully drawn, on a weak tap and on a strong shot. Rest is a smooth gentle sag; full draw is taut. Firing visibly wobbles and settles completely around 0.35 seconds. Canceling returns smoothly without firing wobble. Repeated correction/equip cycles leave no extra points, stale curves or floating pebble.
6. Observe remote launches and rebounds over many pooled shots, then join during flight and disconnect a shooter during flight. Projectiles remain stable and their removal/departure effects occur once. Standalone logs remain free of the reported rotation error.
7. Hold/throw representative non-slingshot items and use steering/contact hands. Their reach, grip, charge/recovery behavior and rotation presentation remain unchanged apart from the intended global Environment collisions.
8. When the replacement model is authored, preserve/reassign the anchor and renderer references under the visual wrapper, rename visible mesh objects freely, and repeat grip/draw/band checks. Ordinary replacement must require no presentation-code changes.
