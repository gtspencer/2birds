# Slingshot

## Purpose

Introduce a slingshot as the first weapon type. Players can pick it up, equip it, store it in inventory, and drop it. It fires unlimited gravity-affected pebbles using the existing rock press, hold, and release pattern. Each pebble bounces once, then disappears in a dirt particle effect on its second impact.

The weapon, both hands, band, loaded pebble, flight, bounce, and final impact must present consistently to the shooter and other players. Prioritize responsive local play and reuse the existing rock hit detection, damage application, bounce, and networking foundations. Share their implementation wherever possible, adding only the pebble-specific behavior described below.

## Equipment and assets

- Author one slingshot pickup in the starting Game scene using the existing BakedPickup workflow.
- Use normal PlayerInventory and PlayerEquipment pickup, selection, equipment, storage, and drop conventions.
- Slingshots do not stack: each occupies one inventory slot.
- Firing retains the equipped slingshot. It does not remove or release the weapon's world-item instance.
- Ammunition is unlimited. Pebbles are transient projectiles, never inventory entries or collectible world items.
- Neither the slingshot nor its pebbles can become cauldron ingredients. Enforce this for direct insertion and physical intake.
- Use temporary shapes for the initial Y-shaped frame and pebble. There is no pouch; the loaded pebble rests directly on the center of the band.
- Required assets include the slingshot item definition and pickup prefab, a pooled pebble representation, band rendering, and a small dirt impact particle effect. Register the slingshot with the existing item registry.
- Keep model attachment points explicit so temporary geometry can later be replaced without changing gameplay behavior.

## Controls, charging, and recovery

Use the existing Use action, currently left mouse button or controller right trigger. Preserve the rock input pattern, charge HUD, cancellation rules, and equipment permission gates.

1. Pressing Use while permitted and outside recovery begins charging immediately.
2. Holding Use increases charge linearly to full strength over one second. Full charge can be held indefinitely; reaching it does not fire automatically.
3. Releasing Use fires once at the current charge strength, provided a clear launch position exists.
4. A quick tap fires immediately from the pebble's current presented position. It does not wait for the frame to finish centering or the drawing hand to reach full extension.
5. Successful release starts the configurable recovery gate, initially 0.5 seconds. Another draw cannot begin during that gate.
6. Presses during recovery are ignored, not buffered. Holding an ignored press through recovery does not begin charging; a fresh press is required.

Follow current rock conventions when changing equipment. Switching slots, unequipping, or dropping does not reset, restart, or bypass an active recovery gate. Selection and dropping remain available, but use of any selected item waits for recovery to finish. Existing permission-loss and lifecycle handling still governs interruptions.

Cancel charging through the existing equipment paths, including selection changes, dropping, input-context changes, loss of use permissions, and loss of the equipped item. Cancellation removes the loaded pebble and returns the presentation toward idle without firing or starting a new shot cooldown.

Preserve existing movement and interaction permissions. Charging adds no movement penalty: walking, sprinting, jumping, and permitted cart-passenger use follow current equipment behavior. Driving, being carried, downed state, and other equipment restrictions continue to use the existing permission gates.

## Tuning

| Setting | Initial value | Configurable |
| --- | --- | --- |
| Minimum launch speed | 8 m/s | Yes |
| Maximum launch speed | 28 m/s | Yes |
| Time to full charge | 1 second | Yes |
| Player damage per hit | 10 health | Yes |
| Recovery after firing | 0.5 seconds | Yes |
| Speed retained after the first impact | 75% | Yes |
| Maximum projectile lifetime from firing | 30 seconds | Fixed requirement |

Launch speed interpolates linearly between the configured minimum and maximum using charge progress. Damage does not scale with charge or projectile speed. Bounce retention describes a fraction of incoming speed, not kinetic energy.

Use the project's gravity and the existing rock movement-velocity inheritance, including cart motion. Do not introduce a competing force setting for the same launch-speed behavior.

## Hands, frame, band, and loaded pebble

Use the existing hand IK and held-item presentation infrastructure for both local first-person hands and remote avatars.

