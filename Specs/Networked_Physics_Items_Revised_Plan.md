# Networked physics items: revised implementation plan

## Objective

Make inventory items droppable and throwable, with natural trajectories influenced by the environment, players, vehicles, and other items.

Prioritize immediate response for the interacting player while keeping world physics authoritative and consistent across clients.

The game may contain hundreds of physical world items, especially rocks, but only a relatively small number should normally be awake and moving at once. Sleeping rigidbodies should generate no continuous network traffic.

Initial target:

- Hundreds of world items may exist simultaneously.
- Approximately 12–24 items may be actively moving at once under normal gameplay.
- Active counts may temporarily exceed this during pile impacts or chaotic interactions.
- Settled items remain dynamic rigidbodies but are allowed to sleep.
- Sleeping items send no motion updates.

---

## Recommended architecture

Use real Unity `Rigidbody` physics with the host/server as the authority for world-object simulation.

The host simulates all active world physics interactions, including:

- item ↔ environment
- item ↔ item
- item ↔ player hit proxy
- item ↔ vehicle or other networked physics object

Clients do not retain ownership of thrown item physics for the full flight.

Instead:

1. The throwing client immediately launches a local predicted representation for responsiveness.
2. The client sends the throw request and initial state to the host.
3. The host launches the authoritative rigidbody.
4. The host simulates the complete coupled physics interaction.
5. The host broadcasts batched motion snapshots for awake items.
6. Clients reconcile predicted/local presentation toward authoritative state.

This avoids splitting one physical interaction across multiple independently authoritative clients.

FishNet handles:

- lifecycle transactions
- inventory/world consistency
- object identity
- player prediction
- authoritative hit events
- snapshot transport
- late joining

The host is authoritative for ordinary world physics. For this noncompetitive game, validation can remain lightweight; the host does not need expensive anti-cheat validation beyond basic sanity bounds.

---

## Why this authority model

Do not make the releasing client authoritative for a rock for its entire flight.

A physics interaction such as:

`Rock A → Rock B → Rock C → player → cart`

is a coupled system. Splitting authority by object creates ambiguity over which client determines impulses, wakes pile members, or resolves secondary collisions.

Host-authoritative simulation guarantees that all active rigidbodies participating in the same interaction are solved by the same Unity physics world.

Clients still get immediate responsiveness through local throw prediction.

---

## Gameplay assumptions

- Picking up an item adds it to the existing inventory.
- Throwing or dropping the selected item releases one unit.
- Dragging a stack out of inventory creates one world item containing that stack.
- Stack quantity does not automatically multiply physical mass.
- Moving items can hit players, moving items, sleeping items, vehicles, and environment geometry.
- Player reactions use controlled knockback rather than fully coupled player ragdoll physics.
- Damage rules remain a separate gameplay decision.
- All world items use the same physics-ready representation.
- Held items are the same logical world item placed in a held state.
- Sleeping items remain dynamic rigidbodies rather than being converted to kinematic objects.

---

# 1. Item states and collision layers

Use gameplay states separately from Rigidbody sleep state.

Recommended logical states:

- `Held`
- `World`

Within `World`, Unity determines whether the rigidbody is awake or sleeping.

Recommended collision roles:

| State/layer | Environment | World items | Player item hitboxes |
|---|---|---|---|
| ItemWorld | Collides | Collides | Collides |
| ItemHeld | No physics collision | No | No |

Do not create a separate `ItemSettled` layer solely to represent resting physics.

A settled rock should generally remain:

```csharp
rb.isKinematic = false;
rb.IsSleeping() == true;
```

Unity's physics system can automatically wake it when another dynamic object strikes it or a force is applied.

Use `ItemDefinition` for item identity and physical properties rather than encoding item type in physics layers.

Add a `PlayerItemHitbox` layer for dedicated item collision proxies.

Item bodies should ignore the primary player movement collider to prevent the player's movement controller and world-object physics from producing duplicate collision responses.

Pickup queries should include world items but exclude held items and player item hitboxes.

Environment occlusion should remain enabled so players cannot pick up an item through walls.

