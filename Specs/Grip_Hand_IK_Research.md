# Grip / Hand IK Research

Follows up on `Grip_Hand_IK_Polish.md`. Covers how held-item hand IK works today, why third-person hands jitter or shoot out behind the avatar, what other games and tools do, and a recommended redesign.

## Decisions taken (from the design Q&A)

| Question | Decision |
|---|---|
| Where the base hand pose comes from | **Shared pose archetypes.** A few shared hold styles. Each item authors its grips and a small offset. IK only closes the last few centimetres. |
| Boulder target look | **Low, arms hanging.** The boulder hangs in front of the hips and thighs with the arms nearly straight. |
| Authored pose out of reach | **Arms stretch.** The item stays where it was authored. The arms reach as far as they can and may fall short. No runtime pull-in of the item. |
| Solver | **Unity Animation Rigging** (`TwoBoneIKConstraint`), replacing Mecanim `OnAnimatorIK` for hands. |

First person and third person stay separate presentations, as the Polish doc asked. Both use the same data model and the same solver.

---

## 1. How it works today

### Data an item carries
`ItemDefinition` (`Assets/Game/Runtime/Items/ItemDefinition.cs`), `HeldItemSettings`, and `HeldItemPoseSettings`/`HeldItemSpatialSettings` (`Assets/Game/Runtime/Items/HeldItemPose.cs`):

- `HoldMode` (Hand / Heavy)
- `RightPalmContact`, `LeftPalmContact`: palm pose in item-root metres, before prefab scale
- `OverrideHoldSettings` → `HandPose`: 5 × Vector3 (HoldPosition, HoldWristEuler, ChargeControlPosition, ChargedPosition, ChargedWristEuler), 6 timing and reach floats
- `OverrideFirstPersonPose` → `FirstPersonPose`: another 5 × Vector3
- `GripFingers`
- Slingshot adds `PullingPalmContact`, 2 charge-pose overrides, and `RecoverySeconds`

Shared data used alongside:
- `HeldItemSettings`: 3 default pose sets plus the slingshot charge pose
- Per avatar: `Left/RightPalmCorrection`, `FirstPersonPlacementOffset`, `FirstPersonReachOffset`, `FirstPersonHoldOffset`
- `FirstPersonHandsSettings`

The units differ from field to field:
- metres before prefab scale (contacts)
- arm lengths from the right shoulder (one-hand hold)
- average arm lengths from the shoulder midpoint to the collider centre (heavy hold)
- a camera-oriented shoulder frame (first person)
- metres before avatar scale (palm corrections)

To predict where an item will end up, the author has to combine all of these in their head.

### Runtime pipeline
1. `HeldItemPresentationState` (812 lines) runs a state machine: Idle blend → Charge (a Bézier curve in arm-length space) → Recover (follow, pause, return). Each item has separate one-hand and heavy branches, plus slingshot special cases.
2. The pose math in `HeldItemPoseCalculation` / `HeavyItemPoseCalculation` places the **item** in body space and derives each palm from the item and its contact. For heavy items, `Place()` then projects the item into the intersection of two "wrist within reach" spheres (`ItemReleaseReach.TryProject`).
3. Palm targets go into `AvatarHandTargets`, a priority stack: Free < Item < Carry < Contact.
4. `AvatarHandIK` feeds Mecanim humanoid IK from `OnAnimatorIK`: `SetIKPosition`/`Rotation`, plus a fixed body-space elbow hint at weight 0.4. `AvatarHandIK.Correct` (`AvatarHandIK.cs:54`) already contains a small analytic two-bone solver, but only the carry correction pass uses it.
5. Third person builds the body frame from **bone positions** (`PlayerHandPresentation.cs:365`, `GripObserverPreview.cs:65`). Other paths build it from **settings** (`HeldItemPresentationState.Body`). `WithReference` mixes the two for heavy items.

---

## 2. Why third-person hands jitter or shoot out the back

Ranked by how much each contributes to the Boulder symptoms. 2.1 is confirmed as the primary cause. 2.2 and 2.3 are the likely sources of the rare jitter that remains once the hold is in reach.

### 2.1 The Boulder hold is far outside reach, and the reach projection flickers (primary cause, confirmed)
The new Boulder `HandPose.HoldPosition` is `(0, -1.09, 1.04)`, which is about **1.5 average arm lengths** from the shoulder midpoint. Reach is capped at `0.85`. So every frame, `HeavyItemPoseCalculation.Place` (`HeldItemPose.cs:213`) and the heavy branch of `Blend` (`HeldItemPresentationState.cs:746`) have to project the item onto the boundary of the two-sphere reach set.

