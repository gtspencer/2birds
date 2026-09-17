The shared-route approach is appropriate for 300 birds: keep the host's route decisions, local motion evaluation, client hit reporting, and cosmetic bodies. The working changes nevertheless have correctness and scheduling issues that should be addressed before treating the feature as ready. Small individual packets alone do not establish frame-time or bandwidth headroom.

P1 means a release blocker or a core gameplay/network failure. P2 means a functional or scaling issue worth fixing before acceptance.

1. **[P1] The new mandatory startup dependency is absent from the project assets.**

   Location: [PickupRegistry.cs:10](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Pickup/PickupRegistry.cs:10), [SessionController.cs:318](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Networking/SessionController.cs:318).

   `PickupRegistry` now exits the session when `BirdRegistry` is missing, and entering gameplay requires `birdsReady`. The current `SessionRoot.prefab` has no `BirdRegistry`; there are also no authored BirdSettings/BirdCatalog assets under `Assets/Game`. Consequently, the existing solo and multiplayer game cannot start, even with no bird population.

   **Action:** complete the minimum persistent-prefab wiring in Unity: add `BirdRegistry`, create Settings and Catalog assets, and assign them. An empty catalog is sufficient for an empty world. `Birds_Setup.md` explains this prerequisite, but documentation does not make the current checkout playable. This requires user authoring, not an automatic game-scene edit.

2. **[P1] Late joining cannot catch up when live events exceed approximately 27 per second.**

   Location: [BirdRegistry.Network.cs:102](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Birds/BirdRegistry.Network.cs:102), especially lines 124-132.

   Transfers accrue 16,384 bytes of credit per second, but every queued event consumes a fixed 600 credits. That caps catch-up at `16384 / 600 = 27.3` events/second, even when a plan is only about 80-120 bytes. Events accumulate throughout snapshot delivery because the chunk branch always continues before draining them. A legal 300-bird population producing 30 or more plans/second therefore grows its backlog faster than it can drain it. Completion requires an empty queue, so the player remains loading until the session load timeout; restarting an oversized transfer does not solve this rate imbalance.

   **Action:** debit the actual serialized event size including framing, and give queued live events a fair share of transfer bandwidth while sending snapshot chunks. Preserve the snapshot/event ordering and atomic installation. Do not fix this by simply increasing the load timeout or removing pacing.

3. **[P1] Physics contacts use a frame clock instead of the interval that was simulated.**

   Location: [WorldItem.Birds.cs:32](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Items/WorldItem.Birds.cs:32), [BirdHitReporter.cs:305](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Birds/BirdHitReporter.cs:305), [BirdMotion.cs:155](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Birds/BirdMotion.cs:155).

   Rock and cart pre-physics callbacks capture `registry.Now`, then sweep birds over that value through `+1` tick. `BirdClock.Read` advances using `Time.unscaledTimeAsDouble`; subsequent calls in the same render frame have zero elapsed time and cannot advance it. FishNet can simulate multiple physics ticks inside one frame, so distinct rock/cart segments are tested against the same bird interval. At 30 FPS with 60 Hz physics, two consecutive source segments reuse one target interval. Fast crossing birds can be missed or killed without an actual intersection. FishNet's precise render tick also includes elapsed frame time, so it is not the start tick of the physics step.

   **Action:** assign each physics segment its actual simulation start/end ticks, mapped consistently into the bird timeline. Keep the smoothed clock for presentation. Preserve consecutive intervals across catch-up ticks and exclude reconciliation/correction movement. Unity documents the frame-constant behavior of [Time.unscaledTimeAsDouble](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Time-unscaledTimeAsDouble.html); the local FishNet loop is in [TimeManager.cs:721](C:/Users/spenc/source/repos/2birds/Assets/FishNet/Runtime/Managing/Timing/TimeManager.cs:721).

