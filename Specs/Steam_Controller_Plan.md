# Steam Controller and Steam Input implementation plan

## Objective and decision

Make Two Birds work well with Steam Controller and Steam Deck, prefer Valve's controller glyphs, and keep prompts consistent with the player's actual bindings.

Adopt the native Steam Input action API for Steam-managed controllers through the existing Steamworks.NET dependency. Retain Unity Input System support for keyboard/mouse and ordinary gamepads that are not using native Steam actions. Preserve the generated glyphs as the last image fallback.

This is an implementation specification. Complete the input integration, UI integration, configuration files, and build packaging together. Calling `SteamInput.Init` or changing glyph images alone does not complete native support.

## User context and scope

- The user launches the installed game through Steam under its own App ID, `5300780`.
- Steam Controller does control the game, but appears to emulate keyboard/mouse and causes keyboard/mouse prompts.
- An Xbox controller controls the game but displays the generated fallback artwork.
- The user explicitly wants Steam Controller and Steam Deck support and prefers Steam-provided artwork.
- The exact active Steam Controller layout is not available in the repository. Keyboard/mouse emulation is the leading explanation of the reported behavior, not a verified Steam configuration setting.
- Do not assume a particular Steam Controller hardware revision. Use Steam handles and returned action origins instead of model-specific hardware assumptions.
- The existing build pipeline targets Windows x64. Include Windows through Proton on Deck in the manual acceptance criteria. Shipping a native Linux build is outside this task.
- Input and glyph selection are local-client concerns. Do not change FishNet messages, authority, movement simulation, or prediction for this integration.

## Repository constraints

Read the applicable `AGENTS.md` before implementing. In particular:

## Current implementation map

| File or directory | Responsibility and integration implications |
| --- | --- |
| `Assets/Game/Runtime/Networking/SteamLifetime.cs` | Initializes Steam and `SteamInput.Init(false)` in `Awake`; pumps `SteamAPI.RunCallbacks()` in `Update`; shuts down both APIs. Skips Steam completely when `SessionBootstrap.LocalNetworking` is true. |
| `Assets/Game/Runtime/Networking/SessionBootstrap.cs` | Loads Unity binding overrides, enables UI actions, disables Player actions, and instantiates the session prefab. |
| `Assets/Game/Runtime/Networking/SessionController.cs` | Owns `InputBindings` and `InputPresentation`, settings, and session/panel state. Suitable existing owner for integration services. |
| `Assets/InputSystem_Actions.inputactions` | Existing Player and UI actions. Native action controls need explicit bindings here if using the recommended Unity bridge. |
| `Assets/Game/Runtime/Player/PlayerInputReader.cs` | Reads Unity actions on `InputSystem.onAfterUpdate`, Dynamic updates only. Owns movement, camera, held buttons, press/release behavior, and suppression around gameplay transitions. |
| `Assets/Game/Runtime/Player/InventoryInputHandler.cs` | Subscribes to Unity actions for inventory, previous/next item, and hotbar selection. Uses control identity to prevent overlapping UI inputs. |
| `Assets/Game/Runtime/Player/InputBindings.cs` | Enumerates keyboard/mouse and gamepad bindings for in-game rebinding and persists overrides in PlayerPrefs. |
| `Assets/Game/Runtime/UI/InputPresentation.cs` | Chooses last active Unity device, resolves prompts, tracks focus/overlay suppression, controls cursor visibility, and raises presentation changes. Currently considers only `Gamepad` to be controller input. |
| `Assets/Game/Runtime/UI/SteamInputGlyphs.cs` | Translates Unity gamepad controls through Xbox origins, associates devices with Steam handles, loads PNGs from Steam, caches textures, and disposes them. |
| `Assets/Game/UI/Resources/ControllerGlyphs/` | Generated `Xbox`, `PlayStation`, `Nintendo`, and `Generic` fallback textures. |
| `Assets/Game/Runtime/UI/ControlsHintPanel.cs` | Displays image or keycap for contextual action hints. |
| `Assets/Game/Runtime/UI/InteractionTooltip.cs` | Displays image or keycap for the targeted interaction. |
| `Assets/Game/Runtime/UI/ControlsRemapPanel.cs` | Interactive Unity rebinding, conflict checking, controller-only capture, and glyph rows. |
| `Assets/Game/Runtime/UI/MenuNavigation.cs` | UI Toolkit focus, navigation, and controller scrolling; currently gates controller behavior using `Gamepad`. |
| `Assets/Game/Runtime/UI/MenuPresenter.cs` | Front-end input, text entry, and text-only footer prompts. |
| `Assets/Game/Runtime/UI/SessionOverlay.cs` | Pause/settings input, focus, and text-only footer prompts. |
| `Assets/Game/Runtime/UI/HudController.cs` | Inventory input, contextual hints, text-only inventory footer, and hotbar shortcuts. |
| `Assets/Game/Runtime/UI/SettingsPanel.cs` | Owns controls-remapping UI and sensitivity settings. |
| `Assets/Game/UI/` | UXML/USS presentation assets to update where labels need image-capable prompt containers. |
| `Assets/Game/Editor/TwoBirdsBuildPipeline.cs` | Windows development, local-networking, and Steam build commands. The Steam build command also uploads; do not use it merely to compile. |
| `BuildScripts/Steam/app_build_5300780.vdf` | SteamPipe app build descriptor; content root is `Build/Development-Steam`. |
| `BuildScripts/Steam/depot_build_5300781.vdf` | SteamPipe depot descriptor. This is not a Steam Input action manifest. |

