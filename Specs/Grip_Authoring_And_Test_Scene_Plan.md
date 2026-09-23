# Grip Authoring and Test Scene Plan

Implement [Grip_Authoring_And_Test_Scene_Spec.md](Grip_Authoring_And_Test_Scene_Spec.md). The result is one production grip representation plus an Editor/development-build authoring workflow in `Assets/Scenes/AvatarPresentationDemo.unity`.

## Workflow and scope

Live gameplay comparison uses Play mode in the Editor and a local Solo session in development builds. The Editor window can edit, save, and revert drafts outside Play mode; its live previews and spatial handles require the running authoring scene. This is the planning assumption for Edit-mode behavior.

Keep ordinary holds, heavy holds, slingshot actions, inventory transitions, movement, clearance, and reach on their production paths. The observer pane has an independent presentation instance driven by the same gameplay state. It has no network connection or gameplay simulation of its own.

Required scene changes are confined to repurposing the demo and adding its gameplay bootstrap, spawn/world setup, observer presentation, cameras, and UI. Shared integration includes session routing, presentation code, asset resolution, and build filtering. The normal `Game.unity` remains the gameplay environment.

Excluded features: item-by-avatar grip variants, new grip-profile assets, finger fitting/editing, arm stretching, a new reach solver, action recording, clock controls, paused-pose editing, runtime clip assignment/catalogs, export importing/loading, and cross-process editing.

## Code and asset integration map

All paths in this table are relative to `Assets/Game/` unless they start with `Assets/` or `ProjectSettings/`.

| Area | Existing integration points | Planned responsibility |
| --- | --- | --- |
| Grip data and inheritance | `Runtime/Items/ItemDefinition.cs`, `HeldItemSettings.cs`, `HeldItemPose.cs`, `SlingshotDefinition.cs`; `Editor/ItemDefinitionEditor.cs` | Item-relative contacts, shared resolution, grouped overrides, contact conversion, effective-value display. |
| Held presentation and release | `Runtime/Player/PlayerHeldItemPresentation.cs`, `PlayerHandPresentation.cs`, `PlayerEquipment.cs`; `Runtime/Items/WorldItem.cs`, `WorldItemRegistry.cs`, `ItemReleaseClearance.cs` | Consume the unified data, preserve evaluated-hand attachment, expose shared observer presentation and reach results. |
| Slingshot | `Runtime/Items/SlingshotPresentation.cs`, `SlingshotItemUse.cs`, `SlingshotAim.cs`; `Runtime/Player/PlayerEquipment.cs` | Resolve the moving pulling contact from item data while preserving pouch/pebble release behavior. |
| Avatar palms and fingers | `Runtime/Avatars/AvatarSettings.cs`, `AvatarPresentation.cs` (`AvatarBinding`), `AvatarHandIK.cs`, `AvatarHandTargets.cs`, `LocalFirstPersonHands.cs`, `AvatarFingerLayers.cs`, `AvatarAnimationGraph.cs` | Calibrated palm measurements and consistent target/evaluated-palm reporting in both rigs. |
| Avatar regeneration | `Editor/AvatarProcessor.cs`, `AvatarSettingsEditor.cs` | Retain authored calibration separately from generated skeleton data. |
| Session and input | `Runtime/Networking/SessionBootstrap.cs`, `SessionController.cs`, `GamePlayerSpawner.cs`; `Runtime/Pickup/PickupRegistry.cs`; `Runtime/Player/PlayerInputReader.cs`, `PlayerAvatarPresentation.cs`; `Runtime/Inventory/PlayerInventory.cs` | Local Solo routing, temporary content binding, gameplay-driven authoring commands. |
| Preview/UI patterns | `Runtime/Avatars/Appearance/AvatarEditorPreview.cs`, `Runtime/UI/AvatarEditorPanel.cs`, `Runtime/UI/InputPresentation.cs`, `UI/Shared.uss` | Reuse orbit, RenderTexture, layout, and input-focus patterns. The cosmetic preview's fixed pose is not the observer evaluator. |
| Scene and prefabs | `Assets/Scenes/AvatarPresentationDemo.unity`, `Prefabs/SessionRoot.prefab`, `Prefabs/Player.prefab`, `Runtime/Avatars/AvatarPresentationDemo.cs` | Replace automatic cycling and the hand-target showcase with controlled gameplay authoring. |
| Build/menu | `Editor/TwoBirdsBuildPipeline.cs`, `Runtime/UI/MenuPresenter.cs`, `UI/Session.uxml`, `ProjectSettings/EditorBuildSettings.asset` | Development scene inclusion and menu access; release exclusion. |

Authoritative content lists:

- `Assets/Game/ScriptableObjects/ItemRegistry.asset` and its `Items` array.
- `Assets/Game/Settings/Avatars/AvatarRegistry.asset` and its `Entries` list.
- Shared values referenced by those registries: `Assets/Game/Settings/HeldItemSettings.asset`, `Assets/Game/Settings/FirstPersonHandsSettings.asset`, and `Assets/Game/Settings/Avatars/AvatarAnimationSet.asset`.

The registered avatars are `44816e438f2e4775` / **138 Chill** and `461d9592f370666a` / **175 Genesis Gerbil**. Their settings are the correspondingly named assets under `Assets/Game/Settings/Avatars/`.

## 1. Unify contacts and migrate authored assets

### Contact contract

Add a serializable `ItemPalmContact` value with `Position` and `Euler`, using item-root local axes. Positions are metres before the item prefab root scale; rotations are degrees. Store `RightPalmContact` and `LeftPalmContact` on `ItemDefinition`. `HoldMode.Hand` uses the right contact; `HoldMode.Heavy` uses both. Do not add another hand-count switch.

Keep contact fields independent of `HandPose`, `FirstPersonPose`, and `GripFingers`. Hold positions retain their current shoulder/body/camera-relative meaning. The shared conversion functions belong with `HeldItemPoseCalculation` in `HeldItemPose.cs` and serve gameplay, previews, handles, and diagnostics.

For an item root pose `(x, R)`, prefab scale `S`, and contact `(p, Q)`:

```text
palm.rotation = R * Q
palm.position = x + R * Scale(S, p)

R = palm.rotation * Inverse(Q)
x = palm.position - R * Scale(S, p)
```

Use the prefab's authored scale exactly once. Attaching an item under an avatar must not introduce avatar scaling into the item contact. Use quaternion composition for conversion; Euler fields are the authoring representation.

### Direct asset conversion

Convert these registered assets in `Assets/Game/ScriptableObjects/Items/`:

| ID | Asset | Contact source |
| --- | --- | --- |
| 1 | `Rock.asset` | Existing `GripPosition` / `GripEuler`. |
| 2 | `Mushroom_Amanita.asset` | Existing ordinary grip. |
| 3 | `Basketball.asset` | Existing ordinary grip. |
| 4 | `SmallHealthPotion.asset` | Existing ordinary grip. |
| 5 | `MediumHealthPotion.asset` | Existing ordinary grip. |
| 6 | `LargeHealthPotion.asset` | Existing ordinary grip. |
| 7 | `BouncyPotion.asset` | Existing ordinary grip. |
| 8 | `Slingshot.asset` | Existing ordinary grip plus pulling contact on its prefab. |
| 9 | `Boulder.asset` | `LeftHandGrip` / `RightHandGrip` on `Prefabs/Items/Boulder.prefab`. |

For an old ordinary grip `(g, G)` with `G = Quaternion.Euler(GripEuler)`, write `Q = Inverse(G)` and `p = Divide(-Q * g, S)`. The old position is a palm-relative offset already expressed in metres, so copying or merely negating it is incorrect. Convert the heavy transforms into item-root coordinates using their complete transform hierarchy, retaining their root-relative rotations. Preserve the heavy hold solver's use of prefab rotation as part of placement, separate from contact rotation.

Migrate `SlingshotPresentation.PullingPalmEuler` and `PullingPalmOffset` into `SlingshotDefinition.PullingPalmContact`. This contact uses a frame translated to the moving pouch center with axes aligned to the item root. Its current offset is expressed in palm axes without item scaling; convert it to pouch/item axes with `p = Divide(Q * oldOffset, S)` and preserve `Q = Quaternion.Euler(oldEuler)`.

Remove the superseded serialized item grip fields, heavy grip-transform lookup/cache, the Boulder grip markers, and the slingshot prefab's pulling-contact fields. Preserve the identically named avatar carry-grip transforms: they belong to player carrying. Forks, `RestCenter`, `DrawCenter`, bands, and loaded-pebble geometry remain on the slingshot prefab.

### Runtime consumers

- Replace `HeldItemPoseData.GripPosition`, `GripRotation`, and prefab-derived `HeavyItemGrips` with resolved contacts. A derived inverse attachment pose is a cache, not another serialized authority.
- Update both `HeldItemPose` constructors, `HeldItemPoseCalculation.FromItem`, ordinary hold/charge/recovery calculations, and `HeavyItemPoseCalculation.FromItem`/`Reach` to use the shared conversions.
- Update `WorldItem.ApplyHeldAttachment` and `PlayerHeldItemPresentation.CommitHands` together. Ordinary items derive their final root from the evaluated right palm, including when reach limits move that palm.
- Keep release geometry caching in `WorldItem.CacheReleaseGeometry`; remove contact acquisition from it. Editing a contact must not depend on rebuilding collider geometry.
- Update `SlingshotCharge`, `SlingshotPresentation.Evaluate`, and `CommitPalm` to use the new moving contact. After evaluating the left hand, derive the pouch center by reversing that same contact transform. `Center`, bands, `LoadedPebble`, `DepartureCenter`, `TryPreparePebble`, and `TryPebbleDeparture` must agree.

