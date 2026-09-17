# Networked physics items: implementation plan

## Objective

Make inventory items droppable and throwable, with natural trajectories influenced by the environment, players, and other items. Prioritize immediate response for the interacting player, including guests. Other clients should see smooth motion and converge on consistent collision outcomes and resting positions.

Support an initial target of 12–24 simultaneously moving items. Settled pickups should stop sending position updates and should not obstruct players.

## Recommended architecture

Use real Unity Rigidbodies with client-controlled simulation and batched motion snapshots. The releasing client controls an item's physics for its entire flight. When items from different clients collide, each client simulates its own item's response independently — slight divergence is acceptable and resolves within a few motion snapshots. The throwing client temporarily controls struck settled items until they separate.

FishNet relays motion, sequences lifecycle transactions, and coordinates item identity. The host trusts client-reported physics rather than recalculating or validating ordinary throws.

Separate item collision physics from the existing predicted player movement simulation. Dedicated player hit colliders affect item trajectories; explicit hit events carry player knockback into the prediction system. The throwing client is authoritative for hit detection against players.

Compared with a client-owned NetworkTransform per item, this requires more implementation work but provides explicit control over batching, freezing, and recovery. Sending only launch parameters is insufficient because independently simulated impacts can diverge.

## Gameplay assumptions

- Picking up an item adds it to the existing inventory.
- Throwing or dropping the selected item releases one unit.
- Dragging a stack out of inventory creates one world item containing that stack. Its physical configuration comes from the item definition; stack quantity does not automatically multiply its mass.
- A rolling or bouncing item remains active even while touching the ground. Only sustained, supported rest makes it nonblocking to players.
- Moving items can hit players, moving items, and settled pickups.
- Player reactions use controlled knockback rather than a fully coupled ragdoll simulation. Damage rules are a separate gameplay decision.
- All world items — baked scene pickups, dropped items, and thrown items — use the same physics-ready representation: a kinematic Rigidbody, colliders, and a runtime item component. There is no lightweight pickup variant. Waking an item is a flag change (`isKinematic = false`, layer switch), not a conversion or promotion.

## 1. State-based collision layers

Use physics layers for collision behavior. Store item identity and physical properties in ItemDefinition rather than encoding item type in the collision layer.

| State/layer | Environment | Moving items | Settled items | Player item hitboxes |
|---|---|---|---|---|
| ItemMoving | Collides | Collides | Collides and wakes targets | Collides |
| ItemSettled | Retains support | Can be struck and awakened | Remains stationary until awakened | Does not collide |
| ItemHeld | No physics collision | No | No | No |

Replace the collision role of Pickup/Generic and Pickup/Rock with ItemMoving and ItemSettled. Apply state changes to every physical collider belonging to the item.

Add a PlayerItemHitbox layer for dedicated player collision proxies. Item bodies ignore the main Player movement collider, preventing duplicate physics responses. ItemMoving collides with PlayerItemHitbox; ItemSettled does not.

Update pickup query masks to include moving and settled items while excluding held items and player item hitboxes. Keep environment occlusion so interaction cannot pick up an item through a wall. Ground queries must continue to identify intended support surfaces.

When an item is held, it is the same world item object reparented to the player's hand bone with its colliders disabled and layer set to ItemHeld. No separate held model is created.

Unity's [layer collision matrix](https://docs.unity3d.com/6000.0/Documentation/Manual/LayerBasedCollision.html) defines the corresponding physical interactions.

## 2. Configurable item physics

Extend Assets/Game/Runtime/Items/ItemDefinition.cs with an inline physics settings group.

| Setting | Purpose |
|---|---|
| Throw speed | Direct control over throw distance |
| Drop speed | Small release push for dropping |
| Inherited player velocity | Momentum carried from movement or falling |
| Mass | Response to forces and impacts with other items |
| Linear and angular damping | Air resistance and spin decay |
| Initial spin | Tumbling after release |
| Physics material | Friction and bounce |
| Settle speed, angular speed, and duration | Conditions for freezing |

Use an appropriate collider shape on each item's physical prefab and normal gravity initially. A rock can travel farther than a mushroom through higher throw speed and lower damping. Mass alone is not the throw-distance control.

At release:

`initial velocity = aim direction × release speed + player velocity × inheritance`

Apply velocity and spin once, then let Unity resolve motion and contacts. Use continuous collision detection for fast bodies and check release placement against nearby geometry.