Before editing, search all game runtime callers of `ActiveDevice`, `is Gamepad`, `is not Gamepad`, `InputSystem.actions`, and `presentation.Label`. The listed files are starting points, not permission to leave other affected input consumers broken.

## Existing behavior to address

### Glyph priority already prefers Steam

`InputPresentation.Resolve()` resolves the Unity binding, requests Steam artwork, then tries the generated family texture, then text. Keep that priority but expand how a Steam origin is obtained.

`SteamInputGlyphs.ResolveOrigin()` currently rejects everything except a Unity `XInputController` whose interface name equals `XInput`. It then requires a Steam handle from `GetControllerForGamepadIndex`. Consequently:

- Keyboard-emulating Steam Controller events cannot reach controller glyph lookup.
- A direct Xbox controller with no Steam-managed handle immediately falls back to generated artwork, despite Steam being able to supply artwork for known Xbox origins.
- Native Steam action controllers cannot use this path; they need their action's origins and actual Steam handle.
- Native Linux gamepad interfaces cannot use the Windows-only association path.

### Controller association is guessed

`ResolveHandle()` uses the sole Steam gamepad handle for any eligible Unity gamepad, or treats `Gamepad.all` position as an XInput slot. This can select another controller's glyphs. A sole Steam handle does not prove that an independently connected Xbox controller belongs to it.

The installed Unity XInput class does not expose a public XInput user-index property. Do not implement a nonexistent API or assume `deviceId` is an XInput index. Native integration should use the handle stored on its own input device. Do not invest in a speculative Windows device-matching subsystem just to preserve the old guess.

### Several UI surfaces discard images

Interaction tooltips, hint rows, and rebinding rows support images. Menu, session-overlay, and inventory footers call the text-only `Label()` helper. Hotbar glyph elements are created hidden and are not populated with resolved action glyphs. `Resolve()` returns no image for multiple matching bindings.

### Input source and camera mode are conflated

`TrackDevice()` treats Unity mouse events as mouse input, including mouse emulation from a controller. Its delay only postpones switching prompts. `PlayerInputReader` selects camera scaling based on whether `look.activeControl.device` is a `Gamepad`.

Native trackpad/gyro camera deltas must retain controller identity while using delta semantics. Do not multiply these deltas by frame time or apply joystick deadzones to them.

### Steam pumping is already present

`SteamInput.Init(false)` with regular `SteamAPI.RunCallbacks()` is an intentional automatic-update configuration. A missing additional `RunFrame()` is not the identified cause of keyboard prompts. Change pumping only to establish the bridge's required update ordering.

## Immediate configuration remedy

This is useful before native integration is shipped and is not a substitute for the complete implementation.

