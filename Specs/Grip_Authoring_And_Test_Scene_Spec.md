# Grip Authoring and Test Scene Specification

## Purpose

Provide a consistent hand-grip authoring model and a dedicated gameplay scene for tuning held items across avatars, first-person hands, and third-person observer presentation.

Preserve the intended contact and finger pose of authored holds. Existing inconsistencies may be corrected; reproducing every existing result exactly is not required. Physical hand-to-item contact must remain consistent across output resolutions and aspect ratios. Screen composition may change with aspect ratio or field of view.

The Editor and development-build workflow must support reproducing and correcting presentation differences under matching conditions. Resolution must not be assumed to be the cause of an Editor/build discrepancy.

## Scope

Include all registered held-item types and their existing actions:

- Ordinary one-handed items.
- Heavy two-handed items.
- Slingshot holding, aiming, charging, and release.
- Idle holds, locomotion, equip/dequip, use, and action transitions.

Player carrying and environment hand contacts remain in their existing systems.

## Grip and pose data

### Unified item contacts

Use the existing `ItemDefinition` as the authoritative source of item grip data. Represent ordinary and heavy item contacts consistently as item-relative palm positions and rotations, supporting one or two hands as appropriate.

Convert existing palm-relative item offsets and heavy-item prefab grip transforms into this representation while preserving their intended contact poses. Retire the superseded grip-authoring fields and transform authority rather than maintaining competing sources of truth. Apply this as a direct migration; do not create a one-off migration tool.

Item contact and hold placement are separate concepts:

- **Contact** defines where a palm meets an item.
- **Hold placement** defines where the held item and hands sit relative to the body or first-person camera frame.
- **Finger pose** uses the existing animation-clip system.

First-person and third-person presentation share palm contacts and finger poses. They may use different hold, aim, and charge placement. Shared defaults should avoid unnecessary duplicated authoring.

### Defaults and overrides

Retain grouped overrides rather than introducing per-field inheritance. Expose effective inherited values and identify their source.

Enabling an item override copies the currently resolved settings group into that item's authored values. Disabling the override returns the group to shared defaults. While an override is enabled, changes to the corresponding default group do not replace the item's values.

Keep shared settings in `HeldItemSettings` and other existing settings assets appropriate to those values. Do not introduce a parallel grip-profile asset system.

### Finger poses

Expose selection and immediate preview of existing finger-pose clips in the Editor. Preserve the existing fallback to shared/avatar animation settings when an item has no assigned clip.

Finger editing remains in Unity's animation tools. Development builds display the selected clip but do not assign arbitrary clip assets. Do not add individual finger controls, a runtime clip catalog, or automatic finger fitting.

Avatar independence requires stable palm contact and a convincing finger pose across supported avatars. Exact surface contact for every finger is outside scope.

### Slingshot contacts

Include the slingshot's holding-palm contact, pulling-palm contact offsets, and hold/charge adjustments in item authoring. The pulling contact must follow the existing moving pouch anchor rather than being treated as a fixed second grip.

Preserve the existing relationship between the evaluated pulling hand, pouch, bands, loaded pebble, and departure position. Keep fork positions, band geometry, and mechanical pouch travel on the prefab, edited through its existing Inspector.

## Avatar calibration and reach

Support one authored grip across registered humanoid avatars, with limited per-avatar calibration where necessary.

Add authored position and rotation corrections for each palm to the existing `AvatarSettings`. Apply the same per-avatar corrections consistently to first-person and full-body rigs, using each rig's generated measurements as its baseline. Corrections must participate consistently in target calculation, IK, and evaluated palm reporting.

Keep corrections separate from generated measurements so avatar reprocessing preserves them. Do not introduce item-by-avatar grip variants or artificial arm-length adjustments.

Retain the existing reach behavior:

- Ordinary items remain attached to the evaluated hand as reach limiting changes placement.
- Heavy-item placement adjusts within the existing solver's constraints to preserve both contacts where possible.
- Impossible two-handed reaches retain the existing arm-clamping fallback and receive a clear authoring indication that the hands cannot reach their contacts.

Supported combinations must be corrected through grip and hold authoring. Do not stretch arms, resize items, or add a new reach solver in this scope. Arm stretching is a possible separate follow-up.

## Authoring scene

Repurpose `Assets/Scenes/AvatarPresentationDemo.unity` as the dedicated grip authoring scene. Reuse its stage, step, ramp, camera, and suitable assets. Replace automatic avatar cycling and the original hand-target showcase behavior with designer-controlled content selection.

Use `AvatarRegistry` and `ItemRegistry` as the content lists. Registered content becomes available automatically; do not scan for unregistered assets or maintain a duplicate authoring catalog.

The scene must start a local gameplay session without requiring Steam login, a lobby, or another process. Exercise the real inventory, equipment, item-action, movement, pose, reach, and clearance paths.

Provide synchronized presentations of the selected avatar, item, and action state:

- First-person hands and item through the production first-person path.
- An orbitable third-person observer preview through shared production observer presentation code.

Display both views together and allow either to expand. Looking at the owner with another camera is insufficient because owner and observer presentation differ.

The observer preview is a presentation comparison, not a second network client. Actual network delivery and timing require a separate multiplayer comparison.

Preserve the normal gameplay scene's purpose. Add only the scene routing and shared integration needed to support the dedicated authoring scene.

## Controls and live editing

Provide a custom Editor window using the project's UI Toolkit stack. Expose three distinct editing contexts:

