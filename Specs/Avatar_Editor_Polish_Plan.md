# Avatar Editor Polish Plan

## Objective

Implement [Avatar_Editor_Polish_Spec.md](Avatar_Editor_Polish_Spec.md): show tattoo editing UI only on the Tattoos tab, play the existing shared idle with cursor head tracking outside placement, and use the existing fixed pose for accurate tattoo placement.

## Decisions

- Reuse the editor controller, panel, preview, shared avatar presentation graph, and tattoo surface implementation.
- Treat tab navigation separately from choosing a different avatar. Navigating tabs preserves the selected tattoo; an actual avatar replacement retains the existing transfer and selection-reset behavior.
- Pause the existing idle phase during placement and resume it afterward. Tab navigation and color changes must not rebuild or reseed the animation graph.
- Retain the preview's existing disabled foot IK and springs, cursor tracking limits, camera controls, and cosmetic attachment behavior.
- This feature requires changes to existing C# and UXML files. No new animation asset, Animator controller, prefab, component, or scene change is needed.

## Relevant code

| File | Responsibility and implementation anchor |
| --- | --- |
| `Assets/Game/UI/AvatarEditor.uxml` | The equipped heading, `equipped` list, and `tattoo-controls` are currently separate siblings. `remove-all` follows them. |
| `Assets/Game/Runtime/UI/AvatarEditorPanel.cs` | Owns `tab`, button callbacks, `Refresh`, control enablement, pointer gestures, and placement gizmos. |
| `Assets/Game/Runtime/Avatars/Appearance/AvatarEditorController.cs` | Owns `Selection` and `Placement`. `AddTattoo` currently calls `CentralTattoo` before entering placement. `SetPlacement` coordinates UI navigation and preview state. |
| `Assets/Game/Runtime/Avatars/Appearance/AvatarEditorPreview.cs` | Configures the presentation, owns the placement surface, and provides `SetPlacement`, `CentralTattoo`, `Place`, `Bound`, and `Unbound`. It currently sets `EditorPose = true` and disables animation throughout the session. |
| `Assets/Game/Runtime/Avatars/AvatarPresentation.cs` | Holds editor context, feature switches, and animation state. `UpdateInput` advances animation when `AnimationEnabled` is true. |
| `Assets/Game/Runtime/Avatars/AvatarInstance.cs` | Creates the fixed pose before `SampleSeated`, evaluates the graph, applies the current editor cursor head rotation, and processes VRM output. |
| `Assets/Game/Runtime/Avatars/AvatarHumanoidIK.cs` | `ApplyHead` currently derives gameplay look direction from presentation input. It must not also apply that look to the editor preview. |

Supporting systems to reuse:

- `AvatarAnimationState` in `AvatarAnimationGraph.cs` already wraps `IdleTime` by the shared idle clip length. Grounded, stationary preview input selects idle through the existing graph.
- `AvatarPresentationSystem` owns animation evaluation. The preview does not need another animation driver.
- `AvatarTattooSurface` in `AvatarTattooPlacement.cs` bakes the visible skinned meshes and stores region-relative triangle points. Rebuild it after applying the placement pose.
- `AvatarCosmeticPresentation` attaches tattoo projectors to region bones, allowing them to follow idle movement.
- `MenuNavigation.Eligible` excludes descendants of elements with `display: none`; use the existing focus repair.
- `Assets/Game/Settings/Avatars/AvatarRegistry.asset` already references the shared animation set, and `AvatarAnimationSet.asset` already supplies `Idle`.

## Required states

| Editor state | Tattoo section | Color and placement controls | Preview | Cursor head tracking |
| --- | --- | --- | --- | --- |
| Avatars or Hats tab | Hidden, with no layout space | Hidden with section | Looping idle | Enabled |
| Tattoos, no selection | Visible | Visible and disabled | Looping idle | Enabled |
| Tattoos, selection, outside placement | Visible | Enabled unless switching avatars | Looping idle | Enabled |
| Tattoos, active placement | Visible | Retain existing interaction behavior | Fixed editor pose | Disabled |

