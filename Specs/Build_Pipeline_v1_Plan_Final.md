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

`Local Networking` must enable the existing local-networking behavior for the standalone build.

`Development` must produce a normal Steam-enabled development build for local testing.

`Development - Steam` must complete the Unity build first and then upload the resulting build to Steam through SteamCMD / SteamPipe.

The standalone `Tools/Local Networking` menu entry must be removed.

---

## Current State

- `ProjectSettings/EditorBuildSettings.asset` already contains the two enabled scenes:
  - `Assets/Scenes/MainMenu.unity`
  - `Assets/Scenes/Game.unity`
- There is no current build pipeline implementation in `Assets/Game/Editor`.
- The README references `TwoBirds.Editor.MvpAssets.Build`, but no `MvpAssets` class exists in the repository.
- `Assets/Game/Editor/LocalNetworkingToggle.cs` exposes `Tools/Local Networking` and stores `TwoBirds.LocalNetworking` in `EditorPrefs`.
- `Assets/Game/Runtime/Networking/SessionBootstrap.cs` reads that editor preference in the Unity Editor and also supports the `-localNetworking` command-line argument in players.
- `Assets/Game/Editor/SteamDevelopmentBuild.cs` is an `IPostprocessBuildWithReport` hook. For Windows builds it copies the root `steam_appid.txt`; for development builds it also writes `LocalNetwork.bat`.
- `steam_appid.txt` contains App ID `5300780`.
- Steam App ID: `5300780`.
- Windows/base-content Depot ID: `5300781`.
- Existing checked/untracked output is under `Builds`, but the requested output root is `/Build`.
- `.agents/build-and-release/SKILL.md` specifies SteamCMD at:

  `C:\steamworks_sdk_165\sdk\tools\ContentBuilder\builder\steamcmd.exe`

  and `steamcmd` is also expected to be available through `PATH`.
- SteamCMD has already been authenticated on this machine.
- SteamCMD username: `spencerobsitnik`.

---

## Build Output Layout

Use distinct output directories so one build flavor does not overwrite another:

```text
Build/
├── LocalNetworking/
│   └── TwoBirds.exe
├── Development/
│   └── TwoBirds.exe
├── Development-Steam/
│   └── TwoBirds.exe
└── SteamPipeOutput/
```

Use one explicitly standardized executable name:

```text
TwoBirds.exe
```

Do not derive the executable filename dynamically from `PlayerSettings.productName`.

The Steam launch configuration must use the same executable name.

`Build/` is generated output and should remain disposable / gitignored.

---

## 1. Add One Editor Build Entry Point

Create a focused editor script under:

```text
Assets/Game/Editor/
```

Recommended file:

```text
TwoBirdsBuildPipeline.cs
```

It owns:

- the three menu commands;
- the shared Windows build routine;
- build-flavor-specific defines;
- build-flavor-specific post-build files;
- Steam upload invocation for `Development - Steam`.

Use:

```csharp
UnityEditor.BuildPipeline.BuildPlayer
```

with:

- enabled scenes from `EditorBuildSettings.scenes`;
- `BuildTarget.StandaloneWindows64`;
- `BuildOptions.Development`.

Do not edit scenes or create prefabs as part of the build.

After every build, inspect:

```csharp
BuildReport.summary.result
```

and stop immediately if the Unity build did not succeed.

---

## 2. Local Networking Build Behavior

Do **not** use `EditorPrefs` as the mechanism that makes a standalone build use local networking.

`EditorPrefs` is editor-only state and is not embedded into the built player.

Instead, the `Local Networking` build must use a per-build scripting define through:

```csharp
BuildPlayerOptions.extraScriptingDefines
```

Add:

```text
TWO_BIRDS_LOCAL_NETWORKING
```

for the `Local Networking` build only.

`SessionBootstrap` should treat that define as making local networking the default in the standalone player.

Conceptually:

```csharp
#if UNITY_EDITOR
    localNetworking = EditorPrefs.GetBool("TwoBirds.LocalNetworking");
#elif TWO_BIRDS_LOCAL_NETWORKING
    localNetworking = true;
#else
    localNetworking = false;
#endif
```

