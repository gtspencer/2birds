# Crafting and Potions Specification

## Purpose

Let players combine inventory items in a shared cauldron to create consumable potions. Players can throw potions to activate an area effect or use them directly at their own location. Cauldron contents, crafted outputs, potion activations, and temporary abilities must remain consistent across clients while preserving responsive local play.

The initial content is three health-potion tiers and one bouncy potion. Recipes produce an `ItemDefinition`, allowing future outputs to be ordinary items rather than requiring every result to be a potion.

## Scope

- A networked cauldron with three ingredient slots, insertion animation, brewing, disposal, failure effects, and a hovering output.
- Exact recipes and ordered override recipes.
- Potion definitions derived from the existing item definition, sharing the existing inventory, holding, throwing, and catching behavior.
- Instantaneous area application and lingering spherical zones.
- A temporary jump modifier with exponentially decaying automatic rebounds.
- Dedicated player effect receivers, including seated and carried players.
- Fixed world-space ingredient panels, interaction prompts, and a local active-buff indicator.
- A Potion Setup entry that reuses the existing Item Setup workflow.

Actual healing, damage, health limits, killing, death, and respawning are outside this feature. Provide concise TODOs in the methods that will add or subtract player health. Do not change the player's health value or implement a substitute health system. Failed-brew knockback and the bouncy ability are functional gameplay behaviors in this feature.

The user supplies VFX and particles. Use the existing cauldron and potion models. No new migration tool, general-purpose ability editor, recipe-discovery UI, or speed-boost potion is required.

## Domain and integration boundaries

| Term | Meaning |
| --- | --- |
| Item definition | The existing `ItemDefinition` ScriptableObject: item identity, icon, prefab, stacking, held presentation, throw settings, and physics settings. |
| World item | An individual item with a world-item ID, managed by `WorldItemRegistry` through world, held, and removed lifetimes. |
| Potion definition | An `ItemDefinition` subclass containing effect, area, lifetime, strength, and presentation settings. |
| Ingredient | One accepted inventory/world item occupying one cauldron slot. A stack contributes individual items. |
| Exact recipe | A recipe matching the complete unordered ingredient multiset, including quantities. |
| Override recipe | A recipe whose requirements can match despite additional ingredients. Overrides are evaluated in explicit order before exact recipes. |
| Pulse | One application to eligible players inside a sphere at activation time. Its fading visual does not apply effects to later entrants. |
| Zone | A sphere with a lifetime that applies its effect while eligible players occupy it. |
| Buff | A timed effect on a player that can continue after the activation cloud disappears. |
| Effect receiver | A dedicated player trigger used to receive potion effects and determine failed-brew eligibility. |

Reuse these existing integration points:

