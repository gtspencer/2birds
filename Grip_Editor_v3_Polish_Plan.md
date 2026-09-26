# Grip Editor v3 Polish — Plan

Source: `Grip_Editor_v3_Polish.md`. Held-item mode only; World contact mode keeps its two save buttons.

## Decisions

| Topic | Decision |
|---|---|
| Layers | Runtime still resolves Avatar → Item → Slot. The window writes the **Avatar** layer. The Item layer becomes read-only (Inspector-editable). The Slot layer stays authorable only through a collapsed **Advanced → Save as slot default**. |
| Save flow | Two-stage stays. Panel **Save** commits drags into the asset in memory; toolbar `Save (n)` is renamed **Write to disk (n)**, next to **Revert**. |
| Save scope | Save commits every changed (●) target in the **current view, all phases**, because Copy to Hold/Charged can leave ● on the phase you aren't looking at. |
| Fingers | One clip per item × avatar (both hands, both views, both phases), stored on the avatar entry. Resolves avatar → `ItemDefinition.GripFingers` → `HoldSlot.Fingers` → registry `GripFingers`. |
| Copy to items/avatars | Copies the **full resolved entry**: every used target in **both views**, plus fingers, baked into the destination's Avatar layer. Disabled while anything is ●. |
| Copy to other view | Available on every row, plus a whole-phase button. Writes to the Avatar layer of the other view. |
| Rename | Only the `Hold` **action button** becomes `Charge`. Phases stay Live / Hold / Charged. |
| Generator | Edit-mode editor window with its own scene. Not usable in Play Mode. |

## 1. Data: per-avatar fingers

`Assets/Game/Runtime/Items/GripPoses.cs`
- `AvatarGripPoses`: add `public AnimationClip Fingers;`.
- Add `FindEntry(List<AvatarGripPoses>, AvatarId)` returning the entry; `Find` and `Ensure` use it.
- Add `AnimationClip ResolveFingers(ItemDefinition item, AvatarId avatar, out GripLayer layer)`: avatar entry `Fingers` → `item.GripFingers` (Item) → `item.HoldSlot.Fingers` (Slot) → `null` / `None`. The caller falls back to the registry default.

`Assets/Game/Runtime/Items/HeldItemPose.cs`
- Remove `HeldItemPoseData.Fingers`, because it can't know the avatar.

`Assets/Game/Runtime/Player/HeldItemPresentationState.cs`
- Add `private AnimationClip selectedFingers;`. Set it in `ApplySlot` via `GripPoses.ResolveFingers(selectedDefinition, AvatarKey, out _)`. `ApplySlot` already runs on bind, selection and content change, so nothing is added to the per-frame path.
- `SubmitTargets`: `var fingers = selectedFingers ? selectedFingers : clips.GripFingers;`.
- `SubmitTargets`, left hand: replace `selectedData.Mode != HoldSlotMode.Slingshot ? fingers : SlingshotRecovery ? clips.OpenFingers : clips.GripFingers` with `SlingshotRecovery ? clips.OpenFingers : fingers`. The slingshot pouch hand then uses the left-hand half of the resolved clip while pinching, and still opens on release. The current slingshot has no item or slot fingers, so it resolves to `clips.GripFingers` and looks the same as before.

## 2. Window: single Save