1. Have the user open Two Birds' controller layout in Steam under App ID `5300780`.
2. Inspect the active bindings. For an emulation baseline, select a layout that sends gamepad buttons and left-stick movement rather than keyboard keys. Use gamepad-style camera output for the baseline; introduce mouse-style aiming separately because the current presentation logic can switch prompts.
3. Configure a suitable official legacy gamepad layout/default in Steamworks if retaining the emulation build for players.
4. Check the user's saved layout explicitly; changing an official default must not be assumed to replace a previously selected personal layout.
5. Do not hardcode Steam Controller prompts merely because a controller is connected. That would mislabel actual keyboard use and remapped controls.

The intended final default is the native action configuration described below.

## Recommended implementation architecture

### 1. Native Steam actions enter Unity through an action-shaped device

Implement a small custom Unity `InputDevice` layout backed by Steam action data, one instance per connected native Steam controller handle. An illustrative name is `SteamActionDevice`; match repository naming conventions.

Use controls named for actions, such as `move`, `lookDelta`, `lookStick`, `interact`, `use`, `jump`, `inventory`, `navigate`, and `submit`. Do not pretend the device is an Xbox controller or squeeze independent grip actions into Xbox button slots.

Add a dedicated binding group, for example `SteamInput`, to the existing action asset. Bind the custom action controls to the corresponding existing Player/UI actions. Existing keyboard/mouse and Gamepad bindings and saved overrides remain valid. Native bindings are implementation wiring and must not be exposed as editable Unity rebinding rows.

This bridge preserves existing InputAction subscriptions and UI Toolkit navigation input. It still requires updating controller classification, context handling, camera modes, and glyph resolution; adding bindings alone is insufficient.

Do not rely on Unity's internal/conditional Steam plugin as a turnkey integration. The installed package contains old Steam support behind a compile symbol, with incomplete features. Use the installed Steamworks.NET `ISteamInput` wrapper directly.

### 2. Lifetime and update ordering

- Own the bridge from existing session/platform lifetime code; do not add a scene component solely to pump input.
- Cache action/set/layer handles after Steam Input initialization and the manifest is available. Invalid handles must produce a clear development diagnostic and leave fallback inputs usable.
- Register the custom device layout before creating devices and before relying on their action bindings.
- Subscribe to Steam connection/disconnection/configuration callbacks and enumerate existing controllers during initialization. Reconcile device lifetime by Steam handle.
- Establish one Steam Input state pump before the Unity Dynamic input update that will consume the queued state. A suitable design is `SteamInput.Init(true)` plus `SteamInput.RunFrame()` in a guarded `InputSystem.onBeforeUpdate` handler, followed by action reads and queued Unity state. Continue `SteamAPI.RunCallbacks()` for other Steam services.
- Do not leave both explicit and automatic Steam Input pumping enabled accidentally. Inspect the installed wrapper signatures, not old documentation examples, when implementing this change.
- Restrict input sampling to the intended Dynamic update. Avoid sampling again for BeforeRender or Fixed updates and creating duplicate button edges.
- Queue changed state for held buttons and vectors. Delta controls must return to zero on the next update without motion; never replay the previous camera delta.
- Consume the queued state before `PlayerInputReader`'s existing `onAfterUpdate` reader. If the engine's actual queue/update ordering requires a different hook, preserve this ordering requirement.
- On disconnect, configuration change, focus loss, and shutdown, release held state and cancel use as appropriate. Remove devices and unsubscribe callbacks during disposal.
- Preserve the explicit local-networking mode's existing ability to run without Steam. It must use Unity input and generated fallback glyphs when Steam is skipped.

### 3. Controller ownership and duplicate prevention

- Use native input when the selected Steam configuration supplies active native actions. Use Unity's existing path for ordinary controllers and legacy-emulation configurations.
- Determine action availability from valid handles and returned `bActive` data, not from whether a button happens to be held. A connected controller is not proof of an active native configuration.
- Official native layouts should bind gameplay and menu actions natively, without duplicate legacy keyboard/gamepad outputs for the same action.
- A physical controller must not trigger a gameplay action twice through both native state and a mirrored emulated gamepad. If suppressing a mirrored device is needed, suppress only a reliably associated device. Never globally disable all gamepads or keyboard/mouse when native Steam input is present.
- Do not equate presentation's last-used controller with exclusive input acceptance. Players must still be able to combine real mouse input and controller movement.
- The game has a local owning player and network peers. Do not add local multiplayer assignment or transmit device handles over FishNet.