| Source or asset | Required integration |
| --- | --- |
| `Assets/Game/Runtime/Items/ItemDefinition.cs` | Allow inheritance; retain existing item settings and identity. |
| `Assets/Game/Runtime/Items/ItemRegistry.cs` | Register potion definitions alongside ordinary item definitions. |
| `Assets/Game/Runtime/Items/WorldItem.cs` | Preserve pickup/holding/physics behavior; integrate armed potion contact and definition-driven appearance. |
| `Assets/Game/Runtime/Items/WorldItemRegistry.cs` and `WorldItemRegistry.Motion.cs` | Add crafted-item creation and coordinate consumption, activation, and current-state replication with existing item lifetimes. |
| `Assets/Game/Runtime/Items/WorldItemMessages.cs` | Represent the necessary potion release/activation state within the existing identity and ordering model. |
| `Assets/Game/Runtime/Inventory/PlayerInventory.cs` | Consume a single selected item for insertion or direct use without leaving its ID in a stack. |
| `Assets/Game/Runtime/Player/PlayerEquipment.cs` and `Assets/Game/Runtime/Items/ThrowableItemUse.cs` | Retain charged throwing; route direct consumable use through the equipped item. |
| `Assets/Game/Runtime/Player/PlayerInteraction.cs`, `IInteractable.cs`, and `Assets/Game/Runtime/UI/InteractionTooltip.cs` | Support the cauldron's primary and secondary actions and their bindings. |
| `Assets/Game/Runtime/Player/PlayerMotor.cs` | Apply bounce behavior inside predicted movement and reuse the existing world-impact path for failed-brew knockback. |
| `Assets/Game/Runtime/Player/PlayerSeating.cs` | Keep effect eligibility during seating/carrying and apply the agreed movement interruptions. |
| `Assets/Game/Runtime/Vehicles/GolfCartNetwork.cs` | Identify carts and attach zones using the cart's existing physical and displayed poses. |
| `Assets/Game/Editor/ItemSetup.cs`, `ItemDefinitionEditor.cs`, and `IconCaptureWindow.cs` | Reuse setup, derived-definition editing, registration, and icon capture. |
| `Assets/InputSystem_Actions.inputactions` | Add direct potion use and the secondary cauldron interaction. |
| `Assets/Game/Prefabs/Cauldron.prefab` | Author the cauldron behavior, colliders, animation anchors, VFX references, and fixed display. |
| `Assets/Game/Prefabs/Items/Potion.prefab` | Prepare one shared gameplay prefab used by every potion definition. |
| `Assets/Game/Prefabs/Player.prefab` | Add the dedicated effect receiver. |

World items use the registry's compact item identities rather than one FishNet `NetworkObject` per item. Preserve that model. Cauldrons need a shared network identity and current state; avoid introducing a parallel inventory or item registry.

## Cauldron interaction and lifetime

### Capacity and admission

A cauldron accepts at most three ingredients. Any ordinary inventory item, including a potion, can be an ingredient. Carried avatars are excluded.

Players insert ingredients by either releasing an item into the intake trigger or interacting while holding an inventory item. An interaction inserts exactly one item from the selected stack. One accepted world item occupies one slot.

Reserve the slot immediately when insertion is accepted. At that point the ingredient is permanently consumed and unavailable for pickup or other inventory actions. Retain whatever local presentation is needed to animate its final movement into the cauldron. Rejected items are not consumed.

Animate each accepted ingredient from its current presentation through a configurable curved path above the cauldron and then down into it. Play the insertion VFX on arrival. Held insertion uses the same arrival presentation. Other ingredients may enter remaining slots while an insertion is in progress.

Brewing and disposal wait until every accepted ingredient has finished entering. An empty cauldron cannot brew or produce a failed-brew explosion.

### Actions and controls

Use the existing interaction range, targeting conventions, and input-context gating.

| Context | Action | Default binding |
| --- | --- | --- |
| Holding an inventory item; cauldron accepts ingredients | Insert item | E / gamepad north |
| Empty hands; ingredients present and settled | Brew | E / gamepad north |
| Empty hands; ingredients present and settled | Dispose | R / D-pad right |
| Holding a potion | Direct use | Right mouse / left trigger |
| Holding a throwable | Existing charged throw | Left mouse / right trigger |

Show both Brew and Dispose prompts when those actions are available. Show the actual bindings through the existing prompt system. Carrying a player does not turn that player into an insertable item or count as empty hands for cauldron actions.

### Brewing

Brewing consumes the complete accepted mixture and resolves one recipe result. Clear the ingredient icons when brewing starts. Lock insertion, further brewing, and disposal for the brew/result sequence.

On success, play the success presentation and raise exactly one newly created world item from the cauldron into a hovering output position. The result remains there until a player successfully picks it up. Any player may collect it through the ordinary inventory interaction. A failed pickup, including lack of inventory space, does not unlock the cauldron or discard the output.

Do not permit the hovering output to be consumed as another ingredient, fall away, or become an independently duplicated item. After successful collection, the cauldron returns to its empty state.

