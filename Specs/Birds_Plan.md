# Birds implementation plan

Implement birds as a small shared simulation coordinated by the host: the host chooses destinations, reserves perches, and publishes timed movement plans; every client evaluates those same plans locally. Clients detect hits and scares, while the host commits deaths and rewards exactly once. Living birds use shared motion; animation, feathers, and corpse physics remain local.

The central design choice is to send the route a bird will follow, rather than repeatedly send where it is. At approximately 300 birds, this keeps targeting coherent without adding 300 network transforms, physics simulations, or independent client AI simulations. Use the existing FishNet broadcast approach, with a separate bird registry and bird-specific messages.

**Planning assumptions.**

- Keep the existing solo/listen-host session model and eight-player limit. Solo follows the same lifecycle through local calls. Host departure ends the session, as it does today; host migration is outside this feature.
- All participants use the same species catalog, habitat definitions, and static scenery. Include a content fingerprint in bird initialization so different species IDs or perch layouts cannot silently produce different worlds.
- Air, ground, and water habitats are authored, mostly static spaces. Begin with bounded ground regions and flat water surfaces; decorative waves do not move the gameplay swimming surface. Arbitrary moving perches and detailed navigation through clutter are outside the initial implementation.
- The numerical values below are proposed engineering targets and starting defaults. Species tuning remains content work. Use a six-core CPU comparable to a Ryzen 5 3600 or Core i5-10400, 16 GB RAM, and an appropriate 1080p GPU as the performance reference; the development machine's RTX 3080 Ti should not define the minimum visual budget.
- For the rock lethal threshold, use the rock's incoming world-space linear speed at the bird contact, before that contact's response. Do not reuse the player-shove threshold. For carts, use a moving cart entering a bird's hit volume; no separate lethal-speed threshold is required by the spec. A parked overlap alone does not kill.
- Currency resets on leaving a session. A disconnected player does not transfer future rewards to another player who inherits a connection ID, spawn slot, or object ID. Rejoining starts a new player lifetime; reconnect persistence is outside scope.

Implementation requires a BirdRegistry component on SessionRoot, bird presentation/body prefabs, shared settings and species/catalog assets, threat-sensor components, physics-layer configuration, and designer-authored spawn/perch/habitat components. Keep placement in the existing game scene as an explicit designer setup step. Use Unity CLI where available, then Unity MCP, for asset/component work; let Unity create metadata. The perch editor is an ongoing authoring feature, not a migration utility.

**Existing integration points.** These are the boundaries the implementation should extend.

| Area | Relevant code | Implementation consequence |
| --- | --- | --- |
| Session timing | [SessionRoot.prefab](Assets/Game/Prefabs/SessionRoot.prefab), [TimeManager.cs](Assets/FishNet/Runtime/Managing/Timing/TimeManager.cs) | The session uses 60 Hz ticks and FishNet-controlled physics. Schedule bird decisions and contact sampling against these callbacks; do not introduce another physics loop. |
| Startup and readiness | [PickupRegistry.cs](Assets/Game/Runtime/Pickup/PickupRegistry.cs), [SessionController.cs](Assets/Game/Runtime/Networking/SessionController.cs) | Item lifecycle callbacks start/join/end the world. Gameplay currently waits for the player and item baseline. Add a separate bird-baseline readiness requirement, including the empty-population case. |
| Rock replication | [WorldItemRegistry.cs](Assets/Game/Runtime/Items/WorldItemRegistry.cs), [WorldItemMessages.cs](Assets/Game/Runtime/Items/WorldItemMessages.cs) | Reuse the pattern of reliable lifecycle messages, epochs, revisions, pooling, and targeted baselines. Moving items send unreliable snapshots at 20 Hz in batches of eight. Birds should use their own compact route messages rather than inherit this snapshot frequency. |
| Rock presentation/contact | [WorldItem.cs](Assets/Game/Runtime/Items/WorldItem.cs), [PlayerItemHitbox.cs](Assets/Game/Runtime/Player/PlayerItemHitbox.cs) | Each released rock has one simulator: its connected releaser, with host fallback. Other peers, including the host, interpolate reported motion. Use native sphere contacts for birds and preserve the existing local-player impact path on observers. Non-rock items retain host simulation. |
| Release identity | [PlayerInventory.cs](Assets/Game/Runtime/Inventory/PlayerInventory.cs), WorldItemRegistry | Releases already carry item ID, releaser, operation, and launch tick. An inventory operation can release multiple rocks. Pickup clears releaser/operation, and motion revisions identify lifecycle and simulator handoffs. Preserve release attribution separately from the current item record. |
| Carts and horns | [GolfCartNetwork.cs](Assets/Game/Runtime/Vehicles/GolfCartNetwork.cs), [SteeringWheelHorn.cs](Assets/Game/Runtime/Vehicles/SteeringWheelHorn.cs) | Occupied carts use a client simulator and motion epochs; unoccupied carts use the host. Bind cart reports to the simulator and capture the driver at impact. Honks can originate from interacting with the steering wheel, not only from the driver. |
| Cart motion implementation | [NetworkTransform.Motion.cs](Assets/FishNet/Runtime/Generated/Component/NetworkTransform/NetworkTransform.Motion.cs) | This is a project-specific FishNet extension with epoch handoffs. Consume GolfCartNetwork's interface; do not build birds on private transform internals or replace the imported package. |
| Authoring | [ItemPlacementZone.cs](Assets/Game/Runtime/Pickup/ItemPlacementZone.cs), [ItemPlacementZoneEditor.cs](Assets/Game/Editor/ItemPlacementZoneEditor.cs), [BakeTools.cs](Assets/Game/Editor/BakeTools.cs) | Follow the volume/gizmo/generated-children/Undo workflow. Bird ground generation must reject unsuitable samples rather than copy the item generator's fallback to an unsnapped point. |
| Audio and UI | [SoundEffect.cs](Assets/Game/Runtime/Audio/SoundEffect.cs), [SfxSource.cs](Assets/Game/Runtime/Audio/SfxSource.cs), [HudController.cs](Assets/Game/Runtime/UI/HudController.cs), [Hud.uxml](Assets/Game/UI/Hud.uxml) | Reuse sound assets and cached sources. Add balance and reward events to the existing HUD with no new interaction mode. |
| Transport and diagnostics | [GameTransport.cs](Assets/Game/Runtime/Networking/GameTransport.cs), [GameSteamTransport.cs](Assets/Game/Runtime/Networking/GameSteamTransport.cs), [MvpValidation.cs](Assets/Game/Runtime/Diagnostics/MvpValidation.cs) | Extend the existing payload counters and development diagnostics when profiling is requested. Measure local transport and Steam separately, including seven-recipient host fan-out. |

