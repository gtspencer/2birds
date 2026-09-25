# Grip Authoring Gizmos Plan

## Goal
Author how items sit in the hands by dragging Scene view gizmos in Play Mode:

1. **Class hand pose**: per hold class (Regular, Heavy, Slingshot, …), per view (first person / third person), per phase (hold / charged). Drag a palm gizmo, the arm follows by IK, the item follows the hand. On release the pose is baked to the class's pose clip, so every item of that class, on every avatar, uses it.
2. **Item offset**: per item, per view. Drag a gizmo on the item and it moves or rotates in the hand. It applies on every avatar.
3. **Avatar item offset**: per avatar, per item, per view. An extra move or rotation of the item for that avatar only.
4. **Go through the motions**: equip, charge, release/throw, cancel, drop, use, with automatic re-equip. Every avatar in the scene mirrors the action.

## Decisions
| Topic | Decision |
|---|---|
| Arm pose storage | Keep pose clips (`AvatarArmLayers`). The gizmo drives IK while dragging, and mouse-up bakes the arm muscles into the slot's clip. The drag preview goes through the same muscle conversion as clip playback, so the baked pose matches what you see. |
| Tool scope | Editor only. The tool edits the real assets live in Play Mode with Undo; Save writes them, Revert restores a session snapshot. The draft copies, JSON export, in-game panel and main-menu entry are removed. |
| View split | Item offset and avatar item offset each have separate FP and TP values. FP is initialised from TP, and there is a **Copy TP → FP** button. |
| Hold classes | `HoldClass` ScriptableObject assets. `ItemHoldMode` (OneHand / TwoHand / Slingshot) moves from the item to the class and only picks behaviour. |
| Two-hand model | The class clip places both palms. The item hangs off a two-hand grip frame and never pulls the hands. To keep the palms the same distance apart on every avatar, the class stores the palm-to-palm distance measured when the pose was baked, and IK squeezes or spreads each avatar's palms to it. |
| Per-avatar offset storage | `ItemDefinition.AvatarGrips`, so all of an item's grip data lives on the item asset. |
| Palm correction | `AvatarSettings.Left/RightPalmCorrection` is unchanged. It's edited in the AvatarSettings Inspector (live in Play Mode now that the assets aren't copied). The tool doesn't have a layer for it. |
| Composition | `item = G ∘ D ∘ O`: G = grip frame, D = avatar item offset, O = item offset. D is expressed in the hand's frame, so it survives later edits to O. |

## Replaces
- `HeldItemSettings` and the per-mode `HoldModePoses` → `HoldClass` assets.
- `ItemDefinition.HoldMode`, `RightPalmContact`, `LeftPalmContact` → `HoldClass`, `ThirdPersonGrip`, `FirstPersonGrip`, `AvatarGrips`.
- `SlingshotDefinition.PullingPalmContact` → `PouchOffset`: the pouch position in the left-palm frame, in metres.
- The palm-contact handles, which move the frame they're measured in so a drag runs away. Every new gizmo is measured in a frame that doesn't depend on the value being edited.
- `PoseEdit`, whose frozen pose also swallowed the charge/throw visuals, and the separate pose-editor flow in Shared Defaults.
- The draft system, whose runtime copies ignored Inspector edits made during a session, plus the export, the panel, the comparison render textures and the ghost hands.

## Data model

### `HoldClass` (new, `Runtime/Items/HoldClass.cs`)
```csharp
[CreateAssetMenu(menuName = "Two Birds/Hold Class")]
public sealed class HoldClass : ScriptableObject
{
    public event Action ContentChanged;
    public void NotifyContentChanged() => ContentChanged?.Invoke();
    private void OnValidate() => NotifyContentChanged();
    public ItemHoldMode Mode;
    public HoldClassView ThirdPerson, FirstPerson;
    [Min(0f), Tooltip("Visual charge duration in seconds. Slingshot follows ThrowChargeTime instead.")] public float ChargePoseDuration = 0.35f;
    [Min(0f)] public float MaximumFollowDuration = 0.2f, EndPosePauseDuration, ReturnBlendDuration = 0.2f;
    [Range(0.01f, 0.85f)] public float FollowReachFraction = 0.85f;
    internal HoldClassView View(bool firstPerson) => firstPerson ? FirstPerson : ThirdPerson;
}

[Serializable]
public struct HoldClassView
{
    public AnimationClip Hold, Charged;
    [Tooltip("TwoHand only. Palm-to-palm metres, written when the pose is baked.")] public float HoldSpread, ChargedSpread;
}
```

