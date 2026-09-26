# Hand Grip IK Plan

Implements `Hand_Grip_IK_Spec.md`. Line numbers refer to the code at commit `3bd345f`.

## Decisions

1. **World-contact authoring body**: the existing `GripObserverPreview`, at zero lateral offset, is the third-person body in the driver seat. The owner's own third-person body stays dormant, so there is no hand-target override on `AvatarPresentation`. (User decision.)
2. **Carry keeps its first-person body-frame blend**: only the held-item part of `HeavyFrameWeight` is removed. The property becomes carry-only as `CarryFrameWeight`. (User decision.)
3. **The first-person slot frame is not a child of the `LocalFirstPersonHands` instance.** The rig instance is destroyed and replaced when the avatar changes, and that would destroy a `WorldItem` parented under it. The frame lives under the player root and copies the rig root pose every frame.
4. **Third-person slot posing runs after animation and before arm IK**, through a new `AvatarPresentation.PosingHands` event raised from `AvatarInstance.Evaluate`.
5. **The IK is given the existing standalone target transforms, not the authored slot transforms.** These are `RightItemHandTarget`, `LeftItemHandTarget` and two new hint transforms. Their poses are copied from the slot rig, or blended during switches and throw follow, so every blend lives in one place.
6. **Slingshot cancel** returns charge, grab and draw to 0 over `RecoverySeconds`, because the slingshot does not use `ChargePoseDuration`.
7. **The release progress byte keeps today's encoding**: the linear charge fraction. Receivers apply `easeOutSine`.
8. **Authoring lock**: while the edited rig is in the Hold or Charged phase, content-change refreshes don't rewrite authored transforms. A change of item, avatar, view or slot always applies, and the window forces a re-apply after Save, Clear and Revert.
9. **Undefined elbow hints while authoring** follow the automatic hint each frame and have no IK influence. Once moved, they drive the IK and count as dirty.
10. **Row actions**: "Clear" removes the value at the row's source layer. It is disabled for a Slot-layer pose target, because those are required. "Copy to other view" copies the row's saved value at its source layer to the other view at the same layer. It is disabled while the row is dirty.
11. **Window extras**: the existing Look-through-FP-camera toggle, Use action and Auto re-equip toggle stay. The spec doesn't list them as removed.

---

## 1. Data model

### 1.1 `Runtime/Items/GripPoses.cs` (new; replaces `GripOffset.cs`)
Delete `Runtime/Items/GripOffset.cs` and its `.meta` with `git rm`.

```csharp
public enum GripTarget : byte
{ HoldPose, ChargePose, RightHand, LeftHand, PouchDraw, RightElbowHold, RightElbowCharge, LeftElbowHold, LeftElbowCharge, ContactPalm, ContactElbow }
public enum GripLayer : byte { None, Slot, Item, Avatar, Contact }

[Serializable] public struct GripPose { public GripTarget Target; public bool FirstPerson; public Vector3 Position; public Quaternion Rotation; }

[Serializable] public sealed class GripPoseTable
{
    public List<GripPose> Entries = new();
    public bool TryGet(GripTarget target, bool firstPerson, out Pose pose);
    public void Set(GripTarget target, bool firstPerson, Pose pose);   // replace or append
    public bool Remove(GripTarget target, bool firstPerson);
}

[Serializable] public sealed class AvatarGripPoses { public AvatarId Avatar; public GripPoseTable Poses = new(); }

public static class GripPoses
{
    public const float ReferenceHeight = 1.8f;
    public static float Scale(AvatarSettings settings) => settings ? settings.VisualHeight / ReferenceHeight : 1f;
    public static bool IsHint(GripTarget t);        // four elbow targets + ContactElbow
    public static bool InSlotFrame(GripTarget t);   // HoldPose, ChargePose, four elbow targets (scaled)
    public static bool Uses(HoldSlotMode mode, GripTarget t);
    public static bool Required(HoldSlotMode mode, GripTarget t) => Uses(mode, t) && !IsHint(t);
    public static GripPoseTable Find(List<AvatarGripPoses> list, AvatarId avatar);   // null when absent
    public static GripPoseTable Ensure(List<AvatarGripPoses> list, AvatarId avatar); // editor saves
    public static bool TryResolve(HoldSlot slot, ItemDefinition item, AvatarId avatar, GripTarget target,
        bool firstPerson, out Pose pose, out GripLayer layer);  // Avatar → Item → Slot
}
```

`Uses` table:

| Mode | Targets |
|---|---|
| Hand | HoldPose, ChargePose, RightHand, RightElbowHold, RightElbowCharge |
| Heavy | Hand + LeftHand, LeftElbowHold, LeftElbowCharge |
| Slingshot | HoldPose, ChargePose, RightHand, LeftHand (pouch frame), PouchDraw, RightElbowHold, RightElbowCharge, LeftElbowCharge |

Stored slot-frame positions use the 1.8 m reference height: multiply by `GripPoses.Scale` at runtime and divide when saving. Item-frame and pouch-frame values are plain metres. Rotations are never scaled.

### 1.2 `HoldSlot` (rename of `HoldClass`)
- Rename with `git mv Runtime/Items/HoldClass.cs Runtime/Items/HoldSlot.cs` and `git mv` its `.cs.meta` alongside. This keeps the script GUID, so `Assets/Game/Settings/HoldClasses/{Regular,Heavy,Slingshot}.asset` and every reference survive.
- File contents:
```csharp
public enum HoldSlotMode { Hand = 0, Heavy = 1, Slingshot = 2 }

[CreateAssetMenu(menuName = "Two Birds/Hold Slot")]
public sealed class HoldSlot : ScriptableObject
{
    // ContentChanged / NotifyContentChanged / OnValidate unchanged
    public HoldSlotMode Mode;
    [Tooltip("Required for every pose target the mode uses, in both views.")] public GripPoseTable Defaults = new();
    [Tooltip("Used when the item has no Grip Fingers.")] public AnimationClip Fingers;
    [Min(0f)] public float MaximumFollowDuration = 0.2f, EndPosePauseDuration, ReturnBlendDuration = 0.2f;
    [Range(0.01f, 0.85f)] public float FollowReachFraction = 0.85f;
}
```
- Delete `HoldClassView`, `View()`, `Spread()` and `ChargePoseDuration`.
- Delete `ItemHoldMode` (`ItemDefinition.cs` L7) and replace every use: `OneHand`→`Hand`, `TwoHand`→`Heavy`, `Slingshot`→`Slingshot`. The files are `ItemSetup`, `GripAuthoringScene`, `AvatarArmIK`, `HeldItemPose`, `ItemDefinition` and `HeldItemPresentationState`.
- Retype `HoldClass` → `HoldSlot` in `ItemRegistry.CarryHold` (L12), `WorldItemRegistry.CarryHold` (L82), `HandHoldPresentation` (L20, L51, L57, L62), `PlayerHandPresentation.CarrySettings` (L30), `GripAuthoringAssets`, `GripAuthoringWindow` and `GripAuthoringBuildPolicy`.