**Responsibilities and code layout.** Place runtime code under Assets/Game/Runtime/Birds and authoring code under Assets/Game/Editor. Use a few concrete types with clear boundaries; avoid a general AI framework or a new networking abstraction.

| Proposed type | Responsibility |
| --- | --- |
| BirdSpecies, BirdSettings, BirdCatalog | Species identity/model/scale, capabilities and tuning, shared defaults, animation/audio mappings, and stable species IDs. |
| BirdSpawnZone, BirdPerch, BirdHabitatVolume, BirdPerchVolume | Scene authoring and stable IDs for population sources and compatible destinations. Habitat volume kinds cover air, ground, and water. |
| BirdRegistry | Session initialization, compact life records, authoritative population/perch decisions, scheduled transitions, broadcast handlers, and client views. It is the single lifecycle coordinator. |
| BirdStateMachine and four behavior drivers | Shared lifecycle plus Bush, Soaring, Water, and Ground destination/movement choices. Drivers operate on records; they do not each own an Update loop. |
| BirdMotion | Pure evaluation of shared route geometry, phase boundaries, orientation, and velocity at a supplied time. |
| BirdScareDriver and BirdThreatSensor | Shared scare handling and local trigger/line-of-sight candidate detection. |
| BirdHitReporter | Rock/cart swept contacts, source attribution, report batching, and local feedback. |
| BirdView and BirdBody | Living appearance/animation/audio and pooled cosmetic death presentation. |
| BirdMessages | Broadcast structs and explicit compact serializers. |
| BirdRewardLedger | Per-player balances and per-rock-release kill counts, owned by the registry; owner-targeted balance/notification messages. |

