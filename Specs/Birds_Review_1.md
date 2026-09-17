# Bird system review — Stage 1-3 integration changes

## Bugs and correctness issues

### 1. `AfterPhysics` host guard removed — host runs bird sweeps twice

`WorldItemRegistry.AfterPhysics` originally skipped the host (`if (!worldReady || Replaying || IsHost) return;`). The diff removes the `IsHost` guard so clients and host both call `item.AfterPhysics(delta)`, which calls `AfterBirdPhysics`. But on the host, `BeforePhysics` also runs (it was never host-gated), so the host already calls `BeforeBirdPhysics` + `AfterBirdPhysics` through `BeforePhysics`/`AfterPhysics` in its physics callbacks — and now also through the `AfterPhysics(float delta)` callback again.

For non-bird physics segments, the host previously relied on `OnCollisionEnter` (which checks `IsHost`), so the `AfterPhysics` skip was fine. Now that the bird path needs `AfterPhysics` on the host too, the non-bird part of `AfterPhysics` (`physicsContactSampled` segments) will also execute on the host. `physicsContactSampled` is set from `Predicted` in `BeforePhysics`, and host items are never predicted, so the early return at `if (!physicsContactSampled) return` should prevent actual duplicate work for existing player-contact segments. The bird path in `AfterBirdPhysics` guards with `if (!birdSampling) return`, and `birdSampling` is set in `BeforeBirdPhysics` which clears it at the end of `AfterBirdPhysics` — so the second call from the registry `AfterPhysics` should no-op. **This is safe but fragile.** If `birdSampling` is ever not cleared (e.g. an exception), the second call produces duplicate sweeps. Consider re-adding the host guard and calling `AfterBirdPhysics` through a separate path.

**Severity: low (currently safe, architecturally fragile)**

### 2. `BirdHitReporter.Sweep` — cart initial-overlap check breaks out of wrong loop

`BirdHitReporter.Sweep` line ~140: `if (source.Cart && cursor == fromTick && ...) break;` — this `break` exits the inner `while (cursor < toTick)` loop, skipping the rest of the sweep for *that one bird*. This is the intended behavior (skip already-overlapping birds for carts). However, the outer `foreach (uint life in candidates)` continues to the next bird. This is correct.

**No bug here.**

### 3. `SampleBirdContacts` — `birdTravel` is populated by physics callbacks, consumed in `SampleBirdContacts`, but also consumed in `AfterBirdPhysics`

`AfterBirdPhysics` iterates `birdTravel` and sweeps birds (line 62-63 of WorldItem.Birds.cs), then in `SampleBirdContacts` (called from LateUpdate), the same `birdTravel` is iterated again (lines 78-79) if `IsHost || Predicted`. Since `AfterBirdPhysics` sets `birdSampling = false` but does NOT clear `birdTravel`, the physics-generated segments are swept twice — once in `AfterBirdPhysics` and again in `SampleBirdContacts`.

Looking more carefully: `AfterBirdPhysics` adds the final segment to `birdTravel`, sweeps, then sets `birdSampling = false` but does NOT clear `birdTravel`. Then `SampleBirdContacts` checks `if (registry.IsHost || Predicted)` and re-iterates `birdTravel`. The clear happens at the bottom of `SampleBirdContacts` (line 112: `birdTravel.Clear()`).

This means the physics segments from `AfterBirdPhysics` are swept twice against birds. The second sweep in `SampleBirdContacts` will produce duplicate contacts for the same birds. Since `predicted` is checked before adding contacts, a bird killed in the first sweep won't be hit again, but a bird that was only weakly hit (scare) could generate a duplicate scare report.

**Severity: medium — duplicate sweeps for the same segments cause redundant scare reports and wasted computation.**

**Fix:** Clear `birdTravel` at the end of `AfterBirdPhysics` (after the sweep loop), or skip the `birdTravel` iteration in `SampleBirdContacts` when it was already consumed.

### 4. `ReceiveHits` — `scareLives` shared mutable list across re-entrant `ReceiveScare`

`BirdRegistry.Events.cs` line 196-197: When a weak hit occurs, `scareLives.Clear()` then `scareLives.Add(hit.Life)` then `ReceiveScare(...)` is called. `scareLives` is a class-level field (line 205). If `ReceiveScare` is called while already iterating `report.Hits` in the outer `ReceiveHits` loop, and `ReceiveScare` internally modifies anything that could trigger another `ReceiveHits`... it won't, because `ReceiveScare` only publishes. But the `scareLives` list is passed into the `BirdScareReport` struct. Since the struct captures the *reference*, and the next iteration of the outer `foreach (var hit in report.Hits)` will call `scareLives.Clear()` again for another weak hit, the first scare report's `Lives` list gets mutated.