### 1.3 `ItemDefinition` (`Runtime/Items/ItemDefinition.cs`)
Replace L49–65 (the header through `Grip()`) with:
```csharp
[Header("Held Item Grip")]
[Tooltip("Required."), FormerlySerializedAs("HoldClass")] public HoldSlot HoldSlot;
[Min(0f), Tooltip("Visual Hold → Charge time in seconds, independent of Throw Charge Time. The slingshot uses Throw Charge Time instead.")]
public float ChargePoseDuration = 0.35f;
public GripPoseTable GripPoses = new();
public List<AvatarGripPoses> AvatarGripPoses = new();
public AnimationClip GripFingers;
public HoldSlotMode HoldMode => HoldSlot ? HoldSlot.Mode : HoldSlotMode.Hand;
```
Clamp `ChargePoseDuration` in `NotifyContentChanged`. Every `definition.HoldClass` becomes `definition.HoldSlot`.

### 1.4 `SlingshotDefinition`
Remove `PouchOffset` (L9–11). Under the `Slingshot Pouch` header add `[Min(0f)] public float PouchGrabSeconds = 0.15f;`.

### 1.5 `AvatarHandContact` (`Runtime/Avatars/AvatarHandContact.cs`)
```csharp
public AvatarIKGoal Hand; public AnimationClip Fingers;
[Range(0.1f, 0.98f)] public float MaximumReach = 0.98f; [Min(0f)] public float BlendTime = 0.15f;
[Tooltip("Frame the elbow hint is stored in, so turning the palm's parent does not swing the elbow. Defaults to the parent.")]
public Transform HintFrame;
public GripPoseTable Poses = new();                  // contact default: ContactPalm (parent frame), ContactElbow (hint frame)
public List<AvatarGripPoses> AvatarPoses = new();    // contact × avatar
internal Transform HintSpace => HintFrame ? HintFrame : transform.parent;
internal bool TryResolve(GripTarget target, AvatarId avatar, bool firstPerson, out Pose local, out GripLayer layer); // Avatar → Contact
internal Pose Palm(AvatarId avatar, bool firstPerson);   // parent ∘ resolved; falls back to this transform's world pose
internal bool TryHint(AvatarId avatar, bool firstPerson, out Vector3 world); // HintSpace.TransformPoint(resolved)
```
Contact values are not scaled by avatar size. Section 6.1 adds the authoring members.

### 1.6 `HeldItemPose.cs`
- `HeldItemPoseData` gets these fields: `HoldSlot Slot`, `HoldSlotMode Mode`, `AnimationClip Fingers`, `ItemReleaseSphere Sphere`, `float ReleaseRadius`, `float ChargeDuration`, `float GrabSeconds` and `float RecoverySeconds`, plus `bool Heavy => Mode == HoldSlotMode.Heavy`.
  - `Fingers` is the item's `GripFingers`, falling back to `Slot.Fingers`.
  - `ChargeDuration` is `ThrowChargeTime` for the slingshot and `ChargePoseDuration` for everything else.
  - `GrabSeconds` and `RecoverySeconds` are the slingshot's values, or 0.
  - Remove `Class`, `PouchOffset` and `TwoHand`.
- `HeldItemBodyFrame` gets `internal readonly Vector3 Hips`.
  - Placement constructor (L41–54): compute it like `Shoulder`.
    - Standing: `input.SolePosition + torso * (StandingOffset + carried) + Rotation * ((Generated.Hips − up * SolePlane) * scale)`.
    - Seated: `input.Facing.position + input.Facing.rotation * AvatarDriverPose.SeatedOffset(...)`.
    - Lerp between the two by `seatedWeight`.
  - Bone constructor: set `Hips` to the shoulder midpoint. No caller uses it.
  - Keep the `heavy` parameter, because carry still uses it.
- `HeldItemPoseCalculation`: delete `FallbackPalm`, `FallbackCenter`, `GripFrame` and `FallbackFrame`. Keep `Compose` and `Inverse`.

---

## 2. IK core

### 2.1 `AvatarHandTargets`
- `Target`: add `internal Transform Hint; internal float HintWeight;`.
- `Set(...)`: append `Transform hint = null, float hintWeight = 0f`, which store into those fields.
- Delete `TwoHandAnchor`, `Anchor`, `SetAnchor`, `ClearAnchor`, `Arms`, `SetArms` and the instrumentation block (L26–29).

### 2.2 `HandHoldPresentation.Submit`
Append `Transform leftHint = null, Transform rightHint = null, float leftHintWeight = 0f, float rightHintWeight = 0f` and pass them through to `targets.Set`. Carry callers don't change.

### 2.3 `AvatarArmIK` (rewrite `Runtime/Avatars/AvatarArmIK.cs`)
- `Arm` gets `Transform Clavicle`, set to `GetBone(Left/RightShoulder)`. It can be null.
- `Solve(targets, dt, weight)`:
  - Remove pitch (L128–134), the anchor block (L140–154) and `Swivel()` (L159–166).
  - Keep the Item-source pre-solve of the Free target (L137–139).
  - Then call `Solve(left…)` and `Solve(right…)`.
