# Controls remapping plan

Support keyboard, mouse-button, and controller-button remapping through the existing shared `InputSystem.actions` asset. Apply binding overrides at runtime and save them to PlayerPrefs. Add Graphics and Controls tabs to the existing Settings page.

Centralize binding management and input presentation so remapping, settings labels, hotbar hints, vehicle hints, and interaction tooltips use the same binding and device information. Keep Unity Input Actions as the source of truth and let gameplay consume those actions directly.

## Scope

- Include keyboard movement: Forward, Backward, Left, and Right, with both WASD and arrow-key bindings exposed individually.
- Include Interact, Use, Drop, Inventory, Jump / Handbrake, Exit Vehicle, Lights, Horn, Previous Item, Next Item, and hotbar slots 1–8.
- Treat controller triggers, D-pad directions, and stick clicks as buttons.
- Leave analog movement, look inputs, mouse wheel, and UI navigation bindings unchanged.
- Keep Jump and Handbrake as one shared binding, matching the existing gameplay action.
- Use an explicit list of supported actions. Do not expose unused actions such as Attack, Crouch, or Sprint or remove them as part of this work.
- Expose remapping through the existing main-menu Settings page. Adding Settings access to the pause menu is separate work.
- Keep bindings local to the player installation. No FishNet messages or server-side binding storage are needed.

## Shared input layer

Use three concrete responsibilities, without introducing a custom input framework or action dispatch system:

- **Unity Input Actions:** Own action definitions, control schemes, defaults, and runtime overrides. Existing gameplay code continues reading or subscribing to these actions directly.
- **`InputBindings`:** Own loading, saving, applying, and resetting overrides. Include a small shared catalog of remappable action references, readable names, and eligible binding IDs/device groups. Reference the existing actions instead of duplicating paths, defaults, or action state. Keep UI capture state out of this helper.
- **`InputPresentation`:** Resolve an action and device context into display text and an optional glyph. Accept a specific binding when a settings row or movement entry needs one. Centralize active-device tracking and use `SteamInputGlyphs` for controller glyph resolution with a text fallback. Expose a presentation-change event for binding changes, meaningful device switches, and device connection/configuration changes.

The presentation resolver must work while gameplay actions are disabled and when a controller is disconnected. Resolve settings labels from effective bindings, rather than requiring an enabled action's active control. Settings supplies its explicitly selected device group; gameplay prompts use the shared active-device context. For actions with multiple bindings or movement composites, provide the requested binding or a readable composite description rather than picking an unrelated control.

Have the existing session lifetime own and dispose one `InputPresentation` instance, including its input-event subscriptions and glyph cache. Move the device-tracking behavior currently in `PlayerInputReader` into this helper, retaining meaningful-input thresholds so stick drift and incidental pointer changes do not continually switch prompts. Consumers cache the shared reference and subscribe/unsubscribe with their existing lifecycle. Keep `PlayerInputReader.ActiveDevice` as a forwarding property if needed by existing callers.

`ControlsRemapPanel` owns the temporary capture operation, conflict UI, cancellation, and focus restoration. It delegates committed mutations to `InputBindings` and obtains labels/glyphs from `InputPresentation`. Use the shared catalog for remapping eligibility and conflict labels; contextual prompt verbs such as Handbrake stay with the UI that knows the gameplay context.

`InventoryInputHandler` remains a separate, narrow extraction of inventory shortcuts from the HUD. It translates Input Actions into inventory operations and does not own remapping, persistence, device tracking, or glyph resolution. No generic backend interfaces or additional event bus are needed.

## File responsibilities

Use plain C# helpers owned by existing components. No new scene objects, prefab components, or ScriptableObject assets are required. Let Unity generate any script metadata.