On failure, play the poof/smoke effect and apply one failed-brew blast. Return the cauldron to its empty, available state after the result sequence. The spent ingredients are not refunded.

### Disposal

Disposal clears every settled ingredient and plays a brief effect. It causes no damage, knockback, or additional gameplay penalty. Ingredients remain permanently lost.

### Presentation states and defaults

Keep the requested empty/inactive and occupied/active contents presentation. Internally, distinguish insertion, brewing, and waiting for output as needed to enforce availability; these are functional phases, not additional crafting systems.

The contents effect is active while ingredients are present or brewing. The hovering output has a separate ready presentation.

| Duration | Initial value |
| --- | --- |
| Ingredient insertion | 0.6 seconds |
| Brewing | 1.5 seconds |
| Output rise | 0.5 seconds |

Expose these timings on the cauldron configuration so they can match the supplied VFX.

## Recipes

Ingredient order does not affect matching. Ingredient identity and quantity do.

First evaluate override recipes in their explicitly authored order. The first eligible override wins. If no override matches, evaluate exact recipes against the entire ingredient multiset. An unmatched mixture fails.

| Ingredients | Output |
| --- | --- |
| Exactly one mushroom | Small health potion |
| Exactly two mushrooms | Medium health potion |
| Exactly three mushrooms | Large health potion |
| At least one basketball and at least two total ingredients | Bouncy potion, regardless of the other ingredients |

The basketball rule is an override. Two basketballs qualify. Basketball plus mushroom plus an unrelated item also qualifies. One basketball alone does not qualify. Mushroom plus an unrelated item, or two mushrooms plus an unrelated item, fails unless another authored recipe matches.

Author recipe data separately from potion behavior. A recipe identifies its matching requirements and an output `ItemDefinition`; the cauldron must not be restricted to producing `PotionDefinition` outputs. Do not hardcode the initial item IDs into the matching logic.

## Potion definitions and shared presentation

`PotionDefinition` inherits from `ItemDefinition`. Retain the existing item ID, inventory icon, stack behavior, throw tuning, held poses, physical settings, and `DontPushPlayer` setting.

Initial potion definitions are distinct item types. Stack at most five items of the same type/tier; do not merge different health tiers. Throwing, direct use, and cauldron insertion each consume or release one individual item.

All potion definitions reuse the prepared `Assets/Game/Prefabs/Items/Potion.prefab`. Runtime presentation and behavior come from the individual world item's definition, not a fixed default definition on that shared prefab. Reinitializing a pooled item must also update its potion appearance and effect settings.

Each definition independently configures the applicable fields:

- Effect type and application mode: pulse or lingering zone.
- Area radius.
- Zone lifetime or applied buff duration, as appropriate.
- Effect strength, including planned healing rate or bounce settings.
- Color/presentation and supplied impact/cloud VFX references.
- The inherited item icon, stacking, throwing, and player-shove settings.

A large health potion may have a different radius or lifetime from a small potion even though their initial defaults match. Do not put these values in one shared health-potion constant or global area setting.

Apply appearance per potion instance and per icon preview without modifying the shared model/material assets for every other potion. The configured visual targets must support the user's supplied generic bottle model.

## Potion activation, throwing, and catching

Ordinary dropping leaves a potion intact and unarmed. Throwing arms it. An armed potion activates once on its first relevant world or player impact and consumes its world item. Environment and ground impacts must work in addition to cart and player hits.

A cauldron with available capacity captures an entering potion as an ingredient before it activates. A full or busy cauldron does not capture it; the potion remains armed and can activate on a subsequent solid contact.

Direct use consumes one held potion and activates its effect immediately at the user's feet. It does not animate a throw. It uses the same area/application behavior as a thrown potion, so nearby eligible players can also receive the effect. Direct use creates a stationary zone even when the user is seated in a cart.

