# Hand Grip IK Spec

## Summary

Held items, charge poses, post-throw hand follow, the slingshot draw, and world interactions (golf cart steering wheel) are driven entirely by explicit IK targets. Arm pose AnimationClips are removed; hand position, forearm, upper arm and clavicle are solved from authored target transforms. Finger shapes remain AnimationClips.

Every authored target is a real Transform that a designer selects in the Scene view, moves with Unity's native Move/Rotate tools, and saves with one button, per item, per avatar, per view (first person / third person), with shared defaults.

## Vocabulary

| Term | Meaning |
|---|---|
| **Slot** | The frame a held item lives in. Two per view: **Hand slot** (regular items, slingshot) and **Heavy slot** (two-handed items). |
| **Hip follower** | Third-person slot frame: position of the avatar's `Hips` bone, rotation from the avatar's body yaw. |
| **First-person rig frame** | First-person slot frame: the `LocalFirstPersonHands` rig root pose (camera-locked), ignoring the rig's scale. |
| **ItemAnchor** | Runtime transform under a slot; the held item is parented to it. Its pose is the lerp of HoldPose → ChargePose. |
| **Hand target** | An authored **palm** pose (palm calibration from `AvatarPalmCalibration`), not a wrist pose. |
| **Elbow hint** | An authored pole position that defines the elbow's bend plane. |
| **HoldSlot** | The renamed `HoldClass` asset: slot type, slot-level defaults, finger default, follow/return timings. |
| **Layer** | One level of the default chain: slot default → item default → item × avatar override. |
| **Charge progress** | 0–1 value that drives every Hold → Charge lerp. |
| **World contact** | An `AvatarHandContact`: a hand target owned by a world object for the duration of an interaction. |

## Slot frames

### Third person
- Both slots are parented to the **hip follower**: each frame, after animation and before arm IK, it takes the world position of the `Hips` bone and the avatar's body yaw rotation. Gait bob carries into the item and hands; hip twist, pitch and roll do not.
- Seated and carried placements use the same rule (hips position, body rotation).
- If the avatar has no binding yet, the slot frame falls back to the placement-derived body frame (`HeldItemBodyFrame`).

### First person
- Both slots (Hand and Heavy) ride the **first-person rig frame**: camera-locked, pitching with look.
- The first-person rig's heavy body-frame blend is removed; heavy items pitch with the camera like regular items.
- While driving, the rig still blends onto the cart seat frame (existing `PlacementBlendTime` behaviour).

