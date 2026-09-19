# Controller polish implementation plan

## Goal and assumptions

Make the entire player journey usable with a controller: launch, main menu, create or join a lobby, enter gameplay, open inventory, pause, change settings, resume, and leave. Switching between controller and keyboard/mouse must preserve the current screen and interaction while updating selection, cursor behavior, glyphs, and prompts.

- Support Xbox/XInput, PlayStation, Nintendo-style, and generic gamepads recognized by Unity's Input System, plus controllers exposed through Steam Input. Device-specific drivers or unsupported HID layouts are a separate compatibility concern; do not assume every physical controller identifies itself correctly.
- Assume one local player per client. Multiple connected controllers can take over that player's controls; this is not a local multiplayer project.
- Keep Unity Input Actions as the gameplay and UI input source. Core navigation must work without Steam running or a published Steam layout.
- Treat pause as the local session menu and suspension of local gameplay input. Do not freeze the multiplayer simulation or other clients.
- Use directional focus navigation as the default controller interaction. A virtual mouse is unnecessary for the core flow.
- Preserve saved gameplay remaps, contextual Back, ownership restrictions, and developer-console input isolation.
- Input presentation and navigation remain local. No new network messages are needed; inventory moves reuse the existing network path.

## 1. Establish one UI navigation contract

**Files:** `Assets/InputSystem_Actions.inputactions`, `Assets/Game/Runtime/UI/MenuPresenter.cs`, `SessionOverlay.cs`, `SettingsPanel.cs`, and `ControlsRemapPanel.cs`.

Use UI Toolkit's navigation events and the project-wide UI actions. Unity supports this path without requiring a new scene EventSystem for a UI Toolkit-only interface. Keep one input delivery path so a press cannot activate twice. [Unity runtime UI input handling](https://docs.unity3d.com/6000.0/Documentation/Manual/UIE-Runtime-Event-System.html).

| Interaction | Controller behavior |
| --- | --- |
| Navigate | D-pad or left stick; immediate first step, then controlled repeat while held |
| Select / confirm | `UI/Submit`, normally the south face button; display the actual resolved control |
| Back | `UI/Cancel`, normally the east face button; consume at the innermost active interaction |
| Pause | Fixed Start/Menu/Options via `UI/Pause`; retain Escape for keyboard |
| Scroll | Moving focus reveals the selected row; right stick scrolls the active scroll region |
| Tabs | Reachable through directional navigation and Submit; no shoulder shortcut required |

- Remove right-stick bindings from `UI/Navigate` when adding controller scrolling. The same stick must not both move selection and scroll the page.
- Keep navigation, Submit, Back, and Pause available independently of gameplay remaps. Nintendo-style prompts must describe the resolved binding rather than hard-code Xbox letters or assume a printed A/B position.
- Make every enabled control reachable, including dropdowns, text fields, remap/reset buttons, and dynamically created friend rows. Hidden and disabled controls must never become the active selection.
- Use natural visual order for simple lists. Add explicit directional handling only for layouts that need it, such as binding/reset rows and inventory grids.
- At a list boundary, keep selection at the boundary rather than unexpectedly wrapping to another region.
- Keep gameplay input blocked while a menu owns input. Closing a screen must not turn its Submit or Back press into Jump, Use, Drop, or another gameplay action; preserve the existing release/suppression guards.

## 2. Make focus survive every menu and lobby transition

**Files:** `MenuPresenter.cs`, `SettingsPanel.cs`, `ControlsRemapPanel.cs`, `Assets/Game/UI/Menu.uxml`, and `Settings.uxml`.

Cache page roots and frequently used controls during binding. Share only the small focus/scroll behavior needed by multiple presenters; keep page-specific choices in their presenters.

| Screen or transition | Initial selection and restoration |
| --- | --- |
| First main menu | Solo selected without requiring a mouse click |
| Return to main menu | Restore Host, Join, or Settings according to the page being closed |
| Local host | Create Lobby; port field remains reachable and editable |
| Local join | Host IP field; port, Connect, and Back reachable |
| Steam join | First available friend lobby; Refresh when empty, with Back always reachable |
| Connection/loading | Cancel when cancellation is supported; otherwise show status without focusing a hidden or disabled control |
| Host lobby | Start Game if enabled; otherwise the next available action, including Leave Lobby |
| Joined lobby | Invite if available, otherwise Leave Lobby; host-only controls skipped |
| Connection failure or cancellation | Return to the originating host/join page with entered values retained and focus restored |
| Settings | Selected tab; return from a popup or remap to the control that opened it |

