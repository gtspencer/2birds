# Avatar Editor Specification

## 1. Objective

Give players one fullscreen editor for choosing an avatar, equipping a hat, and placing colored tattoos on their avatar. The same editor is available from the main menu, lobby, and an interactable station in the game world.

Players edit a local appearance draft, then apply or cancel it. Forced interruptions automatically commit the current appearance. Applied cosmetics persist in PlayerPrefs and appear consistently on other clients, including clients joining later. Arm and hand tattoos also appear on the local first-person representation.

Use the project's Unity UI Toolkit, New Input System, FishNet networking, VRM avatar presentation, and URP renderer. Tattoos use Unity DecalProjector components parented to avatar bones.

## 2. Scope

| Area | Requirement |
| --- | --- |
| Entry points | Main-menu Avatar Editor button, lobby button, and an interactable world station. |
| Layout | Libraries and editing controls on the left; a 3D avatar preview on the right. |
| Avatars | Select from the avatar library. Both existing avatars start unlocked. |
| Hats | Equip one hat at the head, using designer-authored fit settings. |
| Tattoos | Up to eight tattoos, including duplicate designs and overlaps. |
| Tattoo editing | Surface placement, rotation, proportional resizing, and one RGB ink color per tattoo. |
| Input | Complete mouse/keyboard and controller support, with active controls displayed onscreen. |
| Unlocks | Permanent local avatar and hat unlocks through one centralized API. |
| Authoring | A reusable Two Birds > Hat Setup tool with automatic icon capture. |
| Initial content | Process and validate one GentlemanHat from the supplied hat pack. Tattoo artwork is supplied separately. |

Milestone integration, achievement tracking, tattoo opacity editing, mirroring, multiple independently recolorable regions within one tattoo, tattoo reorder controls, and player-adjustable hat fit are outside this scope.

## 3. Entry points and station behavior

### 3.1 Main menu and lobby

Add an Avatar Editor button to both pages. Closing the editor restores its originating page. Back from the editor must not invoke the lobby's leave-session action.

The preview is a local presentation instance that works without a spawned network player. Main-menu and lobby edits update the saved appearance used when the player enters gameplay. Other players' lobby previews are not part of this feature.

### 3.2 World station

The station is a fixed, indestructible world object with a collider and an interaction component using the existing IInteractable path. Use the existing interaction range, targeting, tooltip, and remappable Interact action. Default bindings are E on keyboard and Y/Triangle on controller.

Interacting opens the fullscreen editor. While it is open:

- Disable the local player's movement and gameplay actions.
- Keep the world running and the player vulnerable.
- Close the editor immediately when the player takes damage or becomes downed.
- Close the editor if the player moves beyond the station's normal interaction distance, including through external movement.
- Permit multiple players to use the same station independently; there is no exclusive occupant.

Station movement and destruction are not gameplay behaviors to implement. Provide and document the component hookup for a collider-bearing world object without requiring unrelated game-scene changes.

## 4. Appearance draft and editor lifecycle

Opening the editor creates one editable draft from the player's committed appearance. Avatar, hat, tattoo, and color changes affect the preview until the draft is committed. Ordinary editing gestures do not save appearance changes or send them to other clients.

| Event | Required behavior |
| --- | --- |
| Apply | Commit the current draft, save it, update the live appearance when applicable, and close the editor. |
| Cancel | Discard the draft, restore the previously committed appearance, and close without another confirmation. |
| Back/Escape with changes | Ask for confirmation before discarding the draft and closing. |
| Back/Escape without changes | Close directly. |
| Back from tattoo placement mode | Return to the left panel while preserving the draft; do not close the editor. |
| Host starts the game | Commit the current draft before the scene transition, then close. |
| Disconnect or session termination | Commit the current draft before editor/session teardown, then close. |
| Damage or downing | Commit the current draft and close, returning control to the applicable gameplay state. |
| Moving outside station range | Commit the current draft and close. |
| Normal application quit | Save the current draft before shutdown. |
| Application focus loss, Steam overlay, or controller disconnection | Keep the editor and draft open. Resume interaction when focus returns or an available input device is used. |

Damage and other forced-close events still take effect during focus or controller interruptions. A forced commit saves the appearance currently shown, including removals and any tattoo loss caused by avatar changes. It does not create a separate recoverable draft.

Integrate the editor as its own modal context so gameplay input blocking does not also display the pause menu. Restore the appropriate input context on exit and prevent held UI inputs from becoming unintended gameplay actions.

Commit at explicit lifecycle events. Generic UI disable/destruction must not implicitly commit a canceled draft. Persist before scene replacement or connection teardown; an interrupted connection must not prevent local saving. Unchanged appearance must not generate redundant appearance broadcasts.

## 5. Layout, preview, and controls

### 5.1 Layout

