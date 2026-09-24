# Hand IK Stability Spec

## Goal
Hands on held items in third person are stable. There are no frame-to-frame flips between two hand poses, no arms thrown behind the avatar, and no one-frame lag between the animated body and the hands. Third-person avatars and the local first-person rig use the same hand solver.

This spec does not change the held-item data model, the `HeldItemPresentationState` state machine, or any network message. Spec B (`Held_Item_Pose_Layers_Spec.md`) builds on this pipeline.

## Decisions
| Topic | Decision |
|---|---|
| Solver | A custom two-arm IK animation job replaces Mecanim `OnAnimatorIK` hand IK. No Animation Rigging package. |
| Out of reach | Held items are never moved to fit the arms. The arms reach toward the grips and may fall short. |
| Full extension | Soft-IK near the reach cap. No bone stretch. Fixed constants, not settings. |
| Body frame | One frame, built from bones: the right and left `UpperArm` positions plus the animator root rotation. The job rebuilds it from **this frame's** animation stream. |
| First-person rig anchor | The settings-built heavy frame (`HeavyBody`) stays only to place the first-person rig when holding a heavy item or carrying a player, so a held boulder stays low whatever the look pitch. It never feeds hand targets. |
| Job ordering fallback | If Mecanim foot and look-at IK turn out to run after the job: head-only look-at, and the pelvis drop is passed to the job to offset world-space targets. Body-relative targets need no offset, since they move with the body. |
| Item placement | Heavy items are placed after the graph evaluates, from the same frame the job used. |
| Elbow | The bend plane comes from the incoming animated elbow. The fixed body-space hint is deleted. |
| Hand targets | `AvatarHandTargets` stays the only input to the solver, with the same priority stack. The carry correction pass is deleted. |
| `ItemReleaseReach.TryProject` | Fix its tolerance and its fallback direction. First-person clearance still uses it until spec B. |
| Heavy reach cap | Heavy-item hand targets use `MaximumReach` 0.98, so the soft cap limits the arms instead of `FollowReachFraction`. |
| Boulder | Re-authored to an in-reach hold that approximates arms hanging. The starting value is unverified, and the user tunes it with the reach readout. |

## Design

### Arm IK job
Add `AvatarArmIK`, which owns an `AnimationScriptPlayable` and its job data, and `AvatarArmIKJob : IAnimationJob`.

- **Bones:** `Left/RightUpperArm`, `Left/RightLowerArm` and `Left/RightHand`, bound with `animator.BindStreamTransform`. The animator root rotation is passed in as job data.
- **Graph position:** the job is the last node.
  - `AvatarAnimationGraph`: `Fingers.Output` → arm IK playable → `Humanoid` output.
  - `LocalFirstPersonHands`: `fingers.Output` → arm IK playable → `Hands` output.
- **Filling the job data.** Just before each `graph.Evaluate`, fill one entry per hand from `AvatarHandTargets.Resolve`:
  - The wrist target position and rotation, converted from palm to wrist once with `Left/RightWristToPalmPosition/Rotation`, as `AvatarHandIK.Apply` does today.
  - The target space: `World` or `Body`.
  - Position weight and rotation weight.
  - Reach cap: arm length × `min(MaximumReach, 0.98)`.
  - The contact fade weight. The smoothstep over the last 0.08 of reach, smoothed over 0.08 s, moves unchanged from `AvatarHandIK.Apply` into the fill step.
- **Solve**, per hand, inside the job:
  1. Rebuild the body frame from the stream's `UpperArm` positions and the root rotation.
  2. Convert `Body` targets to world with that frame.
  3. Apply soft reach. Past 85% of the cap, the distance approaches the cap exponentially. This is the curve currently in `HeldItemPoseCalculation.Resolve`. Move it to a shared static that both use.
  4. Blend from the animated wrist by position weight, and slerp rotation by rotation weight.
  5. Solve analytically with two bones, as in `AvatarHandIK.Correct`. The bend plane comes from the stream's elbow projected off the shoulder → target line. If that is degenerate, use the upper arm's forward axis.
- **Ordering check.** This is the first implementation step. Confirm that the stream the job reads already contains Mecanim foot IK (`animator.bodyPosition` pelvis drop) and look-at (body weight 0.15). Both are applied by `OnAnimatorIK` at the clip playables with `SetApplyPlayableIK(true)`.
  - **If they aren't applied yet:** set the look-at body weight to 0 (head-only look-at). Also pass the pelvis drop to the job, and subtract it from `World`-space wrist targets so the later shift doesn't move the hands off their targets. `Body` targets move with the body and need no offset. Don't move the pelvis drop itself into the job, because foot IK is solved against the lowered hips.