However, `ReceiveScare` iterates `report.Lives` synchronously in the same call, so by the time it returns, it has already processed all entries. The list isn't retained. **This is safe but brittle** — if `ReceiveScare` ever defers processing, the shared list would corrupt.

**Severity: low (safe today, risky pattern)**

### 5. `PickupRegistry` — missing `BirdRegistry` causes session leave instead of allowing empty bird worlds

`PickupRegistry.OnStartServer/OnStartClient`: If `BirdRegistry.Instance` is null, the code calls `SessionController.Instance.Leave(...)`. The plan states "An empty catalog and no spawn zones support an empty bird world," but this code requires `BirdRegistry` to exist on SessionRoot. This is intentional per `Birds_Setup.md`, but the error message ("Add BirdRegistry and its settings/catalog references to SessionRoot") will trigger if someone hasn't set up birds yet. This is a deliberate hard requirement — sessions won't start without the bird registry.

**Severity: design choice (intentional, but worth noting)**

### 6. `BirdClock.Read` — monotonic guarantee can fail on first large negative correction

Line 164 of BirdMotion.cs: `seconds = Math.Max(seconds, predicted + Math.Clamp(error, ...))`. The slew rate is `elapsed * 0.1`, so a 10% correction per sample. If the synchronized clock jumps backward significantly (e.g., FishNet tick correction), the clock's `seconds` never decreases because of `Math.Max(seconds, ...)`. This is correct for monotonicity. However, if the initial `seconds` value is set from a bad first sample (e.g., before FishNet synchronization stabilizes), the clock could be permanently ahead. The `Reset()` method exists to handle epoch changes, but mid-session clock jumps could leave the bird clock permanently offset.

**Severity: low (FishNet tick sync is generally stable after connection)**

### 7. `ContentFingerprint` — `unchecked` block scope

`BirdRegistry.cs` line 251: The `Number` local function uses `unchecked { hash = (hash ^ (uint)value) * 16777619; }`. The `unchecked` keyword is inside the lambda, so it correctly prevents overflow exceptions. However, the cast `(uint)value` for negative ints will produce different results on different platforms only if `int` size differs, which it doesn't in C#. **This is fine.**

**No bug.**

## Performance concerns

### 8. `ChoosePerch` iterates all perches every time

`BirdRegistry.Planning.cs` line 237: `foreach (var perch in perches.Values)` — this iterates every perch in the world for every destination selection. With potentially thousands of perches, this is O(N) per decision. The plan mentions building a habitat/perch lookup once at world start; this linear scan should be replaced with a biome-indexed lookup.

**Severity: medium for scale — acceptable for initial implementation with modest perch counts, but will become a bottleneck with hundreds of perches and frequent replanning.**

### 9. `ChooseHabitat` iterates all habitats every time

Same pattern as perches — `foreach (var habitat in habitats.Values)`. Habitats are fewer, so this is less concerning.

**Severity: low**

### 10. `WaterAt` and `WaterCrossing` iterate all habitats per body per frame

`BirdRegistry.Presentation.cs` lines 92-110: These are called from `BirdBody.Step` which runs every frame for every active body. Each call iterates all habitat volumes. With 32 active bodies and many habitats, this adds up.

**Severity: low-medium (bounded by BodyLimit of 32, but still per-frame work)**

### 11. `BirdHitReporter.EnsureGrid` rebuilds the entire grid on any change

Every `Dirty()` call marks the grid for full rebuild. Any spawn, death, plan change, or event triggers this. The rebuild iterates all live records and populates a spatial hash. With 300 birds, this is 300 records * multiple cells each. Since `Dirty()` is called frequently (every event publish, every hit confirmation), the grid could rebuild multiple times per tick.

The grid is lazily rebuilt on the next `Query()` call, so multiple `Dirty()` calls between queries are fine. But if `Query()` is called multiple times per frame (once per active rock, once per cart, once per threat), the rebuild happens only once thanks to the `gridDirty` flag. **This is acceptable.**

**Severity: low (lazy rebuild is efficient enough)**

### 12. Per-rock `SampleBirdContacts` in LateUpdate — O(rocks * birds) worst case