The existing command-line override must remain supported:

```text
-localNetworking
```

The `TwoBirds.LocalNetworking` EditorPrefs key may remain in use for Editor Play Mode only.

The new build pipeline becomes the only build-related UI for choosing local vs Steam networking.

---

## 3. Remove the Old Local Networking Menu

Remove the standalone:

```text
Tools/Local Networking
```

menu entry.

Either:

- delete `Assets/Game/Editor/LocalNetworkingToggle.cs`; or
- retain only any reusable preference helpers while removing its menu registration.

Do not remove support for the `TwoBirds.LocalNetworking` EditorPrefs key if `SessionBootstrap` still uses it during Editor Play Mode.

---

## 4. Build-Flavor-Specific Output Files

The new build pipeline should own the output-file behavior rather than relying on a global postprocessor to infer which build flavor was requested.

The desired behavior is:

| Build Flavor | Networking Default | `steam_appid.txt` | `LocalNetwork.bat` | Steam Upload |
|---|---|---:|---:|---:|
| Local Networking | Local | No | Yes | No |
| Development | Steam | Yes | No | No |
| Development - Steam | Steam | No | No | Yes |

### Local Networking

After a successful Local Networking build:

- generate/copy `LocalNetwork.bat` beside `TwoBirds.exe`;
- do not copy `steam_appid.txt`.

`LocalNetwork.bat` exists specifically for the Local Networking build and should be preserved.

### Development

After a successful Development build:

- copy `steam_appid.txt` beside `TwoBirds.exe`;
- do not generate `LocalNetwork.bat`.

This build is intended for launching the Steam-enabled development executable directly outside the Steam client.

### Development - Steam

After a successful Development - Steam build:

- do not include `steam_appid.txt`;
- do not include `LocalNetwork.bat`;
- upload the output folder to SteamPipe.

Valve's Steam documentation states that `steam_appid.txt` is a development helper and should not be shipped in the Steam depot.

If `Assets/Game/Editor/SteamDevelopmentBuild.cs` becomes redundant after moving this behavior into the new centralized build pipeline, remove it.

---

## 5. Add SteamPipe Configuration

Keep source-controlled SteamPipe configuration outside generated build output.

Use:

```text
BuildScripts/
└── Steam/
    ├── app_build_5300780.vdf
    └── depot_build_5300781.vdf
```

Do not store the VDF source files under `Build/`.

### App Build Configuration

The app build script should define:

```text
AppID: 5300780
DepotID: 5300781
```

It should include:

- `AppID` `5300780`;
- a useful internal `Desc`;
- `ContentRoot` pointing to `Build/Development-Steam`;
- `BuildOutput` pointing to `Build/SteamPipeOutput`;
- the Windows depot mapping for depot `5300781`.

Example structure:

```vdf
"AppBuild"
{
    "AppID" "5300780"
    "Desc" "Development Steam build"

    "BuildOutput" "../../Build/SteamPipeOutput"
    "ContentRoot" "../../Build/Development-Steam"

    "Depots"
    {
        "5300781" "depot_build_5300781.vdf"
    }
}
```

Paths may be absolute or correctly resolved relative paths. The implementation must make the path behavior explicit and reliable.

### Depot Configuration

Example:

```vdf
"DepotBuild"
{
    "DepotID" "5300781"

    "FileMapping"
    {
        "LocalPath" "*"
        "DepotPath" "."
        "Recursive" "1"
    }
}
```

The Steam depot should receive the contents of:

```text
Build/Development-Steam/
```

and not the parent `Build/` directory.

---

## 6. Steam Branch Strategy

For v1, do **not** create or depend on a separate Steam beta branch.

The desired flow is:

```text
Development - Steam
        ↓
SteamPipe upload
        ↓
Build appears in Steamworks
        ↓
Developer manually selects that BuildID
        ↓
Set live on the default branch
        ↓
Approved testers install through Steam
```

The app is unreleased, so using the default branch does not make the game publicly available to everyone.

Steam access is controlled separately through Steam packages / keys.

