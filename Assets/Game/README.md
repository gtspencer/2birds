# Networked scene MVP

Open `Assets/Scenes/MainMenu.unity` and press Play, or build the two enabled scenes for Windows. Multiplayer supports one host and seven guests. Steam hosts create a friends-only lobby; guests join through friends or invitations. The host starts manually, and guests can join during play. Solo uses a loopback server with an OS-assigned port and starts immediately.

Launch with `-localNetworking` for temporary development networking without Steam. Host uses UDP 7770 by default; local guests use `127.0.0.1` and the same port (an IPv4 host address is also accepted). Both paths share the same lobby and gameplay flow. Steam peers require distinct accounts/devices. See [the implementation note](../../Eight_Player_Networking_Implementation.md) for required SessionRoot transport wiring and eight Game spawn markers.

The root `steam_appid.txt` is the development App ID configuration. Windows development builds copy it beside the executable. Steam launch invitations use `+connect_lobby <id>`. Steam discovery checks the Two Birds game/protocol metadata before connecting.

Controls: WASD / left stick to move, mouse / right stick to orbit, Space / gamepad south button to jump, Escape / gamepad cancel to open the session panel. Resume restores gameplay input; Leave Session returns to the menu. Settings persists a 60, 90, or 120 FPS rendering cap with v-sync disabled; simulation remains at 60 Hz. Quit remains an unwired placeholder.

## Ownership and responsibilities

- `SessionController` owns admission, lobby roster, connection attempts, deadlines, scene transitions, readiness, cancellation, and shutdown. `SteamLifetime` owns Steam and Steam Input throughout menus and gameplay; `SteamLobby` handles discovery and invitations. `SessionAuthenticator` reserves the host identity before admitting guests. Gameplay waits for the local player and item baseline; host departure ends the session.
- `GamePlayerSpawner` responds to scene observation, waits for initial scene acknowledgement, and maintains the server's connection/slot registry. FishNet destroys owned players on disconnect.
- `PlayerInputReader` reads only the local owner's actions after a dynamic Input System update. It buffers jump edges until consumed by a tick and converts movement using camera yaw.
- `PlayerMotor` is the sole movement authority. FishNet runs its replicate/reconcile methods and physics at 60 Hz. Horizontal acceleration is bounded, vertical velocity is preserved, and scripted impulses enter through a server-only boundary. Motor mode, cooldown, pending impulse, and reset revision reconcile with the body.
- `PlayerNetworkState` holds the small persistent public descriptor used by the MVP. `PlayerPresentation` exposes a read-only snapshot and follows the smoothed graphics with a local camera. One FishNet `NetworkTickSmoother` smooths the graphics; no NetworkTransform or Rigidbody interpolation also writes the root.
- `GameSettings.asset` holds movement and timeout tuning. The prefabs hold networking, physics, and smoothing configuration.

Game implementation lives under `Assets/Game`. The only scene NetworkObject has a stable explicit ID because FishNet's throttled editor validation can skip IDs during batch scene generation.

## FishNet cart motion extension

The imported FishNet 4.7.3 runtime includes an opt-in cart extension in [NetworkTransform.Motion.cs](../FishNet/Runtime/Generated/Component/NetworkTransform/NetworkTransform.Motion.cs), with integration hooks in [NetworkTransform.cs](../FishNet/Runtime/Generated/Component/NetworkTransform/NetworkTransform.cs). It carries motion epochs, pose and velocity samples, and wheel/parking state through one motion stream. Transforms with epoch motion disabled retain the existing behavior.

When upgrading or replacing FishNet, preserve or adapt both files: the partial declaration, `GoalData.Motion`, epoch guards, interpolation-goal hook, and reset hook are required. Keep both partial declarations in the `FishNet.Runtime` assembly because the extension accesses private internals. FishNet's normal IL post-processing modifies compiled assemblies; it does not regenerate these C# source files despite their `Generated` directory.

## Asset generation and checks

The generated scenes and prefabs are saved assets; no runtime asset construction is required. `Two Birds > Validate MVP Configuration` checks endpoints and essential prefab/build configuration. The explicit `Create MVP Assets` editor command **replaces** the MVP scenes and prefabs; use it only when intentionally regenerating them. It preserves existing shared tuning assets.

With the project closed in the Editor, build using the installed Unity CLI:

```powershell
& 'C:\Program Files\Unity\Hub\Editor\6000.5.7f1\Editor\Unity.exe' `
  -batchmode -nographics -quit -projectPath . `
  -executeMethod TwoBirds.Editor.MvpAssets.Build -logFile build.log
```

This produces a development build at `Builds/Windows/TwoBirds.exe`. `TWOBIRDS_BUILD_PATH` can override the output. `MvpAssets.GenerateAndBuild` additionally regenerates assets. For a release build, use Unity's normal build UI with Development Build disabled; the automation and diagnostics are excluded.

## Validation

See `Specs/Generated/NetworkedSceneMVPResults.md` for measured results, limitations, and outstanding manual checks. `Specs/Validation/run-pair.ps1` launches two standalone processes; the Python UDP proxy supplies reproducible delay, jitter, and packet loss. The scripted routes inject movement intent at the input boundary, so they exercise prediction and authority but do not replace physical keyboard/mouse/gamepad testing.

Development-only command-line switches include `-mvpMode Solo|Host|Join`, `-mvpPort`, `-mvpIp`, `-mvpFps`, `-mvpRoute walk|collision|impulse|fall|idle`, `-mvpSeconds`, `-mvpCycles`, `-mvpDelay`, `-mvpCancelAfter`, `-mvpCancelPhase`, and `-mvpOutput`. The harness exits after its run and has a watchdog. `-mvpCapture path.png` captures rendered scene/UI textures even when the validation window is hidden; `-mvpPage host|join` submits the corresponding menu button before capture.