## 2. Define grouped resolution and calibration

### Effective groups

Centralize resolution in the existing held-pose data path. The Inspector, authoring fields, and runtime evaluator must call the same group-resolution methods.

| Group | Default | Override behavior |
| --- | --- | --- |
| Ordinary third-person hold/action timing | `HeldItemSettings.HoldSettings` | Existing `OverrideHoldSettings` selects `ItemDefinition.HandPose`. |
| Heavy hold/action timing | `HeldItemSettings.HeavyHoldSettings` | Same switch selects `HandPose`; position is the collider reference center relative to the shoulder midpoint, in average arm lengths. |
| Ordinary first-person spatial pose | `HeldItemSettings.FirstPersonPose` | Existing `OverrideFirstPersonPose` selects `ItemDefinition.FirstPersonPose`. Timing remains in the resolved hold group. |
| Heavy first-person spatial pose | Spatial fields from the resolved heavy hold group | Preserve this existing fallback; `OverrideFirstPersonPose` enables a separate spatial group. |
| Slingshot charge placement | Add a shared `SlingshotChargePose` group to `HeldItemSettings` | Add grouped switches on `SlingshotDefinition` for the existing `RemoteChargePose` and `FirstPersonChargePose`. Both inherit the shared group until independently overridden. |
| Finger clips | Existing `AvatarAnimationSet` fallbacks | An assigned `ItemDefinition.GripFingers` replaces its existing fallback; a missing item clip remains inherited. |

Seed the new shared slingshot charge group from the existing common settings. During asset conversion, preserve a current first-person or remote group by enabling its override whenever its values differ from that shared group.

Every false-to-true override transition copies the currently resolved entire group before setting the switch, including when another draft supplies its defaults. This applies to the authoring UI and `ItemDefinitionEditor`, including multi-object edits resolved per item. Turning a switch off returns to inheritance. Turning it on again captures the then-current effective defaults. Editing defaults cannot overwrite enabled item groups.

Display the actual effective inherited values, source asset/group, and override state. Inherited numeric controls are read-only until their group is enabled; they do not silently enable overrides. Camera comparison controls never write any of these groups.

### Palm calibration

Add authored `LeftPalmCorrection` and `RightPalmCorrection` position/Euler values to `AvatarSettings`, outside `Generated` and `FirstPersonGenerated`. Zero values preserve each rig's generated baseline.

Define each correction in its generated palm's local axes, in metres before avatar visual scaling. For generated wrist-to-palm `(p0, Q0)` and correction `(dp, dQ)`:

```text
effectivePosition = p0 + Q0 * dp
effectiveRotation = Q0 * dQ
worldOffset = effectivePosition * avatarScale
```

Add a shared `AvatarPalmCalibration` helper under `Runtime/Avatars/` that returns effective measurements without changing the generated struct stored on the asset. It only replaces the wrist-to-palm fields in the returned measurement snapshot. Each rig starts from its own generated skeleton and applies the same authored correction.

Use those effective measurements in:

- `AvatarBinding.Measurements` and `AvatarBinding.Palm` in `AvatarPresentation.cs`.
- `AvatarHandIK` construction and calibration refresh.
- Every `HeldItemBodyFrame` construction path, including full-body, local hands, and `WithMeasurements`/`WithReference` callers.
- `HeldItemPose` constructors, heavy reach calculation, and slingshot pulling-hand reach calculations that currently read raw wrist-to-palm data.

Retain generated arm lengths. Extend `AvatarContentValidation` for finite authored correction values. `AvatarProcessor.Process` already clones existing settings before replacing measurements; preserve that separation and expose the new authored fields in `AvatarSettingsEditor` without regenerating them.

### Reach indication

Surface the existing solver outcomes from `HeldItemPoseCalculation.Resolve`, `HeavyItemPoseCalculation.Reach`, and the clearance/reach projection path. Report the requested contact, evaluated palm, and positional/angular error per hand and view after evaluation. Distinguish ordinary reach-limited placement, an active blend, clearance adjustment, and impossible two-hand reach.

For impossible heavy poses, keep arm clamping and show an explicit unreachable-contact indication with the affected hand(s). Do not turn a normal equip/action blend into an unreachable warning. These are observations of production evaluation, not another solver.

## 3. Make production presentation reusable and refreshable

### Shared presentation state