### Hand targets
- `AvatarHandTargets.Set` gets a `bodyRelative` flag. Item-source targets are body-relative. Free, Carry and Contact targets stay in world space.
- `AvatarHandTargets` also stores the body frame (shoulder position and rotation) that the preparer used when it wrote body-relative targets. `PlayerHandPresentation.PrepareRemote` and the first-person prepare path set it next to the targets. At fill time, a body-relative target is converted to local with that stored frame. The job converts it back with the stream frame.
- **Carry:** the carrier reads the partner's `Left/RightCarryGrip` at fill time. The existing `HandDependency` pass order already evaluates the partner first, so the grips are final.

### Body frame
- `HeldItemBodyFrame` loses `WithReference`, `referenceCenter`, `referenceArmLength`, `poseCenter` and `poseArmLength`. `CenterToWorld`/`CenterToLocal` use `Center` and the average arm length.
- `PlayerHandPresentation.PrepareRemote` stops calling `WithReference`. `BodyFor(rig)` returns `rig.BodyFrame`.
- The settings-built frame (`TwoHandHoldPresentation.Body`, `HeavyBody`) no longer feeds hand targets. It stays only where it positions the first-person rig: the `HeavyFrameWeight` blend in `PlayerHandPresentation.Place`, and the `TryBody` fallback when no rig exists.

### Item placement after evaluation
- **One-hand items:** unchanged. `CommitHands` already places them from the evaluated palm (`binding.Palm(true)`).
- **Heavy items:** `PrepareHands` stores `pose.Item` relative to the body frame. `CommitHands` (`HandsEvaluated` for remote avatars, and after `active.Evaluate` locally) rebuilds the frame from the evaluated `UpperArm` bones and places the item from it. The arm IK doesn't move the `UpperArm` bones, so this is the frame the job used.

### Remove the reach projection from the hold path
- `HeavyItemPoseCalculation.Place` places the item at its authored position. `unreachable` comes from `InReach(..., 0.98f)`, and `limited` is false.
- Delete the heavy projection in `HeldItemPresentationState.PrepareCandidate` and in the heavy branch of `Blend`.
- `CommitHands` computes `impossible` with `HeavyItemPoseCalculation.InReach` instead of `TryProject`.
- Heavy-item hand targets pass `MaximumReach` 0.98, so the soft cap limits the arms, not `FollowReachFraction`.
- First-person clearance (`ResolveClearance`, `ItemReleaseClearance.TryResolve`) keeps using `ItemReleaseReach` until spec B.

### `ItemReleaseReach` fixes
- `Contains`: tolerance relative to each radius, `<= r² * (1 + 1e-4)`, instead of adding an absolute `0.000001f`.
- `TryProject(point, forward, out result)`: when `radial` is degenerate, use `ProjectOnPlane(forward, axis)`, and fall back to `ProjectOnPlane(up, axis)` only if that is also degenerate. `TryResolve` passes `body * Vector3.forward`.

### Deletions
- `AvatarHandIK`.
- `AvatarHumanoidIK`: the `hands` field, `CorrectHands`, and the `hands.Apply` call.
- `AvatarInstance.CorrectHands`, `AvatarPresentation.CorrectHands`, and the `CorrectHands` loop in `AvatarPresentationSystem`. The `HandDependency` evaluation passes stay.
- `LocalFirstPersonHands`: `OnAnimatorIK`, `ik`, `deltaTime` and `applied`. The basis clip uses `SetApplyPlayableIK(false)`.
- The fixed elbow hint `(±0.35, -0.45, -0.1)` and its 0.4 weight.

### Boulder
Set `Boulder.asset` `HandPose.HoldPosition` to `(0, -0.7, 0.3)` as a starting point. The user tunes it in the grip authoring scene until the reach readout shows neither hand as unreachable. Other Boulder values are unchanged.

## Out of scope
Arm-pose layers, hold modes, grip and pose authoring, first-person clearance changes, charge pitch, and deleting the pose settings. All of these are in spec B.

## Visual validation (user)
- Hold the Boulder as a remote/observer avatar while idling, walking and sprinting. The hands stay on the boulder with no frame-to-frame flicker and never go behind the avatar.
- Temporarily set the Boulder back to `(0, -1.09, 1.04)`. Both arms extend straight toward it and stop short, eased in with no snap. There is no flicker and nothing flips behind the avatar. Then restore the tuned value.
- One-hand items (rock, potion, basketball) in third person: hold, charge and throw look as before, with no elbow pops. Overhead charge elbows may still look off until spec B.
- Player carry: carrier hands stay on the partner's carry grips while both move, with no lag and no second-pass correction.
- Cart driving: hands stay on the cart handles.
- First person: held items, free-hand bob, fall and landing look as before.
- Sloped ground and looking around: foot IK pelvis drop and spine look-at don't pull hands off held items.
