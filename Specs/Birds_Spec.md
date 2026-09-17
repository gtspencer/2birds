# Birds Specification

## Purpose and scope

Birds are a core gameplay feature. Players hunt them with the existing networked rocks and earn individual currency rewards. A single rock must be able to kill multiple birds during one release.

Support approximately 300 living birds across the map, with dozens nearby, in solo play and the existing multiplayer sessions of up to eight players. Bird positions and major actions must remain sufficiently consistent for players to observe and hunt the same targets.

The feature includes all four behavior types, a state machine with behavior drivers, species definitions, habitat and perch authoring, population replacement, scare reactions, hits, death presentation, rewards, and reward UI. It uses Unity 6000.5.7f1, FishNet 4.7.3, and UI Toolkit within the existing game.

The available bird collection contains roughly 100 species with different models and sizes, sharing a rig and animations. Initial implementation supplies the four behaviors and the ScriptableObject definition used to author species. Importing and configuring the full bird collection is separate content work; models, animation assets, and sound clips can be supplied later.

## Species definitions

Each species is defined by a ScriptableObject. Adding a species with an existing behavior type should require content configuration rather than new behavior code.

Species data covers:

- Bird identity, model reference, and size configuration appropriate to that model.
- Behavior type and its supported movement capabilities.
- Relevant movement tuning, including flight speed, ground speed, swimming speed, flight height, and short-flight limits where applicable.
- Idle and travel variation appropriate to the behavior.
- Scare radius and configurable delay, in seconds, between registering a scare and beginning to flee.
- An optional override of the shared minimum lethal impact speed.
- Currency reward per kill.
- Animation references or configuration compatible with the shared rig and animations.
- Optional sound overrides, using shared sound fallbacks when no species override is supplied.

Only settings relevant to a species' behavior need to affect it. Shared defaults should avoid repeating common setup across the bird collection. Exact tuning values remain adjustable content decisions.

## Population and biomes

Designers author spawn zones with:

- A weighted list of species; a list containing a single species is valid.
- Minimum and maximum population values.
- One biome tag.
- A volume defining where the zone spawns birds.
- Replacement timing controls for gradually replenishing killed birds.

At session start, each zone randomly chooses a target population between its minimum and maximum. It maintains that target through gradual replacement of killed birds. Species selection follows the zone's weighted list. Corpses do not count toward the living population.

A bird remains associated with its original spawn zone for population accounting, even after moving outside that zone. Leaving the spawn volume does not create a population vacancy.

Birds may use suitable destinations outside their spawn zone, but destinations must match the zone's biome. The bird's biome restriction lasts for its lifetime. This is a restriction on suitable destinations, rather than a requirement to remain inside the spawn volume.

Replacement birds appear within their original zone's authored habitat. Visible pop-in is acceptable; there is no requirement to hide replacements from players or check camera visibility.

## Perches and habitat authoring

Perch authoring follows the existing rock placement tools' general workflow: designers work with scene volumes and editable generated results.

- Volumes can automatically generate perch points, including points within tree canopies and ground points scattered onto suitable surfaces.
- Designers can also place individual perch markers and adjust generated points manually.
- Perches carry a biome tag and enough authoring information to identify an appropriate landing destination.
- Perches and authoring volumes have clear Scene-view visualization, such as small wire spheres and volume gizmos. These are editor aids, not gameplay markers.
- Generation and editing support normal editor undo behavior.

Each perch accommodates one bird. A bird searching for a destination excludes occupied and reserved perches. Selecting a free destination reserves it so that multiple birds cannot choose it concurrently. A reservation is released if the bird dies or abandons that destination; an occupied perch becomes available when its bird departs.

Birds must continue suitable behavior or retry when no valid perch is available, rather than overlap another bird or select an incompatible destination.

Habitat authoring must also express the usable airspace, ground areas, and water surfaces required by the four behaviors. These destinations follow the same biome restriction. Perch generation does not require every swimming or wandering position to be represented by a perch marker.

## Behavior and movement

| Behavior type | Normal behavior | Escape behavior |
| --- | --- | --- |
| Bush | Idle on tree or ground perches, then fly relatively low between suitable perches. Flight includes natural variation. | Fly away toward another suitable destination. |
| Soaring | Circle and glide within suitable airspace, periodically landing at a suitable perch. | Leave the threat and seek suitable airspace or a landing destination. |
| Water | Fly, land on water, and swim on its surface. Water birds do not walk on shore. | Flee toward a suitable destination using their supported movement. |
| Ground | Idle and wander on the ground. Species may be flightless or capable of very short, low flights. | Flightless species run; short-flight species can escape through low, short flights. |

Birds resume normal behavior after escaping a threat. Escape destinations must remain compatible with their biome and movement capabilities.