Every active world item calls `SampleBirdContacts` in LateUpdate. For non-rock items, `BirdEligible` returns false quickly (the `birdRock` check). For rocks, each one does a spatial query and sweep. With 64 moving rocks and 300 birds, this could be significant, but the spatial grid keeps per-rock candidate counts manageable.

**Severity: low (spatial grid mitigates)**

## Network message analysis

### 13. Route message sizes are within budget

Analyzing the serializer:
- **Hold route:** 4 (revision) + 4 (startTick) + 1 (kind) + 1 (activity) + 12 (A) + 4 (facing) + 2 (habitat) + 2 (perch) = **30 bytes**
- **Curve route:** 30 + 4 (seconds) + 7-13 (B relative) + 7-13 (C relative) + 7-13 (D relative) + 4 (takeoff) + 4 (landing) + 4 (orbitRadius) = **67-85 bytes** typical
- **Orbit route:** 30 + 4 (seconds) + 13 (B absolute) + 13 (C absolute) = **60 bytes**
- **Surface route (9 points):** 30 + 4 (seconds) + 1 (count) + 9 * 7 (relative points) = **98 bytes**

These are within the plan's ~64-96 byte target. Curve routes with full-position control points (> 327.6m offset) would use 13 bytes each instead of 7, pushing to ~103 bytes.

**BirdRecord** baseline: 4 (life) + 4 (revision) + 2+2+2+2+2 (species/zone/biome/occupied/reserved) + 1 (interrupt) + 4 (fleeAt) + route + 1 (hasNext) + optional route = **~52-110 bytes per record.** A 300-bird baseline at ~80 bytes average = ~24 KB, sent in MTU-bounded chunks at 16 KiB/s pacing. This is reasonable.

**BirdEvent (Plan):** 4+4+1 (header) + 4 (life) + 4 (revision) + 4 (route revision) + 2+2 (occupied/reserved) + 1 (interrupt) + 4 (fleeAt) + 1 (hasNext) + optional route = **~27-95 bytes.** Lean.

**BirdEvent (Death):** 4+4+1+4+2+12+4+4 = **35 bytes.** Very compact.

**Digest entry:** 4+4+4+4 = **16 bytes** (plan says 8-12; actual is 16 due to uint fields). With 8 entries per digest: 8+4+4 (header) + 8*16 = **144 bytes** unreliable. This is slightly over the 8-12 bytes per entry budget but still well within the 2-second staggered cadence target.

**Severity: the digest entries are 16 bytes vs the planned 8-12. Consider using packed uint16 for the `Effective` field (tick delta from sequence) to shrink entries if traffic becomes tight.**

### 14. `BirdDigestEntry.Effective` is a uint but stores a double-precision tick

`BirdDigest` entry's `Effective` field is `uint`, but route `StartTick` is also `uint` — so this is consistent. The digest comparison in `ReceiveDigest` only checks revision mismatch, not effective tick. The `Effective` field is serialized but never read on the client side. It's dead data in the current implementation.

**Severity: low — wasted 4 bytes per digest entry. Either use it for mismatch detection or remove it.**

### 15. Scare report and hit report `Lives`/`Hits` lists use default FishNet collection serialization

The `BirdScareReport.Lives` and `BirdHitReport.Hits` use `List<uint>` and `List<BirdHit>` with FishNet's default collection serializer. FishNet writes a length prefix + elements. `BirdHit` is 4+4+4+4+4+12 = **32 bytes** per hit. With the plan's 20-32 byte target per hit entry, this is at the upper bound. The `Revision` field (4 bytes) and `Tick` field (4 bytes) could potentially be eliminated or packed.

**Severity: low — within budget**

### 16. No custom serializers registered for `BirdEvent`

`BirdEvent` has custom `WriteBirdEvent`/`ReadBirdEvent` extension methods, but FishNet uses these only if they're registered or if the struct is annotated. FishNet's auto-serialization will find extension methods named `Write<TypeName>`/`Read<TypeName>` on `Writer`/`Reader`. The methods are named `WriteBirdEvent` and `ReadBirdEvent` — FishNet expects `WriteBirdEvent` and `ReadBirdEvent` on `Writer`/`Reader` as extension methods, which these are. **This should work with FishNet's code generation.**

However, `BirdBaselineChunk.Records` uses `List<BirdRecord>` — FishNet will use the extension methods `WriteBirdRecord`/`ReadBirdRecord` for element serialization. **This relies on FishNet's generated collection serializer finding the custom element serializer.** If FishNet doesn't pick these up, records would serialize with default reflection-based serialization, which would be correct but larger and slower.

