# Hand IK Stability Plan

Implements `Hand_IK_Stability_Spec.md`. Read the spec first. This plan says where and how each part goes into the code.

## Change from the spec: post-evaluation solve instead of an animation job
The user chose to solve the arms **on the bone Transforms right after `graph.Evaluate`** instead of in an `AnimationScriptPlayable`. This reuses the existing analytic solve in `AvatarHandIK.Correct`, which already runs on Transforms after evaluation.

What follows from that:
- The solve reads this frame's evaluated bones, which already include Mecanim foot IK (pelvis drop) and look-at. **The spec's ordering check and its fallback (head-only look-at, passing the pelvis drop) don't apply.** Look-at body weight stays at 0.15, and `AvatarAnimationGraph` is unchanged.
- There is no job, no job data and no fill step. The spec's fill step and solve steps become one method, `AvatarArmIK.Solve`, called after `graph.Evaluate` and before VRM `runtime.Process()`. `ApplyEditorHeadLook` is an existing post-evaluation bone write in the same place.
- Body-relative targets: the spec converts to local at fill time and back to world in the job. Here it is one rebase, from the frame stored with the targets to the evaluated frame.
- Writes go straight to the Transforms, so humanoid muscle limits never clamp them.
- Where spec B says "the arm IK job", read it as `AvatarArmIK.Solve`.

Everything else follows the spec.

## Shared definitions

### Body frame pose: `AvatarBinding.Body`
`Assets/Game/Runtime/Avatars/AvatarPresentation.cs`, class `AvatarBinding` (next to `Palm`, line 48):
```csharp
internal Pose Body => new((GetBone(HumanBodyBones.LeftUpperArm).position + GetBone(HumanBodyBones.RightUpperArm).position) * 0.5f,
    Animator.transform.rotation);
```
This is the spec's single body frame: the midpoint of the `UpperArm` bones plus the animator root rotation. It matches `HeldItemBodyFrame.Center` and `.Rotation` for frames built from those bones (`PrepareRemote`, `LocalFirstPersonHands.BodyFrame`, `GripObserverPreview.Prepare`). The arm solve never moves the `UpperArm` bones, so reading `Body` after the solve returns the frame the solve used.

### `AvatarHandTargets` (`Assets/Game/Runtime/Avatars/AvatarHandTargets.cs`)
- `Target` gets `internal bool BodyRelative;`.
- `Set(...)` gets a trailing `bool bodyRelative = false` parameter, stored in `BodyRelative`.
- Add the frame the preparer used for body-relative targets:
  ```csharp
  internal Pose Body { get; private set; } = Pose.identity;
  internal void SetBody(Pose body) => Body = body;
  ```
- Add the shared rebase helper, used by the solver and by heavy item placement:
  ```csharp
  internal static Pose Rebase(Pose pose, Pose from, Pose to)
  {
      Quaternion rotation = to.rotation * Quaternion.Inverse(from.rotation);
      return new Pose(to.position + rotation * (pose.position - from.position), rotation * pose.rotation);
  }
  ```

### Soft reach: `AvatarArmIK.SoftReach`
This moves the curve out of `HeldItemPoseCalculation.Resolve` (`HeldItemPose.cs:260-264`) into a static on the new solver:
```csharp
internal static float SoftReach(float distance, float reach)
{
    float start = reach * 0.85f;
    if (distance <= start) return distance;
    float range = reach - start;
    return start + range * (1f - Mathf.Exp(-(distance - start) / range));
}
```
`Resolve` then becomes:
```csharp
if (soften && distance > reach * 0.85f) delta *= AvatarArmIK.SoftReach(distance, reach) / distance;
else delta = Vector3.ClampMagnitude(delta, reach);
```
Delete the `softStart` and `softRange` locals.

## Step 1: `AvatarArmIK` replaces `AvatarHandIK`
Delete `Assets/Game/Runtime/Avatars/AvatarHandIK.cs` and its `.meta`. Create `Assets/Game/Runtime/Avatars/AvatarArmIK.cs`:

```csharp
internal sealed class AvatarArmIK
{
    private sealed class Arm { internal Transform Upper, Lower, Hand; internal float Weight; internal bool Seeded; }
    private readonly AvatarBinding binding;
    private readonly Arm left, right;

    internal AvatarArmIK(AvatarBinding binding) { /* bind Left/Right UpperArm, LowerArm, Hand via binding.GetBone */ }

    internal void Solve(AvatarHandTargets targets, float dt)
    {
        Pose body = binding.Body;
        Solve(left, false, targets.Resolve(AvatarIKGoal.LeftHand), targets.Body, body, dt);
        Solve(right, true, targets.Resolve(AvatarIKGoal.RightHand), targets.Body, body, dt);
    }

    internal static float SoftReach(float distance, float reach) { ... }
}
```