FishNet broadcasts can send reliable or unreliable messages without requiring a NetworkObject for each receiver. That matches a registry plus ordinary pooled bird views. Register handlers once on the persistent session root, restrict recipients to connections whose bird baseline is active, and unregister/clear on shutdown. See [FishNet broadcasts](https://fish-networking.gitbook.io/docs/guides/features/network-communication/broadcasts).

**ECS, Burst, and jobs.** Use the central registry and pooled GameObject presentation for the initial 300-bird implementation. Defer Unity Entities/ECS: the current scale and existing FishNet, Animator, audio, and physics integration do not justify adding an entity lifecycle and presentation bridge. Reconsider ECS only if a substantially larger workload and measured bottlenecks justify that integration cost.

Keep the numerical work easy to optimize within this architecture:

- Store motion inputs/results and hit geometry in plain numeric structs, separate from views and authoritative lifecycle records. Resolve ScriptableObject tuning into cached numeric values when a species is initialized. Process active records centrally in reusable batches; keep GameObject references and callbacks outside the numerical functions.
- Initial implementation uses ordinary C# for these batches. Candidate Burst work is curve/orbit evaluation, swept bounds, spatial candidate filtering, and relative-motion hit math. Keep FishNet calls, reservations, rewards, Animator/audio changes, pooling, and existing UnityEngine.Physics calls on the main thread.
- During explicitly requested profiling, add Burst only where numerical work materially threatens the existing frame-time budgets. Burst supports compatible static methods and jobs without requiring ECS; first consider a single compiled batch, then jobs if parallel execution produces a further end-to-end improvement. See [Unity's Burst documentation](https://docs.unity3d.com/Packages/com.unity.burst@1.8/manual/index.html).
- Include data preparation, scheduling, completion waits, and applying results in the comparison. Reuse native buffers if jobs are adopted; do not schedule one job per bird or add a frame of hit-feedback latency to hide scheduling cost. Make Burst a direct package dependency when game code begins using it.
- Keep result application on the main thread. Motion outputs carry world epoch, LifeId, and action revision so an obsolete batch cannot move a dead, replaced, or rerouted bird. Contact results retain source/contact identities and enter the same one-time death/reward commit path. Complete outstanding work before disposing buffers on session shutdown.

Burst and jobs optimize local execution. The shared route format, clock, and lifecycle protocol remain responsible for visual consistency and bandwidth. Do not rely on compiled floating-point math being bit-identical across machines; retain the same decoded route inputs and position-error budgets.

**Identity and lifecycle data.**

- Assign each world a BirdWorldEpoch and each new bird life a monotonically increasing uint LifeId. Never reuse a LifeId within the epoch. Reusing a pooled GameObject does not reuse the bird life.
- Each live record contains species ID, original zone ID, biome ID, behavior/state, current action revision, current route, optional queued route, occupied perch ID, reserved perch ID, and scare timing. Keep rendering objects out of the authoritative record.
- Use stable ushort IDs for species, zones, habitats, biomes, and perches initially, with zero meaning absent. Authoring must reject capacity overflow and duplicate IDs. These IDs come from explicit serialized authoring/catalog data, never FindObjectsByType order, Unity instance IDs, or string hash codes.
- Use uint action revisions and a world event sequence. Increment the action revision when replacing a movement/scare plan. A death terminates the life regardless of the movement revision observed by its reporter.
- Retain dead-life terminal information long enough to answer pending reports; after pruning, an unknown old LifeId cannot create or revive a bird. Only an explicit spawn message can create a life. Full-baseline replacement also removes records absent from that baseline.
- Allocate a session player token at player registration. Map it to the active PlayerInventory/connection, and capture it in release/driver records. This prevents ID reuse from miscrediting delayed reports.

**Shared movement and visual coherence.**

1. The host selects a compatible destination and constructs a complete short route. Normal travel uses a cubic curve or a small list of timed curve spans. Soaring uses an orbit definition with center, axes/radii, direction, starting phase, speed, and an explicit end/landing transition. Ground and swimming travel use bounded, surface-compatible spans.
2. Publish the route's start tick, duration, movement mode, control geometry, destination/reservation, action revision, and any root-motion variation parameters. Include phase boundaries for takeoff/travel/landing where they are part of one journey. Do not require a separate packet for each cosmetic subphase.
3. Every client, including the host's local presentation, evaluates the identical decoded route at the same estimated server time. Use an analytic function of absolute time, not accumulated per-frame integration. The host also evaluates the serialized/quantized representation, so compression cannot put its birds on a different path.
4. For normal actions, publish roughly 150 ms ahead of their start and retain the current and next plan. Construct a continuation before the current route expires. Scare and death interrupts supersede queued plans. Idle/perched birds hold an exact shared pose and transmit nothing until a change.
5. Root flight variation must be part of the route or a time-based function whose seed, amplitude, frequency, and envelope are shared. Make offsets approach zero at constrained landing endpoints. Apply obstacle clearance to the full possible variation envelope. Independent Unity random calls are acceptable only for bone animation, sounds, and effects.
6. Derive facing from the path tangent, with a defined orientation at zero speed. Derive banking and locomotion animation from movement mode and curvature. No client performs its own avoidance, chooses another destination, or changes swim height locally.
7. Plan from the current route's evaluated pose/velocity when interrupted, maintaining position and tangent continuity where feasible. Death uses the accepted hit pose and stops living movement immediately.

Use TimeManager.GetPreciseTick with the synchronized Tick domain, converted through TickDelta, as the clock input. LocalTick is not a shared clock, and TicksToTime defaults to LocalTick in this checkout. The synchronized Tick can move backward when adjusted; maintain a monotonic presentation clock that slews small corrections and reinitializes on a new epoch. Never let a clock correction replay takeoff, landing, sound, or reward events. FishNet distinguishes these tick domains in its [TimeManager API](https://fish-networking.com/FishNet/api/api/FishNet.Managing.Timing.TimeManager.html).

Begin with no additional bird interpolation delay: future route delivery supplies the lookahead. Render birds on the shared presentation clock. Drive their independent physics hit shapes from the actual network simulation tick, including every catch-up tick. Only the designated rock simulator reports physical bird contacts. Other peers interpolate rocks with the existing 100 ms delay and do not detect bird hits from delayed copies. Reliable bounce boundaries discard the prior interpolation segment and install the new motion; this can produce a visible correction under latency.

If a route arrives late, evaluate it at its current shared phase rather than starting it on receipt. Blend a small visual correction over approximately 100 ms; larger corrections rebase the view and hit history. Collision sampling must exclude correction motion. If a continuation is unavailable, finish the published span and use its defined terminal behavior: hold a supported perch/ground/water pose or remain on an explicitly published safe orbit. Request the current record once; never invent a new flight path.

Send a low-rate unreliable revision digest, staggered across the population every two seconds, containing LifeId and current/queued action revisions. A mismatch prompts one targeted reliable record request. This detects missing state without periodic transform streaming. Digest sequence and expected effective ticks must distinguish a future queued revision from an already active one. A digest reports disagreement; it does not by itself measure positional error or clock drift.

**Route construction, habitats, and perches.**

The host performs scenery checks when creating a route, not for every bird every render frame. Sample the curved path into conservative swept segments, accounting for bird body radius and maximum path deviation. A clear chord is not proof that a curved route clears a trunk. Check terrain height, landing clearance, and the surface approach. Use bounded attempts to select an alternative candidate or add a simple detour; otherwise retain suitable behavior and retry. Leaves may be excluded from the solid-obstacle mask, but trunks/buildings/terrain must have colliders in it.

For ground travel, raycast support along the candidate route, reject excessive steps/slopes and unsupported gaps, and transmit the sampled surface-compatible route. Initial ground regions should contain open walkable spaces; do not attach 300 NavMeshAgents. The presence of the AI Navigation package is not a reason to assume the game has an authored bird navigation mesh. If later maps require routing around arbitrary connected obstacles, a host-only baked graph/NavMesh route planner can replace this bounded planner without changing motion replication.

Represent water habitat as a surface plane plus a bounded usable area and depth/volume information for corpse entry. Swimming routes remain inside compatible water habitat; landings end on that surface, and takeoffs begin there. Water birds never generate shore-walking routes. Air habitats supply navigable volume and altitude limits; biome matching applies to destinations, not to an invisible wall around the spawn zone.

Generate canopy perches from an authored canopy volume, with trunk/solid-clearance checks and explicit landing orientation/offset. Generate ground perches by projecting samples onto an allowed surface and rejecting misses. Perch fields include biome, tree/ground landing type, normal/facing, usable clearance, and stable ID. Species capabilities filter destination type and clearance, including whether a large model fits.

Generated markers remain editable children, with separate handling for hand-placed markers. Generate/clear/edit operations use Undo and only affect the selected volume's generated results. Add wire-sphere perch gizmos, volume bounds, biome/type colors, and a landing-direction aid. Regenerating or duplicating a volume must not produce duplicate runtime IDs or silently delete manually placed points. Build the habitat/perch lookup once at world start; no scene scans in update loops.

The host owns a single occupancy/reservation table. Destination selection and reservation are one operation on the host thread. Reserve an arrival perch before publishing its route; keep the current perch occupied until actual departure. Convert the destination reservation to occupancy at arrival. Death or route abandonment releases only entries still owned by that LifeId and matching reservation revision. This prevents a delayed cancellation from releasing another bird's new reservation.

If no compatible perch is free, do not overlap an existing bird or silently choose another biome. Keep a suitable existing state or published safe movement and schedule a retry. A soaring bird can remain in suitable airspace while awaiting a perch. Content must supply enough compatible habitat for ground-only birds and enough landing capacity for species that depend on perches.

Replicate perch claim changes reliably with the corresponding lifecycle/route revision. Arrival/departure conversions can be scheduled from the shared route phase times; a resync includes the authoritative current claims and timing. Clients display and cache claims but never arbitrate them. In a baseline, occupied/reserved IDs in every live record are sufficient to reconstruct the table.

**Population and common behavior state.** At world creation, roll each zone's inclusive min/max target once. Spawn that many lives using its weighted species list and suitable habitat inside the zone. Choose the replacement species once per vacancy and retain it during habitat retries, so repeated placement failures do not bias the weights toward easy-to-place species. Preserve target counts and replacement deadlines on the host; clients never roll their own population.

Count living records by original ZoneId. Movement outside the volume and view culling never create a vacancy. Death decrements the count immediately and schedules a replacement under that zone's timing controls; corpses are independent. Stagger vacancies through a per-zone replacement schedule instead of respawning an entire batch at once. If the selected species has no valid starting habitat, retain the vacancy and retry with an authoring diagnostic; do not spawn inside solid scenery or change its biome. A replacement always receives a new LifeId.

Use a small locomotion state plus a shared interruption status, rather than separate state machines for every behavior:

| Activity | Shared behavior |
| --- | --- |
| Idle / Perched | Exact stationary pose; optional occupied perch; a scheduled normal action. |
| Takeoff | Timed initial segment, departure claim release, and takeoff animation. |
| Flight / Soar | Evaluated curve or orbit; driver controls supported destinations and tuning. |
| Landing | Timed approach; reservation becomes occupancy, or water/ground movement begins. |
| GroundMove | Supported ground path; short hops only if the species supports them. |
| Swim | Movement on an authored water surface. |
| WaitingToFlee | Shared interrupt status with fixed flee deadline. Continue the current locomotion until that deadline; an airborne bird must not freeze in midair. Cancel incompatible queued normal actions. |
| Fleeing | Shared interrupt status using the driver's supported locomotion and escape route. Return to normal after its escape leg/cooldown. |
| Dead | Terminal lifecycle state. Cancel all transitions/claims, disable live hit participation, and start cosmetic presentation. |

Bush drivers select tree/ground perch journeys at low altitude. Soaring drivers circle/glide and schedule periodic landing attempts. Water drivers alternate flight, water landing, and swimming. Ground drivers idle/wander and either run or use bounded low hops during escape. All use the same death, reservation, scare, and replacement paths. Future drivers add destination/movement choices without rewriting lifecycle handling.

Store idle/travel timing, flight/ground/swim speed, flight height, short-flight limits, supported capabilities, scare radius/delay, optional lethal-speed override, reward, model/size/pivot, animation configuration, and optional sound overrides in species data. Keep common lethal speed, multi-kill bonus, corpse timing, shared sounds, and common animation defaults in shared settings. Only the selected driver's relevant fields affect behavior.

**Local scares with bounded reporting.** Assign responsibility by threat rather than making eight clients report every nearby bird:

| Threat | Primary detector |
| --- | --- |
| Player | That player's client, using its presented player pose. |
| Moving rock | Its connected releaser's client; the host client handles unattributed rocks and takes over when the releaser disconnects. |
| Occupied cart | The current cart simulator/driver client. |
| Unoccupied cart | The host client. |
| Horn | The client that invoked Honk, including a player interacting with an unoccupied cart. |

This assignment reduces duplicate traffic; it is not an anti-cheat rule. The host still accepts otherwise valid trusted reports from another client and deduplicates them. Switching a primary detector must not reset a bird's scare delay or a rock's kill count.

Use trigger-based candidate membership around locally owned threat sources, sized to cover the largest relevant scare radius, followed by each species' actual distance check and one scenery line-of-sight raycast. Cache component/source mappings and nearby membership. Maintain only locally relevant bird proximity proxies; hit queries against analytic bird records remain available for an active projectile outside the camera range. Unity trigger callbacks occur during physics simulation, so account for FishNet's simulation and replay boundaries. See [Unity trigger callbacks](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Collider.OnTriggerEnter.html).

Evaluate on trigger entry, source activation/movement, and scheduled rechecks for still-near candidates whose line of sight was blocked. A small 5 Hz recheck of existing candidates is necessary: a bird can become visible without another trigger-enter event. Do not raycast every bird against every player each frame. Source sensors are enabled only on their primary detector. For rocks, sleeping or effectively stationary sources are excluded, and a wake/movement transition re-evaluates existing overlaps. A bird remains scareable by a nearby visible player even if the player is standing still.

Batch newly detected bird IDs with threat kind/identity, one threat position, and one source event sequence. Locally suppress repeats for the same threat encounter and bird scare cycle. The host accepts the first scare while calm, records its registration tick, and fixes FleeAt = registration time + species delay. Additional reports during WaitingToFlee/Fleeing do not extend the deadline. After escape, re-arm through a new encounter or a short retry interval; sustained threats can produce another scare without an every-tick storm.

At registration, plan and reserve the escape while the delay runs, then publish the deadline and route together when possible. A configured zero delay means immediate transition; do not add the normal 150 ms route lookahead. The reporting client can play an immediate alert/scare cue, but shared root escape waits for the host's plan. Thus exact simultaneous escape is limited by delivery latency, especially with zero delay. Death interrupts the delay immediately.

Weak rock contacts enter this same scare path with a contact event ID and no damage. They do not need another line-of-sight raycast after contact.

Connect horn detection to the initiating Honk call exactly once. Perform one overlap query around the presented cart pose, filter by horn radius, deduplicate compound colliders by LifeId, and send a single batched scare event. Horns ignore line of sight. Do not trigger scare detection from PlayHorn or TargetHonk: those execute for remote audio playback and would multiply reports. Preserve the existing 0.3-second horn cooldown and handle overlap-buffer saturation by processing additional candidates rather than dropping birds.

**Fast rock and cart hits.** Give every living bird a pooled, runtime kinematic sphere on BirdQuery, using its species radius. Keep these shapes independent of living model colliders, animations, view distance, and corpse physics. The BirdQuery matrix can remain disabled: per-collider layer overrides include ItemWorld only, and contact modification ignores pairs without an eligible locally simulated rock. Existing environment and interaction queries continue excluding BirdQuery.

Use the rock's existing sphere collider and Continuous Dynamic collision detection. Unity resolves bird and scenery contacts in the same physics simulation; stop sweeping the abandoned straight path after a bounce. Contact modification sets friction to zero and directional restitution before the solver. Handle both ordinary and CCD contacts, including the inverted CCD contact normal. Cache numerical contact data for reporting; contact-modification callbacks can run on worker threads and must not access Unity objects or mutate gameplay records.

RockBounceMultiplier is normal restitution from zero to one. With the normal pointing from bird toward rock, compute directional weight as SmoothStep(0, 1, Clamp01(1 + normal.y)). Effective restitution is bounce multiplied by Lerp(underside multiplier, 1, weight). Direct underside contacts therefore rebound weakly; side/top contacts receive full restitution. Weak and lethal contacts use the same response.

On actual collision entry, apply upward lift scaled by incoming normal approach speed, RockLiftMultiplier, and directional weight. Remove the lift component that would point into the bird, then cap the resulting rock speed using ItemDefinition.MaxSpeed. Lift is applied after the native contact solve and affects subsequent simulation movement; normal restitution participates in the native solve itself. Lethality uses the cached incoming world-space rock speed, independently of player shove settings and post-impact lift.

The initial speed envelope covers the Rock asset's 50 m/s cap, including inherited player/cart velocity, drops, scenery bounces, initial overlaps, and multiple simulation ticks per render frame. Native contacts decide which birds were reached; never report birds merely lying farther along the pre-bounce line.

The releaser starts local physics immediately, holds reports until release acceptance, and remains the accepted rock simulator. The host commits inventory and bird outcomes and relays motion. Item lifecycle revision also identifies simulator handoffs; a separate monotonically increasing motion sequence orders snapshots within that revision. Every snapshot also carries a path counter, incremented at bounce/sleep/wake boundaries, so a newer unreliable snapshot arriving before the reliable boundary still discards the old interpolation segment. Send routine motion at the existing snapshot rate and bounce/sleep/wake/removal boundaries reliably. A snapshot from an older revision or sequence cannot restore an old trajectory. Active simulators ignore their own relayed snapshots. Baselines retain reported position and velocity for remote-simulated rocks rather than capturing the host's delayed kinematic copy.

On disconnect, the host installs the latest reported motion, increments its revision, and takes over simulation without changing releaser identity, operation, or kill count. Pickup, rejected release, correction rebase, handoff, and pool reuse reset contact state. Rebased overlaps do not create fresh hit reports. Ignore FishNet prediction replay callbacks. Remote rock copies still participate in local-player shove detection on both host and clients.

Maintain a local contact set for weak contacts so a resting overlap does not repeatedly scare. A later contact after separation can become lethal. Predicted-dead lives leave local live-hit queries immediately. Send reliable compact hit batches no later than the next network tick, with source identity, per-contact sequence, LifeId, observed bird action revision, contact time, trusted incoming speed, and quantized bird/contact pose. Keep per-source fields in the batch header. Award nothing locally until the host accepts the result.

The primary detector immediately shows the bird's feathers/body response. The host checks lifecycle identity, release attribution, and threshold classification, but performs no geometric replay or line-of-sight validation. If the life is alive, it atomically marks it dead, cancels claims/scares, updates population, commits the reward, and publishes the terminal event. A hit observed during an older movement revision can still kill the same living life.

Two concurrent reports race for that single death commit. First accepted wins; later reports receive the already-committed outcome. Reconcile local feedback by LifeId: do not spawn a second body or play a second feather burst when confirmation arrives. If the source release was rejected and the life remains alive, return its speculative body/effect and restore the current live record. A negative reply only clears the prediction with the matching contact sequence; an older weak-hit result must not restore a later predicted kill. A valid release keeps its physical rebound even when another report wins the death commit. Record reward outcome separately from cosmetic prediction.

For carts, sweep a small set of cached body volumes over actual simulator movement, including rotational travel. Use pre-impact motion and exclude recovery teleports and epoch installation from the sweep. The detector captures the driver token and cart epoch/seat revision at contact. Keep a short driver-by-epoch history on the host so a driver changing seats before the report arrives cannot redirect the reward. An unoccupied cart can kill but grants no currency. Cart kills never alter a rock release count.

**Release accounting and rewards.** Use a release key containing world epoch, rock ID, releaser player token, and InventoryRequest.Operation. Rock ID is essential because a stack drop shares one operation. Capture the accepted release's launch identity once; item motion revisions, sleep/wake, and bounces do not create new releases.

Keep an immutable attribution/count record for the active release and a small history of completed releases after pickup/removal. Start with a three-second completed-release grace, retaining entries with outstanding report outcomes; the supported network envelope below is substantially shorter. Do not clear this history when SetHeld clears ItemRecord.Releaser/Operation. A late accepted hit belongs to its original release and never contributes to the newly thrown rock's count. Disconnected-player mappings must not be redirected to reused player IDs.

Predicted hits can precede release acceptance because releases use an inventory RPC and bird hits use broadcasts. Queue local reports until that release is acknowledged, while showing local feedback immediately. The host also resolves a report arriving during a pending release without creating a phantom accepted release. Rejected inventory operations cancel their pending bird hits. Do not rely on ordering between unrelated RPC/broadcast channels.

Within the death commit, increment that release's accepted kill count once. Award species reward for kill one; award species reward plus the shared flat bonus for every later kill. Currency uses an integer balance and a monotonically increasing reward revision/event ID. Send the absolute new balance, species reward, bonus amount, and release kill count only to the credited player; spectators need only the shared death outcome. All clients receive the same committed attribution/count outcome where it is displayed, but private balances need no global stream.

The HUD reads the initial balance snapshot without generating a notification. Subsequent reward events update a cached label and a short scheduled notification such as '+12  (+5 multi-kill bonus)'. Use UI Toolkit picking-mode Ignore on the entire notification subtree. Aggregate rapid notifications if necessary without losing the separate bonus amount. Do not change cursor locking, aiming, inventory state, or action maps. Bind/unbind through player/session and reward events; do not poll balances in Update. Clear balance, queued notifications, and reward revisions on session shutdown.

**Wire format, ordering, and late joins.** Start with all ready clients receiving every bird's compact lifecycle and route decisions. At 300 birds this gives consistent distant sightings, global perch claims, and immediate movement into range without introducing per-client AI or ownership handoffs. Spatial relevance initially controls rendering, animation, effects, and local detection work; it does not create different simulation states.

| Message | Delivery | Content and cadence |
| --- | --- | --- |
| BaselineRequest / Start / Complete | Reliable, targeted | Client attempt token, world epoch, protocol/content fingerprint, snapshot sequence and tick. Once per join/resync. |
| BaselineChunk | Reliable, targeted | Current live records, current/queued plans, scare deadlines, and current perch claims. Byte-bounded chunks. |
| BirdSpawn | Reliable, batched | New LifeId, species/zone/biome, initial state/plan and claims. Only at initial creation or replacement. |
| BirdPlan | Reliable, batched | LifeId/revision, effective tick/duration, phase/motion type, geometry, variation, and claim changes. Only when a plan changes. |
| ScareReport | Reliable, batched | Source identity/event, threat pose and affected LifeIds. Once per new encounter/cycle. |
| HitReport / HitResult | Reliable, batched/targeted | Source release/cart identity, contact identity, affected LifeIds and impact data; terminal outcome for reporter reconciliation. |
| BirdDeath | Reliable, batched | LifeId, terminal revision/tick, shared death pose, source/credit outcome, released claims. No subsequent corpse updates. |
| RevisionDigest / RecordRequest | Unreliable digest; reliable repair | Staggered revisions every two seconds; one request per mismatch until answered. |
| RewardBalance / RewardEvent | Reliable, owner-only | Absolute balance/revision and optional new reward breakdown. No idle updates. |

Keep epoch and common time/source data in batch headers. Use explicit enum/flag bytes and integer IDs, not strings, animation parameter arrays, or serialized species objects. Use 1 cm position/control-point quantization relative to a shared spatial cell, with an explicit full-position or new-cell encoding when a coordinate is outside range. Quantized velocity/duration fields need defined ranges and overflow encoding; never clamp a valid long flight or location silently. Orientation may use a compact facing representation or FishNet's packed quaternion when a full pose is needed.

Budget approximately 64-96 bytes for a common route record, 20-32 bytes per hit entry plus its source header, and 8-12 bytes per digest entry. Long multi-span routes and spawn records cost more; the traffic budget must include them. The existing FishNet integer writers use packed encodings, so final accounting must come from serializer output rather than C# struct sizes.

Target roughly 1,000 bytes of application payload per batch, then enforce the active transport's returned MTU minus FishNet framing. FishySteamworks in this checkout has a 1,200-byte unreliable MTU; its large reliable limit is not an invitation to send one huge baseline. Keep baselines paced at roughly 16 KiB/s per joining peer, with urgent live events ahead of the next unsent baseline chunk. Reuse send buffers and avoid allocations in the steady-state motion evaluator. Serialize common broadcasts once for their recipients where FishNet supports it.

For a late join:

1. Cache authored catalogs/IDs locally and request the bird baseline after the game scene is available. Keep input disabled until both item and bird readiness are satisfied.
2. On the host, capture an immutable snapshot at event sequence S and start retaining subsequent deltas for that connection. Copy compact records so chunking over several frames cannot mix different moments.
3. Send Start, bounded chunks, and Complete for that snapshot. Stage incoming live deltas with sequence greater than S; do not apply half a baseline to the visible population.
4. Install the live set and claims atomically, then apply subsequent deltas in order. Evaluate current movement/scare phases using their original effective times. A life killed during transfer remains dead, and a replacement retains its distinct identity.
5. Enter live delivery and set bird readiness. Include an initial owner balance snapshot without replaying rewards. Do not reconstruct old feathers or corpse physics. The initial visible population is the current live set, not the original zone target rerolled.
6. Reject stale epochs/attempts and older revisions. Handle missing/duplicate chunks with the snapshot identity. If a long-running transfer outgrows the retained-delta limit, restart its snapshot rather than permanently stalling host event processing.

Increment the existing session protocol identifier for the bird message contract and check the bird content fingerprint before completing readiness. SessionController.SessionId is a local attempt counter, while its wire session and the item world's epoch have different roles; do not treat those values as interchangeable. Host-mode handlers must share one authoritative record and execute cosmetic presentation once, not process a local echo as a second spawn/death/reward.

**Death, animation, and audio.** A living view has no dynamic body driving its root. On death, stop its animator/driver, disable the live hit/proximity proxy, and detach or rent a whole-body cosmetic object with the same model, scale, materials, and pose. Disable root motion for living models. Use a shared activity mapping or AnimatorOverrideController so species can reuse the rig/clips, with simple fallbacks when particular clips are absent. Missing art must not block gameplay-state implementation.

Spawn feathers at the accepted death pose. For the reporter, retain the already-started local effect instead of restarting it on confirmation. Set body linear velocity to zero or a small downward value; permit only a capped sideways nudge, initially at most 0.5 m/s. Apply local angular tumble. Do not copy flight velocity or rock impulse into the corpse.

Use a BirdBody layer that collides only with appropriate scenery. It must not physically interact with players, carts, rocks, living birds, or other corpses. Also explicitly exclude it from existing environment/interaction/support/clearance queries: a physics collision matrix does not prevent a raycast from treating a corpse as ground or an obstruction. Required mask touchpoints include WorldItemRegistry, PlayerInteraction, PlayerSeating, and GolfCartController. Keep bird sensors/query volumes excluded from unrelated gameplay queries as well.

Add OfflineRigidbody to cosmetic body prefabs, cache/set its PredictionManager when rented, and detach it when pooled, following the existing item's integration. Otherwise player prediction can re-simulate corpse physics during reconciliation. Suppress death/scare/contact callbacks during replay. Physics remains driven by FishNet's existing tick loop.

Use the authored water volume/surface to detect entry, disable supporting body contacts as needed, and sink the body below that surface. Always run a lifetime deadline even if the body never settles. Start with eight seconds visible plus a one-second smooth scale-down. Make these shared settings adjustable. Freeze settled bodies to reduce physics work. Under corpse bursts, shrink/pool older or distant bodies first using a local cosmetic cap, initially 32 actively simulating bodies; this must not change the shared living population or rewards.

On reuse, restore original species/model scale, collider state, rigidbody velocity, animator state, audio loops, particle state, and water/cleanup timers. Do not reuse an already shrunken scale as the next normal scale. Bound pools across the species collection rather than prewarming 300 instances of each species.

Resolve ambient, scare, wing, and hit SoundEffect references once per rented species as override, then shared fallback, then silence. Schedule ambient calls and wing loops locally only while the corresponding activity is audible; stop them at state exit/death/pooling. Reuse SfxSource and cap concurrent distant sounds. Animation phase and sound variation need no packets, but state selection must agree with the published activity.

**Scheduling and numerical budgets.** These targets apply to bird work in addition to the existing players, items, and carts. They are acceptance criteria for later requested profiling, not a claim that a particular content pack will meet them automatically.

| Dimension | Initial target / policy |
| --- | --- |
| Representative session | 300 living birds, eight players including host, 60 nearby animated birds, four moving carts, and 64 moving rocks. Also cover all birds moving and a large simultaneous horn/death burst. |
| Whole host frame | Sustain 60 FPS at the reference configuration; aim for p95 total CPU and GPU frame times below 16.7 ms with headroom, not merely a 60 FPS cap. Existing non-bird work consumes part of that budget. |
| Bird coordination on host | At most 1 ms p95 and 2 ms p99 per rendered frame, including decisions, population/claims, and serialization; avoid route-planning spikes over 4 ms. |
| Host bird presentation/contact/cosmetics | At most another 2 ms p95 CPU in the representative session. Set an initial incremental GPU target of 2 ms for 60 nearby birds, then adjust content LOD/materials to fit. |
| Steady-state memory churn | No per-frame allocations from route evaluation, candidate iteration, or ongoing pool use. Bound receive, baseline, contact-history, and effect buffers; separate join-time allocations from play-time churn. |
| Normal bird traffic | At most 8 KiB/s host-to-each-remote-client average across a representative minute, including bird framing/digests. At seven remotes this is 56 KiB/s, about 0.46 Mbps host payload upload. |
| Stress bird traffic | Aim below 32 KiB/s per remote over a five-second mass-action window; seven-client fan-out is 224 KiB/s, about 1.84 Mbps. Baseline traffic is separately paced and counted. |
| Client reports | Normally below 1 KiB/s per client. Large horn/multi-hit events may burst; batch them without delaying contact feedback. No repeated calm/idle data. |
| Shared pose | Perched/idle root disagreement at most 2 cm after state delivery. Moving roots: p95 at most 15 cm and p99 at most 30 cm in the supported network envelope, with major phase agreement within 25 ms once the plan is available. |
| Small targets | Additionally require moving position disagreement below half the species' hittable body diameter at p95. If this is tighter than 15 cm, it governs. A small species may require tighter clock alignment or slower travel. |
| Network envelope | Plan for up to 150 ms RTT, 20 ms jitter, and 1% packet loss; separately review 250 ms RTT / 3% loss for graceful recovery. These are test conditions, not eligibility checks for trusted hits. |
| Immediate events | Reporter hit feedback within one rendered frame. Remote death occurs after report upload plus broadcast delivery and up to two network ticks of processing. Zero-delay scares have the same unavoidable delivery constraint. |
| Timing/recovery | Aim for inter-client bird-clock disagreement at most 10 ms p95; recover small late-plan errors in approximately 100 ms after receipt. A long transport stall cannot preserve simultaneous visuals. |

For scale, 150 moving birds replacing an 80-byte plan once every four seconds cost about 3,000 bytes/s per recipient before framing. If all 300 do so, it is about 6,000 bytes/s. A full 300-entry 8-byte digest every two seconds adds 1,200 bytes/s. This makes an 8 KiB/s normal target plausible but tight when routes are larger or short ground paths change frequently. If all 300 need 96-byte plans every second, plans alone cost 28,800 bytes/s; frequent replanning must therefore be treated as a content/navigation performance problem.

Compare that with even a 30-byte pose at 20 Hz for 300 birds: 180,000 bytes/s per recipient, or 1.26 MB/s for seven recipients, before protocol overhead. More compression cannot recover the main benefit of avoiding unnecessary position samples.

Evaluate simple motion centrally at render time for visible birds and at contact-sampling time for relevant hit candidates. Maintain a coarse spatial index for all live records, conservatively covering their travel between index updates. This permits projectile contacts beyond the camera's render radius without activating every animator. Maintain simple live collision proxies for the full population; rendering culling must not disable a hittable bird. Include their physics and per-tick route evaluation in the performance budget.

Use scheduled next-action times for AI, spawn replacement, retries, audio, and corpse expiry. Process normal route work with an initial soft allowance of two route decisions and approximately 16 scenery queries per network tick, spreading startup and routine transitions. Sampled curved routes may span several scheduler slices. Death and scare registration bypass the normal work queue; reserve time for escape route completion before the configured deadline. Zero-delay scare bursts need precomputed compatible candidates/safe continuations and explicit profiling, not a silent extension of the species delay.

Do not lower the global 60 Hz physics rate to make birds fit. Reduce animation cost, ordinary planning churn, expensive scenery queries, and unnecessary reports first. Start with global compact route delivery. Add per-connection spatial subscription only if measured traffic exceeds the target; that later change must retain the canonical host population, send current state before a bird becomes visible/hittable, and prefetch along rock and cart travel. It is not needed to make the first implementation coherent.

**Implementation sequence.** Each stage leaves a concrete subsystem ready for the next; full bird-library import remains separate content work.

| Stage | Work and completion condition |
| --- | --- |
| 1. Data and authored identity | Add species/settings/catalog and spawn/perch/habitat component types, stable IDs, masks, and content fingerprint. Implement repeatable perch generation/editing with Undo. Supply minimal representative definitions and identify required prefab/scene setup. |
| 2. Registry and session lifecycle | Add BirdRegistry and wire it into the existing world start/join/end callbacks. Add separate item/bird readiness flags. Implement life IDs, population accounting, reliable spawn/death records, baseline staging and shutdown cleanup before adding complex behavior. |
| 3. Shared motion and claims | Implement clock handling, analytic motion, queued plans, compact messages, reservation ownership, and targeted repair. Keep numerical motion in plain structs and centrally evaluated batches, separate from GameObject presentation. Begin with a stationary bird and a perch-to-perch journey; living state and root motion must have one writer. |
| 4. Four behavior drivers | Add low flight, soaring/periodic landing, water landing/swimming, and flightless/short-flight ground movement. Add bounded scenery avoidance, habitat compatibility, and no-destination retries. All drivers use the common lifecycle. |
| 5. Scares and horns | Add primary-threat sensors, line-of-sight evaluation, encounter deduplication, shared delayed escape, and horn overlap batching. Hook the initiating honk path and cart simulator transitions. |
| 6. Hits, release ledger, rewards | Implement physical bird contacts, directional rock rebound/lift, simulator ownership and ordered motion boundaries, release acceptance/history, cart driver capture, atomic death/reward outcomes, and local prediction reconciliation. Connect weak contacts to the scare driver. |
| 7. Presentation and HUD | Add animation mapping, pooled feathers/bodies, scenery-only body physics, water sinking, scale cleanup, audio overrides/fallbacks, and event-driven local currency UI. Configure body/query exclusions in existing physics queries. |
| 8. Representative content and requested acceptance work | Designer sets up all behavior habitats and representative small/large models. When explicitly requested, run the numerical and visual acceptance scenarios below, then tune planner scheduling, LOD, pool caps, and payload formats against the stated targets. Apply selective Burst/jobs only to measured numerical bottlenecks and retain them only when total frame cost improves within the same visual/contact budgets. |

Prefer surgical changes to the named integration points. Do not rewrite player prediction, inventory transactions, cart motion, or imported FishNet code to host bird AI. Do not add per-bird NetworkAnimator, SyncVar collections, or NetworkTransform streams. If the eventual measured route cost invalidates this design, replace that narrow movement-planning boundary rather than layering another independent movement writer over it.

**Potential implementation snafus and their treatment.**

| Risk | Consequence and response |
| --- | --- |
| Matching seeds but different simulation | Equal seeds do not synchronize independent physics, obstacle checks, or random-call order. Send host-chosen route geometry and evaluate by absolute time. |
| Clock skew on small/fast birds | A 20 ms skew at 15 m/s produces 30 cm of root error even with identical routes. Measure clock and spatial error separately; tune alignment and species speed before adding transform packets. |
| Predicted rock versus delayed observers | A shooter can legitimately hit a bird while another player's rock appears behind it. Trust the shooter, share the accepted death pose, and document the immediate-event latency exception. Near-identical living birds do not imply identical rock collision frames. |
| Reliable delivery stalls | A lost reliable packet can delay later plans. Publish ordinary routes ahead, retain continuations, prioritize live events over unsent baseline chunks, and use bounded repair. Do not claim loss-proof simultaneous zero-delay actions. |
| Baseline/event race | A copied live bird can die during join and be resurrected by a later chunk. Stage a snapshot at a known sequence and replay its subsequent deltas before making it visible. |
| Identity reuse | Pooled objects, recycled player slots, or a new throw can receive a previous life's message. Use world/life/player/release identities independently of instance and motion revisions. |
| Prediction replay | A physics callback can run during player replay and generate duplicate hits, scares, or corpse movement. Guard reports and pause cosmetic bodies through OfflineRigidbody. |
| High-speed bounce geometry | Use native CCD for ordered bird/scenery contacts and capture incoming speed before the solve. Handle CCD normal orientation, correction overlaps, contact threading, and reliable motion boundaries. Lift is applied after the contact solve; densely packed same-tick impacts require visual acceptance. |
| Release/pickup/hit ordering | Pickup clears existing attribution; an optimistic release can be rejected. Retain release history, wait for release acknowledgement, and resolve speculative feedback without extra rewards. |
| Cart driver handoff | Current ownership may differ from the driver at contact. Capture epoch/driver identity when detecting the hit and retain enough host history to resolve it. |
| Corpse query interference | A scenery-colliding corpse may become a cart wheel's ground, a blocked seat exit, or an interaction obstruction. Update explicit query masks as well as collision pairs. |
| Trigger visibility changes | Entry-only checks miss a threat becoming visible while still inside range. Recheck existing candidates at a low rate, then suppress network reports after registration. |
| Invalid habitat or too few perches | Zones cannot maintain their target or fleeing birds cannot find destinations. Preserve vacancies and retries, surface authoring errors, and never break biome/capability rules to hide the problem. |
| Curves crossing scenery | Clear endpoints or a clear straight ray do not imply a clear flight arc. Check the curve and its variation/body envelope; reject routes that exceed the bounded planner's capabilities. |
| Thin or decorative water | A renderer/layer alone does not define swim height or corpse entry. Require explicit water habitat and sinking bounds. |
| Species size/rig differences | Shared rigs do not guarantee correct pivots, landing offsets, hit sizes, scales, or root motion. Exercise a small and a large model early and keep model adjustments in species data. |
| Visual cost outweighs networking | Hundreds of skinned renderers, shadows, audio sources, and corpses can dominate a host. Cull/LOD presentation independently of the shared live state and bound local effects. |
| Packet-size/count assumptions | A count-limited batch can exceed MTU when a route contains extra spans or IDs grow. Bound serialized bytes, support overflow encoding, and count transport fan-out. |
| Incorrect performance attribution | Lower server AI work does not guarantee lower total listen-host load. Account for host rendering, its local detectors, existing rock physics, cart simulation, and network work together. |
| Jobs add overhead or stale results | Small batches can cost more to schedule/copy than they save, and results can outlive their bird state. Measure the complete pipeline, reuse buffers, guard pose application with life/action identities, and preserve same-frame local contact feedback. |

**Designer visual acceptance after implementation.** Use at least a host and a remote client side by side, then a representative eight-player session for load. Check the same LifeIds from comparable camera positions; cosmetic wing phase and corpse pose may differ.

- Watch all four behaviors, including soaring landings, water-only swimming, flightless escape, and short low hops. Verify takeoff/landing/idle animations agree with the motion and missing sound slots stay silent.
- Observe nearby small and large birds on both clients: matching identities, paths, turns, perch arrivals, and scare deadlines. Watch a distant bird approach without becoming a different bird or changing route on visibility.
- Edit/generated perches with Undo; check gizmos, one-bird occupancy, cancellation on death, and biome-compatible destinations outside the original spawn volume. Exhaust perch availability and inspect retry behavior.
- Approach behind scenery, then enter line of sight while remaining inside scare range. Confirm moving rocks scare, sleeping rocks do not, repeated reports do not restart the delay, and horns work through obstacles.
- Throw and drop a rock into birds from underneath, the side, and above. Include bird/scenery ricochets, maximum-speed travel, an initial overlap, and a weak hit followed by separation and a stronger contact. Confirm each life dies only once, rebounds preserve release counts, and untouched birds on the abandoned pre-bounce line survive. Compare lift zero with default lift, and disconnect the simulator during a ricochet.
- Have two players hit one bird, rapidly pick up/release a multi-kill rock, and switch cart drivers near a hit. Confirm one reward per life, the correct recipient, and one flat bonus for each rock kill after the first.
- Watch corpse falls stay predominantly downward, settle against scenery, leave carts/rocks/players unaffected, sink in water, and shrink even without settling. Reuse pooled models and check restored scale/materials/audio.
- Join while birds are flying, waiting to flee, dying, and being replaced. Confirm current states arrive without population resets, revived lives, replayed sounds, or old reward notifications. Leave/rejoin a new session and confirm balances and pools reset.
- Keep aiming while rewards arrive, including a rapid multi-kill. Verify readable balance/bonus notifications without input interception or cursor changes.
- When performance validation is requested, capture CPU/GPU frame percentiles, bird planner/query counts, report duplication, actual serialized bytes, host upload across seven peers, and moving-root error under the stated latency/loss scenarios. Evaluate real content and the whole host frame rather than relying on packet estimates or an empty scene.