`Assets/Game/Editor/GripAuthoringWindow.cs`
- `HeldPanel`: replace the three buttons with `Save` (tooltip: "Save changed (●) targets in this view for this item and avatar.").
- `SaveHeld(bool slot = false)`: a single `GripAuthoringAssets.Edit(slot ? item.HoldSlot : item, …)` that loops every target where `GripPoses.Uses(item.HoldMode, t)` and `TryTarget(… changed)`. It writes `stored` for `firstPerson` into `Ensure(item.AvatarGripPoses, avatarId)`, or into `item.HoldSlot.Defaults` when `slot` is set. Then it calls `scene.Reapply()`. Keep the existing "shadowed" dialog, but only on the slot path, for ● targets that resolve from the Item or Avatar layer. A slot write there would be hidden, and the reapply would drop the drag.
- **Advanced** foldout below the Save row (state in `[SerializeField] bool advancedOpen`), collapsed by default, holding one button: **Save as slot default**. Tooltip: "Fallback for every item using {slot} when neither the item nor the avatar defines it. Saves changed (●) targets in this view."
- `ClearHeld`: only clears Avatar-layer entries. Remove the avatar entry when both its table and `Fingers` are empty. Enable Clear only when `layer == GripLayer.Avatar`. Tooltip for Item/Slot rows: "Fallback from the {layer} layer. Edit it in the Inspector." Remove the `Layer()` helper and the `slotLocked` logic.
- Toolbar: `save.text = $"Write to disk ({n})"`, with tooltip "Write saved and copied grip edits to their assets."
- Extract `bool Dirty()` from `ConfirmDiscard` (used by the copy enable states below).

## 3. Window: fingers row

At the top of `HeldPanel`, shown in all phases:
```
Fingers   [ObjectField<AnimationClip>]   Avatar|Item|Slot|Default   [Clear]
```
- The field shows the resolved clip. Changing it calls `GripAuthoringAssets.Edit(item, "Set grip fingers", () => GripPoses.FindEntry-or-Ensure(...).Fingers = clip)`. It applies immediately, with no ● stage. It shows up in the unsaved list and Revert covers it.
- Clear sets the avatar entry's `Fingers = null` (it prunes the entry if it's empty). It's enabled only when the source is Avatar.
- Refresh through the existing `refreshers` list.

## 4. Window: Copy to Hold / Charged

In `HeldPanel`, next to Save, and only in the Hold/Charged phases: **Copy to Charged** (in Hold) or **Copy to Hold** (in Charged).
- Pairs: `HoldPose↔ChargePose`, `RightElbowHold↔RightElbowCharge`, `LeftElbowHold↔LeftElbowCharge`, filtered by `GripPoses.Uses` on both sides.
- It works on the live rig, not the asset. For each pair, `TryTarget(source, out src, …, out layer, out changed)`. Skip automatic hints (`layer == None && !changed`). Then `Undo.RecordObject(dst, …)` and `dst.SetLocalPositionAndRotation(src.localPosition, src.localRotation)`. Both transforms share `slot.Root`, so local copy is exact.
- The result shows as ● on the other phase. Hold↔Charged switches don't re-apply, so the ● survives until Save (which now covers all phases) or Reset.
- Hands (`RightHand`, `LeftHand`) live in item space and are shared by both phases, so they need no copy. `PouchDraw` has no Hold counterpart.

## 5. Copy to other view: fix and extend

**Cause:** `CopyHeld` writes to the **source's** layer for the other view (`Layer(item, layer)`). If the other view resolves that target from a higher layer (for example, you copy a Slot or Item entry while the other view has an Item or Avatar entry), the write is shadowed and nothing visibly changes. Also, only the three item-space hand rows had the button.

**Fix:**
- `CopyHeld(target)` resolves the current view from data: `GripPoses.TryResolve(item.HoldSlot, item, avatarId, target, firstPerson, …)`. It writes to `Ensure(item.AvatarGripPoses, avatarId).Set(target, !firstPerson, pose)`. The Avatar layer always wins, so the write can't be shadowed.
- Add the button to every `HeldRow`. It's disabled when the row is ● ("Save or reset before copying") or unresolved ("Nothing saved to copy").
- Add a panel button **Copy phase to other view** that does the same for all `scene.PhaseTargets()` in one `Edit`. It's disabled while `Dirty()`.
- `GripAuthoringScene.Reapply()`: also call `ReapplyAuthored()` on the non-edited view state (`FirstPerson ? observer.State : held.State`) and on the strip states. The other view then refreshes directly instead of relying only on `ContentChanged`. The edited state keeps its lock, because `ReapplyAuthored` forces.

