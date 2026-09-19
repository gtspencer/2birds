# Controller polish plan

Support a polished Steam Controller experience through Steam Input gamepad emulation while retaining Unity Input Actions as the gameplay input source. Native Steam Input action integration is optional and outside this plan.

## Scope

- Publish a suitable default Steam controller layout.
- Keep gameplay prompts stable when controller input is mixed with emulated mouse aiming.
- Resolve controller glyphs correctly when multiple controllers are connected.
- Make inventory item management usable from a controller.
- Preserve existing remapping, persistence, pause, and contextual Back behavior.
- Keep input preferences local; no networking changes are needed.

## Default Steam layout

Create and publish an official controller layout through Steamworks. Configure movement, camera controls, gameplay buttons, inventory, and fixed pause input against the existing Unity action defaults.

- Use gamepad emulation for movement and gameplay buttons.
- Configure trackpad and optional gyro aiming. Consider Valve's Gamepad With High Precision Camera/Aim template, which combines gamepad input with mouse aiming.
- Provide pointer movement and a held mouse-button binding for inventory dragging if retaining pointer-based item management.
- Give additional grip buttons useful assignments through Steam's layout.
- Keep Start/Menu available for pause and remapping cancellation.
- Publish the layout as the appropriate default for the game's App ID.

The in-game remapper receives emulated keyboard, mouse, or gamepad controls. Physical grips, trackpad modes, and gyro settings remain configurable in Steam's controller layout. Document this distinction in any player-facing controller configuration guidance.

Reference: [Steam Input gamepad emulation best practices](https://partner.steamgames.com/doc/features/steam_controller/steam_input_gamepad_emulation_bestpractices).

## Stable prompts with mixed input

Update `Assets/Game/Runtime/UI/InputPresentation.cs` so trackpad or gyro input emulating a mouse does not repeatedly switch gameplay prompts between controller and keyboard/mouse.

- Continue accepting simultaneous mouse aiming and gamepad movement/buttons.
- Use Steam Input device information where available to inform prompt selection. Do not assume every mouse event can be attributed to a physical controller.
- Keep controller prompts stable during controller use with mouse aiming, while allowing deliberate keyboard/mouse use to change the prompt context.
- Preserve meaningful-input thresholds so stick drift and incidental pointer movement do not switch prompts.
- Keep the Settings device selector independent of gameplay's active-device context.
- Keep device tracking and presentation notifications centralized in `InputPresentation`; consumers should not add their own tracking loops.

Choose the smallest prompt-selection policy that handles mixed input reliably. Add a user preference only if automatic selection cannot resolve the ambiguity adequately.

## Controller identity and glyphs

Update `Assets/Game/Runtime/UI/SteamInputGlyphs.cs` and its connection to `InputPresentation` to remove the assumption that exactly one gamepad and one Steam Input controller are connected.

- Associate the active Unity gamepad with the corresponding Steam Input handle using a reliable device/XInput-slot mapping.
- Do not treat Unity's device ID or position in `Gamepad.all` as an XInput slot index.
- Resolve glyph origins through the matched Steam Input handle so prompts reflect the controller and its Steam layout.
- Refresh device associations and presentation on connection, disconnection, or relevant configuration changes.
- If an association cannot be established, retain readable binding text instead of selecting an unrelated controller's glyphs.
- Preserve session ownership and disposal of the glyph cache and subscriptions.

Valve documents `GetControllerForGamepadIndex` and `GetActionOriginFromXboxOrigin` for gamepad-emulation glyph resolution. The Unity-to-XInput association must be established before using these APIs.

Reference: [Steam Input gamepad emulation best practices](https://partner.steamgames.com/doc/features/steam_controller/steam_input_gamepad_emulation_bestpractices).

## Inventory item management

Inventory toggle, hotbar selection, and cycling already use Input Actions. Inventory item movement uses pointer dragging.

Choose the intended interaction before implementation:

- **Trackpad pointer:** Retain the existing drag interaction and provide usable pointer movement and click/hold bindings in the official Steam layout. This is the smallest change for Steam Controller support.
- **Button navigation:** Add focusable inventory slots, directional navigation, and a select-source/select-destination interaction for moving items. Show the selected source and let contextual Back cancel the pending move before closing inventory.

For button navigation, reuse the existing inventory move rules and UI navigation actions. Keep visual selection and item movement feedback in the HUD, and keep shortcut handling in `InventoryInputHandler`. Preserve ownership, equipment, pause, and developer-console restrictions.

## Optional native Steam Input integration

Consider native integration separately if Steam's configurator should expose named gameplay actions or automatically switch layouts for menus, on-foot play, vehicles, and inventory.

That work would require action definitions/manifests, action sets or layers, and an explicit integration with the game's existing input handling. It is not required for basic Steam Controller compatibility or the polish work above.

Reference: [Getting started with the Steam Input API](https://partner.steamgames.com/doc/features/steam_controller/getting_started_for_devs).

## Implementation order

1. Choose the default aiming layout and inventory interaction.
2. Implement stable prompt selection for mixed mouse/gamepad input.
3. Implement reliable controller-to-Steam Input association for glyphs.
4. Add button-based inventory item management if selected.
5. Publish the official Steam layout after hardware validation.

## Visual validation by the user

Launch a build through Steam with Steam Input enabled and use the actual target controller.

- Move while aiming with the trackpad or gyro; confirm camera behavior and stable controller prompts.
- Deliberately switch to keyboard/mouse, then back to the controller; confirm prompt switching remains responsive.
- Navigate main-menu and pause-menu Settings, switch tabs, scroll, remap buttons, and reset bindings.
- Check glyphs for gameplay, hotbar, interaction, and vehicle actions, including after changing the Steam layout.
- Connect a second controller, switch active controllers, and disconnect/reconnect them; confirm prompts match the active device or use readable fallback text.
- Open inventory and move items using the chosen interaction. For button navigation, confirm Back cancels an unfinished move before closing inventory.
- Confirm B/Circle gameplay assignments do not open pause, and that Escape/Start cancels capture without leaving Settings or resuming gameplay.
- Restart through Steam and confirm the intended default layout, saved remaps, and resets behave consistently.