---

# 2. Configurable item physics

Extend:

`Assets/Game/Runtime/Items/ItemDefinition.cs`

with per-item physics settings.

| Setting | Purpose |
|---|---|
| Throw speed | Primary throw-distance control |
| Drop speed | Small release push |
| Inherited player velocity | Momentum inherited from movement/falling |
| Mass | Collision response |
| Linear damping | Air resistance |
| Angular damping | Spin decay |
| Initial spin | Tumbling after release |
| Physics material | Friction and bounce |
| Collision detection mode | Discrete/continuous selection |
| Max speed | Optional sanity/safety bound |
| Sleep threshold overrides | Optional per-item tuning |

At release:

```text
initial velocity =
    aim direction × release speed
    + player velocity × inheritance
```

Apply velocity and angular velocity once and let Unity physics determine the trajectory.

Mass should affect impact response, not throw distance.

Use simple colliders wherever possible.

Use continuous collision detection only where required by expected projectile speed.

---

# 3. Sleeping rigidbodies instead of custom settling

Do not manually convert settled rocks into kinematic bodies.

Keep world items as dynamic rigidbodies and allow Unity to sleep them naturally.

When a rigidbody is sleeping:

- it remains physically present
- it does not require continuous simulation work
- it does not send network motion snapshots
- it can automatically wake from collision or force

The host tracks an `ActivePhysicsItems` collection.

A world item enters this collection when its Rigidbody becomes awake.

It leaves when it has returned to sleep.

When an item transitions to sleep:

1. send one reliable final/rest state
2. record its authoritative pose
3. remove it from motion snapshot batches

When it wakes:

1. mark it active
2. begin sending motion snapshots
3. continue normal host simulation

Avoid custom support-dependency graphs unless playtesting proves they are needed.

If removing a rock underneath a pile causes Unity to wake/fall the unsupported items correctly, rely on that behavior.

---

# 4. Immediate inventory-to-world transactions

Replace deletion-only drop operations in:

`Assets/Game/Runtime/Inventory/PlayerInventory.cs`

with complete inventory-to-world transactions.

On Drop or Throw:

1. Immediately reserve the local inventory quantity.
2. Update local inventory presentation.
3. Transition or spawn the local item representation immediately.
4. Launch the local predicted visual/physics representation.
5. Send a reliable request containing:
   - operation ID
   - item definition ID
   - quantity
   - release pose
   - initial linear velocity
   - initial angular velocity
6. The host commits the inventory transaction once.
7. The host assigns or confirms the world item ID.
8. The host launches the authoritative Rigidbody.
9. Remote clients begin presenting the item from authoritative state.
10. The initiating client associates the authoritative ID with its predicted instance.

Operation IDs make requests idempotent.

Pending quantities prevent rapid inputs from spending the same inventory units twice.

---

# 5. Held-item behavior

When an item is picked up:

- the host resolves the pickup claim
- the item enters `Held`
- physics collisions are disabled
- its Rigidbody becomes kinematic while held
- it is attached to the player's held-item transform or hand bone
- continuous world motion snapshots stop

Remote clients attach their representation of the same item to the corresponding player's hand.

Simultaneous pickup claims are resolved by the host in arrival order.

The losing client rolls back its optimistic pickup.

The winning client should still see the pickup immediately through local prediction.

If the host rejects a pickup because of:

- another player's claim
- insufficient inventory capacity
- stale state
- invalid item state

restore the local representation to authoritative world state.

---

# 6. Local throw prediction and simple reconciliation

The throwing player should not wait for host round-trip latency before seeing the throw.

On local input:

```text
Input
  ↓
launch local predicted rock immediately
  ↓
send throw request
  ↓
host launches authoritative rock
  ↓
authoritative snapshots arrive
  ↓
compare against local prediction
```

The local prediction exists only to improve responsiveness.

It is not authoritative.

For ordinary ballistic flight, predicted and authoritative trajectories should usually remain very close.

Do not rollback and replay the surrounding physics world.

## Visual structure

Locally predicted items should separate simulation state from rendered presentation:

