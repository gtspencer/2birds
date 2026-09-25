# Grip Authoring Gizmos Review

Scope: working-tree changes, excluding `.md` files. Checked against `Grip_Authoring_Gizmos_Plan.md`.

---

## 1. Test values committed to the 138 Chill avatar settings (High)

**Where:** `Assets/Game/Settings/Avatars/44816e438f2e4775.asset:16202-16207`

**Problem:** The diff sets `LeftPalmCorrection.Position = (0, 0.74, 0)`, `FirstPersonReachOffset = (0, 0.61, 0)` and `FirstPersonPlacementOffset = (-0.17, 0, 0)`. No other avatar serializes a palm correction. A 0.74 m shift of the left palm is almost certainly a leftover from testing the palm-correction Inspector in Play Mode.

**Why it matters:** `AvatarPalmCalibration.Measurements` moves this avatar's left palm 74 cm, which breaks every TwoHand, Slingshot and carry hold on it. If the Heavy pose is baked on this avatar, the 0.74 m error also goes into the measured `HoldSpread` that every other avatar is IK'd to. The underlying cause comes from the design: Play Mode now edits the real assets, and Save/Revert only tracks assets the tool touches. A Play Mode tweak to `AvatarSettings` stays in the asset without any prompt.

**Fix:** Revert these three fields unless they were meant to change. Then either have the tool track `AvatarSettings` edits during an authoring session (for example with `ObjectChangeEvents` → `GripAuthoringAssets.Touch`) so the exit dialog covers them, or document that palm-correction edits persist immediately.

---

## 2. An item with no `HoldClass` throws in the held-item state (High)

**Where:** `HeldItemPresentationState.cs`: 73-76 (`RecoveryFinished`), 201 (`SelectionChanged`), 228 (`ActionChanged`), 291-293 (`ChargeProgress`), 372 (`RefreshArms`), 453/456 (`SubmitFollow`). Also `ItemDefinition.HoldMode`.

**Problem:** `ItemDefinition.HoldMode` treats a null class as allowed (`HoldClass ? HoldClass.Mode : OneHand`), but the state reads `selectedData.Class.ReturnBlendDuration`, `actionData.Class.ChargePoseDuration` and similar without a null check. `ItemDefinition` has `[CreateAssetMenu]`, so an item made from the menu has no class. `SlingshotDefinition` no longer forces its mode either. The old `HeldItemSettings.Poses(mode)` returned a struct and could never be null. `Claim(null)` also takes an empty slot at weight 1, which inflates `ArmWeight`.

**Why it matters:** Equipping such an item throws every frame (`SetInput` → `SelectionChanged`, and `LateUpdate` → `CompleteAtDeadline` → `RecoveryFinished`). Held-item presentation for that player stops working.

**Fix:** Pick one rule. Either require the class (validate in `ItemRegistry.OnValidate` and in the build guard, and make `HoldMode` assume a class), or resolve a fallback in `HeldItemPoseData` (for example `definition.HoldClass ? definition.HoldClass : registry.DefaultHold`) so `Class` is never null downstream.

---

## 3. Two-hand spread interpolates from 0 when only the Charged pose is baked (Medium)

**Where:** `HoldClass.cs:19-24`, used at `HeldItemPresentationState.cs:425-434` and `AvatarArmIK.cs:46-51`

**Problem:** `Spread` returns `Lerp(HoldSpread, charged, charge)`. Suppose the Charged pose is baked before the Hold pose, or **Clear pose** has reset the Hold slot. Then `HoldSpread == 0` and `ChargedSpread > 0`, so charge 0.05 gives a spread of 5% of the charged spread. That value is above 0, so the IK runs (`reach = weight`) and the palms are pulled to almost the same point.

**Why it matters:** At the start of every charge, the palms snap together at the grip centre and then spread out again. This shows on every avatar, and in FP Live.

**Fix:** Treat a zero endpoint as not baked, on both sides:
```csharp
float hold = view.HoldSpread > 0f ? view.HoldSpread : view.ChargedSpread;
float charged = view.Charged && view.ChargedSpread > 0f ? view.ChargedSpread : hold;
return Mathf.Lerp(hold, charged, charge);
```

---

## 4. The two-hand grip frame is sampled before the free-hand blend-out, so the item leaves the FP hands (Medium)

**Where:** `AvatarArmIK.cs:39-58` (the anchor block moved above the free-hand solve), with `PlayerHandPresentation.cs:526-527`

**Problem:** In FP, a hand claimed by an item still gets its free-hand target at weight `1 - rig.PoseWeight(right)`. `PoseWeight` is the arm-layer weight, and it is 0 when the class has no FP Hold clip (true of every class right now) and partial while blending in. The new order computes `GripFrame`/`AnchoredItem` from the palms before the free-hand solve moves them. The old code computed the anchor after that solve.

**Why it matters:**
- **spread == 0** (Heavy has no pose yet, or the pose was cleared): nothing IKs the palms back. The boulder is anchored to the idle-clip palms while the visible hands sit at the FP free rest positions, so in FP the item floats away from the hands.
- **spread > 0, during blend-in:** the palms end between the free pose and the grip targets, but the item is at the full clip frame.

