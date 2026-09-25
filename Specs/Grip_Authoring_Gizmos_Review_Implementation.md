# Grip Authoring Gizmos Review Implementation

Response to `Grip_Authoring_Gizmos_Review.md`. The review was written against the working tree before `d8702a3`; every finding was re-checked against that commit.

## Summary

| # | Finding | Result |
|---|---|---|
| 1 | Test values in the 138 Chill avatar asset | Fixed |
| 2 | Item with no `HoldClass` throws | Fixed (the class is required) |
| 3 | Two-hand spread interpolates from 0 | Fixed (different rule from the review's) |
| 4 | Grip frame sampled before the free-hand blend-out | Fixed |
| 5 | Stale Revert snapshots | Fixed |
| 6 | Bake captures the previous frame; keyboard swivel never bakes | Partly rejected; the swivel part is fixed |
| 7 | Two class slots pop on quick switching | Fixed (third slot) |
| 8 | Grip frame Z poorly defined | Fixed |
| 9 | Offset gizmos use the window's avatar id | Fixed |
| 10 | Offset gizmos depend on `committed.Item` | Fixed |
| 11 | Content refresh runs several times per edit | Fixed |
| 12 | Undo after Save isn't tracked | Partly fixed |
| 13 | Recompute Held Offset overwrites FP | Fixed |
| 14 | Pouch gizmo does nothing without a Slingshot Hold clip | Fixed |
| 15 | Look-through leaves the Scene view camera changed | Fixed |
| 16 | Leftovers | Fixed, except `Ghost.mat`, which is left for you to delete |

---

## Fixed

### 1. Test values in `44816e438f2e4775.asset`
Confirmed. It's the only avatar with a palm correction, and 0.74 m and 0.61 m are far outside any plausible value. `LeftPalmCorrection.Position`, `FirstPersonReachOffset` and `FirstPersonPlacementOffset` are reset to zero in the working tree. The rest of that file's diff (the `Driver*` fields and the re-serialised mesh indices) is untouched.

The review's design point (a Play Mode Inspector edit to `AvatarSettings` stays in the asset with no prompt) is handled by the finding 5 fix. Inspector edits to `AvatarSettings`, `HoldClass` and `ItemDefinition` made while the session is attached are now tracked by Save/Revert. `Docs/Grip_Authoring_Guide.md` is updated to match. It previously said these edits weren't tracked.

### 2. Item with no `HoldClass`
Confirmed that the state dereferences `Class` without a null check. The rule chosen is **the class is required**, the same as `WorldPrefab`: `HeldItemPoseData` already throws on an item with no prefab, so an item made from the asset menu was never usable before it was configured. `ItemSetup` assigns `Regular` to new items. `GripAuthoringBuildGuard` now fails the build and lists any registered item with no class, and the field has a "Required." tooltip. No runtime fallback was added.

### 3. Spread with only the Charged pose baked
Confirmed. `HoldClass.Spread` now returns 0 when `HoldSpread` is 0, so the two-hand IK is skipped until the Hold pose is baked. This follows the arm-layer rule: a slot plays nothing without a Hold clip, so the Charged spread shouldn't apply either. The review's version would instead IK the base-animation palms to the Charged spread.

### 4. Free-hand blend-out order
Confirmed against the diff: the anchor block had been moved above the free-hand solve. The previous order is restored: blend-out first, then the grip frame and the spread targets. In TP there's no Free target, so only FP is affected. In steady state with an FP Hold clip, the free weight is 0 and nothing changes.

### 5. Revert snapshots
Confirmed, with one correction. Domain reload runs on entering Play Mode (`EnterPlayModeOptions = 0`), so a snapshot doesn't carry across Play sessions. It does go stale within one session, including Edit Mode, where **Clear pose** and **Copy Hold → Charged** work. The post-Save embedded-Inspector case is confirmed.
- `HandPosePanel` no longer snapshots on every rebuild. `TouchIfModified` is deleted. Snapshots are taken only in `Touch`, and `Snapshot` is now private.
- The window hooks `Undo.postprocessModifications`. That runs before an Inspector edit is applied, so the first edit to a `HoldClass`, `ItemDefinition` or `AvatarSettings` while the session is attached snapshots the prior state.
- `RevertAll` also notifies reverted `AvatarSettings`, so the avatar rebinds.

### 6 (second half). Keyboard swivel never bakes
Confirmed in principle. The swivel sliders are now not focusable, so they can only be changed by pointer, and every change ends in the `PointerUpEvent` that bakes. That was simpler and safer than baking on `FocusOutEvent`: focus changes when you click into the Scene view, which could cut off a palm drag.

### 7. Class slot eviction
Confirmed: switching Regular → Heavy → Slingshot within the blend evicts a half-weighted slot, and `HeavyFrameWeight` jumps. This happens even now, with no clips baked. There are now three slots (`AvatarArmPose.Slots`), one per existing class, so a class already in a slot is always reused and nothing mid-blend is evicted. `AvatarArmLayers` has 6 mixer inputs per arm and subscribes to each distinct class once.

### 8. Grip frame Z
Confirmed. The threshold only caught an exact cancel. Z now blends from the body's forward to the fingers' direction as the projected finger sum grows from 0.2 to 0.6. The result stays in the plane ⟂ X, so the frame remains orthonormal.

### 9. Avatar id mismatch
Confirmed: `RequestAuthoringAvatar` goes through the appearance commit, so the rig changes avatar later than the window's selection. `TryGrip` now also returns the rig's avatar id, and the offset gizmos are hidden while it differs from the selected avatar.

### 10. `committed` cleared on content change
Confirmed. `RefreshContent` no longer resets `committed`. `PrepareHands` resets it and `CommitHands` rebuilds it every frame anyway, so the reset only created a gap between the editor event and the next frame.

### 11. Redundant refreshes
Confirmed. `GripAuthoringScene.ContentEdited` and all its calls are deleted, since the states' own `ContentChanged` subscriptions already cover them. `Watch` now subscribes once per asset when the selected and action fields hold the same item or class.

### 12. Undo after Save (partly)
Fixed: `UndoRedo` now notifies every registered item and each distinct class, not just the touched ones. An undo past a Save therefore rebinds the arm playables and refreshes grips.

Not fixed:
- **Re-touching after undo.** The only snapshot available at that point is the undone state. Revert would then "restore" the undone state and write it to disk, which is worse than leaving it untracked. The undone asset is marked dirty and saved the Unity way.
- **Orphaned `.anim` after undoing a saved bake.** It's a harmless file in `HeldPoses/`, and it only occurs after Save followed by Undo. Tracking it isn't worth the extra state.

### 13. Recompute Held Offset
Confirmed. `GenerateHeldOffset` still writes `ThirdPersonGrip`, but writes `FirstPersonGrip` only when FP is zero (a new item) or still equal to TP.

### 14. Pouch gizmo
Confirmed. `TryPouch` now fails, which hides the gizmo, unless the Slingshot class has a Hold clip for the current view. The guide's troubleshooting row says so.

### 15. Look-through camera
Confirmed. The window saves the Scene view's field of view, dynamic clip, near clip and orthographic flag when look-through starts. It restores them when look-through turns off, the view switches to TP, Play Mode exits, the window closes or a different Scene view takes over.

### 16. Leftovers
- `GripAuthoringScene.Avatars`, `GripAuthoringScene.SelectedItem` and `GripObserverPreview.ItemRoot` are removed.
- The `HandHoldPresentation` parameters are renamed to `hold`.
- `Assets/Game/Materials/Ghost.mat` (+ `.meta`) is confirmed unreferenced, but it's **not deleted**. It's untracked, so deleting it couldn't be undone through git. Delete it yourself.

---

## Rejected

### 6 (first half). Bake captures the pose from the previous frame
This isn't a defect. `EndPalmDrag` captures the muscles of the last evaluated frame, which is exactly the arm that was on screen when the mouse was released. The baked clip reproduces that arm, so it doesn't jump. The only thing a same-tick release loses is the final drag increment, which was never shown on the arm. The gizmo then snaps to the reached palm. Deferring the capture to after the next `CommitHands` would bake a pose the user never saw, and would add a pending-capture event path between the scene and the window.

## Note (expected migration state)
Agreed. With no clips, `HoldSpread` is 0, and after finding 3 the two-hand IK stays off until Heavy's Hold pose is baked. Boulder sits between base-animation palms until then. After finding 4, the FP boulder follows the FP free-rest palms instead of floating away from them.