Movement avoids terrain, buildings, and solid tree trunks. Navigation can be loose: passing through leaves is acceptable, and birds do not physically collide with one another. The feature does not require detailed navigation through arbitrary clutter.

Birds behave independently. Flocking, coordinated group movement, and fear spreading from one bird to another are excluded.

### State machine and drivers

Birds use a state machine with drivers that determine their available behavior and movement. The four initial behavior types must share common lifecycle and scare handling while allowing their different movement capabilities. The design must accommodate additional behavior types later without duplicating the common bird lifecycle.

The state model must express the relevant activities: idle/perched, takeoff, flight or soaring, landing, ground movement, swimming, waiting to flee, fleeing, and death. These describe required behavior; the exact code-level state arrangement belongs to implementation planning.

The scare driver is shared across bird types. It interrupts normal behavior after the configured delay and chooses an escape compatible with the bird's capabilities. A lethal hit interrupts any living state immediately, including the scare delay, landing, swimming, and fleeing. Death ends normal behavior and releases perch occupancy or reservations.

## Scares

Players, golf carts, and moving rocks can scare birds. Stationary or sleeping rocks do not cause rock-proximity scares.

Proximity alone is insufficient. Nearby threats require line of sight, using a cheap obstruction check against relevant scenery. Favor trigger-based proximity detection followed by a line-of-sight raycast when evaluating a possible scare.

Scares are first detected locally by clients and reported to the server. Trust those reports; server-side resimulation of scare detection is not required. Scare reports communicate events rather than continuously transmitting threat checks.

Once a scare is registered, the bird waits its configured scare delay before fleeing. Weak rock impacts also trigger a scare, without injury, and follow this delay. Repeated reports of the same scare must not repeatedly restart the delay or produce duplicate transitions.

The existing golf-cart horn is an additional scare source. Each honk performs an area overlap check around the cart using a configurable horn radius. Horn scares work through obstacles and do not require line of sight. They use the same delayed scare response.

Birds seek suitable escape destinations and eventually return to normal behavior. Nearby birds do not become scared merely because another bird fled or died.

## Hits and death

### Lethal impacts

- A qualifying rock impact kills a living bird in any state with one hit. There is no injury or partial-health system.
- A rock must meet the minimum lethal impact-speed threshold. Use a shared default with an optional override in each species definition.
- Dropped and bounced rocks can kill when they meet the threshold.
- A weaker rock impact scares the bird without injuring it.
- Fast-moving rocks must register bird hits reliably at supported gameplay speeds.
- Rocks physically collide with living birds and rebound on both weak and lethal hits. Shared settings control normal restitution, upward lift, and reduced underside restitution. Direct underside hits receive no added lift; side and top hits receive full lift, with a smooth transition. Lift scales with incoming normal approach speed and never pushes back into the bird; the item speed cap still applies. Tangential motion is preserved by frictionless bird contacts.
- Rocks can hit additional birds along the resulting ricochet path. Bounces preserve the release identity and multi-kill count. Corpses remain non-colliding with rocks.
- The releaser simulates its released rock; the host takes over from the latest reported motion on disconnect. The host commits inventory, deaths, and rewards. Ordered motion updates prevent delayed packets from undoing a bounce or crossing a release/handoff boundary.
- Golf-cart impacts can also kill birds, with rewards credited to the driver. An unoccupied cart has no driver to receive a reward.

Hit detection may be performed by clients, and client hit reports are trusted. Prioritize responsive local hit feedback. The server coordinates the shared death and reward outcome without requiring server-side hit simulation or replay.

Each bird life can die and award currency only once. Duplicate reports, concurrent hits, and later contacts with its corpse cannot create extra rewards or multiple deaths. A replacement bird is a new life.

### Death presentation and cleanup

A lethal hit produces a feather particle effect and switches the bird to cosmetic whole-body tumbling. Articulated skeletal ragdolls are not required.

The body should fall mostly straight down. Do not directly transfer the rock's impulse to it. A small sideways nudge is acceptable, but the death response must not launch the bird along the rock's trajectory or preserve enough flight momentum to carry it far away.

Bodies collide with scenery for their visual fall and settling, but do not affect players, carts, rocks, or other gameplay bodies. Bodies sink when they enter water.

After a short configurable lifetime, the body smoothly scales down and is returned to a reusable pool. Cleanup must also handle bodies that fall into water or never settle on ground. Pool reuse must restore the correct species appearance and normal scale.

Body motion and tumbling are local cosmetic simulation. Clients may see different poses and final resting positions. Feather simulation is also cosmetic; clients agree on the death that triggers it.

Bird-body pickup and inventory integration are outside this scope.

## Animation and audio

Animation must represent each bird's current activity, including the relevant idle, flight, glide, takeoff, landing, ground-movement, and swimming actions supported by its behavior and supplied clips.

