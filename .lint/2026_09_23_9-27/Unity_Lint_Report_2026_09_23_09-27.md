# Unity Lint Report

## Status

- InspectCode: ISSUES; completed, exit 0.
- Project Auditor: ISSUES; completed, exit 0; all 13 modules reported completion.
- Scope: full solution and normal Project Auditor audit, Windows 64-bit target.
- Fix mode: no. Strict mode: no.
- Timestamp: 2026_09_23_09:27 local time; filenames use `09-27` because Windows prohibits colons.

## Summary

- Critical: 0 confirmed first-party findings.
- High: 0 confirmed first-party findings.
- Medium: 5 prioritized concern groups, detailed below.
- Low: 5 follow-up groups, detailed below.
- Style-only: 2,332 first-party InspectCode occurrences across naming, namespace, syntax and similar non-behavioral rules. Of these, 2,002 are naming and 189 namespace findings.

Priority counts are reviewed groups, not a reclassification of every raw analyzer entry. No runtime bottleneck was measured. Medium performance findings identify opportunities to measure, not demonstrated frame-time regressions.

| Analyzer scope | Errors / Critical / Major | Other results |
| --- | --- | --- |
| InspectCode, entire solution | 2 errors, both vendor shader-parser diagnostics | 9,729 warnings; 5,729 suggestions (`note`); 15,460 total |
| InspectCode, `Assets/Game` | 0 errors | 2,416 warnings; 269 suggestions; 2,685 total |
| InspectCode, vendor/generated/other | 2 errors | 12,775 total |
| Project Auditor, entire report | 0 Critical or Major | 11,030 items, including inventory and informational entries |
| Project Auditor, descriptor-backed diagnostics | 0 Critical or Major | 1,542 Moderate; 5,080 Info; 6,622 total |
| Project Auditor, descriptor-backed `Assets/Game` diagnostics | 0 Critical or Major | 408 Moderate; 1,534 Info; 1,942 total |
| Project Auditor, compiler messages | No error-severity entries | 236 code warnings, including 44 under `Assets/Game`; 6 shader warnings |

Project Auditor inventory includes 3,960 `Default` and 202 `None` entries. Do not count these as performance problems. The 242 compiler-message entries have no descriptor ID and are counted separately from descriptor-backed diagnostics. First-party compiler messages and API warnings overlap InspectCode results.

## Highest-Priority Findings

### 1. Cart headlights create an unnecessary material instance