## 6. Window: Copy to items / avatars

This replaces `CopyPanel`/`CopyToItems`. It is one foldout, shown in all phases:
```
▸ Copy (2 items, 3 avatars)
  Items    [All] [None]   ☐ Rock  ☑ Egg …        (same HoldSlot, excluding this item)
  Avatars  [All] [None]   ☑ Bird  ☐ Frog …       (registry entries, excluding this avatar)
  [Copy to items] [Copy to avatars] [Copy to items and avatars]
```
- State: the existing `copyItems`, plus `[SerializeField] List<AvatarId> copyAvatars`.
- Destinations:
  - Copy to items: `selectedItems × {avatarId}`
  - Copy to avatars: `{item} × selectedAvatars`
  - Copy to items and avatars: `(selectedItems ∪ {item}) × (selectedAvatars ∪ {avatarId})` minus `(item, avatarId)`
- All three buttons are disabled while `Dirty()` (tooltip "Save or reset before copying"), and each is disabled when its selection is empty.
- `CopyEntry(ItemDefinition to, AvatarId toAvatar)`: for each view in {FP, TP} and each target where `GripPoses.Uses(item.HoldMode, t)`:
  - if the source resolves (`TryResolve(item.HoldSlot, item, avatarId, t, view, …)`), call `Set` on the destination avatar table;
  - otherwise call `Remove`, so the destination matches the source exactly (including automatic hints).
  - Fingers: `dest.Fingers = ResolveFingers(item, avatarId, out _)`.
- Confirm once if any destination already has an avatar entry: "Overwrite grips for N item × avatar pairs?"
- Wrap the batch in one undo group (`Undo.GetCurrentGroup` / `CollapseUndoOperations`) so one Ctrl+Z reverts it.

## 7. Rename

`Action("Hold", …)` → `Action("Charge", "Begin a charge and keep holding.", …)`. Text only.

## 8. Finger Pose Generator

**Scene (you create it):** `Assets/Scenes/FingerPoseGenerator.unity`. Use File → New Scene → Basic (camera + light) and save it. The window works in any scene; this one is just a clean stage.

**New: `Assets/Game/Editor/FingerPoseGeneratorWindow.cs`** (`#if UNITY_INCLUDE_INSTRUMENTATION`, menu `Two Birds/Finger Pose Generator`)
- **Open scene** button. It's disabled in Play Mode, with the message "Exit Play Mode to edit finger poses."
- **Avatar** stepper over `AvatarRegistry.Entries`, plus a **Third / First person** toggle. It instantiates `settings.Generated.Source` (or `FirstPersonGenerated.Source`) at the origin with `HideFlags.DontSave`, and ensures an `Animator` whose `avatar` is the matching `HumanoidAvatar`. It creates a `HumanPoseHandler(avatar, root)`. On avatar change or window close, it destroys the preview and disposes the handler.
- **Source clip** field, defaulting to the registry `RelaxedFingers`. **Load** reads the 40 finger muscle values from the clip's t=0 keys through `AnimationUtility.GetEditorCurve` on `EditorCurveBinding.FloatCurve("", typeof(Animator), attr)`.
- Muscle mapping comes from `HumanTrait.MuscleName`. It takes the entries named `Left|Right <Finger> (1|2|3) Stretched` and `Left|Right <Finger> Spread`. Clip attribute: `"Left Index 2 Stretched"` → `"LeftHand.Index.2 Stretched"`, and `"Left Thumb Spread"` → `"LeftHand.Thumb Spread"`.
- **Controls** per hand (Right / Left tabs), per finger (Thumb, Index, Middle, Ring, Little):
  - **Curl** macro slider (−1..1) that sets 1/2/3 Stretched together
  - **Spread** slider
  - foldout with the three joint sliders
  - **Mirror R→L** / **Mirror L→R**, and **Reset to loaded**
  - **Frame hand** (SceneView frames the hand bone)