For the current small private test, use Steam's testing / release-override key flow to give selected testers access.

Do not add `SetLive` to the v1 VDF.

A private beta branch can be introduced later if multiple simultaneous build tracks become useful.

---

## 7. SteamCMD Authentication

SteamCMD has already been authenticated on this machine.

Username:

```text
spencerobsitnik
```

The pipeline should invoke SteamCMD using the cached authentication session.

Preferred command:

```powershell
steamcmd +login spencerobsitnik +run_app_build "<path-to-app_build_5300780.vdf>" +quit
```

Preferred executable resolution:

1. first try:

   ```text
   steamcmd
   ```

2. if not found in the current shell environment, fall back to:

   ```text
   C:\steamworks_sdk_165\sdk\tools\ContentBuilder\builder\steamcmd.exe
   ```

Do not hard-code:

- password;
- Steam Guard token;
- secrets of any kind.

Do not store credentials in:

- source;
- VDF files;
- `EditorPrefs`;
- generated logs;
- environment files committed to the repository.

The Unity build pipeline should assume authentication already exists.

If SteamCMD reports that authentication is required or expired, fail the upload cleanly and instruct the developer to authenticate manually with SteamCMD outside Unity.

Do not make the Unity Editor wait on an interactive Steam Guard prompt.

---

## 8. SteamCMD Invocation

Only invoke SteamCMD after:

```csharp
BuildReport.summary.result == BuildResult.Succeeded
```

The upload command shape is:

```powershell
steamcmd +login spencerobsitnik +run_app_build "<absolute-path-to-app_build_5300780.vdf>" +quit
```

The pipeline must:

- capture stdout;
- capture stderr;
- capture the process exit code;
- surface upload failure clearly in the Unity Console;
- print or reference the SteamPipe output/log directory on failure;
- not report success merely because the Unity build succeeded.

A successful `Development - Steam` command means both:

1. Unity build succeeded;
2. SteamCMD upload succeeded.

---

## 9. Steamworks Configuration Assumptions

The Steamworks depot configuration currently contains:

```text
App ID:   5300780
Depot ID: 5300781
```

The existing depot is sufficient for a Windows-only build with one shared set of game files.

No additional depot is required for v1.

Before testing externally, verify in Steamworks that depot `5300781` is included in the packages used for:

- the developer account;
- the tester/release-override keys.

Also verify the Steam launch configuration points to:

```text
TwoBirds.exe
```

for Windows.

---

## 10. Menu Behavior

The Unity Editor must expose exactly:

```text
Two Birds
└── Build
    ├── Local Networking
    ├── Development
    └── Development - Steam
```

Expected actions:

### Two Birds > Build > Local Networking

1. Build enabled scenes.
2. Target Windows x64.
3. Use `BuildOptions.Development`.
4. Add `TWO_BIRDS_LOCAL_NETWORKING` using `extraScriptingDefines`.
5. Output to:

   ```text
   Build/LocalNetworking/TwoBirds.exe
   ```

6. Create `LocalNetwork.bat`.
7. Do not copy `steam_appid.txt`.
8. Do not invoke SteamCMD.

### Two Birds > Build > Development

1. Build enabled scenes.
2. Target Windows x64.
3. Use `BuildOptions.Development`.
4. Do not add `TWO_BIRDS_LOCAL_NETWORKING`.
5. Output to:

   ```text
   Build/Development/TwoBirds.exe
   ```

6. Copy `steam_appid.txt`.
7. Do not create `LocalNetwork.bat`.
8. Do not invoke SteamCMD.

### Two Birds > Build > Development - Steam

1. Build enabled scenes.
2. Target Windows x64.
3. Use `BuildOptions.Development`.
4. Do not add `TWO_BIRDS_LOCAL_NETWORKING`.
5. Output to:

   ```text
   Build/Development-Steam/TwoBirds.exe
   ```

6. Do not copy `steam_appid.txt`.
7. Do not create `LocalNetwork.bat`.
8. Invoke SteamCMD.
9. Upload depot `5300781`.
10. Leave the uploaded build unassigned until it is manually set live in Steamworks.

---