- Per-arm `Solve(Arm arm, bool rightHand, Target target, Pose prepared, Pose body, float dt, float weight)`. Drop the `grip` and `swivel` parameters.
  1. Palm, wrist rotation, reach and contact weight stay as today (L182–199).
  2. **Clavicle.** This runs before `root` is read.
     - Condition: `arm.Clavicle && positionWeight > 0`.
     - Compute the wrist target from the palm, then `t = InverseLerp(0.85f, 1f, distance(upper, wrist) / length) * positionWeight`.
     - Apply `arm.Clavicle.rotation = Quaternion.RotateTowards(identity, FromToRotation(upper − clavicle, wrist − clavicle), MaxClavicleAngle * t) * arm.Clavicle.rotation`.
     - Use constants `ClavicleStart = 0.85f` and `MaxClavicleAngle = 20f`.
     - Then set `root = arm.Upper.position`.
  3. **Pole.** Replace L209–211.
     - `animatedBend` is today's bend, including the fallback.
     - Start from `pole = AutoHint(root, end, body.rotation, rightHand, upper)`. If `target.Hint` is set, use `pole = Vector3.Lerp(pole, target.Hint.position, target.HintWeight)`.
     - `hintBend = ProjectOnPlane(pole − root, direction).normalized`. If it is degenerate, use `animatedBend`.
     - `bend = Vector3.Slerp(animatedBend, hintBend, positionWeight)`.
  4. Run the two-bone block whenever `positionWeight > 0`. Drop the swivel test.
- Add `internal static Vector3 AutoHint(Vector3 shoulder, Vector3 hand, Quaternion body, bool right, float upperLength)`. It returns `(shoulder + hand) * 0.5f + body * new Vector3(right ? 0.6f : -0.6f, -1f, -0.3f).normalized * upperLength`. The authoring code reuses it.

### 2.4 Remove the arm clip layers
- Delete `Runtime/Avatars/AvatarArmLayers.cs` and its `.meta` (`AvatarArmPose`, `AvatarArmLayers`).
- `AvatarAnimationGraph`: remove `Arms` (L131). Change L161 to `Fingers = new AvatarFingerLayers(graph, states);`. Remove `Arms?.Dispose()` from `Dispose`.
- `AvatarInstance.Evaluate`:
  - Remove L128 (`graph.Arms.Set`) and L140–142 (`RoundTripMuscles`).
  - Insert `host.PoseHands(Binding);` directly before L139 (`arms.Solve`).
  - Remove `Binding?.Dispose()` (L222).
- `LocalFirstPersonHands`:
  - Remove `poses` (L13) and `PoseWeight` (L20).
  - Change L45 to `fingers = new AvatarFingerLayers(graph, basis);`.
  - In `Evaluate`, remove `poses.Set` and `RoundTripMuscles`.
  - In `OnDestroy`, remove `poses?.Dispose()` and `Binding?.Dispose()`.
  - Rename `Place`'s `heavyWeight` parameter to `bodyWeight`. It is carry-only now.
- `AvatarBinding` (`AvatarPresentation.cs` L57–71): delete `AnchoredItem`, the instrumentation pose handler, `RoundTripMuscles`, `CaptureMuscles` and `Dispose`.
- `AvatarPresentation`: add `internal event Action<AvatarBinding> PosingHands;` and `internal void PoseHands(AvatarBinding binding) => PosingHands?.Invoke(binding);`.

---

## 3. Slot rig and held-item presentation

### 3.1 `Runtime/Player/GripSlotRig.cs` (new)
This class owns the slot Transform hierarchy for one `HeldItemPresentationState`, in either view.
```
HeldItemSlots               (Frame: hip follower / first-person rig frame; under the state's host, scale-compensated like today's HeldItemFallback)
├─ HandSlot                 (identity local)
│  ├─ HoldPose  ChargePose  RightElbowHold  RightElbowCharge  LeftElbowHold  LeftElbowCharge
│  └─ ItemAnchor
│     ├─ RightHand  LeftHand  PouchDraw
│     └─ Pouch
│        └─ LeftHand
└─ HeavySlot                (same children)
```
```csharp
internal sealed class GripSlotRig : IDisposable
{
    internal sealed class Slot { internal Transform Root, ItemAnchor, Pouch; internal HoldSlotMode Mode; /* per-target transforms + applied records */ }
    internal Transform Frame { get; }
    internal readonly Slot Hand, Heavy;
    internal bool Locked;                                   // authoring lock (Decision 8)
    internal GripSlotRig(Transform host);
    internal Slot For(HoldSlotMode mode) => mode == HoldSlotMode.Heavy ? Heavy : Hand;
    internal Transform Target(Slot slot, GripTarget target); // LeftHand → Pouch/LeftHand in Slingshot mode
    internal void Apply(Slot slot, HoldSlot holdSlot, ItemDefinition item, AvatarId avatar, bool firstPerson, float scale, bool force = false);
    internal void Place(Pose frame);                         // Frame.SetPositionAndRotation
    internal void Anchor(Slot slot, float charge, float draw, Vector3 restPouch, float pitch, Vector3 pivot);
    internal bool Hint(Slot slot, bool right, float charge, float pitch, Vector3 pivot, out Vector3 world, out float weight);
    internal bool TryAuthored(Slot slot, GripTarget target, float scale, out Transform transform, out Pose stored, out GripLayer layer, out bool dirty);
    public void Dispose();                                   // destroys Frame
}
```

**`Apply`**
- Set `slot.Mode`. Activate only the transforms whose target `GripPoses.Uses(mode, …)`: `ItemAnchor/LeftHand` for Heavy only, and the `Pouch` subtree and `PouchDraw` for Slingshot only.
- For each used target, write the resolved local pose (`GripPoses.TryResolve`). Multiply slot-frame positions by `scale`. A missing required target writes identity.
- Record `{Defined, Stored, Layer}` per target for dirty checks.
- An undefined hint records `Defined = false`.
- Skip the whole call when `Locked && !force` and the key (holdSlot, item, avatar, view, scale) matches the last applied key.

