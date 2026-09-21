# Avatar Editor Implementation Plan

## 1. Objective and scope

Implement [Avatar_Editor_Spec.md](Avatar_Editor_Spec.md): one fullscreen UI Toolkit avatar editor, available from the main menu, lobby, and a world station. Edit one local draft containing an avatar, one optional hat, and up to eight ordered tattoos. Apply and forced interruptions save the draft; Cancel discards it. Committed appearance persists locally and replicates through FishNet, including to later observers. Arm and hand tattoos also appear on the separate first-person representation.

Use Unity 6000.5.7f1, the installed URP 17.5 package, New Input System, FishNet 4.7.3, and the existing VRM presentation. The spec is authoritative if another document disagrees.

Do not implement milestone tracking, achievements, tattoo unlocks, player hat adjustment, tattoo opacity, mirroring, multiple ink regions, tattoo reorder controls, station occupancy, or station movement/destruction. Retain the spec's accepted rigid-projector distortion and projection spill.

## 2. Execution constraints and assumptions

- Follow `AGENTS.md`. Use the Unity CLI at `C:\Users\spenc\AppData\Local\Unity\bin\unity.exe` for supported Unity operations, falling back to Unity MCP. Discover supported commands rather than assuming CLI syntax. Use normal Unity asset workflows and let Unity generate every `.meta` file.
- Announce the required assets and component assignments before implementation: editor prefab/UI/preview, cosmetic catalogs, avatar authoring additions, decal shader/material and renderer feature, processed GentlemanHat assets/icons, and station component hookup.
- Keep gameplay scene authoring to the documented station hookup. Configure persistent feature dependencies through `SessionRoot.prefab` and the new editor prefab. Do not overwrite unrelated scene or source-pack changes.
- Do not add one-time migration tools. Extend the reusable Avatar Processor and implement the explicitly requested reusable Hat Setup workflow. Process existing avatars through that workflow when their generated region data changes.
- Keep new runtime code in namespace `TwoBirds`, editor code in `TwoBirds.Editor`. Cache asset lookups, bones, cameras, controls, and surface data at initialization or binding. Reconstruct cosmetics on changes and binding events, not every frame.
- The user supplies tattoo artwork. Build the catalog and import instructions without inventing final designs. Actual tattoo visual acceptance requires at least one supplied transparent design. An empty catalog must leave avatar/hat editing usable.
- Use a local preview prefab and a RenderTexture shown by UI Toolkit. No network player is required. This is an implementation choice for satisfying all three entry points without adding scene-specific editor rigs.
- Treat the ordered tattoo list as authoritative identity/order for persistence and replication. Use temporary editor row handles for selection; do not add networked GUIDs for individual instances when list position already distinguishes duplicates.
- Do not run automated tests, builds, play-mode checks, screenshots, or visual validation unless the user requests them. Section 12 assigns visual acceptance to the user; content processing and icon generation remain required authoring work.

## 3. Existing integration points

All paths below are relative to the project root.

