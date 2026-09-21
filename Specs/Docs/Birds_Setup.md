# Bird authoring

## Session assets

1. In Unity, create **Two Birds → Birds → Settings**, **Catalog**, and species assets. Add `BirdRegistry` to the persistent `SessionRoot` prefab and assign Settings and Catalog. An empty catalog and no spawn zones support an empty bird world.
2. Add the existing Rock item definition to Settings → Rocks. Bird lethality uses Settings → Lethal Speed or the species override; the player shove threshold is independent.
3. Create `BirdBody` and `BirdQuery` layers. Disable every collision pair involving BirdQuery in the matrix. Runtime hit shapes explicitly include ItemWorld and exclude all other layers; only an eligible locally simulated rock is allowed through contact modification. Allow BirdBody to collide only with static scenery layers; disable its pairs with Player, PlayerItemHitbox, GolfCart, CartSeat, ItemWorld, ItemHeld, BirdBody, and BirdQuery.
4. Set Settings → Solid Mask to terrain, trunks, buildings, and other static solid scenery. Set Ground Mask to permitted walkable surfaces. Exclude leaves, water surface renderers, bodies, sensors, players, carts, and items from these masks.

## Presentation prefabs

Create a living prefab with `BirdView` on its root. Assign its model child, Animator, and optional separate `SfxSource` components for calls and wings. Each SfxSource needs an AudioSource. Living prefab colliders and rigidbodies are disabled by the view. The registry creates pooled kinematic sphere hit shapes at runtime from the species radius, independently of visibility and animation. No hit-shape prefab or scene object is required. Rock definitions should use Continuous Dynamic collision detection for fast sphere contacts.

Create a matching whole-body prefab with `BirdBody`, Rigidbody, OfflineRigidbody, a simple body collider, and the same model hierarchy. Assign its model child, feather particle system, and optional hit SfxSource. Use a continuous collision mode for thin scenery. Keep the feather system outside the shrinking model if its visual scale should stay constant. BirdBody disables living animators and copies the available bone rotations at death.

Assign both prefabs to each species. Model Scale and Model Offset apply to the assigned model child; Radius and Landing Offset are world-space gameplay dimensions and need to match that model. A landing offset must leave room for the body radius and approach clearance. Missing presentation prefabs do not prevent shared simulation, but those birds are invisible.

Animation slots contain Animator state names or full paths, not clip names. Supply an Animator controller/override controller containing those states. Empty slots fall back to shared settings, then leave the available animation unchanged. Disable imported root motion. Sound slots fall back from species to shared settings, then silence.

## Representative species

Use **Assets → Create → Two Birds → Birds → Templates** for these representative definitions. The templates assign an unused species ID and matching capabilities. Add the resulting assets to Catalog → Species:

| Example | Behavior | Capabilities | Required destinations |
| --- | --- | --- | --- |
| Small bush bird | Bush | Flight, Tree, Ground | Compatible free perches |
| Large soaring bird | Soaring | Flight, Tree and/or Ground | Air volume and suitable landing perches |
| Water bird | Water | Flight, Swim | Water volume |
| Flightless ground bird | Ground | Ground | Ground volume |
| Short-flight ground variant | Ground | Ground, ShortFlight | Ground volume |

Start with the default speeds, delays, rewards, and lifetimes. Size the large species' radius and perch clearance to its model. Keep ground spaces open; the bounded planner does not navigate mazes or step across unsupported gaps.

## Scene authoring

Place `BirdSpawnZone` components in the game scene, set unique nonzero zone IDs, positive biome IDs, bounds, weighted species, population range, and replacement timing. Species are selected once per vacancy; invalid placements retain that species and retry.

Place `BirdHabitatVolume` components for air, ground, and water. Give habitats unique nonzero IDs and matching biomes. Water volumes must be upright; Water Height is the world-space swimming plane and must lie inside the volume. Give the volume enough depth for falling bodies to enter it. Ground volume bounds must include bird center heights, not just the terrain surface.

Place `BirdPerchVolume` components and use **Generate perches** in the Inspector. Ground generation rejects failed raycasts and steep surfaces. Generated perches remain editable and regeneration preserves available IDs. **Clear generated perches** only removes that volume's generated children. Hand-placed `BirdPerch` objects remain untouched. After duplicating any authored component, assign fresh IDs; regeneration assigns fresh IDs to duplicated generated perches. Zero and duplicate runtime IDs prevent session startup.

Provide enough clearance and landing capacity for every species. Perch IDs are unique across the scene, independently of zone and habitat IDs. Matching biomes allow destinations outside the original spawn zone. Moving perches are unsupported.

Increment Catalog → Content Version when changing static scenery or model hit geometry. Catalog IDs, numeric species tuning, habitat geometry, and perch layouts are fingerprinted automatically. Participants must use matching content and protocol versions.

## Visual acceptance

Use a host and remote client side by side. Observe all four behaviors, landing capacity, water-only swimming, delayed scares, line-of-sight changes without leaving range, and horns through obstacles. Compare small and large birds during flight and landing.

Throw and drop rocks into birds from below, the side, and above. Compare rebound with lift set to zero and with the default lift. Test ricochets between birds and scenery, weak contacts followed by separated lethal contacts, and maximum-speed throws. A bird on the original trajectory beyond a bounce must survive unless the new trajectory actually reaches it. Check one reward per life, the flat bonus on each later kill of the same rock release, and fresh counts after pickup/rethrow. While a rock is airborne, disconnect its releaser and check that host takeover preserves the trajectory and release count. Join while rocks are airborne or sleeping. Under latency, confirm old snapshots cannot reverse a bounce; reliable bounce updates snap observers to the new path and may produce a visible correction. Change cart drivers around an impact and verify attribution; parked overlaps must not kill.

Watch bodies fall mostly downward, settle on scenery, sink through water, shrink, and reuse their normal scale. Confirm they never obstruct interaction, seat exits, cart support, or rocks. Join during flight, scares, deaths, and replacement; check for no revived birds or replayed rewards. Leave/rejoin and confirm currency resets. Reward notifications must leave aiming and input unchanged.

Use Birds_Plan.md for the separately requested eight-player performance and network-envelope acceptance scenarios.