Temporarily ignore collisions with the releasing player's hitbox until the item clears it. Restore collision afterward so a ricochet can hit the thrower. Use per-collider exceptions, not a global player-layer change, and clear exceptions when reusing an instance. See [Physics.IgnoreCollision](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Physics.IgnoreCollision.html).

End the thrower-ignore window after the item separates from the hitbox **or** after a configurable grace period (starting at 0.3 seconds), whichever comes first. This prevents permanent ignore if the player chases the throw.

## 3. Immediate inventory-to-world transactions

Replace the deletion-only drop operations in Assets/Game/Runtime/Inventory/PlayerInventory.cs with complete inventory-to-world transactions.

On Drop or Throw:

1. Immediately reserve the local quantity and update inventory state. The held item is already the world item parented to the hand bone — unparent it, set `isKinematic = false`, switch to the ItemMoving layer, and apply launch velocity. No object creation or destruction occurs. Dropping from an unequipped slot that has no held item instantiates a new physics-ready world item at a default release point near the player.
2. Send a reliable request containing an operation ID, item quantity, launch pose, and initial velocities.
3. The host commits the transaction once, assigns a world ID, and announces the item.
4. The initiating client associates that ID with its existing instance without respawning it or resetting its trajectory. Remote clients unparent their copy of the item and apply the received pose and velocity.

On pickup, the world item reparents to the player's hand bone, goes kinematic, and switches to the ItemHeld layer. The network sends the held item's definition ID on the player — remote clients reparent their copy of the same world item to that player's hand bone. No position updates are sent while the item is held; the hand bone positions it. The host commits inventory insertion together with the hold state. Resolve simultaneous claims and insufficient capacity without duplicating or losing items; restore the item to its world position if a request cannot commit.

Simultaneous pickup claims are resolved by the host in arrival order — first request received wins. The losing client's pending hold is rolled back and the item reparents back to its last world position. There is no contested or tug-of-war state. The winning client sees the item in their hand instantly.

Operation IDs make requests idempotent. Pending quantities prevent rapid inputs from spending the same inventory units twice. These are consistency rules, not checks against client-reported trajectories.

## 4. Independent item simulation

Each item's releasing client remains its simulator for its entire flight. There are no ownership transfers for item-on-item collisions.

When two items from different clients collide, each client simulates its own item's response independently using the latest known state of the other item. The results may differ slightly between clients. Within a few motion snapshots (~100–150 ms), the remote representation of each item converges back to where its owning client actually simulated it. Visual smoothing blends out the difference. By the time items settle, all clients agree on final poses via the reliable settle message.

Temporary group simulation (one client controlling multiple items) is used only when a thrown item wakes settled items (§5). The thrower simulates the struck items because only they have the accurate incoming trajectory. Woken items return to independent status once they separate from the collision.

This eliminates group ownership transfers, revision tracking, and the associated host round-trips. Clients always see immediate local response to their own throws. Ordinary uninterrupted throws do not follow relayed corrections to their own simulation.

## 5. Wake settled items before impacts

A settled pickup must become a movable collision participant before the solver resolves a strike against it.

Before an active item's physics step:

1. Sweep its expected movement for nearby settled items.
2. Set `isKinematic = false` on potential targets and switch them to the ItemMoving layer.
3. Include targets in the thrower's temporary simulation group.
4. Resolve the collision with both bodies' masses available.

Wake only items in direct contact with the struck target or within one collision radius of the impact point. Do not propagate waking through an entire pile. Distant pile members remain settled. An item woken unnecessarily can resettle, but aggressive wake propagation is not needed for plausible-looking reactions.

Combine conservative sweeps with overlap checks for existing contact and fast motion.

Do not rely solely on OnCollisionEnter to change a settled body from kinematic to dynamic. The initial response may already have treated it as immovable. Unity documents that [kinematic bodies are not moved by collision forces](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Rigidbody-isKinematic.html).

Because all world items (including baked scene pickups) already carry a kinematic Rigidbody and colliders, waking is a flag change — no component attachment or promotion step. Track which items have been displaced from their original baked positions for late-join state so new clients see items where they actually are.

## 6. Player collision and knockback

Add a dedicated kinematic collision proxy for each player on PlayerItemHitbox. Move it on physics ticks using the player's locally represented movement. For remote players, use short, bounded movement prediction.