Retain ordinary airborne pickup as catching. A successful catch disarms the potion; rethrowing arms it again. Coordinate pickup, insertion, and activation so only one valid transition wins for the same item release. If catch and activation requests race, the first valid transition accepted by the host wins. A delayed contact from an earlier release cannot activate a potion that has since been caught or rethrown.

New potion definitions default `DontPushPlayer` to enabled. This setting controls the existing thrown-item shove, not activation. Disabling it allows the normal configured shove in addition to the potion effect. Activation must still work when shove is disabled, impulse strength is zero, or impact speed is below the shove threshold.

Preserve the existing protection against immediately hitting the thrower on release. Held items must not activate from their holding presentation. Scope potion collision changes so ordinary item physics remain intact.

## Effect receivers and area behavior

### Isolated trigger layers

Add a dedicated player effect receiver trigger on a new layer. Add a potion-effect layer for area volumes and potion contact sensors. Restrict the physics collision matrix so these two layers interact only with each other.

Physical potion bottles retain ordinary world collisions separately from these effect triggers. Include environment contacts for potions without globally changing every ordinary item's collision behavior. Item held/world layer updates must preserve the dedicated layer of a potion's contact sensor.

The receiver identifies its player and remains available when ordinary movement/item-hitbox colliders are disabled for seating or carrying. A potion can detect those receivers directly even when the player's usual physical hitbox is suspended. Reuse the player's Rigidbody rather than creating another networked physics object for the receiver.

Use receiver overlaps, not a recurring scan of all player positions. Lingering zones maintain membership from trigger entry and exit. Seed initial overlaps so activation includes players already inside a newly created sphere. Pulses and failed-brew blasts can query the receiver layer once and deduplicate by player identity.

### Eligibility and disabling

Disabling the receiver blocks new applications, including subsequent healing applications from an overlapping zone. It does not explicitly clear an already-applied timed buff; that buff expires normally.

Ensure disabled/despawned receivers cannot remain eligible through stale zone membership. Collider disabling does not guarantee an exit callback, so application eligibility and cleanup must account for it. Keep this lifecycle handling small; do not build a separate immunity system.

Seated and carried players can receive healing applications and timed buffs. Effects do not eject them from seats or a carrier. Movement effects act only when the player's movement context permits them.

### Pulse and zone semantics

Potion areas are ordinary spheres and do not test line of sight. They can affect receivers through walls. Particle effects do not require special wall-occlusion logic.

A pulse applies once to the eligible receivers inside its sphere at activation. Entering its fading visual afterward does nothing.

A lingering zone remains active for its configured lifetime. It applies only while the recipient is eligible and inside; leaving stops further zone applications. Use scheduled effect work for occupants and event-driven membership changes rather than no-op per-frame application calls.

Separate common application/lifetime logic from the direct-use, thrown-impact, and cauldron call sites. Share behavior across these boundaries without building unused effect types or speculative frameworks.

## Initial health potions

All three health potions initially create lingering zones with these independently editable defaults:

| Definition | Radius | Zone lifetime | Planned healing rate |
| --- | --- | --- | --- |
| Small health potion | 3 m | 8 seconds | 5 health per second |
| Medium health potion | 3 m | 8 seconds | 10 health per second |
| Large health potion | 3 m | 8 seconds | 15 health per second |

Every eligible occupant receives its own application; the zone is not a shared pool of health. Leaving stops applications immediately. Among overlapping healing zones, use only the strongest current rate for each recipient, and switch to the remaining strongest rate when membership changes.

Route the calculated healing application to a method containing a short TODO to add player health. Do not mutate health, clamp it, add health networking, or simulate a visible increase. Zone timing, membership, presentation, and strongest-rate selection still belong to this feature.

Healing and a bouncy buff can coexist. Healing-zone membership does not create a persistent HUD buff timer.

## Bouncy potion and movement

The bouncy potion produces a pulse with a default radius of 3 m. Eligible players receive a 10-second buff. Both values are configurable per definition.