- Repair focus when a page changes, the selected control disappears/becomes disabled, or a controller takes over a menu without valid focus. Do not reassign focus on every session/status update.
- Preserve selection by friend/lobby identity when `RenderFriends()` rebuilds the list. If that row disappears, select the nearest available row, then Refresh as a fallback. An asynchronous refresh must not pull focus away from Back or another region.
- Scroll focused friend and binding rows into view. Preserve the existing binding-row focus scrolling and extend it where missing.
- Right-stick scrolling targets the active region, stops at its bounds, and keeps controller selection visible. If scrolling carries the selection out of view, move selection to the nearest visible eligible row without submitting it.
- Returning from the Steam overlay or application focus loss restores a valid selection. Ignore the dismissal press until released so it cannot immediately activate a menu item.
- Keep Back harmless at the root main menu. Start in a lobby must not silently leave the session; leaving uses Back or the explicit Leave action. Disable or hide the nonfunctional Quit placeholder so it does not appear usable.

### Controller text entry

Local hosting/joining must not force players to reach for a keyboard just to change an IP address or port.

- Add a small numeric entry panel inside the existing menu document for the current IPv4/port fields: digits, decimal point where applicable, delete, confirm, and cancel. Avoid a general-purpose keyboard for fields the game does not have.
- Submit on a field opens the entry panel. Confirm commits and restores field focus; Back discards that edit and restores focus before any page-level Back can run.
- Keep direct keyboard editing available. Device switching preserves the edit buffer, and input goes to exactly one editor.
- Show invalid endpoint feedback next to the field and retain focus for correction. Preserve the existing endpoint rules.
- Steam's keyboard can be an optional enhancement later; it is not the fallback for native gamepad support.

## 3. Give selection a clear shared appearance

**Files:** `Assets/Game/UI/Shared.uss`, `Hud.uss`, and the corresponding menu/settings/HUD elements.

- Use a brighter background plus a strong contrasting outline for the focused control. Start with these two cues; growth is optional polish, not required for clarity.
- Keep normal, hover, focused, pressed, disabled, and selected-tab states visually distinct. A selected tab and the currently focused button must remain distinguishable.
- If adding growth, use a subtle transform around 1.03 rather than changing layout dimensions. Reserve room so the control does not clip in scroll views or overlap adjacent controls; omit growth on dense binding rows.
- Apply the same focus treatment to text fields, dropdowns and their options, friend rows, remap/reset controls, entry-panel keys, and inventory slots. Do not rely on color alone; retain the outline or another shape cue.
- During controller navigation, suppress a stationary mouse cursor's competing hover emphasis. On deliberate pointer use, show mouse hover while remembering the last controller selection.
- Add concise contextual footer prompts such as Select, Back, Scroll, and Resume. Generate their binding labels/glyphs through `InputPresentation`, including capture-cancel instructions.

## 4. Make Pause and Back predictable

**Files:** `SessionOverlay.cs`, `MenuPresenter.cs`, `SettingsPanel.cs`, `ControlsRemapPanel.cs`, and `Assets/Game/Runtime/Networking/SessionController.cs`.

- Start/Menu opens pause during gameplay and focuses Resume. On the top-level pause page, Start or Back resumes; opening Settings and returning restores focus to Settings.
- Preserve the existing nested Pause behavior: from Settings, close Settings first; from inventory, close inventory first. A subsequent Pause press opens the pause menu. Present prompts that match this behavior.
- Route Back to the innermost owner: open dropdown or text entry, remap conflict, pending inventory move, inventory, Settings, then the containing menu/session action. One press performs one transition.
- During remap capture, retain fixed Escape/Start cancellation and the existing suppression rules. The east face button can be a remap candidate rather than universally canceling capture. Conflict confirmation, timeout, and cancellation restore the initiating row and scroll position.
- Replace page-level raw Cancel handling where necessary so it cannot leave a lobby or close Settings while a dropdown or editor is consuming that same press. Toolkit event propagation and raw action callbacks must not compete.
- Back during unobstructed gameplay remains available to its gameplay binding and never opens pause.
- Keep Resume, Settings, Invite, and Leave reachable; skip Invite when unavailable. During disconnect/stopping states, focus an available recovery action instead of disabled Resume.
- If the active controller disconnects during gameplay, clear its held input and open the local pause menu. Keyboard/mouse or another controller can immediately operate it. Reconnection alone neither resumes nor activates anything; disconnecting an unused controller does not interrupt play.

## 5. Switch devices without disrupting input or presentation

**Files:** `InputPresentation.cs`, `Assets/Game/Runtime/Player/PlayerInputReader.cs`, `SessionOverlay.cs`, `HudController.cs`, `ControlsHintPanel.cs`, `InteractionTooltip.cs`, and `ControlsRemapPanel.cs`.