- The projected point lies exactly on both sphere surfaces. `TryProject` then re-checks it with `Contains` (`ItemReleaseClearance.cs:45`), using an absolute tolerance of `1e-6` on **squared world-space distances** (`:23`). Away from the world origin, float error in `center + radial * radius` is larger than that tolerance. The check can therefore pass on one frame and fail on the next.
- When it fails, `reachable = false` and the item keeps its raw position 1.5 arm lengths away. `AvatarHandIK` then clamps each wrist **independently** to 0.98 of its length (`AvatarHandIK.cs:101`). The two hands leave the item and point toward the raw target.
- Result: from frame to frame the pose switches between "hands on a projected boulder" and "arms fully extended at a distant boulder". This is the jitter.
- **Out the back:** if `radial` is degenerate, the fallback is `Cross(axis, up)` (`ItemReleaseClearance.cs:43`). The axis runs from the right shoulder to the left, so this cross product is the body's **backward** direction. Any case where the desired point sits near the shoulder axis sends the item, and the arms, behind the avatar.

**Finding:** with `HoldPosition` back at the heavy default `(0, -0.4, 0.5)`, the Boulder is stable in third person apart from a rare single jitter. So the out-of-reach hold triggers the main symptom. The test does not show which step of the projection is at fault: the tolerance flicker above, or the per-hand clamp alone. Logging how often `reachable` flips per second in `Blend` at the far value would tell them apart. The redesign removes the projection from the hold path either way (4.2).

**Until the redesign:** keep every held pose within reach. For heavy items, that means `HoldPosition` well under 0.85 average arm lengths from the shoulder midpoint.

### 2.2 Hand targets have to follow the animated upper body, but only last frame's copy is available
Remote and observer avatars build the shoulder frame from `RightUpperArm` / `LeftUpperArm` positions inside `PreparingHands` (`AvatarPresentation.cs:281`). This coupling is **required**. The animated shoulders sway at idle, bob while walking, and sit differently from the rest-pose measurements. A frame built only from the root and avatar settings (`HeldItemPresentationState.HeavyBody`) makes the jitter worse and puts the hands well off the grips. The item stays rigid to the root while the animated arms move under it, and near full reach Mecanim IK amplifies every small mismatch.

The weakness is timing. `PreparingHands` runs **before** this frame's animation and root `Place()` (`AvatarInstance.cs:117`), so the bones still hold last frame's result, **after IK**:
- **Lag:** the targets trail by one frame of body motion. This is small but varies with frame time.
- **Possible feedback:** Mecanim's humanoid IK can slightly adjust shoulders and arm length (up to 5% squash/stretch), and next frame's target is computed from those adjusted bones. This is less important than 2.1 but may explain the occasional jitter that remains with an in-reach hold.

Unity forum threads confirm that bone transforms read around `OnAnimatorIK` already contain the previous frame's IK correction. The fix is not to leave the bones out. It is to read them **from the current frame, before hand IK**. `OnAnimatorIK` cannot do that; an animation job can (see 4.3).

### 2.3 Mecanim humanoid IK is a poor fit for hard hand constraints
- It solves in muscle space. Wrist rotation goals outside muscle limits get clamped, and twist is redistributed by the solver, so the wrist can pop.
- A fixed hint at weight 0.4 (`AvatarHandIK.cs:105-111`) competes with the solver's own elbow preference. The effect is worst for overhead charge poses, where the hint is still "down, out, back".
- There is no soft-IK near full extension, so the elbow snaps as the arm reaches its limit.

### 2.4 Too many coordinate frames
There are two body-frame definitions: settings-based, and bone-based with `WithReference` / `poseCenter` mixing. Heavy items blend frames with `HeavyFrameWeight`. Many different timers (charge, follow, pause, return, blend) each capture their own start pose in their own frame. Nothing is individually wrong. But every transition converts between frames, and every conversion is a chance for a one-frame pop. It is also why "even with those settings, I don't feel that everything is covered".

### 2.5 Arm-length units make authoring avatar-dependent
Positions are expressed in arm lengths from the shoulder. The same value therefore lands at a different torso-relative height on avatars with different proportions. A pose tuned on one avatar can be unreachable on another, which makes 2.1 worse.

---

## 3. What others do

