# Build Pipeline v1 Plan

## Goal

Add a small Windows development-build pipeline under the `Two Birds` Unity menu:

```text
Two Birds
└── Build
    ├── Local Networking
    ├── Development
    └── Development - Steam
```

All three entries build the enabled Windows scenes as Unity development builds into the repository-root `Build` directory. Production builds are intentionally out of scope.

`Local Networking` must enable the existing local-networking behavior for the build. The standalone `Tools/Local Networking` menu entry must be removed.

`Development - Steam` must complete the Unity build first and then upload the resulting build to Steam through SteamCMD/SteamPipe.

## Current state

- `ProjectSettings/EditorBuildSettings.asset` already contains the two enabled scenes: `Assets/Scenes/MainMenu.unity` and `Assets/Scenes/Game.unity`.
- There is no current build pipeline implementation in `Assets/Game/Editor`. The README references `TwoBirds.Editor.MvpAssets.Build`, but no `MvpAssets` class exists in the repository.
- `Assets/Game/Editor/LocalNetworkingToggle.cs` exposes `Tools/Local Networking` and stores `TwoBirds.LocalNetworking` in `EditorPrefs`.
- `Assets/Game/Runtime/Networking/SessionBootstrap.cs` reads that editor preference in the Unity Editor and also supports the `-localNetworking` command-line argument in players.
- `Assets/Game/Editor/SteamDevelopmentBuild.cs` is an `IPostprocessBuildWithReport` hook. For Windows builds it copies the root `steam_appid.txt`; for development builds it also writes `LocalNetwork.bat`.
- `steam_appid.txt` contains App ID `5300780`.
- Existing checked/untracked output is under `Builds`, but the requested output root is `/Build`.
- `.agents/build-and-release/SKILL.md` specifies SteamCMD at `C:\steamworks_sdk_165\sdk\tools\ContentBuilder\builder\steamcmd.exe`, preferably invoked as `steamcmd`.

## Proposed implementation

### 1. Add one editor build entry point

Create a focused editor script under `Assets/Game/Editor` that owns the three menu commands and the shared build routine. Use `UnityEditor.BuildPipeline.BuildPlayer` with the enabled scenes from `EditorBuildSettings.scenes`, target `StandaloneWindows64`, and `BuildOptions.Development` for every command.

Use distinct subdirectories so choosing one build does not partially overwrite another:

```text
Build/LocalNetworking/TwoBirds.exe
Build/Development/TwoBirds.exe
Build/Development-Steam/TwoBirds.exe
```

The exact executable name should come from `PlayerSettings.productName` (currently `2birds`) or be explicitly standardized to `TwoBirds`; decide this once in the implementation and use the same name in Steam launch configuration. The plan assumes `TwoBirds.exe` because the existing post-build output and README use that name.

Before building `Local Networking`, set `TwoBirds.LocalNetworking` to `true`. Before the other two builds, set it to `false` so the result is not affected by an earlier editor choice. The player should still retain the command-line override for manually launching a local build.

The build command should create the selected output directory, pass the output executable path to Unity, and report the `BuildReport` result. Do not edit scenes or create prefabs.

### 2. Remove the old preference menu

Delete `Assets/Game/Editor/LocalNetworkingToggle.cs` or move its preference helpers into the new build entry point. Remove the `Tools/Local Networking` menu registration entirely.

Keep the `TwoBirds.LocalNetworking` key because `SessionBootstrap` still needs it for Editor Play Mode and the build commands need to control the setting. The new build commands should be the only editor UI that changes it.

### 3. Keep post-build behavior compatible with all development builds

Retain the existing Windows post-build behavior in `SteamDevelopmentBuild`:

- Copy `steam_appid.txt` beside the executable for development Steam testing.
- Generate `LocalNetwork.bat` beside development executables.

Make the postprocessor use the build report output directory safely and ensure it does not accidentally become the mechanism that selects the build mode. The build menu command owns the mode; the postprocessor only adds derived files.