**`Anchor`**
- The `ItemAnchor` local pose is the lerp/slerp of the `HoldPose` and `ChargePose` local poses by `charge`.
- If `pitch != 0`, rotate the anchor's world pose about `pivot` around `Frame.rotation * Vector3.right` by `pitch * charge` degrees.
- The `Pouch` local position is `Lerp(restPouch, PouchDraw.localPosition, draw)` and its local rotation is `Slerp(identity, PouchDraw.localRotation, draw)`.

**`Hint`**
- Resolve both ends of the hold/charge pair.
  - Both defined: world is the lerp of the two local positions by `charge`, transformed by the slot, and the weight is 1.
  - One defined: use that end, with weight `charge` or `1 − charge` as appropriate.
  - Neither defined: return false.
- Apply the same pitch rotation as `Anchor`.

**`TryAuthored`**
- `stored` is the transform's local pose, with the position divided by `scale` for slot-frame targets.
- Dirty means the value differs from the applied one by more than 1e-5 m or 0.01°. For an undefined hint, dirty means it has moved.

### 3.2 `HeldItemPresentationState` (`Runtime/Player/HeldItemPresentationState.cs`)

**Remove**
- The arm-slot machinery: `Slots`, `classes`, `weights`, `startWeights`, `Claim`, `ClassWeight`, `ModeWeight`, `chargeClass` and `spread`.
- `HeavyFrameWeight` and `ArmWeight`, along with the `fallback` transform and its `Attachment` body.
- `ItemPose()`, `GripFrame`, `RefreshGrips`, `selectedGrip` and `actionGrip`.
- `HeavyBody()`; `TryBody` always uses `Body(settings)`.
- `AuthoringPose`, `Authoring`, `SubmitAuthoring`, `Override` and all swivel and round-trip lines.

**Keep, with retyping**: input, action and age handling, `Watch` for `ItemDefinition` and `HoldSlot`, `RecoveryFinished`, `CanShowHeldItem`, `ReadyForUse`, the pending-release queries, `ReleaseSample`, `StageRelease`, `TryPebbleDeparture`, `ReleaseSubmitted`, `FindProjectile`, `StartFollow`, `SubmitFollow`, `BeginReturn`, `Readout`, `InHandReach`, `Shift` and `CommitItem`.

**Add**
```csharp
private readonly GripSlotRig rig;             // created in the constructor with host
private Transform rightHint, leftHint;        // standalone, beside target/leftTarget
private GripSlotRig.Slot selectedSlot;
private float holdWeight, leftWeight, grab, draw;
// switch blend source (frame-local): palms, hints, hint weights, hand weights; blend clock reuses blendStart/blendDuration
// follow hints (frame-local), captured in StartFollow
internal Transform Attachment => (selectedSlot ?? rig.Hand).ItemAnchor;
internal float HoldWeight => running && input.CanEquip ? holdWeight : 0f;
internal void PoseHands(AvatarBinding binding, Pose? frame = null);
#if UNITY_INCLUDE_INSTRUMENTATION
internal GripAuthoringPhase AuthoringPhase;          // Live / Hold / Charged
internal bool AuthoringLocked { set => rig.Locked = value; }
internal bool TryAuthored(GripTarget target, out Transform transform, out Pose stored, out GripLayer layer, out bool dirty);
internal void ReapplyAuthored();                      // forced Apply on selectedSlot
#endif
```

**Behaviour**

*`SelectionChanged` / `RefreshContent` / `Bind`*
- Pick `selectedSlot = rig.For(selectedData.Mode)`.
- Call `rig.Apply(selectedSlot, …)`. The avatar is `boundBinding.Id`, or `avatar.Resolved.Id` when unbound. The scale is `GripPoses.Scale(settings)`.
- On a selection change, `BeginBlend` captures the standalone palm and hint transforms, their hint weights and the current hand weights relative to `rig.Frame`. The duration is the new slot's `ReturnBlendDuration`. On dequip, use the previous duration.
- The WorldItem is reparented by the existing `registry.RefreshHolder` → `WorldItem.ApplyHeldAttachment` path (`WorldItem.cs` L383–405). That code doesn't change.

*`ActionChanged`* keeps today's structure without the slot claims.
- Charging → Idle (cancel): `startCharge = charge` and blend it to 0 over `actionData.ChargeDuration`, using `RecoverySeconds` for the slingshot, grab and draw.
- Recovering (non-slingshot): as today.
- Slingshot recovery: as today, plus the left-hand fade.

*`Advance`* (was `RefreshArms`) computes `charge`, `grab`, `draw`, `holdWeight` and `leftWeight`.
- **Charging, not slingshot**: `charge = Ease(age / ChargeDuration)`, or 1 when the duration is ≤ 0.
- **Charging, slingshot**: `charge = Ease(age / ThrowChargeTime)`. `grab = SmoothStep(age / GrabSeconds)`, or 1 when `GrabSeconds` is ≤ 0. `draw = Ease(Clamp01((age − GrabSeconds) / (ThrowChargeTime − GrabSeconds)))`, or 1 when the denominator is ≤ 0.
- **Cancel blend**: `charge`, and for the slingshot `grab`/`draw`, go to 0 with `SmoothStep` over the cancel duration.
- **Non-slingshot recovery**: `charge` holds `releaseCharge` during Follow/Pause and blends to 0 with the return progress, as today (L380–384).
- **Slingshot recovery**: `t = SmoothStep(age / RecoverySeconds)`, `charge = Lerp(releaseCharge, 0, t)`, `grab = 1 − t`, `draw = 0`.
- **Hold weight**: `holdWeight` is 1 when the selected item is showable and 0 otherwise, blended over the switch blend. `leftWeight` is `holdWeight` for Heavy, `grab` for Slingshot and 0 for Hand.
- **Authoring phase Hold**: `charge = grab = draw = 0`, no blends. **Charged**: all three are 1.

*`PrepareHands(binding, body)`* runs before animation. It keeps today's flow, then submits:
- **Recovering (non-slingshot)**: `SubmitFollow` as today.
  - When a selected item is showable, the destinations are the world poses of the selected slot's `RightHand` and, for Heavy, `LeftHand` from the last `PoseHands`, with weight 1. Otherwise pass null with weight 0, as today.
  - Pass the follow hints and their weights through `HandHoldPresentation.Submit`.
