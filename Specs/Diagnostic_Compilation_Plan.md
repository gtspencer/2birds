Migrate first-party diagnostic compilation to Unity 6.6 managed-code variants and make the custom Windows build pipeline select its variant explicitly. Development builds should retain the existing tools; shipping Release builds should omit their calls and diagnostic-only implementations.

This plan implements the diagnostic compilation recommendation in [Unity_Lint_Followup_Review.md](Unity_Lint_Followup_Review.md). It uses the existing logging, tracing, console, automation, and build systems. Performance profiling is covered separately by [Profiler_Checks.md](Profiler_Checks.md).

**Confirmed policy: Instrumented players include all existing diagnostic tools.** The AI recorder starts under its current initialization rules, the console is created in non-batch runs, and MVP automation remains opt-in through its existing command-line arguments. Their compilation gates remain consistent under `UNITY_INCLUDE_INSTRUMENTATION`, without an additional runtime development-player restriction.

**The intended behavior is defined by this matrix.**

| Context | Managed variant | Native Development Build | First-party diagnostic behavior |
| --- | --- | --- | --- |
| Unity Editor | Editor defines instrumentation and checks | Not a player build | Existing tools and Editor lifecycle hooks available |
| Existing development build routes | Checked | Enabled | AI recorder, console, tracing, traffic counters, marker/screenshot controls, and opt-in automation available |
| Instrumented player | Instrumented | Either; use disabled in the acceptance check | Same first-party diagnostic tools available independently of the native development flag |
| Shipping/local Release build | Release | Disabled | Diagnostic-only types and state excluded; conditional calls omitted; ordinary error reporting retained |

`UNITY_INCLUDE_INSTRUMENTATION` identifies logging and profiling code. `UNITY_ENABLE_CHECKS` identifies optional assertions and safety checks. `Debug.isDebugBuild` identifies the native player's development status. Unity defines the new symbols in the Editor, so existing `UNITY_EDITOR || DEVELOPMENT_BUILD` gates can become `UNITY_INCLUDE_INSTRUMENTATION` where their purpose is diagnostics. Keep genuine Editor API guards as `UNITY_EDITOR`. [Unity 6.6 migration guidance](https://docs.unity.com/en-us/engine/6000.6/manual/upgrade-guides/upgrade-guide-unity66).

The current first-party occurrences cover diagnostics and their supporting hooks. This migration does not require adding new assertions or moving ordinary gameplay/content validation behind `UNITY_ENABLE_CHECKS`. In particular, `MvpValidation` is diagnostic automation, and the finite-value detection inside impact tracing triggers a diagnostic dump; their names and checks do not justify splitting their dependency chain across different symbols.

**1. Migrate the following files as one coherent change.**

All paths below are relative to `Assets/Game`.

| Files | Required change |
| --- | --- |
| `Runtime/Diagnostics/AILogger.cs` | Replace the whole-file gate, inner implementation gates, and all diagnostic Conditional attributes together. Its `AILogLevel` and `AILogContext` types follow the same gate. |
| `Runtime/Diagnostics/AILogCapture.cs`, `AILogWriter.cs` | Replace whole-file gates; retain current recording, sampling, limits, and shutdown behavior. |
| `Runtime/Diagnostics/DevConsole.cs`, `DevConsoleStats.cs` | Replace whole-file gates together with their SessionController and transport dependencies. |
| `Runtime/Diagnostics/LogMarkerService.cs`, `MvpScreenshot.cs`, `MvpValidation.cs` | Replace whole-file gates. `PredictionDiagnostics`, declared in MvpValidation.cs, follows that file's gate. Preserve command-line opt-in and capture-availability rules. |
| `Runtime/Diagnostics/Log.cs` | Replace Conditional attributes on Info, Warning, and Marker; preserve the public Log type and unconditional Error/Exception methods. |
| `Runtime/Networking/GameTransport.cs`, `GameSteamTransport.cs` | Gate traffic dictionaries, counters, and their forwarding overrides consistently with their consumers. |
| `Runtime/Networking/SessionController.cs` | Migrate diagnostic accessors, SetConsoleOpen, and console creation. |
| `Runtime/Player/PlayerInputReader.cs` | Migrate both AutomatedInput declaration and invocation. Preserve input precedence and all normal input guards. |
| `Runtime/Player/PlayerMotor.cs` | Migrate trace fields/events, tick bookkeeping, AISnapshot, trace implementation blocks, Conditional attributes, and diagnostic context-menu gate. |
| `Runtime/Items/WorldItem.cs` | Migrate AISnapshot and its references to AILogger helpers. |
| `Editor/TwoBirdsBuildPipeline.cs` | Implement the explicit build policy and local Release route described below. |