Extract the held-item visual state machine from `PlayerHeldItemPresentation` into `Runtime/Player/HeldItemPresentationState.cs`. Move existing hold/charge/recovery, two-hand transition, target preparation, evaluated-palm attachment, and slingshot presentation logic into it. Keep `PlayerHeldItemPresentation` as the gameplay adapter for its existing callers and release APIs.

Give each evaluator explicit presentation inputs: selected/action definitions and world IDs, `ItemActionSnapshot`, action age, body/aim/camera frames, equipment permissions, presentation mode, and the existing projectile-follow sample. Supply visual/target bindings separately. Do not make observer mode depend on `PlayerInventory.IsOwner` inside the shared evaluator.

The production adapter remains responsible for inventory/network subscriptions, release prediction/submission, world-item commits, and `CompleteRecovery`. The observer adapter supplies the observer mode and commits to its own visual item. Advancing a preview must never complete an action, publish a release, consume inventory, change ownership, or move the real world item.

Refactor the placement-input construction around `PlayerAvatarPresentation.CaptureInput` / `CurrentPlacement` enough to share observer interpretation of movement and look state. The preview receives the local action/movement state without network delivery delay, then uses the same observer evaluator and full-body `AvatarPresentation` as a remote player. Preserve actual multiplayer smoothing/timing in its existing adapter.

Preserve evaluation order through `AvatarPresentation` prepare/evaluate/correct/commit and `PlayerHandPresentation`'s local-hands path. Each presentation instance has independent state, hand targets, rig binding, and slingshot visual state. A shared evaluator must not share mutable pose state between the two views.

### Refresh contract

Expose an explicit content-change method on `ItemDefinition`, `HeldItemSettings`, `AvatarSettings`, and relevant existing shared settings. `OnValidate` and authoring mutations call the same method; runtime editing has no dependency on Editor callbacks. Add equivalent notification support to `FirstPersonHandsSettings`.

Handle an edit as one coherent refresh before the next rendered frame:

1. Apply the selected context's authored values to its runtime draft object.
2. Resolve effective groups and refresh both selected-item and active-action pose caches.
3. Refresh calibration snapshots/IK or rebind affected rigs; restore current selection/action inputs on both views.
4. Invalidate evaluated release samples and stale attachment data, then prepare/evaluate/commit both previews from the new values.

`PlayerHeldItemPresentation.PoseContentChanged` currently skips refreshing `actionData` during recovery. Replace that limitation with explicit invalidation of authorable pose/finger data for the current phase. Preserve action identity, elapsed time, and already submitted projectile trajectories. Rebase visual transition endpoints from the current evaluated pose where necessary; do not restart the action merely because a field changed.

Contact and finger edits refresh both views immediately. Avatar edits must reach `LocalFirstPersonHands` as well as the full-body rebind path. Rebinding must not leave one view using a stale generation's release sample or palm correction. Slingshot contact refresh must preserve its current charge/recovery state.

## 4. Add isolated drafts and runtime content binding

Create the shared authoring model in `Runtime/Authoring/GripAuthoringDrafts.cs`, available to Editor and development builds. Use typed records for the editable fields, with source identity, last-saved baseline, current values, and dirty state. Item keys include item ID; avatar keys include `AvatarId`; Editor records also retain stable source-asset identity. A shared-default record is keyed by its source settings asset.

The three UI contexts own these fields:

| Context | Editable values |
| --- | --- |
| Item | Item contacts; `GripFingers` in Editor; hold/first-person override switches and groups; applicable slingshot pulling contact/charge groups and switches; existing action timing needed for hold, throw charge, and slingshot recovery. |
| Avatar Calibration | Both palm corrections; `VisualHeight`, `StandingOffset`, `YawOffset`, `FirstPersonPlacementOffset`, `FirstPersonReachOffset`, and `FirstPersonHoldOffset`. Label height/scale and placement distinctly from palm correction. |
| Shared Defaults | Select a source asset: hold/heavy/first-person/slingshot groups on `HeldItemSettings`; hand-placement/free-hand transition fields on `FirstPersonHandsSettings`; existing relaxed/grip/open finger-clip defaults on `AvatarAnimationSet` in Editor. Build clip fields are display-only. |

Do not expose unrelated item physics, damage, avatar cosmetics, generated skeleton fields, or locomotion-clip authoring through these context editors. Existing prefab mechanics remain in their Inspector.

Create transient copies of the existing `ItemRegistry` and `AvatarRegistry` for the authoring session. Populate them from the source registry entries, replacing definitions/settings with session-owned copies and retaining prefab/clip references. Retain the concrete item subclass when cloning. Copy editable nested groups deeply. This is a temporary binding of the registered catalog, not a separate authored catalog.

Add a narrow runtime binding seam:

- `WorldItemRegistry` accepts the session's item registry before `BeginWorld` and exposes it to authoring/HUD consumers. All spawned/pool-reused items resolve definitions from that registry.
- `SessionController` exposes a presentation registry override whose default is its existing avatar registry. `GamePlayerSpawner` / `PlayerAvatarPresentation.Initialize` and `OnStartClient` configure presentation from this effective registry instead of a prefab's fixed source reference.
- Both the owner and observer receive the same runtime registry copies. Their `HeldItemSettings`, first-person settings, and animation-set references resolve to the corresponding shared drafts.
- Runtime draft asset identities remain stable during a session. Field edits update them in place and notify subscribers; source registry arrays and source assets are never replaced or edited by preview binding.
- Preserve item identity across systems that receive these copies. In particular, follow `WorldItem.Initialize` into `BirdRegistry.IsRock` and use the registered item ID wherever source-asset reference equality would reject a runtime definition copy. Keep non-authoring behavior equivalent.
- End the binding before restoring normal menu/gameplay content. Restore source registry references and dispose transient rigs, registry copies, definitions, and subscriptions when the authoring session ends. Editor draft records remain owned by the window.

This avoids applying edits to real assets and attempting to undo them on exit. `AvatarAppearanceStore` is for persisted cosmetic choices and does not own grip or calibration drafts.

## 5. Repurpose the demo into a Solo gameplay scene

Add `Runtime/Authoring/GripAuthoringScene.cs` as the scene controller. Remove the demo's automatic cycling and fixed `HandTarget` behavior, then remove the obsolete `AvatarPresentationDemo` component/script when its scene reference is replaced.

Reuse `Stage`, `Step`, `LeftFootRamp`, `KeyLight`, and `ShowcaseCamera`. Replace `AvatarShowcase` with the observer presentation host. Add the authoring controller, a bootstrap using the existing session prefab, one player spawn marker, a scene `NetworkObject` with `GamePlayerSpawner` and `PickupRegistry`, and the runtime UI document. Reuse the production player prefab and existing suitable UI panel settings.

### Session routing

Add an explicit authoring start route to `SessionController` that sets the target scene and calls the existing `StartSession(SessionMode.Solo)`. Normal sessions still target `Game`. Authoring uses the existing Solo loopback transport, ephemeral port, admission, and automatic `StartGame` behavior; it does not require Steam readiness or a lobby.

Replace the hard-coded `"Game"` load in `StartGame` and the `GamePlayerSpawner.SpawnPlayer` scene-name condition with the active session's selected gameplay-scene identity. Keep authoring routing limited to Editor/development builds.

Support both entry paths:

- Main-menu/window entry selects the route before starting Solo and lets FishNet load the authoring scene.
- Pressing Play directly in the authoring scene bootstraps the session, sets the same route, and allows its normal network scene load. The controller must recognize the subsequent loading/in-game instance and attach instead of starting another session.

Keep `PickupRegistry`'s existing world and bird baseline lifecycle. An authoring scene with no bird habitat can still complete the empty bird baseline; `TryEnterGame` must retain meaningful world/player/bird readiness instead of being forced into `InGame`.

Keep drafts in the Editor window or development session owner through the initial scene reload. Bind session content before the network world and player initialize. Exiting uses the existing session stop/return-to-menu lifecycle and clears the route.

### Selection and actions

Populate content selectors only from the two registries. Use `AvatarRegistry.ContentChanged` and add corresponding change notification to `ItemRegistry` to refresh an open selector and reconcile transient entries without discarding existing drafts. Use IDs for selection and show display names; registration does not depend on unlock state in this tool.

For avatar selection, add a temporary authoring selection entry point alongside `PlayerAvatarPresentation.RequestAvatar` that uses the normal resolve/request/appearance network path without committing a cosmetic choice to `PlayerPrefs`. Feed the accepted identity to the observer and apply its corresponding calibration draft.

For item selection, add a small development/Editor entry point on `WorldItemRegistry` to create a normal world record using `RuntimeItemIds`, pool instantiation, and lifecycle publication. Spawn the requested registered item near the player, then collect it through `PlayerInventory.Collect`. Use the same entry point to replenish a consumed/thrown item on an explicit Equip command. It must not construct a held-only dummy or write inventory arrays directly.

Use `PlayerInventory.SelectSlot` for equip/dequip, respecting its toggle behavior; move an existing selected item into a hotbar slot through the inventory operation when required. Track and clean up authoring-supplied items through registry lifecycle operations so repeated selections do not fill all 24 slots. Do not remove an in-flight action item before its release/recovery presentation finishes.

Drive action controls through `PlayerEquipment.BeginUse`, `EndUse`, `CancelUse`, and `DirectUse`, plus the existing drop path where applicable. Holding is a latched Begin/End pair, not a fabricated charge percentage. Expose only controls applicable to the definition's existing behavior.