### 4. Action inventory and contexts

Use stable internal names in the manifest and localize their player-facing labels. Start with these actions and preserve the current gameplay meanings:

| Context | Native actions | Notes |
| --- | --- | --- |
| Gameplay | `Move`, `LookDelta`, optionally `LookStick` | `Move` is `joystick_move`. `LookDelta` is Steam's `absolute_mouse` mode, which returns motion deltas. `LookStick` is a rate vector if retained for stick parity. |
| Gameplay | `Sprint`, `Jump`, `Interact`, `Use`, `Drop`, `Inventory`, `Previous`, `Next`, `Pause` | `Use` needs held and release semantics. `Jump` must preserve buffered press behavior. |
| Gameplay | `Hotbar1` through `Hotbar8` | Expose for optional remapping; existing controller defaults are unassigned. Do not require eight physical default buttons. |
| Vehicle layer | `Lights`, `Horn`, `ExitVehicle`, `Handbrake` | Feed the existing vehicle behavior. `Handbrake` may map into the current Jump/handbrake reader while keeping a distinct Steam-facing action label. Preserve seated camera/movement rules. |
| Menu | `Navigate`, `Submit`, `Cancel`, `Scroll`, `Pause` | Bind to the existing UI action map so UI Toolkit continues to receive navigation events. `Pause` must still close inventory/pause surfaces as currently expected. |

Do not expose unused sample actions such as `Attack` or `Crouch` just because they exist in the Unity asset. Do not delete them as adjacent cleanup.

Use a gameplay action set and a menu action set, plus a vehicle layer where needed. Activate them on actual session, inventory, seating, and panel transitions. A Unity UI map can remain enabled while gameplay runs; that alone must not force Steam into menu context.

Define context precedence explicitly: Steam overlay/focus suppression first; interactive menus, settings, pause, and inventory next; vehicle gameplay next; ordinary gameplay last. Return to the correct context after closing a panel or leaving a seat. Apply identical context to newly connected native devices.

Transitions must clear irrelevant held actions and require release before reactivation where the current code does so. Opening inventory, closing the overlay, or leaving a vehicle while holding a button must not also use/drop an item, jump, or activate a menu button.

### 5. Camera input

Separate source identity from camera interpretation. Update `PlayerInputReader` and any settings-dependent calculation accordingly:

- Real mouse and Steam `LookDelta` are deltas; no `deltaTime` multiplier.
- Stick camera input is a rate; apply controller sensitivity and elapsed time once.
- If `LookStick` and `LookDelta` are both supported, read and scale them separately, then combine them intentionally. One Unity value action's control-conflict resolution must not silently discard one of them.
- Steam trackpad/gyro motion remains controller activity for prompts.
- Do not enable `os_mouse` for native gameplay camera input. Otherwise input can be duplicated through Unity Mouse and controller identity is lost again.
- Keep keyboard/mouse settings behavior intact. Avoid adding new sensitivity controls unless a concrete requirement emerges; document the chosen conversion for native deltas in implementation-facing code only if needed.
- Pointer-driven native menus are optional. Controller navigation, submit, cancel, scrolling, and existing text entry must work first. Do not add an OS mouse emulation path solely to avoid integrating UI navigation.

## Glyph and presentation implementation

### Resolve native actions directly

For a native controller prompt, resolve the relevant Steam action and current action set/layer, then call `GetDigitalActionOrigins` or `GetAnalogActionOrigins` using the active controller handle. Feed the returned origins to `GetGlyphPNGForActionOrigin` and `GetStringForActionOrigin`.

The native branch must run before attempting to derive a Unity Gamepad control. Keep an explicit mapping between presented game action and Steam action, including context aliases such as Jump/Handbrake and LookDelta/LookStick.

Use this image priority:

1. Steam artwork for the actual native action origin, or a reliably associated legacy-emulation origin.
2. Steam artwork for an explicitly known ordinary controller/control origin, including direct Xbox controllers without a Steam-managed handle.
3. Existing generated family glyph for a known compatible control.
4. A readable binding label or `Unassigned` when no binding exists.

Do not invent a physical binding when Steam returns no origins. A grip or trackpad action with missing artwork must retain the correct Steam-provided label rather than silently becoming an Xbox button. Unknown controller families should use a neutral fallback.

For direct Xbox controls, map known Unity control positions to explicit Xbox origins and ask Steam for their artwork while Steam is available. Do not pass a zero controller handle into a function that requires controller association. Device-specific artwork without a managed handle is a separate lookup path.

Remove the `Gamepad.all`/sole-handle association guess. For any retained emulation path, use a proven association or show known-device/generic fallbacks. Native controllers should never use the emulation association path.

### Cache textures, refresh binding origins

- Cache textures by origin plus glyph size/style if more than one size/style is used. Cache stable Steam action handles and input references.
- Refresh visible prompt origins after configuration-loaded callbacks, action-set/layer changes, active-source changes, reconnects, and return from Steam's binding panel/overlay.
- Steam recommends frequent origin queries because players can reconfigure live. If callback coverage leaves stale origins, query origins for visible prompts during the existing UI refresh cycle and rebuild only when the origin list changes. Do not reload images or reconstruct visual trees every frame.
- Do not cache a failed PNG load forever. Retry on a relevant configuration/reconnect/overlay-return event; avoid uncontrolled per-frame file retries.
- Preserve correct Steam text when artwork fails. The current generic fallback path can overwrite useful Steam labels.
- Destroy runtime-created textures on disposal. Keep resource-loaded fallback ownership separate from runtime image ownership.
- Preserve dynamically returned origins even if their numeric values exceed the SDK's known enum range. Steam supplies the corresponding image; do not reject future devices using an enum-range check.

### Represent source identity explicitly

Extend presentation state to distinguish keyboard/mouse, ordinary Unity gamepad, and native Steam controller. Keep the active Steam handle available for glyphs and binding-panel requests. Add a clear controller classification accessor and migrate relevant `is Gamepad` checks to it.

This includes prompt grouping, cursor visibility, menu focus repair, text-field controller behavior, inventory drag cancellation, rebinding cancellation, scrolling, and suppression/held-button scanning. Do not limit this change to `InputPresentation` while its consumers still require `Gamepad`.

Native meaningful button/stick/trackpad activity should select its device. Ignore idle/noise updates. Real keyboard/mouse activity must still switch presentation under the existing intended hysteresis behavior. Native camera deltas should not masquerade as a Unity Mouse device.

### Update every prompt surface

- Keep contextual interaction and control hints image-capable.
- Replace text-only footer composition in `MenuPresenter`, `SessionOverlay`, and `HudController` with image/text prompt elements.
- Use a small shared prompt renderer where it serves these repeated surfaces. Do not introduce a general UI framework.
- Resolve hotbar shortcuts from actual active-source bindings. Keep slot numbers as inventory indices if desired, but do not imply an unassigned controller action has a keyboard shortcut. Hide the binding indicator when unassigned.
- Support multiple origins/bindings by rendering their individual image/text tokens with separators. Distinguish alternative bindings from a composite/chord; do not display a chord as alternatives. Do not drop all glyphs just because there is more than one origin.
- Use Steam's glyph style that is legible against the game's existing backgrounds. Inspect the installed enum for flags rather than guessing numeric values.
- Current glyph sizes include roughly 24 px hints, 28 px remapping rows, and 32 px interactions. Preserve layout readability and allow wider grip/trackpad art without clipping.

## Rebinding policy

For a native Steam controller, show a clear `Configure controller in Steam` action that calls `SteamInput.ShowBindingPanel(activeHandle)`. Display current action bindings using Steam origins; do not allow Unity interactive rebinding or Unity reset buttons to modify the native bridge bindings.

Retain current keyboard/mouse and ordinary Gamepad rebinding, conflict handling, and saved overrides. Select the controls view based on the chosen/active source without trapping keyboard-only users in an unavailable Steam panel.

