# Native Steam Input and hardware-specific artwork

## Shipping input contract

Use Unity Input System for keyboard/mouse and gamepad input. For App ID `5300780`, select Steam's built-in **Gamepad** template as the emulation baseline. It sends Xbox-style controls to existing Unity bindings and requires no native manifest or exported native layouts. Players with personal keyboard/mouse layouts must select the gamepad layout explicitly. Restart Steam to clear any manifest override from earlier development runs.

Keep `SteamInput.Init(false)` and `SteamAPI.RunCallbacks()` for automatic updates and glyph access. Known XInput controls use Steam's Xbox artwork; other recognized Unity controller families use generated artwork, with neutral/text fallbacks. An emulated gamepad's Xbox identity does not identify the physical hardware behind it.

Keep image-capable footers, hotbar prompts, multiple binding tokens, contextual hints, and keyboard/gamepad rebinding. Steam builds must not require native exports while shipping emulation. **Development - Steam** still builds and uploads; it is not a local-only build command.

## Future objective and boundary

Use native Steam Input actions to identify the actual controller and display returned action origins, including grips and trackpads. Do not add an XInput adapter, another gamepad polling backend, or a device-matching subsystem. Do not infer hardware identity from Unity device IDs, `Gamepad.all` order, a sole Steam handle, or running on Deck.

**Layouts alone are insufficient.** Implement the native bridge and presentation integration below before selecting native layouts as shipping defaults. Retain Unity input for ordinary gamepads, legacy layouts, keyboard/mouse, and local-networking mode. Keep input and artwork local; do not change FishNet messages or simulation.

Use `Steam_Controller_Plan.md` for the detailed native design and acceptance matrix. This guide defines future integration requirements; native artifacts are not prerequisites for emulation builds.

## 1. Implement native input integration

- Own a disposable native action service from `SteamLifetime`, without a new scene component. Register an action-shaped Unity device, such as `SteamActionDevice`, and associate each instance with its actual Steam handle.
- Add a dedicated `SteamInput` binding group to `Assets/InputSystem_Actions.inputactions`. Bind native controls to existing Player/UI actions. Exclude this wiring from Unity rebinding/reset; preserve keyboard/gamepad overrides.
- Cache action-set, layer, digital, and analog handles after the manifest becomes available. Diagnose invalid handles in development and leave Unity input usable. Determine native availability from valid handles and returned `bActive` data, not connection or held-button state alone.
- Switch to `SteamInput.Init(true)` only when the bridge owns explicit pumping. Run Steam Input once before each Unity Dynamic update and queue state for that update. Keep Steam callbacks for other services. Avoid additional Fixed/BeforeRender samples and stale camera deltas.
- Enumerate initial controllers and reconcile connection, disconnection, and configuration callbacks. Release input and cancel use on interruption. Remove devices and subscriptions on disposal.
- Activate Gameplay, Menu, and Vehicle on actual session, inventory, panel, console, and seating transitions. Require release before reactivation across context changes, focus loss, overlay return, and reconnects.
- Preserve simultaneous real mouse and controller input. Official layouts must not duplicate native actions through legacy outputs. Define fallback behavior for mixed native/gamepad layouts without disabling unrelated devices or guessing associations.

| Context | Native actions | Integration |
| --- | --- | --- |
| Gameplay | Move | `joystick_move`; movement and vehicle steering |
| Gameplay | LookDelta | `absolute_mouse`; separate Unity camera action with delta semantics |
| Gameplay | Sprint, Jump, Interact, Use, Drop, Inventory, Previous, Next, Pause | Existing held, press/release, buffered jump, inventory, and pause behavior |
| Gameplay, optional bindings | Hotbar1 through Hotbar8 | Expose actions; defaults may remain unassigned |
| Vehicle layer over Gameplay | Lights, Horn, ExitVehicle, Handbrake | Retain Move/LookDelta/Pause; route Handbrake to existing brake behavior |
| Menu | Navigate, Submit, Cancel, Scroll, Pause | UI Toolkit navigation, focus, scrolling, and text entry |

Use a separate native camera action, for example `Player/SteamLook`, so Unity value-action conflict resolution cannot discard simultaneous native and ordinary camera input. Do not multiply deltas by frame time, apply stick deadzones, or enable `os_mouse`. Steam can configure stick response through LookDelta. Preserve existing sensitivity settings and define the native delta conversion. If LookStick is added, scale its rate separately and combine intentionally.

## 2. Resolve actual origins and configure native bindings

- Extend `InputPresentation` to distinguish native Steam input from ordinary gamepad and keyboard/mouse. Retain the active native handle and select it only on meaningful activity.
- Extend controller classification in cursor visibility, focus repair, text fields, scrolling, inventory drag cancellation, and held-button/rebinding guards. Native camera motion must retain controller identity.
- Resolve prompts with `GetDigitalActionOrigins` or `GetAnalogActionOrigins` using the actual handle and appropriate set/layer. Map aliases explicitly, including Jump/Handbrake and the Unity camera action/LookDelta.
- Pass origins to `GetGlyphPNGForActionOrigin` and `GetStringForActionOrigin`. Preserve future numeric origins and useful labels when artwork fails. Show `Unassigned` or hide optional shortcuts when no origin exists.
- Reuse `SteamInputGlyphs`, `InputPresentation.ResolveTokens`, and `InputPrompt` for caching and image/text rendering. Render multiple origins individually; distinguish alternatives from Unity composites/chords.
- Refresh origins after configuration, context, source, reconnect, and overlay events. If callbacks leave stale origins, query visible native prompts periodically and rebuild only when origins change. Cache textures and retry failed loads on relevant events.
- Offer **Configure controller in Steam** via `SteamInput.ShowBindingPanel(activeHandle)`. Show native bindings, prevent Unity rebinding/reset for them, and retain keyboard/gamepad views. Handle panel failure with instructions; restore focus/context and release guards on return.
- Keep known Xbox Steam artwork and generated family/text fallbacks for ordinary Unity input. Do not reintroduce guessed emulation associations.

