# VRM avatar presentation review

The overall architecture is coherent: a stable network player owns identity and gameplay, replaceable instances own skeleton resources, the local player avoids a hidden avatar, and one ordered presentation system drives animation, IK, and native springs. Preserve those boundaries. The implementation still has correctness and lifecycle defects, and its spring-buffer teardown creates avoidable performance spikes. It should not be considered bug free or proven optimal.

Scope: the working changes against `HEAD`, including the new runtime/editor code, generated prefabs/settings, animation import changes, demo scene, and accompanying guides. Findings below distinguish concrete code defects from content and scaling risks. P1 means high priority; P2 means a normal-priority defect; P3 means a lower-priority maintenance issue.

## Findings

### 1. [P1] Spring initialization failures bypass replacement failure handling

**Locations:** `Assets/Game/Runtime/Avatars/NativeSprings/AvatarSpringRuntimeProvider.cs:22`, `Assets/Game/Runtime/Avatars/AvatarInstance.cs:54`, `Assets/Game/Runtime/Avatars/AvatarPresentation.cs:224`.

The provider's `InitializeAsync` captures failures in its returned `Task`. In the installed UniVRM version, `Vrm10Instance.MakeRuntime` calls that method without awaiting or inspecting its result. Reading `vrm.Runtime` therefore does not establish that spring construction succeeded. `AvatarInstance.Initialize` nevertheless sets `springsRegistered = true` and later `Initialized = true`, allowing the candidate to replace the working avatar.

A broken spring joint/collider reference can consequently publish an avatar with failed secondary-motion initialization instead of retaining the previous avatar and reporting an actionable preparation failure. The processor also checks only joints before the last joint and does not validate collider-group contents, so its checks do not close this gap.

**Recommendation:** expose completion/failure from the custom provider and explicitly require successful initialization before committing the candidate. Propagate the original failure to `PreparationFailed`; validate the full spring chain and collider references during processing. Do not assume an `ImmediateCaller` makes exceptions escape an `async Task` method synchronously.

**Supporting package code:** `Library/PackageCache/com.vrmc.vrm@48d64123d085/Runtime/Components/Vrm10Instance/Vrm10Instance.cs:136`; `Runtime/Components/Vrm10Runtime/Springbone/FastSpringBoneBufferFactory.cs` in the same package.

### 2. [P2] Registry resolution does not establish that the prefab matches its settings

**Locations:** `Assets/Game/Runtime/Avatars/AvatarRegistry.cs:45`, `Assets/Game/Runtime/Avatars/AvatarInstance.cs:28`, `Assets/Game/Runtime/Avatars/AvatarSettings.cs:20`.

Registry validation compares identity/source metadata but never compares the processed prefab's Animator Avatar with `Settings.Generated.HumanoidAvatar`. Runtime staging also trusts generated dimensions, required bones, renderers, and processor format without validating them. `Generated.FormatVersion` is written but never consumed.

In an in-memory registry copy, assigning the Gerbil prefab to Chill's otherwise unchanged entry still made `TryResolve` return true, despite different Humanoid Avatar references. This mixes one skeleton with another's scale, limb lengths, sole offsets, and seated placement. A separate staging check with a zero generated height produced Unity's invalid infinite-scale diagnostic, but `Stage` returned successfully: engine diagnostics are not a substitute for explicit rejection.

**Recommendation:** validate the prefab/settings association and finite, positive measurements once at resolution/preparation. Check required runtime components, mappings, usable renderers, and the supported generated-data version before publishing a binding. These checks belong at the asset boundary, not in the frame loop. Preserve the existing avatar on rejection.

### 3. [P2] Seated head look applies the cart's tilt to an already world-relative pitch

**Locations:** `Assets/Game/Runtime/Avatars/AvatarHumanoidIK.cs:146`, `Assets/Game/Runtime/Player/PlayerAvatarPresentation.cs:130`, `Assets/Game/Runtime/Player/PlayerSeating.cs:286`, `Assets/Game/Runtime/Avatars/AvatarPresentation.cs:170`.

The seated camera uses `Quaternion.Euler(Input.Pitch, WorldYaw, 0)`, keeping pitch relative to the world. The avatar mount inherits the seat's full pitch/roll, but `ApplyHead` multiplies that tilted rotation by a quaternion containing the same camera pitch. These are different coordinate conventions.

For a seat pitched 30 degrees, neutral camera pitch and zero relative yaw produce an avatar look direction 30 degrees away from the camera direction. Roll also mixes horizontal free look into vertical motion. Other clients therefore see incorrect head/eye aim when the cart tilts.

**Recommendation:** reconstruct the desired world look direction, transform it into the attached body's frame to apply anatomical limits, then transform the clamped direction back to world space. Retain attachment rotation for the body. No additional network payload is needed.

### 4. [P2] Blending defeats the advertised playback-speed clamp