| Area | Files and integration contract |
| --- | --- |
| Avatar identity/content | `Assets/Game/Runtime/Avatars/AvatarId.cs`, `AvatarRegistry.cs`, `AvatarSettings.cs`. Preserve the existing 64-bit avatar IDs, source registration, and generated full/first-person measurements. |
| Initial avatars | `Assets/Game/Settings/Avatars/44816e438f2e4775.asset` is 138 Chill; `461d9592f370666a.asset` is 175 Genesis Gerbil. Both use generated first-person meshes and must be default-unlocked. Registry: `Assets/Game/Settings/Avatars/AvatarRegistry.asset`. |
| Full representation | `AvatarPresentation.cs` supplies `Configure`, `RequestAvatar`, `IdentityResolved`, `WillUnbind`, `DidBind`, `Binding.Generation`, and `SetFeatures`. `AvatarBinding.GetBone` supplies cached humanoid bones. Bind only to the accepted representation, accounting for staged replacements. |
| Evaluation/pose | `AvatarInstance.cs`, `AvatarAnimationGraph.cs`, `AvatarHumanoidIK.cs`, and `AvatarPresentationSystem.cs`. Disabling animation currently also stops regular IK/VRM evaluation; a frozen editor pose must preserve head-look evaluation explicitly. |
| Gameplay appearance | `Assets/Game/Runtime/Player/PlayerAvatarPresentation.cs` currently synchronizes `SyncVar<AvatarId>` and uses avatar-only request/deferred-selection paths. Replace those with coherent appearance snapshots. Leave the separate `AvatarLookSample` traffic unchanged. |
| First-person lifecycle | `Assets/Game/Runtime/Player/PlayerHandPresentation.cs` creates, initializes, evaluates, swaps, and destroys `LocalFirstPersonHands`. Its candidate swap and `StopPresentation` are the attachment/cleanup boundaries. Full-body `DidBind` alone cannot cover local arms. |
| Menu | `Assets/Game/UI/Menu.uxml`, `Assets/Game/Runtime/UI/MenuPresenter.cs`. `Back()` currently leaves a non-idle session; editor Back must be consumed before this path. |
| Persistent session | `Assets/Game/Runtime/Networking/SessionController.cs`, `SessionBootstrap.cs`, `Assets/Game/Prefabs/SessionRoot.prefab`. Use `BeginLoading`, `QueueStarted`, `Leave`, `ShutdownPlatform`, and local-player availability for explicit lifecycle integration. |
| Input/modal behavior | `PlayerInputReader.SetGameplay`/`ClearContext`, `SessionController.SetPanel`/`InputInterrupted`, `SessionOverlay.cs`, `InputPresentation.cs`, `InputPrompt.cs`, and `MenuNavigation.cs`. `PanelOpen` means pause UI, so it must not represent avatar editing. |
| Input asset | `Assets/InputSystem_Actions.inputactions`. `UI/Scroll` uses the right stick. `InputBindings.cs` currently exposes Player-map bindings; extend it narrowly for editor actions and their effective binding prompts. |
| World interaction | `PlayerInteraction.cs`, `IInteractable.cs`, `InteractionTooltip.cs`. Use `Player/Interact`, `PickupRange`, cached aim/view references, and a non-trigger station collider on an ordinary queryable world layer. |
| Damage/downing | `PlayerHealth.DamagingHit` and `LifeChanged`. `DamagingHit` fires before the lethal-hit downing transition completes; force-close must be idempotent and tolerate that order. |
| Camera exclusion | `PlayerPresentation.CreateCamera` creates the local camera and exposes `LocalCameraChanged`. Configure preview visibility at camera creation/scene entry, not through camera searches in Update. |
| Authoring | `Assets/Game/Editor/AvatarProcessor.cs`, `AvatarSettingsEditor.cs`, `IconCaptureWindow.cs`, `ItemSetup.cs`. `CaptureAndSave` already supports a null `ItemDefinition`; it returns a Sprite. |
| PC rendering | `Assets/Settings/PC_Renderer.asset` uses Forward+ (`m_RenderingMode: 2`) and currently has SSAO. Add the Decal Renderer Feature without replacing the existing feature. |

## 4. Shared data and service contracts

Add focused files under `Assets/Game/Runtime/Avatars/Appearance/`. Keep persistence, transfer, and reconstruction shared across the editor, full avatar, and first-person arms; do not duplicate them in UI presenters.

### 4.1 Content definitions

- Extend `AvatarSettings` with `Icon`, `UnlockedByDefault`, designer head-fit position/rotation/scale, and generated tattoo-region data for full and first-person representations. Keep new designer values outside regenerated measurements. Existing processing clones settings, so preserve these fields during reprocessing and identity-preserving updates.
- Add `HatDefinition`, `HatCatalog`, `TattooDefinition`, and `TattooCatalog` ScriptableObjects. Definitions hold stable IDs and display/icon data. Hats also hold source reference, processed visual prefab, default availability, and authored local fit. Tattoos hold artwork and its aspect ratio; all registered designs are available.
- Use separate `HatId` and `TattooId` value types following the existing `AvatarId` serialization pattern. Zero means no hat; zero is not a registered content ID. Assign IDs once and persist them in definitions. List order, display name, and generated asset filename must not determine identity.
- Cache catalog lookups and report duplicate IDs during authoring. Runtime rendering resolves content independently of local unlock ownership.
- Keep a shared tattoo decal material reference in the tattoo catalog. Do not require one authored material asset per tattoo instance.

### 4.2 Appearance snapshots

Define `AvatarAppearance` and `TattooAppearance` with these semantics:

| Field | Contract |
| --- | --- |
| Avatar | Existing `AvatarId`. |
| Hat | `HatId`, or zero. Fit transforms are content-derived. |
| Tattoos | Ordered array/list with at most eight elements, oldest first. Deep-copy at draft and publication boundaries. |
| Tattoo design | Stable `TattooId`. |
| Attachment | Stable region key resolving to one humanoid bone or an authored custom bone mapping. Bone Transform references and mesh triangle indices are never persisted. |
| Position | Coordinates normalized within the attachment region's canonical frame and dimensions. |
| Surface orientation | Region-relative surface normal plus a defined tangent basis; rotation is a twist around that normal. Use one deterministic tangent convention in all representations. |
| Size | One positive relative scalar; artwork aspect ratio determines the other dimension. Normalize against the local region's tangent-plane dimensions rather than whole-avatar height. |
| Ink | RGB bytes with fixed ink alpha of one. Artwork controls transparency. |