The per-arm `Solve(arm, rightHand, target, prepared, body, dt)` runs these steps in order:
1. If `!target.Transform`, return. Leave `Weight` and `Seeded` untouched, as `AvatarHandIK.Apply` does today.
2. Read `binding.Measurements` and `binding.Scale` on every call, not cached, so `RefreshMeasurements` needs no rebuild. Arm length is `(Arm.x + Arm.y) * Scale` for that side.
3. Palm pose is `target.Transform`'s position and rotation. If `target.BodyRelative`, set `palm = AvatarHandTargets.Rebase(palm, prepared, body)`.
4. Palm to wrist, using the `AvatarHandIK.Apply` math: `wrist = palm.rotation * Inverse(WristToPalmRotation)`, `root = arm.Upper.position`, `delta = palm.position - wrist * (WristToPalmPosition * Scale) - root`.
5. Reach fraction: `target.MaximumReach > 0f ? Mathf.Min(target.MaximumReach, 0.98f) : 0.98f`.
6. Contact fade, copied unchanged from `AvatarHandIK.Apply` lines 97-100: the smoothstep over the last 0.08 of reach for `Contact` targets, then `Lerp` with `AvatarPresentation.Smooth(dt, 0.08f)` once `Seeded`. Then set `Seeded = true`.
7. Soft reach: `cap = length * reach`. If `delta.magnitude > cap * 0.85f`, `delta *= SoftReach(distance, cap) / distance`.
8. `positionWeight = target.Position * arm.Weight` and `rotationWeight = target.Rotation * arm.Weight`. If both are ≤ 0, return.
9. `end = Vector3.Lerp(arm.Hand.position, root + delta, positionWeight)`. Compute `rotation = Quaternion.Slerp(arm.Hand.rotation, wrist, rotationWeight)` **before** any bone is rotated.
10. Two-bone solve, copied from `AvatarHandIK.Correct` lines 64-77 (`Shoulder`/`Elbow`/`Wrist` → `Upper`/`Lower`/`Hand`). The bend plane comes from the evaluated elbow projected off the `root → end` line, falling back to `ProjectOnPlane(arm.Upper.forward, direction)` when degenerate. Rotate `Upper` first, then `Lower`, re-reading child positions after each write, as `Correct` does.
11. `arm.Hand.rotation = rotation`.

This removes the fixed elbow hint `(±0.35, -0.45, -0.1)` and its 0.4 weight, the goal calibration (`GoalOffset`, `BoneToGoal`), `TargetWrist`/`TargetRotation` and `HadCarryTarget`.

## Step 2: Wire the solver in and remove Mecanim hand IK

### Third-person avatars
- `AvatarInstance.cs`:
  - Add a field `private AvatarArmIK arms;`. Create it in `Initialize` next to `ik = new AvatarHumanoidIK(host, Binding);` (line 89). `RefreshMeasurements` needs no change.
  - In `Evaluate`, right after the `try { graph.Evaluate(host.State); } finally { ... }` block (line 131) and before `ApplyEditorHeadLook` (line 133), add:
    ```csharp
    using (AvatarPresentationSystem.IkMarker.Auto()) arms.Solve(host.HandTargets, dt);
    ```
    This runs before `runtime.Process()`, so VRM constraints and look-at see the solved arms.
  - Delete `CorrectHands()` (lines 150-153). `OnAnimatorIK` stays for foot IK and look-at.
- `AvatarHumanoidIK.cs`: delete the `hands` field (line 19), `hands = new AvatarHandIK(binding);` (line 33), `CorrectHands()` (line 46) and `hands.Apply(host.HandTargets, DeltaTime);` (line 84).
- `AvatarPresentation.cs`: delete `CorrectHands()` (lines 298-312).
- `AvatarPresentationSystem.cs`: delete the last `foreach` loop that calls `host.CorrectHands()` (lines 96-101). The two-pass `HandDependency` evaluation order (lines 74-80) stays.

