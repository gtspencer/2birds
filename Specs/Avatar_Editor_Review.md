# Avatar Editor Review

## 1. [P2] Support vector controls before exposing Orbit and Move for rebinding

**Location:** `Assets/Game/Runtime/Player/InputBindings.cs:56-57`; `Assets/Game/Runtime/UI/ControlsRemapPanel.cs:194-199`.

**Problem:** The new entries expose `AvatarEditor/Orbit` and `AvatarEditor/Move`, both `Vector2` actions, through a remapper that unconditionally uses `WithExpectedControlType<ButtonControl>()`. It cannot capture another stick, but it can save a face button as the replacement binding.

**Why it matters:** A player following the remapping prompt can persist an incompatible binding. `AvatarEditorPanel.TickInput()` reads these actions with `ReadValue<Vector2>()`; when the bound button drives the action, that read throws instead of rotating the preview or moving the tattoo. The saved override survives restarting the game.

**Recommended fix:** Select the expected control type from the action and binding being edited. Capture sticks for standalone vector bindings, retain button capture for button/composite-part bindings, and wait for the captured stick to return to neutral before restoring navigation.

## 2. [P2] Preserve anatomical coordinates when reconstructing first-person tattoos

**Location:** `Assets/Game/Editor/AvatarProcessor.cs:475`; `Assets/Game/Runtime/Avatars/Appearance/AvatarCosmeticPresentation.cs:50-52,72-78`.

**Problem:** First-person regions are generated after the arm mesh is cropped, producing new region centers and dimensions. Reconstruction then interprets the full-avatar tattoo's normalized position and size directly in those different bounds. For Chill's left upper arm, the full-avatar region has center X `-0.12638639` and width `0.294273`, while the first-person region has center X `-0.14473832` and width `0.25756913` (`44816e438f2e4775.asset:1487-1488,11147-11148`).

**Why it matters:** An applied upper-arm tattoo moves toward the elbow and changes size in first person despite using the same avatar and skeleton. This mismatch exists before animation and comes from treating removed geometry as a change in anatomical proportions.

**Recommended fix:** Normalize the extracted first-person surfaces using the corresponding full-avatar region frame, center, and dimensions. Use the extracted surface to determine whether the original placement is still visible. For companion representations, explicitly map the source anatomical coordinates into the companion's bone space before surface resolution.

## 3. [P2] Include Reset View in controller navigation

**Location:** `Assets/Game/Runtime/UI/AvatarEditorPanel.cs:63-65`; `Assets/Game/UI/AvatarEditor.uxml:37`.

**Problem:** Normal controller navigation is scoped to `editor-controls`, but `reset-view` is inside the sibling `preview-column`. `MenuNavigation.Move()` only considers controls inside its scope, and there is no separate controller action for Reset View.

**Why it matters:** Controller users cannot reach the required reset control after rotating or zooming the preview. Returning from tattoo placement does not help because navigation returns to the same restricted scope.

**Recommended fix:** Include both columns' buttons in the normal editor navigation scope, or place Reset View inside the existing scope. Keep the discard dialog isolated and placement navigation disabled as intended.

## 4. [P3] Reset the preview's placement mode when reopening

**Location:** `Assets/Game/Runtime/Avatars/Appearance/AvatarEditorController.cs:49-50`; `Assets/Game/Runtime/Avatars/Appearance/AvatarEditorPreview.cs:69-83,137`.

**Problem:** Opening resets the controller's `Placement` flag directly, but neither opening nor cleanup resets `AvatarEditorPreview.placement`. A forced close during tattoo placement leaves that separate flag true and `HeadLookEnabled` false. The next initial bind does not call `SetPlacement(false)` because there is no staged avatar switch.

**Why it matters:** Reopening after damage, leaving station range, or a lobby transition presents normal library mode while pointer-driven head look remains disabled. The two components disagree about the current mode until another operation explicitly changes placement mode.

**Recommended fix:** Reset preview placement explicitly during cleanup or opening, alongside the controller state, so every new editor session starts with matching mode and head-look settings.