### `GripOffset` (replaces `ItemPalmContact`, `Runtime/Items/GripOffset.cs`)
```csharp
[Serializable]
public struct GripOffset
{
    public Vector3 Position, Euler;
    public Pose Pose => new(Position, Quaternion.Euler(Euler));
}

[Serializable]
public struct AvatarGripOffset { public AvatarId Avatar; public GripOffset ThirdPerson, FirstPerson; }
```

### `ItemDefinition` (Held Item Grip section)
```csharp
public HoldClass HoldClass;
[Tooltip("Item root in the grip frame: metres and Euler degrees.")]
public GripOffset ThirdPersonGrip, FirstPersonGrip;
public List<AvatarGripOffset> AvatarGrips = new();
public AnimationClip GripFingers;
internal ItemHoldMode HoldMode => HoldClass ? HoldClass.Mode : ItemHoldMode.OneHand;
internal Pose Grip(AvatarId avatar, bool firstPerson) // returns D ∘ O
```
- `SlingshotDefinition`: remove the forced `HoldMode`. Replace `PullingPalmContact` with `public Vector3 PouchOffset;`.
- `ItemRegistry`: replace `HeldItemSettings HeldItemDefaults` with `HoldClass CarryHold`, which supplies the player-carry release timing (assign Heavy).
- Delete `HeldItemSettings.cs`, `ItemPalmContact.cs` and `Assets/Game/Settings/HeldItemSettings.asset`.

## Runtime composition

### Frames
- **Grip frame G**
  - OneHand / Slingshot: `binding.Palm(true)` after IK.
  - TwoHand, from the clip-posed palms before the two-hand IK:
    - Position: the midpoint of the two palms.
    - X: `normalize(R − L)`.
    - Z: the mean of both palms' +Z, orthogonalised against X.
    - Rotation: `LookRotation(Z, Cross(Z, X))`.
    - If Z degenerates, fall back to the body's forward.
- **Item pose:** `G ∘ C`, where `C = item.Grip(avatarId, firstPerson)` (D ∘ O). The state caches C and recomputes it on bind, on selection change, and on item or class content change.
- **Two-hand IK:**
  - Target palms: `center ± X · spread / 2`, each keeping its clip rotation, where `spread = Lerp(HoldSpread, ChargedSpread, charge)`.
  - Skip this when the spread is 0 (not baked yet).
  - It changes nothing on the authoring avatar and squeezes or spreads the palms on the others.
- **Slingshot pouch:** `leftPalm.position + leftPalm.rotation · PouchOffset`.
- **Fallback (no binding):**
  - OneHand: `(body.ToWorld(FallbackPalm), body.Rotation) ∘ C`.
  - TwoHand: `(body.CenterToWorld(FallbackCenter), body.Rotation) ∘ C`.

### `HeldItemPose.cs`
- `HeldItemPoseData`: carries `Class`, `HoldMode`, `Fingers`, `PouchOffset`, `Sphere`, `ReleaseRadius`, `PrefabRotation`. Drop the contacts and `PrefabScale`.
- `HeldItemPoseCalculation`: `GripFrame(right, left)`, `Compose(frame, offset)`, `Fallback(body, data, grip)`. Delete `PalmFromItem`, `ItemFromPalm`, `TwoHandAnchor`.

### `AvatarArmLayers`
- Classes replace the three fixed modes. Each arm's mixer gets four inputs: two class slots × (hold, charged). The slots ping-pong so a class switch blends without rebinding the class that's fading out.
- `AvatarArmPose`: `{ HoldClass A, B; float WeightA, WeightB; HoldClass ChargeClass; float Charge, Pitch; }`.
- A slot feeds the left arm only when its class `Mode != OneHand`.
- The layer and charge weight logic is the same as now: a slot contributes only if it has a Hold clip for this view, and charge splits a slot only if that slot is the `ChargeClass` and it has a Charged clip.
- Subscribe to `ContentChanged` on the bound classes. Rebind recreates a slot's `AnimationClipPlayable` even when the clip reference is unchanged, because a bake rewrites the curves in place.