Provide deep clone, ordered structural equality, and content-resolution helpers. Compare values, not array reference identity. A draft can become clean again after edits return it to the original values. Selection, preview framing, mode, and temporary row handles are not appearance data.

### 4.3 Local services

- Create `AvatarAppearanceStore`, owned by the persistent session, with `Committed`, `Commit(snapshot)`, and `CommittedChanged`. Load once from a versioned PlayerPrefs JSON value such as `AvatarAppearance.v1`. A commit deep-copies, saves locally with `PlayerPrefs.Save`, and raises a change only when appearance values differ.
- Keep local persistence independent of player/network existence. Resolve missing saved content deliberately: fall back to a valid default-unlocked avatar, clear an unavailable hat, and omit missing/unresolvable tattoo entries without disturbing the remaining order. Invalid JSON falls back to a default appearance. Do not normalize saved state from an observer's unlock inventory.
- Create `CosmeticUnlockService` with distinct `UnlockAvatar(AvatarId)` and `UnlockHat(HatId)` operations and corresponding availability queries. Persist permanent grant sets in PlayerPrefs, separately from appearance. Effective availability is default-unlocked OR explicitly granted. Repeated grants are no-ops; emit an event for newly effective unlocks so an open library refreshes immediately.
- Session initialization constructs the services from serialized registry/catalog references before menu/editor use. Subscribe the local gameplay adapter when ownership starts and detach when ownership stops. Remote players never write the observing client's preferences.

## 5. Body regions, surface placement, and transfer

Implement shared region/placement logic in `AvatarTattooPlacement.cs`; put reusable generation support alongside `AvatarProcessor` in `AvatarTattooRegionAuthoring.cs`.

1. Generate region records for torso sections, head, each upper/lower limb, hands, feet, and supported finger segments from humanoid bones and source skinning. Store a canonical anatomically oriented frame relative to the attachment bone, per-axis dimensions, and reference surface geometry sufficient for regional reprojection. Derive axes from anatomical directions/reference pose, not arbitrary imported bone axes.
2. Classify mesh triangles using summed skin influences mapped to these regions, with deterministic handling at seams. Body geometry not covered by humanoid bones needs an explicit custom region mapping. Preserve unique custom keys across reprocessing; only a deliberately shared semantic key permits transfer to a different avatar. Do not map unrelated tails/ears/accessories to a torso region merely because they are nearby.
3. Generate corresponding first-person region records from the actual processed first-person meshes, including separately authored `FirstPersonSource` content. Only regions represented by visible arm/hand geometry receive first-person projectors. Use the same semantic region keys and canonical axis convention as the full avatar.
4. Regenerate derived records through Avatar Processor without resetting authored icon, unlock, hat-fit, or custom-region mapping fields. Version generated data deliberately and process both initial avatars. Avoid introducing runtime mesh extraction on every player frame.
5. For active preview placement, cache baked avatar meshes and their triangle-to-region mapping after the editing pose is established. Raycast these meshes directly, or use a private query structure owned by the preview. Do not raycast hats, scene colliders, or unrelated renderers. Refresh cached surfaces only after binding or pose changes. A CPU mesh query avoids adding interactive physics objects to the game scene.
6. Convert a surface hit into its canonical region position and normal. Define the zero-rotation tangent by projecting the region's canonical up axis onto the surface, with its right axis as the fallback near parallel normals. Store the user's twist relative to that tangent. Normalize size against the geometric mean of the region's extents projected onto the surface tangent axes; apply the artwork aspect ratio proportionally.
7. Reconstruct by denormalizing in the destination region, finding the nearest compatible surface point within that same region, and resolving the projector pose against its bone. Use the saved normal to distinguish opposite surfaces and preserve facing. Reject a match outside a bounded regional distance or with an incompatible normal; never jump to another limb to force a match. Put the matching tolerance in the placement implementation, not player settings.
8. Transfer on avatar selection by resolving each existing tattoo against the destination region records in list order. Keep design, RGB, twist, relative size, and order; update its normalized surface pose to the accepted destination point. Silently remove entries with no suitable counterpart. Apply transfers to the current evolving draft only; do not retain a history per avatar.
9. Rebinding a representation of the same avatar reconstructs cosmetics without editing committed data. A first-person reconstruction failure hides that representation's unresolved tattoo; it does not remove the full-avatar tattoo or write preferences.

Adding a tattoo casts onto the front central torso surface in the stationary pose, creates a new last element, selects it, and enters placement mode. Use a deterministic central-body region fallback if the preferred torso segment is absent. Do not create a floating projector on a failed surface hit. At eight instances, disable Add while retaining all editing/removal controls.

## 6. Cosmetic rendering and representation lifecycle

Implement `AvatarCosmeticPresentation` as shared owned reconstruction logic taking a binding, appearance, catalog references, and full/first-person mode. It owns created hats, projectors, and material instances and releases them at unbind/disposal.