### Presentation per player
- The owner's held item is presented on the first-person slots; every other client presents it on the third-person slots.
- The held `WorldItem` is parented under the presenting **ItemAnchor** with identity local pose (replacing today's `HeldItemFallback` attachment proxy).

## Slot structure

Identical in both views. Only the targets the slot's mode uses exist.

```
Slot (hip follower / first-person rig frame)
├─ HoldPose                 authored — ItemAnchor placement at charge 0
├─ ChargePose               authored — ItemAnchor placement at charge 1
├─ RightElbowHold           authored ┐
├─ RightElbowCharge         authored │ lerped by charge progress
├─ LeftElbowHold            authored │
├─ LeftElbowCharge          authored ┘
└─ ItemAnchor               runtime = lerp(HoldPose, ChargePose, charge)
   ├─ Item                  the held WorldItem
   ├─ RightHand             authored palm, relative to the item
   ├─ LeftHand              authored palm, relative to the item (Heavy)
   ├─ PouchDraw             authored, relative to the item (Slingshot)
   └─ Pouch                 runtime = lerp(RestCenter, PouchDraw, draw) (Slingshot)
      └─ LeftHand           authored palm, relative to the pouch (Slingshot)
```

Hand targets are rigid on the item through Hold → Charge; only the ItemAnchor and the elbow hints lerp.

### Targets per slot mode

| Target | Hand | Heavy | Slingshot | Frame | Scaled by avatar size |
|---|---|---|---|---|---|
| HoldPose | ✓ | ✓ | ✓ | slot | ✓ |
| ChargePose | ✓ | ✓ | ✓ | slot | ✓ |
| RightHand | ✓ | ✓ | ✓ | item | – |
| LeftHand | – | ✓ | ✓ (pouch frame) | item / pouch | – |
| PouchDraw | – | – | ✓ | item | – |
| RightElbowHold / RightElbowCharge | ✓ | ✓ | ✓ | slot | ✓ |
| LeftElbowHold | – | ✓ | – | slot | ✓ |
| LeftElbowCharge | – | ✓ | ✓ | slot | ✓ |

- **Avatar size scaling**: slot-frame positions are stored for a reference avatar height (1.8 m) and multiplied at runtime by `VisualHeight / 1.8`. Item-frame and pouch-frame targets are plain metres (item geometry does not scale). Rotations are never scaled.

## Data model and resolution

### Layers
Every authored target resolves **independently**, per view, through:

1. **Item × avatar override** (on `ItemDefinition`, keyed by `AvatarId`) — sparse
2. **Item default** (on `ItemDefinition`, all avatars) — sparse
3. **Slot default** (on `HoldSlot`) — required for every pose target the mode uses

First-person and third-person values are fully independent; there is no fallback between views.

Elbow hints are optional at every layer. When no layer defines one, the IK uses an automatic "down and out" hint.

### HoldSlot (renamed HoldClass)
- `Mode`: Hand / Heavy / Slingshot (serialized values unchanged from `ItemHoldMode`: 0, 1, 2).
- Slot defaults: a pose per target per view.
- Default finger clip (used when the item has no `GripFingers`).
- Follow/return timings: `MaximumFollowDuration`, `EndPosePauseDuration`, `ReturnBlendDuration`, `FollowReachFraction`.
- Removed: `HoldClassView` (Hold/Charged clips, `HoldSpread`, `ChargedSpread`), `Spread()`, `ChargePoseDuration`.
- Existing assets `Regular`, `Heavy`, `Slingshot` are kept and retyped. `ItemRegistry.CarryHold` retypes to `HoldSlot` (player carry keeps reading its follow/return timings).

### ItemDefinition
- `HoldClass` field retypes to `HoldSlot`.
- `ChargePoseDuration` (seconds, per item; default 0.35) — visual Hold → Charge lerp time, independent of `ThrowChargeTime`.
- Item default table: sparse `(target, view) → pose`.
- Avatar override table: per `AvatarId`, sparse `(target, view) → pose`.
- `GripFingers` kept.
- Removed: `ThirdPersonGrip`, `FirstPersonGrip`, `AvatarGrips`, `Grip()`; `GripOffset` and `AvatarGripOffset` types are replaced by the new pose entries.

### SlingshotDefinition
- Removed: `PouchOffset`.
- Added: `PouchGrabSeconds` (default 0.15).

### AvatarHandContact (world contacts)
- Per hand: a palm target and an optional elbow hint.
- Layers: **contact default** → **contact × avatar override**, per view, per target, all stored on the component (i.e. in the owning prefab).
- Palm targets are relative to the contact's parent (the steering wheel, so hands turn with it). Elbow hints are relative to a configurable hint frame on the contact (the cart body for the steering wheel) so turning the wheel does not swing the elbows.
- Contact targets are not scaled by avatar size.
- Kept: `Hand`, `Fingers`, `MaximumReach`, `BlendTime`.

## Runtime behaviour

### Holding
- Hand targets are submitted to `AvatarHandTargets` with source `Item`: right hand for Hand slots; both hands for Heavy; right hand for Slingshot (left joins only while charging).
- Fingers: item `GripFingers` → `HoldSlot` default fingers. Slingshot left hand uses the registry `GripFingers`.
- **Equip from empty hands**: hands blend from free to the item targets over the slot's `ReturnBlendDuration`.
- **Switching items**: the item visual swaps instantly; hand targets and elbow hints blend from the old item's resolved targets to the new one's over the new slot's `ReturnBlendDuration`.

### Charging
- Charge progress = easeOutSine(age / item `ChargePoseDuration`). ItemAnchor and elbow hints lerp Hold → Charge by it.
- **Third-person aim pitch**: the charge contribution (ItemAnchor and elbow hints) is rotated about the body's right axis by clamp(look pitch, −40°, 50°) × charge progress. Pivot: right shoulder for Hand/Slingshot slots, shoulder midpoint for the Heavy slot. First person needs no pitch term (camera-locked frame).
- **Cancel**: charge progress lerps back to 0 over the item's `ChargePoseDuration`, starting from the current progress.
- **Release before full charge**: the throw leaves from the current partially charged pose.

### Throw and hand follow
- The release pose sent for a throw is the owner's first-person ItemAnchor pose at release, after the `ItemReleaseClearance` wall push (unchanged). Release progress is the charge progress.
- `HandHoldPresentation` runs Follow → Pause → Return, unchanged in behaviour. The hand-in-item offsets used while following are the resolved `RightHand` / `LeftHand` targets. Reach and linecast cut-offs use `FollowReachFraction`.
- Timings come from the thrown item's `HoldSlot`.
- Charge elbow hints stay active during Follow and Pause and blend out during Return.
- Return destination: free hands (IK weight → 0) when nothing is held, otherwise the next held item's resolved hold targets.

### Slingshot
1. **Holding**: slingshot on the Hand slot at HoldPose; right hand on the handle; left hand free.
2. **Charge start — grab**: over `PouchGrabSeconds`, the left hand blends (weight 0 → 1) onto `Pouch ∘ LeftHand` with the pouch still at `RestCenter`.
3. **Draw**: draw = ease((age − `PouchGrabSeconds`) / (`ThrowChargeTime` − `PouchGrabSeconds`)), clamped to 0–1. The pouch moves `RestCenter` → `PouchDraw` in the slingshot's frame; the left hand rides the pouch.
4. The ItemAnchor's Hold → Charge lerp runs over the whole `ThrowChargeTime` (the slingshot does not use `ChargePoseDuration`).
5. Bands and the loaded pebble render from the forks to the `Pouch` position. Pebble launch centre is the `Pouch` position (existing clearance checks unchanged).
6. **Release**: existing band recoil; the left hand switches to open fingers and returns to free over `RecoverySeconds`; the ItemAnchor lerps Charge → Hold over `RecoverySeconds`. No projectile follow.

### World interactions
- `PlayerHandPresentation` exposes `BeginInteraction(AvatarHandContact left, AvatarHandContact right)` and `EndInteraction()`. Interaction/state scripts call these for the interaction's duration; either hand may be null.
- Contacts submit through the existing `Contact` source (highest priority) with the contact's `BlendTime`, `MaximumReach` and `Fingers`.
- Golf cart: driver seating calls `BeginInteraction` with the steering wheel's left/right contacts and `EndInteraction` on leaving; this replaces the hard-coded cart binding in `PlayerHandPresentation.ContextChanged` and the `DriverHandOffset` offset.
- Adding a new interaction = add `AvatarHandContact` children to the object, author them, call `BeginInteraction` / `EndInteraction` from its script.

### IK (`AvatarArmIK`)
- Two-bone solve (upper arm, forearm) to the palm target, with the existing soft reach.
- **Pole hints**: the elbow lies in the plane through the shoulder, the wrist target and the resolved elbow hint. With no hint, an automatic hint below and outward of the arm (body frame) is used. The bend plane no longer comes from the animated arm.
- **Clavicle**: automatic, unauthored. When the target distance exceeds ~85 % of arm length, the `LeftShoulder` / `RightShoulder` bone rotates toward the target by a small capped angle before the two-bone solve, scaled by the hand's IK weight.
- Applies to every hand source (Free, Item, Carry, Contact).
- `AvatarHandTargets` source priority (Free < Item < Carry < Contact) is unchanged.
- Fingers remain on `AvatarFingerLayers`.

### Networking
No new network data. Everything is derived locally from item action state (`Idle` / `Charging` / `Recovering`), action age, release progress, look pitch, and the authored data.

## Authoring

### Scene
- Play-mode scene `AvatarPresentationDemo` (solo session started automatically), as today.
- The authored targets for the selected view are live Transforms in the Hierarchy: first person under the local player's first-person rig, third person under the observer preview's hip follower.
- In the Hold and Charged phases, runtime writes to authored transforms are suspended so native Move/Rotate drags stick. Hold freezes charge progress at 0; Charged freezes it at 1 (slingshot: grab complete, draw 1). Live runs normal gameplay.
- The observer (selected avatar, third person) and the strip (every other avatar, third person) always show their resolved results, updating as data changes.

### Window
- **Toolbar**: Start Authoring, Frame, Save, Revert.
- **Selectors**: Mode (Held item / World contact), item ◀ ▶, avatar ◀ ▶, View (third / first person), Phase (Live / Hold / Charged).
- **Target list**: one row per authored target of the current slot mode and phase: name, source layer (Slot / Item / Avatar), dirty marker, Select, Clear (removes the value at the chosen layer).
- **Save buttons** (apply to dirty targets only): **Save for avatar**, **Save as item default**, **Save as slot default**.
- **Copy to other view** for item-frame and pouch-frame targets (RightHand, LeftHand, PouchDraw).
- **Reach warning** per hand from `GripReachReadout` (requested vs reached error, unreachable flag), for the edited rig and each strip avatar.
- **Actions**: Equip, Dequip, Hold, Release, Throw (charge to full, then release), Cancel, Drop; F2 toggles walking.
- Asset edits use the existing snapshot / revert / save mechanism in `GripAuthoringAssets`, extended to `HoldSlot` and prefab assets.

### World contact mode
- The authoring scene contains a `GolfCart` instance.
- Enter / Exit seat puts the local player in the driver seat.
- Game view shows first person; the Scene view shows the local player's own third-person body, which evaluates hand IK while authoring. The observer and strip are hidden; avatars switch with ◀ ▶.
- Selectable targets: each contact's palm transform and elbow hint. Save buttons are **Save for avatar** and **Save as contact default**.
- Saves write to the source prefab asset of the contact.

### Build guard
- Existing: every item must reference a `HoldSlot`.
- Added: every `HoldSlot` must define slot defaults for all pose targets its mode uses, in both views.

## Removed

- `AvatarArmLayers`, `AvatarArmPose` (arm clip layers, slot blending, charge/pitch fields)
- `HoldClassView`, `HoldClass.Spread`, and the clips in `Assets/Art/Animations/HeldPoses`
- `AvatarHandTargets.TwoHandAnchor` / `SetAnchor` / `ClearAnchor`, `AvatarBinding.AnchoredItem`, `HeldItemPoseCalculation.GripFrame` / `FallbackFrame`
- Muscle baking: `GripAuthoringAssets.Bake` / `CopyHoldToCharged` / `Clear`, `GripPoseCapture`, `AvatarBinding.CaptureMuscles`, `RoundTripMuscles`
- Swivel instrumentation and `HeldItemPresentationState.AuthoringPose`
- Custom palm / item-offset / avatar-offset / pouch Handles in `GripAuthoringWindow`
- `ItemDefinition.ThirdPersonGrip` / `FirstPersonGrip` / `AvatarGrips` / `Grip()`, `GripOffset`, `AvatarGripOffset`
- `SlingshotDefinition.PouchOffset`
- First-person heavy body-frame blend (`HeavyFrameWeight` / `HeavyBody` placement in `PlayerHandPresentation`)
- Third-person look-pitch arm rotation in `AvatarArmIK`
- `AvatarSettings.DriverHandOffset` / `OverrideDriverHandOffset`, `AvatarDriverSettings.DriverHandOffset`, `AvatarDriverPose.HandOffset`

## Retained

- `AvatarHandTargets` arbitration; `AvatarArmIK` (extended); `AvatarFingerLayers`; `AvatarPalmCalibration`
- `HandHoldPresentation`
- `ItemReleaseClearance`
- `GripReachReadout`
- `GripAuthoringPersistence`, `GripAuthoringBuildPolicy`, and the snapshot / revert / save part of `GripAuthoringAssets`
- `LocalFirstPersonHands` free-hand behaviour (rest / rise / fall / bounce / landing) and `FirstPersonHandsSettings`
- Third-person locomotion arms when no hand target is active

## Out of scope

- Player carry behaviour (only its timing source retypes to `HoldSlot`).
- Finger pose authoring.
- Interactables other than the golf cart.

## Content changes required

- Add a `GolfCart` prefab instance to `Assets/Scenes/AvatarPresentationDemo.unity`.
- Re-author after implementation: slot defaults for `Regular`, `Heavy`, `Slingshot` in both views; item defaults and avatar overrides as needed; the golf cart steering wheel contacts in both views.