### `AvatarArmIK`
- `AvatarHandTargets.TwoHandAnchor` becomes `{ Active, Spread, Grip (C) }`.
- `Solve` computes G from the palms after the clip and pitch, sets `binding.AnchoredItem = G ∘ C`, then IKs both palms to the spread targets.
- Pitch uses `ChargeClass.Mode` in place of `ChargeMode`.

### `HeldItemPresentationState`
- Replace `weights[3]`/`startWeights[3]` (per mode) with two class slots, blended the way the current per-mode weights are:
  - A newly selected class takes the lower-weight slot.
  - Recovery starts with the action class at 1.
- `HeavyFrameWeight`: the sum of the weights of TwoHand-mode slots.
- `ArmWeight`: the total weight.
- Timing (`ChargePoseDuration`, follow, return) comes from the class.
- Move the `ContentChanged` subscriptions for the selected/action definition and its class into the state. `PlayerHeldItemPresentation` and the authoring observers both need them (a shared boundary). Drop the `HeldItemSettings` constructor parameter.
- `SubmitTargets`:
  - TwoHand: `SetAnchor(spread, C)`.
  - Otherwise: Item source at weight 0 (fingers only), as today.
- `CommitHands`: the item is `binding.Palm(true) ∘ C` (OneHand/Slingshot) or `binding.AnchoredItem` (TwoHand). The `fallback` transform is always set to the item pose.
- `Attachment`: computes the same item pose from the current binding or fallback body for every mode, not just TwoHand.
- Release follow: at `StartFollow`, capture `palmInItem = inverse(committed.ItemPose) ∘ committed palm` for each hand. `SubmitFollow` then samples `input.Projectile ∘ palmInItem`.
- Slingshot: pass `PouchOffset` to `SlingshotPresentation.Evaluate`, which drops its `contact`/`scale` parameters.

### Other consumers
- `WorldItem.ApplyHeldAttachment`: parent to `Attachment` with an identity local pose, and keep the scale compensation. Delete `HeldItemPresentationState.Grip` / `PlayerHeldItemPresentation.Grip`.
- `PlayerHandPresentation.CarrySettings` → `items.CarryHold`. `HandHoldPresentation` takes `HoldClass` in place of `in HoldModePoses`.
- `PlayerHeldItemPresentation`: drop the `HeldDefaults` subscription and constructor argument.
- `WorldItemRegistry.HeldDefaults` → `CarryHold`.

### Authoring hooks (`#if UNITY_INCLUDE_INSTRUMENTATION`)
- `HeldItemPresentationState.PoseEdit` → `AuthoringPose`:
  ```csharp
  internal sealed class AuthoringPose
  {
      internal bool Charged;
      internal Pose? Right, Left;          // body-local IK override while a palm is dragged
      internal float RightSwivel, LeftSwivel;
  }
  internal AuthoringPose Authoring;       // null = live gameplay
  ```
  - When `Authoring != null`:
    - `RefreshArms`: the selected class's slot is at weight 1, charge = `Charged ? 1 : 0`, pitch 0.
    - `CommitHands`: skip clearance.
    - Actions don't change the arms.
  - A non-null `Right`/`Left`:
    - Replaces that palm's target (body-relative, weight 1).
    - Disables the two-hand anchor.
    - Sets `AvatarHandTargets.RoundTripMuscles`.
- `AvatarBinding` gets `RoundTripMuscles()` and `CaptureMuscles()`, backed by a lazily created `HumanPoseHandler`. The handler is disposed with the binding (`AvatarInstance.Dispose`, `LocalFirstPersonHands.OnDestroy`).
- `AvatarInstance.Evaluate` and `LocalFirstPersonHands.Evaluate`: after `arms.Solve`, call `binding.RoundTripMuscles()` when the flag is set. That's `GetHumanPose` followed by `SetHumanPose`, so the preview is exactly what the baked clip will play.
- `AvatarHandTargets.Swivel` stays instrumentation-only.

## Authoring tool

### Session (`Runtime/Authoring/GripAuthoringScene.cs`, rewritten)
- Start-up is unchanged: the scene starts `SessionController.StartGripAuthoring`, then attaches to the local player.
- Keeps item supply, equip and cleanup. Adds **Auto re-equip** (default on): when the action is Idle and the selected item is no longer in the inventory, supply and equip it again.
- Observers:
  - The main `GripObserverPreview` follows the selected avatar and sits 1.2 m to the player's right, so it doesn't overlap the FP rig.
  - The strip of other avatars continues from 1.2 m in 0.9 m steps.
  - Observers use the real `AvatarRegistry`.