## 11. Error Handling

The build pipeline must fail clearly for:

- no enabled scenes;
- Unity build failure;
- missing build executable after Unity reports success;
- missing `steam_appid.txt` when producing the Development build;
- missing Steam VDF files;
- SteamCMD not found;
- SteamCMD non-zero exit code;
- Steam authentication failure;
- SteamPipe upload failure.

Do not continue to later stages after an earlier stage fails.

The Unity Console should make it obvious whether failure occurred during:

```text
Unity build
Post-build files
SteamCMD launch
Steam authentication
SteamPipe upload
```

---

## 12. Source-Control Expectations

Source-controlled:

```text
Assets/Game/Editor/TwoBirdsBuildPipeline.cs
BuildScripts/Steam/app_build_5300780.vdf
BuildScripts/Steam/depot_build_5300781.vdf
steam_appid.txt
```

Generated / gitignored:

```text
Build/
```

Do not commit:

- SteamCMD session files;
- Steam credentials;
- SteamPipe temporary upload data;
- generated Unity players.

---

## 13. Documentation Updates

Update existing build documentation to remove stale references to:

```text
TwoBirds.Editor.MvpAssets.Build
```

Document the three menu commands and their intended use:

```text
Local Networking
    Local multiplayer/networking development build.

Development
    Steam-enabled development build intended for direct local execution.

Development - Steam
    Steam-enabled development build uploaded through SteamPipe.
```

Document the known Steam identifiers:

```text
App ID:   5300780
Depot ID: 5300781
```

Document the SteamCMD username:

```text
spencerobsitnik
```

Do not document or store a password.

---

## 14. Implementation Order

1. Add the shared Windows development build entry point.
2. Add the nested `Two Birds > Build` menu.
3. Implement the three output directories.
4. Implement the `TWO_BIRDS_LOCAL_NETWORKING` per-build define.
5. Update `SessionBootstrap` to use that define as the Local Networking standalone default.
6. Remove the old `Tools/Local Networking` menu registration.
7. Move build-flavor-specific output-file logic into the centralized build pipeline.
8. Preserve `LocalNetwork.bat` for the Local Networking build.
9. Preserve `steam_appid.txt` for the non-uploaded Development build.
10. Ensure neither helper file is included in the Steam-uploaded build.
11. Remove or simplify `SteamDevelopmentBuild.cs` if it is no longer needed.
12. Add SteamPipe VDF files using:
    - App ID `5300780`
    - Depot ID `5300781`
13. Add SteamCMD invocation using cached login for `spencerobsitnik`.
14. Capture and report SteamCMD failures.
15. Update build documentation.
16. Perform visual and runtime validation.

---

## 15. User Visual Validation

After implementation, verify in the Unity Editor:

```text
Two Birds > Build
```

contains exactly:

```text
Local Networking
Development
Development - Steam
```

and:

```text
Tools
```

no longer contains:

```text
Local Networking
```

Then validate each generated build.

### Local Networking

Launch:

```text
Build/LocalNetworking/TwoBirds.exe
```

Confirm:

- local networking is the default;
- `LocalNetwork.bat` exists and performs its intended local-networking launch behavior;
- `steam_appid.txt` is absent.

### Development

Launch:

```text
Build/Development/TwoBirds.exe
```

Confirm:

- Steam networking is the default;
- `steam_appid.txt` exists;
- `LocalNetwork.bat` is absent.

### Development - Steam

Run:

```text
Two Birds > Build > Development - Steam
```

Confirm:

- Unity build succeeds;
- SteamCMD launches without requesting credentials;
- SteamPipe upload succeeds;
- the new BuildID appears on the Steamworks Builds page;
- `steam_appid.txt` is not present in the uploaded depot;
- `LocalNetwork.bat` is not present in the uploaded depot.

Then manually set the desired BuildID live on the Steam `default` branch.

Install the game through Steam and confirm the configured Windows launch option starts:

```text
TwoBirds.exe
```

Finally, test with a Steam testing / release-override key on a non-developer Steam account to confirm an external tester can redeem the key, install the current default build, and launch the game normally.