### First-person rig (`LocalFirstPersonHands.cs`)
- Replace `private AvatarHandIK ik;` with `private AvatarArmIK arms;`. Create it at the end of `Initialize`, replacing line 51.
- `RefreshMeasurements`: delete `ik = new AvatarHandIK(Binding);` (line 57).
- Basis clip (line 44): `SetApplyPlayableIK(false)`.
- `Evaluate`: remove `deltaTime = dt; applied = false; evaluating = true;` and the try/finally. It becomes `graph.Evaluate(0f); arms.Solve(targets, dt);`.
- Delete `OnAnimatorIK`, plus the `deltaTime`, `evaluating` and `applied` fields (they're orphaned).

### Player carry
No code change beyond the deletions. `PrepareCarry` writes `carryTargets` from the partner's `Left/RightCarryGrip` inside the carrier's own `PreparingHands`. The `HandDependency` pass order runs that after the partner's evaluation, and `Solve` reads those Transforms directly after the carrier's `graph.Evaluate`, so the grips are final. Carry targets stay world-space.

## Step 3: Body frame cleanup and storing the prepare frame

### `HeldItemBodyFrame` (`Assets/Game/Runtime/Items/HeldItemPose.cs:138-185`)
- Delete the `poseCenter` and `poseArmLength` fields, the `referenceCenter` and `referenceArmLength` constructor parameters and their assignments (in both constructors), and `WithReference`.
- The first constructor becomes `(Vector3 shoulder, Quaternion rotation, GeneratedSkeleton measurements, float scale, Vector3? leftShoulder = null)`. Callers that pass `leftShoulder:` by name compile unchanged.
- `CenterToWorld`/`CenterToLocal` use `Center` and `(ArmLength + LeftArmLength) * 0.5f`.
- `WithMeasurements` becomes `new(Center + Rotation * ((measurements.RightShoulder - measurements.LeftShoulder) * (scale * 0.5f)), Rotation, measurements, scale)`.
- `GripAuthoringScene.cs:260-261` (`body.CenterToWorld`) needs no edit. It now matches the frame `Place` uses.

### `PlayerHandPresentation.cs`
- `PrepareRemote` (lines 361-372): delete the `WithReference` line (368). Before the `held.Prepare*` calls, add `avatar.HandTargets.SetBody(binding.Body);`.
- Delete `BodyFor` (lines 400-401) and use `rig.BodyFrame` directly: `TryBody` line 382 (`active.BodyFrame`), and the pending-rig block lines 433-434 (`candidate.BodyFrame`).
- First-person prepare path:
  - Pending-rig block: after `Place(candidate, 0f);` (line 431), add `avatar.HandTargets.SetBody(candidate.Binding.Body);`.
  - Main path: after `if (active) { Place(active, dt); FreeHands(active, dt); }` (line 454), add `if (active) avatar.HandTargets.SetBody(active.Binding.Body);`.
- `HeavyBody` and `HeavyFrameWeight` stay only in `Place` (lines 480-486) and the no-rig branch of `TryBody` (lines 390-396).

### `GripObserverPreview.cs` (authoring-scene observer, also a preparer)
- `Prepare`: delete the `WithReference` line (68). Before the `State.Prepare*` calls, add `presentation.HandTargets.SetBody(binding.Body);`.

## Step 4: Body-relative item targets
Every Item-source `Set` passes `bodyRelative: true`:
- `HeldItemPresentationState.PrepareLeft`: the heavy branch `Set` (line 395) and the slingshot `Set` (line 408).
- `HeldItemPresentationState.SetTarget`: the one-hand right-hand `Set` (line 801).
- `TwoHandHoldPresentation.Submit` (`TwoHandHoldPresentation.cs:117-126`): pass `bodyRelative: source == AvatarHandSource.Item` on both `Set` calls. Carry calls stay world-space.

Free, Contact and Carry targets keep the default `false`.

## Step 5: Heavy items: no reach projection, placement after evaluation

### `HeavyItemPoseCalculation` (`HeldItemPose.cs:187-231`)
- Add `internal const float MaximumReach = 0.98f;`.
- `Place` places the item at its authored position with no projection:
  ```csharp
  Quaternion orientation = body.Rotation * rotation * data.PrefabRotation;
  Pose root = new(body.CenterToWorld(center) - orientation * data.Sphere.Center, orientation);
  var pose = FromItem(root, body, data);
  return new HeldItemPose(root, pose.LeftPalm, new Pose(pose.FollowPosition, pose.FollowRotation), body,
      unreachable: !InReach(pose, body, MaximumReach));
  ```
- `Reach(...)` stays, because first-person clearance (`ResolveClearance`) still uses it.
- The heavy `HeldItemPose` constructor (lines 89-97): remove the `limited` parameter (no caller passes it after this change) and set `ReachLimited = false`.

### `HeldItemPresentationState.cs`
- `PrepareCandidate` heavy branch (lines 465-471) becomes `sample = HeldItemPoseCalculation.FromItem(pose.Item, body, data);`.
- `Blend` heavy branch (lines 740-753): delete `reach`, `reachable`, `limited` and the `TryProject` line. Build the pose from `frame` with `requestedRight: destination.RequestedRight, requestedLeft: destination.RequestedLeft, unreachable: destination.Unreachable`.
- Heavy reach cap 0.98:
  - `PrepareLeft` heavy branch (line 396): pass `HeavyItemPoseCalculation.MaximumReach` instead of `effectiveReach`.
  - `SetTarget` heavy branch (line 798): pass `HeavyItemPoseCalculation.MaximumReach` to `Submit` instead of `reach`.
  - Follow-through stays limited by `TwoHandHoldPresentation.Sample`'s `FollowReachFraction` check.
- `CommitHands` (lines 491-532):
  - Heavy item placement from the evaluated frame. Replace the `itemPose` line with:
    ```csharp
    Pose itemPose = !grip.Heavy ? HeldItemPoseCalculation.ItemFromPalm(palm, grip.RightContact, grip.PrefabScale) :
        binding != null ? AvatarHandTargets.Rebase(pose.Item, new Pose(lastBody.Center, lastBody.Rotation), binding.Body) : pose.Item;
    ```
    `lastBody` is the prepared frame (the same bones and rotation passed to `SetBody`), and `binding.Body` is rebuilt from the evaluated `UpperArm` bones, so this is the frame the solve used. `fallback`, `CommitItem` and `committed.ItemPose` all take this pose.
  - `impossible = pose.Heavy && !HeavyItemPoseCalculation.InReach(pose, lastBody, HeavyItemPoseCalculation.MaximumReach);`
  - Readout reach for heavy poses: `RightUnreachable` uses `InHandReach(pose.RequestedRight, true, pose.Heavy ? HeavyItemPoseCalculation.MaximumReach : effectiveReach)`. `LeftUnreachable` uses `pulling ? 0.85f : pose.Heavy ? HeavyItemPoseCalculation.MaximumReach : effectiveReach`.
- Unchanged until spec B: `ResolveClearance`, `ApplyClearancePose`, `CorrectCommittedPose`, `PlayerHandPresentation.CommitCorrection`, and the second `active.Evaluate` in `PlayerHandPresentation.Evaluate`.

## Step 6: `ItemReleaseReach` fixes (`Assets/Game/Runtime/Items/ItemReleaseClearance.cs`)
- `Contains` (lines 21-23): `(point - first).sqrMagnitude <= firstRadius * firstRadius * (1f + 1e-4f)`, and the same for `second`.
- `TryProject(Vector3 point, Vector3 forward, out Vector3 result)`: when `radial` is degenerate, use `Vector3.ProjectOnPlane(forward, axis)`. If that is also degenerate, use `Vector3.ProjectOnPlane(Vector3.up, axis)`.
- `TryResolve` passes its existing `forward` (`body * Vector3.forward`, line 107) at both call sites (lines 122 and 147). After step 5, these are the only `TryProject` callers.

## Step 7: Boulder
`Assets/Game/ScriptableObjects/Items/Boulder.asset` line 41: set `HandPose.HoldPosition` to `{x: 0, y: -0.7, z: 0.3}`. Change nothing else. `OverrideFirstPersonPose` is 0 and the item is Heavy, so first person also uses this value.

## Pre-existing dead code (mention, don't delete)
- `AvatarPresentation.SetHandTarget` / `ClearHandTarget`: no callers. `SetHandTarget` writes a world-space Item target.
- `PlayerHeldItemPresentation.HeavyBody`: no callers.

## Visual validation (user)
- Hold the Boulder as a remote/observer avatar while idling, walking and sprinting. The hands stay on the boulder with no frame-to-frame flicker and never go behind the avatar. In the grip authoring scene, tune `HandPose.HoldPosition` until the reach readout shows neither hand as unreachable.
- Temporarily set the Boulder back to `(0, -1.09, 1.04)`. Both arms extend straight toward it and stop short, eased in with no snap. There is no flicker and nothing flips behind the avatar. Then restore the tuned value.
- First-person Boulder: it sits at the new hold height and the hands stay on it.
- One-hand items (rock, potion, basketball) in third person: hold, charge and throw look as before, with no elbow pops. Overhead charge elbows may still look off until spec B.
- Player carry: the carrier's hands stay on the partner's carry grips while both move, with no lag and no second-pass correction.
- Cart driving: hands stay on the cart handles.
- First person: held items, free-hand bob, fall and landing look as before.
- Sloped ground and looking around: foot IK pelvis drop and spine look-at don't pull hands off held items.