- Source: Project Auditor, `PAC0039`, 1 Moderate occurrence.
- Severity: Medium.
- Location: [GolfCartPresentation.cs:66](../Assets/Game/Runtime/Vehicles/GolfCartPresentation.cs#L66), also line 87.
- Why it matters: `Awake` reads `headlightMesh.material`, creating a per-renderer copy. The code subsequently only switches between that material and `litMaterial`; it does not edit the cached material. There is no matching material cleanup in this class. Each spawned cart therefore creates an avoidable material instance, with possible batching and lifetime costs.
- Recommended change: Cache `sharedMaterial` and switch shared material references. If a unique material becomes necessary, explicitly own and destroy it. This is a spawn-time cost, not a material allocation every frame.

### 2. Diagnostic code still depends on deprecated DEVELOPMENT_BUILD compilation

- Source: InspectCode, `UAC0009`, 36 first-party warning occurrences; related compiler messages also appear in Project Auditor.
- Severity: Medium, compatibility/reliability risk.
- Location: [PlayerMotor.cs:139](../Assets/Game/Runtime/Player/PlayerMotor.cs#L139), lines 428 and 629 onward; [AILogger.cs:1](../Assets/Game/Runtime/Diagnostics/AILogger.cs#L1), and other locations in the findings CSV.
- Why it matters: The Unity analyzer explicitly deprecates this preprocessor directive in favor of managed-code variant symbols. It controls logging types, implementation blocks and conditional call sites. Successful Editor compilation does not establish that diagnostic behavior remains correct in development players.
- Recommended change: Choose the intended managed-code variant symbol consistently for types, blocks and `Conditional` attributes; use `Debug.isDebugBuild` when runtime development-build detection is intended. Check a development player and a release player before declaring this resolved. Do not simply enable all diagnostic code in every player.

### 3. Static batching and SRP Batcher are enabled together

- Source: Project Auditor, `URP0302`, 3 Moderate occurrences.
- Severity: Medium; measure rendering impact.
- Location: `Project/Graphics` default `PC_RPAsset`, and `Project/Quality` Mobile and PC pipeline assets. Confirmed `m_StaticBatching: 1` in [ProjectSettings.asset:533](../ProjectSettings/ProjectSettings.asset#L533) and `m_UseSRPBatcher: 1` in [PC_RPAsset.asset:72](../Assets/Settings/PC_RPAsset.asset#L72).
- Why it matters: Auditor flags conflicting batching strategies. Actual cost depends on scene content and material/shader compatibility. The two PC references and Mobile setting are one concern, not three independent defects.
- Recommended change: Compare the Steam target with static batching disabled using the Frame Debugger and CPU/GPU profiling. Keep the strategy that performs best with the actual scene; do not infer an improvement from this warning alone.

### 4. Physics queries allocate result arrays during gameplay actions

- Source: Both. InspectCode `Unity.PreferNonAllocApi`: 5 warnings. Project Auditor `PAC0008`: 2, `PAC0013`: 2, `PAC0012`: 1, all Moderate; these describe the same five call sites.
- Severity: Medium, burst allocation opportunity.
- Locations: [SlingshotAim.cs:13](../Assets/Game/Runtime/Items/SlingshotAim.cs#L13); [WorldItem.Potions.cs:36](../Assets/Game/Runtime/Items/WorldItem.Potions.cs#L36); [PotionArea.cs:73](../Assets/Game/Runtime/Effects/PotionArea.cs#L73); [WorldItemRegistry.Effects.cs:250](../Assets/Game/Runtime/Items/WorldItemRegistry.Effects.cs#L250), also line 256.
- Why it matters: `RaycastAll`, `OverlapSphere` and `OverlapCapsule` return allocated arrays. Cauldron blasts perform an overlap plus raycasts for eligible local receivers. These can combine with potion creation and other allocations in action-heavy frames.
- Call-site qualification: Slingshot aiming is invoked when firing (`PlayerEquipment.TryFireSlingshot`, line 127), not continuously while aiming. Potion seeding occurs at initialization, reseeding and cart contact-generation changes. These are not five unconditional per-frame allocations.
- Recommended change: Profile simultaneous potion/blast/shot activity. If material, reuse query buffers with a defined overflow strategy that preserves every required hit. Do not silently truncate blast targets or change nearest-hit selection.

### 5. Collision meshes are not prebaked

- Source: Project Auditor, `PAS0007`: 1 Moderate setting warning; `PAA6014`: 17 triangle-mesh and `PAA6013`: 1 convex-mesh observations.
- Severity: Medium, load-time opportunity; scene relevance requires confirmation.
- Location: [ProjectSettings.asset:100](../ProjectSettings/ProjectSettings.asset#L100), `bakeCollisionMeshes: 0`. Related observations reference built-in Plane/Sphere meshes in `Library/unity default resources`.
- Why it matters: Missing cooked collision data can move mesh-cooking work into initialization. The whole-project scan includes demo and non-shipping content; the report does not establish that all 18 referenced objects ship in the game.
- Recommended change: Identify which colliders occur in shipped scenes and compare initialization time with prebaking enabled. Account for build size/time. Built-in or runtime-created geometry may need separate treatment; the setting alone is not guaranteed to resolve every observation.

## Performance Findings

The material, physics-query, batching and mesh-cooking findings above are the primary actionable candidates. Lower-priority groups:

1. **Collision callbacks need measurement, not removal.** `PAC0035` reports 4 Moderate occurrences: `WorldItem.Birds.cs:45`, `PlayerMotor.CartContacts.cs:262`, `PebbleProjectile.cs:216`, `GolfCartController.cs:231`. These callbacks maintain gameplay contact state. Reviewed paths already filter ineligible contacts; player contacts use `GetContact` rather than allocating the contacts array. Profile dense cart/player/item contact scenarios before changing them. Priority: Low/profiling follow-up.
2. **Generated meshes retain readable CPU copies.** `PAA1002` flags 2 first-party generated first-person arm meshes under `Assets/Game/Generated/Avatars/{461d9592f370666a,44816e438f2e4775}/FirstPerson/`. Confirm that runtime skinning, processing and collision paths do not require CPU mesh data before disabling readability in the generation pipeline. Memory savings are not quantified. Priority: Low.
3. **Small code-level optimization suggestions.** InspectCode reports 18 multidimensional-array accesses: 14 in the Editor-only placement tool and 4 in `AvatarHandTargets.cs:22,29,34`. The runtime table is only 2 by 4. There are 19 string-based shader-property lookups (15 Editor-only in `HatSetup`, 4 cosmetic setup calls) and 8 multiplication-order warnings. These are not evidence of a meaningful frame-time problem. Cache property IDs when naturally editing the code; avoid broad churn. Priority: Low.

Auditor also reports 112 boxing sites under `Assets/Game`, many in Editor tooling, logs, UI construction and error paths. Allocation findings include cached collections created once, such as the cart shape cache at `BirdHitReporter.cs:249-260`. These should not be reported as per-tick allocation bugs without checking their guards.

There are 76 controller-glyph Resources warnings (`PAA3000`) under `Assets/Game/UI/Resources`. This is a bounded glyph library; a wholesale loading-system migration is not justified by the warning alone. Mipmap streaming, upload-budget defaults, mesh-data optimization and mipmap stripping remain profiling-dependent settings. Physics2D collision-matrix advice is not automatically relevant to this 3D game. Domain reload protects static initialization semantics and should not be disabled solely for lint cleanliness.

## Correctness / Reliability Findings

No first-party compile failure or confirmed serious runtime failure was established. The diagnostic-build compatibility concern above warrants follow-up.

InspectCode emitted 12 possible-null warnings. Reviewed examples are guarded or depend on explicit internal contracts:

- `AvatarRegistry.cs:32`: `BuildLookup` initializes the dictionary before use.
- `SteamLobby.cs:47,87`: the callback captures `call`; assignment completes before the Steam API handle is registered with `Set`. The related 4 `AccessToModifiedClosure` warnings do not establish a callback race.
- `PlayerSeating.cs:232`: `Seated` requires a non-null cart and a valid seat index.
- `GolfCartNetwork.cs:283`: unsuccessful driver lookup yields `owner = -1`, which takes the ownership-removal branch.
- `HudController.cs:574,580`: loop bounds collapse to zero for null arrays. Line 231's event handler is registered on the constructed slot elements.
- `MenuPresenter.cs:267`: dereference follows successful lookup in a list populated with constructed buttons.
- Editor folder helpers and `AILogWriter.cs:404` rely on internal path/JSON contracts; these remain contract-dependent warnings rather than demonstrated failures.

`PossibleLossOfFraction` at `PlayerInventory.cs:209` is intentional integer row indexing for a four-column stack-drop layout. Do not change it to floating-point division.

`NonReadonlyMemberInGetHashCode` has 3 occurrences at `AvatarAppearance.cs:36`. No dictionary or hash-set keyed by `AvatarAppearance` was found in the searched first-party appearance code. Avoid mutating it while used as a key if such use is introduced; no current hash-container failure was established. The readonly Playable warnings concern wrapper copies; they do not by themselves prove lost graph updates.

## Maintainability / Style Findings

4. **Deprecated APIs and a hidden base member.** InspectCode reports 11 `CS0618` occurrences covering FishNet byte serialization and Unity object-finding APIs, plus one `CS0108/CS0114` warning at `PlayerPotionEffects.cs:26` for the `Reset` property hiding `NetworkBehaviour.Reset()`. Project Auditor overlaps the API warnings. Update deliberately while preserving serialization layout, lookup semantics and callers. These compile today. Priority: Low.
5. **Unused/private-member cleanup candidates.** Examples include `HudController.GetItemName` at line 626, unused local values (4 occurrences), and an ignored return value in `PlayerHeldItemPresentation.cs:459`. Solution-wide member and parameter warnings also include Unity callbacks, serialized fields and FishNet RPC signatures. Do not remove members merely because a static call graph cannot see their consumers. Priority: Low.

The 2,332 style-only occurrences are dominated by project naming and namespace conventions. Raw warning severity does not turn them into correctness failures. Existing rules were used without overrides; no suppression or broad style cleanup was applied.

Per-rule counts, original severity, representative message and location are in [inspectcode-groups_2026_09_23_09-27.csv](inspectcode-groups_2026_09_23_09-27.csv). Exact occurrences are in the findings CSV files below.

## Third-Party or Generated-Code Findings

- InspectCode's 12,775 non-first-party entries include FishNet, FishySteamworks, LeanTween, TextMesh Pro, other vendor content and generated/transient material. They are separate from the remediation priorities above.
- The two InspectCode errors are `.CppCompilerErrors` in `Assets/TextMesh Pro/Shaders/TMPro_Mobile.cginc:3,14`. They occur at Unity shader macro declarations. Unity's audit reported shader warnings but no matching errors, so these appear to be InspectCode parsing limitations, not established shader compile failures. No shader-variant build was performed.
- Project Auditor's 6 shader warnings include obsolete TextMesh Pro debug pragmas, a duplicate URP keyword, and MToon `_FORWARD_PLUS` deprecation plus a gradient-in-loop warning. Review package/vendor updates and inspect avatar rendering before editing vendor shaders.
- Across all content, asset rules include 126 imported-mesh readability warnings (`PAA1000`), 4 generated-mesh readability warnings (`PAA1002`, including the 2 first-party meshes above), 2 32-bit index-format warnings, 88 Resources entries, 3 sprite/UI mipmap warnings and 4 audio import warnings. Runtime residency and inclusion in shipped scenes were not established.
- Package update/downgrade and experimental-package suggestions are informational/contextual. Do not downgrade the working Auditor rules package or change dependencies automatically.

## Analyzer Notes

- InspectCode 2026.2.2 ran with `--swea --severity=SUGGESTION`, all solution projects, and no exclusion overrides. No repository `.editorconfig` or `.DotSettings` files, or `.editorconfig` files in the checked ancestor directories, were found. Existing tool defaults/settings layers remained enabled. Generated C# projects specify warning level 4 and `NoWarn` 0169/0649.
- Project Auditor used Unity 6000.6.2f1, rules 2.0.0, Windows 64-bit, Release code optimization, Player and Editor code, default `User` code-owner filtering, and all project areas. `UseRoslynAnalyzers` is false in the saved report; InspectCode separately provided source-analyzer findings. Default code ownership is not equivalent to auditing all package source code.
- Existing Project Auditor rules contain no ignore rules; the report contains 0 ignored entries. Custom existing diagnostic thresholds were preserved.
- Build-size, build-step and complete shader-variant coverage require a player build. This invocation performed a normal audit, not a player build or runtime benchmark.
- The `.projectauditor` file contains a `PROJECT_AUDITOR_REPORT` header and version line before its JSON payload. Parsing skipped that header. No missing output was treated as a pass.
- The first CLI launch rejected forwarded `-batchmode` because `unity run` owns that flag. The corrected launch succeeded; no analyzer remains failed or blocked.
- A reusable bridge was added at `Assets/Editor/AgentTools/ProjectAuditorCli_2026_09_23_09-27.cs`. Unity generated its metadata. InspectCode started before this bridge existed, so the bridge is outside that SARIF snapshot; Unity compiled and executed it successfully.
- Unity saved incidental material and physics-settings changes during the batch run. Those four files were clean at preflight, their changes were reviewed, and they were restored after Unity exited. No production-code, scene, project-setting, package or analyzer-rule changes remain from this audit.
- The pre-existing modified skill and untracked `Unity_Upgrade_Errors.md` were preserved. `/Specs` was not read.

## Validation

Commands run (from the repository root):

```powershell
jb inspectcode '2birds.sln' --output='.lint/inspectcode_2026_09_23_09-27.sarif' --swea --severity=SUGGESTION
$env:UNITY_LINT_TIMESTAMP = '2026_09_23_09-27'
& 'C:\Users\spenc\AppData\Local\Unity\bin\unity.exe' run . --editor-version 6000.6.2f1 -- -executeMethod AgentTools.ProjectAuditorCli.Run -logFile 'C:\Users\spenc\source\repos\2birds\.lint\project-auditor_2026_09_23_09-27.log'
```

- InspectCode exit: 0; SARIF saved with 15,460 results.
- Unity CLI retry / Editor exit: 0; audit completion logged with 11,030 report items.
- No Play Mode tests, standalone player build, live multiplayer session or runtime profiling was performed.

Output files:

- [InspectCode SARIF](inspectcode_2026_09_23_09-27.sarif)
- [InspectCode execution log](inspectcode_2026_09_23_09-27.log)
- [InspectCode grouped counts](inspectcode-groups_2026_09_23_09-27.csv)
- [InspectCode exact findings](inspectcode-findings_2026_09_23_09-27.csv)
- [Project Auditor report](project-auditor_2026_09_23_09-27.projectauditor)
- [Project Auditor diagnostics and compiler messages](project-auditor-findings_2026_09_23_09-27.csv)
- [Unity Editor log](project-auditor_2026_09_23_09-27.log)
- [Initial CLI argument rejection](unity-cli_2026_09_23_09-27.log)
- [Successful CLI retry log](unity-cli-retry_2026_09_23_09-27.log)

User visual validation: open the game and inspect cart paint/headlights, avatar materials and first-person arms, TextMesh Pro text, and potion effects. For the performance candidates, observe the Profiler during repeated cart spawning, dense collisions, shots and potion blasts; compare GC allocations and initialization costs before considering fixes.