Moving items bounce against these proxies. The throwing client is authoritative for whether its item hit a player. When the thrower's simulation detects a hit, it emits a hit event containing a unique event ID, target player, contact normal, and impulse magnitude. The host trusts and relays this event. When the thrower and target disagree due to latency, the thrower's result stands.

Knockback is a single sharp impulse — no sustained force or build-up. Hit timing policy: the server stamps the hit event with the server tick it was received on. The target applies the impulse at that tick. If the target has already simulated past that tick, it replays from the hit tick forward (a reconciliation). The impulse is short enough that the resulting correction is small in practice. Deduplicate events by ID across retransmission and prediction replay.

Extend Assets/Game/Runtime/Player/PlayerMotor.cs so external hit impulses are recorded at simulation ticks and survive reconciliation. The affected player can respond immediately when receiving or predicting a hit; the server incorporates the same trusted event into its player simulation.

A client-side AddForce alone is insufficient because reconciliation could undo it. Conversely, replaying a recorded hit must not add the impulse twice. Include acknowledged hit state in reconciliation and retain pending predicted events until resolved.

Ensure normal movement acceleration does not immediately erase knockback.

## 7. Integrate with FishNet prediction

Item physics and collision proxies must not advance during player reconciliation replays. Use FishNet's existing OfflineRigidbody handling where appropriate; it is designed to prevent nonpredicted bodies from being advanced by prediction re-simulation. See [FishNet OfflineRigidbody](https://fish-networking.gitbook.io/docs/fishnet-building-blocks/components/prediction/offlinerigidbody).

- Advance item trajectories and detect contacts during normal physics steps.
- Replay recorded player impulses through the player prediction system.
- Never emit new hit or wake events during replay.

Use the project's existing FishNet-controlled physics clock. Do not add another global physics simulation loop.

## 8. Batched motion and remote presentation

Add a WorldItemRegistry network component to SessionRoot. Runtime items can use ordinary GameObjects, with identity and networking handled by the registry rather than a NetworkObject per item.

Starting rates:

- Physics: 60 Hz on the existing simulation clock.
- Motion snapshots: 20 Hz, batched per sending client.
- Impact updates: prompt snapshots when collisions significantly change motion.

Motion entries contain item ID, sequence or timestamp, pose, linear velocity, and angular velocity. Send item definition and quantity during lifecycle changes rather than every motion update. Pack rotation and velocities with explicit supported ranges.

Use unreliable, independently usable motion snapshots. Use reliable messages for creation, collection, waking, settling, and committed hits. Split batches below the transport's packet limit.

For imminent local interactions, use dynamic predicted replicas with real item mass. Do not use kinematic remote replicas as mass-bearing participants in locally predicted item collisions. Ordinary remote presentation can remain buffered and smoothed.

Correct physics separately from visual smoothing. Mark collision boundaries so interpolation does not draw an item through the surface it struck. Permit only brief extrapolation when snapshots are missing.

At roughly 48–64 bytes per motion entry, 24 items at 20 Hz would produce about 23–31 KB/s of motion payload per receiving client before protocol overhead. Group metadata, lifecycle events, and collision bursts add traffic; the final serialization format determines the actual budget. Server outbound traffic scales with recipients.

Use reusable buffers, simple colliders, active-body registries, and physics broadphase queries. Avoid per-frame allocations and reliable messages for every resting contact. Cache Rigidbody, collider, definition, camera, and networking references during initialization.

## 9. Settling, piles, and support

Starting settle conditions:

- Linear speed below 0.05 m/s.
- Angular speed below 0.1 rad/s.
- Continuous supporting contact.
- All conditions maintained for 1.5 seconds.
- Maximum active duration (starting at 15 seconds). An item that has been active longer than this and has supporting contact freezes immediately regardless of speed. An airborne item past the timeout freezes as soon as it lands. Items that leave the playable world (fell through geometry, launched out of bounds) are despawned by the host rather than frozen in place. This is a designer-tunable safety net.

Reset the timer if any condition fails. Low speed alone must not freeze an item at the apex of a throw.

Settle touching items together when freezing one body early would change the others' behavior. Publish exact final poses reliably, clear velocities, make bodies kinematic, switch to ItemSettled, and remove them from motion updates. Retain colliders and pickup interaction.

On remote clients, blend to the reliable final pose over 2–3 frames rather than snapping instantly. The blend duration should be short enough to be imperceptible but long enough to avoid a visible pop.