For `Development - Steam`, the Steam upload must happen after the Unity build and after the post-build hook has copied any files intended to ship. Decide explicitly whether `steam_appid.txt` and `LocalNetwork.bat` belong in the Steam depot. The likely default is to exclude `steam_appid.txt` from the uploaded content because it is a development-only local App ID file, and to exclude `LocalNetwork.bat` unless local-networking is intentionally part of the Steam test package.

### 4. Add SteamPipe configuration

SteamCMD uploads SteamPipe content from VDF configuration files; it does not upload an arbitrary Unity output folder by itself. Add repository-owned scripts in a clearly named folder, for example:

```text
Build/Steam/
    app_build_5300780.vdf
    depot_build_*.vdf
```

Prefer stable source-controlled configuration outside generated build output if SteamCMD requires the generated path to remain clean; for example `BuildScripts/Steam/`. The implementation should pass an absolute or correctly relative `ContentRoot` pointing at `Build/Development-Steam` and a separate `BuildOutput` for SteamPipe logs/cache.

The app script should define:

- `AppID` `5300780`.
- An internal `Desc` containing the build type and timestamp/version.
- `ContentRoot` for `Build/Development-Steam`.
- `BuildOutput` for SteamPipe output/cache.
- The configured Windows depot mapping.
- Optional `SetLive` only if the desired private beta branch is known and explicitly approved.

The depot ID is not present in this repository and must be supplied from Steamworks App Admin. Do not guess it. The implementation is blocked until the app has a published SteamPipe install directory, a Windows depot, and a build account with the required permissions.

### 5. Invoke SteamCMD after a successful Steam build

The Steam menu command should launch SteamCMD only after `BuildReport.summary.result == Succeeded`. Use the path from `.agents/build-and-release/SKILL.md`, preferring `steamcmd` and falling back to the documented absolute path.

The upload shape is:

```powershell
steamcmd +login <build-account> +run_app_build <path-to-app_build_5300780.vdf> +quit
```

Do not hard-code a password or Steam Guard token in source, VDF, `EditorPrefs`, or generated logs. Use SteamCMD's persisted login/session configuration or a documented environment/interactive login approach. Capture the process exit code and show the SteamPipe log/output location when the upload fails.

The initial v1 behavior should upload the build and leave it on Steam's builds page. Automatically setting a public/default branch live is not safe to infer. If a private branch is supplied later, add `SetLive` only for that branch and keep the default branch manual.

Steam's official SteamPipe flow is documented at https://partner.steamgames.com/doc/sdk/uploading.

## Open inputs required before implementation

1. Confirm whether the output layout should use the proposed per-build subdirectories or a single `Build/TwoBirds.exe` path.
2. Provide the Windows depot ID for App ID `5300780`.
3. Confirm the Steam beta branch, if any, that `Development - Steam` may set live automatically. Otherwise leave the uploaded build pending manual activation.
4. Decide whether the Steam build should include `steam_appid.txt` and `LocalNetwork.bat`.
5. Decide how the build account is expected to authenticate SteamCMD on this machine. Credentials must remain outside the repository.

## Implementation order

1. Add the shared Windows development build entry point and the nested `Two Birds/Build` menu.
2. Remove the old `Tools/Local Networking` registration while preserving the preference key and runtime command-line override.
3. Adjust the existing post-build hook only where needed for the new output layout and Steam-content decision.
4. Add SteamPipe VDF files after the depot ID and branch/authentication decisions are available.
5. Connect `Development - Steam` to SteamCMD and surface upload failures.
6. Update the build documentation to replace the stale `MvpAssets.Build` instructions with the three menu commands and the Steam prerequisites.

## User visual validation

After implementation, confirm in the Unity Editor that `Two Birds > Build` contains exactly the three requested entries and that `Tools` no longer contains `Local Networking`. Launch the generated players and confirm the local build uses local networking while the other development builds use Steam, then confirm the Steam-installed test build launches from its configured Steam launch option.
