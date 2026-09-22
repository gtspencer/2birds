# Boulder and Heavy Hold

## Purpose

Introduce a large, collectible boulder that players can store, hold with both hands, throw a short distance, and craft from three small rocks. A sufficiently fast loose boulder damages and shoves players. Extend the existing item presentation with a reusable Heavy hold mode while retaining the existing inventory, charge throwing, hand IK, physics, networking, and cauldron systems.

## Player behavior

- A boulder occupies one inventory slot and does not stack. Players may carry multiple boulders when space permits.
- Only one item can be equipped at a time. Hand and Heavy are presentation modes for the selected item, not independent equipment slots.
- Holding or storing a boulder does not change walking, sprinting, jumping, or inventory capacity.
- A held boulder rests around the avatar's stomach, with both palms on its sides. At a normal forward view, only the top of the boulder should be visible in first person.
- Charging raises the boulder toward an overhead pose. Releasing at any point throws immediately from its current visible position with strength determined by the charge reached.
- Loose boulders can roll downhill, be pushed by players and golf carts, and become dangerous through any source of motion.

## Heavy hold presentation

### Item settings and grips

Add a hold-mode choice to `ItemDefinition`, defaulting to the existing Hand behavior. Heavy selects the stomach hold, two-hand grip, overhead charge pose, and two-hand recovery. It does not implicitly select boulder damage, throw strength, mass, or sleeping collision behavior.

Heavy items have two authored child transforms identifying the left and right palm contact positions and orientations. Discover these transforms automatically and cache the references during setup. Do not generate contact positions from mesh or collider bounds.

Use `HeldItemSettings` and the existing per-item override mechanism for pose tuning. Reuse the existing hand targets and IK. The heavy item's body-relative pose determines both hand targets; its final position must not depend solely on the solved right palm.

### Body placement and scale

The stomach pose follows the avatar's position and yaw, independently of camera pitch. Looking up or down does not tilt or reposition the boulder as a camera-aimed item. Throw direction still follows camera aim.

Derive the body placement from existing avatar measurements without requiring authored stomach or overhead anchors on every avatar. Preserve the boulder's authored size, approximately 0.61 metres across. Author the grips and tune the poses for supported avatars while respecting existing arm reach limits. Do not resize the boulder per avatar or stretch arms to force contact.

Account for the prefab's authored rotation, scale, and offset geometry/collider center when placing grips and calculating clearance.

### Charge, release, and recovery

- Begin the short lift when charging starts. Reuse the existing charge-pose timing, initially 0.35 seconds, independently of gameplay charge duration.
- Hold the boulder overhead once the lift finishes, until release or cancellation.
- Early release does not queue or wait for the overhead pose. Release immediately from the current committed visible pose, including during the lift.
- Reuse `ThrowableItemUse` for charge strength and the existing movement inheritance calculation. Do not add a separate heavy throw input or charge system.
- Open both hands on release. Apply the existing recovery behavior to both hands, initially 0.20 seconds of follow-through, no pause, and 0.20 seconds of return.
- Keep existing recovery restrictions on subsequent uses and existing behavior when the selected item changes during recovery.
- Cancelling an unfinished charge uses the existing return blend and does not throw the item or enter throw recovery.

### Obstructions

Reuse existing release-clearance behavior, adapted to the boulder's actual bounds and both hands' reach. Small valid corrections must move the visible held item and its hand targets together. If no clear release position exists, cancel the release and retain the item.

Accept the existing limitation that held or charging visuals may clip when a pose cannot be corrected. This feature does not require a new obstruction animation system.

## Throwing and impact rules

### Initial tuning

| Setting | Boulder value |
| --- | --- |
| Minimum throw speed | 2 m/s |
| Maximum throw speed | 5 m/s |
| Full charge duration | 1 second |
| Movement inheritance | 100%, matching small rocks |
| Drop nudge speed | 1.5 m/s |
| Minimum damage and shove speed | 8 m/s |
| Damage per qualifying contact episode | 25 |
| Impulse multiplier | 1× existing speed-based calculation |
| Rigidbody mass | 10 kg |
| Linear damping | 0.05 |
| Angular damping | 0.1 |
| Sleeping | Normal physics sleeping; no small-rock aggressive sleep override |
| Rotation synchronization | Enabled |

Store these values in the existing item settings, extending those settings only where the required per-item behavior is not currently supported. Runtime initialization must apply the boulder definition's physics values rather than unintentionally substituting ordinary item defaults.

### Qualifying impacts

Only loose boulders deal impact damage or apply the special player shove. Held boulders retain disabled physical collisions and cannot become contact weapons when the carrier moves.

Damage and shove share one boulder-specific minimum speed. Preserve direction-sensitive contact behavior: both the boulder's own velocity toward the player's contact surface and its relative closing speed must meet the threshold. Total boulder speed alone is insufficient. Running into a stationary boulder causes neither damage nor the special shove, although ordinary physical contact may push the boulder.

Qualifying impacts deal fixed damage. Faster qualifying impacts produce greater shove through the existing impulse calculation; mass does not introduce an additional multiplier into player shove. Preserve existing limits on combined player impulses.

Use the existing contact-episode behavior: sustained contact does not repeatedly deal damage, and separation permits a subsequent qualifying hit. A boulder does not need to have been thrown to qualify.

Every player is eligible, including teammates and the player who released it. Preserve the normal release collision grace and releaser pickup lockout. After those existing protections expire, the releaser has no special immunity.