## 3. Create the manifest and official layouts

Announce new Unity assets and let Unity generate `.meta` files. Create `Assets/StreamingAssets/SteamInput/action_manifest.vdf` with Valve's `actions`, `action_layers`, `localization`, and `configurations` sections. Define Gameplay and Menu sets plus a Vehicle layer with `parent_set_name` Gameplay. Localize labels; leave configuration references empty until real exports exist.

1. Enable Steam developer mode in Big Picture settings, then **Steam Input Layout Dev Mode** in developer settings.
2. Add a development-only `SetInputActionManifestFilePath` call using the absolute StreamingAssets manifest path. If an IGA override is needed, copy action definitions to `<Steam installation>/controller_config/game_actions_5300780.vdf`, use outer key `In Game Actions`, and omit `configurations`. Overrides assist authoring; they do not distribute production layouts.
3. Launch under App ID `5300780`, open Two Birds' Controller layout, and bind every required action in every context. Preserve internal action names and avoid duplicate legacy outputs.
4. Start with Xbox controls: left stick Move/Navigate, right stick LookDelta/Scroll, A Jump/Submit, B Drop/Cancel, X Interact, Y Inventory, right trigger Use, bumpers Previous/Next, left stick click Sprint, and Menu Pause. Override overlapping Vehicle bindings with A Handbrake, B ExitVehicle, X Lights, and Y Horn.
5. Author Steam Controller and Deck layouts with trackpad LookDelta, optional gyro/grip assignments, and Menu scrolling. Use actual exported controller types; do not assume all Steam Controller revisions use the same identifier.
6. Export layouts from the configuration gear menu. On Windows run `Start-Process 'steam://dumpcontrollerconfig?appid=5300780'` and retrieve the VDF from Documents. On Deck use the devkit's **Get Controller Config**, or `xdg-open 'steam://dumpcontrollerconfig?appid=5300780'` in desktop mode and retrieve the VDF from `/tmp`.
7. Copy the real exported `controller_mappings` files to the manifest directory. Do not fabricate binding syntax, Workshop IDs, or controller types. Register each export under its matching type in `configurations`.

Suggested names for the corresponding hardware:

| Export | Controller type |
| --- | --- |
| `steam_controller.vdf` | `controller_steamcontroller_gordon` for matching Steam Controller hardware |
| `steam_deck.vdf` | `controller_neptune` |
| `xbox_controller.vdf` | `controller_xboxone` |

Example configuration block, after those exports exist:

```text
"configurations"
{
    "controller_steamcontroller_gordon"
    {
        "0" { "path" "steam_controller.vdf" }
    }
    "controller_neptune"
    {
        "0" { "path" "steam_deck.vdf" }
    }
    "controller_xboxone"
    {
        "0" { "path" "xbox_controller.vdf" }
    }
}
```

Paths are relative to the manifest. These filenames are a project convention, not a universal Steam requirement. Steam can convert configurations between many types; verify converted layouts and device-specific behavior. Add explicit exports where needed and keep legacy fallback for types without an accepted native layout.

## 4. Package and configure Steamworks

Use **Two Birds / Build / Development** for a local Windows build. StreamingAssets packages the manifest and exports. The existing executable name gives this install-relative path:

`TwoBirds_Data/StreamingAssets/SteamInput/action_manifest.vdf`

For App ID `5300780`, select **Custom Configuration (Bundled with game)** in Steamworks Input settings and enter that path. Configure supported controller defaults. Depot `5300781` includes StreamingAssets recursively.

When native input becomes the shipping default, enforce the manifest's actual referenced files and required supported layouts during packaging, rather than an unconditional list of three filenames. Publish settings and upload only with explicit authorization. **Development - Steam** builds and uploads.

Remove authoring IGA overrides and restart Steam before checking depot-delivered behavior. Explicitly select/reset to native official layouts on devices with personal layouts; do not overwrite personal configurations from game code.

## User acceptance before switching defaults

Run the manual matrix in `Steam_Controller_Plan.md` against the installed Steam game, including:

- Steam Controller, Deck through Proton, Xbox native, direct Unity gamepads, keyboard/mouse, and local-networking mode.
- Two connected controllers: artwork follows the actual active source, including reconnects, with no duplicate actions or idle devices stealing prompts.
- Interact remapped to a grip and two alternative origins: visible prompts reflect returned origins after leaving Steam's binding panel.
- Legible menu, settings, inventory, hotbar, interaction, and driving prompts; hidden unassigned shortcuts and readable fallback labels.
- Trackpad/gyro motion retains controller identity and stops immediately; real mouse input combines with controller movement.
- Correct use/release, buffered jump, driving, focus/overlay behavior, held-button transitions, navigation/scrolling, and ordinary Unity remapping.
- Independent local input/artwork on host and joining clients without networking changes.

References: [Valve emulation guidance](https://partner.steamgames.com/doc/features/steam_controller/steam_input_gamepad_emulation_bestpractices), [action manifests and exports](https://partner.steamgames.com/doc/features/steam_controller/action_manifest_file), [IGA schema and layers](https://partner.steamgames.com/doc/features/steam_controller/iga_file), [Steam Input API](https://partner.steamgames.com/doc/api/ISteamInput).