| File | Responsibility |
| --- | --- |
| `Assets/Game/Runtime/Player/InventoryInputHandler.cs` (new) | Handle inventory toggle, hotbar selection, and previous/next item actions outside `HudController`. |
| `Assets/Game/Runtime/UI/ControlsRemapPanel.cs` (new) | Build binding rows, own capture/cancellation and conflict UI, and delegate binding changes and presentation to the shared helpers. |
| `Assets/Game/Runtime/Player/InputBindings.cs` (new) | Catalog remappable bindings and apply, reset, load, and save overrides using PlayerPrefs. |
| `Assets/Game/Runtime/UI/InputPresentation.cs` (new) | Track device context, resolve binding text and glyphs, and notify UI when input presentation changes. |
| `Assets/InputSystem_Actions.inputactions` | Add hotbar actions and fixed pause input; retain existing action and binding IDs. |
| `Assets/Game/Runtime/Networking/SessionBootstrap.cs` | Restore overrides before input is enabled for use. |
| `Assets/Game/Runtime/Networking/SessionController.cs` | Own the shared input-presentation instance for the session lifetime. |
| `Assets/Game/Runtime/Player/PlayerInputReader.cs` | Use shared device context in place of its private device tracker while retaining direct gameplay action consumption. |
| `Assets/Game/Runtime/UI/MenuPresenter.cs` | Select settings tabs and own the controls panel lifecycle. |
| `Assets/Game/UI/Menu.uxml` | Define Graphics and Controls containers and controls-page layout. |
| `Assets/Game/UI/Shared.uss` | Style settings tabs, binding rows, scrolling, and capture feedback. |
| `Assets/Game/Runtime/UI/HudController.cs` | Delegate inventory input to the new handler and update displayed hotbar binding labels. |
| `Assets/Game/Runtime/UI/InteractionTooltip.cs` and `ControlsHintPanel.cs` | Use the shared resolver and change notifications instead of independently resolving bindings and glyphs. |
| `Assets/Game/Runtime/UI/SteamInputGlyphs.cs` | Retain the existing Steam glyph implementation behind the shared presentation resolver. |
| `Assets/Game/Runtime/UI/SessionOverlay.cs` | Separate gameplay pause from contextual UI Cancel behavior. |

## Inventory and hotbar input

`HudController` currently reads Tab, controller Select, number keys, and shoulder buttons directly. Move this shortcut handling into `InventoryInputHandler`, using cached Input Actions and button callbacks instead of polling physical controls.

- Create and dispose the handler with the HUD's enable/disable lifecycle. Subscribe once and unsubscribe on disposal.
- Supply player references when the HUD binds to the local inventory. Clear them when the player is unbound; do not find components or resolve actions repeatedly in an update loop.
- Invoke a HUD callback to toggle inventory. Keep visual inventory opening, closing, cursor changes, dragging, and rendering in `HudController`.
- Move shortcut-driven slot selection and cycling into the handler.
- Preserve ownership, session phase, pause, developer-console, inventory-open, and equipment restrictions. Inventory toggle must still work while the inventory is open.
- Add button actions for hotbar slots 1–8 with the existing keyboard defaults. Provide unassigned controller binding slots that can receive overrides.
- Reuse Previous and Next for cycling, retaining their shoulder-button defaults. Remove their current keyboard defaults of `1` and `2` when connecting them to gameplay, because those keys select hotbar slots. Expose unassigned keyboard/mouse bindings for cycling.
- Keep the remaining HUD update and presentation behavior outside this extraction.

## Settings UI

Graphics contains the current frame-rate dropdown and V-sync explanation.

Controls contains:

- A Keyboard & Mouse / Controller selector.
- A scrollable list with readable action names, current-binding buttons, and individual Reset buttons.
- Separate primary and alternate movement binding buttons for the WASD and arrow-key entries.
- An Unassigned label for bindings without defaults.
- Restore Defaults for the selected device group, preserving the other group's overrides.
- A capture prompt, cancellation instructions, and conflict feedback.

Use the existing UI Toolkit focus/navigation system so the page is usable with either a mouse or controller. Keep the selected device group stable until the user changes it. Save successful changes immediately; no Apply button is needed.

## Interactive rebinding

Use `PerformInteractiveRebinding` against a specific binding on the shared runtime asset. Identify bindings by their stable IDs and device groups, rather than assuming fixed numeric indices or matching their original keys. Resolve and cache references when constructing the panel.

Intercept the candidate with `OnApplyBinding` so capture does not commit an override before conflict handling finishes. Apply accepted candidates through `InputBindings`; cancellation never reaches its mutation or save methods.

1. Wait for the input that activated the binding button to release before listening.
2. Disable the target action during capture and preserve its previous enabled state.
3. Restrict candidates to keyboard/mouse buttons or controller buttons according to the selected device group. Explicitly exclude pointer axes, scroll, and stick directions. Movement composite parts require button capture even though the parent Move action produces a vector.
4. Suppress normal UI submission, navigation, and page-back handling during capture so the captured input cannot also activate the menu.
5. Reserve Escape and controller Start/Menu for cancellation. Provide a visible cancellation route and a timeout.
6. Stage a conflicting candidate before committing it. Show the other supported actions using that control and offer Use Anyway or Cancel. Permit deliberate sharing across gameplay contexts; do not silently swap or clear another action.
7. Commit only accepted changes through `InputBindings`, save preferences, and refresh affected labels through the shared presentation notification.
8. On completion, cancellation, timeout, focus loss, page closure, or owner disable, dispose the operation and restore prior input state and focus. Cancellation retains the previous binding. Consume the finishing input so it cannot activate another UI control.