### 6.1 Hats

- For a full-avatar binding, create one visual hat under the head bone. Compose a designer avatar-fit transform followed by the hat-fit transform. Define their units as unscaled avatar-local units, inherited once through the avatar hierarchy; do not multiply by avatar scale twice.
- Both avatar and hat fits expose position, rotation, and scale for designers. The two positional offsets remain independently authorable. If either offset is zero, the other still applies. Preview and gameplay call the same composition function.
- Recreate/reattach on the accepted `DidBind` generation and dispose on `WillUnbind`. Do not attach hats to first-person arms. Do not add inventory, pickup, network object, or item-use components to cosmetic visuals.

### 6.2 Tattoos and URP

- Add one Decal Renderer Feature to `Assets/Settings/PC_Renderer.asset`; explicitly choose **Screen Space** and disable **Use Rendering Layers**. Keep the existing Forward+ renderer and SSAO configuration. Preview and gameplay cameras must use this PC renderer.
- Create a URP Decal Shader Graph and material under `Assets/Game/Art/Cosmetics/Tattoos/`. Expose artwork texture and ink RGB, preserve texture alpha, and enable the graph's **Angle Fade** support. Use single-ink artwork with shape/detail carried by transparency or a documented luminance mask; retain that mask when tinting. Disable unnecessary normal/MAOS contributions.
- Use standard `UnityEngine.Rendering.Universal.DecalProjector` components, parented directly or through a pose child to the resolved attachment bone. Choose shallow region-scaled depth and angle fading; keep player opacity fixed. Projector size must account for inherited scale exactly once.
- Create a private material instance for each rendered tattoo. Set design texture, color, and `_DrawOrder` before registration. Installed URP reads drawing order from the material, not a public per-projector drawing-priority field. Use list index as priority so later entries cover earlier entries. On removal, update/rebuild priorities without changing the relative order of survivors.
- Update only the affected draft projector during ordinary movement, rotation, size, or color gestures. Rebuild on structural content/binding changes. Destroy owned materials and projectors at disposal; never modify shared source materials.
- Keep existing avatar receiving materials unchanged. Do not add receiver-isolation shaders or skin-deforming decal geometry. Projection spill onto a nearby hat, limb, player, or wall remains within the specified limitations.