The left side provides avatar, tattoo, and hat libraries and their applicable controls. In tattoo editing, include an equipped-tattoo list with a selectable row and an X removal control for each instance. Duplicate designs remain independently selectable and removable.

Provide Apply, Cancel, Remove All, and Reset View controls. Remove All removes the equipped hat and every tattoo from the draft while preserving the selected avatar. All controls, including individual tattoo removal and color adjustment, are usable with a controller.

Display input hints for the current device and mode using the existing input-presentation infrastructure. Preview rotation and zoom controls remain visible. Labels and prompts follow remapped controls and device changes.

### 5.2 Preview behavior

- Show the draft's complete avatar, hat, and tattoos.
- Use a stationary pose with the arms slightly spread to expose placement surfaces.
- Allow full horizontal rotation and limited vertical rotation.
- Focus zoom on the selected tattoo when one is selected.
- Reset View restores whole-avatar framing.
- Use the existing head-look behavior to make the face follow the mouse outside tattoo editing.
- Disable pointer-driven head look during tattoo editing so the placement surface remains steady.

The preview must be separate from gameplay simulation and usable in all three entry contexts. Preview-only camera movement and gaze are not networked.

### 5.3 Input mapping

| Action | Mouse/keyboard | Controller |
| --- | --- | --- |
| Navigate the left panel | Click controls | Left stick or D-pad |
| Select a library item or equipped tattoo | Click item or row | Submit on the focused item or row |
| Rotate the avatar | Right-drag the preview; left-drag preview background also rotates | Right stick |
| Zoom | Mouse wheel over the preview | Right/left triggers for zoom in/out |
| Enter tattoo placement mode | Select the tattoo and use its editing controls | Select Edit placement |
| Move the selected tattoo | Drag its movement handle across the avatar surface | Left stick in placement mode |
| Rotate the selected tattoo | Drag its rotation ring | Bumpers in placement mode |
| Resize the selected tattoo | Drag its resize handle | D-pad up/down in placement mode |
| Leave placement mode | Return to the panel controls or use Back | B/Circle, retaining draft edits |
| Change tattoo color | RGB sliders and preset swatches in the left panel | Navigate and adjust the same panel controls |

Keep preview rotation and zoom available during tattoo placement. Mouse interaction with a gizmo must not also rotate the preview. In placement mode, controller movement and resize inputs must not also navigate the library. Right-stick preview rotation must take precedence over the existing UI scrolling binding.

## 6. Avatar selection and appearance transfer

Use the existing avatar identity, registry, settings, and presentation binding. The existing 138 Chill and 175 Genesis Gerbil avatars are unlocked by default.

Selecting an unlocked avatar replaces the draft's preview model and attempts to retain the equipped hat and tattoos. Reattach the hat to the new head and use the new avatar's designer fit settings.

For each tattoo:

1. Find the corresponding body region or suitable attachment on the new avatar.
2. Preserve relative placement and relative size within that region, rather than preserving whole-avatar coordinates or absolute world size.
3. Preserve the tattoo design, ink color, surface-relative rotation, and position in the overlap order.
4. Silently remove the tattoo if a suitable transfer cannot be made.

Use anatomical correspondences where available. Placement on an avatar-specific body part remains subject to the same transfer rule: retain it where a suitable counterpart exists, otherwise remove it. Transfer is best effort; different avatar proportions must not be treated as identical merely because overall heights are similar.

Maintain one evolving draft. Returning to an earlier avatar does not recover tattoos removed during a previous transfer. Cancel restores the entire committed appearance that existed before opening the editor.

## 7. Hats and fit settings

Attach the equipped hat to the avatar's head bone through the existing presentation binding. The hat follows head animation and is rebuilt or reattached when the avatar representation changes.

Support an optional per-avatar hat position offset and a per-hat position offset. Expose fit/scale settings to designers through Unity authoring so hats can fit different head proportions. Resolve these settings consistently in the preview and gameplay presentations.

Players choose the hat but cannot move, rotate, or resize it. Hat fit is derived locally from shared content settings; only the selected hat ID is part of the player's saved/networked appearance. Hats belong to the full-avatar presentation, not the local first-person arms representation.

## 8. Tattoos

### 8.1 Library and equipped instances

Tattoos come from a predefined library of allowed artwork. All initial tattoos are available; tattoo unlocking is not part of this scope.

Adding a tattoo creates a selected instance at the center of the avatar's body and activates its gizmo. Enforce a maximum of eight equipped instances. Allow multiple copies of the same design and allow overlap.

Each instance supports surface movement, rotation, proportional resizing, and one RGB ink color. Preserve the artwork's alpha/transparency when changing color. Artwork supplies the tattoo's shape and detail; players do not adjust opacity.

Selecting an equipped-list row selects that instance for editing. Its X removes only that instance from the draft. Newer instances draw on top of older instances. Moving, recoloring, or resizing an existing tattoo does not reorder it.