When the binding panel closes, refresh origins, restore context, restore focus, and retain release-before-reactivation guards. If the panel cannot open, show an actionable message instead of entering a nonfunctional Unity rebind session.

Steam configuration is the authority for native bindings. Do not create two editable binding layers for the same native controller actions.

## Steam configuration assets and packaging

Native integration requires an action manifest and exported default controller configurations. Proposed source location: `Assets/StreamingAssets/SteamInput/`. Announce these new assets before creating them; let Unity create `.meta` files.

1. Create a production action manifest, for example `action_manifest.vdf`, defining the actions, action sets, vehicle layer, localization, and default-configuration references.
2. Use Valve's documented manifest schema and an actual exported configuration as the basis for controller configuration files. Do not fabricate Steam-exported binding syntax or Workshop identifiers.
3. Create official native configurations for Steam Controller, Steam Deck, and an Xbox layout. Provide an appropriate default/fallback strategy for other supported controller types using Valve's current configuration system.
4. Bind every required action across gameplay/menu/vehicle contexts. Optional hotbar shortcuts may remain unassigned. Prefer useful trackpad/gyro camera input and allow grip bindings without requiring legacy keyboard emulation.
5. Use a development IGA override or `SetInputActionManifestFile` where appropriate for local authoring, following the installed SDK and Valve documentation. A development override alone does not distribute production configuration.
6. StreamingAssets places the manifest in the built player's data directory. For the existing executable name, the expected install-relative path is `TwoBirds_Data/StreamingAssets/SteamInput/action_manifest.vdf`; confirm the actual product output naming before entering the Steamworks setting.
7. Ensure SteamPipe includes the manifest and referenced configuration files. Inspect the depot mapping for exclusions. Do not confuse the SteamPipe VDF files under `BuildScripts/Steam` with input manifests.
8. In Steamworks for App ID `5300780`, set the Steam Input configuration/template to the custom configuration bundled with the game and enter the correct install-relative manifest path. Configure supported controller defaults as required by Valve's current UI.
9. The user must select/reset to the native layout on devices that still have a saved keyboard/mouse layout. Do not silently overwrite personal controller configurations from game code.
10. Do not mark the game as having the desired controller support merely because initialization succeeds; complete the user acceptance criteria first.

If Steam configuration authoring or partner-site access is unavailable, complete the local code and manifest work that does not depend on exported files, then give the user exact remaining export/setup instructions and expected destinations. Clearly identify any required configuration artifacts that are still missing. Do not claim the feature is ready to distribute without them.

Writing this plan does not authorize publishing Steamworks settings or uploading a build. Obtain explicit authorization for those external actions when executing the plan unless it has already been granted. Prepare concrete files and settings first. Do not accidentally invoke `BuildDevelopmentSteam`, which also uploads, for local validation.

## Suggested implementation order

1. Read repository instructions and the listed sources; inspect installed SDK signatures and current action consumers. Preserve unrelated user changes.
2. Announce required configuration assets and state the native-bridge design. No scene changes should be necessary.
3. Define the action manifest, source/device model, context mapping, and exported-configuration requirements.
4. Implement initialization, device lifecycle, update ordering, and native action controls/bindings.
5. Update gameplay, camera, inventory, menu navigation, focus, and release/suppression handling together. Ensure native actions reach all existing consumers.
6. Implement native origin resolution and direct-controller Steam artwork fallback. Remove association guessing and fix cache invalidation.
7. Convert all prompt surfaces and integrate the Steam binding-panel controls view.
8. Package manifest/configuration assets and prepare the exact Steamworks setting changes. Complete any user-assisted configuration exports.
9. Only run validation that the user explicitly authorizes. Otherwise hand off the manual matrix below and state any external setup that remains.

Do not spend an implementation phase fully repairing the temporary emulation architecture if native integration removes that code. The temporary gamepad layout change and the final native layout are separate configurations, not two mandatory input frameworks to build.

## Manual acceptance matrix

These are user-run checks unless the user separately authorizes the agent to perform validation. Use the installed Steam build under App ID `5300780` for Steam behavior; Editor behavior alone is insufficient.