Refresh the occurrence inventory before editing if the source has moved. Include any additional first-party callers that use the affected diagnostic-only types or fields. Limit vendor/package warnings to a separate follow-up.

**2. Preserve conditional-call semantics while replacing the symbols.**

Replace diagnostic `#if UNITY_EDITOR || DEVELOPMENT_BUILD` blocks with `#if UNITY_INCLUDE_INSTRUMENTATION`. Replace paired `[Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]` attributes with `[Conditional("UNITY_INCLUDE_INSTRUMENTATION")]`, preserving other attributes and the existing fully qualified style where used.

Preserve the distinction between a gated declaration and a conditional method:

- Whole diagnostic files, supporting fields, and snapshot methods can retain their existing declaration gates with the new symbol.
- PlayerMotor's `TraceImpact`, `TraceImpactState`, and `DumpImpactTrace` methods must remain declared where their existing unguarded callers can resolve them. Keep their method declarations and Conditional attributes, and gate their bodies as today. Release compilation then removes calls and argument evaluation while preserving a valid source-level method reference.
- Keep Log.Info, Log.Warning, and Log.Marker conditional at the call site. Replacing them with ordinary methods that immediately return would still evaluate interpolated strings and other arguments.
- Keep Log.Error, Log.Exception, and existing ordinary Unity logging behavior. A Release player is not expected to have an empty Player.log.
- Preserve Editor-only shutdown hooks in AILogger. They reference UnityEditor APIs and still require `#if UNITY_EDITOR`.

The metadata field `development = UnityEngine.Debug.isDebugBuild` in AILogger remains a runtime development flag. A non-development Instrumented player should record `false` while still producing AI diagnostics. Do not replace this field with the instrumentation symbol, rename existing metadata, or change the log format.

Changing the compile gates must leave transport forwarding, gameplay simulation, network message formats, logger limits, console commands, input behavior, and opt-in automation semantics intact.

**3. Make build options and the managed variant explicit in the existing pipeline.**

Extend the existing private `Build` helper in [TwoBirdsBuildPipeline.cs](Assets/Game/Editor/TwoBirdsBuildPipeline.cs) to receive a `ManagedCodeVariant` and `BuildOptions`, in addition to its existing output path, extra defines, and post-build callback. Set `BuildPlayerOptions.options` from that argument. Update every existing caller explicitly; do not infer the variant from a native development flag or ambient Editor setting.

| Build menu | Output directory | Variant | BuildOptions | Existing extra behavior |
| --- | --- | --- | --- | --- |
| Local Networking | `Build/LocalNetworking` | Checked | Development | Keep `TWO_BIRDS_LOCAL_NETWORKING` and LocalNetwork.bat |
| Development | `Build/Development` | Checked | Development | Keep steam_appid.txt precheck/copy |
| Development - Steam | `Build/Development-Steam` | Checked | Development | Keep its current upload entry point and success condition |
| Release (new local menu) | `Build/Release` | Release | None | Copy steam_appid.txt for local execution; produce a local build |

Retain the existing menus' names and output paths. Add **Two Birds > Build > Release** after them. Reuse the Development route's app-ID preparation/copy for the Release route, using a small private helper inside this class if necessary to avoid duplication. Preserve enabled-scene selection, `TwoBirds.exe`, Windows x64 targeting, build-result handling, and post-build callbacks.

Apply the managed variant around the actual synchronous `BuildPipeline.BuildPlayer` call:

```csharp
var target = NamedBuildTarget.Standalone;
var previousVariant = PlayerSettings.GetManagedCodeVariant(target);
BuildReport report;
try
{
    PlayerSettings.SetManagedCodeVariant(target, variant);
    report = BuildPipeline.BuildPlayer(options);
}
finally
{
    PlayerSettings.SetManagedCodeVariant(target, previousVariant);
}
```

