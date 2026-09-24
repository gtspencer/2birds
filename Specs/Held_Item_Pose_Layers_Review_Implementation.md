# Held Item Pose Layers Review Implementation

Response to `Held_Item_Pose_Layers_Review.md`.

| # | Finding | Outcome |
|---|---|---|
| 1 | No fallback for a missing pose clip | Partly fixed (the FP part, through #2). The rest is declined. |
| 2 | FP free-hand to clip handoff dips | Fixed with a different mechanism. The proposed fix would not work. |
| 3 | Save pose clip overwrites any clip | Fixed |
| 4 | Muscle-space capture error readout | Declined |
| 5 | Body frame moves with the arm clips | Declined |
| 6 | Arm layers rebind every frame and tear down on default | Fixed |
| 7 | FP two-hand charge ignores pitch | Declined (a spec decision) |
| 8 | `Attachment` anchor differs from `CommitHands` | Fixed |
| 9 | Reach readout mixes corrected and uncorrected poses | Fixed |
| 10 | Slingshot recovery edge cases | Fixed |
| 11 | Follow targets submitted as world after following stops | Fixed with a broader condition |
| 12 | Pose-edit state leaks | Fixed |
| 13 | `HoldMode = Slingshot` on non-slingshot items | Declined |
| 14 | Duplicated logic and leftovers | Mostly fixed. The `HeavyBody` merge is declined. |
| 15 | `GripGhostHand` shader lookup | Fixed. **Needs a material asset from the user.** |

---

## Fixed

### 2. Free-hand to clip handoff
The proposed fix assumes the free hand is still solved once the zero-weight `Item` target is installed. It isn't. `AvatarHandTargets.Resolve` returns only the highest-priority target, so the `Free` target is never solved while any `Item` target exists, whatever `freeWeights` holds. Neither suggested change (`1 - layerWeight`, or blending `owned`) would have had any effect.

What changed:
- `AvatarArmIK.Solve`: when a hand resolves to an `Item` target, the solver first solves that hand's `Free` target at its own weight. Then it computes the two-hand anchor and solves the `Item` target. The anchor uses the palms after that first solve, so two-hand items are covered too.
- `PlayerHandPresentation.AdvanceFreeHands`: under an `Item` target, the free weight is `1 - ` the arm's actual pose-layer weight (`LocalFirstPersonHands.PoseWeight` → `AvatarArmLayers.Weight`). The free arm blends out as the clip blends in.
- Third person is unchanged, because remote avatars have no `Free` targets.
- This also removes the same drop to the Idle pose at the end of throw recovery, when the follow target fades out and nothing is selected.

### 1. Missing clip, first-person part
The layer weight counts only modes that have a clip. So after #2, a mode with no FP clip keeps the free hand at full weight: one-hand and slingshot items stay in the free hand, and two-hand items anchor between the free palms. The rest of #1 is declined (see below).

### 3. Save pose clip
- Only a main asset under `Assets/Art/Animations/HeldPoses/` is overwritten. Anything else, such as an FBX sub-asset or a shared animation-set clip, gets a new asset instead.
- `clip.name = existing.name` is set before `CopySerialized`, so a `X 1.anim` file keeps a matching object name.
- Not changed: the clip file is written at save time while the reference stays a draft. "Save pose clip" is an explicit save of the clip's content. Overwriting the clip already in a slot is the action being asked for, so draft revert is not expected to undo it.

### 6. Arm layer binding
`AvatarArmLayers` binds clips when the `HeldItemSettings` reference changes and on its `ContentChanged` event, and unsubscribes in `Dispose`. `Set` now writes only weights. A default pose (no settings) sets the weights to 0 and keeps the nodes, so transitions like sitting or being carried no longer change the graph's topology.

### 8. Attachment anchor
`Attachment` uses `boundBinding.AnchoredItem ?? TwoHandAnchor(...)`, the same as `CommitHands`.

### 9. Reach readout
The unreachable flags are computed before the clearance correction is applied, so the palms and the prepare-time shoulders are in the same frame. The readout still uses prepare-time shoulders. It's already suppressed while blending, and with a steady clip those shoulders match the solve-time ones.

### 10. Slingshot recovery
- While loaded, the pouch centre is stored in item-local space (`loadedLocal`). The release recoil starts from that point, not from last frame's world position mapped through this frame's item pose.
- The settle window is `Mathf.Min(SettleSeconds, recovery * 0.7f)`, so a `RecoverySeconds` at or below 0.35 s still blends the pouch back to the hand instead of snapping on Idle.

### 11. Follow target space
The code now uses `bodyRelative: !(hands.Following && available)`. The proposed `!hands.Following` would still send the retained body-relative poses as world poses while following is active but the projectile isn't available yet. That is the usual case for remote avatars waiting on the spawned projectile.

### 12. Pose-edit leaks
- `GripAuthoringPanel.Rebuild` ends the pose edit whenever the current record isn't the `HeldItemSettings` record. This covers switching context and switching the shared source.
- `ClearTargets` zeroes `Swivel` (instrumentation only), so an early return in `PrepareHands` or a `Dispose` can't leave elbow swivel on carry, contact or free targets.

### 14. Leftovers
- Added `AvatarArmIK.MaximumReach` (0.98). It replaces the literals in `HeldItemPresentationState`, the `AvatarArmIK` cap and the `AvatarHandTargets.Set` default. `InHandReach` drops its always-0.98 parameter.
- Removed `WorldItemRegistry.GetHeldPose`, along with the `worldItem` constructor parameter of `HeldItemPoseData` that only it used.
- `HeldItemPresentationState.HeavyBody` is now private.
- `HandHoldPresentation.FrameWeight` is now the private field `frameWeight`.

### 15. Ghost-hand material
`GripAuthoringScene` has a serialized `ghostMaterial`, which is passed to `GripGhostHand`. The runtime `Shader.Find` and material setup are gone. Nothing in `Assets` references `Universal Render Pipeline/Unlit`, so an instrumentation build would strip it. The old constructor then threw on every `LateUpdate`, and it also leaked the avatar instance it had just created each time. The ghost path array is now static, and the selected-avatar lookup no longer allocates.

**Required:** create a URP/Unlit material (Surface Transparent, Blend Alpha, Base Color `(0.35, 0.85, 1, 0.35)`), for example `Assets/Art/Materials/GripGhostHand.mat`. Assign it to **Ghost Material** on `GripAuthoringScene` in `Assets/Scenes/AvatarPresentationDemo.unity`.

---

## Declined

### 1. No fallback for a missing clip (all but the FP part)
This rests on an incorrect assumption: that the empty settings asset is a shipped state. The spec decides "Missing pose clip → that arm layer stays at weight 0, so the arms keep the base animation." Its Migration step also has the user author all 12 clips in the pose editor. Empty slots are the expected state before authoring, and they show up as soon as you play.
- **Third person:** riding the locomotion arms is the specified fallback.
- **`HeavyFrameWeight`:** moving the FP rig to the body frame for two-hand items is specified separately from clips.
- **Slingshot pouch:** requiring a hold clip for `attach` is deliberate, because the pouch follows the posed left palm.

The FP regression, the part that made held items unusable, is resolved by #2. A per-mode warning would add checks for a temporary authoring state and is not worth it.

### 4. Muscle round-trip readout
Saving already resets the edit and re-seeds the palms from the saved clip, so the avatar shows the round-tripped pose right away. Any visible clamp shows up as a jump. For one-hand and slingshot holds, the item is placed from the evaluated palm (`ItemFromPalm`), so clamping moves the hand and the item together. The grip relationship stays exact, and a sub-centimetre difference from where the author dragged the palm doesn't matter. Two-hand holds are corrected by the anchor IK anyway. A numeric readout adds authoring UI with no practical payoff.

### 5. Body frame from the upper-arm midpoint
The shoulder anchor is intentional. The arm IK roots on `UpperArm`, and `HeldItemBodyFrame` is shoulder-based too. The shift between prepare and solve is exactly the in-frame shoulder motion that `Rebase` is meant to absorb. When a clip shrugs the shoulders, body-relative hand targets move with the shoulders the arms hang from, which is what they should do. The error doesn't accumulate. Rooting on the Chest would instead let reach change as the shoulders shrug. In the pose editor, weights and charge are forced, so the clip is static and `Body` is stable. Changing slots re-seeds. Ignoring spine lean matches `HeldItemBodyFrame`, which also uses root rotation.

### 7. FP two-hand charge pitch
This is a spec decision ("Charge pitch: third person only"). FP and TP are separate presentations with their own clips, and the FP two-hand charged clip is authored for the body-anchored rig. If you want FP two-hand throws to follow look pitch, scale the FP pitch in `SubmitTargets` by `HeavyFrameWeight` instead of zeroing it.

### 13. Slingshot hold mode on other items
Choosing this mode on another item is an author error, and the result is visible right away. `HoldMode` only selects a pose set. A future non-slingshot item could reasonably reuse the Slingshot mode's two-arm, no-IK clips. The pouch logic already requires a `SlingshotPresentation`. Clamping in `ItemDefinition` would tie the base class to a subclass for no runtime benefit.

### 14. Merging the two `HeavyBody` helpers
This duplication predates the change (it was `TwoHandHoldPresentation.Body`), and the conditions differ on purpose:
- `PlayerHandPresentation` picks owner or remote input by ownership.
- `HeldItemPresentationState` picks by whether its own binding is bound, which for the owner is the FP rig.

Merging them would mean reconciling those semantics, a behaviour change that fixes no defect.
