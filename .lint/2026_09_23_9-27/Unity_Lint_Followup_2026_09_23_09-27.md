# Unity Lint Follow-up

Source: [Unity lint report](Unity_Lint_Report_2026_09_23_09-27.md).

These recommendations require a decision, profiling, or compatibility checks before implementation.

## Diagnostic compilation — finding #2

Replace `DEVELOPMENT_BUILD` consistently across diagnostic types, implementation blocks, and `Conditional` attributes:

- Use `UNITY_INCLUDE_INSTRUMENTATION` for logging and profiling.
- Use `UNITY_ENABLE_CHECKS` for assertions and safety checks.
- Use `Debug.isDebugBuild` where behavior must depend on a development player rather than the managed code variant.

Recommended build policy: Checked for development and Release for shipping. Confirm whether diagnostic tooling should also be available in Instrumented players before migrating. Check both development and release players for compilation, diagnostic availability, and logging behavior.

Reference: [Unity 6.6 migration guidance](https://docs.unity.com/en-us/engine/6000.6/manual/upgrade-guides/upgrade-guide-unity66).

## Static batching and SRP Batcher — finding #3

Compare the current configuration against static batching disabled in Steam-target scenes. Use the Frame Debugger and CPU/GPU profiling to choose the configuration. Keep the current settings until measurements justify a change.

## Physics query allocations — finding #4

Profile active potion flight and simultaneous potion blasts and slingshot shots. If allocations warrant a change, replace allocating queries with reusable buffers that grow and retry when full.

Preserve every required hit, current filtering, nearest-hit selection, and blast obstruction checks. Fixed buffers without an overflow strategy can silently omit targets or blockers. Account for nested query use when deciding buffer ownership.

Relevant paths:

- `SlingshotAim.Direction`
- `WorldItem.AfterPotionPhysics`
- `PotionArea.Seed`
- `WorldItemRegistry.ApplyBlast`

## Collision mesh prebaking — finding #5

Identify the mesh colliders included in shipping scenes. Compare initialization time with collision prebaking enabled against build size and build time. Treat built-in and runtime-created geometry separately where necessary; enabling the setting may not resolve every observation.

## Generated first-person arm mesh readability

Confirm that runtime skinning, tattoo processing, collision, and other downstream consumers do not require CPU mesh data. If compatible, disable readability in the generation pipeline after all CPU processing is complete and regenerate the affected assets. Compare memory usage and inspect both avatars' first-person arms.

## Collision callbacks and loading systems

Keep gameplay collision callbacks until profiling dense cart, player, and item contacts demonstrates a problem. Any optimization must preserve contact-state updates.

Keep the bounded controller-glyph Resources library and existing loading settings unless memory or loading measurements justify a change. Do not disable domain reload solely to clear lint advice; static initialization depends on its semantics.

## Small optimization and cleanup suggestions

- Leave multidimensional arrays unchanged unless profiling identifies a meaningful cost; most reported accesses are Editor-only and the runtime table is small.
- Leave multiplication order unchanged without a demonstrated benefit. Reordering changes floating-point rounding and can affect simulation results.
- Treat boxing findings according to execution frequency; cached allocations, tooling, and logging paths are not automatically per-frame problems.
- Leave existing unused members in place under the repository's cleanup rules. `HudController.GetItemName` is a cleanup candidate, but Unity callbacks, serialized fields, FishNet serializers, and RPC parameters require special care.
- Preserve calls to `PlayerHeldItemPresentation.CurrentAge()`: the method updates state even though callers ignore its return value.
- Keep naming, namespace, and other style-only findings out of this remediation scope.

## Contract-dependent and third-party findings

Do not alter guarded null handling, intentional integer row indexing, mutable appearance hashing, or readonly Playable wrappers solely to silence warnings without a demonstrated defect.

Review compatible vendor/package updates before editing vendor shaders. Inspect avatar materials and TextMesh Pro rendering when making such updates. Do not automatically downgrade dependencies or change import settings without confirming shipping usage and runtime requirements.

## User validation for future changes

Inspect cart rendering and collisions, potion contacts and blast obstruction, slingshot targeting, first-person arms and tattoos, controller glyphs, and TextMesh Pro text as applicable. For performance changes, compare Profiler measurements in representative Steam-target scenes; for diagnostic migration, check development and release players.