Ordinary page changes and input events drive these operations; do not add a permanent polling loop for remapping.

## Pause and Back behavior

The session overlay currently listens to `UI/Cancel`, whose bindings include B/Circle. A gameplay assignment to that button must not also pause the game.

- Add a fixed pause action bound to Escape and controller Start/Menu in the always-available UI map.
- Make the session overlay use that action to toggle pause.
- Keep general UI Cancel for contextual Back behavior, including closing inventory or an open menu, rather than opening pause during active gameplay.
- Ensure one input closes only one UI layer and does not both close inventory and open pause.
- Guard these callbacks during rebinding so cancellation never leaves Settings, resumes gameplay, or leaves a session.

## PlayerPrefs persistence

`InputBindings` owns all persistence and uses a dedicated `InputBindingOverrides` string key. Callers do not serialize bindings or write this preference themselves.

- At bootstrap, establish default overrides, then load the saved JSON with `LoadBindingOverridesFromJson` before gameplay or settings consumes the bindings. An absent preference uses defaults, including when editor play sessions reuse runtime state.
- After each committed rebind or reset, call `SaveBindingOverridesAsJson`, `PlayerPrefs.SetString`, and `PlayerPrefs.Save`.
- Reset individual bindings by removing their overrides. Reset a device group by removing only its supported binding overrides, then save the remaining configuration.
- Preserve existing action and binding IDs when editing the input asset so saved overrides continue to resolve after bindings are reordered or new actions are added.
- If saved JSON is invalid, discard that preference and restore defaults rather than preventing startup.
- Save overrides, not the entire input asset. Never write player preferences back into the source `.inputactions` file.

Unity documents this persistence flow in [Saving and loading rebinds](https://docs.unity.cn/Packages/com.unity.inputsystem%401.7/manual/ActionBindings.html#saving-and-loading-rebinds).

## Binding labels and prompts

Route settings rows, hotbar hints, inventory shortcut labels, vehicle hints, and interaction tooltips through `InputPresentation`. Use binding display strings even when a controller is disconnected, and automatically resolve controller glyphs through the existing `SteamInputGlyphs` implementation when available. Keep readable text as the fallback for missing glyphs or unassigned bindings.

Replace the independent binding/control lookup and glyph resolution in `InteractionTooltip` and `ControlsHintPanel` with the shared resolver. Subscribe them to its change notification instead of maintaining duplicate input-system subscriptions. Retain their existing positioning, visibility, contextual verbs, and other presentation behavior.

Replace hardcoded number-key hints in the hotbar and inventory grid with the corresponding slot action's resolved presentation, keeping slot identity separate from its shortcut. Cache label references and refresh binding text/glyphs on presentation changes or a displayed-action change, rather than every frame. `HudController` only connects these references and callbacks; shared resolution logic belongs in `InputPresentation`.

## Implementation order

1. Update the existing action asset, add the shared binding catalog and management helper, and connect bootstrap loading.
2. Add `InputPresentation`, connect its session lifetime, and consolidate device tracking and binding/glyph resolution from existing consumers.
3. Extract inventory/hotbar shortcut handling into `InventoryInputHandler` and connect hotbar/inventory labels to shared presentation.
4. Add the settings tabs and `ControlsRemapPanel`, including capture, conflict handling, and reset behavior through the shared helpers.
5. Separate pause and contextual Back handling.

## Visual validation by the user

- Navigate Graphics and Controls with both mouse and controller; check scrolling, focus, and readable labels.
- Remap primary and alternate keyboard movement bindings independently. Confirm movement and vehicle steering follow the new keys while analog movement and look remain unchanged.
- Remap a keyboard key, mouse button, controller trigger, D-pad direction, and stick click. Confirm moving the mouse or sticks never creates a button binding.
- Assign B/Circle to gameplay and confirm it performs the action without opening pause; check contextual Back and fixed pause controls separately.
- Check inventory toggle, slots 1–8, previous/next item, equipment use, interaction, and vehicle actions. Confirm hotbar, interaction, and vehicle prompts agree with the bindings.
- Check duplicate-binding feedback, cancellation, timeout, focus loss, and leaving the page during capture. Confirm the previous binding survives cancellation and input remains usable.
- Switch between keyboard/mouse and controller and connect/disconnect the controller. Confirm gameplay hints and tooltips update their text/glyphs consistently, settings retains its selected device group, and disconnected-controller bindings remain readable. Check that idle stick drift does not switch prompts.
- Restart the game and confirm both device groups retain their changes. Check individual reset and device-group reset, then restart again to confirm the resets persist.