- **Otherwise**: `Set(RightHand, Item, target, holdWeight, holdWeight, MaximumReach, fingers, rightHint, rightHintWeight)`.
  - Left for Heavy: `leftTarget` at `leftWeight`, using the item fingers.
  - Left for Slingshot: `leftTarget` at `leftWeight`. Fingers are `clips.GripFingers` while charging and `clips.OpenFingers` in recovery.
  - Hand mode: clear the left target.
- `fingers` is `data.Fingers`, falling back to `clips.GripFingers`.

*`PoseHands(binding, frame)`* runs after animation and before IK in third person. In first person, `PlayerHandPresentation` calls it directly.
1. **Frame**: use the given `frame` (first person). Otherwise, with a binding, use `(Hips bone position, binding.Animator.transform.rotation)`. Otherwise use `(lastBody.Hips, lastBody.Rotation)`. Then call `rig.Place(frame)`.
2. **Pitch**: third person with `AuthoringPhase == Live` only. `pitch = Clamp(placement.LookPitch, −40, 50)`, where `placement` is `avatar.Input` when bound and `input.Placement` otherwise.
   - Pivot for Heavy: `binding.Body.position`, falling back to `lastBody.Center`.
   - Pivot otherwise: the RightUpperArm bone, falling back to `lastBody.Shoulder`.
3. **Anchor**: `rig.Anchor(selectedSlot, anchorCharge, draw, slingshot ? slingshot.RestOffset : zero, pitch, pivot)`. `anchorCharge = charge` only while the selected item is the action item and is charging or cancel-blending; otherwise it is 0.
4. **Not in follow**:
   - Hand world poses come from `rig.Target(slot, RightHand)` and `LeftHand`.
   - Blend them against the captured switch source: `Blend(Frame ∘ source, world, SmoothStep(progress))`. A hand the new slot doesn't use keeps its captured pose while its weight goes to 0.
   - Write the results into `target` and `leftTarget`.
   - Compute hints with `rig.Hint`, blend them against the source the same way, and write them into `rightHint` and `leftHint`.
5. **In follow**: hints come from the retained frame-local values during Follow/Pause. During Return, they lerp to the destination item's hold hints, or to weight 0 when none, using the return blend.
6. **Authoring, locked**: undefined hint transforms of `selectedSlot` that haven't moved are placed at `AvatarArmIK.AutoHint(...)` from the bound shoulders and targets.

*`StartFollow`*
- `rightInItem` and `leftInItem` come from the action item's resolved `RightHand` and `LeftHand` (`GripPoses.TryResolve` with `actionDefinition`), not from the evaluated palms.
- The follow hints are the standalone hint transforms' last world positions, converted into `rig.Frame`-local space.
- The start palms still come from `prepared`, `committed` or the binding, as today.

*`CommitHands(binding)`* runs after IK.
- The item pose is `selectedSlot.ItemAnchor`'s world pose.
- **First-person clearance** (L512–521):
  - The sphere is `data.Heavy ? data.Sphere : radius`.
  - Apply `correction` to `rig.Frame.position`. The item and the anchor children move with it, so `CommitItem(item)` is idempotent.
  - Return `correction` for `TranslateRig`, as today.
- **Requested vs evaluated poses**: requested = the `target` and `leftTarget` poses. Evaluated = `binding.Palm`, or the requested poses when there is no binding.
- **Readout**:
  - `HasLeft = Mode != Hand`.
  - The unreachable flags apply to every used hand, not only TwoHand.
- **Committed sample**: `ItemPose` is the corrected item pose. `Progress` is unchanged (L545).
- `slingshot.Evaluate(item, state, age, draw, recovery, rig.For(Slingshot).Pouch.position)`.

*`Shift(correction)`* (pebble): replace the `fallback` shift with `rig.Frame.position += correction`.

*`Dispose`*: `rig.Dispose()`, and destroy the hint transforms. `PlayerHeldItemPresentation.StopPresentation` already detaches held WorldItems first (L68).

### 3.3 `PlayerHeldItemPresentation`
- Remove `HeavyFrameWeight` (L25). Replace `ArmWeight` (L26) with `internal float HoldWeight => State?.HoldWeight ?? 0f;`.
- Add `internal void PoseHands(AvatarBinding binding, Pose? frame = null) => State?.PoseHands(binding, frame);`.
- In `LateUpdate` L154, call `PoseHands(null)` between `PrepareHands` and `CommitHands`.
- `Attachment` (L24) now returns the `ItemAnchor`.

### 3.4 Slingshot visuals (`Runtime/Items/SlingshotPresentation.cs`)
- `Awake`: add `internal Vector3 RestOffset { get; private set; }` = `Quaternion.Inverse(transform.rotation) * (RestCenter.position − transform.position)`. This is metres in the item frame.
- `Evaluate(Pose frame, ItemActionState state, double age, float draw, float recovery, Vector3 pouch)`: replace L37–38 with `Vector3 held = pouch;`. Everything else is unchanged, so bands and the pebble render to the `Pouch` and `DepartureCenter` stays the pouch.
- `PlayerHeldItemPresentation.TryPreparePebble` (L180–194) is unchanged.

---

## 4. First-person integration (`Runtime/Player/PlayerHandPresentation.cs`)
- **L32–43**:
  - Replace `HeavyFrameWeight` with `CarryFrameWeight => carryHands.Releasing ? carryHands.Frame(CarrySettings, …) : CarryHolding ? 1f : 0f`.
  - Rename `HeavyBody` to `CarryBodyFrame`.
  - Use `CarryFrameWeight` in `TryBody` (L415) and `Place` (L496).
  - Keep passing it to `rig.Place` as `bodyWeight`.