| Scenario | Required visible and behavioral result |
| --- | --- |
| Steam Controller, official native layout | Menu navigation, submit/cancel, gameplay, use/release, inventory, and driving work. Prompts show returned Steam Controller origins. |
| Steam Deck, Windows build through Proton | Built-in controls work; Deck artwork appears; trackpads/grips can bind native actions. |
| Xbox with native Steam layout | Native actions work and Steam artwork appears. |
| Xbox through direct Unity/XInput | Existing controls and rebinding work; Steam's known Xbox artwork is preferred when Steam is available. |
| Steam unavailable or explicit local-networking build | Keyboard/mouse and supported Unity gamepads work with generated/text fallbacks and no native-input dependency failure. |
| Saved keyboard/mouse legacy Steam layout | Input remains usable; keyboard/mouse prompts are expected for synthetic keyboard events. Selecting the native layout enables native prompts and action bindings. |
| Steam Controller plus Xbox connected | Using either switches to that device's prompts; connecting an idle device does not steal presentation; no duplicated gameplay actions. |
| Real mouse plus controller movement | Both inputs remain usable. Source switching reflects actual use; native trackpad/gyro aiming retains controller prompts. |
| Trackpad/gyro camera motion | Smooth delta-based motion; no continued rotation after motion stops; no double sensitivity/time scaling. |
| Remap Interact to a grip in Steam | Returning from the binding panel immediately shows that grip's origin/label, and the action activates once. |
| Two origins for one action | Both alternatives appear legibly, without blanking the entire prompt. |
| Unassigned native action | Shows `Unassigned` where appropriate or hides an optional shortcut; never invents a button. |
| Gameplay, inventory, settings, pause, vehicle transitions | Correct action context and focus; held buttons do not trigger unrelated actions on transition. |
| Overlay, focus loss, reconnect | No stuck movement/use, accidental submit, or stale device glyphs; correct context resumes. |
| Menu, session overlay, inventory, tooltips, hints, hotbar | All intended controller prompt surfaces render glyphs with readable fallback text and no clipping. |
| Keyboard remapping and ordinary gamepad remapping | Existing saved bindings and conflict/reset behavior still work. Native Steam bindings use the Steam panel. |
| Host and joining client on separate machines | Each client uses its own local input and glyphs; gameplay/network behavior remains unchanged. |

Completion requires the full implementation, included configuration artifacts, necessary Steamworks setup, and disclosure of any unperformed user checks. Do not equate a successful compilation with controller acceptance.

## Documentation and API references

Prefer these primary sources and inspect the installed Steamworks.NET wrapper when documentation signatures differ:

- [Steam Input overview](https://partner.steamgames.com/doc/features/steam_controller): emulation versus native action integration.
- [Gamepad emulation best practices](https://partner.steamgames.com/doc/features/steam_controller/steam_input_gamepad_emulation_bestpractices): actual XInput slot association, Steam artwork, and known Xbox origins without managed handles.
- [Getting started for developers](https://partner.steamgames.com/doc/features/steam_controller/getting_started_for_devs): action sets, camera modes, configuration authoring, origin refresh, and binding panel.
- [ISteamInput reference](https://partner.steamgames.com/doc/api/ISteamInput): initialization, action data/origins, callbacks, glyphs, and controller handles.
- [Action manifest files](https://partner.steamgames.com/doc/features/steam_controller/action_manifest_file): production manifest and referenced default configurations.
- [Legacy mode bindings](https://partner.steamgames.com/doc/features/steam_controller/legacy_mode): keyboard/mouse versus gamepad output for the immediate layout remedy.
- [Action set layers](https://partner.steamgames.com/doc/features/steam_controller/action_set_layers): context overrides such as vehicle controls.
- Local `Packages/manifest.json` and resolved `Library/PackageCache/com.rlabrecque.steamworks.net@*/Runtime/autogen/isteaminput.cs`: exact installed API and callback availability. Package cache hashes are not stable paths; rediscover them instead of hardcoding one.
- Local Unity Input System custom-device, state-event, and UI documentation/source: implement the bridge using the installed version. Do not assume a public XInput slot API exists.
