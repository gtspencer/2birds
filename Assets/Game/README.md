# Networked scene MVP

Open `Assets/Scenes/MainMenu.unity` and press Play, or build the two enabled scenes for Windows. Multiplayer supports one host and seven guests. Steam hosts create a friends-only lobby; guests join through friends or invitations. The host starts manually, and guests can join during play. Solo uses a loopback server with an OS-assigned port and starts immediately.

Launch with `-localNetworking` for temporary development networking without Steam. Host uses UDP 7770 by default; local guests use `127.0.0.1` and the same port (an IPv4 host address is also accepted). Both paths share the same lobby and gameplay flow. Steam peers require distinct accounts/devices. See [the implementation note](../../Eight_Player_Networking_Implementation.md) for required SessionRoot transport wiring and eight Game spawn markers.

The root `steam_appid.txt` is the development App ID configuration. Windows development builds copy it beside the executable. Steam launch invitations use `+connect_lobby <id>`. Steam discovery checks the Two Birds game/protocol metadata before connecting.

Controls: WASD / left stick to move, mouse / right stick to orbit, Space / gamepad south button to jump, Escape / gamepad cancel to open the session panel. Resume restores gameplay input; Leave Session returns to the menu. Settings persists a 60, 90, or 120 FPS rendering cap with v-sync disabled; simulation remains at 60 Hz. Quit remains an unwired placeholder.

## AI reproduction logs

Editor Play sessions and development players record structured JSON Lines. Editor output is `<project>/Logs/AI/<UTC-start>-<run-id>/`; development-player output is `<Application.persistentDataPath>/AILogs/<UTC-start>-<run-id>/`. The development console prints the run directory at startup. Release builds omit logging calls and capture services.

Targeted investigations can temporarily wire the existing marker/screenshot service and call `AILogger` directly. Markers request a five-second state-sampling burst; screenshots capture the completed game view, including UI. PNGs live under the run's `screenshots/` directory. Follow `screenshot.saved` for the actual capture frame/time and relative image path; throttled or unavailable requests have explicit outcomes.

AI logging is available automatically in Editor Play Mode and development players; no launch flag is required. Add `-aiLogDir "C:\absolute\directory"` only to select an output root. Targeted gameplay diagnostics should call `AILogger` directly and be removed after the reproduction. Supply the complete host run folder and each relevant client run folder for multiplayer diagnosis, plus the marker number or screenshot capture ID and which peer you controlled. Keep `run.json`, retained `events-*.jsonl`, screenshots, and any `summary.json` together. See [the event catalog and streaming examples](../../Specs/AI_Logging_Events.md) for identity, sampling, and retention rules.

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

Development-only command-line switches include `-mvpMode Solo|Host|Join`, `-mvpPlayers`, `-mvpPort`, `-mvpIp`, `-mvpFps`, `-mvpRoute walk|collision|impulse|fall|idle`, `-mvpSeconds`, `-mvpCycles`, `-mvpDelay`, `-mvpCancelAfter`, `-mvpCancelPhase`, and `-mvpOutput`. Automated Host/Join runs require `-localNetworking`. The automated host calls Start once the lobby roster reaches `-mvpPlayers` (including the host; default 1, maximum 8), within the runner's 45-second connection window. Interactive hosts still start manually. The harness exits after its run and has a watchdog. `-mvpCapture path.png` captures rendered scene/UI textures even when the validation window is hidden; `-mvpPage host|join` submits the corresponding menu button before capture.

## Item motion format

Protocol `eight-player-2` uses compact velocities only in unreliable `ItemMotionBatch` messages. Launch requests and reliable lifecycle/baseline records retain their existing format. Position stays at three floats and synchronized rotation uses FishNet's four-byte quaternion.

Each sample has three packed unsigned integers (ID, revision, tick), totaling 3–15 bytes. The original serializer adds 40 bytes for position, rotation, and both velocities: 43–55 bytes per sample. The compact format adds one flag byte for omitted rotation and full-precision linear/angular fallback:

| Sample | Bytes excluding the three packed integers | Saving |
| --- | --- | --- |
| Original | 40 | — |
| Synchronized rotation, both velocities compact | 29 | 11 |
| Cosmetic rotation, linear velocity compact | 19 | 21 |

Each fallback vector adds six bytes. Each batch also contains a packed epoch (1–5 bytes) and a packed list count (one byte for the eight-item batches); broadcast and transport framing are separate. At 64 active items, 20 Hz, and seven guests, these encodings save approximately 0.79 or 1.51 Mbps of sample payload respectively when all samples fit.

Velocity components use signed 16-bit values with 0.01 m/s or rad/s steps, rounded to nearest: at most 0.005 quantization error per component, apart from floating-point arithmetic. The range is -327.68 through 327.67. Any vector outside that range uses three original floats. Rock's configured linear cap is 50 m/s and its angular cap is at least 50 rad/s; fallback preserves larger values from other definitions or physics impulses without changing simulation limits.

`ItemDefinition.SyncRotation` defaults to enabled. Disable it only for orientation-independent collision and hit volumes with a separate collider-free visual root. The existing Rock retains synchronization because its sphere center is offset from its root. Cosmetic rotation affects observer visuals; host physics, predicted thrower rotation, and reliable launch/settled orientations remain authoritative for their existing roles.

Departure drops search within four metres of the departing player's pose using cached item bounds, swept paths, destination clearance, ground support, and cleanup bounds. If no nearby position fits, the same search uses the player's spawn area. Drops start at rest. Items blocked at both locations detach from the departing inventory and remain hidden until a one-second placement retry finds space; they remain part of the reliable world baseline. The returning player receives an empty inventory.