**Severity: needs verification — confirm FishNet code generation picks up the extension methods. If not, register them explicitly.**

## Room to grow

### 17. `BirdRoute` struct — no version/kind expansion byte

The route serializer uses `BirdMotionKind` as a single byte, which supports 256 kinds. Adding new route kinds (e.g., dive, spiral, perch-hop) is straightforward. The serializer's structure is kind-dependent, so new kinds can have entirely different payloads without breaking backward compatibility — as long as old clients don't receive new kinds. This is handled by the protocol version bump.

**Good — room to grow.**

### 18. `BirdRecord` has no reserved fields

The record struct and its serializer are tightly packed. Adding new fields (e.g., animation state, sound state) requires a protocol version bump. This is expected and fine.

### 19. `BirdEvent` uses a flexible kind byte

Only 3 kinds today (Spawn, Plan, Death). 253 more available. New event kinds can carry different payloads via the custom serializer. **Good.**

### 20. `BirdHitReport` has space for extension

The `Cart` bool could be promoted to a source-kind enum (byte) to support future threat sources (e.g., explosions, environmental hazards) without adding more bool flags.

**Suggestion for future — not blocking.**

## Other observations

### 21. `ReceiveRecord` requests full baseline on unknown life

`BirdRegistry.Network.cs` line 279: If a repair response arrives for a life that doesn't exist locally, it requests a full baseline. This could cascade: if a life was removed between the repair request and response, the full baseline request is unnecessary. Consider simply ignoring unknown lives in repair responses instead of re-baselining.

**Severity: low — rare edge case, but could cause unnecessary baseline traffic during mass deaths.**

### 22. `body.excludeLayers` assignment in `BirdBody.Rent` uses bitwise NOT on a LayerMask

Line 52: `body.excludeLayers = ~settings.SolidMask.value | LayerMask.GetMask(...)`. The `~` inverts the SolidMask, meaning the body collides ONLY with layers in SolidMask (minus the explicitly excluded layers). This is correct — bodies should only interact with scenery. But `LayerMask.value` is an int, and `~` on an int inverts all 32 bits. The OR with the explicit exclusions is additive. This works correctly because `excludeLayers` uses a bitmask where 1 = excluded.

**No bug — just dense.**

### 23. `BirdPerchVolumeEditor.Generate` — `FindObjectsByType` across all scenes

Line 32: `FindObjectsByType<BirdPerch>(FindObjectsInactive.Include, FindObjectsSortMode.None)` finds perches across all loaded scenes, not just the volume's scene. The subsequent scene check (line 34: `if (perch.gameObject.scene != volume.gameObject.scene) continue`) filters correctly, but the initial query could be expensive in multi-scene setups.

**Severity: negligible (editor-only, not runtime)**

### 24. `OnCollisionStay` added to WorldItem for bird scenery contacts

WorldItem.Birds.cs line 52: `private void OnCollisionStay(Collision collision) => BirdSceneryContact(collision);`. This means every physics frame where a rock is touching scenery (e.g., resting against a wall), `OnCollisionStay` fires and calls `BirdSceneryContact`. The guard `if (!birdSampling)` returns early, and `birdSampling` is only true during the physics step window. However, `OnCollisionStay` itself has overhead from Unity's callback dispatch, even if the method body returns immediately.

For sleeping rocks, `Body.IsSleeping()` prevents `birdSampling` from being set, so the callback shouldn't fire (Unity doesn't fire collision callbacks for sleeping bodies). **Acceptable.**

**Severity: negligible**

## Summary

| # | Issue | Severity | Category |
|---|-------|----------|----------|
| 3 | Double sweep of physics segments in `birdTravel` | Medium | Bug |
| 8 | `ChoosePerch` linear scan of all perches | Medium | Performance |
| 14 | `BirdDigestEntry.Effective` unused on client | Low | Network waste |
| 1 | `AfterPhysics` host guard removal is fragile | Low | Architecture |
| 4 | `scareLives` shared mutable list in ReceiveHits | Low | Architecture |
| 10 | `WaterAt`/`WaterCrossing` per-frame habitat scan | Low-Medium | Performance |
| 16 | FishNet custom serializer pickup needs verification | Low | Network correctness |
| 21 | Full baseline request on unknown repair life | Low | Network efficiency |
| 13 | Digest entries 16 bytes vs planned 8-12 | Low | Network budget |
