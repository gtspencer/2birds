# Held Item Pose Layers Spec

## Goal
Held-item arm poses come from a few shared pose clips per hold mode, played on a masked arm layer:
- **One-hand items** ride the posed hand with no IK.
- **Two-hand items** sit between the posed palms, and IK closes only the last few centimetres.
- **The slingshot** needs no IK.

Per item, authoring is reduced to a hold mode, two grips and a finger clip. First person and third person stay separate presentations with their own pose clips.

**Requires** `Hand_IK_Stability_Spec.md`, which provides the arm IK job, the stream-rebuilt body frame, body-relative hand targets, and item placement after evaluation.

## Decisions
| Topic | Decision |
|---|---|
| Arm pose source | A masked arm layer that plays single-frame pose clips, modelled on `AvatarFingerLayers`. |
| Hold modes | `ItemHoldMode { OneHand, TwoHand, Slingshot }`. `HeldItemSettings` holds each mode's clips and timings. No per-item pose or timing overrides. |
| Grips | Stay as data on the definition: `RightPalmContact`, `LeftPalmContact`, and `PullingPalmContact` on the slingshot. No prefab child transforms. |
| Per-item offsets | None. The grips fully determine where the item sits. An item that needs a different placement gets a new mode. |
| Two-hand anchor | The midpoint of the grips sits at the midpoint of the posed palms. The grip axis lines up with the palm axis, and roll comes from the posed palms. |
| Slingshot | The frame rides the posed right hand. The pouch follows the posed left palm. No IK. |
| Charge | Blend Hold → Charged clips by charge progress. |
| Charge pitch | Third person only. The job rotates the posed arm chain about the shoulder by look pitch × charge weight. |
| Transitions | Layer and charge weight blends. No spring, no start-pose snapshots. Network timing rules are unchanged. |
| Throw follow-through | IK, not a clip. The released hand briefly tracks the thrown item's grips while the arm layer holds Charged. |
| First-person rig anchor | Unchanged from spec A. The `HeavyFrameWeight` blend to `HeavyBody` still places the first-person rig for `TwoHand` items and player carry. |
| First-person bob | Moves the whole first-person rig while holding. `FreeHands` IK applies only to an empty hand. |
| First-person clearance | Translates the whole first-person rig and the item, capped at `MaximumCorrection`. No IK, no second evaluation. |
| Authoring tool | Ghost-hand grips, Mirror R→L, a pose editor with an elbow swivel slider per arm, and a multi-avatar strip with a reach readout for two-hand items. |
| Two-hand reach cap | `TwoHand` grip targets use `MaximumReach` 0.98. The pose clip keeps the elbows bent, not a reach cap. Follow-through stays capped at `FollowReachFraction`. |
| Player carry timings | Shared with the `TwoHand` mode. Split them into their own block only if Boulder tuning and carry feel start to conflict. |
| Missing pose clip | That arm layer stays at weight 0, so the arms keep the base animation. |

## Data model

### `ItemHoldMode`
`{ OneHand = 0, TwoHand = 1, Slingshot = 2 }`. The existing serialized values carry over: `Hand` → `OneHand` and `Heavy` → `TwoHand`. Set `Slingshot.asset` to `HoldMode: 2`.

### `HeldItemSettings`
- Replace `SlingshotChargePose`, `FirstPersonPose`, `HoldSettings`, `HeavyHoldSettings`, all `Resolve*` methods, and `SetOverride` with one `HoldModePoses` per mode: `OneHand`, `TwoHand` and `Slingshot`.
- `HoldModePoses` holds:
  - `ThirdPersonHold`, `ThirdPersonCharged`, `FirstPersonHold`, `FirstPersonCharged` (`AnimationClip`)
  - `ChargePoseDuration`, `MaximumFollowDuration`, `EndPosePauseDuration`, `ReturnBlendDuration`, `FollowReachFraction`
- Add `HeldItemSettings.Poses(ItemHoldMode)`.
- Player carry reads its timings and reach from `TwoHand`. It currently reads `HeavyHoldSettings`.

### `ItemDefinition` ("Held Item Grip")
- **Keep:** `HoldMode`, `RightPalmContact`, `LeftPalmContact` and `GripFingers`. The contacts are the grips, in item-root metres before prefab scale.
- **Delete:** `OverrideHoldSettings`, `OverrideFirstPersonPose`, `FirstPersonPose`, `HandPose`, and the "Held Hand Pose" header.