- Layers:
  - Only the owner's third-person graphics get the `GripAuthoringOwner` layer. The FP rig keeps its own.
  - The FP camera culls `AvatarPreview` for the session and is restored on destroy.
- API for the editor window:
  - `SetPhase(Live | Hold | Charged)`: sets or clears `Authoring` on every state (owner FP, main observer, strip).
  - `TryRig(firstPerson, out state, out binding)`, `TryPalm(firstPerson, right, out Pose)`, `TryGrip(firstPerson, out Pose)`, `TryItem(firstPerson, out Pose)`, `TryPouch(firstPerson, out Vector3)`.
  - `BeginPalmDrag(firstPerson, right)` seeds the override from the current palm. `DragPalm(world)`, `SetSwivel(right, degrees)`, and `EndPalmDrag(out GripPoseCapture)`, which captures muscles and the palm spread, then clears the override.
  - `ContentEdited()` calls `RefreshContent()` on every state.
  - Actions: `Equip`, `Dequip`, `Hold`, `Release`, `Throw`, `Cancel`, `Drop`, `Use`, `SetWalking`.
    - Each action sets the phase to Live.
    - `Throw` holds for `max(ThrowChargeTime, ChargePoseDuration)`, then releases (coroutine, unscaled time).
  - `Readout(firstPerson)` / `StripReadouts()`: the existing status, clearance and two-hand shortfall text, plus the palm error while dragging.
- Keeps F2 to toggle walking/editing.
- `GripPoseCapture`: `{ HoldClass Class; bool FirstPerson, Charged; float[] Muscles; float Spread; }`.

### Editor window (`Editor/GripAuthoringWindow.cs`, rewritten, UIElements built in code)
```
[Start Authoring] [Frame] [Look through FP camera] [Save (n)] [Revert]
Item ▾   Avatar ▾
View   ○ Third person  ○ First person
Phase  ○ Live  ○ Hold  ○ Charged
Edit   ○ Hand pose  ○ Item offset  ○ Avatar offset  ○ None
── layer panel ──
Actions: Equip · Dequip · Hold · Release · Throw · Cancel · Drop · Use · [x] Auto re-equip · [ ] Walk (F2)
Readouts: first person / third person / strip
Unsaved: …
```

Layer panels:
- **Hand pose:**
  - The class object field (ping), mode, and the clip slot name or "not authored".
  - Right and left elbow swivel sliders. Changing one applies an override; releasing the slider bakes and resets the swivel.
  - **Copy Hold → Charged** and **Clear pose**.
  - The class timing, via an embedded `InspectorElement`.
  - Selecting Hand pose in Live switches the phase to Hold.
- **Item offset:**
  - Position/Euler fields for O in the current view, and **Copy TP → FP**.
  - Slingshot only: the `PouchOffset` field.
- **Avatar offset:**
  - Position/Euler fields for D (selected avatar, current view), and **Clear** (removes the entry once both views are zero).
  - A list of the avatars that have entries for this item.

Scene view (`duringSceneGui`, Play Mode, scene attached):
- Gizmos follow `Tools.current` (W move, E rotate, Y both) and `Tools.pivotRotation`. `Tools.hidden = true` while Edit ≠ None and is restored on disable.
- **Hand pose** (phase Hold or Charged only): a labelled gizmo on the right palm, plus the left palm for TwoHand/Slingshot.
  - Mouse-down: `BeginPalmDrag`. Drag: `DragPalm`.
  - Mouse-up: `EndPalmDrag`, then `GripAuthoringAssets.Bake`.
  - While dragging, draw a yellow sphere at the requested palm and a cyan sphere at the reached palm, joined by a line.
- **Item offset:** a gizmo at the item root.
  - On change: `O = inverse(D) ∘ inverse(G) ∘ itemWorld`, where G is read that frame.
  - Slingshot: an extra position gizmo on the pouch, `PouchOffset = inverse(leftPalm) · pouchWorld`.