`Remove All` stays outside the tattoo section and remains available on every tab, retaining its existing temporary disablement during avatar switching. It continues to clear both the hat and every tattoo.

## Implementation sequence

### 1. Scope the tattoo UI and centralize tab transitions

Edit `AvatarEditor.uxml` and `AvatarEditorPanel.cs`.

1. Add a named `tattoo-section` wrapper around the equipped heading, the `equipped` ScrollView, and the entire `tattoo-controls` element, including its swatches. Leave `remove-all`, Apply, and Cancel outside it.
2. Store the wrapper reference in `Initialize`. In `Refresh`, set its display to `Flex` only when `tab == "tattoos"`, otherwise `None`.
3. Keep the existing `tattoo-controls.SetEnabled(editor.Selection >= 0 && !editor.Switching)` behavior. Do not hide these controls for an empty selection on Tattoos.
4. Route all three tab callbacks through a single panel method, such as `SelectTab(string value)`. Set the destination tab before notifying or refreshing the panel.
5. When the destination is not Tattoos and placement is active, release the viewport gesture and call `editor.SetPlacement(false)`. That call already triggers `Refresh` through `Changed`; avoid a second refresh in this path. Otherwise refresh directly.
6. Do not change `Selection` in this method and do not enter placement when returning to Tattoos. Retain the existing pointer-down behavior that exits placement when interacting with panel controls; tab transitions must also enforce the exit independently of pointer events.
7. Apply section visibility before existing navigation repair. Only prefer a selected tattoo row as fallback focus while Tattoos is active, so hidden rows are not requested as focus targets.

The existing USS layout should support this wrapper without additional styling: `display: none` removes the whole section and its minimum heights from layout, and the library keeps its existing flex growth.

### 2. Separate editor context from the fixed-pose evaluation path

Edit `AvatarPresentation.cs`, `AvatarInstance.cs`, and `AvatarHumanoidIK.cs`.

1. Rename the internal, nonserialized `AvatarPresentation.EditorPose` flag to `EditorPreview`. It identifies the editor presentation for the lifetime of a binding; placement uses the existing animation feature switch to select the fixed pose.
2. In `AvatarInstance.Initialize`, create and capture the existing `HumanPoseHandler` pose whenever `host.EditorPreview` is true. Keep this before `graph.SampleSeated`, including the existing cleared muscles and arm values. A binding first opened in idle must already have its canonical fixed pose available; do not capture an animated frame on first entering placement.
3. Restrict the fixed-pose branch in `AvatarInstance.Evaluate` to an initialized editor preview with animation disabled. Continue placing the avatar, applying the saved pose, and processing VRM output in that branch. Placement will have head tracking disabled.
4. With animation enabled, let editor previews use the existing graph evaluation path, including the shared idle clip and finger layers.
5. Move the existing editor head rotation calculation into a private method in `AvatarInstance`. Apply it after graph evaluation and before `runtime.Process` when the host is an editor preview with head tracking enabled. Retain its current yaw/pitch limits and target calculation. Each evaluation must start from the graph's fresh pose so cursor rotation does not accumulate.
6. In `AvatarHumanoidIK.ApplyHead`, set Animator look-at weight to zero and return for editor previews, as well as for disabled head tracking. This prevents gameplay look-at from being applied before the editor's cursor rotation.
7. In the animated VRM processing path, use `EditorLookTarget` for an editor preview and `ik.LookTarget` for other hosts when head tracking is enabled. Keep the existing look reset in `ApplyFeatures` when tracking is disabled.

The shared graph and animation state need no new playback mode. Keep gameplay presentations on their existing evaluation and look-at paths.

### 3. Coordinate preview modes with placement surface refresh

Edit `AvatarEditorPreview.cs`.