Use the UnityEditor APIs and `UnityEditor.Build.NamedBuildTarget`. Complete ordinary preflight checks before changing the setting. Restore it before result handling, callbacks, or upload logic, including when BuildPlayer throws. Keep existing failure behavior; this restoration does not need a new exception-handling framework. [GetManagedCodeVariant](https://docs.unity3d.com/6000.6/Documentation/ScriptReference/PlayerSettings.GetManagedCodeVariant.html), [SetManagedCodeVariant](https://docs.unity3d.com/6000.6/Documentation/ScriptReference/PlayerSettings.SetManagedCodeVariant.html).

Include the chosen variant and development option in the existing build-start log so the artifact's intended configuration is clear. Do not add custom diagnostic scripting defines to emulate the variant. Instrumented already emits the instrumentation symbol, while Checked includes checks as well. [ManagedCodeVariant API](https://docs.unity3d.com/6000.6/Documentation/ScriptReference/ManagedCodeVariant.html).

Instrumented compilation is supported without adding another permanent menu or a new build-profile asset. Its acceptance artifact can be built through Unity's existing build API using Instrumented and `BuildOptions.None`. This also demonstrates that the new diagnostic behavior does not depend on `DEVELOPMENT_BUILD`.

The policy is enforced by the custom build routes. Builds started through Unity's generic Build Profiles UI must select their own intended managed variant; preserving the previous Editor setting is deliberate.

**4. Keep the implementation focused on compilation and build policy.**

The expected production edits are the 15 runtime files listed above and the existing build-pipeline file. This feature needs no new scenes, prefabs, serialized components, diagnostic service, runtime toggle, package, or generated asset. The Release menu is an extension of the existing build workflow. Existing dynamic component creation remains in the same locations under the new gates.

Do not combine this migration with query-buffer changes, rendering/loading settings, logger optimization, domain-reload changes, vendor upgrades, or unrelated cleanup. If a new blocking dependency appears, identify its exact caller and symbol relationship before expanding the work.

**5. Use the following acceptance checks when validation is explicitly requested.**

This document specifies the future checks; creating or implementing the plan does not by itself request running player builds, profiling, or gameplay validation.

| Check | Required evidence |
| --- | --- |
| First-party source inventory | No remaining DEVELOPMENT_BUILD directive or Conditional attribute in the affected first-party scope; declarations, callers, and bodies use consistent gates |
| Editor compilation and Play mode | No new compiler errors; existing console, marker controls, recording, and lifecycle behavior remain available |
| Checked development player | Successful Windows build; console/statistics and AI recording present; development metadata true; normal input and networking work |
| Non-development Instrumented player | Successful Windows build; the same diagnostic tools and opt-in automation available; development metadata false |
| Non-development Release player | Successful Windows build; no diagnostic console or AI-recorder initialization/output; opt-in MVP arguments do not activate diagnostic automation; normal input and networking work |
| Conditional calls in Release | Inspection of compiled managed code, or equivalent targeted evidence, shows that affected call sites and diagnostic argument construction are omitted; retained conditional method declarations alone are not a failure |
| Build-setting restoration | Standalone managed variant after a successful build and a controlled build failure matches its pre-build value |

For player checks, use the enabled MainMenu and Game scenes and preserve the existing scripting backend. Use distinct output directories for the three variants. For the Instrumented acceptance build, query/save the current Standalone variant, select Instrumented, call `BuildPipeline.BuildPlayer` with Windows x64 and `BuildOptions.None`, and restore the original setting in `finally`. Use an existing Unity CLI/MCP execution facility; do not add a permanent validation builder or migration tool. Supply steam_appid.txt for local Steam execution where needed. The existing `-localNetworking` runtime option can be used for controlled local host/client checks.

Use a fresh absolute `-aiLogDir` for each diagnostic player run, so old files cannot make Release appear to produce logs. Exercise a recording marker and screenshot in graphical runs. Retain the existing batch/headless behavior, where screenshots are unavailable and console creation is skipped. Check regular error reporting independently of the recorder.

Use the local build routes for validation. The Development - Steam entry also uploads and is unnecessary for establishing compilation or diagnostic behavior. A non-development Instrumented build checks compilation policy; renderer/GPU profiling remains a separate task under Profiler_Checks.md.

The main failure modes to watch for are references to excluded diagnostic types, calls whose arguments still allocate in Release, inconsistent transport/statistics gates, and a build variant that leaks into the next build. Editor compilation alone does not cover stripped player branches.

**The user should visually inspect the changed diagnostic availability after implementation.** In Checked and Instrumented graphical players, open/close the console, toggle statistics, confirm that gameplay input resumes, and inspect a requested screenshot. In Release, confirm that diagnostic controls do not appear or intercept input. In each variant, enter a session and inspect player movement, held-item interaction, and host/client presentation for regressions around the migrated hooks.