- The right hand holds the frame at idle and throughout use.
- The left hand comes up to draw the center of the band. It follows the same draw position as the loaded pebble.
- On charge start, show the loaded pebble and move the frame toward the center of the screen beneath the crosshair over approximately 0.15 seconds.
- While the frame centers, the left hand continues stretching the band over the full charge duration. Band extension and the HUD charge fraction must agree; frame centering uses its separate, shorter timing.
- Remote avatars show the corresponding centered aiming pose and two-handed draw, using suitable avatar-space poses rather than copying first-person offsets literally.
- Represent the band as two procedural segments joining the fork tips to a shared movable center. Full elastic rope physics is unnecessary.
- On release, launch the pebble immediately, snap the band forward, and apply a brief damped settling wobble.
- Return the frame and hands toward idle over approximately 0.2 seconds. Finishing that visual return does not shorten the recovery gate.
- Cancellation returns toward idle without creating a projectile or playing a firing release.

The slingshot remains held and visible during firing and recovery. Do not reuse the rock follow-through that tracks a released equipped item or hides the next held item. The pulling hand releases the band and returns; it does not follow the flying pebble.

Drive remote charge and recovery presentation from the existing replicated item-action state and timing. Do not stream band points, hand targets, or charge percentages every frame.

## Aim and launch clearance

Use the existing crosshair and charge bar. Aim from the loaded pebble's current position toward the point under the camera crosshair. When the crosshair has no target, aim into the corresponding view direction.

There is no random spread, target assistance, or automatic elevation compensation. Gravity curves the shot after launch, so players must account for drop and travel time.

Preserve existing held-item obstruction correction and release-clearance behavior. Near a wall, clearance may shift the frame away from its preferred centered pose. If no clear pebble launch position can be obtained, cancel without firing or starting cooldown. Never create a projectile through an obstruction. A valid launch that immediately encounters a nearby surface produces a normal first-impact bounce.

## Projectile physics and impacts

Simulate pebbles with gravity and the existing rock Rigidbody simulation foundations. Use collision handling appropriate for small, fast projectiles.

Each live pebble has two impact stages:

1. **First impact:** apply any eligible target reaction, reflect off the impact surface, and retain the configured fraction of incoming speed. The initial retention is 75%.
2. **Second impact:** apply any eligible target reaction, remove the pebble, and play the small dirt effect at the resolved contact position and normal.

Use the same rebound rule across eligible surfaces. Do not add the rock system's bird-specific upward boost to pebbles. Gravity continues after the bounce.

Count distinct impacts rather than individual contact points or repeated contact-stay callbacks. Simultaneous contacts count as one impact. Returning to the same floor, wall, or object after the bounce counts as the second impact; the second target need not be a different object.

The dirt burst accompanies second-impact destruction. Lifetime expiration and kill-volume removal do not produce an impact effect in empty space.

### Collision targets

| Target | Pebble response | Target response |
| --- | --- | --- |
| Solid scenery, terrain, and solid cauldron geometry | Counts as an impact | No gameplay damage or impulse |
| Living players | Counts as an impact | Fixed configured damage using the rock system's contact and repeat-hit rules |
| Shooter after initial launch clearance | Counts as an impact | Same player damage rules |
| Living birds | Counts as an impact | Existing rock bird-hit rules and rewards |
| Golf carts | Counts as an impact | No damage or impulse |
| Loose items and other solid physics props | Counts as an impact | No damage or impulse |
| Downed player bodies | Counts as an impact | No additional damage or impulse |
| Held items and other pebbles | Ignore | None |
| Ordinary trigger volumes | Ignore | None |
| Existing item kill volumes | Remove immediately | No dirt burst |

Ignore the shooter while the projectile clears its initial launch position. After that clearance, a returning shot can hit its owner.

Prevent both explicitly scripted shove and Rigidbody contact impulses from pebbles to targets. This applies on the simulator and remote clients: a remote kinematic projectile must not push dynamic objects. Pebble contacts must not introduce cart crash forces. The pebble itself still rebounds.

## Damage and birds

Either impact may deal the configured player damage, initially 10. Reuse the rock system's contact and repeat-hit rules: a later eligible contact may damage the same player again, and one pebble may damage different players. There is no once-per-pebble player-damage limit. Apply the same rules to the shooter after launch clearance.