- On any change: `GetHumanPose`, write the finger muscles, `SetHumanPose`. Slider values are `[SerializeField]` on the window with `Undo.RecordObject(this)`.
- **Save** overwrites the loaded clip in place (after a confirm), which keeps its GUID so item and avatar references stay valid. **Save as…** uses `SaveFilePanelInProject` (default `Assets/Art/Animations/Hands/`) and writes `Object.Instantiate(sourceClip)` with the 40 finger curves replaced (`AnimationCurve.Constant(0, 0, v)`). The other curves are kept, so the clip matches the existing `Grip`/`Open`/`Relaxed` layout.

## 9. What we might be missing

- **Copy bakes fallbacks.** Copied entries freeze today's Item/Slot values. Later edits to a slot or item default won't reach them.
- **Authoring a new HoldSlot's defaults** means Advanced → Save as slot default in both views. Do it on an item with no Item or Avatar entries for that slot, or the shadow dialog blocks the save.
- **Legacy Item-layer data** still wins over Slot for avatars without overrides, and the window can't clear it. The row's source label makes it visible.
- **New avatars** get only fallbacks until you run Copy to avatars. Removing an avatar from the registry leaves orphaned `AvatarGripPoses` entries in items.
- **Cross-avatar copies are a starting point.** Slot-frame poses scale by `VisualHeight`, but arm proportions differ. Check the strip shortfall readouts after copying.
- **Slingshot pouch hand** now reads the left-hand half of the finger clip. Any clip you assign to a slingshot needs a pinch authored in its left hand.
- **Fingers aren't per view.** "Copy to other view" doesn't touch them. The `OpenFingers` overlay still blends on top of the override.
- **Overwriting a clip in place during a grip session:** the generator is edit-mode only, and a running `AnimationClipPlayable` may not pick up new curves until the clip is re-selected or you re-enter Play Mode.
- **Behavior change:** Save now commits ● targets from both phases of the current view, not just the visible phase.

## Files

| File | Change |
|---|---|
| `Assets/Game/Runtime/Items/GripPoses.cs` | `Fingers`, `FindEntry`, `ResolveFingers` |
| `Assets/Game/Runtime/Items/HeldItemPose.cs` | drop `Fingers` |
| `Assets/Game/Runtime/Player/HeldItemPresentationState.cs` | cache/use `selectedFingers`; slingshot pouch hand uses it |
| `Assets/Game/Runtime/Authoring/GripAuthoringScene.cs` | `Reapply` refreshes both views and the strip |
| `Assets/Game/Editor/GripAuthoringWindow.cs` | §2–§7 |
| `Assets/Game/Editor/FingerPoseGeneratorWindow.cs` | new |
| `Assets/Scenes/FingerPoseGenerator.unity` | new, created by you |

## Visual validation (user)

1. Held item, FP, Hold: drag the right hand, click Save. The row shows `Avatar` and no ●. Toolbar shows `Write to disk (1)`.
2. Copy to other view on that row. The TP observer's hand updates immediately. Switch to TP and the row shows `Avatar`.
3. Drag HoldPose and click Copy to Charged. Switch to Charged: ChargePose shows ● at the Hold position. Save, then Actions → Charge: the item doesn't move between the two poses.
4. Set a finger clip. The FP hand and the TP observer change grip; strip avatars without overrides don't. Copy to avatars: the selected strip avatars match.
5. Copy to items and avatars with 2 items × 2 avatars, then Ctrl+Z once: all copies revert.
6. Advanced → Save as slot default on an item with no overrides: the row shows `Slot`. On an item whose row shows `Avatar`, the button shows the shadow dialog instead.
7. Slingshot with no finger clip: the pouch hand looks the same as before. Assign a clip with a left-hand pinch: the pouch hand uses it while drawing and opens on release.
8. Finger Pose Generator: load `Grip`, curl the index, Save as `Point.anim`. Assign it in Grip Authoring, and in Play Mode the index points.