**Fix:** Run the free-hand blend-out first, then compute the frame, as before. In steady state the free weight is `1 - PoseWeight = 0`, so this changes nothing once a pose is baked. It only fixes the blend and the unbaked case.

---

## 5. Revert snapshots can be stale, and Revert writes them to disk (Medium)

**Where:** `GripAuthoringWindow.cs:245-247`, `GripAuthoringAssets.cs:95-113`, `131-142`

**Problem:**
- `HandPosePanel` calls `Snapshot(owner)` on every rebuild, including in Edit Mode, whether or not anything changes. The snapshot lasts until the next Save or Revert, possibly across several Play sessions. Suppose the class is later changed outside the tool (Project Inspector, or a pull) and then touched by a bake. **Revert** then copies the old snapshot back and `RevertAll` calls `SaveAssetIfDirty`, which writes the old values to disk.
- In the other direction, after **Save** clears the snapshots, the first embedded-Inspector edit goes through `TouchIfModified` with no snapshot. `Touch` then snapshots the already-edited state, so that edit can't be reverted.

**Why it matters:** Edits made outside the tool can be lost without warning, and **Revert** doesn't always restore what the user expects.

**Fix:** Snapshot only just before the first change to an asset. `EditItem` and `Slot` already do this. For edits in the embedded Inspector, hook `Undo.postprocessModifications` (it runs before the change is applied) and call `Touch` there instead of `TouchIfModified`. Drop snapshots of untouched assets on Save, on Revert and on leaving Play Mode.

---

## 6. The bake captures the pose from the frame before the last drag or swivel step (Medium-Low)

**Where:** `GripAuthoringScene.cs:242-259`, `GripAuthoringWindow.cs:257` and `400`

**Problem:** `EndPalmDrag` reads `CaptureMuscles()` during the MouseUp or PointerUp editor event. The transforms at that moment come from the last evaluated frame. If the last `DragPalm` or `SetSwivel` and the release are handled in the same editor tick, before the player loop runs, that last step never reaches the captured pose.

Also, a swivel change without a pointer (keyboard or navigation) starts an edit that never bakes. The next `BeginEdit` then throws it away without a message.

**Why it matters:** A fast release makes the arm jump a little. The plan's validation step 2 expects no jump.

**Fix:** Defer the capture. `EndPalmDrag` should only mark a capture as pending, and the scene captures after the next `CommitHands` (for example with `yield return null` in a coroutine, then raise an event the window uses to call `Bake`). Bake the swivel on `FocusOutEvent` or on the slider's `ChangeEvent` with a short debounce, not only on `PointerUpEvent`.

---

## 7. Two class slots make the arms pop when classes are switched quickly (Low-Medium)

**Where:** `HeldItemPresentationState.cs:271-278` (`Claim`)

**Problem:** Say two classes are mid-blend (for example Regular 0.5 and Heavy 0.5) and a third is selected. `Claim` takes the lower-weight slot and sets its weight and start weight to 0 at once. Half of the arm pose disappears, and `HeavyFrameWeight` drops, which also moves the FP rig placement. The old code had three fixed mode slots and no eviction.

**Why it matters:** Scrolling the hotbar quickly (Rock → Boulder → Slingshot within 0.2 s) makes the arms visibly pop.

**Fix:** Evict only a slot whose weight is near 0, and wait (keep blending) until one is. Or add a third slot, since three classes exist. Or fold the evicted slot's weight into the remaining one before the new blend starts.

---

## 8. The grip frame's Z axis is poorly defined when the fingers point toward each other (Low-Medium)

**Where:** `HeldItemPose.cs:81-89` (`GripFrame`)

**Problem:** Palm +Z is the finger direction (`AvatarProcessor.MeasurePalm`). If a two-hand hold has the fingers pointing inward or around the item (for example cradling a boulder), the two +Z vectors nearly cancel after projection onto the plane ⟂ X. The `1e-6` fallback threshold only catches an exact cancel, so the small leftover vector decides Z.

**Why it matters:** The item's rotation can jitter or flip 180° with small palm movements. This affects both the runtime pose and the item-offset gizmo frame.

**Fix:** Blend toward the body's forward (projected onto the plane ⟂ X) as the finger-sum magnitude falls, for example `z = Vector3.Slerp(bodyZ, fingerZ, Mathf.InverseLerp(0.2f, 0.6f, fingerSum.magnitude))`. At minimum, raise the threshold.

---

## 9. Offset gizmos use the window's avatar id, but the rig uses the bound avatar id (Low)

**Where:** `GripAuthoringWindow.cs:296-309` and `424-439`, versus `GripAuthoringScene.cs:197-203`

**Problem:** `TryItem` builds the item pose from `binding.Id`. `OffsetGizmos`/`AvatarGrip`/`SetAvatarGrip` use the window's `avatarId`. `RequestAuthoringAvatar` goes through the network appearance commit, so it doesn't take effect at once, and it returns early without a message if the entry doesn't resolve. While the two ids differ, a drag computes O or D against the wrong avatar's offset.