- **Contacts**:
  - Replace the fields at L53–57 (`contactWeights`, `contactBlends`, `contactReaches`, `contactFingers`, `contactTargets`) with `HandContactPresentation contacts` (section 5.2). Create it in `StartPresentation` and dispose it in `StopPresentation`.
  - Keep `leftContact` and `rightContact`. `StopPresentation` no longer clears them, so the interaction survives a restart.
- **Public API**:
```csharp
public void BeginInteraction(AvatarHandContact left, AvatarHandContact right) { leftContact = left; rightContact = right; }
public void EndInteraction() { leftContact = rightContact = null; }
internal AvatarHandContact LeftContact => leftContact;
internal AvatarHandContact RightContact => rightContact;
```
- **`ContextChanged`**: delete L136–144, the driver/cart binding and `CacheContacts`. Delete `BindContact`, `CacheContacts` and the `CacheContacts` call in `IdentityResolved` (L127).
- **`Contacts(dt, settings)`** (L366–385) becomes `contacts.Submit(avatar.HandTargets, leftContact, rightContact, id, owner.IsOwner, avatar.Registry.Animations.GripFingers, dt)`. The `id` is the evaluating binding's id; use `avatar.Resolved.Id` when unbound. The `DriverHandOffset` offset is gone.
- **Third-person posing**: subscribe `avatar.PosingHands += PoseRemote` in `StartPresentation` and unsubscribe in `Stop`.
```csharp
private void PoseRemote(AvatarBinding binding) { if (owner.IsOwner) return; if (avatar.Binding == null || binding == avatar.Binding) held.PoseHands(binding); }
```
- **First-person frame**: add `private Pose FirstPersonFrame()`.
  - With a rig, it is `(active.transform.position, active.transform.rotation)`.
  - Otherwise it is `CameraPose` + `ShoulderOffset` + `FirstPersonPlacementOffset` − (the `FirstPersonGenerated` shoulder midpoint × `Scale`), with the camera rotation.
- **`Evaluate`**: add `held.PoseHands(binding, FirstPersonFrame())` directly after `held.PrepareHands(...)`, both on the candidate path (after L452) and on the main path (after L475).
- **`Place`** L485: `contact` = `leftContact || rightContact`, as today.
- **`HoldBob`** L510: `held.ArmWeight` → `held.HoldWeight`.
- **`AdvanceFreeHands`** L542: `1f - rig.PoseWeight(right)` → `1f`.

---

## 5. World contacts

### 5.1 Driver seating (`Runtime/Player/PlayerSeating.cs`)
Add:
```csharp
private PlayerHandPresentation hands;
private void RaiseContextChanged()
{
    if (!hands) hands = GetComponent<PlayerAvatarPresentation>().Hands;
    bool driving = IsDriver && !TransitionPending && !AwaitingReference && !PlacementPending;
    if (driving) hands.BeginInteraction(Cart.Presentation.LeftHandContact, Cart.Presentation.RightHandContact);
    else hands.EndInteraction();
    PresentationContextChanged?.Invoke();
}
```
Replace the four `PresentationContextChanged?.Invoke()` calls: L137 (`player.RaiseContextChanged()`), L168, L192 and L258.

### 5.2 `Runtime/Avatars/HandContactPresentation.cs` (new; shared)
This is used by `PlayerHandPresentation` (owner first person and remote third person) and by `GripObserverPreview` (third-person authoring).
```csharp
internal sealed class HandContactPresentation : IDisposable
{
    internal HandContactPresentation(Transform host);   // Left/RightContactPalm, Left/RightContactHint under host
    internal void Submit(AvatarHandTargets targets, AvatarHandContact left, AvatarHandContact right,
        AvatarId avatar, bool firstPerson, AnimationClip defaultFingers, float dt);
    internal void Clear(AvatarHandTargets targets);
    public void Dispose();
}
```
- Per hand, this is today's `Contacts` body. When the bound contact reference changes, re-cache `BlendTime`, `MaximumReach` and `Fingers` (falling back to `defaultFingers`). This replaces `CacheContacts`.
- Palm = `contact.Palm(avatar, firstPerson)`. Hint = `contact.TryHint(...)`, with weight 1 when resolved and 0 otherwise.
- The weight moves toward 1 or 0 over `BlendTime`. Call `targets.Set(hand, Contact, palm, w, w, reach, fingers, hint, hintWeight)`. When the contact is gone and the weight reaches 0, call `Clear`.

### 5.3 Remove driver hand offsets
- `AvatarSettings`: delete `OverrideDriverHandOffset`, `DriverHandOffset` (L29–32) and the `DriverHandOffset` term in validation (L138).
- `AvatarDriverPose`: delete `DefaultHandOffset` (L95) and `HandOffset()` (L101–102).
- `AvatarDriverSettings`: delete `DriverHandOffset` (L11).

---

## 6. Authoring

### 6.1 `AvatarHandContact` instrumentation (`#if UNITY_INCLUDE_INSTRUMENTATION`)
```csharp
internal static bool? LiveView;            // the view whose presenter reads the live transforms
internal Transform AuthoredHint { get; }   // lazily created "<name> Elbow Hint" under HintSpace
internal void ApplyAuthored(AvatarId avatar, bool firstPerson);
internal bool TryAuthored(GripTarget target, AvatarId avatar, bool firstPerson, out Transform transform, out Pose stored, out GripLayer layer, out bool dirty);
internal void CopyPoses(AvatarHandContact source);   // deep-copies Poses and AvatarPoses
```
- `ApplyAuthored` writes the resolved palm into `transform.localPosition/localRotation` and the resolved hint into `AuthoredHint`. When the hint is undefined, it goes 0.3 m below the palm in the hint frame. The applied values are recorded for dirty checks.
- `TryAuthored` covers the `ContactPalm` and `ContactElbow` targets.
- When `LiveView == firstPerson`:
  - `Palm()` returns this transform's world pose.
  - `TryHint()` returns `AuthoredHint`, with weight 1 when it is defined or has moved.

### 6.2 `GripAuthoringScene` (`Runtime/Authoring/GripAuthoringScene.cs`)