4. **[P2] Remote clients can permanently stop reporting scares after the first escape.**

   Location: [BirdRegistry.cs:158](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Birds/BirdRegistry.cs:158), [BirdRegistry.cs:215](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Birds/BirdRegistry.cs:215), [BirdHitReporter.cs:269](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Birds/BirdHitReporter.cs:269).

   Only the host runs `AdvanceClaims`, which promotes queued routes and changes `WaitingToFlee` to `Fleeing` and then `Calm`. Those timed changes neither increment the record revision nor publish an event. Clients evaluate the right route through `BirdMotion.Current`, but retain the old interrupt state. `Threat` rejects every non-Calm record, including horn targets. If normal planning subsequently cannot find a route, no new plan publishes the host's Calm state, so remote players/carts/rocks/horns cannot scare that bird again. Digests do not repair it because the revisions still match. Client perch occupancy/reservation fields also remain stale.

   **Action:** advance shared timed phases on clients as well as the host, leaving reservation arbitration on the host, or explicitly replicate those transitions. Use the same phase evaluation for scare eligibility and expired-route handling.

5. **[P2] An older weak-hit reply can undo a newer predicted death.**

   Location: [BirdRegistry.Events.cs:206](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Birds/BirdRegistry.Events.cs:206), [BirdHitReporter.cs:224](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Birds/BirdHitReporter.cs:224).

   Results carry `Contact`, but `ReceiveHitResult` discards it and `Confirmed` resolves prediction by life alone. With latency, a weak hit can be reported, then a later lethal contact can predict that bird's death before the weak hit's `Dead=false` reply arrives. The old reply removes the newer prediction and restores the living bird. The eventual accepted death then produces another death effect. Reliable message ordering does not prevent this: the second local prediction can already exist before the first reply arrives.

   **Action:** retain the contact identity responsible for each predicted death and only roll it back for the matching negative result. A confirmed terminal death should still resolve the life regardless of which contact won.

6. **[P2] Mass scares bypass the planner's work limit.**

   Location: [BirdRegistry.Events.cs:109](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Birds/BirdRegistry.Events.cs:109), especially line 124; [BirdRegistry.Planning.cs:205](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Birds/BirdRegistry.Planning.cs:205).

   Normal planning performs one decision per tick, but `ReceiveScare` immediately builds an escape for every reported bird. One full 64-life horn batch can perform 64 route searches and up to 768 curve sphere casts, plus destination scans and endpoint checks, in one callback. Several batches from the same honk execute without a shared budget. This puts the expensive work precisely on the frame when players expect responsive scare/hit feedback.

   **Action:** register the scare and its deadline immediately, then process escape requests through a bounded, deadline-aware planner. Reuse the existing retry path rather than introducing a second planner. Ensure repeated scare reports do not duplicate queued work, and account explicitly for zero-delay scares.

7. **[P2] Unfillable population vacancies can starve all living-bird planning.**

   Location: [BirdRegistry.cs:160](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Birds/BirdRegistry.cs:160), especially lines 165 and 174.

   A due vacancy always consumes the tick's planning slot, even when placement fails. Living birds only receive a decision when no vacancy was attempted. With the default two-second retry interval and 60 Hz ticks, roughly 120 continuously unfillable vacancies can consume the entire scheduler. For example, a 300-bird target with insufficient perch capacity can leave existing birds holding forever and failed escapes waiting indefinitely. Adding invalid content should not suspend otherwise valid birds.

   **Action:** share the bounded planning budget fairly between vacancies, escape retries, and normal decisions. Reserve service for existing lives even while population placement keeps failing.

8. **[P2] A single bird change rebuilds the entire contact grid.**

   Location: [BirdRegistry.Network.cs:176](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Birds/BirdRegistry.Network.cs:176), [BirdHitReporter.cs:48](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Birds/BirdHitReporter.cs:48).

   Every spawn, plan, death, or repair marks the whole grid dirty. The next query clears every bucket and reinserts every living route, including unchanged idle birds. At 300 birds and dozens of route events per second this repeatedly pays the whole-population cost. Multiple local threat sources can interleave queries with synchronous host scare publications and trigger several rebuilds in one frame. Each route may occupy up to 512 cells before the fallback, so the scaling cost is materially larger than simply scanning 300 records.

   **Action:** retain each life's occupied grid cells and update only that life when its route bounds change, removing its entries on death. Reuse buckets and keep the existing large-route fallback. This removes avoidable work before considering Burst or jobs.