Receiving the buff does not launch a grounded player. Receiving it while airborne does not start a rebound sequence for that existing flight. A deliberate jump while the buff is active starts the sequence.

| Bounce setting | Initial value |
| --- | --- |
| Initial launch-velocity multiplier | 2.0 times normal jump velocity |
| Velocity retained at each rebound | 0.5 times the previous launch velocity |
| Minimum rebound threshold | 0.5 times normal jump velocity |

All three settings are configurable on the potion definition. The multiplier changes launch velocity, not apex height; at equal gravity, doubling launch velocity produces approximately four times the normal jump height.

On each subsequent landing, calculate the next launch velocity using the configured decay factor. Automatically rebound if it meets the threshold; stop the sequence if the next velocity would be below the threshold. Rebounds use the decayed launch target rather than accumulating falling speed. Retain directional air control.

With a normal jump velocity of 4 m/s and the default settings, the deliberate launch is 8 m/s, followed by automatic rebounds at 4 m/s and 2 m/s. The next candidate is 1 m/s, so the sequence stops. After settling, another fresh jump press starts a new sequence if the buff is still active. Pressing or holding jump during a sequence cannot reset its strength or add an extra impulse.

Another dose refreshes the remaining buff duration to the configured duration without stacking strength or restarting the current sequence.

### Interruptions

- Expiry stops future rebounds without changing current airborne velocity.
- Seating or being carried cancels the active sequence while the buff timer continues. A fresh jump after becoming free is required to start another sequence.
- The existing out-of-world reset clears the buff and sequence.
- Disabling the effect receiver prevents new applications but does not cancel an existing buff.

Use the movement motor's existing Ground and Environment grounding rules. Do not add GolfCart grounding or change ordinary cart-roof jumping as part of this feature.

Implement bouncing in the existing predicted motor rather than through a bouncy physics material or a separate world-impact RPC for each landing. Preserve the normal jump settings as the base values. Temporary modifiers and sequence state must participate in prediction/reconciliation so local control stays responsive and remote movement agrees. Detect actual takeoff/landing transitions so the ground probe cannot repeatedly trigger tiny rebounds.

## Failed-brew blast

A failed brew creates one spherical blast with these configurable cauldron settings:

| Setting | Initial value |
| --- | --- |
| Radius | 3 m |
| Planned damage | 25 |
| Outward knockback velocity at the center | 6 m/s |
| Upward knockback velocity at the center | 3 m/s |

Find eligible players through their enabled effect receivers. Check one unobstructed ray from an explosion anchor above the cauldron rim to each receiver's center. Solid environment geometry and carts block the blast. Ignore players, loose items, triggers, and the exploding cauldron itself.

The same eligibility and line-of-sight result gates knockback and the future damage call. Route damage to a method containing a short TODO to decrement player health. Do not change health or implement a lethal/nonlethal rule.

Apply knockback to players who are neither seated nor carried. Combine the outward and upward components, reducing their strength linearly with distance to zero at the radius. Reuse the existing player world-impact/knockback path and its local response as much as possible. A knocked-back player carrying someone drops them according to that existing behavior. Seated and carried recipients remain attached; they can still reach the future damage hook if otherwise eligible.

Disposal never invokes this blast.

## Cart-mounted zones

A lingering zone from a thrown potion that directly hits a solid part of a golf cart attaches at the impact point relative to that cart. Hitting a passenger is a player impact; merely overlapping a cart does not attach a zone.

Store the cart identity and local impact offset. Follow the cart's translation and rotation using its existing replicated motion. Use the physical cart pose for gameplay overlap and the displayed pose for visual presentation. Do not stream a separate zone transform.

The zone retains its original expiry time through driver changes, flips, and recovery teleports. It follows a recovery teleport with the cart. Remove it if the cart despawns. A bouncy pulse remains a one-time application even if its brief presentation occurs on a cart.