```text
RockRoot
├── Rigidbody / Collider
└── VisualRoot
    └── Mesh
```

`RockRoot` represents the locally predicted/authoritative physics state.

`VisualRoot` exists so visible corrections can be smoothed independently from collision state.

## Minimal reconciliation rules

Use only three rules initially.

### 1. Compare equivalent simulation ticks

The client should retain a short history of predicted state:

- position
- rotation
- linear velocity
- angular velocity
- simulation tick

Authoritative snapshots must include the host simulation tick.

When a host snapshot arrives, compare it against the client's predicted state for the corresponding tick rather than against the client's current position.

This prevents ordinary network delay from being mistaken for prediction error.

### 2. Ignore small errors

If prediction is already sufficiently close to the authoritative state, do nothing.

Do not continuously correct harmless centimeter-scale drift.

Start with a simple configurable positional error threshold and tune it during playtesting.

A reasonable initial test value for a typical rock is approximately:

`0.05 m`

The exact threshold should remain configurable because item scale and game feel may change.

### 3. Correct physics, smooth visuals

If error exceeds the threshold:

1. correct `RockRoot` to the authoritative physics state
2. preserve the visible world pose of `VisualRoot`
3. represent the correction as a temporary local visual offset
4. decay that offset toward zero over a short duration

Example:

```text
Before correction:

Physics position: 5.0
Visual position:  5.0

Authoritative position: 5.3

Immediately after correction:

Physics position: 5.3
Visual position:  5.0
Visual offset:   -0.3

Over a short smoothing window:

Visual offset → 0
```

Start with a visual correction duration around:

`50–100 ms`

and tune through playtesting.

The authoritative collider should become correct immediately; only rendering should visually catch up.

## Collision disagreements

Do not build special collision-branch detection initially.

If a locally predicted bounce differs from the host:

- accept the authoritative physics state when the error exceeds the normal threshold
- use the same `VisualRoot` offset decay to hide the correction

Only add specialized collision reconciliation if normal playtesting shows that this is visibly inadequate.

## Important rules

- Do not put locally predicted throws through the normal remote interpolation buffer.
- Do not slowly move the actual collider through walls or other geometry.
- Correct simulation state first and smooth rendering second.
- Do not chase perfect synchronization when the discrepancy is visually irrelevant.
- Never rewind or replay the surrounding item physics world on the client.

---

# 7. Host-authoritative world collisions

The host simulates all world-object collisions.

This includes:

- thrown rock ↔ sleeping rock
- thrown rock ↔ moving rock
- pile reactions
- rock ↔ vehicle
- rock ↔ player hit proxy
- moving item ↔ other networked physics object

Do not transfer temporary physics authority when one item strikes another.

Do not create per-impact simulation groups.

Do not independently simulate one half of an item-item collision on different clients.

All rigidbodies participating in the interaction should be solved inside the same authoritative host physics world.

---

# 8. Player collision and knockback

Add a dedicated collision proxy for each player on `PlayerItemHitbox`.

The proxy represents the player's physical target for world items.

The main player movement collider should not also participate in item collision response.

The host determines canonical rock/player collisions.

When the authoritative world physics detects a meaningful hit:

1. calculate contact information and impulse
2. create a unique hit event
3. associate it with the simulation tick
4. feed the impulse into the predicted player movement system
5. broadcast/replicate the event as needed

A hit event should contain enough information to reproduce the gameplay response:

- event ID
- target player
- source world item
- authoritative tick
- impulse
- optional contact point
- optional contact normal

Knockback should be represented as a discrete external impulse rather than ordinary sustained force.

`PlayerMotor.cs` should record external impulses by prediction tick so FishNet reconciliation does not erase them.

Replay must deduplicate hit IDs so the same impulse is never applied twice.

Normal movement acceleration should not immediately cancel knockback.

The throwing client may predict cosmetic hit feedback immediately, but the host determines the actual gameplay impulse.

---

# 9. FishNet prediction integration

World item physics is not part of player prediction replay.

During player reconciliation:

