# Birds final review

Findings and recommended fixes for bird simulation, collision handling, networking, scheduling, and presentation.

P1: core gameplay or session failure. P2: functional or scaling issue to address before acceptance. P3: lower-priority efficiency issue.

## Findings

### 1. [P1] Bird hit bodies do not enable continuous collision detection

**Location:** [BirdRegistry.Physics.cs:72](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Birds/BirdRegistry.Physics.cs:72).

`CreateHitShape` adds a kinematic Rigidbody but leaves its collision detection mode at the default `Discrete`. The rock uses Continuous Dynamic, but Unity documents that its continuous detection against other Rigidbodies requires continuous detection on those bodies too. Merely subscribing to `ContactModifyEventCCD` does not enable CCD for this pair. Rock hits rely on native contacts, so a fast rock can cross a small bird between physics samples without a hit or rebound. At the configured 50 m/s rock cap and 60 Hz simulation, the rock travels about 0.83 m per step. This risk follows from the configured modes and [Unity's Continuous Dynamic contract](https://docs.unity3d.com/6000.5/Documentation/ScriptReference/CollisionDetectionMode.ContinuousDynamic.html).

**Action:** configure a supported continuous collision strategy for both sides of the rock/bird pair. Kinematic bodies support Continuous Speculative; account for its possible speculative contacts when deciding which contacts are actual hits. Keep native scenery/ricochet ordering and exercise small moving targets at maximum relative speed. See [Unity's Rigidbody collision-mode guidance](https://docs.unity3d.com/6000.5/Documentation/ScriptReference/Rigidbody-collisionDetectionMode.html).

### 2. [P1] Late joiners cannot catch up above approximately 27 live events per second

**Location:** [BirdRegistry.Network.cs:102](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Birds/BirdRegistry.Network.cs:102), especially the event-credit loop at line 126.

Transfers receive 16,384 bytes of credit per second, but each queued event consumes a fixed 600 credits regardless of its serialized size. Catch-up is therefore capped at approximately 27.3 events/second. Snapshot delivery also postpones draining live events until all chunks have been sent. A 300-bird world producing 30 or more plans/second can grow the queue faster than it drains. Completion requires an empty queue, leaving the joining player loading until the session timeout. Restarting the snapshot does not fix that rate imbalance.

**Action:** charge the actual serialized event size plus framing and share transfer bandwidth between snapshot chunks and retained events. Preserve sequence ordering and atomic installation; increasing the loading timeout alone cannot solve this.

### 3. [P2] Rejected pickup can erase a locally simulated rock's velocity

**Location:** [WorldItem.cs:167](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Items/WorldItem.cs:167), [WorldItemRegistry.cs:461](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Items/WorldItemRegistry.cs:461).

`PredictPickup` calls `StopBody`, which zeros velocity and makes the body kinematic. If pickup is rejected while the rock remains assigned to this client, `Rollback` reinitializes it from the saved record. `ApplyRecord` nevertheless sets `preserveMotion` because the simulator, release, and motion revision still match. It makes the body dynamic but skips `CorrectBody`, leaving the stopped velocity in place instead of restoring the saved motion. A rejected pickup caused by optimistic inventory state can therefore make an airborne rock lose its trajectory, and that client subsequently publishes the incorrect motion to everyone.

**Action:** restrict motion preservation to an uninterrupted active simulation. Rollback from an optimistic pickup must restore the saved position and velocities explicitly.

### 4. [P2] Correction overlaps are not consistently excluded from physical contacts

**Location:** [BirdRegistry.Physics.cs:61](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Birds/BirdRegistry.Physics.cs:61), [BirdRegistry.Physics.cs:105](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Birds/BirdRegistry.Physics.cs:105), [WorldItem.Birds.cs:33](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Items/WorldItem.Birds.cs:33).

When a late route, repair, or clock adjustment changes a bird's current position, `PrepareRockPhysics` teleports its hit body to the new `from` position. It does not rebase the contact sets of nearby rocks. A correction that places a bird sphere into a moving rock consequently becomes a fresh physical collision and potentially a lethal report. The view can still be blending its correction for 100 ms, making the contact visibly disconnected from the bird.

Rock-side rebasing has a related gap: `RebaseRockContacts` suppresses the subsequent gameplay callback through `touchingBirds`, but contact modification never consults that suppression. The solver can still apply restitution/depenetration from a correction-created overlap. The callback then returns before marking a motion boundary, so the synthetic trajectory change is also omitted from reliable boundary delivery.

**Action:** distinguish continuous movement from installation/correction on both the bird and rock. Rebase pair membership for correction-created overlaps and apply the corresponding suppression before the native solve, keeping suppression until physical separation. Make the data available safely to the contact-modification callback.

### 5. [P2] Rock death effects use the beginning of the tick instead of the impact pose

**Location:** [BirdRegistry.Physics.cs:119](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Birds/BirdRegistry.Physics.cs:119), [BirdRegistry.Physics.cs:132](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Birds/BirdRegistry.Physics.cs:132).

The cached `RockContact` contains speed and normal but no contact position or within-step time. `ReportRockContact` reconstructs the bird pose at `physicsTick`, even though the hit body moved from that tick to the next during the solve. A bird struck near the end of the step thus produces its corpse and feathers behind the impact. The displacement can approach one tick of bird travel, before any presentation correction; 15 m/s gives 25 cm at 60 Hz, which is substantial for a small target. That incorrect position is then broadcast as the accepted death pose.

**Action:** retain numerical contact geometry sufficient to recover the bird center at impact, or retain the actual within-step impact pose/time. Use that same accepted pose for local prediction and remote death effects instead of evaluating the route at the start of the tick.

### 6. [P2] Motion boundaries omit scenery ricochets

**Location:** [WorldItem.Birds.cs:41](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Items/WorldItem.Birds.cs:41), [WorldItem.cs:745](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Items/WorldItem.cs:745), [WorldItemRegistry.Motion.cs:65](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Items/WorldItemRegistry.Motion.cs:65).

`MotionBoundary` is set for a new bird contact, and the publisher additionally recognizes sleep/wake/removal. A bounce against scenery does neither. After a bird ricochet, a subsequent wall or ground ricochet can therefore keep the same `Path` and rely solely on ordinary unreliable snapshots. Observers interpolate across the direction change or briefly extrapolate the abandoned path; the local-player shove sweep can also consume that artificial interpolation segment. This leaves the ordered-bounce mechanism incomplete for the bird/scenery chains it is intended to support.

**Action:** publish a reliable boundary and increment the path when a simulated rock undergoes a scenery impact that changes its trajectory. Do not generate a new boundary for every resting `OnCollisionStay` callback.

### 7. [P2] Cart hit sweeps use a frame clock for physics intervals

**Location:** [BirdHitReporter.cs:243](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Birds/BirdHitReporter.cs:243), [BirdHitReporter.cs:270](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Birds/BirdHitReporter.cs:270), [BirdMotion.cs:155](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Birds/BirdMotion.cs:155).

`CartBefore` captures `registry.Now`, and `CartAfter` tests birds over that value through one additional tick. `BirdClock.Read` advances through `Time.unscaledTimeAsDouble`, which returns the same value within a rendered frame. FishNet can simulate multiple ticks in that frame, so consecutive cart segments reuse the same bird interval. At 30 FPS with 60 Hz physics, crossing birds can be missed or hit against the wrong target movement. Unity documents the [frame-constant time value](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Time-unscaledTimeAsDouble.html); the local catch-up loop is in [TimeManager.cs:721](C:/Users/spenc/source/repos/2birds/Assets/FishNet/Runtime/Managing/Timing/TimeManager.cs:721).

**Action:** use the actual simulation start/end ticks for cart sweeps, with a consistent mapping into bird time. Keep the smoothed clock for presentation and exclude replay and recovery teleports.

### 8. [P2] Remote scare eligibility can remain stuck after an escape

**Location:** [BirdRegistry.cs:162](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Birds/BirdRegistry.cs:162), [BirdRegistry.cs:207](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Birds/BirdRegistry.cs:207), [BirdHitReporter.cs:207](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Birds/BirdHitReporter.cs:207).

Only the host runs `AdvanceClaims`, which promotes queued routes and changes `WaitingToFlee` to `Fleeing` and then `Calm`. Those timed changes do not change the record revision or publish an event. Clients evaluate the correct route through `BirdMotion.Current`, but keep the old interrupt state. `Threat` excludes every non-Calm record, including horn targets. If the host subsequently cannot find a normal route, no new plan delivers its Calm state, and remote players, carts, rocks, and horns can stop scaring that bird indefinitely. Equal revisions prevent digest repair; client occupancy/reservation fields remain stale as well.

**Action:** evaluate shared timed phases on clients too, while retaining claim arbitration on the host, or replicate the phase transitions explicitly. Use that phase consistently for scare eligibility and expired-route recovery.

### 9. [P2] Host relay splits every client motion batch into individual broadcasts

**Location:** [WorldItemRegistry.Motion.cs:17](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Items/WorldItemRegistry.Motion.cs:17).

`ReceiveSimulatorMotion` adds one accepted motion and immediately calls `FlushMotion` inside the loop. An incoming eight-rock batch becomes eight outgoing application broadcasts, each repeating its header and serialization/dispatch work for the observer set. Sixty-four remotely simulated moving rocks at 20 Hz produce up to 1,280 such broadcasts per second instead of 160 full batches. Transport aggregation can combine datagrams, but it does not remove the repeated application headers or per-message work. The relay also sends the simulator its own motion, which it immediately discards.

**Action:** collect accepted entries and flush at `BatchSize` or the end of the incoming batch, preserving its delivery channel. Exclude the source simulator from the relay where practical.

### 10. [P2] Mass scares bypass the route-planning budget

**Location:** [BirdRegistry.Events.cs:105](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Birds/BirdRegistry.Events.cs:105), [BirdRegistry.Planning.cs:205](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Birds/BirdRegistry.Planning.cs:205).

Normal planning handles one decision per tick, but `ReceiveScare` immediately calls `BuildRoute` for every reported life. A 64-life horn batch can perform 64 route searches and hundreds of scenery casts, plus destination scans and endpoint checks, in one callback. Some soaring routes check both a curve and its continuation. Multiple batches from the same honk have no shared budget, concentrating expensive work on the frame when scare feedback should be responsive.

**Action:** register the scare/deadline immediately and service route requests through a bounded planner that respects deadlines. Reuse escape retries, deduplicate repeated reports, and provide an explicit solution for zero-delay scares rather than silently extending the configured delay.

### 11. [P2] Unfillable vacancies can starve all living-bird planning

**Location:** [BirdRegistry.cs:164](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Birds/BirdRegistry.cs:164).

A due vacancy consumes the tick's planning slot even when placement fails. Living birds receive a decision only when no vacancy was attempted. With the two-second retry interval and 60 Hz ticks, roughly 120 continuously unfillable vacancies can occupy the entire scheduler. A 300-bird target with insufficient perch capacity can leave existing birds holding forever and failed escapes retrying indefinitely without service.

**Action:** share the bounded budget fairly between population placement, escape retries, and normal decisions. Reserve service for existing lives while failed vacancies remain queued.

### 12. [P2] Every bird change invalidates the entire spatial grid

**Location:** [BirdRegistry.Network.cs:176](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Birds/BirdRegistry.Network.cs:176), [BirdHitReporter.cs:47](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Birds/BirdHitReporter.cs:47).

Every spawn, plan, death, or repair marks the whole grid dirty. The next query clears all buckets and reinserts every living route, including unchanged idle birds. A route can occupy up to 512 cells before using the large-route fallback. Laziness combines invalidations only until the next query: host threat queries can publish scares synchronously, dirty the grid again, and cause the next source to rebuild it in the same frame. Cart and scare queries incur this scaling cost.

**Action:** track and update the cells belonging to each changed life, removing entries on death and retaining pooled buckets and the large-route fallback. At minimum, prevent interleaved sources from repeatedly rebuilding unchanged records in one frame.

### 13. [P2] Distant deaths create cosmetics and evict nearby corpses

**Location:** [BirdRegistry.Presentation.cs:62](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Birds/BirdRegistry.Presentation.cs:62).

Living views respect `ViewDistance`, but every death rents or instantiates a body regardless of distance or whether the bird had a view. Kills across the map can create rigidbodies, feathers, audio work, and pool churn on every client. At the body cap, `ReturnBody(0)` removes the oldest corpse even if it is visible nearby, cutting short its configured lifetime and shrink.

**Action:** apply local presentation relevance before creating death cosmetics while always committing shared death and reward state. Prefer retaining visible nearby bodies when enforcing the cap.

### 14. [P3] Destination and water queries repeatedly scan unrelated authoring data

**Location:** [BirdRegistry.Planning.cs:233](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Birds/BirdRegistry.Planning.cs:233), [BirdRegistry.Presentation.cs:93](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Birds/BirdRegistry.Presentation.cs:93).

Every destination search scans all perches or habitats before filtering biome/type. Every non-sinking corpse also calls `WaterCrossing` each rendered frame; this can scan the habitat collection twice and continues for settled bodies. The body cap bounds the corpse count, but increasing authored perch/habitat counts still multiplies work unrelated to the nearby bird or corpse. Mass scares amplify the destination scans described in finding 10.

**Action:** build biome/type lookups once during world initialization and retain a water-only candidate set or spatial lookup for corpses. Skip repeated crossing checks for a stationary settled body where the water surface cannot change. Prioritize this according to actual content scale.

### 15. [P3] Unused and redundant wire fields consume growth headroom

**Location:** [BirdMessages.cs:21](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Birds/BirdMessages.cs:21), [BirdRegistry.Network.cs:249](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Birds/BirdRegistry.Network.cs:249), [BirdRegistry.Events.cs:148](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Birds/BirdRegistry.Events.cs:148).

The digest sends `Next` and `Effective`, but reception checks only life and record revision. Hit handling ignores `BirdHit.Revision` and `Tick`; scare handling ignores `Event`, `Source`, and `Kind`; death-event application ignores `Player` and `Kills`. Surface routes send `Surface[0]` even though it equals `A`. Hit reports also serialize both cart attribution and rock-release fields regardless of source kind.

**Action:** use fields needed for phase/repair semantics and remove fields without a consumer. Reconstruct the first surface point from `A` and serialize only attribution fields for the selected source kind. Retain hit-result `Contact`, which distinguishes competing predictions. Make wire-format changes with the corresponding protocol bump.

## Network size and growth

FishNet's [WriteUInt32 implementation](C:/Users/spenc/source/repos/2birds/Assets/FishNet/Runtime/Serializing/Writer.cs:428) packs integers, so encoded sizes depend on their values. Wire-size accounting must use packed widths rather than assuming four bytes per integer.

The following application-payload estimates assume a five-byte epoch, three-byte sequence/start tick, two-byte life, one-byte revisions, zero scare deadline, and compact point offsets for typical plans. They exclude FishNet and transport framing.

| Payload | Application bytes |
| --- | ---: |
| Typical plan with a queued orbit | 64 |
| Typical plan with a queued flight curve | 83 |
| Typical plan with a queued nine-point surface route | 114 |
| Hit report with 12 entries, maximum packed integers | 464 |
| Scare report with 64 lives, maximum packed integers | 350 |
| Digest with eight entries, maximum packed integers | 171 |

Fifty surface-route plans per second at the illustrated size consume about 5.7 KB/s per recipient before digests and framing, or 39.9 KB/s across seven remote recipients. Event frequency and fan-out matter alongside individual packet size. Keep batches bounded by serialized bytes as routes and identifiers grow.

Every item motion entry carries packed `Sequence` and `Path`, consuming two to ten bytes per entry; motion flags share one flag byte. Client-simulated rocks require an upload leg as well as host relay traffic. Include that traffic and the batching issue in finding 9 when considering whole-session headroom.

## Visual acceptance for the user

Use representative bird content and compare a host with a remote client side by side.

- Throw at small stationary and crossing birds at maximum speed from below, beside, and above. Watch for tunneling, incorrect rebound/lift, and feathers or bodies appearing behind the impact.
- Force low rendered FPS while retaining 60 Hz physics. Drive through crossing birds and check consistent cart hits across catch-up ticks.
- Observe late scare plans and correction/handoff overlaps near airborne rocks. A correction alone must not create a kill or unexplained rebound. Disconnect a rock's simulator during a ricochet and check trajectory and release-count continuity.
- Follow bird-to-wall-to-bird ricochets on both peers. Watch for rocks following the abandoned path, cutting through scenery, or producing player shoves from a false interpolated segment.
- Exercise a rejected optimistic pickup of a locally simulated moving rock. Its saved motion must resume instead of dropping from rest. Also check ordinary pickup/rethrow, sleeping/waking rocks, and a weak hit followed by a separate lethal contact under latency.
- Scare birds again after their escape finishes, including with scarce destinations. Exhaust spawn capacity and confirm existing birds still move and complete escape retries.
- Join a busy 300-bird world, honk into a dense flock, and observe loading completion and frame hitches. Trigger distant deaths while nearby corpses settle; nearby bodies should retain their lifetime and shrink.
- Check one reward per life, same-release multi-kill bonuses, cart-driver attribution, and uninterrupted aiming while rewards appear.