**Locations:** `Assets/Game/Runtime/Avatars/AvatarAnimationGraph.cs:88` and `:162`.

Each clip's desired playback ratio is clamped to 0.65–1.8, but those ratios are converted to cycle frequencies and averaged into one shared phase. Sampling every clip from that phase makes its actual playback multiplier equal to `sharedFrequency * clip.length`, which need not remain within those limits.

With the supplied Chill settings, sustained forward movement at 6.5 m/s produces a 50/50 walk/run blend. A focused invocation of the production state code produced approximately 2.145 cycles/second: the 1.033-second walk clip plays at **2.216x**, while the 0.7-second run clip plays at **1.501x**. The walk contribution exceeds the 1.8x bound even with the shipped assets.

**Recommendation:** preserve synchronized gait phase, but constrain the resulting phase rate using the duration-adjusted bounds of participating clips. Define how incompatible cycle durations are handled when adding content. Verify the actual sampled rates for diagonal and walk/run blends, not only the intermediate ratios.

### 5. [P2] Disable/re-enable can permanently stop avatar presentation

**Locations:** `Assets/Game/Runtime/Avatars/AvatarPresentation.cs:61`, `Assets/Game/Runtime/Avatars/AvatarPresentationSystem.cs:39`, `Assets/Game/Runtime/Avatars/AvatarPresentationDemo.cs:84`.

`AvatarPresentation.OnDisable` calls `SetVisual(false)`, destroying instances and clearing the rendering request. `OnEnable` only resubscribes to content events; it never restores rendering or re-registers the host. Invoking that lifecycle on a temporary host left `visual` false after re-enable. Temporarily disabling a live remote host or its parent can leave it invisible until another caller explicitly configures visibility. The demo has the same restart problem because setup runs only in `Start`.

The global driver has a related defect: it elects the first entry in `systems` without checking `isActiveAndEnabled`. Disabling that system leaves it registered, so every other scene's driver returns early and no avatar/spring batch runs.

**Recommendation:** separate requested visibility from active registration. Restore eligible hosts on enable, and elect only enabled drivers or explicitly manage driver registration in enable/disable callbacks. Exercise this before introducing pooling or visibility-based feature policies.

### 6. [P2, performance] Each spring disposal rebuilds the entire shared batch

**Locations:** `Assets/Game/Runtime/Avatars/NativeSprings/AvatarSpringRuntimeProvider.cs:52`, `Assets/Game/Runtime/Avatars/AvatarPresentationSystem.cs:62`, `Assets/Game/Runtime/Avatars/AvatarPresentation.cs:236`.

Every provider `Dispose` queues a removal and immediately calls `ReconstructIfDirty(...).Complete()`. In this UniVRM implementation, reconstruction backs up all registered models, disposes the combined native buffers, and recreates them. Seven avatars losing springs or despawning together can rebuild the shrinking global batch seven times. A normal replacement can rebuild once for candidate registration and again for old-instance removal.

This is allocation and synchronization work proportional to the entire spring population, performed repeatedly on the main-thread path. It undermines the benefit of sharing the simulation service and will be most visible with spring-heavy content.

**Recommendation:** let the batch owner collect removals, keep departing buffers alive until UniVRM has backed them up, flush the combined reconstruction once at a safe frame boundary, and then dispose the removed buffers. Do not simply remove the current completion call: the comment correctly identifies a buffer-lifetime hazard that the replacement must preserve.

**Supporting package code:** `Library/PackageCache/com.vrmc.gltf@75b3f33fb8d1/Runtime/SpringBoneJobs/FastSpringBoneBufferCombiner.cs:43` and `:89`.

### 7. [P2] Failed reprocessing can leave existing assets out of sync

**Locations:** `Assets/Game/Editor/AvatarProcessor.cs:149`, `:160`, `:193`, and `:388`.

Processing overwrites an existing prefab before saving its matching settings and registry. The catch path restores settings/registry JSON and deletes newly created assets, but it does not restore the existing prefab. A failure after prefab save can leave a new skeleton paired with old generated measurements while reporting that processing failed.

Shared animation preparation has the same partial-commit issue. It reimports FBXs as it goes, temporarily removes root-motion baking for calibration, and saves the shared animation set independently. A failure partway through can leave some importers changed, including an unbaked calibration import, while the outer operation rolls back only other assets.

**Recommendation:** complete fallible checks before publication and make the existing prefab, settings, registry, animation set, and changed importer state part of a consistent commit/restore boundary. At minimum, restore any importer temporarily changed for measurement in a `finally` block. This belongs in the reusable processor, not a separate migration tool.

### 8. [P3, extensibility] State counts and clip assignments are duplicated as positional constants

**Locations:** `Assets/Game/Runtime/Avatars/AvatarAnimationGraph.cs:12`, `:64`, `:70`, `:128`, `:152`, `:171`; `Assets/Game/Editor/AvatarProcessor.cs:378` and `:416`.