9. **[P2] Distant deaths allocate bodies and evict nearby corpses.**

   Location: [BirdRegistry.Presentation.cs:62](C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Birds/BirdRegistry.Presentation.cs:62), especially lines 68-75.

   Living views respect `ViewDistance`, but every replicated death rents or instantiates a physical body regardless of distance or whether the bird had a view. A remote player's kills across the map therefore create rigidbodies, particles, audio work, and species-pool churn on every client. Once the 32-body limit is reached, those invisible deaths remove the oldest local bodies, including visible ones, before their configured shrink/lifetime finishes.

   **Action:** apply local presentation relevance before creating cosmetic bodies/effects, while always committing the shared death and reward. Prefer retaining visible bodies when the cap is reached. Corpse simulation does not need additional network messages.

**Network size and growth headroom**

The application messages are currently small. Explicit route serialization, packed `uint` fields, centimeter offsets with full-coordinate fallback, and broadcasting once to the observer set are good choices. `BirdPlayer` adds one packed integer to item lifecycle records; it does not enlarge the continuous `ItemMotionBatch` stream.

The following sizes are calculated from the checked-in writers, before FishNet/transport framing. Typical plan estimates assume a five-byte epoch, three-byte sequence/start tick, two-byte life ID, one-byte revisions, zero scare deadline, and compact point offsets. Actual sizes grow as IDs/ticks/revisions grow or coordinates use the fallback.

| Payload | Calculated application bytes |
| --- | ---: |
| Plan event with one queued orbit | 64 typical |
| Plan event with one queued flight curve | 83 typical |
| Plan event with one queued nine-point ground route | 114 typical |
| Hit report with 12 entries | At most 464 with the current fields |
| Scare report with 64 lives | At most 350 with the current fields |
| Digest with eight entries | At most 171 with the current fields |

These leave room within the existing roughly 1,000-byte application target. The catch-up problem in finding 2 is artificial credit accounting, not oversized event packets. Keep future batches bounded by serialized bytes rather than increasing entry counts without accounting for larger IDs and added fields. The baseline already measures serialized record sizes.

For sustained traffic, 50 ground-route plans/second cost about 5.7 KB/s per recipient before framing and digests. Seven remote recipients multiply that component to about 39.9 KB/s of host upload. Thus the plan's 8 KiB/s normal per-peer target has some space at this rate, but cannot absorb unrestricted replanning and feature growth. Frame time and bandwidth acceptance still need the representative population and content described in `Birds_Plan.md`.

Several straightforward reductions are available without changing the architecture:

- `BirdDigestEntry.Next` and `Effective` are transmitted but ignored by `ReceiveDigest`. Either use them to detect the relevant state mismatch or remove them; routinely sending unused route metadata wastes the digest budget.
- Surface routes transmit `Surface[0]` even though it equals `A`. Reconstruct that first point from `A` to save seven bytes on each compact ground route.
- `BirdHit.Revision` and `Tick` are not consumed by `ReceiveHits`; `BirdScareReport.Event`, `Source`, and `Kind` are not consumed by `ReceiveScare`; death-event `Player`/`Kills` are not consumed by `ApplyEvent`. Remove unused wire fields or give them a concrete purpose. Keep and correctly use hit-result contact identity as described in finding 5.
- Hit reports serialize both rock-release and cart-attribution fields. A custom serializer can write only the fields for the selected source kind. Batch event bursts and hit acknowledgements where useful to share epoch/framing overhead, while sending them in the same tick.

Preserve room for future fields by accounting for byte growth and event frequency together. No additional transform streams, per-bird network objects, or server geometric hit replay are needed to address these findings.

**Visual acceptance for the user**

After the minimum Unity setup and fixes, compare a host and remote client side by side. Check fast crossing birds against thrown/bounced rocks and carts during low-FPS frames; weak hits immediately followed by lethal hits under latency; repeated scares after a completed escape with scarce destinations; and late joining a busy 300-bird world. Honk into a dense group and watch for a frame hitch. Kill distant birds while nearby corpses are settling and confirm nearby bodies keep their normal lifetime and shrink. Check one reward per life, the same-release multi-kill bonus, driver attribution, and uninterrupted aiming while reward notifications appear.