Track support dependencies so removing a supporting pickup wakes bodies above it.

If an item wakes while overlapping a player who was walking through it, temporarily ignore that specific player until separation. This prevents explosive depenetration while preserving collisions with other players.

All settle thresholds (speed, angular speed, support duration, max active duration, and remote blend duration) should be exposed as tunable values on a single configuration object, not buried in constants. Designers will need to iterate on these values during playtesting.

## 10. Recovery and lifecycle consistency

Maintain a current record of every runtime item, including settled poses and simulator identity. For baked scene items that have been displaced, track their current position so late joiners see them correctly.

Late joiners receive a reliable baseline followed by newer updates. Sequence numbers prevent baseline data and delayed motion packets from overwriting later creation, collection, or settlement events. Handle packets arriving before the corresponding creation event.

If a simulator disconnects, despawn all of that client's in-flight items. Settled items belonging to that client remain in place — they are already frozen and no longer require a simulator. Other clients see the in-flight items disappear, which is acceptable over the alternative of items freezing mid-air or snapping to stale positions. The host broadcasts removal events so late joiners never see orphaned items.

Also handle session teardown, collection while settlement is in flight, items leaving the playable world, and pooled instance cleanup. Pool reuse must clear identity, velocities, collision exceptions, pending hits, and interpolation history.

## 11. Implementation sequence and asset changes

1. Define state layers, item physics settings, and physics-ready prefab configuration; implement local drop and throw.
2. Implement local item collisions, pre-impact waking, piles, and support changes.
3. Implement immediate inventory transactions and network lifecycle records.
4. Add batched snapshots, independent item simulation, and wake-group control.
5. Add player hit proxies, thrower-authoritative hit events, and prediction-safe knockback.
6. Finish settling, late joining, disconnect cleanup, and instance pooling.

Expected integration points:

| File or asset | Planned responsibility |
|---|---|
| ItemDefinition.cs | Per-item physical settings |
| PlayerInventory.cs | Atomic world/inventory transactions and pending quantities |
| PlayerEquipment.cs | Replace with reparent-to-hand-bone logic; remove viewmodel and dual-model spawning |
| PlayerPresentation.cs | Stop hiding the owner's body renderers; remove renderer components instead (first-person arms/hands will be added later) |
| PlayerInputReader.cs and input actions | Drop and Throw actions |
| HudController.cs | Route existing drop operations through the new transaction flow |
| PlayerInteraction.cs | Query moving and settled pickups |
| PlayerMotor.cs | Recorded hit impulses and knockback behavior |
| BakedPickup.cs / PickupRegistry.cs | Collection, displacement tracking, and late-join identity |
| Item prefabs | Physics-ready from spawn: kinematic Rigidbody, colliders, runtime item component; same object used when held and in the world |
| New WorldItemRegistry | Lifecycle, motion batches, and disconnect cleanup |
| SessionRoot prefab | WorldItemRegistry component and references |
| Player prefab | Dedicated item-hit proxy and component |
| Player prefab items | Replay protection for item physics during prediction |
| TagManager.asset / DynamicsManager.asset | State layers and collision matrix |

Configure these through Unity tooling during implementation. Prefer prefab and project-setting changes over editing the game scene. Let Unity generate asset metadata.

## User visual validation

- Compare host and guest throws for immediate local release and smooth remote presentation.
- Throw rocks and mushrooms at identical angles and confirm their configured range and impact differences.
- Hit stationary and moving players; look for a convincing item bounce and a single knockback response.
- Throw items head-on from different clients; confirm both items deflect reactively and converge to consistent positions within a few snapshots.
- Strike settled pickups and piles; confirm directly struck items and immediate neighbors respond. Distant pile members should remain still.
- Walk through settled items, then wake an item beneath a player; confirm no explosive displacement.
- Remove a supporting pickup and confirm unsupported items fall.
- Check wall impacts, slopes, fast throws, and ricochets back into the thrower.
- Pick up moving items, make simultaneous pickup attempts, and try pickup with full inventory.
- Join after items have moved or settled, and disconnect a thrower mid-flight; confirm in-flight items disappear cleanly and settled items remain.
- Repeat with 24 moving items under simulated latency and packet loss. Watch for duplicate knockback, stale items reappearing, collision smoothing through surfaces, and disagreement about resting poses.

Use a development traffic counter alongside these visual checks to confirm settled items stop sending position updates.