The four-state count is repeated across arrays, transitions, mixer construction, and sampling. Clip meaning is separately encoded by filename order, numeric indices, and a catch-all `else draft.Seated = clip`. Appending another state or source file can silently overwrite Seated or leave part of the graph at the old size. `Avatar_Animation_States.md` accurately documents these traps, but documentation does not prevent them.

**Recommendation:** use a pose count sentinel and enum-based state ports, and make clip assignment exhaustive. A small fixed mapping is sufficient; a general animation framework is unnecessary. Keep the existing layer mixer as the future upper-body composition point.

## Additional performance and integration risks

- **The supplied content does not exercise springs.** Both processed prefabs contain `Springs: []`. Chill has one renderer/material slot and 3,536 triangles; Gerbil has one renderer/material slot and 7,422 triangles. These are inexpensive geometry examples, but their demo cannot establish spring correctness or the cost of representative seven-remote content. The setup guide discloses this; the plan's spring-motion acceptance still requires suitable content.
- **The registry eagerly references the whole catalog.** `AvatarRegistry.Entry.Source` and `Prefab`, plus `AvatarSettings.Generated.Source`, put every registered model and its dependencies in the directly referenced asset graph. Catalog growth can increase baseline loading and memory even with one selected avatar. Unity documents direct-reference assets as automatically loaded in its [direct-reference asset guide](https://docs.unity3d.com/Manual/assets-direct-reference.html). Keep processing-only metadata separate where practical; introduce explicit asset loading only when catalog size warrants it. Shared references do not imply duplicate texture copies by themselves.
- **Preparation is count-limited, not time-limited.** Only one candidate is instantiated per frame, but full synchronous instantiation, VRM/expression initialization, graph construction, and buffer rebuilds still create content-dependent spikes. A later frame can contain one avatar's initialization and another's instantiation. Measure worst replacement frames as well as steady-state cost before deciding whether pooling or asynchronous preparation is justified.
- **An active-avatar exception aborts the shared frame.** `AvatarPresentationSystem.LateUpdate` catches preparation errors but not active evaluation errors. A bad active clip, native constraint, or future input provider can prevent later hosts, the spring batch, and commits from running. Isolate a failed host and report once rather than allowing repeated exceptions to stall unrelated avatars.
- **Name clearance is not proportion-aware.** `AvatarPresentation.TryGetNameAnchor` uses head position plus 12% of visual height. For Gerbil's generated rest measurements, that is about 1.139 m above the sole while the bounds reach 1.500 m. The label can intersect a large head/ears. This follows the plan exactly, so the formula itself needs visual review rather than assuming implementation compliance guarantees readability.
- **Small avoidable recurring work remains.** `AvatarPresentation.UpdateInput` calls `Animations.IsComplete` every frame for every host, traversing all eight locomotion slots each time. Cache validity at bind/content-change boundaries. `AvatarSpringBatch.Retain` also performs a redundant scene search before obtaining UniVRM's singleton and introduces a Unity 6.5 obsolete-API warning for `FindFirstObjectByType`; this is startup cleanup, not a likely frame-time bottleneck.

## Performance and extensibility assessment

The current approach is a reasonable foundation for eight players: compact identity/look replication, cached skeleton references, no local full-body simulation, manual graph evaluation, two grounded probes per remote, and shared native spring scheduling. Those choices should remain. Fix failure propagation, coordinate handling, lifecycle restoration, and batched buffer retirement before adding more architecture.

There is no basis for claiming this is the most performant implementation without a representative multiplayer CPU/GPU profile. Use the plan's 16.67 ms total frame target and provisional 4 ms combined avatar CPU budget as acceptance thresholds, not achieved results. Review allocations, native memory, and worst swap frames alongside averages. Existing feature switches provide useful future policy boundaries once their lifecycle is reliable; speculative LOD tiers or a replacement IK solver are not justified by this review.

## Required visual and performance validation

1. From another client, observe walking, running, backward/sideways/diagonal movement, jumping, ledge falls, landing, and both avatars at different configured heights. Inspect cadence and foot sliding during walk/run blends.
2. Observe seated free look while a cart pitches and rolls. Compare the remote head/eye direction with the seated player's actual view. Check carrying, release previews, and reattachment separately.
3. Swap repeatedly in idle, motion, air, seated, and carried states. Check simultaneous hand/foot IK, label clearance, capsule fallback, and preservation of equipped items. Disable/re-enable hosts and the presentation driver.
4. Include an avatar with real spring chains. Inspect motion through swaps, teleports, feature toggles, and simultaneous despawns. Exercise malformed replacement content and confirm the working avatar remains visible.
5. Profile an eight-player session with all seven remote avatars nearby at 1920×1080, including representative spring-heavy models. Use `Avatar.*` markers, record CPU/GPU frame times and worst replacement frames, and watch managed/native memory through repeated swaps.