Derive cosmetic animation locally from shared bird motion and major actions where practical. Wingbeat phase, individual bone poses, and exact animation timing do not need to match between clients. Animation must still agree with visible behavior: a perched bird should not appear to be flying, and a dead bird must stop its living behavior.

Support optional ambient-call, scare-call, wing-sound, and hit-sound references. Each sound category can use a species override or a shared fallback. Use the existing sound-effect asset approach where suitable. If neither reference supplies a sound, that category is silent.

Implement the sound slots and playback behavior without requiring sound clips to be available initially. Exact cosmetic sound variation does not require network synchronization.

## Currency and multi-kill rewards

Currency belongs to individual players. Every accepted kill grants the species' configured reward to the rock's releaser or, for a cart kill, the cart's driver. Balances reset between sessions; persistent saves, stores, and spending are excluded.

A rock killing multiple birds during the same release earns a special bonus:

- The first kill grants its normal species reward.
- The second and every subsequent kill grant their normal species reward plus an additional configurable bonus.
- Each rock release tracks its own kill count. Bounces preserve that count.
- Picking up and releasing the rock starts a new count. The rule applies to qualifying thrown or dropped rock releases.
- Duplicate death reports and corpse contacts do not advance the count.
- Cart kills do not contribute to a rock's multi-kill count.

For `n` kills in one release, the total is the sum of the species rewards plus `max(0, n - 1)` additional bonuses. No escalating multiplier or special exactly-two-kills rule is required.

The HUD displays the local player's balance and a brief, unobtrusive reward notification. The notification identifies multi-kill bonuses. Reward UI must not interrupt aiming or require interaction.

## Networking and performance requirements

Prioritize responsive client presentation, consistent gameplay outcomes, and small messages. Trust client hit and scare reports; anti-cheat validation is outside scope.

Clients must agree on:

- Which birds exist, their species, and their association with a spawn zone and biome.
- Live positions and major movement actions sufficiently closely for shared observation and targeting.
- Perch occupancy and destination reservations.
- Scare transitions, deaths, and replacement birds.
- Kill attribution, multi-kill counts, and awarded currency.

Flight randomness must not lead clients to display unrelated paths for the same bird. Major actions and movement need sufficient shared information to maintain the intended consistency. Clients do not need identical wingbeats, particles, body tumbling, or bone poses.

Use compact reports for hits and scares and avoid redundant reports for the same event. Local detection reduces server simulation work; its bandwidth benefit depends on reporting events efficiently and on the chosen movement replication approach.

The host coordinates shared lifecycle decisions, contested destinations, and one-time reward awards. This coordination does not require the host to independently reproduce every client's perception or hit detection.

Late joiners receive the current live population and ongoing major states without restarting the bird population, reviving killed birds, or replaying old rewards. Cosmetic death effects do not require reconstructing an identical historical physics simulation.

The system must support the target population alongside existing rocks, players, and carts. Prefer event-driven state changes, cached references, and reusable bodies/effects. Avoid continuous transmission of idle data, animation parameters, or corpse physics.

Detailed motion replication, simulation scheduling, relevance handling, and message formats belong to the implementation plan. Numerical frame-time, bandwidth, and position-error budgets must be chosen during planning; this specification does not prescribe unmeasured values.

## Scope boundaries

Included authoring support will require scene components for spawn zones, perch volumes, individual perches, and relevant habitats, plus species and shared configuration assets. These are designer-facing systems. This specification does not require automatic edits to the existing game scene or immediate import of the bird collection.

Excluded features are persistent currency, shops and items for purchase, body pickup, skeletal ragdoll physics, flocking, propagated fear, water birds walking on shore, cross-biome destination selection, and hidden-spawn visibility logic.

Detailed class layouts, RPC and serialization designs, exact numerical defaults, animation-controller wiring, content import steps, and implementation sequencing are deferred to planning.

## Visual acceptance review

After implementation and representative content setup, the designer should visually review:

- All four behaviors, including periodic landing by soaring birds, water landing/swimming without shore walking, and both flightless and short-flight ground birds.
- Generated and manually placed perch gizmos, one-bird occupancy, and birds selecting biome-compatible destinations outside their spawn zone.
- Natural flight variation and loose avoidance of terrain, buildings, and trunks.
- Delayed escape from visible threats, no proximity scare from stationary rocks, and horn scares through obstacles.
- The same living birds and major actions across clients, with responsive local hit and scare presentation.
- A single rock ricocheting between and killing multiple birds, including qualifying dropped or bounced impacts, with correct individual rewards and bonus notifications.
- Weak hits causing scares without injury and cart kills crediting the driver.
- Feathers, predominantly downward body falls, modest sideways motion, scenery contact, sinking, and smooth shrinking before cleanup.
- Gradual population replacement, including acceptable visible pop-in, and late joining without population resets or repeated rewards.
- A readable balance display, unobtrusive notifications, and audio fallback/override behavior once clips are supplied.
