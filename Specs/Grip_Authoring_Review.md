# Grip Authoring Review

## 1. [P2] Preserve authoring cursor state when the session enables gameplay

**Location:** `Assets/Game/Runtime/Authoring/GripAuthoringScene.cs:71` and `:84`.

**Problem:** When this scene subscribes during loading, `SessionController.TryEnterGame()` calls `SetPhase(InGame)`, which synchronously invokes `SessionChanged()`. The scene sets `AuthoringFocus = true` and unlocks the cursor. Immediately afterward, `TryEnterGame()` calls `RefreshGameplay()`, and `PlayerInputReader.SetGameplay(true)` relocks the cursor without clearing `AuthoringFocus`. The same disagreement can recur when closing the pause panel.

**Why it matters:** The authoring scene can open with an invisible, locked cursor while gameplay input is suppressed. The user cannot immediately operate the controls; toggling F2 twice is needed to restore editing.

**Recommended fix:** Make the cursor's gameplay state account for authoring focus in the common input-presentation path, including session and pause transitions, instead of setting it independently in `SetEditing()`.

## 2. [P2] Report unreachable slingshot contacts even when the pulling hand disengages

**Location:** `Assets/Game/Runtime/Player/HeldItemPresentationState.cs:515`.

**Problem:** Both unreachable flags require `impossible`, which is restricted to `pose.Heavy`. Slingshots therefore cannot report an unreachable hand. In addition, `HasLeft` becomes false when `SlingshotPresentation.Evaluate()` sets `LeftWeight` to zero because the requested pulling contact is outside arm reach.

**Why it matters:** Moving `PullingPalmContact` or the draw offset beyond reach hides the left-hand error precisely when it is needed. The panel can show "Contact" while the pulling hand never reaches the pouch, misleading the author into saving an unusable grip.

**Recommended fix:** Track requested slingshot contacts independently of their applied IK weights. Evaluate reach for each requested hand and retain the left-hand readout while a slingshot charge requests that contact, including when its applied weight is zero.

## 3. [P2] Retain the requested heavy-item pose through reach projection

**Location:** `Assets/Game/Runtime/Player/HeldItemPresentationState.cs:740` and `Assets/Game/Runtime/Items/HeldItemPose.cs:219`.

**Problem:** The heavy branch in `Blend()` projects the item into reach and reconstructs it with `FromItem()`, replacing the original requested contacts with the projected contacts. The heavy-pose constructor also always sets `ReachLimited = false`. Consequently, a reachable pair of contacts translated far outside arm reach is silently pulled back into reach and loses its diagnostic evidence. This happens even at the settled hold pose, where `Blend()` still runs with `t = 1`.

**Why it matters:** The heavy-item readout reports "Contact" and near-zero error for hold positions that the avatar cannot actually reach as authored. The spatial handle and displayed diagnostic disagree.

**Recommended fix:** Preserve the unprojected requested palms through the heavy hold/blend path and mark reach limiting when projection changes the root. Continue using the projected pose for presentation.

## 4. [P2] Use the first-person slingshot frame for its draw-offset handle

**Location:** `Assets/Game/Runtime/Authoring/GripAuthoringScene.cs:231`.

**Problem:** `TryHandle()` selects the first-person state for `FirstPersonChargePose.PullingHandDrawOffset`, but this branch always constructs the handle basis from `observer.State.Slingshot.DrawCenter` and `observer.ItemRoot.rotation`.

**Why it matters:** First-person and observer slingshots have different positions and rotations, especially with separate charge overrides. Dragging the first-person draw handle edits first-person values using the observer's coordinates, so the handle does not represent the pouch being edited.

**Recommended fix:** Use the selected state's slingshot `DrawCenter` and item-root rotation for this branch, consistently with the existing first-person state selection.

## 5. [P3] Use the required instrumentation compilation boundary

**Location:** `Assets/Game/Runtime/Authoring/GripAuthoringScene.cs:1`, the other new authoring runtime files, and their guards in `PlayerInputReader`, `PlayerAvatarPresentation`, `WorldItemRegistry`, `SessionController`, and `MenuPresenter`.

**Problem:** The new developer tooling is compiled under `UNITY_EDITOR || DEVELOPMENT_BUILD`, contrary to the repository instruction to use `UNITY_INCLUDE_INSTRUMENTATION`. The build policy also decides authoring-scene inclusion solely from `BuildOptions.Development`.

**Why it matters:** Authoring availability follows a different boundary from the project's instrumentation tooling, so code and scene inclusion do not follow the requested build configuration.

**Recommended fix:** Gate the runtime tooling and its call sites with `UNITY_INCLUDE_INSTRUMENTATION`, and align scene inclusion and editor references with that boundary.