Keep input acceptance independent of prompt selection. Gameplay must continue accepting gamepad movement with mouse aiming; a presentation change must not disable/re-enable action maps, clear valid movement, or change camera sensitivity. Retain look sensitivity based on the control actually supplying look input.

### Active-device policy

- Centralize device tracking in `InputPresentation` and notify consumers only when the presentation context, effective bindings, or relevant device identity changes.
- Treat keyboard/button presses, mouse clicks/scroll, intentional stick movement beyond the deadzone, and deliberate pointer movement as meaningful input. Ignore releases, stick drift, tiny pointer motion, and cursor warps.
- In menus, deliberate mouse movement can take over pointer presentation; a meaningful controller input restores the remembered valid selection. The first press must work without an extra activation click.
- During gameplay, retain controller prompts while gamepad activity and mouse-only aiming are mixed. Mouse motion alone must not repeatedly flip the glyph family during gyro/trackpad aiming.
- Allow deliberate keyboard input or mouse clicks/scroll to switch immediately. Allow sustained mouse-only aiming to switch after a short settling period when controller activity has ceased. Use thresholds/hysteresis within the shared tracker, not a separate tracking loop per widget.
- Treat emulated mouse events as ambiguous: they do not inherently reveal whether a physical mouse or trackpad generated them. If hardware use still cannot be resolved acceptably, add a minimal Auto / Keyboard & Mouse / Controller prompt preference. This affects presentation only.
- Connecting a controller does not steal control. The next meaningful input selects it; another connected gamepad can take over in the same way.
- Keep the Settings remapping device selector independent of active-device detection. Using a controller to browse keyboard bindings must not rebuild the page as controller bindings.

### State that must survive a switch

- Preserve the current page, selected row, scroll position, text edit, remap target, and pending inventory selection.
- Update menu footers, interaction prompts, gameplay/vehicle hints, hotbar labels, inventory instructions, and remap presentation together through the shared change event.
- Clear stale glyph images when falling back to text. Resolve effective remapped bindings rather than displaying action defaults.
- Coordinate cursor ownership across `PlayerInputReader`, `SessionOverlay`, and `HudController`: locked/hidden in gameplay, unlocked in menus, visible for pointer interaction, hidden for controller navigation. Change cursor state on context transitions rather than every frame.
- On disconnect or application focus loss, cancel held use/drag state and clear stale movement. Switching presentation between two functioning devices must not itself cancel valid gameplay actions.

## 6. Provide correct glyphs with and without Steam

**Files:** `InputPresentation.cs`, `SteamInputGlyphs.cs`, and a small bundled controller glyph set under `Assets/Game/UI`.

Resolve each prompt in this order:

1. A Steam glyph for a reliably associated active controller, when Steam Input is available.
2. A bundled glyph for the recognized native controller family and effective binding.
3. Readable control text or a neutral positional button symbol when identity is ambiguous.

- Bundle the face buttons, shoulders, triggers, sticks, D-pad, and menu controls actually used by the game for Xbox, PlayStation, and Nintendo-style families. Reuse licensed project artwork if available; do not make Steam runtime textures a requirement for offline prompts.
- Identify native devices from their Input System layout/control metadata. Generic or remapper-exposed XInput devices may hide the physical model; do not claim to identify it from a display name guess.
- Remove the single-gamepad/single-Steam-handle assumption. Use a reliable Unity-device-to-XInput-slot association before Steam handle lookup. A Unity device ID or `Gamepad.all` index is not an XInput slot.
- Refresh associations on connection, disconnection, and relevant configuration changes. If the platform cannot provide a reliable association, use the native/neutral fallback instead of borrowing another controller's glyph.
- Keep glyph caches and subscriptions owned by the session presentation service and dispose them with it.

