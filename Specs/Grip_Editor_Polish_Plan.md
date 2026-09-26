# Grip Editor Polish Plan

Scope: `Assets/Game/Editor/GripAuthoringWindow.cs` and the runtime pieces it drives (`GripAuthoringScene`, `HeldItemPresentationState`, `GripSlotRig`, `SessionController`).

---

## 1. Mirror left/right hands

A **Mirror left/right** toggle in the held panel. Dragging either hand (or elbow hint) drives its partner as a reflection.

### Scope
- Heavy hold mode only. Slingshot's left hand sits on the pouch, so mirroring is meaningless there.
- Hold and Charged phases only (the rig is locked and editable).
- Pairs: `RightHand ↔ LeftHand`, `RightElbowHold ↔ LeftElbowHold`, `RightElbowCharge ↔ LeftElbowCharge`.
- World-contact (steering wheel) mirroring is out of scope; it needs a shared mirror frame for both contacts.

### Mirror planes
- **Hands** mirror across the item's local YZ plane. Both hand targets are children of `ItemAnchor` and are stored item-relative, shared by Hold and Charged, so item-space mirroring stays symmetric in both phases. Body-space mirroring would only be symmetric in the phase it was authored in.
  - Assumes the item's local X is its left/right axis.
- **Elbow hints** mirror across the body midline: both live under the slot root, which sits under the rig frame (hips position, animator rotation).
- Reflection: position `(-x, y, z)`, rotation `(qx, -qy, -qz, qw)`. This matches the palm-frame convention (forward = fingers, up = palm normal, mirrored consistently per hand in `AvatarProcessor.MeasurePalm`).

### Changes
1. **`GripSlotRig`** (instrumentation)
   - `Slot.Synced` (`Pose[TargetCount]`): last-synced local pose per target.
   - Update `Synced[i]` at the end of each target in `Apply`, and in `FollowAutomaticHint`, so reapply and automatic hint-follow are never read as user drags.
   - `internal bool Mirror`: enabling re-baselines both slots from the current transforms.
   - `internal void MirrorFollow(Slot slot)`: no-op unless `Mirror && slot.Mode == Heavy`. Per pair, if exactly one side differs from `Synced` (`GripPoses.Differs` for hands, position-only for hints), write `Reflect(moved)` to the partner's local pose. Then re-sync both. If both moved (undo, reapply, multi-select), only re-sync.
   - `static Pose Reflect(Pose)`.
2. **`HeldItemPresentationState`**
   - `internal bool AuthoringMirror { set => rig.Mirror = value; }`
   - In `PoseHands`: `if (rig.Locked) { rig.MirrorFollow(slot); if (binding != null) FollowAutomaticHints(binding); }`. Mirroring runs before targets are read (same-frame), and before automatic hint-follow so a mirrored hint is not overwritten.
3. **`GripAuthoringScene`**
   - `public bool Mirror { get; private set; }` and `SetMirror(bool)`, which calls `ApplyPhase(false)`.
   - In `Apply`, before the `reapply` early-out: `state.AuthoringMirror = Mirror && state == edited;`
4. **`GripAuthoringWindow`**
   - `[SerializeField] private bool mirror;`
   - `HeldPanel`: toggle shown when `item.HoldMode == Heavy` and `phase != Live`, with a tooltip.
   - `SceneChanged`: `scene.SetMirror(mirror)`.

The mirrored partner becomes dirty, so the existing Save buttons persist both sides.

The check is a per-frame compare because scene-view handle drags have no reliable post-apply event in Play Mode. It runs only while Mirror is on and the rig is locked.

---

## 2. Focus loss disables gameplay input (Release, Throw, Walk, Enter seat)

### Cause
Clicking the Grip Authoring window takes focus from the Game view:
- `InputPresentation.FocusChanged` → `Interrupted` → `SessionController.InputInterrupted` (`SessionController.cs:499`).
- That calls `localInput.ClearContext()`, which cancels any charge including `AuthoringCharge`.
- It then calls `SetPanel(true)`, which turns gameplay off via `RefreshGameplay`.

No pause panel appears because `SessionOverlay` only exists in `Game.unity`, not in `AvatarPresentationDemo`. `PanelOpen` is set with nothing to show it. Nothing can clear it either, because Escape handling also lives in `SessionOverlay`.

