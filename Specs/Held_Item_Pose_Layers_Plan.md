# Held Item Pose Layers Plan

Implements `Held_Item_Pose_Layers_Spec.md`. Read the spec first. This plan says where and how each part goes into the code.

Spec A (`Hand_IK_Stability_Spec.md`) was built as a **post-evaluation solve on bone Transforms**, not as an animation job: `AvatarArmIK.Solve` runs right after `graph.Evaluate` in `AvatarInstance.Evaluate` and `LocalFirstPersonHands.Evaluate`. Wherever the spec says "the arm IK job", read it as `AvatarArmIK.Solve`. The spec's "job output buffer" becomes `AvatarBinding.AnchoredItem`.

Steps 1–9 are interdependent, so the project compiles only after all of them are done. Steps 10–13 (authoring) build on them.

## Decisions made while planning
| Topic | Decision | Why |
|---|---|---|
| Mode transitions (user) | `AvatarArmLayers` holds all three modes' Hold/Charged clips per arm and takes per-mode weights plus one charge weight. This replaces the spec's `Select(HoldModePoses, firstPerson)` + `Set(holdWeight, chargeWeight)`. | Switching rock ↔ boulder ↔ slingshot, and a throw whose recovery ends on a different mode, cross-fade directly instead of popping or dipping through the base pose. |
| Follow-through (user) | `TwoHandHoldPresentation` becomes the shared `HandHoldPresentation`, with a one-handed flag. OneHand items, TwoHand items and player carry all use it. | One follow → pause → return implementation. No one-hand logic lives in a class named TwoHand. |
| No avatar bound (user) | A fixed default placement, from private constants, is used only when no binding exists. | Keeps held items visible for loading or failed avatars, as today. |
| `HoldModePoses` vs `HeldItemPoseSettings` | `HoldModePoses` replaces `HeldItemPoseSettings` entirely. | The spec deletes only the spatial fields of `HeldItemPoseSettings`, but its remaining timing fields are exactly `HoldModePoses`' timings. |
| Fingers without IK | Holding hands get an Item-source target with position/rotation weight 0 that carries only the finger clip. | Finger clips flow through the resolved hand target today. This target also masks the first-person `Free` target, which is the spec's "FreeHands only for an empty hand". |
| Slingshot charge | Progress stays on `ThrowChargeTime` (today's rule). `HoldModePoses.Slingshot.ChargePoseDuration` is unused. | The draw must match gameplay charge strength and the networked release progress. |
| Charge easing | `LeanTween.easeOutSine` on progress for every mode. | Mixer weights can't overshoot, so the old `easeOutBack` doesn't fit. Tune in code if needed. |
| Missing Charged clip | The mode's Hold clip is used for the whole charge. A missing Hold clip removes the mode from that arm, and the arm falls back to the base animation. | The spec only says "a mode whose clip is unassigned". |
| Slingshot pouch weight | The pouch moves from rest to the left palm by the Slingshot mode weight, and only when that view's Slingshot Hold clip exists. | Without a clip, the left arm hangs and the pouch would stretch to the hip. Equip also eases the pouch into the hand. |
| Slingshot pouch in recovery | The pouch recoils from its release point to rest with the existing wave. It then eases back into the left palm between `SettleSeconds` and `RecoverySeconds`. | The spec only covers Hold through full draw. |
| First-person bob offset | "The holding hand's FreeHands offset" is read as the **delta from the rest pose**: the smoothed right free-hand position minus the clamped `RestPosition + FirstPersonReachOffset`, times arm length, times the arm weight. The right hand's offset is used for every mode. | Adding the full rest position would shift the whole rig. With two holding hands, averaging would cancel the opposite-phase bounce. |
| Free hand under bob | The rig bob is subtracted from free-hand targets. | Otherwise the free left hand's bounce cancels against the rig bob (opposite phase) while the right hand holds. |
| Pose editor input | A pose-edit override on `HeldItemPresentationState` (instrumentation only) produces body-relative Item targets. The swivel angle travels on `AvatarHandTargets`. | Reuses the existing Item-target and body-relative machinery. `AvatarArmIK` only gains the swivel. |
| Ghost hands | A hand-only copy of the selected avatar's third-person prefab. Its fingers are posed once with `GripFingers`, the body collapses onto the wrist, and it renders with a translucent material. | It uses the real avatar mesh, needs no mesh processing, and stays readable where it overlaps the real hand. One-hand grips coincide exactly. |
| `SlingshotDefinition` | `NotifyContentChanged` forces `HoldMode = Slingshot`, next to its existing `Stackable`/`MaxStack` guard. | Prevents a slingshot whose arm mode doesn't match its pouch behaviour. |

## Spec issues and inconsistencies
1. **Job vs post-evaluation solve.** The spec is written against an `IAnimationJob` that spec A didn't build. Every job step maps onto `AvatarArmIK.Solve`, and the output buffer becomes `AvatarBinding.AnchoredItem`.
2. **Duplicate timing types.** The spec deletes the "spatial fields" of `HeldItemPoseSettings` and also adds `HoldModePoses` with the same timing fields. This plan deletes `HeldItemPoseSettings`.
3. **`Select`/`Set` API.** Swapping one clip pair pops on mode changes, and the spec doesn't cover recovery ending on a different mode. Replaced by per-mode weights (user decision).
4. **Slingshot charge duration.** The spec says charge weight is progress over `ChargePoseDuration`, but the slingshot's draw is driven by `ThrowChargeTime` (the pebble speed and networked progress depend on it). Kept `ThrowChargeTime`.
5. **Slingshot recovery and cancel.** The spec says nothing about the pouch after release. The recoil and settle behaviour is chosen above. On cancel, the pouch simply follows the palm as the charge weight blends back.
6. **No-binding placement.** Deleting all spatial data leaves nothing to place an item when no avatar or first-person rig is bound. Resolved with constants (user decision).
7. **"Arm-length unit labels".** `FirstPersonReachOffset` and the free-hand poses are still in arm lengths. Only the labels for the removed pose fields are deleted. The avatar-placement header keeps "reach in arm lengths".
8. **First-person bob size.** With the default `FirstPersonHandsSettings`, the rest → fall delta is about 0.65 arm lengths upward, and it now moves the whole rig and the held item. It will probably need a smaller `FallPosition`, or a scale factor, after the user looks at it.
9. **First-person clearance smoothness.** The spec's validation asks for a smooth pull-back. The correction is unsmoothed, as today. `TryResolve` candidates are partly discrete (0.05 / 0.10 / 0.20 m), so the rig can step when the sphere-cast candidate isn't the nearest.
10. **Pebble final check.** It used a reach sphere of radius 0 to mean "the centre itself is clear". With the reach parameter deleted, it becomes "`TryResolve` returns the same position".
11. **Charge pitch compounds with look-at.** Look-at already turns the spine (body weight 0.15). Adding the full `LookPitch × charge` on the arms tilts slightly more than the look pitch.
12. **Per-item overrides lost.** Basketball overrides its first-person pose (`HoldPosition (0.1, 0, 0.78)`), and Slingshot overrides both views and its timings (`ChargePoseDuration 0.15`, `MaximumFollowDuration 0`). They now use shared mode data: Slingshot's timings move into `HeldItemSettings.Slingshot`. If the shared OneHand first-person clip doesn't suit the basketball, it needs its own mode.
13. **TwoHand before its clips exist.** The spec says items ride the base-animation arms until clips are assigned. For TwoHand, that means the boulder is anchored between hanging hands at hip height, with the IK pulling the hands together.
14. **First-person equip pop (existing).** The finger-only target masks the `Free` target immediately, so on equip the first-person hand snaps from its free pose to the Idle basis and then blends into the Hold clip. Today's code has the same snap.
15. **Mirror R→L** assumes the item is symmetric about its local x = 0 plane. The rotation mirror `(x, y, z, w) → (x, −y, −z, w)` matches the generated palm frames (fingers forward, palm normal up). It maps the Boulder's current right contact `(90, 270, 0)` to its current left `(90, 90, 0)`.
16. **Arm mask and shoulders.** The saved clips include the shoulder muscles. This assumes `AvatarMaskBodyPart.LeftArm/RightArm` include the shoulder. If shoulder shrug doesn't play back, that assumption is wrong.

## Step 1: Data model

### `ItemHoldMode` (`Items/ItemDefinition.cs:6`)
`public enum ItemHoldMode { OneHand = 0, TwoHand = 1, Slingshot = 2 }`

### `HoldModePoses` and `HeldItemSettings` (`Items/HeldItemSettings.cs`)
Replace the whole body below `OnValidate` (lines 11–43):
```csharp
public HoldModePoses OneHand = HoldModePoses.Default, TwoHand = HoldModePoses.Default, Slingshot = HoldModePoses.Default;
public HoldModePoses Poses(ItemHoldMode mode) => mode switch
{ ItemHoldMode.TwoHand => TwoHand, ItemHoldMode.Slingshot => Slingshot, _ => OneHand };
```
Add to the same file:
```csharp
[System.Serializable]
public struct HoldModePoses
{
    public AnimationClip ThirdPersonHold, ThirdPersonCharged, FirstPersonHold, FirstPersonCharged;
    [Min(0f), Tooltip("Visual charge duration in seconds. Slingshot follows ThrowChargeTime instead.")] public float ChargePoseDuration;
    [Min(0f)] public float MaximumFollowDuration, EndPosePauseDuration, ReturnBlendDuration;
    [Range(0.01f, 0.85f)] public float FollowReachFraction;
    public static HoldModePoses Default => new()
    { ChargePoseDuration = 0.35f, FollowReachFraction = 0.85f, MaximumFollowDuration = 0.20f, ReturnBlendDuration = 0.20f };
    internal AnimationClip Hold(bool firstPerson) => firstPerson ? FirstPersonHold : ThirdPersonHold;
    internal AnimationClip Charged(bool firstPerson) => firstPerson ? FirstPersonCharged : ThirdPersonCharged;
}
```
Delete `ResolveHold`, `ResolveSpatial`, `ResolveCharge` and `SetOverride`.

### `ItemDefinition` (`Items/ItemDefinition.cs:48-58`)
Under `[Header("Held Item Grip")]`, keep `HoldMode`, `RightPalmContact`, `LeftPalmContact`, and move `GripFingers` up under them. Delete the `[Header("Held Hand Pose")]` line, `OverrideHoldSettings`, `OverrideFirstPersonPose`, `FirstPersonPose` and `HandPose`.

### `SlingshotDefinition` (`Items/SlingshotDefinition.cs`)
- Delete the `SlingshotChargePoseSettings` struct (lines 5–16) and the fields `OverrideFirstPersonChargePose`, `OverrideRemoteChargePose`, `FirstPersonChargePose` and `RemoteChargePose`.
- Rename the header to `[Header("Slingshot Pouch")]` and keep `PullingPalmContact`.
- In `NotifyContentChanged`, add `HoldMode = ItemHoldMode.Slingshot;` next to `Stackable = false;`.

### `AvatarSettings` (`Avatars/AvatarSettings.cs`)
Delete lines 40–41 (the `FirstPersonHoldOffset` tooltip and field) and `!Finite(settings.FirstPersonHoldOffset) ||` at line 138.

### `HeldItemPose.cs`
- **Delete:** `HeldItemPoseSettings`, `HeldItemSpatialSettings`, the `HeldItemPose` struct, `HeavyItemPoseCalculation`, and from `HeldItemPoseCalculation`: `FromItem`, `Resolve`, `Hold`, `SlingshotReach`, `SlingshotCharge`, `ReachInterval` and `Charge`.
- **`HeldItemPoseData` becomes:**
  ```csharp
  internal readonly struct HeldItemPoseData
  {
      internal readonly HoldModePoses Poses;
      internal readonly ItemPalmContact RightContact, LeftContact, PullingContact;
      internal readonly AnimationClip Fingers;
      internal readonly Vector3 PrefabScale;
      internal readonly ItemHoldMode HoldMode;
      internal readonly Quaternion PrefabRotation;
      internal readonly ItemReleaseSphere Sphere;
      internal readonly float ReleaseRadius;
      internal bool TwoHand => HoldMode == ItemHoldMode.TwoHand;
      internal HeldItemPoseData(ItemDefinition definition, HeldItemSettings defaults, WorldItem worldItem = null) { ... }
  }
  ```
  The constructor keeps its geometry lines, sets `Poses = defaults.Poses(definition.HoldMode)` and `Fingers = definition.GripFingers`, and drops the `firstPerson` parameter. Rename every `data.Heavy` / `grip.Heavy` to `TwoHand`. That includes `WorldItem.cs:401`.
- **`HeldItemBodyFrame`:** add the pose overloads that replace `HeavyItemPoseCalculation.ToLocal/ToWorld`:
  ```csharp
  internal Pose CenterToWorld(Pose pose) => new(CenterToWorld(pose.position), Rotation * pose.rotation);
  internal Pose CenterToLocal(Pose pose) => new(CenterToLocal(pose.position), Quaternion.Inverse(Rotation) * pose.rotation);
  ```
- **`HeldItemPoseCalculation`** keeps `PalmFromItem` and `ItemFromPalm`, and adds:
  ```csharp
  private static readonly Vector3 FallbackPalm = new(0.15f, -0.40f, 0.50f), FallbackCenter = new(0f, -0.40f, 0.50f);

  internal static Pose TwoHandAnchor(Pose rightPalm, Pose leftPalm, ItemPalmContact right, ItemPalmContact left, Vector3 scale)
  {
      Quaternion rotation = Quaternion.Slerp(rightPalm.rotation * Quaternion.Inverse(right.Rotation),
          leftPalm.rotation * Quaternion.Inverse(left.Rotation), 0.5f);
      Vector3 rightGrip = Vector3.Scale(scale, right.Position), leftGrip = Vector3.Scale(scale, left.Position);
      Vector3 gripAxis = rotation * (leftGrip - rightGrip), palmAxis = leftPalm.position - rightPalm.position;
      if (gripAxis.sqrMagnitude > 0.000001f && palmAxis.sqrMagnitude > 0.000001f)
          rotation = Quaternion.FromToRotation(gripAxis, palmAxis) * rotation;
      return new Pose((rightPalm.position + leftPalm.position) * 0.5f - rotation * ((rightGrip + leftGrip) * 0.5f), rotation);
  }

  internal static Pose Fallback(in HeldItemBodyFrame body, in HeldItemPoseData data)
  {
      if (!data.TwoHand) return ItemFromPalm(new Pose(body.ToWorld(FallbackPalm), body.Rotation), data.RightContact, data.PrefabScale);
      Quaternion rotation = body.Rotation * data.PrefabRotation;
      return new Pose(body.CenterToWorld(FallbackCenter) - rotation * data.Sphere.Center, rotation);
  }
  ```
  `TwoHandAnchor` is the spec's anchor: roll comes from the average of the item rotations that each palm implies through its grip, then the grip axis is swung onto the palm axis. `Fallback` uses the old default hold offsets and is used only when no binding exists.
- **`WorldItemRegistry.GetHeldPose`** (`WorldItemRegistry.cs:91-92`): drop its `firstPerson` parameter to match the constructor. It has no callers (pre-existing dead code).

### `GripReachReadout` (`Player/GripReachReadout.cs`)
Delete `ReachLimited`.

### `HeldItemPresentationInput` (`Player/HeldItemPresentationInput.cs:12`)
Delete `Camera`, which only the removed slingshot charge used. Also delete its assignment in `PlayerHeldItemPresentation.CaptureInput` (line 138).

## Step 2: `HandHoldPresentation` (shared follow-through)
Delete `Avatars/TwoHandHoldPresentation.cs` and its `.meta`. Create `Avatars/HandHoldPresentation.cs` with the same content, renamed `HandHoldPresentation`, and these changes:
- `Reach(in HoldModePoses poses)`, `ReturnProgress(in HoldModePoses …)`, `Frame(in HoldModePoses …)`, and `Sample(… in HoldModePoses settings …)`.
- **Delete `Body(...)`.** Its body moves into `PlayerHandPresentation` as a private `HeavyBody(AvatarSettings settings)`, using `owner` (see step 7).
- Add `private bool twoHanded;`. `BeginRelease(Pose left, Pose right, in HeldItemBodyFrame body, bool twoHanded = true)` stores it and sets `LeftWeight = startLeftWeight = twoHanded ? 1f : 0f` after `Hold(left, right)`.
- In `Sample`, `Following` requires the left reach check and the two left linecasts only when `twoHanded`.
- Make `Sample`'s destinations `Pose? destinationLeft, Pose? destinationRight`. In the Return stage, blend only when a destination has a value. Otherwise keep the retained pose. Carry keeps passing values, and held items pass `null`.
- `HeavyItemPoseCalculation.ToLocal/ToWorld(pose, body)` becomes `body.CenterToLocal/CenterToWorld(pose)`, and `HeavyItemPoseCalculation.Blend` becomes a private static `Blend` in this class.
- `Submit(..., float reach, AnimationClip fingers, bool bodyRelative)` takes `bodyRelative` explicitly instead of deriving it from the source.
- `Reset()` also sets `returnStart = -1d`, so `ReturnProgress` is correct when a recovery starts without palms.

## Step 3: Arm pose layers

### `AvatarArmLayers` (new, `Avatars/AvatarArmLayers.cs`)
Modelled on `AvatarFingerLayers`. Each arm is one masked layer over a 6-input `AnimationMixerPlayable`: for each mode, input `mode*2` is Hold and `mode*2+1` is Charged. Mixers don't normalise, so the layer weight carries "how much pose", and the inputs inside are normalised shares.
```csharp
internal struct AvatarArmPose
{
    internal HeldItemSettings Settings;
    internal float OneHand, TwoHand, Slingshot, Charge, Pitch;
    internal ItemHoldMode ChargeMode;
    internal float Weight(ItemHoldMode mode) => mode switch
    { ItemHoldMode.TwoHand => TwoHand, ItemHoldMode.Slingshot => Slingshot, _ => OneHand };
}

internal sealed class AvatarArmLayers : IDisposable
{
    private sealed class Arm
    {
        internal AvatarMask Mask;
        internal AnimationMixerPlayable Mixer;
        internal readonly AnimationClip[] Clips = new AnimationClip[6];
        internal readonly AnimationClipPlayable[] Nodes = new AnimationClipPlayable[6];
    }
    private readonly PlayableGraph graph;
    private readonly bool firstPerson;
    private readonly Arm[] arms = new Arm[2];
    internal AnimationLayerMixerPlayable Output { get; }

    internal AvatarArmLayers(PlayableGraph graph, Playable basis, bool firstPerson)
    {
        // As AvatarFingerLayers: Output = 3-input layer mixer, basis on input 0 at weight 1.
        // Arm i (0 left, 1 right): mask with only LeftArm/RightArm active, 6-input mixer on Output input i + 1,
        // SetLayerMaskFromAvatarMask, input weight 0.
    }

    internal void Set(in AvatarArmPose pose)
    {
        for (int i = 0; i < 2; i++)
        {
            var arm = arms[i];
            float total = 0f;
            for (int mode = 0; mode < 3; mode++)
            {
                bool used = i == 1 || mode != (int)ItemHoldMode.OneHand;
                var poses = pose.Settings ? pose.Settings.Poses((ItemHoldMode)mode) : default;
                Bind(arm, mode * 2, used ? poses.Hold(firstPerson) : null);
                Bind(arm, mode * 2 + 1, used ? poses.Charged(firstPerson) : null);
                if (arm.Clips[mode * 2]) total += pose.Weight((ItemHoldMode)mode);
            }
            Output.SetInputWeight(i + 1, Mathf.Clamp01(total));
            for (int mode = 0; mode < 3; mode++)
            {
                float share = total > 0f && arm.Clips[mode * 2] ? pose.Weight((ItemHoldMode)mode) / total : 0f;
                float charge = arm.Clips[mode * 2 + 1] && (ItemHoldMode)mode == pose.ChargeMode ? Mathf.Clamp01(pose.Charge) : 0f;
                arm.Mixer.SetInputWeight(mode * 2, share * (1f - charge));
                arm.Mixer.SetInputWeight(mode * 2 + 1, share * charge);
            }
        }
    }

    private void Bind(Arm arm, int slot, AnimationClip clip)
    {
        if (arm.Clips[slot] == clip) return;
        if (arm.Nodes[slot].IsValid()) { arm.Mixer.DisconnectInput(slot); graph.DestroyPlayable(arm.Nodes[slot]); }
        arm.Clips[slot] = clip; arm.Nodes[slot] = default;
        if (!clip) return;
        var node = arm.Nodes[slot] = AnimationClipPlayable.Create(graph, clip);
        node.SetApplyPlayableIK(false); node.SetApplyFootIK(false); node.SetSpeed(0); node.SetTime(0);
        graph.Connect(node, 0, arm.Mixer, slot);
    }

    public void Dispose() { foreach (var arm in arms) if (arm?.Mask) UnityEngine.Object.Destroy(arm.Mask); }
}
```
OneHand never weights the left layer. Missing clips follow the decisions table.

### Graph wiring
- **`AvatarAnimationGraph`** (`Avatars/AvatarAnimationGraph.cs`): add `internal AvatarArmLayers Arms { get; private set; }` next to `Fingers` (line 115). Line 144 becomes `Arms = new AvatarArmLayers(graph, states, false); Fingers = new AvatarFingerLayers(graph, Arms.Output);`. `Dispose` (line 186) also calls `Arms?.Dispose()`.
- **`AvatarInstance.Evaluate`** (`Avatars/AvatarInstance.cs:126`): before the `graph.Fingers.Select` calls, add `graph.Arms.Set(host.HandTargets.Arms);`.
- **`LocalFirstPersonHands`**:
  - Add a field `private AvatarArmLayers poses;`. At line 43, `poses = new AvatarArmLayers(graph, basis, true); fingers = new AvatarFingerLayers(graph, poses.Output);`.
  - In `Evaluate` (line 63), call `poses.Set(targets.Arms);` before the finger selects.
  - In `OnDestroy`, add `poses?.Dispose();`.

## Step 4: Solver inputs and `AvatarArmIK`

### `AvatarHandTargets` (`Avatars/AvatarHandTargets.cs`)
```csharp
internal struct TwoHandAnchor { internal bool Active; internal ItemPalmContact Right, Left; internal Vector3 Scale; }
internal AvatarArmPose Arms { get; private set; }
internal TwoHandAnchor Anchor { get; private set; }
internal void SetArms(in AvatarArmPose pose) => Arms = pose;
internal void SetAnchor(ItemPalmContact right, ItemPalmContact left, Vector3 scale) =>
    Anchor = new TwoHandAnchor { Active = true, Right = right, Left = left, Scale = scale };
internal void ClearAnchor() => Anchor = default;
#if UNITY_INCLUDE_INSTRUMENTATION
internal readonly float[] Swivel = new float[2];
#endif
```

### `AvatarBinding` (`Avatars/AvatarPresentation.cs`, after `Body` at line 55)
`internal Pose? AnchoredItem { get; set; }`: the TwoHand item pose the solver anchored this frame, or `null`.

### `AvatarArmIK` (`Avatars/AvatarArmIK.cs`)
`Solve(targets, dt)` (lines 28–33) runs the spec's steps in order:
```csharp
Pose body = binding.Body;
var pose = targets.Arms;
if (pose.Pitch != 0f)
{
    Quaternion pitch = Quaternion.AngleAxis(pose.Pitch, body.rotation * Vector3.right);
    right.Upper.rotation = pitch * right.Upper.rotation;
    if (pose.ChargeMode != ItemHoldMode.OneHand) left.Upper.rotation = pitch * left.Upper.rotation;
}
binding.AnchoredItem = null;
Pose? leftGrip = null, rightGrip = null;
var anchor = targets.Anchor;
if (anchor.Active)
{
    Pose item = HeldItemPoseCalculation.TwoHandAnchor(binding.Palm(true), binding.Palm(false), anchor.Right, anchor.Left, anchor.Scale);
    binding.AnchoredItem = item;
    rightGrip = HeldItemPoseCalculation.PalmFromItem(item, anchor.Right, anchor.Scale);
    leftGrip = HeldItemPoseCalculation.PalmFromItem(item, anchor.Left, anchor.Scale);
}
Solve(left, false, targets.Resolve(AvatarIKGoal.LeftHand), leftGrip, targets.Body, body, Swivel(targets, 0), dt);
Solve(right, true, targets.Resolve(AvatarIKGoal.RightHand), rightGrip, targets.Body, body, Swivel(targets, 1), dt);
```
- `Swivel(targets, i)` returns `targets.Swivel[i]` under `UNITY_INCLUDE_INSTRUMENTATION`, and `0f` otherwise.
- **Per-arm `Solve`** (lines 43–80):
  - Takes `Pose? grip` and `float swivel`.
  - Line 49 becomes: if `grip.HasValue && target.Source == AvatarHandSource.Item`, `palm = grip.Value` (world, no rebase). Otherwise use the Transform pose and the existing body-relative rebase.
  - After the degenerate-bend fallback (line 72), add `if (swivel != 0f) bend = Quaternion.AngleAxis(swivel, direction) * bend;`.
- `Pitch` arrives already multiplied by the charge weight and clamped (step 5). First person and pose editing send 0.
- The anchor's grip targets pass `MaximumReach` 0.98 (the `Set` default), which is the spec's TwoHand reach cap.

## Step 5: `HeldItemPresentationState` (`Player/HeldItemPresentationState.cs`)
This is a rewrite. The state reduces to action/network timing → per-mode arm weights, charge weight, hand targets, and the post-evaluation commit.

### Keep unchanged
- Input, selection and action tracking: `SetInput`, `CurrentAge`, `FindProjectile`, `ChargeProgress`, `IsPendingRelease`, `MatchesPendingRelease` and `TryPebbleDeparture`.
- The `ReleaseSample`/`committed` contract and `CommitItem`.
- `CanShowHeldItem`, `ReadyForUse`, `Body`/`HeavyBody`/`TryBody`, `InHandReach`, `Grip`, `ReleaseSubmitted`, and the target/leftTarget/fallback Transforms.
- In `TryBody`, `bool bound = followBone && boundSettings;` becomes `boundBinding != null && boundSettings`.

### Delete
- **Fields:** the private `RecoveryStage` enum (use `HandHoldPresentation.RecoveryStage`), `twoHands` (replaced by `hands`), `pose`, `preparedPose`, `correctedItem`, `followBone`, `pullNeedsCorrection`, `chargeStart`, `followStart`, `retainedPosition`, `returnStart`, `chargeRotation`, `retainedRotation`, `returnRotation`, `retainedItem`, `followItem`, `returnItem`, `returnLeft`, `leftWeight`, `returnLeftWeight`, `returnFrameWeight`, `returnHeavy`, `returnSegmentStart`, `weight`, `returnWeight`, `effectiveReach`, `returnReach`, `stage`, `hasPose`, `recoveryNeedsPose` and `chargeFromBlend`.
- **Members:** `Pose` (property), `CorrectItem`, `CaptureTwoHands`, `PrepareCandidate`, `PrepareLeft`, `ClearLeftTarget`, `ChargePose`, `RefreshPose`, `Recover`, `RecoverHeavy`, `Blend`, `SetTarget`, `ResolveClearance`, `ApplyClearancePose` and `CorrectCommittedPose`.

### New fields
```csharp
private readonly HandHoldPresentation hands = new();
private readonly float[] weights = new float[3], startWeights = new float[3];
private float charge, startCharge, releaseCharge;
private ItemHoldMode chargeMode;
private Pose preparedLeft, preparedRight;
internal float HeavyFrameWeight => running && input.CanEquip ? weights[(int)ItemHoldMode.TwoHand] : 0f;
internal float ArmWeight => running && input.CanEquip ? weights[0] + weights[1] + weights[2] : 0f;
private static float Ease(float progress) => LeanTween.easeOutSine(0f, 1f, Mathf.Clamp01(progress));
```
`HeavyFrameWeight` replaces the old timer-based getter. It follows the TwoHand weight, so the first-person rig blends to `HeavyBody` with the TwoHand arms.

### Members to rewrite
- **`RecoveryFinished`:** read `actionData.Poses` instead of `actionData.Settings`.
- **`Blending`:** `blending || action.State == ItemActionState.Recovering`.
- **`Advance()`:** `CurrentAge(); RefreshArms(); advancedFrame = Time.frameCount;`. `PrepareHands` calls `Advance()` when `advancedFrame != Time.frameCount`. `PlayerHeldItemPresentation.LateUpdate` (order 175) advances before `PlayerHandPresentation.Place` (210) reads `HeavyFrameWeight`/`ArmWeight`.
- **`SetInput` reset branch:** `blending = tracking = false; Array.Clear(weights, 0, 3); charge = 0f; hands.Reset(); ClearTargets();`.
- **`Bind(binding)`:** sets `boundBinding`, `boundSettings` and `committed = default`, and nothing else. Under instrumentation, also `if (Edit != null) Edit.Seeded = false;`.
- **`Attachment`:** for a shown TwoHand selection, place `fallback` at `TwoHandAnchor(boundBinding.Palm(true), boundBinding.Palm(false), …)` when bound. Otherwise use `Fallback(body, selectedData)` from `TryBody`. Return `fallback`.
- **`StageRelease()`:** `prepared = true; preparedId = committed.Item; preparedLeft = committed.LeftPalm; preparedRight = committed.Palm;`.
- **`SelectionChanged`:** keep the guard. If the state is Recovering and not `SlingshotRecovery`, and `hands.Stage == Return`, call `BeginReturn()`. Otherwise call `BeginBlend(definition ? selectedData.Poses.ReturnBlendDuration : blendDuration)`.
- **`RefreshContent`:** rebuild the data, `committed = default; prepared = false;`, and call `BeginReturn()` if it's in the Return stage.
- **`ActionChanged`** keeps its guard and header lines, reading `returnDuration` from `actionData.Poses`. Then, by the new state:
  - **Charging:** `chargeMode = actionData.HoldMode;`. The weights keep blending, and `RefreshArms` computes the charge.
  - **`SlingshotRecovery`:** `submitted = !input.FirstPerson; prepared = blending = false; chargeMode = ItemHoldMode.Slingshot; releaseCharge = charge = Ease(action.ReleaseArcProgress / 255f); hands.Reset();`.
  - **Recovering:**
    ```csharp
    submitted = !input.FirstPerson; blending = false; chargeMode = actionData.HoldMode;
    releaseCharge = charge = Ease(action.ReleaseArcProgress / 255f);
    StartFollow();
    for (int i = 0; i < 3; i++) startWeights[i] = i == (int)chargeMode ? 1f : 0f;
    startCharge = charge; prepared = false; FindProjectile();
    ```
    During follow and pause, the arm layer holds the action mode's pose at the release charge.
  - **Idle:** `prepared = false;`. If the previous state was Recovering, `blending = false; hands.Reset(); charge = 0f;`. Otherwise `BeginBlend(returnDuration)`, which blends a cancelled charge back to Hold.
- **New helpers:**
  ```csharp
  private void BeginBlend(float duration)
  {
      blendDuration = Mathf.Max(0f, duration);
      Array.Copy(weights, startWeights, 3); startCharge = charge;
      blendStart = Time.unscaledTimeAsDouble; blending = blendDuration > 0f;
  }
  private void BeginReturn()
  {
      Array.Copy(weights, startWeights, 3); startCharge = charge;
      hands.Retarget(lastBody, age);
  }
  private void StartFollow()
  {
      hands.Reset();
      if (!TryBody(out var body, out _)) return;
      Pose left, right;
      if (prepared && preparedId == action.WorldId) { left = preparedLeft; right = preparedRight; }
      else if (committed.Item == action.WorldId) { left = committed.LeftPalm; right = committed.Palm; }
      else if (boundBinding != null) { left = boundBinding.Palm(false); right = boundBinding.Palm(true); }
      else return;
      lastBody = body;
      hands.BeginRelease(left, right, body, actionData.TwoHand);
  }
  private void RefreshArms()
  {
  #if UNITY_INCLUDE_INSTRUMENTATION
      if (Edit != null)
      {
          for (int i = 0; i < 3; i++) weights[i] = i == (int)Edit.Mode ? 1f : 0f;
          charge = Edit.Charged ? 1f : 0f; chargeMode = Edit.Mode; return;
      }
  #endif
      int selected = selectedDefinition && input.CanEquip ? (int)selectedData.HoldMode : -1;
      float t;
      if (action.State == ItemActionState.Recovering && !SlingshotRecovery && actionDefinition)
      {
          t = Mathf.SmoothStep(0f, 1f, hands.ReturnProgress(actionData.Poses, age));
          charge = Mathf.Lerp(startCharge, 0f, t);
      }
      else
      {
          float blend = blending ? Mathf.Clamp01((float)((Time.unscaledTimeAsDouble - blendStart) / blendDuration)) : 1f;
          if (blend >= 1f) blending = false;
          t = Mathf.SmoothStep(0f, 1f, blend);
          if (action.State == ItemActionState.Charging && actionDefinition) charge = Ease(ChargeProgress(age));
          else if (SlingshotRecovery) charge = Mathf.Lerp(releaseCharge, 0f, Mathf.SmoothStep(0f, 1f,
              Mathf.Clamp01((float)(age / Mathf.Max(0.01f, ((SlingshotDefinition)actionDefinition).RecoverySeconds)))));
          else charge = Mathf.Lerp(startCharge, 0f, t);
      }
      for (int i = 0; i < 3; i++) weights[i] = Mathf.Lerp(startWeights[i], i == selected ? 1f : 0f, t);
  }
  ```
- **`PrepareHands(binding, body)`:**
  ```csharp
  if (!running) return;
  if (advancedFrame != Time.frameCount) Advance();
  committed = default;
  if (boundBinding != binding) Bind(binding);
  preparedBody = body;
  if (!TryBody(out lastBody, out _)) { ClearTargets(); return; }
  if (!input.CanEquip || !input.HasAction) { avatar.HandTargets.SetArms(default); ClearTargets(); return; }
  SubmitTargets();
  ```
- **`SubmitTargets()`:**
  1. `SetArms` with `Settings = defaults`, the three weights, `Charge = charge` and `ChargeMode = chargeMode`. `Pitch` is `0f` in first person or while editing. Otherwise it is `Mathf.Clamp(placement.LookPitch, -40f, 50f) * charge`, with `placement = boundBinding != null ? avatar.Input : input.Placement`. Under instrumentation, also write `avatar.HandTargets.Swivel[0/1]` from `Edit`, or 0.
  2. If Recovering and not `SlingshotRecovery`: `ClearAnchor()`, then `SubmitFollow()`.
  3. Under instrumentation, if `Edit != null`: `SubmitEdit()` (step 13), then return.
  4. If `!selectedDefinition || !CanShowHeldItem`: `ClearTargets()`.
  5. Otherwise, let `data` be `actionData` while Charging and `selectedData` otherwise, `fingers = data.Fingers ? data.Fingers : clips.GripFingers`, and `w = weights[(int)data.HoldMode]`.
     - **TwoHand with `w > 0`:** `SetAnchor(data.RightContact, data.LeftContact, data.PrefabScale)`, then Item targets on both hands at weight `w` with `fingers`. The solver replaces their poses with the grips.
     - **Otherwise:** `ClearAnchor()`, a finger-only Item target on the right hand (weights 0, `fingers`), and for Slingshot, a finger-only Item target on the left hand with `clips.GripFingers`. Otherwise clear the left Item target.
     - Set `targetInstalled = true`.
- **`SubmitFollow()`:**
  ```csharp
  if (!hands.Releasing) { ClearTargets(); return; }
  bool available = tracking && input.ProjectileAvailable;
  hands.Sample(HeldItemPoseCalculation.PalmFromItem(input.Projectile, actionData.LeftContact, actionData.PrefabScale),
      HeldItemPoseCalculation.PalmFromItem(input.Projectile, actionData.RightContact, actionData.PrefabScale),
      available, releaseUnavailable || tracking && !available, lastBody, actionData.Poses, input.EnvironmentMask, age, null, null);
  if (!hands.Following) { tracking = false; releaseUnavailable = true; }
  HandHoldPresentation.Submit(avatar.HandTargets, AvatarHandSource.Item, leftTarget, target, hands.Left, hands.Right,
      hands.LeftWeight, hands.RightWeight, HandHoldPresentation.Reach(actionData.Poses), avatar.Registry.Animations.OpenFingers,
      hands.Stage != HandHoldPresentation.RecoveryStage.Follow);
  targetInstalled = true;
  ```
  Follow targets are world space. Pause and Return targets are body-relative, as the spec says. `Sample` still aborts on the linecasts and on `FollowReachFraction`.
- **`ClearTargets()`:** `avatar.HandTargets.ClearAnchor();`, then the old `ClearTarget` body using `HandHoldPresentation.Clear`.
- **`Dispose()`:** also calls `avatar.HandTargets.SetArms(default)`.
- **`CommitHands(AvatarBinding binding)`** returns the first-person rig correction (`Vector3`):
  ```csharp
  if (!running) return Vector3.zero;
  var data = action.State == ItemActionState.Charging ? actionData : selectedData;
  bool showing = selectedId != 0 && CanShowHeldItem;
  Pose palm, left, item;
  if (binding != null)
  {
      palm = binding.Palm(true); left = binding.Palm(false);
      item = !data.TwoHand ? HeldItemPoseCalculation.ItemFromPalm(palm, data.RightContact, data.PrefabScale) :
          binding.AnchoredItem ?? HeldItemPoseCalculation.TwoHandAnchor(palm, left, data.RightContact, data.LeftContact, data.PrefabScale);
  }
  else
  {
      item = HeldItemPoseCalculation.Fallback(lastBody, data);
      palm = HeldItemPoseCalculation.PalmFromItem(item, data.RightContact, data.PrefabScale);
      left = HeldItemPoseCalculation.PalmFromItem(item, data.LeftContact, data.PrefabScale);
  }
  Pose requestedRight = data.TwoHand ? HeldItemPoseCalculation.PalmFromItem(item, data.RightContact, data.PrefabScale) : palm;
  Pose requestedLeft = data.TwoHand ? HeldItemPoseCalculation.PalmFromItem(item, data.LeftContact, data.PrefabScale) : left;
  Vector3 correction = Vector3.zero; bool clear = true;
  if (showing && input.FirstPerson /* && Edit == null under instrumentation */)
  {
      clear = ItemReleaseClearance.TryResolve(item, input.Aim.position, data.TwoHand ? data.Sphere : new ItemReleaseSphere(Vector3.zero, data.ReleaseRadius),
          lastBody.Rotation, input.EnvironmentMask, out var allowed);
      if (clear) correction = allowed.position - item.position;
      if (correction.sqrMagnitude < 0.000001f) correction = Vector3.zero;
      palm.position += correction; left.position += correction; item.position += correction;
      requestedRight.position += correction; requestedLeft.position += correction;
  }
  clearanceAdjusted = correction != Vector3.zero;
  // fallback: TwoHand → item pose, otherwise → palm pose
  // if showing: CommitItem?.Invoke(item); then the slingshot pouch (step 6), passing `left` when bound
  // edit seeding (step 13)
  // Readout: Requested/Evaluated from the poses above, ActiveBlend = Blending, ClearanceAdjusted,
  //   Right/LeftUnreachable = !Blending && data.TwoHand && !InHandReach(requested, right, 0.98f),
  //   HasLeft = data.HoldMode != ItemHoldMode.OneHand
  // committed = new ReleaseSample { Item = selectedId, Generation = binding?.Generation ?? 0, Palm = palm, LeftPalm = left,
  //   ItemPose = item, Clear = clear, Progress = as today }
  return correction;
  ```
  The correction is already applied to every returned pose, so the caller only moves the rig. For TwoHand, the readout's position errors are the spec's "short by X cm".
- **`Shift(Vector3 correction)`** (pebble path):
  - Add `correction` to `committed.Palm`, `committed.LeftPalm`, `committed.ItemPose` and `fallback`.
  - `CommitItem?.Invoke(committed.ItemPose);`
  - `if (slingshot) slingshot.Shift(correction);`

## Step 6: Slingshot (`Items/SlingshotPresentation.cs`)
- **Delete:** `DrawCenter` (and its copy in `ItemPresentationVisual.cs:40`), the `draw` local, `Pouch`, `ForkMidpoint`, `CommitPalm`, `LeftWeight`, `lastDraw`, `cancelDraw`, `cancelledAt`, `engagedAt` and `returnPullWeight`.
- **Add fields:** `private Vector3 releaseLocal; private float recoilDraw;`.
- **New signature:** `internal void Evaluate(Pose frame, ItemActionState state, double age, float draw, float attach, float recovery, Pose? palm, ItemPalmContact contact, Vector3 scale)`.
  - Let `restWorld = frame.position + frame.rotation * Vector3.Scale(rest, scale)` and `mapped = HeldItemPoseCalculation.ItemFromPalm(palm, contact, scale).position`. Then `held = palm.HasValue ? Vector3.Lerp(restWorld, mapped, attach) : restWorld`.
  - **On entering Recovering:** `recoilDraw = draw`, and `releaseLocal` = the slingshot-local (unscaled) position of `previous == Charging ? Center : held`.
  - **On entering Charging:** `HasLoadedPose = false`.
  - `Loaded = state == Charging`. Otherwise `Center = held` and `slack = 1f - draw`.
  - **Recovering:**
    - The recoil runs from `releaseLocal` to `rest` over 0.07 s, with the existing wave. The wave amplitude uses `(releaseLocal - rest)` in place of `(drawPosition - rest)` and `recoilDraw` in place of `charge`.
    - Then `Center = Vector3.Lerp(loose, held, SmoothStep(InverseLerp(SettleSeconds, max(SettleSeconds, recovery), elapsed)))`.
    - `slack = 1f - recoil * recoilDraw`.
  - Keep the pebble and band tail (lines 90–94) and the `HasLoadedPose`/`DepartureCenter` rules.
  - Return `void`.
- **Called from `CommitHands`** when showing and a slingshot is bound, with:
  - `state = selectedId == action.WorldId ? action.State : Idle`, `draw = charge`, and `recovery = RecoverySeconds` of the action's `SlingshotDefinition` (0.5 if none).
  - `palm = binding != null ? left : null`, `contact = data.PullingContact` and `scale = data.PrefabScale`.
  - `attach = weights[(int)ItemHoldMode.Slingshot]` if `defaults.Slingshot.Hold(input.FirstPerson)` is assigned, otherwise 0.
- **Add** `internal void Shift(Vector3 offset)`: `Center += offset; if (Loaded) DepartureCenter = Center; LoadedPebble.position = Center;` then the two `Band(...)` calls from `ResetPose`.
- `ResetPose` drops the deleted fields.

## Step 7: `PlayerHandPresentation` and `PlayerHeldItemPresentation`

### `Player/PlayerHandPresentation.cs`
- **Carry** (lines 22, 30, 34, 250–253, 273–281, 295):
  - `carryHands` becomes a `HandHoldPresentation`, and `CarrySettings` becomes `items.HeldDefaults.TwoHand` (`HoldModePoses`).
  - `HeavyBody` is now the moved body of the old `TwoHandHoldPresentation.Body`, using `owner`.
  - Lines 251–252 become `body.CenterToWorld(carryBody.CenterToLocal(carryHands.Left/Right))`.
  - Both `Submit` calls pass `bodyRelative: false`.
- **`PrepareRemote`** (line 369): `if (avatar.Binding == null || binding == avatar.Binding) held.PrepareHands(binding, body);`. The candidate warm-up no longer gets its own held-item targets.
- **Split `FreeHands`** (lines 490–528):
  - `AdvanceFreeHands(rig, dt)` computes phase, fall, rise, dip, weights, `freePositions` and `freeRotations` (lines 492–522).
  - `SubmitFreeHands(rig)` writes the targets (lines 523–526), placing each target at `shoulder.position - rig.transform.rotation * appliedBob + rig.transform.rotation * (freePositions[i] * length)`.
- **Bob:** add `private Vector3 appliedBob;` and:
  ```csharp
  private Vector3 HoldBob(LocalFirstPersonHands rig)
  {
      float weight = held.ArmWeight;
      if (weight <= 0f || !freeInitialized[1]) return Vector3.zero;
      var data = rig.Binding.Measurements;
      Vector3 rest = Vector3.ClampMagnitude(avatar.Registry.FirstPerson.Right.RestPosition + rig.Binding.Settings.FirstPersonReachOffset, 0.85f);
      return (freePositions[1] - rest) * ((data.RightArm.x + data.RightArm.y) * rig.Binding.Scale * weight);
  }
  ```
  In `Place` (line 487): `appliedBob = HoldBob(rig); rig.Place(frame, appliedBob, heavy);`. `LocalFirstPersonHands.Place` already applies `offset` in the frame's axes.
- **`TranslateRig`:** add `internal void TranslateRig(Vector3 offset) { if (active && offset != Vector3.zero) active.transform.position += offset; }`. Delete `CommitCorrection` (lines 409–413).
- **`Evaluate`** (lines 415–463):
  - In the pending-rig block, `Place(candidate, 0f); … FreeHands(candidate, 0f);` becomes `AdvanceFreeHands(candidate, 0f); Place(candidate, 0f); SubmitFreeHands(candidate);`, and the `held.CommitHands(active.Binding)` at line 439 becomes `TranslateRig(held.CommitHands(active.Binding));`.
  - The main path becomes:
    ```csharp
    Contacts(dt, active ? active.Binding.Settings : avatar.Resolved?.Settings);
    if (active) { AdvanceFreeHands(active, dt); Place(active, dt); SubmitFreeHands(active); avatar.HandTargets.SetBody(active.Binding.Body); }
    held.PrepareHands(active ? active.Binding : null, TryBody(out var body) ? body : null);
    if (TryBody(out var carryFrame)) PrepareCarry(active ? active.Binding : null, carryFrame);
    if (active) active.Evaluate(dt, avatar.Registry.Animations);
    TranslateRig(held.CommitHands(active ? active.Binding : null));
    CommitCarry(active ? active.Binding : null);
    ```
    The second `active.Evaluate` is gone.

### `Player/PlayerHeldItemPresentation.cs`
- Delete `HeavyBody` (line 25; no callers, and its callee moved), `PrepareCandidate` (169) and `CorrectCommittedPose` (175).
- Add `internal float ArmWeight => State?.ArmWeight ?? 0f;`.
- `CommitHands` returns `State?.CommitHands(binding) ?? Vector3.zero`.
- **`TryPreparePebble`** (lines 192–210):
  ```csharp
  if (!ItemReleaseClearance.TryResolve(desired, player.AimPose.position, radius, State.LastBody.Rotation, registry.EnvironmentMask, out var allowed)) return false;
  Vector3 correction = allowed.position - desired.position;
  if (correction.sqrMagnitude > 0.000001f) { playerAvatar.Hands.TranslateRig(correction); State.Shift(correction); }
  center = slingshot.Center;
  return State.committed.Clear && ItemReleaseClearance.TryResolve(new Pose(center, release.rotation), player.AimPose.position, radius,
      State.LastBody.Rotation, registry.EnvironmentMask, out var check) && (check.position - center).sqrMagnitude < 0.000001f;
  ```

## Step 8: `ItemReleaseClearance` (`Items/ItemReleaseClearance.cs`)
- Delete `ItemReleaseReach` (lines 12–48).
- **Float overload** (96–99): `TryResolve(Pose desired, Vector3 reference, float envelopeRadius, Quaternion body, int environmentMask, out Pose allowed)`.
- **Main overload** (101): drop `ItemReleaseReach? reach`.
  - Line 119 becomes `if (Accessible(anchor, desired.position, radius, environmentMask)) return true;`.
  - Delete line 123, the dual projection block in `Consider` (146–150), the `!Reachable(candidate)` term (152) and the `Reachable` local function.
- `TryDrop` (169) drops its `null` argument.

## Step 9: Remaining runtime call sites
- `WorldItem.cs:401`: `grip.Heavy` becomes `grip.TwoHand`.
- `GripObserverPreview.Prepare` (lines 69–70): `if (presentation.Binding == null || presentation.Binding == binding) State.PrepareHands(binding, frame);`. The `Commit` handler ignores the returned correction.

## Step 10: Authoring cleanup
- **`GripAuthoringDrafts.cs`:**
  - `GripItemValues` keeps `RightPalmContact`, `LeftPalmContact`, `GripFingers`, `ThrowChargeTime`, `PullingPalmContact` and `RecoverySeconds`.
  - `GripAvatarValues` drops `FirstPersonHoldOffset`.
  - `GripHeldDefaultsValues` becomes `public HoldModePoses OneHand, TwoHand, Slingshot;`.
  - Delete `SetOverride` (197–204).
- **`GripAuthoringPanel.cs`:**
  - Avatar fields (111): drop `"FirstPersonHoldOffset"`, and change the header's "reach/hold in arm lengths" to "reach in arm lengths".
  - Delete `Group` (140–153) and the slingshot pose filter in `ObjectFields` (162–163).
  - `ItemFields` (119–139) becomes: the contacts header, `RightPalmContact`, `LeftPalmContact` and the Mirror button when `HoldMode == TwoHand` (step 11), `PullingPalmContact` when slingshot, and `GripFingers`. Then a header `$"Hold mode · {item.HoldMode} · clips and timing from {drafts.Held.name}"`, `ThrowChargeTime`, and for the slingshot `RecoverySeconds` plus the Inspect button.
  - `GripAuthoringFields.IsSpatial` becomes `path.EndsWith("Contact") || path.EndsWith("Correction")`.
  - `Units` drops the ChargePose/Pose/HoldSettings branches, keeping `"arm lengths / Euler degrees / seconds"` only for `"Left" or "Right"` (first-person free hands).
- **`GripAuthoringExport.cs`:**
  - `UnitsAndFrames`: delete `HandPose`, `FirstPersonPose` and `ChargePose`. `PullingPalmContact` becomes `"Pouch origin mapped from the posed left palm; item-root axes; metres before prefab scale; Euler degrees"`, `AvatarPlacement` drops "hold", and add `["HoldModePoses"] = "Pose clips; timing seconds; follow reach fraction"`.
  - `Effective`: replace `HandPose`/`FirstPersonPose` with `["HoldModePoses"] = Fields(drafts.Held.Poses(item.HoldMode))`.
  - Removals: `LeftPalmContact` when not TwoHand; `PullingPalmContact` and `RecoverySeconds` when not a slingshot.
- **`GripAuthoringScene.TryHandle`** (192–271): delete the pose-group `else` branch (222–266). For any path that isn't a Contact or Correction, return false.
- **`GripAuthoringScene.ReachText`** (181–191): drop "Reach limited". When the selected item is TwoHand and this is the observer text, append one line per observer (main plus strip, step 12): `$"{name}: short R {right * 100:F1} cm · L {left * 100:F1} cm"`, taken from each state's `Readout.Right/LeftPositionError`.
- **`GripAuthoringWindow.cs`:**
  - The handle list (59–64) becomes `RightPalmContact`, `LeftPalmContact`, `PullingPalmContact`, `LeftPalmCorrection`, `RightPalmCorrection`, `Pose.RightPalm` and `Pose.LeftPalm`.
  - `CompatibleHandle` (137–143): `LeftPalmContact` needs `HoldMode == TwoHand`, `PullingPalmContact` needs a `SlingshotDefinition`, and the SharedDefaults and ChargePose clauses go. Pose paths are handled in step 13.
- **`Editor/ItemDefinitionEditor.cs`:** rewrite `OnInspectorGUI` as a plain property loop:
  - When every target is TwoHand, show a help box: "TwoHand items sit between the posed palms: the midpoint of the palm contacts sits at the palms' midpoint. Author both palm contacts in item-root space."
  - Hide `LeftPalmContact` unless TwoHand.
  - Keep the `CollisionDamage` override rule and the disabled `m_Script`.
  - Delete `registry`, `OnEnable`, `DrawInherited` and `DrawPose`.
- **`Editor/ItemSetup.cs:58,246`:** `ItemHoldMode.Heavy` becomes `ItemHoldMode.TwoHand`.

## Step 11: Mirror R→L and ghost hands

### Mirror (`GripAuthoringPanel.ItemFields`)
```csharp
fields.Add(new Button(() =>
{
    BeforeEdit?.Invoke();
    var right = (ItemPalmContact)GripAuthoringFields.Get(record.Values, "RightPalmContact");
    Quaternion q = right.Rotation;
    var mirrored = new ItemPalmContact { Position = new Vector3(-right.Position.x, right.Position.y, right.Position.z),
        Euler = new Quaternion(q.x, -q.y, -q.z, q.w).eulerAngles };
    drafts.Edit(record, () => GripAuthoringFields.Set(record.Values, "LeftPalmContact", mirrored));
    Rebuild();
}) { text = "Mirror R→L" });
```

### `GripGhostHand` (new, `Authoring/GripGhostHand.cs`, `#if UNITY_INCLUDE_INSTRUMENTATION`)
- **Constructor** `(AvatarRegistry.Entry entry, AvatarAnimationSet clips, AnimationClip fingers, bool right, int layer)`:
  1. Instantiate `entry.Prefab` under an inactive holder. Before activating it, `DestroyImmediate` every `MonoBehaviour`, `Joint`, `Collider` and `Rigidbody`, and set the root scale to `entry.Settings.Scale`.
  2. Build a manual `PlayableGraph`: the `clips.Idle` basis (speed 0, no IK), then `AvatarFingerLayers` with `Select(right, fingers)` and `Advance(0f, 0f)`, output to the Animator. Call `Evaluate(0f)`, dispose the layers, destroy the graph, and disable the Animator. The bones keep the finger pose.
  3. Reparent the hand bone (`RightHand`/`LeftHand`) to a new root GameObject with world position kept, and set `Hips.localScale = Vector3.zero`. The body collapses to a point, and a seam stump is left at the wrist.
  4. Every renderer gets the shared ghost material and `ShadowCastingMode.Off`. Skinned renderers get `updateWhenOffscreen = true`. Everything goes on `layer`.
  5. Cache the wrist-to-palm offsets from `AvatarPalmCalibration.Measurements(entry.Settings)` × scale.
- **`Place(Pose palm)`:** set the hand from the palm through those offsets (the `AvatarArmIK` palm → wrist math), then `hips.position = hand.position`, so the collapse point stays at the wrist.
- **`SetVisible(bool)`, `Dispose()`**, and `AvatarId Avatar` / `AnimationClip Fingers` for rebuild checks.
- **Material** (static, created once):
  - `new Material(Shader.Find("Universal Render Pipeline/Unlit"))`
  - `_Surface` 1, `_Blend` 0, `_ZWrite` 0, `_SrcBlend` SrcAlpha, `_DstBlend` OneMinusSrcAlpha, the `RenderType` Transparent tag and the `_SURFACE_TYPE_TRANSPARENT` keyword
  - `renderQueue` Transparent and `_BaseColor (0.35, 0.85, 1, 0.35)`
- **In `GripAuthoringScene`:** a `Dictionary<string, GripGhostHand>` for the three contact paths.
  - In `LateUpdate`, show a ghost when the context is Item, `observer.ItemRoot` exists, the path applies to the item (Left for TwoHand, Pulling for a slingshot), and `TryHandle(path, out var handle)` succeeds. Then call `ghost.Place(handle.World)`.
  - Rebuild a ghost when the selected avatar or the effective fingers change. The effective fingers are `item.GripFingers ?? Avatars.Animations.GripFingers`; the pulling ghost uses the registry's `GripFingers`.
  - The layer is `AvatarEditorPreview.LayerName`, so only the observer camera draws ghosts. Dispose them in `OnDestroy`.
  - The ghosts follow the existing contact handles, because they read the same `TryHandle` poses.

## Step 12: Multi-avatar strip
- **`GripObserverPreview`:**
  - `Initialize(PlayerAvatarPresentation owner, GripAuthoringDrafts drafts, AvatarId avatar = default, float lateral = 0f)`.
  - With a valid `avatar`, call `presentation.RequestAvatar(avatar)` and skip following the owner's identity.
  - `InputSource` returns the owner's placement shifted by `Quaternion.Euler(0, Facing yaw, 0) * Vector3.right * lateral` (`SolePosition` and `Facing.position`).
  - In `Prepare`, apply the same shift to the captured input's `Placement`, `Aim.position` and `Projectile.position`.
  - Add `internal string DisplayName => presentation.Resolved?.Settings.DisplayName;`.
- **`GripAuthoringScene`:**
  - Add `private readonly List<GripObserverPreview> strip = new();` and `RebuildStrip()`, called after `observer.Initialize` in `Attach` and at the end of `SelectAvatar`.
  - `RebuildStrip()` destroys the existing strip. For each `Drafts.Avatars.Entries` entry other than `Drafts.SelectedAvatar`, it creates `new GameObject($"Grip strip {entry.Id}")` under `observer.transform.parent`, adds `AvatarPresentation` and `GripObserverPreview`, and calls `Initialize(avatar, Drafts, entry.Id, ++index * 0.9f)`.
  - Don't clone the observer: its child avatar instance would be cloned too.
  - Destroy the strip in `OnDestroy`.
  - Strip avatars show saved clips only. Pose edits apply to the main observer until they're saved.

## Step 13: Pose editor

### State override (`HeldItemPresentationState`, `#if UNITY_INCLUDE_INSTRUMENTATION`)
```csharp
internal sealed class PoseEdit
{
    internal ItemHoldMode Mode;
    internal bool Charged, Seeded;
    internal Pose Right, Left;
    internal float RightSwivel, LeftSwivel;
}
internal PoseEdit Edit;
```
- **`RefreshArms`** already forces the edit's mode and slot (step 5). `HeavyFrameWeight` follows, so first-person TwoHand clips are authored on the `HeavyBody`-anchored rig.
- **`SubmitEdit()`**, called from `SubmitTargets` before the showing check:
  - `ClearAnchor()`.
  - Place `target` (and `leftTarget` unless the mode is OneHand) at `AvatarHandTargets.Rebase(Edit.Right/Left, Pose.identity, avatar.HandTargets.Body)`.
  - Set them as body-relative Item targets with weight `Edit.Seeded ? 1f : 0f`, fingers from the selected item or the registry `GripFingers`, and reach 0.98. Clear the left target for OneHand.
  - Set `targetInstalled = true`.
- **`CommitHands`:**
  - If `Edit != null && binding != null && !Edit.Seeded`, store `Edit.Right/Left = AvatarHandTargets.Rebase(binding.Palm(true/false), binding.Body, Pose.identity)` and set `Seeded = true`. The handles start at the clip's palms.
  - Skip first-person clearance while editing.
  - For TwoHand, `AnchoredItem` is `null` while editing, so the item sits between the edited palms. The gap shows in the readout.

### Scene API (`GripAuthoringScene`)
- **`BeginPoseEdit(ItemHoldMode mode, int slot)`**:
  - The slot is `0` TP Hold, `1` TP Charged, `2` FP Hold, `3` FP Charged.
  - Call `EndPoseEdit()` first. The target state is `slot >= 2 ? held.State : observer.State`; set its `Edit = new PoseEdit { Mode = mode, Charged = slot % 2 == 1 }`.
  - Store `PoseMode` and `PoseSlot`.
- **`EndPoseEdit()`**, `PoseEditing`, `ResetPoseEdit()` (`Seeded = false`, both swivels 0) and `SetSwivel(bool right, float degrees)`.
- **`TryPoseHandle(bool right, out Pose world)`** and **`SetPosePalm(bool right, Pose world)`** convert through the edit binding's `Body`: `avatar.Hands.LocalBinding` for FP and `observer.Presentation.Binding` for TP.
- **`TryCapturePose(out GripPoseCapture capture)`:** `using var handler = new HumanPoseHandler(binding.Animator.avatar, binding.Animator.transform);`, then `GetHumanPose` and copy the muscles.
- **Capture type:** `public struct GripPoseCapture { public ItemHoldMode Mode; public bool FirstPerson, Charged; public float[] Muscles; }`.
- **`EndPoseEdit()`** is also called on `SelectAvatar`, `SelectItem` and `OnDestroy`.

### Save
- **`GripAuthoringScene`:** `#if UNITY_EDITOR public static event Action<GripPoseCapture> EditorPoseSaveRequested; #endif`.
- **The in-game panel's `SavePose`:**
  - In the editor, it invokes that event.
  - In builds, it shows "Pose clips save only in the editor."
- **`GripAuthoringWindow`:** sets `SavePose = capture => GripAuthoringPersistence.SavePose(drafts, capture)`.
- **`GripAuthoringPersistence`:** subscribe `EditorPoseSaveRequested` in the static constructor next to line 16, and add:
  ```csharp
  public static void SavePose(GripAuthoringDrafts drafts, GripPoseCapture capture)
  {
      var record = drafts.Find("shared-held-item-settings");
      string slot = (capture.FirstPerson ? "FirstPerson" : "ThirdPerson") + (capture.Charged ? "Charged" : "Hold");
      string field = capture.Mode + "." + slot;
      var clip = new AnimationClip { name = capture.Mode + slot };
      for (int i = 0; i < HumanTrait.MuscleCount; i++)
          if (HumanTrait.BoneFromMuscle(i) is (int)HumanBodyBones.LeftShoulder or (int)HumanBodyBones.RightShoulder or
              (int)HumanBodyBones.LeftUpperArm or (int)HumanBodyBones.RightUpperArm or (int)HumanBodyBones.LeftLowerArm or
              (int)HumanBodyBones.RightLowerArm or (int)HumanBodyBones.LeftHand or (int)HumanBodyBones.RightHand)
              AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), HumanTrait.MuscleName[i]),
                  new AnimationCurve(new Keyframe(0f, capture.Muscles[i])));
      var existing = GripAuthoringFields.Get(record.Values, field) as AnimationClip;
      if (existing && AssetDatabase.Contains(existing))
      { EditorUtility.CopySerialized(clip, existing); Object.DestroyImmediate(clip); clip = existing; EditorUtility.SetDirty(clip); AssetDatabase.SaveAssetIfDirty(clip); }
      else
      {
          const string folder = "Assets/Art/Animations/HeldPoses";
          if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder("Assets/Art/Animations", "HeldPoses");
          AssetDatabase.CreateAsset(clip, AssetDatabase.GenerateUniqueAssetPath($"{folder}/{clip.name}.anim"));
      }
      drafts.Edit(record, () => GripAuthoringFields.Set(record.Values, field, clip));
  }
  ```
  - Arm muscle names equal their curve attribute names; finger muscles are excluded.
  - `CopySerialized` overwrites in place and keeps references, the same pattern `AvatarProcessor` uses for meshes.
  - The shared HeldItemSettings draft becomes dirty, and the user saves that context to persist the assignment.
- After a save, the panel calls `ResetPoseEdit()`. The new clip equals the edited pose, so the swivel returns to 0 and doesn't apply twice.

### Panel (`GripAuthoringPanel`)
- **Placement:** when `scene` exists, the context is SharedDefaults, and `record.Runtime is HeldItemSettings`, add a "Pose editor" section above the fields.
- **Controls:**
  - A `DropdownField` "Mode" (OneHand / TwoHand / Slingshot), defaulting to the selected item's mode.
  - A `DropdownField` "Slot" (Third person hold / Third person charged / First person hold / First person charged).
  - A `Toggle` "Edit pose" (`BeginPoseEdit` / `EndPoseEdit`). A mode or slot change while editing calls `BeginPoseEdit` again.
  - "Right elbow swivel °" and "Left elbow swivel °" `Slider`s, −180 to 180, with an input field, calling `SetSwivel`.
  - A "Reset palms to clip" button calling `ResetPoseEdit`.
  - A "Save pose clip" button: `if (scene.TryCapturePose(out var capture)) { SavePose?.Invoke(capture); scene.ResetPoseEdit(); }`.
- Add `public Action<GripPoseCapture> SavePose;`.
- The fields below keep editing the `OneHand`/`TwoHand`/`Slingshot` clips and timings through the existing `ObjectFields`/`ClipField` path.

### Handles (`GripAuthoringWindow.SceneGUI`)
Before `CompatibleHandle`, if `handlePath` starts with `"Pose."`:
- Require `scene.TryPoseHandle(right, out var palm)`.
- Draw the label and the position and rotation handles.
- On change, call `scene.SetPosePalm(right, new Pose(position, rotation))`, which records no draft edit and no undo, then `return`.

## Step 14: Asset migration
- **`Assets/Game/Settings/HeldItemSettings.asset`:** replace the `FirstPersonPose`, `HoldSettings`, `HeavyHoldSettings` and `SlingshotChargePose` blocks with the new blocks.
  - `OneHand` and `TwoHand`: all four clips `{fileID: 0}`, `ChargePoseDuration: 0.35`, `MaximumFollowDuration: 0.2`, `EndPosePauseDuration: 0`, `ReturnBlendDuration: 0.2`, `FollowReachFraction: 0.85`.
  - `Slingshot`: the same fields with the slingshot's old override timings: `ChargePoseDuration: 0.15`, `MaximumFollowDuration: 0`, `EndPosePauseDuration: 0`, `ReturnBlendDuration: 0.2`, `FollowReachFraction: 0.85`.
- **`Assets/Game/ScriptableObjects/Items/Slingshot.asset`:** add `HoldMode: 2` after `ThrowChargeTime: 1` (line 23).
- **Other item and avatar assets:** unchanged. Their stale serialized fields drop on the next save.
- **Pose clips:** the 12 clips are created by the user in the pose editor (see visual validation).

## Pre-existing dead code (mention, don't delete)
- `WorldItemRegistry.GetHeldPose`: no callers. Only its signature changes.
- `HeldItemPresentationState.RightTarget` / `LeftTarget`: no external callers.
- `AvatarPresentation.SetHandTarget` / `ClearHandTarget`: no callers (noted in spec A).

## Visual validation (user)
1. **Before any clips:** held items ride the base-animation arms. The Boulder sits between the hanging hands with the arms pulled in, and the slingshot pouch stays at rest.
2. **Pose editor:** in the grip authoring scene, with the rock equipped, go to Shared Defaults → Pose editor → OneHand / Third person hold → Edit pose.
   - The observer's right arm takes the current pose. Drag the `Pose.RightPalm` handle and the elbow swivel, then Save pose clip.
   - A clip appears in `Assets/Art/Animations/HeldPoses`, the draft shows it assigned, and saving the Shared Defaults context persists it.
   - The rock sits in the palm with no drift while walking and sprinting.
3. **Author the other 11 clips.** First-person slots drive the first-person rig in the first-person view. Author TwoHand with the Boulder equipped and Slingshot with the slingshot equipped.
4. **Multi-avatar strip:** every avatar holds the rock in the same pose relative to its own body. With the Boulder, each strip line shows "short" under about 5 cm, or none.
5. **Boulder (TwoHand):** it hangs low with the arms nearly straight, and the hands stay on the grips while walking. In first person, the Boulder stays low whatever the look pitch.
6. **Mode switching:** switching rock → boulder → slingshot → nothing cross-fades the arms with no pop. Throwing the last rock, then equipping the Boulder mid-recovery, blends straight into the TwoHand hold.
7. **Charge in third person:** look up and down while charging a rock throw and a slingshot shot. The charging arm or arms tilt with pitch, elbows bend naturally overhead, and nothing pops.
8. **Throw follow-through (rock and Boulder):** the hand or hands briefly follow the item, pause, and ease back to Hold, or to the base arms when nothing is left. Throwing into a nearby wall aborts the follow.
9. **Slingshot:**
   - The pouch stays in the left hand from Hold through full draw, in both first and third person, and the pebble launches from the pouch.
   - After release, the pouch snaps back with the band wave and then settles into the left hand.
   - Cancelling a draw brings the pouch back with the hand.
10. **First person:**
    - Walking, falling and landing move the arms and the held item together. The free left hand (while holding a rock) keeps its own bounce.
    - Walking into a wall pulls the whole rig back, and throws and slingshot shots near walls start from the cleared position.
    - Note whether the fall lift or the clearance stepping needs tuning (spec issues 8 and 9).
11. **Grip tool:**
    - Ghost hands sit on each grip and match the in-game hands (the one-hand ghost overlays the real hand exactly).
    - Mirror R→L produces a symmetric left grip on the Boulder, matching its current left contact.
12. **Player carry and cart:** carry grips and cart handles behave as before.
13. **Failed or loading remote avatar:** the held item still shows at the default hold placement.