Direct use always places the zone at the user's activation location; sitting in a cart does not make direct use attach it.

## UI and presentation

### Cauldron contents display

Use native world-space UI Toolkit with three square ingredient panels arranged in a row. They have a fixed orientation and do not face or rotate toward the camera. Keep them visible in world space rather than limiting them to hover.

Place the display on an independently positionable child of `Cauldron.prefab`. Initially it floats beside the cauldron; the user can later align it with a sign. Use separate world-space panel settings rather than changing the screen-space HUD panel.

Show an ingredient's existing item icon immediately when insertion is accepted. Clear the icons when brewing or disposal starts. Use the accepted insertion order for the three display slots even though recipe matching is unordered.

### Buff indicator

Show the local player's active timed potion buff icon and remaining seconds on the HUD. Initially this represents the bouncy buff. Refresh the timer display when another dose refreshes the effect and remove it on expiry or the agreed reset. Do not display healing-zone membership as a persistent timed buff.

### VFX hooks

Provide configurable references/anchors for occupied contents, ingredient arrival, brewing, success, failure smoke, disposal, hovering output, potion impact, and cloud presentation as applicable. The user supplies the particles and VFX. Drive them from accepted gameplay transitions and local effect lifetimes.

## Networking and consistency

Prioritize responsive simulation and consistent visualization. Trust client reports rather than adding anti-cheat validation or requiring every presentation response to wait for a server round trip.

The host serializes irreversible shared item/cauldron transitions so concurrent inserts, pickups, disposal, and brewing cannot consume the same item twice or exceed capacity. Preserve responsive local throwing, interaction prediction, movement, and the existing world-impact behavior.

Use compact identity-based events and current-state snapshots:

| Transition/state | Information needed |
| --- | --- |
| Ingredient admission | Cauldron identity, individual item identity, accepted slot/transition ordering, and timing needed for local insertion presentation. |
| Brew or dispose | Cauldron identity and transition ordering; accepted state supplies contents and the resulting phase/output. |
| Successful output | The crafted world's item identity and definition, associated with its cauldron until pickup. |
| Potion activation | Item/release identity, activation position and time, definition reference, and optional cart identity/local offset. |
| Active zone or buff | Effect identity, recipient or placement, and remaining lifetime/start/expiry data sufficient for current-state reconstruction. |

Derive radius, effect behavior, colors, and other authored settings from definition IDs. Do not send full definitions, spline points, particle state, per-frame hovering transforms, or a network message for every rebound. Coordinate item removal, inventory consumption, and feature events through shared transitions rather than adding redundant parallel messages for the same operation.

Replicate enough state for joining clients to see the current cauldron contents/phase, an existing hovering output, live zones including cart attachments, and active buffs with the correct remaining time. Do not replay completed pulses or failed-brew applications as new effects for a joining client.

Handle host echo, duplicate contact reports, and stale releases as ordinary lifecycle coordination. Only one accepted activation consumes a potion and creates its area effect. An already consumed item must not remain in a held stack or reappear through a stale world-item update.

FishNet physics replay must not create fresh potion activations, duplicate healing hooks, repeated blasts, or repeated buff applications. Temporary physics suspension during reconciliation is not gameplay immunity. Ensure receiver/zone poses are current when seeding overlaps, including seated/carried players and moving carts.

## Potion Setup and asset authoring

Add a top-bar `Two Birds -> Potion Setup` entry that opens the existing setup implementation with Potion selected. Keep `Two Birds -> Item Setup` available. Do not build a separate duplicated setup pipeline or reorganize the Project-window creation menus.

Potion mode accepts the item name, effect type, and presentation color. It creates a `PotionDefinition`, assigns an available item ID, registers it in the existing item registry, and associates it with the shared prepared potion prefab. Reuse held-offset preparation and icon capture. Detailed effect tuning remains in the definition Inspector after creation.