| Context | Authored values |
| --- | --- |
| Item | Palm contacts, finger-clip selection, applicable hold/action settings, and grouped overrides |
| Avatar Calibration | Per-avatar palm corrections and relevant existing avatar hold/presentation settings |
| Shared Defaults | Existing shared hold/presentation defaults used by inherited settings |

Show which context owns each value, its units and coordinate frame, and whether it is inherited or overridden. Clearly distinguish editing a contact from editing hold placement.

Provide numeric fields for precision and Editor-only translation/rotation handles for spatial authoring. Existing live content-change mechanisms should be reused and extended as needed. Changes must refresh both previews immediately, including values previously cached when an item or rig was initialized.

Support normal gameplay input and authoring controls for:

- Selecting avatars and items.
- Equipping and dequipping the selected item.
- Movement and aim.
- Starting, holding, releasing, or cancelling the item's existing actions as applicable.

Controls must drive the existing gameplay behavior rather than a separate approximation of item actions.

Do not add authoring pause/resume, slow motion, single-step, paused-pose editing, recording, or timeline scrubbing. Coordinating the separate gameplay, networking, physics, and presentation clocks is outside scope.

## Camera comparison

Initialize previews from gameplay camera settings. Provide selectable resolution/aspect presets and a field-of-view control, with the actual preview viewport dimensions and FOV visible.

Allow matching conditions between Editor and development build. Camera comparison controls affect the preview and must not alter saved grip data.

Changing output dimensions or FOV may change framing and apparent size. It must not require resolution-specific contact offsets or detach palms from their authored item contacts.

## Drafts and Editor persistence

Edits operate on temporary drafts. Source assets remain unchanged until an explicit Save.

- **Save** writes the selected editing context to its source asset and retains the values after Play mode ends.
- **Revert** discards changes in the selected context since its last Save, or returns it to its source values if it has not been saved during the session.
- Switching items, avatars, or contexts preserves outstanding drafts.
- Leaving Play mode preserves drafts while the authoring window remains open.
- The window clearly identifies unsaved changes and prompts about outstanding drafts when closing.

Saving one context must not silently save other drafts. Inherited values remain inherited unless the relevant override has been explicitly enabled.

Live previews must use the drafts consistently in both views. Development-build edits need an explicit refresh path; they must not depend on Editor-only `OnValidate` callbacks.

## Development builds

### Access and editing

Include a main-menu link to the authoring scene in development builds. Provide the content selection, gameplay controls, dual previews, camera comparison controls, and numeric editing for all three contexts at runtime.

Draggable authoring handles and finger-clip assignment remain Editor features.

### Save files

Development-build Save exports the selected context as formatted, human-readable JSON for manual copying into the Editor. It does not modify the packaged assets.

Each export must include:

- The editing context and item/avatar name and ID where applicable.
- Authored field names matching the Editor controls.
- Values in matching units, including position components and rotation angles suitable for direct entry.
- Applicable override switches.

Export authored settings rather than transient world transforms or reach-corrected pose results.

Write one file per item, avatar, or shared-default context under an authoring folder in `Application.persistentDataPath`. Repeated Save updates that context's file. Display the save location and provide an **Open Folder** control.

Files are exports only. Each new development-build authoring session starts from packaged settings; saved files remain available for manual copying. Do not add automatic loading, an import tool, or cross-process live-edit synchronization.

### Release exclusion

Development builds include the authoring scene and main-menu entry. Release builds must exclude the scene and entry across all build paths, including the project's custom build commands and ordinary Unity builds.

Use development-build status for this distinction rather than assuming another diagnostics or instrumentation flag means the same thing. Keep authoring-only assets from being pulled into release builds through production asset references.

The unified grip model, avatar calibration, and production presentation improvements remain available in release gameplay.

## Shared integration

Reuse the existing pose, IK, finger, registry, inventory, and equipment systems where they fit the agreed model. Relevant shared components include `HeldItemPoseCalculation`, `AvatarHandIK`, `AvatarHandTargets`, and `AvatarFingerLayers`.

Where Editor authoring, development-build editing, or observer preview crosses an existing boundary, place common logic in a shared implementation. Do not duplicate gameplay pose math or action behavior in the authoring tool. Replace obsolete grip representations rather than preserving them solely for compatibility.

Refresh authoring changes through events and explicit cache invalidation or rebinds. Cache stable references rather than repeatedly resolving them during updates.

## Manual acceptance criteria

Use the registered avatars **138 Chill** and **175 Genesis Gerbil**. Begin detailed comparison with **Rock**, **Boulder**, and **Slingshot**, then cover all registered items: Rock, Mushroom Amanita, Basketball, Small health potion, Medium health potion, Large health potion, Bouncy potion, Slingshot, and Boulder.

The designer must visually confirm:

1. Intended hand contact and convincing finger poses across both avatars in first-person and observer views, including during locomotion and item actions.
2. Correct ordinary-item attachment, heavy-item two-hand contact within reachable poses, and clear identification of unreachable contacts.
3. Slingshot holding, pulling-hand contact, pouch/band behavior, and release throughout its existing action sequence.
4. Immediate numeric and handle feedback in both previews, including edits to contacts that were previously cached.
5. Consistent contact under different viewport dimensions and FOV, with matching Editor/build conditions used to identify and resolve discrepancies.
6. Observer-preview agreement with an actual remote client's presentation, with multiplayer timing assessed separately.

The designer must also confirm grouped inheritance/override behavior, per-context Save/Revert, draft retention across selection changes and leaving Play mode, saved calibration surviving avatar reprocessing, readable development-build exports, and the development-only menu entry. Release build contents must exclude the authoring scene.