With gameplay off:
- `PlayerInputReader.ReadInput` cancels any charge every frame. The `AuthoringCharge && SessionInputAvailable` guard fails because `SessionInputAvailable` is false. Hold is cancelled immediately, so Release and Throw have nothing to end.
- `Consume()` returns zero movement, so Walk does nothing.
- `PlayerSeating.Request` returns silently on `!Input.GameplayActive`, so Enter seat does nothing (see §7).

### Fix
- `SessionController.InputInterrupted`: under `UNITY_INCLUDE_INSTRUMENTATION`, return early while `GripAuthoringScene.Instance` is attached (skip both `ClearContext` and `SetPanel`).
- Walk toggle:
  - When enabled from the window, focus the Game view (`EditorApplication.ExecuteMenuItem("Window/General/Game")`), since WASD and F2 only reach the game with Game view focus.
  - Tooltip: "Walk with WASD in the Game view. F2 toggles while the Game view has focus."

---

## 3. Actions section

These buttons drive the real gameplay pipeline on the local player to preview authored grips in motion. Every action switches to the Live phase first (`GripAuthoringScene.Act`).

| Action | Behavior |
|---|---|
| Equip / Dequip | Supply and select the item / select no slot |
| Hold | Begin charge and keep holding |
| Release | End charge (throw or fire) |
| Throw | Hold for `max(ThrowChargeTime, ChargePoseDuration)`, then release |
| Cancel | Cancel the charge |
| Drop | Drop the selected item |
| Use | Direct use, which only works for `PotionDefinition` (`PlayerInventory.ConsumeEquipped`) |
| Auto re-equip | Re-supply the item 1 s after it leaves the inventory |

### Fix
- Rename **Use** to **Drink**, enabled only when the selected item is a potion.
- Add tooltips to each action.

---

## 4. Phases

- **Live**: the normal runtime presentation.
  - Charge, grab and draw come from the real item action.
  - The rig is unlocked, and first-person release clearance can shift the item.
  - Preview only: drags are not kept and the Save buttons are disabled.
- **Hold**: frozen at charge 0, rig locked, editable.
- **Charged**: frozen at charge 1, rig locked, editable.

### Fix
In Live, replace the Save row, the mirror toggle and the copy-to-items section with the note "Preview only — switch to Hold or Charged to edit."

---

## 5. Save button tooltips

Resolution order: Avatar override → Item default → Slot default.

Save writes only dirty (●) targets of the current phase, and only for the current view (first/third person). Slot-frame positions are normalized by avatar height, so item and slot defaults scale across avatars.

| Button | Tooltip |
|---|---|
| Save for avatar | Override for this avatar and this item only. Highest priority. |
| Save as item default | This item, all avatars, unless an avatar has its own override. |
| Save as slot default | Fallback for every item using `<HoldSlot>` when neither the item nor the avatar defines it. |
| Save for avatar (contact) | Contact prefab override for this avatar. |
| Save as contact default | Contact prefab default for all avatars. |

---

## 6. Clear / Copy to other view always disabled

These buttons work as coded (`GripAuthoringWindow.cs:295-296`) but are unclear:
- **Clear** removes a *saved* entry from the layer it resolves from.
  - Disabled when the source column shows **None** (nothing saved yet).
  - Disabled for Slot-layer required targets (hands, hold and charge poses), since clearing those leaves every item on that slot undefined.
- **Copy to other view** copies a *saved*, *unmodified* pose to the other view. It is disabled as soon as the row is dirty.
- Dragging never enables either button.

### Fix
- Per-row **Reset** button, enabled when dirty. It restores the target to its last applied pose:
  - held targets: new `GripSlotRig.ResetAuthored(slot, target)`, which sets the local pose from `Records[i].Stored`;
  - contacts: `AvatarHandContact.ResetAuthored(target)`, which restores `appliedPalm` / `appliedHint`.
- State-dependent tooltips on Clear and Copy, set in the row refresher:
  - "Nothing saved to clear"
  - "Slot defaults for required targets can't be cleared"
  - "Remove the {layer} entry for this view"
  - "Save before copying"

