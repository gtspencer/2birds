Prioritize the diagnostic migration and its build policy. Profile the active-potion sweep next. Defer the other performance changes unless measurements establish a benefit; the warning count substantially overstates the shipping workload.

This assessment uses the current working tree and the [follow-up report](.lint/2026_09_23_9-27/Unity_Lint_Followup_2026_09_23_09-27.md). Shipping scope means the enabled MainMenu and Game scenes in [EditorBuildSettings.asset](ProjectSettings/EditorBuildSettings.asset), which the custom build pipeline also uses. Text dependency tracing includes their referenced serialized prefabs and assets; imported subobjects, dynamically loaded content, and actual player residency still require Unity inspection.

| Follow-up | Recommendation now | Main implementation risk |
| --- | --- | --- |
| Diagnostic compilation | Worth doing soon, together with explicit build variants | Diagnostics disappear, remain in shipping builds, or fail player compilation when related gates differ |
| Active-potion physics sweep | First profiling candidate; implement if allocation cost matters | Missing receivers or changing impact selection |
| Other allocating physics queries | Defer unless the same profiling identifies them | Truncated targets, incorrect nearest hit, or missed blast blockers |
| Static batching | Defer; inexpensive comparison during rendering profiling | Trading existing batching benefits for no measurable improvement |
| Collision prebaking | Defer | Extra build work/data without reducing meaningful initialization cost |
| Generated arm readability | Technically promising, low payoff | Discarding mesh data before generation finishes or breaking a downstream consumer |
| Collision callbacks, glyph loading, loading settings | Keep current behavior | Lost contact state, missing glyphs, or loading regressions |
| Small optimizations, cleanup, contract warnings | Keep unchanged without a demonstrated defect | Semantic changes and maintenance cost for negligible benefit |
| Vendor/package findings | Revisit with a relevant supported update or reproducible defect | Shader, material, import, or package compatibility regressions |

**1. Diagnostic migration needs a build-policy change as well as symbol replacement.**

Locations: [TwoBirdsBuildPipeline.cs](Assets/Game/Editor/TwoBirdsBuildPipeline.cs), lines 66–94; [AILogger.cs](Assets/Game/Runtime/Diagnostics/AILogger.cs), lines 1, 45 and 168; [SessionController.cs](Assets/Game/Runtime/Networking/SessionController.cs), line 133; [PlayerMotor.cs](Assets/Game/Runtime/Player/PlayerMotor.cs), lines 629–676.

Every custom build route uses `BuildOptions.Development`; none explicitly selects a Managed Code Variant, and this pipeline has no release-build route. A symbol-only migration therefore leaves the intended player behavior dependent on another setting. The effective current variant needs an Editor API query; the build script alone cannot establish it.