Keep normal movement/aim on `PlayerInputReader` and `PlayerMotor`. If UI movement/aim controls supply values, route them into the same input-consumption path, with authoring availability gated by development status rather than `UNITY_INCLUDE_INSTRUMENTATION`.

Editing UI requires explicit input focus handling: suppress world look/use while typing or dragging, but keep the simulation running. `PlayerInputReader.ReadInput` currently cancels charging when input is suppressed. Introduce an explicit authoring command source so a UI-latched charge can stay active during numeric editing without disabling that cancellation for normal gameplay input. Selection changes, cancel, session exit, and teardown end that command cleanly.

## 6. Build the synchronized comparison views

Add `Runtime/Authoring/GripObserverPreview.cs` using `AvatarPresentation`, shared `HeldItemPresentationState`, and an independent visual item/slingshot instance. Reuse the existing avatar-preview layer and orbit/zoom patterns, while retaining the production observer's animation, IK, pose, and finger paths.

The observer visual must not register a second `WorldItem`, rigidbody, collider, item-use behavior, or projectile simulation. Split visual creation/binding from `WorldItem` lifecycle where necessary so the same prefab renderer hierarchy and `SlingshotPresentation` can be instantiated safely as presentation-only content. A release may follow the real projectile's supplied presentation sample without modifying it.

Render first person from `PlayerPresentation.ViewCamera`, initialized through its normal `CreateCamera` path. Capture its initial projection/FOV/clipping/render-pipeline settings for comparison controls. Reconfigure the existing showcase camera as a perspective observer camera with the same initial FOV and suitable orbit framing.

Use two RenderTextures with explicit dimensions and UI Toolkit `Image` elements. Show both panes together, with First Person / Observer expand controls and an orbitable observer. Add a `GripAuthoringOwner` render layer for first-person visual descendants in this scene, assigned when local rigs bind and restored on teardown. Exclude it, the owner's held-item layer, and owner player visuals from the observer camera. The owner camera excludes the existing observer-preview layer. Observer copies have no active collision shapes and do not enter aim/clearance queries.

Camera controls:

- Presets: 1280x720, 1920x1080, 2560x1440, 1920x1200, and 3440x1440; permit numeric width/height for exact comparisons.
- One comparison vertical-FOV control applied to both perspective cameras; observer orbit/zoom remains independent.
- Display each actual RenderTexture pixel size, displayed viewport rectangle/aspect, and effective FOV. Letterbox to preserve the selected aspect when a pane is resized or expanded.
- Reallocate textures only when dimensions change and release old textures on resize/teardown. Pane expansion changes display area, not authored contacts.
- Preserve correct mouse/aim/handle coordinate conversion through the pane's actual image rectangle. Do not use the desktop `Screen.width` as a proxy for a preview viewport.

Editor and build start from the same camera settings and use the same presets. Include the selected avatar/item/action phase and both projection readouts beside the comparison so matching conditions are visible. Resolution and FOV never enter contact math or saved settings.

## 7. Implement the shared UI and Editor persistence

### Shared controls

Add `Runtime/Authoring/GripAuthoringPanel.cs` and scene-owned UI Toolkit layout/styles under `Assets/Game/UI/GripAuthoring/`. Reuse this panel in both an Editor window and the runtime UI document. It binds to the draft/session model and calls injected save/revert/folder actions; it does not call `AssetDatabase`.

Provide avatar/item selectors, the three context tabs, the shared-default source selector, numeric fields, grouped override switches, action controls, camera controls, dual previews, reach/contact readouts, Save, Revert, and dirty markers per context. Give fields the same names in UI and JSON, with separate labels for units and coordinate frame.

Keep contact and hold-placement sections visibly separate. Only show spatial/action fields actually used by that item path. For slingshots, expose both presentation charge groups and the moving pulling contact; do not present it as a fixed heavy left grip. Display mechanical prefab values as an Inspector link in the Editor rather than duplicated numeric authoring fields.

### Editor window and handles

Add `Editor/GripAuthoringWindow.cs`, opened from `Two Birds/Grip Authoring`. It provides an Open Scene / Start Authoring action and hosts the shared panel. Retain normal scene-save prompts when switching an unrelated dirty scene.

Add an Editor adapter for finger-clip `ObjectField` selection and `SceneView.duringSceneGui` translation/rotation handles. The runtime panel shows selected/fallback clip names without an assignment control. Clip selection refreshes `AvatarFingerLayers` through the live data path, with null retaining the existing fallback.

Handles operate on a selected authored target and write its local numeric fields through the same draft setter:

