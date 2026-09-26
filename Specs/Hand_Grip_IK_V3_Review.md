# Hand Grip IK Review

Scope: the working-tree changes (everything except the `.md` files), measured against `Hand_Grip_IK_Spec.md` / `Hand_Grip_IK_Plan.md`.

Overall the rewrite matches the plan. It removes the arm clip layers, the anchor and the muscle baking without leaving references behind, and it keeps the `HoldClass` → `HoldSlot` script GUID (`952f7e1d…`), so the three hold assets and `ItemRegistry.CarryHold` still resolve. The `ItemSetup` migration math is correct (new `RightHand` = inverse of the old item-in-palm grip). The findings below are ordered by importance.

---

## 1. Slingshot release snaps the left hand forward to the rest pouch (High)

**Where:** `Runtime/Player/HeldItemPresentationState.cs:371-375` (`RefreshCharge`, slingshot recovery sets `draw = 0f`), `:468` / `:476` (`PoseHands` anchors the pouch with `draw` and reads the left target from `Pouch/LeftHand`), `Runtime/Player/GripSlotRig.cs:108-112`.

**Problem:** During `SlingshotRecovery`, `draw` is forced to 0 on the first recovery frame. `rig.Anchor` then puts `Pouch` back at `RestOffset`. The left IK target is `Pouch/LeftHand`, so it jumps from the fully drawn position to the forks in a single frame. Meanwhile `leftWeight = grab = 1 − t` is still ≈ 1. Nothing smooths the Item source (`arm.Weight` smoothing only applies to Contact), so the left arm visibly snaps forward and only then fades to free.

**Why it matters:** This happens on every slingshot shot, locally and on remote clients. Physically, the draw hand stays where it let go; it doesn't follow the pouch forward to the forks.

**Fix:** When `SlingshotRecovery` begins, capture the left target's pose relative to `rig.Frame` and keep it for the rest of recovery while `leftWeight` fades. It can reuse the `sourceLeft` path that switch blends already use. The pouch/band visual can keep `draw = 0` because `SlingshotPresentation.Evaluate` runs its own recoil. Related detail: `CommitHands` passes `releaseCharge` as the recoil amplitude (`:556`). That is charge progress, not draw, so a release during the grab window shows a strong recoil for a pouch that barely moved. Pass the draw value at release instead.

---

## 2. `holdWeight` snaps whenever visibility changes outside a selection blend (Medium)

**Where:** `HeldItemPresentationState.cs:384-394` (`RefreshWeights`), `:108-110` (`CanShowHeldItem` includes `Emoting` and non-slingshot `Recovering`), `Runtime/Player/PlayerHandPresentation.cs:486` (`HoldBob`).

**Problem:** `holdWeight = Lerp(startRightWeight, show ? 1 : 0, switchBlend)`, and `switchBlend` is 1 unless `BeginBlend` ran. Only selection changes call `BeginBlend`, so any other change to `show` makes the weight jump:
- **Emote start (third person):** the item target weight goes 1 → 0 in one frame while `free = 1 − EmoteWeight` is still ≈ 1, so the arm pops from the grip pose to the locomotion arms before the emote blends in. Before this change, the arm clip layer kept its weight and faded with `free`.
- **Throw release / recovery end (first person):** `HoldWeight` drops 1 → 0 at release and returns 0 → 1 when the next item shows. `HoldBob` scales the first-person rig offset by this weight, so the whole first-person rig (free hands and next item) jumps by the current bob offset. Before this change, `ArmWeight` followed the return progress.

**Why it matters:** These are visible pops in common actions, and they are regressions from the old behaviour.