| Source | Pattern | Relevance |
|---|---|---|
| Opsive Ultimate Character Controller | Keeps the first-person and third-person models of an item separate. In third person the item is parented to a hand **slot**, and the support hand has an IK target: a child transform on the item, with an optional elbow hint. | Our FP/TP split is normal. Grips should be **transforms on the item**, not numbers in a separate asset. |
| UE left-hand weapon IK (Zag's blog) | Compute the off-hand target **in the dominant hand's bone space**, not world space. Otherwise the off-hand lags behind. | Derive the second grip from the first hand or the item frame in the same pass, not from separately timed world poses. |
| KINEMATION FPS framework / Dev_Unallocated | First person uses an **item bone** positioned relative to the camera, and both hands IK to grips on the item. "When using an item bone, the hands follow the item." The base pose must already line up with the grips, so IK only makes small corrections. Sway, bob and recoil are additive layers on top. The tool writes the weapon offset when you press "Save Weapon Position". | This matches our FP model (item placed, hands follow). The difference is that they start from a base pose that is already close, and author with a save-in-place tool rather than typed vectors. |
| Final IK Interaction System | Copies of the character's posed **hand** are placed as children of the object as `InteractionTarget`s. You author a grip by posing a visible hand on the object. | The best authoring UX for grips: pose a ghost hand, not a Vector3 and Euler angles. |
| Unity Animation Rigging `TwoBoneIKConstraint` | Root/Mid/Tip plus Target and Hint transforms, with position, rotation and hint weights. It runs in animation jobs on the **current frame's pre-IK stream**. `RigBuilder.Build(PlayableGraph)` and `RigBuilder.Evaluate(dt)` support manually evaluated graphs. With position weight 1, the arm extends as far as it can toward a target that is out of reach. | This fixes 2.2 and 2.3, and fits our manual `PlayableGraph` evaluation. |
| Soft IK (Blender, Spine, CozyClay) | Close to maximum extension, reach approaches the cap exponentially instead of linearly. This removes the elbow snap. | Needed because we chose "arms stretch": held arms will often be near full extension (for example the hanging boulder). `HeldItemPoseCalculation.Resolve` already has this curve, but the callers pass `soften: false`. |
| Aim offsets (standard in UE and Unity) | A pose authored for looking up, straight and down, blended by aim pitch. | Covers the look-pitch case. Today only slingshot charge accounts for pitch. |

---

## 4. Recommended design

### 4.1 Data model: archetypes + grips

**`HoldArchetype` (shared ScriptableObject, around 4 total).** Starting set: `OneHand`, `TwoHandHeavy` (low, arms hanging), `Slingshot`, and optionally `Overhand`.
- For each view (FP and TP), a small set of **poses**: `Hold`, `Charged`, and optionally `AimUp` / `AimDown` for pitch. Each pose is a single-frame humanoid clip or a stored `HumanPose`. It is authored once, visually, in the grip tool.
- The **anchor rule** that decides where the item goes relative to the pose:
  - `OneHand`: the item hangs from the right palm through the item's right grip.
  - `TwoHand`: the item frame sits at the midpoint of the two palms, oriented from palm to palm.
- Timing: `ChargePoseDuration`, `FollowDuration`, `PauseDuration`, `ReturnDuration`. These move here from each item.

**Per item (replaces everything under "Held Item Grip" / "Held Hand Pose"):**
- `Archetype`
- `RightGrip`, `LeftGrip`: child transforms on the world prefab, authored by posing a ghost hand
- `HoldOffset`: position in metres and rotation in degrees, relative to the archetype anchor. Default is zero.
- `FirstPersonHoldOffset`: optional, same units
- `GripFingers`

That removes 10 Vector3s, two override toggles, and every arm-length unit from items. Avatar palm calibration stays, because it describes the avatar, not the item.

### 4.2 Runtime pipeline
1. **Body frame from the current frame's animated chest.** Anchor held items to the `UpperChest` / `Chest` bone as the animation produced it **this frame, before hand IK**. Don't use the root: a root-only frame ignores the upper-body motion the hands must follow (see 2.2). Don't use last frame's bones either. The same code runs for local, remote and observer avatars.
2. **Palm targets come from the archetype pose.** When an avatar binds, sample each archetype pose once and bake the palm poses **relative to the chest bone**, per avatar. At runtime, blend baked palms by charge progress and aim pitch, then place them with the current chest. Avatar proportions are handled automatically because the pose is sampled on the avatar itself, and walk bob and idle sway carry through because the chest moves with the animation.
3. **Place the item** from the baked palms, the anchor rule, the grips and `HoldOffset`.
4. **IK targets = the item's grips.** Following the "arms stretch" decision, the item is never moved to satisfy reach. If a grip is out of reach, the arm reaches for it and comes up short, with soft-IK so the elbow doesn't snap. **Delete** `ItemReleaseReach.TryProject` from the hold path, which removes 2.1 entirely. (First-person wall clearance is a separate concern. It can stay, but it should move the whole item, not the reach projection.)
5. **Transitions:** use one critically damped spring on the item pose in body-local space, instead of five start-pose snapshots. Charge becomes "blend baked Hold → Charged palms by progress". Follow-through and recovery keep their network timing rules but drive the same spring target.
6. **Carry (player-on-player)** and **cart contacts** keep writing into `AvatarHandTargets`. Only the solver underneath changes.

### 4.3 Solver: Animation Rigging
- Add `com.unity.animation.rigging`. At `AvatarInstance.Stage`, or in `AvatarProcessor` at import time, add a `RigBuilder` and `Rig` with two `TwoBoneIKConstraint`s: UpperArm → LowerArm → Hand, with target and hint transforms owned by the presentation.
- Integrate with the existing manual graph through `RigBuilder.Build(graph)` + `SyncLayers()` (or `Evaluate(dt)`) inside `AvatarInstance.Evaluate`. Do the same for `LocalFirstPersonHands`.
- Map `AvatarHandTargets.Resolve` to the constraint weights: position, rotation, and hint.
- **Read the chest from the current frame.** Since Animation Rigging 1.3, a source transform under `animator.avatarRoot` is bound to the animation stream (`BindStreamTransform`). A transform outside it is bound to the scene. The robust option is a small custom constraint job: it reads the chest from the stream, applies the chest-relative item and grip offsets (passed in as job data each frame), and solves the two arms. The target is then built from this frame's animated chest with no one-frame lag and no feedback. Parenting stock `TwoBoneIK` targets under the chest bone might give the same result, but test it before relying on it: it depends on how the stream handles transforms that no clip animates.
- Convert palm targets to the wrist once, using the existing `WristToPalm` measurements, so the target transform sits at the `Hand` bone.
- **Hints:** each archetype pose carries its own elbow direction, sampled from the pose's elbow when baking. This removes the fixed body-space hint that fights overhead poses.
- **Soft-IK:** `TwoBoneIKConstraint` has no softening. Soften the target distance before writing it, using the existing exponential curve in `Resolve`. If that isn't enough, write a small custom `IAnimationJob` two-bone constraint; `AvatarHandIK.Correct` is most of it already.
- Keep Mecanim `OnAnimatorIK` for feet and look-at only. Delete `AvatarHandIK` and `CorrectHands`, because Animation Rigging reads the current stream and needs no second correction pass.
- Note: Mecanim allowed about 5% arm stretch, and Animation Rigging does not stretch bones. Under "arms stretch" this means arms reach full extension and stop there. Say if you want real stretch; that would need a custom job.

### 4.4 Authoring tool changes
- **Ghost-hand grip editing.** Show the avatar's posed hand mesh (grip fingers applied) at each grip on the item. Move it with scene handles in item metres and degrees. Add a **Mirror R→L** button for symmetric items.
- **Archetype pose editor.** Pose the arms on a reference avatar (handles on the palms, with IK driving the preview) and save `Hold` / `Charged` / `Aim*`. This is done once per archetype, not per item.
- **Multi-avatar reach strip.** Show every registered avatar holding the selected item side by side, with a per-hand "short by X cm" readout. The current `GripReachReadout` already computes this. Given "arms stretch", this is a warning, not a save blocker.
- **Save in place.** Drag the held item in the observer view, and the tool writes `HoldOffset`, like KINEMATION's "Save Weapon Position".
- Drop the arm-length unit labels and override toggles from the panel. An item then has 5 fields.

### 4.5 What gets deleted
`HeldItemSpatialSettings`, the spatial fields of `HeldItemPoseSettings`, `OverrideHoldSettings`, `OverrideFirstPersonPose`, `HeldItemSettings.FirstPersonPose/HoldSettings/HeavyHoldSettings`, `HeldItemBodyFrame.WithReference`/`poseCenter`, the heavy `Place`/`Reach` projection, `AvatarHandIK`, the Bézier charge in arm-length space, and probably `FirstPersonHoldOffset`/`FirstPersonReachOffset` on the avatar. `HeldItemPresentationState` should shrink to the action/network timing plus one spring.

---

## 5. Suggested order of work

1. **Stop the bleeding.** Keep held poses within reach until the redesign lands. For the Boulder, `(0, -0.4, 0.5)` is stable.
2. **Swap the solver.** Add Animation Rigging with the constraints on both rigs, driven by the existing `AvatarHandTargets`, with the chest read from the current stream (4.3). This should remove the leftover occasional jitter.
3. **Archetypes + grips.** Add `HoldArchetype`, bake palms at bind, and switch items to grips + offset. Migrate the existing items by hand, not with a migration tool, per AGENTS.md.
4. **Authoring tool.** Add ghost hands, the archetype pose editor, the multi-avatar strip, and save-in-place.
5. **Delete the old path** (4.5).

---

## 6. Open questions and risks
- **Animation Rigging on processed VRM prefabs.** Decide whether the rig components are added at processing time (`AvatarProcessor`) or created at runtime in `Stage`. Runtime creation avoids reprocessing assets but needs a `Build` per bind.
- **Manual graph ordering.** Rig jobs must run after the humanoid pass and the finger layers, and before `runtime.Process()` (VRM springs and look-at). Confirm when implementing.
- **First-person pose source.** FP arms currently start from a frozen `Idle` clip plus procedural free-hand motion (`FreeHands`). Decide whether FP archetype poses replace that base or layer on top of it.
- **Carry feel.** Player-carry already uses carry grips on the partner's skeleton, which reads bones by nature. It will still depend on bone timing (`HandDependency`) after the solver swap.
- **Aim pitch in third person.** Worth adding `AimUp` / `AimDown` for `OneHand` right away, or only for throwing?

---

## Sources
- [Unity Manual: Inverse Kinematics](https://docs.unity3d.com/Manual/InverseKinematics.html)
- [Unity Blog: Mecanim humanoids](https://unity.com/blog/engine-platform/mecanim-humanoids): humanoid IK corrects the retargeted pose and uses squash/stretch limited to 5% by default
- [Unity Discussions: Get original bone position before IK is set](https://discussions.unity.com/t/get-original-bone-position-before-ik-position-is-set/1587077)
- [Unity Discussions: Read bone transforms before IK corrections with Animation Rigging](https://discussions.unity.com/t/read-bones-transforms-before-ik-corrections-with-animation-rigging/773429)
- [Unity Discussions: Animation Rigging IK jitter](https://discussions.unity.com/t/animation-rigging-ik-jitter-shaking-with-video/895120)
- [Animation Rigging: Two Bone IK](https://docs.unity3d.com/Packages/com.unity.animation.rigging@1.3/manual/constraints/TwoBoneIKConstraint.html)
- [Animation Rigging 1.3.0 changes (stream vs scene binding via `IsChildOf(avatarRoot)`)](https://github.com/needle-mirror/com.unity.animation.rigging/commit/master)
- [Unity FPSSample: TwoBoneIkJob (custom animation-job IK)](https://github.com/Unity-Technologies/FPSSample/blob/master/Assets/Scripts/Game/Modules/Character/Animation/AnimationJobs/TwoBoneIkJob.cs)
- [Animation Rigging: RigBuilder API](https://docs.unity3d.com/Packages/com.unity.animation.rigging@1.3/api/UnityEngine.Animations.Rigging.RigBuilder.html)
- [Breaking free from Mecanim with IK rigs](https://asfunasfun.itch.io/warps-maze/devlog/530175/breaking-free-from-mecanim-with-ik-rigs)
- [Opsive UCC: Third Person Perspective items](https://opsive.com/support/documentation/ultimate-character-controller/items-inventory/character-item/third-person-perspective/)
- [Zag's Blog: The Right Way to Do Left-Hand Weapon IK](https://zaggoth.wordpress.com/2019/01/26/ue4-tutorial-the-right-way-to-do-left-hand-weapon-ik/)
- [KINEMATION FPS Animation Framework: Weapon setup](https://kinemation.gitbook.io/fps-animation-framework/tutorial/getting-started/weapon-setup)
- [Dev_Unallocated: Procedural Weapon Animations](https://www.devunallocated.com/projects/project-killhouse/procedural-weapon-animations-condensed)
- [Final IK: Interaction System](http://www.root-motion.com/finalikdox/html/page10.html)
- [Unity-Humanoid-TransportObjects (Final IK two-handed pickup)](https://github.com/mariusrubo/Unity-Humanoid-TransportObjects)
- [BlenderNation: Avoiding bone popping in IK chains](https://www.blendernation.com/2017/11/27/avoiding-bone-poping-ik-chains/)
- [Spine: IK constraints (softness)](https://en.esotericsoftware.com/spine-ik-constraints)
- [CozyClay: soft max-extension clamp for two-bone solve](https://github.com/NomaDamas/CozyClay/issues/248)