Do not enforce a rule that normal throws are harmless. Inherited movement, falls, slopes, carts, or other physical circumstances may make a throw dangerous. No source-of-motion filter or artificial speed cap is required to prevent this.

## Physics and networking

### Sleeping collisions and pushing

Allow loose boulders to retain player and golf-cart collisions while sleeping, so existing physical contact can wake and push them. Make this an item-specific choice with defaults that preserve the sleeping collision exclusions of all existing items.

Reuse the current player collision body's pushing behavior. A walking player may push a heavy boulder readily despite its mass. Do not introduce a separate walking push-strength or resistance model.

Use the selected low damping and normal sleeping behavior to support sustained downhill rolling. Preserve ordinary environment collision behavior and the existing sphere collider used by item/player contact detection.

### Simulation ownership and replication

After a player releases a boulder, use the existing small-rock behavior in which the releasing client continues simulating it. Reuse existing item motion replication, simulation lifecycle, and takeover behavior. Synchronize rotation so rolling is represented on other clients.

Keep player damage and shove detection responsive through the existing victim-client contact reporting and application paths.

Pushing uses physics on the client currently simulating the boulder. Accept that pushes from other clients take effect after their player or cart movement reaches that simulator. Also accept that a cart on another client may briefly collide with a kinematic boulder copy and react as though it were immovable until motion updates arrive.

Do not add a separate push message protocol, local push prediction, or simulator transfer on contact for this feature.

## Inventory, dropping, and other interactions

### Pickup and dropping

Keep normal pickup behavior at any boulder speed. Successful pickup stores the boulder immediately. A qualifying impact that occurs before pickup still applies normally.

Preserve both existing drop entry points: dropping the selected item and dragging an unequipped inventory item out of the inventory panel. Use the current camera-forward placement for both; do not temporarily equip a stored boulder or require a stomach-origin drop.

Use boulder-sized clearance instead of the existing small fixed drop radius. If there is no clear placement, leave the boulder in inventory. A successful drop receives the existing forward nudge and inherited player or cart movement. Cancelling an active charge to drop does not convert the drop into a charged throw.

### Existing control rules

Reuse the existing behavior for slot changes, inventory opening, input suppression, direct use, cauldron actions, downing, death, and other equipment interruptions. Do not add boulder-specific permission or cancellation rules.

- Drivers and carried players do not display equipped items under the existing equipment rules.
- Passengers can equip and use items when the existing seat-transition rules permit it.
- A player carrying another player may continue displaying the equipped boulder but cannot throw it.
- Downing or death retains the existing inventory lifecycle rather than adding an automatic boulder drop.

## Cauldron crafting

Add an Exact recipe consuming three small rocks and producing one boulder. Use the existing three-ingredient capacity, automatic brewing, consumption, and output behavior.

A boulder is a valid cauldron ingredient through the existing held insertion and physical intake paths. Use the ordinary insertion and output presentation at its authored size. No boulder-specific shrinking or cauldron animation is required.

Do not add a recipe that specifically requires boulders. Existing wildcard and override recipes remain applicable, including Basketball plus Boulder producing BouncyPotion. Unmatched mixtures retain existing failed-brew and disposal behavior.

## Asset authoring and integration

Use `Assets/Game/Prefabs/Items/Boulder.prefab` as the item prefab and preserve its model, authored size, rigidbody, and sphere collider.

Required authoring consists of:

- Two oriented child grip transforms for the palms.
- Standard world-item, throwable-use, offline-rigidbody, and baked-pickup integration so the prefab supports crafting and later manual scene placement.
- A Boulder `ItemDefinition` referencing this prefab and registered in `ItemRegistry`.
- An inventory icon using the existing item icon workflow.
- Heavy pose defaults and boulder-specific item settings.
- The three-small-rock cauldron recipe.

Update the designated prefab in place; do not create a duplicate through an item-setup workflow that automatically chooses a new prefab path. Let Unity generate asset metadata.

Crafting is the initial source of boulders. Do not add scene placements; the user will place boulders manually if desired. No avatar or game-scene anchor authoring is required.

Integrate with `PlayerHeldItemPresentation`, `HeldItemPose`, the existing hand target/IK system, `PlayerInventory`, `WorldItem`, item/player contact handling, `WorldItemRegistry`, and the cauldron's existing recipe system. Preserve ordinary Hand items and specialized existing item behavior while extending shared logic where needed. Cache stable references during setup and keep configuration changes event driven where possible.

## User visual acceptance checks

The user should check these behaviors after implementation:

- Both palms contact the authored side grips at stomach height across supported avatars, with suitable first-person framing and unchanged boulder size.
- Looking up and down leaves the hold body-relative; turning follows avatar yaw.
- Quick taps release during the lift, longer charges reach overhead, throw strength increases with charge, and movement contributes to launch velocity.
- Both hands follow through and return together. Switching, cancelling, carrying, and seating retain their existing behavior.
- Walls and low ceilings use existing release correction or refusal behavior. Equipped and stored inventory drops use sufficient clearance and retain the item when blocked.
- Slow or stationary boulders do not damage or apply the special shove. Fast directional impacts apply the configured damage and shove, without repeated damage from continuous contact.
- Boulders roll downhill, wake when pushed by players or carts, and show rotation across clients. Other items retain their existing sleeping collision behavior.
- Remote pushes exhibit the accepted replication delay without introducing a separate push system.
- Three small rocks produce one boulder; insertion, output, pickup, icon presentation, and existing wildcard recipes work with its full-size visuals.