Prepare and reuse `Assets/Game/Prefabs/Items/Potion.prefab` instead of creating one model/gameplay prefab per potion definition. Configure its physical collider and potion contact detection to work with the existing item contact/shove path as well as effect receivers. Ensure the derived definition's Inspector preserves the base item editing behavior and exposes potion fields.

Icon capture must apply the selected definition's appearance to the preview before rendering, so a shared prefab can produce different potion icons. Reuse the existing capture tool and its options. Avoid changing the shared source model/materials when rendering a preview.

Author the three health definitions, the bouncy definition, and their recipe data with the specified defaults. Cauldron recipe results remain ordinary `ItemDefinition` references.

## Prefab and project setup

Required authoring includes:

- Cauldron behavior, interaction/body collider, intake trigger, curved insertion anchors, output anchor, blast anchor, VFX references, and fixed world-space UI.
- Shared potion gameplay components, physical/contact colliders, and configurable presentation targets.
- Player effect receiver component/collider and the isolated effect collision layers.
- Potion definitions, recipe data, icons, UI layout, and world-space panel settings.

Apply cauldron setup to `Assets/Game/Prefabs/Cauldron.prefab` so its existing scene instance inherits the setup. Keep scene editing to a minimum. The user positions the floating display for the future sign and supplies/assigns the visual effects.

Use the Unity CLI when possible and Unity MCP as the fallback for Unity authoring. Let Unity generate `.meta` files. Do not create a one-time migration editor tool; the reusable Potion Setup extension is part of the requested feature.

Cache references at initialization or binding changes, use Unity-object lifetime checks, and prefer event-driven transitions. Extract shared logic where inventory, item activation, cauldron behavior, and movement cross boundaries. Keep the change focused on these requirements rather than refactoring unrelated systems.

## Visual validation by the user

Perform visual validation after implementation; do not substitute automated or agent-run validation unless explicitly requested.

1. With two clients, insert individual held items and thrown items, including stacked mushrooms and simultaneous attempts at the third slot. Confirm matching icons, one-time consumption, the curved arrival path, and rejection of excess items.
2. Exercise every initial recipe, two basketballs, basketball with unrelated extras, and invalid mushroom mixtures. Confirm override precedence, safe disposal, and a single shared output that keeps the cauldron locked until successful pickup.
3. Check the fixed three-panel display from both clients and position it for the sign. Confirm the insert, brew, rise, ready, and failure presentations and the two empty-hand action prompts.
4. Compare ordinary drops, throws, catches, direct use, and potion insertion into available/full cauldrons. Confirm no duplicate activation, correct stack consumption, and `DontPushPlayer` behavior independent of potion activation.
5. Observe cloud sizes/lifetimes on both clients, enter and leave overlapping health zones, and include seated/carried players. Health values should remain unchanged because health application is a TODO.
6. Apply bouncy while grounded and airborne. Confirm no automatic initial launch, a deliberate boosted jump, progressively smaller rebounds, recovery to a fresh jump, duration refresh, expiry, seating/carrying interruption, and out-of-world reset. Compare local and remote movement and the HUD timer.
7. Throw a lingering potion at a cart and drive, turn, flip/recover, and change drivers. Confirm the zone follows its impact offset and original lifetime. Compare direct use while seated, which leaves a stationary zone.
8. Compare failed-brew knockback in the open and behind environment/cart cover, near and far from the cauldron, while carrying someone, and while seated/carried. Health must remain unchanged.
9. Disable a receiver and confirm that new applications stop without adding a special buff-clearing behavior. Re-enable it and confirm ordinary eligibility resumes.
10. Join an active session containing ingredients, a hovering output, a live cart-mounted zone, and an active buff. Confirm current state and remaining time without replaying old blasts or pulses.
11. Use Potion Setup to create/register a potion and capture its icon. Compare the icon's configured appearance with the held and world potion, and ensure other definitions sharing the prefab retain their own appearance.