- **Avatar offset:** a gizmo at the item root. On change: `D = inverse(G) ∘ itemWorld ∘ inverse(O)`.
- Offset edits call `Undo.RecordObject(item)` once per drag, then `GripAuthoringAssets.Touch(item)`, `item.NotifyContentChanged()` and `scene.ContentEdited()`.
- Tools.visibleLayers:
  - While attached, exclude `GripAuthoringOwner` so the owner's third-person body doesn't hide the FP rig.
  - Restore on play mode exit or window disable.
- **Frame** frames the active gizmo.
- **Look through FP camera** (FP view only): each update, align the last active Scene view to the FP camera's pose and FOV.

### Assets (`Editor/GripAuthoringAssets.cs`, new, static)
- `Bake(GripPoseCapture)`:
  - Folder: `Assets/Art/Animations/HeldPoses/`. Create it if missing.
  - Target: the slot's existing clip if it's in that folder. Otherwise create `<Class>_<TP|FP>_<Hold|Charged>.anim` there and assign it.
  - Before writing: `Undo.RegisterCompleteObjectUndo` on the clip and the class.
  - Write the arm muscle curves for both arms. The muscle filter is the current `SavePose` filter: shoulders, upper arms, lower arms and hands.
  - TwoHand: store the spread in `HoldSpread` / `ChargedSpread`.
  - Touch both assets, then `NotifyContentChanged`.
- `CopyHoldToCharged(HoldClass, firstPerson)` and `Clear(HoldClass, firstPerson, charged)`.
- `Touch(Object)`: on first touch, snapshot the asset with `Instantiate` (HideAndDontSave) and remember assets created during the session.
- `SaveAll()`: `AssetDatabase.SaveAssetIfDirty` on each touched asset, then clear the snapshots.
- `RevertAll()`: `EditorUtility.CopySerialized(snapshot, asset)`, delete clips created this session, and notify.
- `Unsaved`: the touched assets.
- Window hooks:
  - `hasUnsavedChanges` / `SaveChanges` / `DiscardChanges`.
  - On `ExitingPlayMode` with unsaved changes, a dialog: Save / Revert.
  - `Undo.undoRedoPerformed` notifies every touched class and item and calls `ContentEdited()`.

### Other editor changes
- `GripAuthoringPersistence`: keep only the build-settings include/restore for Play Mode, triggered by `ExitingEditMode` in the authoring scene. Remove `Save`, `SavePose` and the session hooks.
- `GripAuthoringBuildPolicy.Apply`: always strip the authoring scene. `GripAuthoringBuildGuard`: require zero copies.
- `ItemSetup`: compute the same resting palm-in-item pose it computes today, then write its inverse to `ThirdPersonGrip` and `FirstPersonGrip`. Gate the button on `HoldMode != TwoHand`.
- `ItemDefinitionEditor`: remove the TwoHand `LeftPalmContact` handling and its help box. Keep the `CollisionDamage` logic.

### Deletions
- `Runtime/Authoring`: `GripAuthoringDrafts.cs`, `GripAuthoringExport.cs`, `GripAuthoringPanel.cs`, `GripAuthoringHandle.cs`, `GripComparisonViews.cs`, `GripGhostHand.cs`.
- `GripAuthoringSession.cs`: keep only `SceneName`/`ScenePath`, or fold them into `GripAuthoringScene`.
- `UI/GripAuthoring/GripAuthoring.uxml`, `GripAuthoring.uss`.
- `MenuPresenter`: the Grip Authoring button.
- `SessionController`: the `GripAuthoringSession.Begin`/`End` calls, `PresentationRegistryOverride` and `PresentationRegistry`. `PlayerAvatarPresentation` uses `Avatars` directly.
- `AvatarPresentationDemo.unity`: the observer camera and UIDocument objects, and the `observerCamera`, `document` and `ghostMaterial` fields on `GripAuthoringScene`. `Assets/Game/Materials/Ghost.mat` becomes unused.

## Migration (apply directly, no migration tool)
1. Create `Assets/Game/Settings/HoldClasses/`:

   | Asset | Mode | ChargePose | MaxFollow | EndPause | Return | Reach |
   |---|---|---|---|---|---|---|
   | `Regular` | OneHand | 0.35 | 0.20 | 0 | 0.20 | 0.85 |
   | `Heavy` | TwoHand | 0.35 | 0.20 | 0 | 0.20 | 0.85 |
   | `Slingshot` | Slingshot | 0.15 | 0 | 0 | 0.20 | 0.85 |

   No clips yet: every `HoldItemSettings` slot is currently empty.