- replay player movement inputs
- replay authoritative external impulses
- do not advance the world physics simulation again
- do not generate new item collision events
- do not generate new hit events

Use FishNet's prediction-compatible handling for nonpredicted rigidbodies where appropriate.

The project should continue using the existing FishNet-controlled physics clock.

Do not add an independent global physics simulation loop.

---

# 10. WorldItemRegistry

Add a `WorldItemRegistry` network component to `SessionRoot`.

Avoid requiring a `NetworkObject` per rock unless FishNet implementation constraints later make that materially simpler.

The registry manages:

- world item identity
- lifecycle state
- held/world transitions
- current authoritative pose
- active/sleeping state
- motion batching
- late-join baseline
- inventory/world transactions
- pooling
- out-of-bounds cleanup

Suggested structure:

```text
WorldItemRegistry
├── ItemsById
│   ├── item ID
│   ├── definition
│   ├── quantity
│   ├── Rigidbody
│   ├── collider references
│   ├── state
│   └── authoritative pose
│
└── ActivePhysicsItems
    └── currently awake/moving items only
```

Hundreds of sleeping rocks may remain registered while only a small active subset participates in motion networking.

---

# 11. Batched motion snapshots

Only the host sends authoritative world-physics motion updates.

Starting rates:

- physics: 60 Hz using the existing simulation clock
- motion snapshots: 15–20 Hz
- optional immediate/burst snapshot after major collision
- reliable final state on sleep
- reliable lifecycle events

Motion entries contain:

- item ID
- sequence/tick
- position
- rotation
- linear velocity
- angular velocity

Do not resend definition or quantity during ordinary motion updates.

Use unreliable delivery for ordinary motion snapshots.

Each snapshot should be independently usable.

Use reliable delivery for:

- item creation
- pickup
- held/world transition
- inventory commit
- sleep/final pose
- destruction/despawn
- hit events if required by gameplay
- important lifecycle changes

Split batches below transport packet limits.

Quantize position/rotation/velocity only after profiling establishes useful ranges.

---

# 12. Remote presentation

Remote clients should ordinarily render host-authoritative items using buffered interpolation.

Recommended behavior:

- maintain a short interpolation buffer
- interpolate pose between authoritative samples
- allow only brief extrapolation
- correct velocity changes quickly after impacts
- avoid interpolating through collision surfaces when possible

Remote presentation does not need fully authoritative local Rigidbody simulation.

For ordinary noninteractive presentation, a client may use a kinematic or presentation-driven representation as long as it does not incorrectly participate as a mass-bearing authority in local gameplay.

The local throwing client is the primary case where predicted dynamic presentation is useful.

Locally predicted items use the reconciliation path in §6 rather than the ordinary remote interpolation buffer.

---

# 13. Wake/sleep networking

The host does not need a custom "wake before impact" sweep solely to convert kinematic settled items.

Sleeping dynamic rigidbodies naturally participate in impacts and can wake from contact.

Networking should observe state changes:

```text
Sleeping
  ↓ collision/force
Awake
  ↓
add to ActivePhysicsItems
  ↓
send motion snapshots
  ↓
Unity eventually sleeps body
  ↓
send reliable final pose
  ↓
remove from ActivePhysicsItems
```

An optional wake event may be sent when useful for presentation, but snapshots themselves may be sufficient.

Do not generate network traffic for every resting contact.

---

# 14. Large piles

Use Unity physics normally first.

Do not implement custom pile ownership, support graphs, or propagated wake groups until profiling or playtesting demonstrates a concrete problem.

For large piles:

- use simple convex colliders
- avoid MeshCollider where possible
- tune solver iteration counts only if needed
- allow sleeping aggressively
- avoid unnecessary continuous collision detection
- profile broadphase cost
- cap pathological velocities
- consider collision-layer simplification for nonessential interactions

If picking up a supporting rock destabilizes the pile, allow Unity to wake affected rigidbodies naturally.

If a newly awakened item overlaps a player, temporarily ignoring that specific player until separation may still be useful to prevent explosive depenetration.

---

# 15. Release collision handling