**Fix:** Stop using a one-shot lerp for `holdWeight`/`leftWeight`. Move them toward their goal at a rate (for example `MoveTowards` over the slot's `ReturnBlendDuration`), or call `BeginBlend` whenever `show` flips. For `HoldWeight` during Following, reuse the return progress as the old `ArmWeight` did.

---

## 3. Authoring: unsaved transform drags are silently discarded (Medium)

**Where:** `Runtime/Authoring/GripAuthoringScene.cs:153-164` (`SetPhase`, `SetView`), `:193-208` (`ApplyPhase` → `Apply` → `ReapplyAuthored()` on the edited state).

**Problem:** Every `ApplyPhase` force-reapplies the edited rig before re-locking it, which overwrites every dragged transform. That covers `SetPhase`, `SetView`, `SelectItem`, `RebuildStrip`/`SelectAvatar` and `LocalBound`. Plan decision 8 says item, avatar, view and slot changes force a reapply, but it doesn't list **phase**. `RightHand`/`LeftHand` are shared between Hold and Charged, so "adjust the hand in Hold, flip to Charged to check it" throws the adjustment away. `hasUnsavedChanges` only tracks asset edits, so no prompt appears.

**Why it matters:** Authoring work is lost without warning in the tool's main workflow.

**Fix:** Don't force-reapply on a phase change. The lock already keeps the transforms, and only charge/draw differ between phases. For view, item and avatar changes, check the current rows for `dirty` first and prompt (Save / Discard / Cancel) or block the change.

---

## 4. Authoring: saving to a lower layer than the resolved layer looks like it reverts (Low–Medium)

**Where:** `Editor/GripAuthoringWindow.cs:304-325` (`SaveHeld`).

**Problem:** If a target currently resolves from the Avatar layer and the user drags it and clicks **Save as item default** (or Slot), the value goes into the item or slot table. `scene.Reapply()` then resolves the Avatar override again, so the transform snaps back. From the user's point of view, the edit disappeared. In reality it silently changed the default for every other avatar.

**Why it matters:** Shared defaults change unintentionally, and the result is confusing.

**Fix:** Disable the save buttons below the row's source layer, or warn per row. Alternatively, offer "save here and clear the higher layer".

---

## 5. World-contact authoring shows the dormant owner body on top of the observer (Low–Medium)

**Where:** `GripAuthoringWindow.cs:82`, `:172` (`HideOwner(scene && Held)`), `GripAuthoringScene.cs:183` (`observer.SetLateral(0)`).

**Problem:** Per plan decision 1, the observer at zero lateral offset is the third-person body in the driver seat, and the owner's own third-person body stays dormant (no Item or Contact targets). But `HideOwner` only hides the `GripAuthoringOwner` layer in held-item mode. In contact mode, the Scene view therefore renders two avatars in the same seat: the observer with hands on the wheel, and the owner with un-IK'd seated arms. The "HeldItem only" rule comes from the spec's original approach, which decision 1 replaced.

**Why it matters:** It's hard to judge the result or click the palm and elbow transforms.

**Fix:** Call `HideOwner(scene)` in both modes.

---

## 6. Clavicle rotation can be applied twice per arm per frame (Low)

**Where:** `Runtime/Avatars/AvatarArmIK.cs:37-40`, `:73-79`.

**Problem:** When the resolved source is `Item`, the arm is first solved to the Free target and then to the Item target. Both `Solve` calls prepend a clavicle rotation (`... * arm.Clavicle.rotation`). If both wrist targets are past 85 % of arm length, the clavicle rotates twice: up to 2 × `MaxClavicleAngle`, partly toward the Free target. In practice first-person free targets rarely pass the threshold, so this is latent.

**Fix:** Apply the clavicle only in the final solve for each arm, or compute it once from the winning target before the pre-solve.

---

## 7. `PrepareHands` uses hint weights and follow destinations from the previous frame (Low)

**Where:** `HeldItemPresentationState.cs:416-421` (`targets.Set(..., hintWeight: rightHintWeight)`), `:432-436` (`SubmitFollow` destinations), `:494-496` (`PoseHands` writes them afterwards).

**Problem:** `PrepareHands` runs before `PoseHands`. The IK reads the hint *transforms* by reference, so their positions are current, but it copies the hint *weights* by value at `Set` time, so they are one frame old. Follow `destinationRight/Left` are likewise read from the anchor pose of the previous `PoseHands`. The effect is a one-frame lag. It is mostly invisible, but there is a small jump when Return ends and the live item target takes over while walking.

**Fix:** Have `PoseHands` push the hint weight into `AvatarHandTargets`, for example with a small `SetHintWeight(hand, source, weight)`. Alternatively, sample follow destinations in `PoseHands`.

---

## 8. `AvatarHandContact.LiveView` is set again during teardown (Low)

**Where:** `GripAuthoringScene.cs:401-409`.

**Problem:** `OnDestroy` clears `LiveView = null` and then calls `ApplyPhase()`. In world-contact mode, `ApplyPhase()` → `ApplyContacts()` sets `LiveView = FirstPerson` again. It also writes authored poses into the cart's contact transforms. The static stays set for the rest of the play session, so any later cart reads the live transforms instead of the authored data.

**Fix:** Clear `LiveView` after `ApplyPhase()`, or skip `ApplyContacts()` while destroying.

---

## 9. Undo after Save doesn't refresh the live contacts (Low)

**Where:** `Editor/GripAuthoringAssets.cs:97-100` (`Reset` clears `pairs`), `GripAuthoringWindow.cs:118-127`.

**Problem:** `UndoRedo` depends on `SyncPairs()` to copy prefab-asset poses into the live contacts. `SaveAll()` → `Reset()` clears `pairs`. So an undo after Save restores the prefab asset, but the live steering-wheel contacts keep the saved values. The comment "including ones Save stopped tracking" is true for items and slots but not for contacts.

**Fix:** Keep `pairs` across `SaveAll` (clear it only on play-mode exit), or have `UndoRedo` re-read the scene's contacts from their source prefab.

---

## 10. Slingshot readout reports the left hand while it isn't engaged (Low)

**Where:** `HeldItemPresentationState.cs:529-531`.

**Problem:** `hasLeft = Mode != Hand`, so in the Hold phase (and whenever not charging) the slingshot checks reach for `Pouch/LeftHand`, even though the left hand is free (`leftWeight == 0`). The window can show "Unreachable palm: Left" for a hand that isn't targeted.

**Fix:** Use `hasLeft = leftWeight > 0f` (or `Heavy || Slingshot && ChargeActive`).

---

## 11. Dead charge bookkeeping during throw follow (Low, simplification)

**Where:** `HeldItemPresentationState.cs:244-246`, `:376-380`.

**Problem:** For a non-slingshot recovery, `chargeItem` is set to 0, so `ChargeActive` is false and `PoseHands` anchors with charge 0. Hints come from the follow capture, and `pull`/`releaseCharge` are slingshot-only. `charge`, `startCharge` and the Following branch of `RefreshCharge` are computed but never read.

**Fix:** Remove the Following branch and the `startCharge`/`releaseCharge` assignments in the non-slingshot Recovering branch.

---

## 12. Contact poses are re-resolved every frame (Low, performance/style)

**Where:** `Runtime/Avatars/AvatarHandContact.cs:28-56`, `Runtime/Avatars/HandContactPresentation.cs:40-47`.

**Problem:** On every frame, for each hand and each player near a cart, `Palm()`/`TryHint()` do a `GripPoses.Find` list scan plus two `TryGet` scans. `HandContactPresentation` already re-caches `BlendTime`/`MaximumReach`/`Fingers` when the bound contact changes. The resolved local palm and hint poses could be cached the same way, keyed by contact, avatar and view, and composed with the parent or hint frame each frame. AGENTS.md prefers caching over per-frame lookups.

---

## 13. Naming traps (Low)

- `Runtime/Items/ItemDefinition.cs:51-52`: the field `GripPoses` has the same name as the static class `GripPoses`, and the field `AvatarGripPoses` has the same name as the type `AvatarGripPoses`. This compiles today, but inside `ItemDefinition` any future `GripPoses.Scale(...)`/`TryResolve(...)` call binds to the field and fails. Consider `DefaultGrips` / `AvatarGrips`.
- `Runtime/Items/HeldItemPose.cs:42`: the bone constructor sets `Hips` to the shoulder midpoint. No caller reaches it today, because `PoseHands` uses the Hips bone whenever a binding exists. But `lastBody.Hips` is the fallback slot frame, so a future caller would silently get shoulder height. Either compute it from the binding or leave it out of that constructor.

---

## 14. Authoring leaves contact transforms modified (Low)

**Where:** `AvatarHandContact.cs:77-89`, `GripAuthoringScene.cs:178-182`.

**Problem:** `ApplyAuthored` overwrites the live contact's `localPosition/localRotation`. Switching back to held-item mode doesn't restore `rest`. For a view with no authored palm, `Palm()` falls back to `transform`, which now holds the other view's value. This only affects the authoring session.

**Fix:** Restore `rest` when leaving contact mode, or keep the authored palm on a separate child transform, as the elbow hint already does.

---

## 15. Unrelated files in the working tree (Low, hygiene)

- `Assets/Game/Materials/Ghost.mat` (untracked) isn't referenced by any asset or script.
- `Assets/Settings/UniversalRenderPipelineGlobalSettings.asset` has a single re-added `rid`, which looks like editor noise.

Keep both out of this change unless they are intentional.

---

## Notes (expected, not defects)

- Until slot defaults are authored, the build guard fails for `Regular`, `Heavy` (also `CarryHold`) and `Slingshot`. Required targets resolve to identity, so held items sit at the hip follower or first-person rig origin.
- Until the contacts are authored and `HintFrame` is set on the prefab, the steering-wheel palms fall back to the contact transforms without the removed `DriverHandOffset`, and the elbow hints rotate with the wheel.