Valve documents the XInput-index-to-Steam-handle glyph path and simultaneous mouse/gamepad support. Apply those capabilities only when the device association is known. [Steam Input gamepad emulation guidance](https://partner.steamgames.com/doc/features/steam_controller/steam_input_gamepad_emulation_bestpractices).

## 7. Finish controller inventory interaction

**Files:** `HudController.cs`, `Assets/Game/Runtime/Player/InventoryInputHandler.cs`, `Assets/Game/UI/Hud.uxml`, and `Hud.uss`.

- Make inventory slots focusable and navigate them as a grid, including empty destinations. Focus the equipped hotbar slot when appropriate, otherwise the first slot, and remember the last valid slot while the inventory remains available.
- Submit on an occupied slot selects a move source; Submit on a destination requests the existing move/swap/stack operation. Preserve the existing inventory rules.
- Show source selection separately from navigation focus and equipped-slot selection. Back cancels a pending move before closing inventory.
- Keep existing pointer dragging. Switching to controller during a drag cancels the drag without moving an item; switching to the mouse during a controller move preserves the source until a destination click or explicit cancel.
- Clear a pending move if its source becomes invalid or the inventory closes. Never automatically commit an item move when the device changes.
- Keep shortcut handling in `InventoryInputHandler` and slot presentation in the HUD. Preserve ownership, equipment, pause, and console restrictions.

## Implementation order and asset requirements

1. Establish UI actions, focus ownership, and input consumption; complete main menu, host/join, lobby, connection recovery, and controller endpoint entry.
2. Apply shared selection styling and scrolling; complete pause, Settings, dropdown, and remapping navigation.
3. Implement shared device-switching/cursor behavior and update every prompt consumer.
4. Add native glyph fallback and reliable optional Steam glyph association.
5. Complete controller inventory navigation and item moves.
6. Have the user perform the visual/hardware walkthrough below before optional Steam layout work.

Plan for edits to existing scripts, UXML/USS, and the existing input-actions asset. The numeric entry panel can be built inside the current menu UI. Bundled glyph artwork is the anticipated new Unity asset requirement; notify the user before creating/importing it. No new scene GameObjects, components, or prefabs are planned. If implementation requires one, notify the user immediately. Let Unity generate all `.meta` files and prefer Unity CLI for Unity-side operations, falling back to MCP when necessary. Do not create migration tools.

## Optional Steam layout draft; publication deferred

A local prototype is feasible before a public store page: add a build as a non-Steam shortcut and draft a personal controller layout. Valve documents non-Steam shortcuts and saved personal configurations; using these together is the proposed prototype workflow. Keep a mapping specification/export where supported, then reapply and export it for the game's own App ID later. Do not assume a shortcut configuration automatically becomes the official layout. [Non-Steam shortcuts](https://help.steampowered.com/en/faqs/view/4B8B-9697-2338-40EC), [personal controller configurations](https://partner.steamgames.com/doc/features/steam_controller/browse_configs).

Use gamepad emulation with optional trackpad/gyro mouse aiming, fixed Pause, and useful grip assignments. Do this after the core controller flow. Publishing an official default requires the game's Steamworks app configuration; a personal prototype does not provide that. Native Steam Input action manifests/action sets and official publication remain separate follow-up work. [Valve's configuration setup and publication workflow](https://partner.steamgames.com/doc/features/steam_controller/getting_started_for_devs).

## Visual validation by the user

Perform these checks after implementation using actual controllers, with a native launch and, where supported, a Steam Input launch. Include Xbox, PlayStation, Nintendo-style, and a generic gamepad, plus two controllers connected together; include USB/Bluetooth paths where available.

- Start with the mouse untouched. Navigate Solo, Host, Join, and Settings from the first menu. Confirm a clearly selected control is visible immediately.
- Create and join local lobbies with changed IP/port values entirely by controller. Cancel an edit, submit an invalid value, fail/cancel a connection, retry, and leave; confirm values and useful focus survive.
- Browse a friend list longer than the viewport; scroll to both ends, refresh it, and let a selected lobby disappear. Confirm selection remains visible and Back is always reachable.
- Enter host and joined lobbies, wait for Start to become enabled, enter gameplay, and return to the menu. Confirm state updates do not move an otherwise valid selection.
- Pause, open Settings, switch tabs, operate dropdowns, reach the last binding row, remap, resolve a conflict, cancel capture with Start, reset, and resume. Confirm each press affects only the current interaction.
- Move inventory items with D-pad/stick and Submit. Confirm source, focus, and equipped-slot visuals differ; Back cancels a move before closing. Repeat after a device switch during a move or pointer drag.
- Switch controller to keyboard/mouse and back in menus, on foot, in vehicles, during text entry, and in Settings. Confirm prompts, cursor, and selection update without camera jumps, extra actions, page resets, or lost edits.
- Combine gamepad movement with mouse/gyro aiming. Confirm readable, stable prompts; deliberately change devices and confirm prompt takeover remains responsive.
- Switch between two controller families, disconnect the active controller while moving/holding Use, reconnect, and disconnect the unused controller. Confirm correct glyphs or honest fallback text, no stuck actions, and usable local pause/recovery.
- Open and close the Steam overlay or switch application focus. Confirm the return press does not activate anything and menu focus is restored.
- Inspect focus outlines, optional growth, and scroll clipping at different window sizes. Confirm disabled controls and the selected Settings tab cannot be mistaken for the active selection.
- Restart and confirm saved gameplay remaps and per-device resets remain intact. Repeat core menus without Steam running to confirm no dependency on Steam glyphs, keyboard overlays, or layouts.