### `SlingshotDefinition`
- **Keep:** `PullingPalmContact`, and `RecoverySeconds` (the slingshot's action recovery timing, used by `RecoveryFinished`).
- **Delete:** `OverrideFirstPersonChargePose`, `OverrideRemoteChargePose`, `FirstPersonChargePose`, `RemoteChargePose`, and `SlingshotChargePoseSettings`.

### `AvatarSettings`
Delete `FirstPersonHoldOffset`. Keep `FirstPersonPlacementOffset`, `FirstPersonReachOffset`, and the palm corrections.

## Arm pose layer
Add `AvatarArmLayers`, modelled on `AvatarFingerLayers`.
- **Structure:** an `AnimationLayerMixerPlayable` with a basis input, a right-arm layer and a left-arm layer. Each layer is masked to `AvatarMaskBodyPart.RightArm` or `LeftArm`.
- **Clips:** each layer is a two-input mixer of the Hold and Charged clip playables, at speed 0 and time 0, with `SetApplyPlayableIK(false)` and `SetApplyFootIK(false)`.
- **API:** `Select(HoldModePoses, firstPerson)` swaps the clips, and `Set(holdWeight, chargeWeight)` sets:
  - the layer weight to `holdWeight`
  - the mixer to Hold `1 − chargeWeight` and Charged `chargeWeight`
- **Which arms:** `OneHand` weights only the right-arm layer. `TwoHand` and `Slingshot` weight both.
- **Missing clip:** a mode whose clip is unassigned leaves that layer at weight 0, and the arms stay on the base animation.
- **Graph order:**
  - `AvatarAnimationGraph`: `states` → arm layers → finger layers → arm IK job → output.
  - `LocalFirstPersonHands`: `Idle` basis → arm layers (first-person clips) → finger layers → arm IK job → output.

## Placement and IK (in the arm IK job)
The job reads the posed stream, which already includes the arm layers, and applies the steps in this order:

1. **Charge pitch** (third person only): for each weighted arm, rotate `UpperArm` about its shoulder, around the body-right axis, by `LookPitch × chargeWeight`. `LookPitch` is clamped to the look-at range (−40°, 50°).
2. **Anchor**, by mode:
   - **OneHand:** no IK on the right hand while holding or charging. The item pose is `ItemFromPalm(right palm, RightPalmContact, prefab scale)`, taken from the evaluated palm in `CommitHands`, as today.
   - **TwoHand:**
     1. Compute both pre-IK palms from the wrists with the `WristToPalm` measurements.
     2. Place the item so the midpoint of its grips sits at the midpoint of the palms, and rotate it so the right-to-left grip axis lines up with the right-to-left palm axis. Roll comes from the average palm rotation.
     3. Grips → wrist targets → two-bone solve for both arms.
     4. Write the item pose to a job output buffer, and have `CommitHands` place the item from it.
   - **Slingshot:** the frame is `ItemFromPalm(right palm, RightPalmContact)`. The pouch's world position is the posed left palm mapped through `PullingPalmContact`. `SlingshotPresentation` takes that position instead of computing `Pouch(amount, drawOffset)`. The pebble departure centre follows the pouch. No IK.
3. **AvatarHandTargets IK:** Carry, Contact, follow-through and Free targets are solved as in spec A, on top of the posed stream.

## Transitions and recovery
`HeldItemPresentationState` reduces to action and network timing → `(holdWeight, chargeWeight, follow-through targets)`.

- **Equip / unequip:** `holdWeight` blends 0 ↔ 1 over the existing blend duration.
- **Charge:** `chargeWeight` = eased charge progress over `ChargePoseDuration`.
- **Follow:** for `MaximumFollowDuration` after release, Item-source world-space IK targets track the released item's grips, capped at `FollowReachFraction`. `OneHand` uses the right hand; `TwoHand` uses both. The existing linecast abort stays. The arm layer holds Charged.
- **Pause:** keep the last follow targets, body-relative, for `EndPosePauseDuration`.
- **Return:** IK weight → 0 and `chargeWeight` → 0 over `ReturnBlendDuration`.
- **Slingshot:** no follow. `chargeWeight` → 0 over `RecoverySeconds`.
- `RecoveryFinished` sums the mode's timings, or uses `RecoverySeconds` for the slingshot.
- Network messages, charge progress, release pose and recovery timing rules are unchanged.

## First person
- **Held poses:** the arm layers use the first-person clips.
- **Rig anchor:** `PlayerHandPresentation.Place` keeps blending the rig from the camera frame to `HeavyBody` by `HeavyFrameWeight` for `TwoHand` items and player carry. The `TwoHand` first-person clips are authored for that body-anchored rig.
- **Bob, fall and landing:** while a hand holds an item, `PlayerHandPresentation.Place` adds the holding hand's procedural `FreeHands` offset (the same rest/rise/fall lerp, bounce and dip, times arm length) to the rig position. `FreeHands` keeps driving IK targets only for a hand with no item. The `FirstPersonReachOffset` usage is unchanged.
- **Clearance:**
  1. After evaluation, `CommitHands` computes the item pose and calls `ItemReleaseClearance.TryResolve` without a reach constraint. The candidate distance is already capped at `MaximumCorrection` (0.20 m).
  2. A correction translates the first-person rig and the item by the same vector in that frame. Bones are children of the rig, so no re-evaluation is needed.
  3. The release pose is read after the correction.
- The pebble path (`TryPreparePebble`) applies its correction the same way.

## Authoring tool
- **Ghost-hand grips:** at each grip, show a copy of the selected avatar's hand posed with `GripFingers`, moved by the existing `GripAuthoringHandle` position/rotation handles in item metres and degrees.
- **Mirror R→L:** writes `LeftPalmContact` as `RightPalmContact` mirrored across the item's local YZ plane.
- **Pose editor:**
  - Pick a mode and a slot (TP/FP × Hold/Charged). The reference avatar shows that mode's current clip.
  - A palm handle per arm drives the arm IK job. An editor-only elbow swivel slider per arm (degrees) rotates the elbow around the shoulder → wrist line from the incoming bend plane.
  - Save reads `HumanPoseHandler.GetHumanPose` and writes the arm muscles as constant curves to a single-frame `AnimationClip` through `GripAuthoringPersistence`.
- **Multi-avatar strip:** every registered avatar, side by side in the observer view, holding the selected item in its current state (hold, charge progress). Two-hand items show a "short by X cm" readout per hand.
- **Remove:** override toggles, arm-length unit labels, the pose vector fields, and the matching `GripAuthoringExport` and `GripAuthoringPanel` field lists.

## Deletions
- `HeldItemPoseSettings` spatial fields and `HeldItemSpatialSettings`.
- The spatial members of `HeldItemPoseData` (`Spatial`, `HoldRotation`, `ChargedRotation`, `SlingshotCharge`, `HoldPosition`).
- `HeavyItemPoseCalculation`.
- `HeldItemPoseCalculation.Hold`, `Charge`, `SlingshotCharge`, `ReachInterval`, `SlingshotReach` and `Resolve`.
- `ItemReleaseReach`, and the `reach` parameter and projection branches of `ItemReleaseClearance.TryResolve`.
- `HeldItemPresentationState` start-pose snapshots (`chargeStart`, `returnStart`, `returnRotation`, `followStart`, `retained*`), the Bézier charge, `CorrectCommittedPose`, `CorrectItem` and `ApplyClearancePose`.
- `PlayerHandPresentation.CommitCorrection`, and the second `active.Evaluate` in `Evaluate`.

## Migration
- Code removals drop the old serialized fields. Grips need no change.
- Edit `Slingshot.asset` to `HoldMode: 2`.
- The user authors the 12 pose clips (3 modes × TP/FP × Hold/Charged) in the pose editor and assigns them in `HeldItemSettings`. Until they're assigned, held items ride the base animation arms.

## Visual validation (user)
- **Pose editor:** author a `OneHand` TP Hold clip and save it. The observer avatar's right arm takes the pose, and the rock sits in the palm with no IK drift while walking and sprinting.
- **Multi-avatar strip:** each avatar holds the rock in the same pose relative to its own body.
- **Boulder (TwoHand):** hanging low with arms nearly straight. Hands stay on the grips while walking. Every avatar in the strip shows less than about 5 cm short, or none.
- **Charge in third person:** look up and down while charging a throw. The throwing arm tilts with pitch, elbows bend naturally overhead, and nothing pops.
- **Throw follow-through:** the hand briefly follows the item, pauses, and eases back to Hold.
- **Slingshot:** the pouch stays in the left hand from Hold through full draw, in both FP and TP. The pebble launches from the pouch.
- **First person:** walking, falling and landing move arms and item together. Walking into a wall pulls the whole rig back smoothly, and throws near walls start from the cleared position.
- **Grip tool:** ghost hands match the in-game hands, and Mirror R→L produces a symmetric left grip on the Boulder.