Immediately after a throw, temporarily ignore collision between the released item and the releasing player's item-hit proxy.

Restore the collision when either:

- the item has separated from the player's hitbox, or
- a configurable grace period expires

Starting grace period:

`0.3 seconds`

Use per-collider collision exceptions.

Do not change the global collision matrix for a single throw.

Clear collision exceptions when pooled objects are reused.

This allows later ricochets to hit the original thrower.

---

# 16. Out-of-bounds and runaway physics

The host handles cleanup.

If a world item:

- falls below the world
- enters a kill volume
- exceeds designer-defined bounds
- becomes physically invalid

the host despawns or resets it according to item rules.

Do not freeze airborne items after an arbitrary active timer.

A maximum-active-time safeguard may still exist for pathological cases, but it should not convert an item to kinematic while airborne.

Prefer:

- velocity clamp
- out-of-bounds cleanup
- forced sleep only when supported and nearly stationary

---

# 17. Disconnect behavior

Host authority removes the need to despawn in-flight items when their throwing player disconnects.

A thrown rock is a world object, not an extension of the throwing client.

If the thrower disconnects:

- the rock continues moving
- collisions continue normally
- the host remains authoritative
- the item can settle and remain in the world

Held items should follow the game's normal player-disconnect inventory policy.

---

# 18. Late joining

Maintain authoritative state for every world item.

Late joiners receive a reliable baseline containing:

- world item ID
- definition ID
- quantity
- state
- pose
- velocity if currently active
- holder if currently held

After baseline delivery, normal snapshots continue.

Sequence numbers/ticks prevent older baseline or delayed packets from overwriting newer state.

Handle packets arriving before their corresponding lifecycle creation record.

---

# 19. Pooling

World items should be pooled where useful.

Pool reuse must clear:

- world item identity
- quantity
- Rigidbody velocity
- angular velocity
- sleep/awake state
- interpolation history
- prediction history
- holder state
- collision-ignore exceptions
- pending hit data
- stale sequence numbers
- stale lifecycle state

Cache:

- Rigidbody
- colliders
- definition references
- renderer references
- network-facing runtime component references

Avoid per-frame allocations.

---

# 20. Bandwidth expectations

Network cost scales primarily with awake items, not total item count.

If the world contains:

- 500 registered rocks
- 480 sleeping
- 20 moving

only approximately 20 require continuous motion snapshots.

At 15–20 Hz, batched custom serialization should be inexpensive relative to sending independent `NetworkTransform` updates for every item.

Do not optimize exact byte packing prematurely.

First establish:

- maximum realistic simultaneous awake count
- typical snapshot size
- packet fragmentation behavior
- host outbound bandwidth
- client inbound bandwidth

Then tune quantization and rates.

---

# 21. Recommended implementation sequence

1. Define item physics settings and simple world/held collision layers.
2. Make all world items real dynamic rigidbodies that can naturally sleep/wake.
3. Implement local-only drop and throw.
4. Implement host-authoritative item simulation.
5. Add immediate client-side throw prediction.
6. Implement inventory-to-world operation IDs and commit/rollback behavior.
7. Add `WorldItemRegistry`.
8. Add host-only `ActivePhysicsItems`.
9. Add batched unreliable motion snapshots.
10. Add reliable rest/final-pose updates.
11. Add remote interpolation and local prediction reconciliation.
12. Add player item-hit proxies.
13. Route authoritative rock/player impacts into prediction-safe player knockback.
14. Add late-join baselines.
15. Add pooling and out-of-bounds cleanup.
16. Profile hundreds of sleeping rocks and escalating active-body counts.
17. Only add more complex pile handling if profiling/playtesting proves necessary.

---

# 22. Expected integration points