**Why it matters:** The item jumps on the first drag event, or the offset is written to the wrong avatar's entry.

**Fix:** Have `TryGrip`/`TryItem` also return the rig's `AvatarId`. Use that id in the gizmo math, and disable avatar-offset editing while it differs from the window's selection.

---

## 10. Offset gizmos depend on `committed.Item`, which every edit clears (Low)

**Where:** `GripAuthoringScene.cs:181-182` (`Showing`), `HeldItemPresentationState.cs:204-212` (`RefreshContent`)

**Problem:** Each gizmo change calls `EditItem`. That calls `item.NotifyContentChanged()` (then the Watch callbacks call `RefreshContent`) and `ContentEdited()`, and `RefreshContent` sets `committed = default`. Until the next `CommitHands`, `TryGrip`/`TryItem` return false, so any Scene view event handled before the next player frame skips the handle.

**Why it matters:** The handle may flicker during a drag, and drag steps can be dropped while the editor is busy.

**Fix:** Base `Showing` on `state.CanShowHeldItem` plus a "has committed since bind" flag. Alternatively, don't reset `committed` when only the offset changed.

---

## 11. Content refresh runs several times per edit (Low, simplification)

**Where:** `GripAuthoringScene.ContentEdited` and its callers (`GripAuthoringWindow.cs:124`, `128`, `146`, `333`); `HeldItemPresentationState.Watch` (159-179)

**Problem:** The states now subscribe to the item's and class's `ContentChanged` themselves, so `ContentEdited()` repeats work that has already happened. When the selected and action definitions are the same asset (the usual case), each state also subscribes twice. One offset drag event runs `RefreshContent` about 3 times per state, and each run rebuilds `HeldItemPoseData` (`GetComponent` + `CacheReleaseGeometry`).

**Fix:** Delete `ContentEdited` and its calls. Subscribe only once when `selectedDefinition == actionDefinition`, or have one watcher over the union of both.

---

## 12. Undo after Save isn't tracked (Low)

**Where:** `GripAuthoringWindow.cs:121-125`, `GripAuthoringAssets.cs:115-129`

**Problem:** Save clears `touched`. After that, Ctrl+Z restores the asset in memory, but `Notify()` only notifies touched assets. The class never raises `ContentChanged`, so `AvatarArmLayers` keeps the old playables for a clip whose curves Undo just restored in place. `hasUnsavedChanges` stays false, so there's no prompt on exit. Undoing a bake that created a clip also leaves an unreferenced `.anim` in `HeldPoses/` once it's saved.

**Fix:** In `UndoRedo`, always notify the selected item and its class, and `Touch` them again. Keep the list of clips created in the session after a save, so orphans can be found.

---

## 13. "Recompute Held Offset" overwrites the first-person grip (Low)

**Where:** `ItemSetup.cs:277`

**Problem:** `GenerateHeldOffset` writes the value it derives from TP into both `ThirdPersonGrip` and `FirstPersonGrip`. FP is now authored separately, so recomputing on an existing item throws away the FP offset without warning.

**Fix:** When recomputing an existing item, write only `ThirdPersonGrip`. Also write FP if it was equal to TP or the item is new.

---

## 14. The pouch gizmo can't change anything until the Slingshot class has a Hold clip (Low)

**Where:** `GripAuthoringWindow.cs:441-447`, `HeldItemPresentationState.cs:522`

**Problem:** The pouch attaches to the palm only through `attach = ClassWeight` when the class view has a Hold clip. The Slingshot class has none yet, so the pouch stays at rest while the gizmo is drawn at `palm + PouchOffset`, and dragging it has no visible effect.

**Fix:** Hide the pouch gizmo, or show a "author the Slingshot Hold pose first" label, while `attach` is 0.

---

## 15. Look-through leaves the Scene view camera changed (Low)

**Where:** `GripAuthoringWindow.cs:357-370`

**Problem:** `LookThrough` sets the Scene view's field of view, `dynamicClip = false` and `nearClip`. Nothing restores them when the toggle is turned off, the view switches to TP, or Play Mode ends. These settings are saved with the Scene view.

**Fix:** Store `cameraSettings` when look-through starts and put them back when it stops.

---

## 16. Leftovers (Low)

- `Assets/Game/Materials/Ghost.mat` (+ `.meta`) is untracked and nothing references it (the plan marks it unused). Delete it instead of adding it.
- `GripAuthoringScene.Avatars` and `GripAuthoringScene.SelectedItem` are never used. `GripObserverPreview.ItemRoot` is also unused.
- The `HandHoldPresentation.Reach/ReturnProgress/Frame/Sample` parameters are still named `poses`/`settings` but now take a `HoldClass`.

---

## Note (expected migration state, not a defect)

No class has clips yet, and `HoldSpread` is 0. In the Game scene, TwoHand items (Boulder) now sit at `G ∘ C` between base-animation palms with no IK. The old code IK'd the palms onto the contacts. Gameplay for heavy items looks worse until the Heavy TP and FP poses are authored and saved. In FP this is also affected by finding 4.