### 8.2 Placement and attachment

Permit placement across the avatar's body, including head and limbs. Placement targets the avatar's surface, excluding equipped hats and scene geometry.

Resolve a bone attachment and surface-relative pose for each tattoo. Store the attachment and placement information needed to reconstruct the projector and transfer it between compatible body regions. Parent the runtime projector to the resolved bone so ordinary skeletal animation carries it without per-frame transform-copying calls.

Region-relative coordinates and size must support the agreed transfer behavior. A world-space position alone is insufficient to reconstruct a tattoo consistently across representations or differently proportioned avatars.

### 8.3 Unity decal rendering

Use the URP Decal Renderer Feature configured explicitly for **Screen Space** on the PC renderer. Use Unity DecalProjector components and keep rendering-layer filtering disabled for this approach. Add a decal shader graph/material supporting the selected ink color while retaining the artwork's alpha. Existing avatar receiving materials remain unchanged.

Give equipped tattoo instances deterministic drawing priorities matching their ordered appearance data. Per-instance color and drawing order must not mutate shared source materials or another player's tattoos.

Use shallow projection volumes and angle fading to reduce projection onto unintended surfaces. The following standard-projector limitations are accepted:

- A projector follows its attached bone as a rigid volume. Skin influenced by neighboring bones can slide through that volume, causing distortion, movement, or partial disappearance near joints.
- Other limbs, hats, players, or world surfaces can receive the projection if they enter its volume.
- Disallowing placement on hats does not guarantee that projection spill can never touch a hat.