| File or asset | Planned responsibility |
|---|---|
| `ItemDefinition.cs` | Per-item physical settings |
| `PlayerInventory.cs` | Atomic inventory/world transactions and pending quantities |
| `PlayerEquipment.cs` | Held-item attachment and state transitions |
| `PlayerInputReader.cs` and input actions | Drop and throw input |
| `HudController.cs` | Route drop actions through transaction flow |
| `PlayerInteraction.cs` | Query and collect world items |
| `PlayerMotor.cs` | Tick-recorded external hit impulses and knockback |
| `BakedPickup.cs` / `PickupRegistry.cs` | Initial world registration and persistent identity |
| Item prefabs | Rigidbody, colliders, runtime item component |
| New `WorldItemRegistry` | Identity, lifecycle, active-body batching, snapshots, late join |
| `SessionRoot` prefab | Registry component and references |
| Player prefab | Dedicated item-hit proxy |
| Player prediction integration | Prevent world physics from replaying during reconciliation |
| `TagManager.asset` / `DynamicsManager.asset` | Collision layers and matrix |

Prefer prefab and project-setting changes over editing the gameplay scene directly.

Let Unity generate asset metadata.

---

# 23. Validation scenarios

## Basic responsiveness

- Host throws a rock.
- Guest throws a rock.
- Both should see immediate release locally.
- Remote observers should see smooth authoritative motion.

## Prediction/reconciliation

- Throw into open space.
- Verify local prediction stays close to host state.
- Introduce latency and packet loss.
- Verify corrections remain visually small.

## Rock ↔ rock

- Throw one rock into another sleeping rock.
- Confirm the host wakes and simulates both.
- Confirm all clients converge on the same outcome.

## Simultaneous throws

- Two different clients throw rocks into each other.
- Confirm one authoritative host physics result.
- Ensure no ownership transfer or simulation-group logic is needed.

## Piles

- Throw into a pile of sleeping rocks.
- Confirm nearby bodies wake naturally.
- Confirm distant stable rocks remain asleep when Unity determines they are unaffected.
- Remove a supporting rock and confirm affected rocks fall.

## Player hits

- Hit stationary player.
- Hit moving player.
- Hit player under simulated latency.
- Verify exactly one gameplay knockback impulse.
- Verify client prediction reconciliation does not remove or duplicate it.

## Vehicles/networked physics

- Throw a rock into a cart or other dynamic world object.
- Verify both are resolved by the host physics world.
- Verify all clients converge.

## Ricochet

- Throw against a wall and ricochet back toward the thrower.
- Confirm initial self-collision ignore ends correctly.
- Confirm later impact with the thrower works.

## Pickup race

- Two clients try to pick up the same rock.
- Host chooses one winner.
- Losing client cleanly rolls back.

## Moving pickup

- Attempt to pick up a rolling/bouncing rock.
- Confirm transition to held state does not duplicate or lose the item.

## Late join

- Move and settle several rocks.
- Join another client.
- Verify exact final positions.
- Leave some rocks moving during join and verify active state continues correctly.

## Disconnect

- Throw a rock and disconnect the thrower.
- Verify the rock continues moving and interacting normally.

## Scale

Test progressively:

- 500 sleeping / 0 awake
- 500 sleeping / 12 awake
- 500 total / 24 awake
- 500 total / 50 awake
- large temporary pile reaction

Measure:

- host physics time
- client frame time
- snapshot bandwidth
- allocation rate
- packet loss behavior
- interpolation quality
- reconciliation frequency

---

# 24. Core design rules

1. **World physics is host-authoritative.**
2. **The throwing client predicts for responsiveness but does not own the rock's full flight.**
3. **All coupled object-object collisions are solved in one authoritative physics world.**
4. **Settled rocks remain dynamic and sleep instead of becoming kinematic.**
5. **Only awake items generate continuous network motion updates.**
6. **Hundreds of total rocks are acceptable if only a small subset are active.**
7. **Player knockback enters FishNet's prediction/reconciliation path as ticked external impulses.**
8. **Do not rollback or replay the entire client physics world.**
9. **Do not build custom pile/ownership machinery until actual profiling demonstrates a need.**
10. **Prefer simple physics, simple colliders, batching, sleeping, and authoritative convergence over distributed physics ownership.**
11. **Keep reconciliation minimal initially; advanced collision-aware techniques should only be added if playtesting demonstrates a visible problem.**