- Item contacts transform through the current item root and its prefab scale.
- Slingshot pulling contacts use the current moving pouch frame; dragging changes the offset, never the mechanical pouch anchors.
- Calibration uses the selected wrist/generated palm frame with its visual scale.
- Hold/charge placement uses the field's body/camera frame and arm-length normalization; heavy placement uses its collider reference center and average-arm frame.

Draw requested targets and evaluated palms separately. Author handles against the authored target, not the reach-corrected output. Keep simulation running while a handle is dragged. Provide a Frame Selected action for the Scene view so its handles can be used while both comparison panes remain visible.

### Draft lifetime, Save, and Revert

Store serialized draft records on the Editor window so they survive domain reload and the transition out of Play mode while it stays open. Preserve source asset identities and clip references using persistent Editor references/identifiers; never serialize live rig objects as draft state. Reconstruct transient copies and subscriptions when the running scene attaches again, including when domain reload is disabled.

Implement persistence in `Editor/GripAuthoringPersistence.cs`:

1. Save identifies the selected context and its one source asset.
2. Resolve the current source asset again and copy only that context's allowed authored fields. In particular, saving avatar calibration cannot overwrite freshly regenerated skeleton measurements, cosmetics, or other tuning.
3. Record Undo, mark that asset dirty, and save that asset specifically. Do not save all dirty project assets or copy an entire runtime clone over its source.
4. Advance only that context's last-saved baseline and dirty state, and notify its live content consumers. Other drafts remain independent.
5. Revert restores only the selected context from its last successful Save baseline, or the source values if it has not been saved during this window session. Reapply inheritance afterward.

Save does not materialize inherited values into item fields. If an override was explicitly enabled while a shared-default draft was active, the copied group is already authored item data and may be saved independently of that default draft.

Use `EditorWindow.hasUnsavedChanges` and its save/discard lifecycle for the close prompt. Identify all outstanding contexts. A close-time Save action must list the contexts it will save and require that explicit choice; the ordinary Save button always saves only the selected context. Cancel keeps the window and drafts open. Play-mode exit alone does not prompt to discard or clear drafts.

Keep source baselines, draft state, and runtime copies separate. Undo/Redo of draft edits refreshes previews; Undo/Redo of a source Save refreshes the matching baseline without silently discarding unrelated drafts.

## 8. Implement development-build exports

Add `Runtime/Authoring/GripAuthoringExport.cs` using the same typed authored records as the UI. Save exports the selected context with formatted JSON under `Application.persistentDataPath/GripAuthoring/`:

```text
item-<ItemId>.json
avatar-<AvatarId>.json
shared-held-item-settings.json
shared-first-person-hands-settings.json
shared-avatar-animation-set.json
```

Include context, display name, item/avatar ID where applicable, source settings name, applicable switches, field names, vector components, and Euler degrees. Include unit/frame labels at the group level so values can be entered directly into the matching Editor controls. Clip names and their inherited source are informational in builds; do not export engine instance IDs as asset identities.

Export authored groups even when currently disabled, along with their disabled override switches; keep any displayed effective/default values separate from those authored fields. Do not export world-space targets, IK output, reach-corrected positions, generated skeleton measurements, viewport transforms, or quaternion-only rotation values.

Repeated Save replaces only that context's file. Mark that context's baseline saved only after the file write succeeds. Revert uses the current session's last exported baseline, or packaged settings if unsaved. A new authoring session always initializes from packaged settings and does not read these files.

Show the full export location and an Open Folder action. Editor Save continues to write source assets through its Editor adapter; development Save never attempts packaged-asset mutation.

## 9. Enforce development-only access and scene inclusion

Add the menu entry in `MenuPresenter` under `UNITY_EDITOR || DEVELOPMENT_BUILD`, creating/binding it only for those builds. It calls the authoring Solo route. Use a scene-path constant or name for routing; production prefabs and menus must not serialize references to authoring-only UI/scene assets.

Keep the authoring scene out of the default release scene list. Add one scene-list policy in `Editor/GripAuthoringBuildPolicy.cs` that operates on the requested `BuildPlayerOptions`:

- When `BuildOptions.Development` is set, include `Assets/Scenes/AvatarPresentationDemo.unity` once while retaining the requested starting scene/order.
- Otherwise, remove that scene from the requested list.
- Apply this policy in `TwoBirdsBuildPipeline.Build` before `BuildPipeline.BuildPlayer`, covering Local Networking, Development, Development - Steam, and Release.
- Register the same policy with Unity's ordinary Build / Build and Run entry point, then invoke the default build method with the adjusted options. Unity provides this interception through [RegisterBuildPlayerHandler](https://docs.unity3d.com/6000.6/Documentation/ScriptReference/BuildPlayerWindow.RegisterBuildPlayerHandler.html).
- Add a `BuildPlayerProcessor.PrepareForBuild` guard that inspects the actual `BuildPlayerContext.BuildPlayerOptions` supplied by any direct build caller. Reject a release request containing the scene, and a development request missing it, with a message to apply the shared policy. Do not assume changing `EditorBuildSettings.scenes` in a late callback changes an already supplied build list. The context exposes the requested options; Unity's [reference implementation](https://raw.githubusercontent.com/Unity-Technologies/UnityCsReference/master/Editor/Mono/BuildPipeline/BuildPlayerContext.cs) exposes them as a getter, so filtering belongs before the build call.