2. `ItemRegistry.CarryHold` = `Heavy`.
3. Assign the class on each item and set `ThirdPersonGrip` = `FirstPersonGrip` to the inverse of the current contact (prefab scale applied). The first five rows are exact. Boulder is approximate: re-check it after authoring the Heavy pose.

   | Item | Class | Position | Euler |
   |---|---|---|---|
   | Basketball | Regular | (0, 0.19425, 0) | (270.02, 0, 0) |
   | Rock | Regular | (0, 0.00603, −0.00003) | (270.02, 0, 0) |
   | Small/Medium/Large Health, Bouncy potions | Regular | (0, 0.006, −0.00084) | (0, 0, 0) |
   | Mushroom_Amanita | Regular | (−0.00032, 0.08583, 0.00271) | (0, 0, 0) |
   | Slingshot | Slingshot | (0.025, 0.02, 0) | (0, 0, 270) |
   | Boulder | Heavy | (−0.0378, −0.2565, 0) | (270, 0, 0) |

4. Slingshot `PouchOffset` = (0, 0, 0). The current contact puts the pouch on the palm.
5. The old contact fields left in the YAML re-serialise away the next time the assets are saved.

## Implementation order
1. Data model, class assets and item migration. At this stage the game compiles and holds items with base-animation arms plus the offsets.
2. Runtime composition: `AvatarArmLayers`, `HeldItemPose`, `AvatarArmIK`, `HeldItemPresentationState`, `WorldItem`, `PlayerHandPresentation`, `PlayerHeldItemPresentation`, `SlingshotPresentation`.
3. Authoring hooks: `AuthoringPose`, muscle round trip, `GripAuthoringScene`, `GripObserverPreview`, `SessionController`/`MenuPresenter` cleanup, deletions.
4. Editor: `GripAuthoringWindow`, `GripAuthoringAssets`, persistence and build policy, `ItemSetup`, `ItemDefinitionEditor`.
5. `AvatarPresentationDemo.unity` edits (listed under Deletions).
6. Rewrite `Docs/Grip_Authoring_Guide.md` for the new workflow.

## Validation (visual, in the editor)
1. **Start Authoring** opens the demo scene and plays. Scene view: FP rig at the camera, observer 1.2 m to the right, strip beyond it, and no owner body over the FP rig.
2. Regular item, TP, Hold, Hand pose:
   - Drag the right palm: the observer's arm and the item follow; the yellow and cyan spheres stay together unless you pass a joint limit.
   - Release: no jump. The clip appears under `HeldPoses/`, and the strip avatars take the pose.
3. Repeat step 2 for Charged, then FP Hold/Charged (use **Look through FP camera**).
4. Phase Live, **Throw**: the arm winds up to Charged, the hand follows the throw, returns, and the item re-equips. Check FP and every strip avatar.
5. Item offset: move/rotate the item. It slides in the hand without the hand moving, on every avatar. **Copy TP → FP** matches FP.
6. Avatar offset: nudge the item on the selected avatar only; the other strip avatars don't change. Switch avatar: its own value, or zero.
7. Heavy (Boulder):
   - Author Hold with both palm gizmos.
   - Other avatars' palms sit at the same spread, and the strip shortfall readout is about 0 cm.
   - Item offset moves the boulder between fixed hands.
   - FP heavy: the rig stays body-anchored while you look up and down.
8. Slingshot: author Hold/Charged for both palms. The pouch follows the left palm through **Hold** / **Release**, and the pouch gizmo moves it.
9. Ctrl+Z undoes drags and bakes. **Revert** restores everything, including deleting new clips. **Save** writes them; restart Play Mode and the poses persist.
10. Editing an `ItemDefinition` or `HoldClass` in the Inspector during Play Mode updates the scene live.
11. In the normal Game scene (solo), held items, charge, throw, drop and remote players look the same as in authoring.

## Out of scope
- Per-avatar class hand pose overrides. Per-avatar fixes are item offsets or palm correction.
- Clavicle/shoulder IK beyond what the baked clip carries.
- Authoring in player builds.
- Player-carry poses.