Unity 6.6 still supports `DEVELOPMENT_BUILD`, so this is not an established current compilation failure. Unity documents removal in 6.8. Its migration guidance recommends selecting Checked to preserve development diagnostics. That makes this worthwhile before the next engine upgrade and useful now for predictable development builds. [Unity 6.6 migration guidance](https://docs.unity.com/en-us/engine/6000.6/manual/upgrade-guides/upgrade-guide-unity66).

The code gates complete types, fields, transport overrides, snapshots, UI construction, and conditional methods. Migrating only the method attributes or only the type declarations can either remove calls unexpectedly or leave references to unavailable members. Preserve the unconditional `Log.Error` and `Log.Exception` methods in [Log.cs](Assets/Game/Runtime/Diagnostics/Log.cs). Preserve `Debug.isDebugBuild` in the logger's metadata: that field describes the native player's development status.

Recommended policy: Checked for normal development; Release for shipping; Instrumented when profiling with instrumentation but without extra checks. Instrumented includes `UNITY_INCLUDE_INSTRUMENTATION`, while Checked includes that symbol and `UNITY_ENABLE_CHECKS`. These settings are independent of the native development-build choice. [Managed code variants](https://docs.unity.com/en-us/engine/6000.6/manual/scripting/debugging-and-diagnostics/managed-code-variants).

Most current gates cover logging, tracing, network statistics, and diagnostic tools. There is no reason to reclassify whole files as safety checks because their names contain “Validation.” Normal gameplay guards and content validation must retain their existing behavior.

One product decision remains: should Instrumented players contain the full AI recorder and console? The recorder initializes automatically when compiled in, and SessionController creates the console in non-batch runs. Including them affects overhead and available UI. My recommendation is to keep diagnostic types and their callers together under instrumentation, with that behavior explicit. If tools must require a development player, apply the runtime development check consistently at their activation points.

Acceptance requires Checked development and Release non-development player builds, plus an Instrumented player if supported. Check compilation, expected diagnostic availability, logger output, and normal networking. Editor compilation cannot exercise the stripped player branches.

**2. The potion sweep deserves priority over the other query warnings.**

Locations: [WorldItem.Potions.cs](Assets/Game/Runtime/Items/WorldItem.Potions.cs), line 32; [WorldItemRegistry.cs](Assets/Game/Runtime/Items/WorldItemRegistry.cs), lines 107–108 and 586; [SlingshotAim.cs](Assets/Game/Runtime/Items/SlingshotAim.cs), line 13; [PotionArea.cs](Assets/Game/Runtime/Effects/PotionArea.cs), line 71; [WorldItemRegistry.Effects.cs](Assets/Game/Runtime/Items/WorldItemRegistry.Effects.cs), line 245.

`AfterPotionPhysics` runs after physics simulation for an active, armed, released, locally simulated potion. The SessionRoot prefab configures a 60 Hz tick rate. This is a repeated flight-time query, not just an allocation when the potion breaks. Actual allocation bytes, empty-result behavior, active potion counts, and GC impact need a player capture.

Slingshot targeting runs when firing. Area seeding runs on initialization/reseeding/contact-generation changes. Blasts filter for locally owned eligible receivers before performing obstruction raycasts, which limits their per-client work. These are lower priorities unless simultaneous activity shows a cost.

For a buffer conversion, grow and retry whenever the raw result count equals capacity, before consuming results. Preserve layer masks, trigger modes, receiver eligibility, release grace, slingshot exclusions, nearest-hit selection, and the blast's exclusion of its own cauldron colliders. Unity does not guarantee that a full nonalloc raycast buffer contains the closest hits. [Physics.RaycastNonAlloc](https://docs.unity3d.com/6000.6/Documentation/ScriptReference/Physics.RaycastNonAlloc.html).

The blast contains an outer overlap and inner raycasts. Give buffers clear ownership and account for callbacks before sharing them across systems. Do not assume the multiplayer player limit bounds environment hits or all collider counts. Buffer growth removes repeated result-array allocations after capacity stabilizes; it does not remove the physics query cost or other action allocations.

Measure `GC.Alloc`, collection spikes, and frame time while several potions fly and shots/blasts overlap. If material, begin with the potion sweep. Exercise overflow and filtering deliberately before extending the conversion. This requires no new network messages or authority changes.

**3. Current scene content gives batching and prebaking little apparent payoff.**

Locations: [ProjectSettings.asset](ProjectSettings/ProjectSettings.asset), lines 100 and 533; [PC_RPAsset.asset](Assets/Settings/PC_RPAsset.asset), line 72; [Game.unity](Assets/Scenes/Game.unity), line 7581.

Both batching settings are enabled. A text traversal of the two enabled scenes and their dependencies reached 19 prefab files. The only serialized MeshCollider and the only GameObject with the Batching Static bit in those scene/prefab documents are `Ground (100m)`. No first-party runtime calls to `StaticBatchingUtility` or creation of MeshColliders appeared in the runtime search. This narrows the likely opportunity considerably, although imported subobjects and runtime state need separate inspection.

Unity recommends SRP Batcher for URP and generally recommends disabling static batching with its newer rendering approaches. That guidance does not establish a measurable gain for this scene. Keep the current configuration until an equivalent camera path shows lower render-thread/main-thread time or memory with static batching disabled. Use Frame Debugger to establish the actual batching path. [Unity draw-call optimization guidance](https://docs.unity3d.com/6000.6/Documentation/Manual/optimizing-draw-calls-choose-method.html).

The original Auditor data has scene/prefab provenance that the CSV's mesh-path column hides. Of the 18 missing-prebake observations, 17 refer to vendor demo scenes or the UniGLTF LookDev floor prefab; one refers to Game's built-in ground Plane. Thus the report does establish one relevant collider, but not an 18-collider shipping problem.

Prebaking is primarily an initialization tradeoff. Compare cold scene initialization, mesh-cooking samples, build duration, and player size before changing it. Confirm that enabling the setting actually supplies cooked data for this built-in mesh. Changing geometry or replacing the ground collider would expand scope and introduce collision behavior changes.

**4. Arm readability looks feasible from first-party consumers, but savings are modest.**

Locations: [AvatarProcessor.cs](Assets/Game/Editor/AvatarProcessor.cs), lines 388–484; [AvatarCosmeticPresentation.cs](Assets/Game/Runtime/Avatars/Appearance/AvatarCosmeticPresentation.cs), line 24; [AvatarTattooPlacement.cs](Assets/Game/Runtime/Avatars/Appearance/AvatarTattooPlacement.cs), line 139; [PlayerHandPresentation.cs](Assets/Game/Runtime/Player/PlayerHandPresentation.cs), line 415.

The generated arm meshes are referenced by the corresponding first-person prefabs. Their serialized vertex-plus-index payloads are approximately 264 KiB and 116 KiB, or 380 KiB combined. This is an estimate of those payloads, not a measured resident-memory reduction; skinning data, other overhead, and actual residency affect savings.

The generator removes colliders and vendor behaviours from the first-person hierarchy. First-person tattoo presentation reads generated tattoo regions and uses decal projectors. The runtime `BakeMesh` consumer is AvatarTattooSurface in the full-avatar customization preview; the first-person presentation follows a different path. Content validation checks mesh metadata rather than fetching vertex arrays. These observations remove several suspected first-party dependencies on readable arm data.

Generation still performs CPU-dependent tattoo authoring after mesh extraction and asset creation. Any discard step must follow all required processing, preserve saved mesh data, and survive reimport and regeneration. Do not extend this change to the original full-avatar meshes. Unity documents readability as retaining CPU-addressable mesh data and identifies several consumers that require it. [Mesh.isReadable](https://docs.unity3d.com/6000.6/Documentation/ScriptReference/Mesh-isReadable.html).

If revisited, change the generation pipeline and regenerate through the existing processor. Inspect both avatars in a Windows player and compare memory there; an Editor-only check cannot settle player compatibility. Given the payload size, this is optional maintenance rather than a current performance priority.

**5. The remaining groups do not justify broad remediation.**

| Group and representative location | Risk and recommended treatment |
| --- | --- |
| Collision callbacks: `PlayerMotor.CartContacts.cs:262`, `GolfCartController.cs:231`, `WorldItem.Birds.cs:43` | These maintain continuing contact state. Removing or reducing callbacks can change grounding, cart interaction, or damage. Profile dense contacts first. |
| Glyphs: `InputPresentation.cs:82` | Resource results are cached. A loading-system migration introduces initialization/lifetime work with no demonstrated benefit for this bounded library. |
| Domain reload: `EditorSettings.asset:29–30` | Options are enabled with value zero, so reloads remain enabled. Disabling domain reload requires an audit of static state and subscriptions across repeated Play sessions; retain it. |
| Arrays, multiplication order, boxing | Small tables, Editor tooling, cached allocations, and logs need frequency-based assessment. Arithmetic reordering can change floating-point results. Keep changes tied to measured runtime cost. |
| Unused members and ignored returns | Preserve Unity/FishNet entry points and serialized contracts. `PlayerHeldItemPresentation.CurrentAge()` updates `age` and `clock`; deleting calls because their return values are ignored changes timing. Defer `HudController.GetItemName` to explicitly scoped cleanup. |
| Nullability, integer indexing, appearance hashing, Playable wrappers | Analyzer warnings alone do not establish broken contracts. Change these only for a demonstrated failure; speculative fixes can alter behavior. |
| Vendor shaders, package suggestions, import/loading settings | Require a compatible update or concrete rendering/loading defect. Review vendor release notes against the installed versions and shipping usage; inspect avatars and TMP text after any update. No broad dependency or import change is justified by this follow-up. |

**Prerequisites that can be checked before implementation**

Source and serialized-asset inspection can establish call frequency, filtering contracts, build entry points, diagnostic dependencies, direct mesh consumers, scene references, and rough asset payload sizes, as used above. These checks cannot establish frame-time improvement or actual player mesh residency.

Unity CLI reported no connected Editor for this project. With an Editor connection, query `PlayerSettings.GetManagedCodeVariant` for Standalone and use `AssetDatabase.GetDependencies` plus loaded-object inspection to finish the imported/runtime asset inventory. Local player builds can establish compilation and diagnostic stripping; the development-and-Steam build menu also uploads, so verification should use a local build path.

Runtime decisions need representative Windows player captures: allocations during potion flight, render timing for batching, cold initialization for collision cooking, and memory snapshots for arms. Keep the workload, build variant, and logging configuration consistent between comparisons. Source inspection cannot choose whether full diagnostic tooling belongs in Instrumented players; that is a build-policy preference.

For changes eventually implemented, visually inspect the development console and release behavior, potion contacts and blast obstruction on host and client, slingshot targeting near blockers, cart/ground rendering and collisions, both avatars' arms and tattoos, and glyphs/TMP text where affected.