---

## 7. World contact: avatar does not enter the golf cart

### Cause
- Switching to World contact never seats the player; seating only happens through the **Enter seat** action.
- **Enter seat** calls `PlayerSeating.Request`, which returns silently when `!Input.GameplayActive`. That is always the case after the window takes focus (§2).
- The hands still reach the wheel because contact authoring poses them from `AvatarHandContact` directly (`LiveView`, `observer.ContactSource`), independent of seating.

### Fix
- §2 fix, which unblocks `Request`.
- `GripAuthoringScene.SetMode(WorldContact)`: after the cart is found, call `EnterSeat()` so the player is seated as driver automatically. Leaving the mode already calls `ExitSeat()`.
- `EnterSeat`: when `seating.Request` can't run (not gameplay-active, transition pending), set `Message` so the reason shows in the readout instead of failing silently.

---

## 8. Copy grip to specific items

Author the grip on one item and avatar, then apply it to a chosen set of other items.

### UI (held panel, below the Save buttons)
- A `Foldout` titled **Copy to items (N selected)**, collapsed by default.
  - **All / None** buttons.
  - One `Toggle` per compatible item, labeled with the item name.
  - Compatible means the same `HoldSlot` asset as the current item, excluding the current item. This also guarantees the same hold mode and target set.
  - Selection is persisted in `[SerializeField] private List<int> copyItems`.
- Two buttons: **Copy for avatar** and **Copy as item default**. They are disabled when nothing is selected or the phase is Live.

### Behavior
- **Source**: every target the item's mode uses (all phases, not just the current one), current view only (same as Save).
  - Takes what is shown in the rig (`TryTarget` stored pose, including unsaved drags).
  - Includes only targets that are saved (`layer != None`) or dirty, so automatic, unset elbow hints are not copied.
  - The source item itself is not saved by this action.
- **Destination per selected item** via `GripAuthoringAssets.Edit(item, "Copy grip to items", …)`:
  - **Copy for avatar**: `GripPoses.Ensure(item.AvatarGripPoses, avatarId).Set(target, firstPerson, pose)`.
  - **Copy as item default**: `item.GripPoses.Set(target, firstPerson, pose)`.
- **Shadow warning**: for **Copy as item default**, list destination items whose avatar-override table for the current avatar already defines a copied target (the override would keep winning). Ask to continue.
- Changes go through the existing unsaved-asset tracking (Save/Revert toolbar, exit-play prompt).
- Hand targets are item-relative, so the copy suits items with similar grip geometry. Positions in the slot frame are already height-normalized.

### Changes (`GripAuthoringWindow` only)
- `CopyToItemsPanel(VisualElement panel, ItemDefinition item, GripAuthoringScene scene)`, called from `HeldPanel`.
- `CopyToItems(GripLayer layer)`: gathers source poses and edits each selected item.


### Agent Output:
What changed
- Focus fix (SessionController.InputInterrupted): while grip authoring is running, the Game view losing focus no longer cancels your input or quietly turns gameplay off. This should fix Release, Throw, Walk and Enter seat.
- Golf cart (GripAuthoringScene): switching to World contact now seats you as the driver automatically. If Enter seat can't run, the readout says why.
- Mirror left/right (two-handed items only, in Hold and Charged): dragging one hand or elbow moves the other as a reflection. Hands mirror across the item's local X axis; elbows mirror across the body's centre. The mirrored side shows as changed (●), so Save stores both.
- Reset button on every held and contact row: discards the unsaved drag on that row only, even with Mirror on.
- Tooltips on Clear and Copy to other view now say why they're disabled. The Save buttons, Actions, Walk and Auto re-equip also have tooltips.
- Use is now Drink, enabled only for potions.
- Walk: turning it on from the window also focuses the Game view.
- Live phase: shows "Preview only — switch to Hold or Charged to edit" in place of the Save buttons, Mirror toggle and Copy to items.
- Copy to items: a collapsible list of items that use the same hold slot, with All/None, and two buttons: Copy for avatar and Copy as item default. It copies what the rig currently shows, for the current view. "Copy as item default" warns first if a chosen item has an override for this avatar that would keep winning. Copies show up in the unsaved list, so Save and Revert cover them.