1. Initialize the presentation with `EditorPreview = true` and `SetFeatures(true, false, true, false)`. Preserve the existing grounded, stationary input in `Show`.
2. Store the bound `AvatarInstance` in `Bound` and clear it together with the surface in `Unbound`.
3. Change `SetPlacement` so its feature switches are `SetFeatures(!value, false, !value, false)`.
4. Enter placement synchronously in this order: update the preview placement flag, disable animation and head tracking, evaluate the bound instance at zero delta to apply the fixed pose, then construct a new `AvatarTattooSurface` from that binding. Return only after the surface represents the fixed pose.
5. On leaving placement, invalidate the surface, enable animation and head tracking, and evaluate the bound instance at zero delta so the current idle phase and cursor target are restored immediately. Subsequent frames advance through the existing presentation system.
6. Replace the unconditional surface bake in `Bound` with placement-only preparation. If a binding arrives while preview placement is active, apply the fixed pose and rebuild its surface even though the placement flag has not changed. Reuse a private preparation method for this and placement entry; an unchanged-value guard in `SetPlacement` must not skip preparation for a new binding.
7. Allow `Place` and `CentralTattoo` to cast only while placement is active and a surface exists. Never use a surface baked during idle.

Preserve idempotent mode changes. Repeated requests for the current mode must not reset idle time or rebuild a placement surface unnecessarily.

### 4. Fix new-tattoo targeting order

Edit `AvatarEditorController.AddTattoo`.

The controller's `SetPlacement(true)` requires an existing selection, so it cannot be used to prepare the very first tattoo before insertion. Use the existing preview method for that synchronous preparation:

1. Keep the existing catalog, capacity, and avatar-switch guards.
2. Create the default tattoo value and call `preview.SetPlacement(true)` before `preview.CentralTattoo`.
3. If targeting fails, restore the preview to the controller's unchanged `Placement` value and return. Do not alter the draft, selection, or navigation state. This resumes idle if the editor was browsing, and preserves placement if another tattoo was already being placed.
4. If targeting succeeds, append the tattoo, select its index, call the controller's `SetPlacement(true)`, and apply the draft through the existing edit path. The preview is already in the prepared pose, so the second placement request must not bake again.

Repositioning an equipped tattoo continues through the existing Edit placement button and controller `SetPlacement(true)`. That path must finish preview pose evaluation and surface preparation before `Notify` updates the gizmo or any move input performs a cast.

All placement exits continue through the controller so input navigation, gizmos, hints, and preview mode remain synchronized. Returning to Tattoos reveals the retained selection and its ink values without restarting placement.

## Scope boundaries

Keep appearance serialization, network messages, tattoo transfer, decal projection math, camera orbit/zoom, and Apply/Cancel behavior as they are. The change concerns UI visibility and the ordering of existing pose, look, and surface operations.

## User visual acceptance

Perform these checks in Unity after implementation:

1. Open the editor with no selected tattoo. Switch among Avatars, Hats, and Tattoos. The complete tattoo section appears only on Tattoos, with no reserved space elsewhere. Its color sliders, swatches, and placement button remain visible but disabled without a selection.
2. Select an equipped tattoo and confirm its ink values and enabled controls. Switch to each other tab and back without changing the avatar. The same tattoo remains selected and placement does not start automatically.
3. Equip a hat and tattoos, then use Remove All from each tab, re-equipping between attempts. Both the hat and tattoos clear each time.
4. Watch several complete idle loops, including while navigating tabs, adjusting color sliders, and choosing swatches. The idle continues without restarting, and cursor head tracking works over the preview.
5. Add the first tattoo during idle. The avatar immediately takes the fixed editor pose, stops head tracking, and places the tattoo on visible skin. Repeat by selecting an equipped tattoo and entering Edit placement; move, rotate, and resize it from several camera angles.
6. End placement using Back and by interacting with panel controls. Idle and head tracking resume, the gizmo disappears, and the tattoo stays attached as the avatar moves.
7. Leave Tattoos during placement, including during a pointer gesture. Placement and the gesture end immediately. Return to Tattoos and confirm the selection remains accessible without placement restarting. Exercise both mouse and controller navigation where applicable.
8. Repeat placement after several idle loops, switch avatars, and close/reopen the editor. Each new placement uses a fresh fixed-pose surface, and each newly displayed avatar idles outside placement.
9. Observe a gameplay avatar outside the editor to confirm its existing animation and head-look behavior are unchanged by the shared presentation edits.