The guard ensures an unfiltered direct build cannot produce a noncompliant player; custom commands and normal Unity builds automatically receive the corrected list. Do not silently fall back to instrumentation or managed-code variant flags as the development test.

Compile authoring controllers, runtime draft UI, and export adapters only for Editor/development builds. Keep contacts, calibration, shared presentation evaluation, and normal content-change support in production. Authoring UXML/styles and other exclusive assets are referenced only by the excluded scene or Editor code, not by production registries, session prefabs, Resources folders, or preloaded assets.

## Delivery order

1. Contact schema, conversion functions, item/prefab migration, and every production contact consumer.
2. Group resolution/override copy semantics, shared slingshot defaults, avatar calibration, and effective-measurement consumers.
3. Shared presentation extraction, explicit content refresh, and reach readouts.
4. Draft model, transient registries, and session content-binding seams.
5. Solo scene route, demo replacement, content selection, and real inventory/action controls.
6. Independent observer, dual cameras, and comparison controls.
7. Shared panel, Editor window/handles, per-context persistence, and draft lifetime.
8. Development JSON export and development-only menu/build policy.

Keep each schema change paired with its asset conversion and affected consumers. The observer is complete only when ordinary multiplayer observers and the authoring comparison use the same production evaluator.

## Designer acceptance

Use **138 Chill** and **175 Genesis Gerbil**. Begin with Rock, Boulder, and Slingshot; then cover all nine registered items. Perform these checks in the Editor and development build under matching camera and authored settings.

| Area | Manual acceptance |
| --- | --- |
| Entry and selection | Start directly from the scene and through the development menu with Steam unavailable. Both views select registered avatars/items and enter real gameplay. Newly registered content appears without a second catalog. |
| Ordinary grips | Inspect both avatars during idle, walking/running, aim, equip/dequip, throw/use, and transitions. The item remains attached to the evaluated palm as placement becomes reach-limited. Finger clips look convincing in both views. |
| Heavy grips | Inspect both contacts during holds, charge, release, and transitions. Reachable poses retain contact; impossible poses clamp arms and identify unreachable contacts without stretching arms or resizing the item. |
| Slingshot | Inspect hold, partial/full charge, prolonged charge, cancel, release, and recovery. The pulling hand follows the moving pouch; bands, loaded pebble, and projectile departure agree with evaluated contact. |
| Calibration | Apply a distinct position and rotation correction to each palm. Both rig types respond consistently. Save, leave Play mode, reprocess the avatar, and confirm the corrections remain authored and effective. |
| Immediate editing | Edit contacts, finger clip, hold/charge groups, and calibration while idle and during applicable actions, including recovery. Both views update without re-equipping. Numeric fields and Editor handles agree. |
| Inheritance | Change a shared draft with an item inheriting it. Enable an override and confirm the entire effective group is copied. Change defaults again and confirm the overridden item stays unchanged; disable the override and confirm inheritance resumes. |
| Draft boundaries | Edit multiple items, both avatars, and multiple shared assets. Switch selections/contexts and leave/re-enter Play mode with the window open. Save or revert one context and confirm others remain unsaved. Closing identifies outstanding drafts and permits cancellation. |
| Saved assets | Reopen saved item/avatar/default settings after Play mode. Only explicitly saved fields persist, inherited groups stay inherited, and generated avatar data remains intact. |
| Camera comparison | Compare the resolution/aspect presets and several FOVs, then expand each pane. Confirm readouts match the actual viewport and contact remains fixed even when framing changes. |
| Observer agreement | Compare the authoring observer with a separate real remote client using saved matching settings and equivalent action phases. Judge contact/pose agreement separately from network delay and interpolation. |
| Exports | Save each context twice, inspect formatted field names/units/angles/IDs and switches, and use Open Folder. A new development session starts from packaged values; existing exports remain on disk. |
| Build exclusion | Use custom and ordinary Unity build paths in development and release configurations. Development includes scene/menu access. Inspect release build contents for exclusion of the scene and exclusive UI/assets. An unfiltered direct build request is stopped by the build policy guard. |

When an Editor/build difference appears, first match avatar, item, authored/draft values, action phase, view mode, actual viewport, FOV, and relevant camera/render settings. Identify the divergent production stage before changing authoring values; do not add resolution-specific contact offsets.