Use the existing rock player-hit detection and victim-owner damage path, including its local collision/sweep handling. Keep the same detection authority, contact re-arming, and presentation timing as rocks. Apply the pebble's fixed damage through that shared path without adding a per-shot player hit history. Use existing health and zero-health/downed behavior. Damage can reduce a living player to zero; there is no nonlethal health floor. Downed players do not receive further damage.

For birds, reuse existing rock hit-speed thresholds, species overrides, kill/scare behavior, rewards, and applicable multi-hit scoring. Ricochet hits participate in those rules. Retain ordinary bird death presentation without transferring projectile momentum to the corpse.

## Networking and pooling

Reuse the rock solution's owner-simulated Rigidbody motion, motion serialization, observer interpolation and correction, reliable trajectory-boundary delivery, and pooling foundations. The shooter simulates immediately; the server relays state and observers follow replicated motion. Preserve existing simulator-disconnect handling.

The projectile simulator decides physical collision order, bounce results, bird hits, and final impact. Player hits use the existing rock detection on the victim's client and apply damage through the existing victim-owner health path. Preserve that division of responsibility: a victim-local damage detection does not choose a new rebound or advance the simulator's impact count. Do not add shooter-resolved player-hit delivery or a second damage detector. Player damage follows the same client-local contact timing as rocks rather than requiring exact agreement with the simulator's contact list.

Replicate the first bounce and terminal impact reliably. Carry the resolved final contact position and normal so every client places the dirt effect at that contact, rather than at an older motion sample. Avoid duplicate bounce or effect application from repeated network delivery, and use the existing rock contact bookkeeping to avoid processing the same player contact twice. A later eligible contact remains able to damage that player again.

Use existing motion-update conventions instead of a separate per-frame projectile transform stream. Keep additional impact and lifecycle messages compact. Non-simulators must not independently choose a conflicting rebound or terminal impact; their victim-local player-hit checks remain active.

Represent live pebble state sufficiently for the existing observer and simulator-handoff lifecycles, including remaining impact state and lifetime. Reuse the rock system's contact rebasing during handoff; do not restart a shot's lifetime, grant another bounce, or replay an already processed contact. No per-shot damaged-player list is required. Process the final valid contact segment before retiring local hit-detection state; do not extrapolate damage beyond the terminal trajectory.

Return pebbles to pools on second impact, lifetime expiry, or kill-volume removal. Reset all shot-specific state before reuse, including velocity, ownership, impact count, contact bookkeeping, timing, and presentation state. Unlimited ammunition must not leave one permanent registry or late-join record for every historical shot.

Reuse or extract the shared rock motion, player-hit detection, damage application, bounce/contact handling, and pooled-lifecycle logic rather than treating ammunition as an unchanged WorldItem. Preserve inventory-item behavior for rocks and the slingshot frame while keeping pebbles outside pickup, inventory, and cauldron flows. Logic crossing these boundaries belongs in shared classes; no general-purpose weapon framework is required for this feature.

## Manual acceptance scenarios

- On two clients, observe the right-hand frame, left-hand draw, loaded pebble, centering motion, full-charge band extension, release snap, and return. Local and remote presentation should describe the same action.
- Tap immediately, release partway through charging, and hold at full charge. Confirm launch timing, increasing speed, visible gravity, and the existing charge UI.
- Fire at a floor so the pebble lands on it again. Observe one bounce followed by a dirt burst and removal at the second impact on both clients.
- Hit players before and after a bounce, revisit the same player, and return a shot toward its shooter. Confirm fixed damage, repeat-hit behavior consistent with rocks, and no knockback.
- Hit birds, carts, loose items, and downed bodies. Confirm bird reactions and rewards, appropriate impact counting, and no projectile-induced movement of hit physics objects.
- Fire, then switch, unequip, or drop during recovery. Confirm normal equipment selection remains available while use stays gated until the original recovery ends. A press during recovery must require a fresh press afterward.
- Charge near walls and release from obstructed positions. Confirm clearance takes precedence over centering, blocked launches cancel without cooldown, and pebbles do not originate through walls.
- Pick up, store, equip, and drop the slingshot. Confirm one weapon per slot, unlimited ammunition, no collectible pebbles, and rejection of both slingshots and pebbles by cauldron intake.
- Fire from elevated terrain or into open space. Observe continued gravity and removal on the second impact, at 30 seconds, or in a kill volume. Only second-impact destruction produces the dirt burst.