Do not add skin-deforming decal geometry or strict receiver-isolation shader changes to eliminate these accepted limitations. Unity documents the relevant [Screen Space technique](https://docs.unity3d.com/6000.0/Documentation/Manual/urp/renderer-feature-decal-reference.html) and [projector volume controls](https://docs.unity3d.com/6000.0/Documentation/Manual/urp/renderer-feature-decal-projector-reference.html).

### 8.4 First-person representation

Applied tattoos on visible arms and hands must also appear on LocalFirstPersonHands. Resolve the same saved tattoo appearance against the corresponding bones of that separate representation. Recreate or rebind those projectors when its avatar instance changes.

The local first-person and full-avatar representations share tattoo identity, attachment intent, placement, color, size, and ordering. Do not require players to edit a second set of first-person tattoos.

## 9. Libraries and permanent unlocks

Avatar and hat entries have stable IDs, library icons, display names, and a designer-configured unlocked-by-default setting. Both existing avatars are default-unlocked. Make the single processed example hat available for use and validation.

A locked entry displays a silhouette version of its icon, a lock overlay, and the label **Locked**. Conceal its actual name and unlock requirement. Selecting a locked entry must not equip it or change the preview. The transparent library icon can supply the silhouette; separate silhouette artwork is unnecessary.

Provide one centralized unlock service/API with explicit ways to unlock an avatar ID or hat ID. Grants are permanent, persist in PlayerPrefs, and immediately update any open library. Repeated grants do not create duplicate state. Preserve the distinction between avatar and hat identities.

Milestone systems will call this API later. This feature does not implement milestone counters, Steam achievement integration, unlock-condition displays, or an unlock notification system.

## 10. Persistence and networking

### 10.1 Appearance data

| Data | Stored locally | Replicated as appearance |
| --- | --- | --- |
| Selected avatar ID | Yes | Yes |
| Selected hat ID or no hat | Yes | Yes |
| Ordered tattoo instances | Yes | Yes |
| Tattoo design ID | Per instance | Per instance |
| Tattoo bone/region attachment and placement | Per instance | Per instance |
| Tattoo rotation, relative size, and ink color | Per instance | Per instance |
| Permanent avatar and hat unlock grants | Yes | No |
| Derived hat fit transforms and preview state | No player appearance data required | No |

Use PlayerPrefs for appearance and unlock persistence, consistent with the project's local preferences. Keep IDs stable across library reordering and repeated content processing. Preserve tattoo list order so overlap order is reconstructible, including when multiple instances use the same design.

### 10.2 Commit and replication behavior

Load committed appearance for the editor and apply it when the player's gameplay representation becomes available. Share one appearance state across menu, lobby, and in-game entry points.

Replicate the current appearance on game entry/initial player synchronization, and afterward only when a commit changes the appearance. Clients joining or observing an existing player must receive that player's current committed appearance. Do not continuously send projector transforms, editing gestures, preview state, or unchanged cosmetic data.

Keep messages compact using IDs and the tattoo parameters necessary for reconstruction. Hat offsets and fit come from content definitions on each client. Bone animation supplies motion after attachment; it does not require additional cosmetic messages.

Apply an owner's committed appearance responsively on that client and propagate the same appearance through FishNet. Trust the owner's choices; remote rendering must not depend on whether the observing player has personally unlocked the same cosmetics.

Treat avatar, hat, and tattoo selections as one coherent appearance when rebinding presentation. Respect representation lifecycle changes so cosmetics attach to the applicable avatar instance instead of stale bones.

## 11. Content authoring

### 11.1 Reusable Hat Setup

Provide **Two Birds > Hat Setup** as a reusable Unity editor workflow. Use the existing icon-capture functionality used by Item Setup, including IconCaptureWindow.CaptureAndSave, for library icon generation.

The workflow must let a designer:

1. Select a source hat prefab and provide its library name and initial availability.
2. Author the hat's placement/fit settings.
3. Create or update the hat definition and catalog registration with a stable ID.
4. Prepare a visual prefab/material copy when needed for the project's URP renderer.
5. Automatically capture and assign a transparent library icon framed around the hat.
6. Reprocess the hat without changing its identity or losing authored settings.

Generated asset and icon naming must distinguish registered hats. Keep source-pack content intact when generated copies or compatible materials are needed. Hat authoring produces cosmetic presentation assets; it does not require inventory, pickup, throwable, or other gameplay behavior.

Expose per-avatar fit settings with the existing avatar authoring data and preserve them when reprocessing an avatar.

### 11.2 Initial hat and documentation

Use `Assets/_3rdParty/Zekirak/Lowpoly Hat Pack/Prefabs/GentlemanHat.prefab` for exactly one processed example. Author and validate it through the reusable workflow, including its captured icon, rendered material, head attachment, and designer fit on both existing avatars.

Provide concise instructions for adding another hat, regenerating its icon, configuring default availability, and adjusting its fit. Do not process the rest of the pack as part of the initial example.

### 11.3 Tattoo content

The user supplies tattoo artwork. Each registered design needs a stable tattoo ID, library name/icon, and artwork reference compatible with single-ink recoloring and preserved transparency. Provide a straightforward catalog authoring path and document how supplied artwork is added. A separate tattoo-processing editor tool is not required.

### 11.4 Required assets and setup

The feature requires the shared editor UI, preview presentation/camera setup, hat and tattoo catalogs, tattoo decal shader/material assets, the configured PC Decal Renderer Feature, and the station interaction component. The sample hat requires its definition and icon, plus a processed visual prefab/material if necessary.

Create Unity assets through normal Unity workflows and let Unity generate their .meta files. Document the station's world-object hookup and any necessary asset assignments.

## 12. Integration boundaries

Use AvatarPresentation and its binding lifecycle for the preview and full-avatar cosmetics, PlayerAvatarPresentation for gameplay appearance integration, and LocalFirstPersonHands for arm/hand tattoo presentation. Use the established menu/session and input-presentation paths for entry, interruption handling, device prompts, and gameplay input blocking.

Keep appearance persistence and permanent unlock operations in shared logic accessible from every entry point. Share cosmetic reconstruction and attachment logic where it crosses preview, full-avatar, and first-person boundaries.

Cache content and bone references when representations are initialized or bound. Respond to commits, unlocks, session transitions, and representation changes through events. Continuous updates are appropriate for active preview input and gaze, but avoid repeated appearance application or reference lookup when nothing has changed.

## 13. Visual acceptance

The user must visually check the completed feature in these scenarios:

- Open the same fullscreen editor from the main menu, lobby, and world station; closing restores the correct context.
- Complete avatar, hat, tattoo, color, individual-removal, and Remove All operations with mouse/keyboard and with a controller.
- Rotate and zoom while editing tattoos, distinguish all gizmos, and confirm that active control hints match the current device and mode.
- Check the stationary editing pose, mouse-following face outside tattoo editing, selected-tattoo zoom, and Reset View.
- Apply changes, reopen the editor, restart the game, and confirm saved appearance. Cancel and confirmed discard restore the committed appearance.
- Exercise forced saving during lobby start, disconnect, damage/downing, movement beyond station range, and normal quit. Focus and controller interruptions preserve the open draft unless another event closes it.
- Have multiple players use one station independently while the world continues running.
- Inspect tattoo transparency, colors, overlap order, duplicate-instance editing, and the eight-tattoo limit.
- Inspect tattoos during joint bends and near hats, neighboring limbs, players, and walls against the accepted projector limitations.
- Compare tattoos on first-person arms/hands with their full-avatar placement.
- Transfer appearances between Chill and Genesis Gerbil, checking relative tattoo placement/size, retained hats, silent removal when transfer fails, and complete restoration through Cancel.
- Inspect GentlemanHat on both avatars during head motion and confirm its captured icon represents the processed hat.
- Check locked silhouettes, concealed names, unavailable previews, and immediate library updates after calling the centralized unlock API.
- Compare committed appearances between clients, including a client joining after another player has changed appearance.