**Remove**: `GripPoseCapture`; `TryGrip`, `TryItem`, `TryPouch`, `TryRequestedPalm`, `BeginPalmDrag`, `DragPalm`, `SetSwivel`, `EndPalmDrag`, `BeginEdit`, `Seed`, `EndEdit`; the `AuthoringPose` usage in `Apply`; `editState`, `editBinding`, `editFirstPerson` and `dragRight`.

**Add**
```csharp
public enum GripAuthoringMode { HeldItem, WorldContact }
public GripAuthoringMode Mode { get; private set; }
public bool FirstPerson { get; private set; }
public void SetMode(GripAuthoringMode mode);
public void SetView(bool firstPerson);
public HoldSlotMode SlotMode => SelectedDefinition ? SelectedDefinition.HoldMode : HoldSlotMode.Hand;
public IReadOnlyList<GripTarget> PhaseTargets();
public bool TryTarget(GripTarget target, out Transform transform, out Pose stored, out GripLayer layer, out bool dirty);
public void Reapply();
public AvatarHandContact LeftContact { get; }  public AvatarHandContact RightContact { get; }
public void EnterSeat();  public void ExitSeat();
```
- **`ApplyPhase`**: each state (the owner's, the observer's and the strip's) gets `AuthoringPhase = Phase`.
  - The edited state is `FirstPerson ? held.State : observer.State`. It gets `ReapplyAuthored()`, then `AuthoringLocked = Phase != Live`.
  - Every other state gets `AuthoringLocked = false`.
  - Re-run this on `SelectItem`, `SelectAvatar`, `SetView` and `LocalBound`.
- **`PhaseTargets`** (held mode):
  - Hold: `HoldPose`, `RightHand`, `RightElbowHold`, plus `LeftHand` and `LeftElbowHold` for Heavy.
  - Charged: `ChargePose`, `RightHand` and `RightElbowCharge`. Heavy adds `LeftHand` and `LeftElbowCharge`. Slingshot adds `LeftHand`, `PouchDraw` and `LeftElbowCharge`.
  - Live: every target the mode uses.
- **`TryTarget`** delegates to the edited state's `TryAuthored`. **`Reapply`** calls the edited state's `ReapplyAuthored`.
- **World-contact mode (`SetMode(WorldContact)`)**:
  - Cancel and dequip.
  - Find the cart once with `FindFirstObjectByType<GolfCartNetwork>()`.
  - Set `observer.SetLateral(0)` and `observer.ContactSource = avatar.Hands`.
  - Hide the strip GameObjects.
  - Set `AvatarHandContact.LiveView = FirstPerson` and call `ApplyAuthored(SelectedAvatar, FirstPerson)` on both steering-wheel contacts.
- **Back to `HeldItem`**: reverse all of that, set `LiveView = null` and call `ExitSeat()`.
- In contact mode, `SetView` and `SelectAvatar` update `LiveView` and re-apply the contacts.
- **Seat**: `EnterSeat` calls `seating.Request(cart, 0)` and `ExitSeat` calls `seating.RequestExit()`, using the local player's `PlayerSeating`. The contacts are `cart.Presentation.LeftHandContact` and `RightHandContact`.
- **`Throw`**: `ThrowAfter(Mathf.Max(item.ThrowChargeTime, item.ChargePoseDuration))`.
- **`StripReadouts`**: show every mode. Include L only when the mode isn't Hand.
- **`OnDestroy`**: also reset `LiveView = null`.

### 6.3 `GripObserverPreview`
- Add `internal void SetLateral(float value) => lateral = value;`.
- Add `internal PlayerHandPresentation ContactSource;` and a `HandContactPresentation contacts`, created in `Initialize` and disposed in `OnDestroy`.
  - In `Prepare`, when `ContactSource` is set, call `contacts.Submit(presentation.HandTargets, ContactSource.LeftContact, ContactSource.RightContact, binding.Id, false, registry.Animations.GripFingers, dt)`.
  - Otherwise call `contacts.Clear(...)`.
- Subscribe `presentation.PosingHands += Pose` and unsubscribe in `OnDestroy`. `Pose` does `if (presentation.Binding == null || presentation.Binding == binding) State.PoseHands(binding);`.

### 6.4 `GripAuthoringWindow` (`Editor/GripAuthoringWindow.cs`)

**Remove**: `EditLayer`/`layer`; `HandPosePanel`, `Swivel`, `ItemOffsetPanel`, `AvatarOffsetPanel`, `OffsetFields`, `SetAvatarGrip`, `AvatarGrip`, `EditItem`; the `SceneGUI` handle code (`PalmGizmo`, `OffsetGizmos`, `Gizmo`, `BakeEdit`, `Offset`); `HideTools` and its fields; and `dragging` and `gizmoStart`.

**Keep**:
- `StartAuthoring`, `Save`/`Revert` and look-through.
- `AssetsChanged`, `PlayModeChanged` and `HideOwner` (held mode only).
- `UndoRedo`: notify items, their `HoldSlot`s and the tracked contacts.
- `Modified`: add `HoldSlot` and `AvatarHandContact`.

**Layout**:
- **Toolbar**: Start Authoring, Frame, look-through, spacer, Save (n), Revert. Frame frames `Selection.activeTransform` if it is a listed target, otherwise the `RightHand` target (or the right contact palm).
- **Selectors**: Mode radio; item ◀ label ▶ over the valid `items.Items`; avatar ◀ label ▶; View radio; Phase radio (held mode only). Each selector calls the matching scene setter and rebuilds.
- **Target rows**: rebuilt when the mode, item, phase or view changes, and refreshed in `Update`. Held rows come from `scene.PhaseTargets()`. Contact rows are left palm, left elbow, right palm and right elbow. Each row has:
  - The target name, the source layer label, and "●" when dirty.
  - **Select**: `Selection.activeTransform = transform`.
  - **Clear**: remove the value at the source layer. Disabled for a Slot-layer pose target and for `None`.
  - **Copy to other view**: only for `RightHand`, `LeftHand` and `PouchDraw`, and disabled while dirty.
- **Save buttons**:
  - Held: Save for avatar, Save as item default, Save as slot default.
  - Contact: Save for avatar, Save as contact default.
  - Disabled in the Live phase in held mode.
- **Readouts**: the existing label, held mode only.
- **Actions**: unchanged.

**Held save** — for each dirty row:
```
table = layer switch { Avatar => GripPoses.Ensure(item.AvatarGripPoses, avatar), Item => item.GripPoses, Slot => item.HoldSlot.Defaults };
GripAuthoringAssets.Edit(owner, "Save grip", () => table.Set(target, firstPerson, stored));
```
Then call `scene.Reapply()`. Clear and Copy use the same `Edit` path, then call `Reapply()`.

**Contact save**:
- The asset is `PrefabUtility.GetCorrespondingObjectFromOriginalSource(liveContact)`. If it is null, log "Contact is not a prefab instance" and stop.
- Edit the asset's `AvatarPoses` or `Poses` through `GripAuthoringAssets.Edit`.
- Call `GripAuthoringAssets.Pair(asset, live)`, then `live.CopyPoses(asset)` and `live.ApplyAuthored(...)`.

### 6.5 `GripAuthoringAssets` (`Editor/GripAuthoringAssets.cs`)
- **Remove**: `Folder`, `Bake`, `CopyHoldToCharged`, `Clear`, `Slot`, `Finish`, `Assign` and `created`.
- **Add** `public static void Edit(Object asset, string undo, Action change)`. It calls `Touch`, `Undo.RecordObject`, `change`, `SetDirty`, then `NotifyContentChanged` for a `HoldSlot` or `ItemDefinition`.
- **Snapshot**:
  - For a `Component` (a prefab-asset contact), store `EditorJsonUtility.ToJson(component)`. `RevertAll` restores it with `EditorJsonUtility.FromJsonOverwrite`.
  - Other assets keep `Instantiate`/`CopySerialized`.
- **SaveAll / RevertAll**: for a `Component`, persist with `PrefabUtility.SavePrefabAsset(component.transform.root.gameObject)`. Other assets keep `SaveAssetIfDirty`.
- **`Pair(AvatarHandContact asset, AvatarHandContact live)`**: after a revert, call `live.CopyPoses(asset)` for each pair. `Reset` clears the pairs.
- **`Notify`**: add `HoldSlot`.

### 6.6 Build guard (`Editor/GripAuthoringBuildPolicy.cs`, `GripAuthoringBuildGuard.PrepareForBuild`)
- Change `item.HoldClass` to `item.HoldSlot`, and the message to "Items without a Hold Slot".
- Add a check over every distinct `HoldSlot` referenced by an item or by `CarryHold`. For both views, every `GripPoses.Required(slot.Mode, t)` must pass `slot.Defaults.TryGet`. Otherwise throw `BuildFailedException`, listing `slot: target (FP/TP)`.

### 6.7 `ItemSetup` (`Editor/ItemSetup.cs`)
- L14: rename the path constant to `DefaultHoldSlotPath`; the path itself is unchanged. L94: set `definition.HoldSlot`.
- `GenerateHeldOffset(GameObject root, ItemDefinition definition, Quaternion palm)`:
  - Keep the bounds math (L248–276), with the `palm` parameter replacing L249.
  - Write the item-default `RightHand` = `new Pose(offset, palm)` for third person. Write first person only when it is missing or equals third person, which preserves today's "linked" rule.
  - Return false for Heavy.
- L110 disappears. `CreateDefinition` passes `Quaternion.Inverse(root.transform.localRotation)` and the potion path passes `Quaternion.identity`.
- The Recompute button (L59–64) becomes "Recompute Right Hand". It is disabled for Heavy and passes the existing third-person item-default `RightHand` rotation, or identity.

---

## 7. Integration checklist (files touched)
- **Runtime/Items**: `GripPoses.cs` (new), `HoldSlot.cs` (renamed), `ItemDefinition.cs`, `SlingshotDefinition.cs`, `ItemRegistry.cs`, `WorldItemRegistry.cs`, `HeldItemPose.cs`, `SlingshotPresentation.cs`; delete `GripOffset.cs`.
- **Runtime/Avatars**: `AvatarHandTargets.cs`, `AvatarArmIK.cs`, `AvatarAnimationGraph.cs`, `AvatarInstance.cs`, `AvatarPresentation.cs`, `LocalFirstPersonHands.cs`, `HandHoldPresentation.cs`, `AvatarHandContact.cs`, `HandContactPresentation.cs` (new), `AvatarSettings.cs`, `AvatarDriverSettings.cs`; delete `AvatarArmLayers.cs`.
- **Runtime/Player**: `GripSlotRig.cs` (new), `HeldItemPresentationState.cs`, `PlayerHeldItemPresentation.cs`, `PlayerHandPresentation.cs`, `PlayerSeating.cs`.
- **Runtime/Authoring**: `GripAuthoringScene.cs`, `GripObserverPreview.cs`.
- **Editor**: `GripAuthoringWindow.cs`, `GripAuthoringAssets.cs`, `GripAuthoringBuildPolicy.cs`, `ItemSetup.cs`.
- **No change needed**: `WorldItem.ApplyHeldAttachment` (it parents under `Attachment`), `PlayerEquipment`, `ItemReleaseClearance`, `GripReachReadout`, `AvatarFingerLayers`, `AvatarPalmCalibration`, `GripAuthoringPersistence`.
- **No clean-up needed**: `Assets/Art/Animations/HeldPoses` doesn't exist.

## 8. Content changes (user)
1. Add a `GolfCart` prefab instance to `Assets/Scenes/AvatarPresentationDemo.unity`.
2. In `Assets/Game/Prefabs/GolfCart.prefab`, set `HintFrame` on `LeftHandContact` and `RightHandContact` (both children of the steering wheel) to the cart body, for example `Graphics`.
3. After implementation, author:
   - Slot defaults for `Regular`, `Heavy` and `Slingshot` in both views. The build guard fails until this is done.
   - Item defaults and avatar overrides as needed.
   - The steering-wheel contacts in both views.
   - Existing `ThirdPersonGrip`/`FirstPersonGrip`/`AvatarGrips`/`PouchOffset` values are dropped, not migrated.