Unity references: [Screen Space and rendering-layer configuration](https://docs.unity3d.com/6000.0/Documentation/Manual/urp/renderer-feature-decal-reference.html), [projector volume and angle controls](https://docs.unity3d.com/6000.0/Documentation/Manual/urp/renderer-feature-decal-projector-reference.html). For exact property support, use the installed package's `Runtime/Decal/DecalProjector.cs`, `Runtime/Decal/Entities/DecalUpdateCachedSystem.cs`, and `Editor/ShaderGraph/Targets/UniversalDecalSubTarget.cs`.

### 6.3 Full avatar and first-person integration

- `PlayerAvatarPresentation` owns the accepted desired appearance and attaches full-avatar cosmetics through `WillUnbind`/`DidBind`. Match the snapshot's avatar ID to the binding; never apply new-avatar placements to an older binding while a candidate is loading.
- Initialize each `LocalFirstPersonHands` candidate with the same accepted appearance after its binding exists and before `SetVisible(true)`. Have `PlayerHandPresentation` forward appearance-only changes to the active local rig and pass the latest matching snapshot into replacements. Cleanup belongs to the local rig's destruction/stop lifecycle.
- First-person reconstruction filters visible arm/hand regions but retains the original ordered indices for priorities. It must work when the owner's full avatar is hidden and has no active binding.
- Preserve ragdoll lifetime rules. Save and synchronize the committed snapshot even when an avatar replacement must wait for revival. Defer the complete visual appearance when its avatar differs from the life-locked representation; apply matching cosmetics together when the new binding is safe. Do not retain avatar-only deferred fields that could lose hats or tattoos.

## 7. Preview, draft lifecycle, and modal input

Add `AvatarEditorController.cs` for draft/lifecycle coordination, `AvatarEditorPreview.cs` for presentation/framing/surface queries, and `AvatarEditorInput.cs` for active preview controls. Keep their responsibilities separate from network simulation.

### 7.1 Preview composition

- Create `Assets/Game/Prefabs/AvatarEditor.prefab` with its controller, UIDocument, preview root containing `AvatarPresentation`, camera, and local lighting. Reference `Assets/Game/UI/SharedPanel.asset`; use a higher UI sorting order than menus/HUD. Create/release the RenderTexture with the preview viewport size. Do not add an AudioListener or mark the preview camera MainCamera.
- Reserve an unused ordinary GameObject layer named `AvatarPreview` through Unity project settings. Apply it to preview avatar/hat/projector descendants on binding. This camera-culling layer is independent of disabled URP decal rendering-layer filtering.
- Place the preview stage away from the playable world, cull the preview camera exclusively to that layer, restrict preview lighting to it, and exclude it from gameplay/menu cameras at creation or scene entry. Use `PlayerPresentation.CreateCamera`/`LocalCameraChanged` and cached menu camera discovery once per entry. Disable any physical colliders/bodies on preview instances; use the private mesh-query data for placement. Do not rely on editor-only preview-scene APIs in builds.
- Give `AvatarPresentation` a narrow editor-pose mode: freeze the existing idle animation sample, disable foot IK and springs, and apply stable spread-arm targets using the existing hand IK. Freeze animation time without disabling graph/IK/VRM evaluation. The pose should expose torso and limb surfaces without playing breathing/locomotion motion.
- Feed preview-specific facing/look input, never a `PlayerMotor`. Rotate/orbit the camera around the stationary avatar through full yaw and limited pitch; do not let head-look yaw make the avatar turn its body. Outside tattoo mode, derive a bounded look target from the pointer and feed the existing head-look behavior. Inside tattoo mode, neutralize head look and bake the stationary surface after the pose settles.
- Track a selected-tattoo focus point for zoom; Reset View clears that focus and restores whole-avatar framing. Keep the active tattoo selection intact. Reframe once after avatar replacement.
- Serialize preview avatar switches while a replacement is staging. Keep the last displayed draft authoritative until the matching new binding and transferred cosmetics are ready, then accept them together. A forced close during staging commits that last displayed state. Cancel invalidates pending callbacks so they cannot republish discarded edits.

### 7.2 Editor lifecycle

Opening captures the origin and initiating focus target, clones `Committed` into one draft, and displays it. Record the station/player/collider references only for a station-origin session. Keep a close-in-progress guard so multiple interruption callbacks cause one commit and one cleanup.

| Trigger | Required operation and hook |
| --- | --- |
| Apply | Commit displayed draft, update owned gameplay appearance through the store event if available, close, restore origin context/focus. |
| Cancel | Discard draft and staged work; close without saving or confirmation. Gameplay already retains the previous committed appearance. |
| Back/Escape | First consume placement-mode Back to return to panel. Otherwise close directly if clean, or show a controller-accessible discard confirmation if dirty. Confirm discards; dismiss returns to the draft. Explicit Cancel still discards immediately. |
| Host/client game start | Force-commit at the beginning of `BeginLoading`, before phase changes and scene replacement. Cover host `StartGame` and client `ReceiveStarting`; make `QueueStarted` a second idempotent pre-unload boundary for loading paths that reach the scene queue first. |
| Leave/disconnect/session end | Force-commit at the start of `Leave`, before pause/phase changes, lobby teardown, or `StopNetwork`. Connection-state callbacks already route session failure into Leave. Local saving must succeed even if the connection has already stopped. |
| Damage/downing | Subscribe to the station user's `DamagingHit` and `LifeChanged`. Commit and close immediately; preserve the health system's subsequent downed input transition. Healing does not close the editor. |
| Station range | While that editor is open, compare the live local aim/player position to the cached station collider's closest point using `PlayerInteraction.PickupRange`. This active range check must detect external movement even with gameplay input disabled. Crossing the range commits and closes. Do not require continued gaze at the station. |
| Normal quit | Force-commit at the start of explicit application/platform shutdown before preferences/services/network disposal. Keep normal-quit handling distinct from generic UI destruction. |
| Focus loss/Steam overlay/controller disconnect | Keep draft, selection, and mode. Release drag capture and clear active gesture inputs; retain already displayed edits. Resume after existing input suppression/release handling permits an available device. Damage/range/session callbacks continue to operate. |
| UI disable/destruction | Dispose visuals, event subscriptions, and input ownership only. Never treat arbitrary disable/destruction as Apply. |

### 7.3 Input context and routing

- Add explicit editor-modal state to `SessionController` and consolidate its existing gameplay-enable expression into one helper: InGame, no pause panel, no console, and no editor. Invoke it on actual context changes, including game entry and local player availability. Do not call `SetPanel(true)` when opening the editor.
- Update `InputInterrupted` to clear player input but keep the editor modal when open; the normal gameplay interruption behavior remains intact elsewhere. Ensure `SessionOverlay`, menu navigation, and HUD/inventory shortcuts defer to the editor while it owns focus.
- On close, recompute the current context rather than restoring an old gameplay-enabled boolean. Downing, stopping, loading, or another modal can have changed it. Use existing `ClearContext` and a shared release-until-neutral gate for held buttons, triggers, and movement sticks, so UI inputs do not immediately jump, interact, throw, move, or give up in gameplay.
- Extend `Assets/InputSystem_Actions.inputactions` with a dedicated editor map for preview rotation, zoom, tattoo movement, rotation, and resize. Reuse UI submit/cancel/navigation/pointer actions where appropriate. Enable this map only while editing; do not read fixed keys or gamepad button names directly in the controller.
- Extend `InputBindings` just enough to register the new editor actions by full action path and expose remapping where applicable. Scope conflict reporting by simultaneously active contexts. Subscribe hints to `InputPresentation.Changed` and binding changes, using `InputPrompt` and effective bindings.

| Operation | Mouse/keyboard | Controller |
| --- | --- | --- |
| Panel navigation/select | Click normal UI controls | Left stick/D-pad, Submit |
| Preview rotation | Right drag within preview; left drag only on preview background | Right stick in panel and placement modes |
| Preview zoom | Wheel over preview | Right/left triggers in both modes |
| Tattoo move | Drag movement handle across avatar surface | Left stick in placement mode |
| Tattoo rotate | Drag rotation ring | Bumpers in placement mode |
| Tattoo resize | Drag resize handle proportionally | D-pad up/down in placement mode |
| Exit placement | Back or return to left-panel controls | B/Circle |
| Color | RGB sliders/preset swatches | Focus/adjust the same controls |

- Use an explicit pointer gesture owner: UI control, tattoo move/rotate/resize, or preview rotation. Capture the pointer for the gesture; a gizmo drag cannot also orbit. Invalid surface hits retain the last valid placement. Compute controller movement in preview screen space and project onto the avatar; a missed surface retains the previous location.
- Suspend `UI/Scroll` ownership while the editor is open so right stick cannot scroll libraries behind preview rotation. In placement mode, consume/suspend normal UI navigation from left stick and D-pad while retaining Back, rotation, and zoom. Restore previous action availability at exit, including confirmation-dialog transitions.
- `MenuNavigation.Control` currently omits sliders and its Move handler consumes navigation. Extend it narrowly for focusable RGB sliders and horizontal value adjustment while allowing vertical movement between controls. Equipped rows and their X buttons must be separate focusable targets; row removal must not also select/submit its parent.
- Route `UI/Pause`, `UI/Cancel`, and Toolkit cancel consistently through the editor with one handled-frame guard. Include modal ownership guards in direct `MenuPresenter`/`SessionOverlay` input-action callbacks; Toolkit event propagation alone does not stop those subscriptions.

## 8. UI and entry points

Add `Assets/Game/UI/AvatarEditor.uxml`, `AvatarEditor.uss`, and `Assets/Game/Runtime/UI/AvatarEditorPanel.cs`.

- Fullscreen two-column layout: libraries and controls on the left, live preview on the right. Use existing shared styles where appropriate, with editor-specific dimensions and responsive viewport sizing.
- Provide avatar/hat/tattoo library tabs, visible preview rotation/zoom hints, Apply, Cancel, Remove All, and Reset View. Remove All clears hat and tattoos while preserving the avatar.
- Selecting an unlocked avatar uses the transfer path; selecting a hat replaces the one equipped hat. Locked entries remain non-equipping and cannot trigger a hidden preview change.
- Render locked icons as black alpha silhouettes with a lock overlay and only the label **Locked**. A black tint of the transparent icon can provide the silhouette. Conceal actual names in tooltips and accessible labels as well as visible text. No unlock requirements are displayed.
- Tattoo rows distinguish duplicates using editor instance handles/list position. Each row selects one instance and has an independent X removal button. Removing the selection clears or moves selection predictably and exits placement if none remain.
- Provide Edit placement, RGB sliders, and a small fixed set of ink swatches. No opacity control. Maintain controller focus after additions, removals, unlock events, and avatar transfers; focus a surviving neighbor when its row disappears.
- Add Avatar Editor buttons to both main and lobby pages in `Menu.uxml`. `MenuPresenter` opens the shared persistent controller with an origin/focus token; hide or suspend the underlying page's navigation while open. Closing in the same session context restores that page and initiating button. A forced session transition takes precedence over page restoration.
- Add `AvatarEditorStation.cs` implementing `IInteractable`, with `InputActionPath = "Player/Interact"` and concise action text such as "Edit avatar". Use `SessionController.LocalPlayer` and existing eligibility conditions; do not introduce station RPCs, an occupant, or a NetworkObject requirement.
- Document station hookup: add the component to a fixed world object with a non-trigger collider on a layer queried by `PlayerInteraction`; place it on the collider or an ancestor; optionally provide a tooltip anchor. No Rigidbody or damage component is required. Each client opens its own local editor independently.

## 9. Persistence and FishNet synchronization

1. Replace `PlayerAvatarPresentation.selected` and avatar-only RPCs/deferred selection with one `SyncVar<AvatarAppearance>` or equivalent single coherent snapshot. Assign fresh deep-copied snapshots; never mutate an array already held by a SyncVar.
2. Initialize the server player with a valid default snapshot in the existing `GamePlayerSpawner` initialization path. On owner client initialization, load/apply the local committed snapshot immediately and submit it reliably once. Prevent the initial server default from overwriting the owner's saved appearance while that submission is pending.
3. Subscribe the owned player to `AvatarAppearanceStore.CommittedChanged`. Apply responsively on the owner, then send one reliable commit RPC if the owner connection is live. The server accepts the owner's choice, resolves known content, and updates the synchronized snapshot only if structurally changed. Do not synchronize or enforce the observer's unlock inventory.
4. Use FishNet synchronized state for initial observation and later observers. Keep remote application idempotent in host mode, where server/client callbacks can both run. Appearance replication must not depend on having witnessed the original edit RPC.
5. Keep the existing look-angle channel separate. No gesture, camera, preview, projector-transform, per-frame cosmetic, or unchanged appearance traffic.
6. Add explicit serializers in `AvatarAppearanceSerializer.cs`: stable content IDs, one-byte tattoo count, region keys, required normalized pose/size values, and RGB bytes. Keep pose floats initially for fidelity; avoid a speculative compression protocol. Do not send strings for bone paths, content fit transforms, thumbnails, preview selection, or unlock sets. Resolve bone paths locally from shared region data.
7. Structural content resolution can omit unavailable remote content while preserving valid order; trust normal owner choices rather than adding anti-cheat infrastructure. An unavailable network/player object must never stop a local commit/save.
8. Update existing avatar request callers to the new appearance path, preserving hats and transferring tattoos when changing identity. Remove only avatar-only fields/methods made obsolete by this replacement. Bump `SessionController.Protocol` once for the incompatible appearance wire change.

## 10. Hat Setup and content authoring

Add `Assets/Game/Editor/HatSetup.cs` with menu item **Two Birds > Hat Setup**.

1. Accept a source hat prefab, library name, default availability, and authored fit. Allow selecting an existing definition for reprocessing. Find an existing source registration rather than generating another identity on each click.
2. Save definitions under `Assets/Game/Settings/Cosmetics/Hats/`, generated visuals/materials under `Assets/Game/Generated/Cosmetics/Hats/<stable-id>/`, and icons under `Assets/Game/UI/Icons/Hats/<stable-id>/`. These paths keep equally named hats distinct. Register the definition in the shared catalog without duplicates.
3. Prepare only presentation content. Copy incompatible materials into the generated folder and convert the copies to appropriate URP materials while preserving source textures/colors. Preserve the source pack and authored transforms. Reuse already compatible source material assets when no conversion is required.
4. Capture the processed hat using `IconCaptureWindow.CaptureAndSave` with transparent background, bounds-based framing, and no `ItemDefinition`. Give the capture its stable-ID-specific folder/prefab name so recapture overwrites that hat's icon predictably without colliding with another hat or item. Assign the returned Sprite to the definition.
5. Preserve the ID, availability, and fit values during reprocessing unless the designer explicitly edits them. Show the existing values when opening a registered source. Provide a clear recapture operation through the reusable workflow.
6. Process exactly `Assets/_3rdParty/Zekirak/Lowpoly Hat Pack/Prefabs/GentlemanHat.prefab`. Make it default-unlocked. Author shared hat fit and the two avatar head-fit records for Chill and Genesis Gerbil; the user checks the final fit visually. Do not process other hats in the pack.
7. Capture transparent icons for the two existing avatars through the shared capture functionality and assign them to their settings. Both remain default-unlocked after processing.
8. Create the tattoo catalog authoring path with ordinary ScriptableObject inspectors/Create Asset menus. Document supplied artwork import, alpha/mask expectations, stable-ID assignment, icon assignment, catalog registration, and aspect ratio. A separate tattoo-processing window is unnecessary.

Write concise durable instructions in `Docs/Avatar_Editor_Authoring.md`: adding/reprocessing hats, recapturing icons, changing default availability and avatar/hat fit, importing tattoo artwork, calling the two unlock APIs, required asset assignments, and station hookup. Do not put work-log status or verification narratives in this document or `AGENTS.md`.

## 11. Implementation order and deliverables

Implement in this dependency order. Each step defines resulting behavior, not permission to run validation.

| Step | Work | Completion condition |
| --- | --- | --- |
| 1 | Appearance/ID types, catalog definitions, store/unlock services, session references | One independent local committed state and permanent grants are accessible in menu, lobby, and gameplay. |
| 2 | Region generation, canonical coordinates, surface reconstruction and transfer | Both avatar representations expose stable region mappings; transfer preserves regional proportions and removes unsuitable tattoos. |
| 3 | Decal graph/material/PC feature, shared cosmetics, binding integration | Hats and ordered colored tattoos reconstruct from one snapshot on preview/full avatar/visible first-person regions. |
| 4 | Reusable Hat Setup, GentlemanHat, avatar icons/default unlocks | The single hat is registered with an icon and designer fit; reprocessing preserves identity/settings. |
| 5 | Preview prefab/pose/framing, draft controller and UI | All editing modifies one displayed local draft; Apply/Cancel/Remove All and transfer behavior are connected. |
| 6 | Editor action map, prompts/navigation, modal routing and lifecycle hooks | Mouse/controller controls and every explicit normal/forced close path use the same draft/commit contract. |
| 7 | Main/lobby buttons and station component | All three entries open the same editor and restore the appropriate context. |
| 8 | Coherent FishNet snapshot and owner store bridge | Initial owner state, changed commits, and late observation use the same appearance, without gesture traffic. |
| 9 | Unity asset assignments and authoring documentation | SessionRoot/editor/catalog/renderer references are assigned; station hookup and supplied-artwork steps are documented. |

Required Unity assignments:

- `SessionRoot.prefab`: avatar registry, hat catalog, tattoo catalog, and shared editor prefab/service initialization references.
- `AvatarEditor.prefab`: controller/panel/preview components, UIDocument UXML and panel settings, preview presentation, camera, lighting, and viewport target.
- Avatar settings: icon, default unlock, head fit, authored custom region mappings where needed, generated full/first-person region data.
- Hat catalog: the processed GentlemanHat definition, processed visual, stable identity, transparent icon, and default availability.
- Tattoo catalog: decal material and user-supplied design definitions when available.
- PC renderer: explicitly configured Screen Space Decal Renderer Feature with rendering-layer filtering off.
- Project settings/cameras: preview GameObject layer and camera culling configuration.
- World object: user-authored station collider/component hookup. No unrelated `Game.unity` edits.

## 12. User visual acceptance

The user performs these checks after implementation, using both mouse/keyboard and a controller where applicable:

1. Open from main menu, lobby, and station. Confirm the same fullscreen layout, correct return page/focus, and no lobby leave or pause menu caused by editor Back.
2. Select both avatars and GentlemanHat. Inspect head attachment and fit during preview gaze and gameplay animation. Check icon framing, transparency, and correspondence to the processed hat.
3. Add supplied tattoos to torso, head, limbs, and hands. Move/rotate/resize/color them, including duplicates and overlaps; confirm eight is the limit and newer tattoos draw above older ones. Edit an older tattoo without changing its overlap order.
4. Use every equipped row and X button, RGB slider, swatch, Apply, Cancel, Remove All, and Reset View with a controller. Confirm Remove All keeps the selected avatar.
5. Orbit/zoom during placement, inspect distinct gizmos, and confirm no simultaneous library scroll/navigation or unintended orbit during a gizmo drag. Change input device/remap controls and inspect live hints, including the station Interact prompt.
6. Inspect the stable spread-arm pose, pointer-driven head look outside tattoo mode, steady head inside tattoo mode, selected-tattoo zoom, and whole-avatar Reset View.
7. Transfer Chill to Genesis Gerbil and back. Inspect region-relative location/size, retained color/twist/order, hat refit, and silent removal where no counterpart exists. Removed tattoos must not reappear on a return switch; Cancel restores the original committed appearance.
8. Apply, reopen, restart, and enter gameplay. Confirm persistence. Cancel and confirmed discard must restore the old committed state; clean Back closes directly; placement Back only returns to panel.
9. During a draft, exercise host game start, disconnect/session end, damage, downing, external movement beyond station range, and normal quit. Confirm the displayed appearance saves once and the appropriate gameplay/downed/session context resumes. Repeat an interruption with the discard dialog open or an avatar switch staging.
10. Lose focus, open the Steam overlay, and disconnect/reconnect the controller. Confirm the draft stays open, available devices resume interaction, and damage/range interruptions still close/save. Hold a UI control while leaving the editor and confirm it does not become a gameplay action before release.
11. Have multiple players use the same station independently while the world continues and players remain vulnerable. Compare committed appearances on host/client, then join later or reobserve an existing player and compare again.
12. Inspect arm/hand tattoos on first-person meshes and other clients' full avatars, including model replacement, downing/revival, and joint bends. Check shallow projection and angle fade while allowing the documented rigid-projector distortion/spill near limbs, hats, players, and walls.
13. Temporarily mark content locked through authoring: confirm silhouette, lock overlay, concealed actual name, and no equip/preview change. Invoke the centralized avatar/hat grant APIs, confirm immediate library updates and permanent availability after restart, and repeat a grant without duplicating state.

If the user later requests automated verification, prioritize behavioral checks for deep-copy/equality and unchanged commits, forced-close idempotence, transfer/order across duplicates and removals, initial/late-observer appearance delivery, and held-input transitions. Do not add tests that only repeat field assignments.
