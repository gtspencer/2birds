# Splat_Brainstorm — Specification

## Purpose

An item can produce a cosmetic splat when it hits a player, the ground, the environment, a golf cart, or another physical target after being deliberately thrown. The splat uses Unity's URP decal system and an authored decal material. Each deliberate throw can produce one splat. Item settings determine whether that impact also destroys the thrown item.

Splats add no damage, slipping, view obstruction, or other gameplay effect. Existing item impact behavior continues to apply.

## Authoring and configuration

### ItemDefinition

Extend the existing `ItemDefinition` and its inspector with these settings:

| Setting | Behavior |
| --- | --- |
| **Spawn Splat** | Enables splatting for this item type. Existing items remain opted out until configured. |
| **Destroy on Splat** | Adds destruction of the thrown item when its splat is consumed. Visible only when **Spawn Splat** is enabled. |
| **Splat Definition** | Reference to a reusable `SplatDefinition` ScriptableObject. Visible only when **Spawn Splat** is enabled. |

The author drags a `SplatDefinition` asset into the reference field on the **ItemDefinition asset**. Every spawned instance of that item uses the definition's settings. This assignment belongs to the item definition, rather than individual item prefab or scene-object configuration.

### SplatDefinition

Each `SplatDefinition` contains all visual settings for a splat style. Multiple item definitions can reference the same asset.

| Setting | Meaning | Default |
| --- | --- | --- |
| **Splat Material** | Authored URP decal material controlling artwork, color, and transparency. | Author assigned |
| **Splat Size** | Final splat width in world meters, independent of the thrown item's physical size. | 0.5 m |
| **Pop Duration** | Time to scale from the initial collapsed size to the configured size. | 0.35 s |
| **Lifetime Before Shrinking** | Time from splat creation until shrinking begins. Includes the pop animation. | 30 s |
| **Shrink Duration** | Time to shrink to zero before removal. | 3 s |

Spawning always uses LeanTween with `easeOutElastic`. The easing is fixed behavior; durations are editable per splat asset. The material is assigned directly, with its texture and color authored in Unity.

### Incomplete configuration

When **Spawn Splat** is enabled but either the definition or its material is missing, show an inspector warning. Treat splatting as disabled at runtime, including **Destroy on Splat**. Existing item behavior remains active.

## Throw and impact lifecycle

Use the existing explicit `ItemReleaseIntent.Throw` and `ItemReleaseIntent.Drop` distinction.

1. A deliberately thrown, correctly configured item becomes eligible to splat.
2. Its first eligible impact consumes that throw's splat and creates the decal event.
3. If **Destroy on Splat** is enabled, remove the thrown item through its normal multiplayer item lifecycle.
4. Otherwise, the item continues under its existing physics and gameplay behavior. Later bounces and contacts from that throw produce no further splats.
5. Picking up the item cancels any pending splat eligibility. A subsequent deliberate throw enables one new splat.

Dropping an item, holding it against something, or leaving it resting where it spawned never arms splatting. Splat eligibility belongs to each throw, not the item's entire lifetime.

### Eligible impacts

The first eligible physical impact counts regardless of speed. Existing damage and shove thresholds remain independent of splatting. Support both the existing player-contact path and world collision path, including ground, environment, and golf carts.

Preserve the existing launch collision immunity for the thrower. Once that immunity ends, hitting the thrower can produce a splat like hitting another player.

Cauldron intake retains precedence: if a cauldron accepts the thrown item as an ingredient, intake occurs and no splat is created. Other impacts follow the normal splat rules.

The first eligible collision still consumes the splat when the receiving surface cannot display a decal, including transparent surfaces. **Destroy on Splat** still applies to that collision.

### Interaction with existing item behavior

Splatting must preserve existing collision damage, shove, potion activation, and other item effects. Disabling **Destroy on Splat** only disables the additional destruction introduced by splatting; existing item behavior may still remove an item.

Consume a throw's splat only once, including when multiple contact paths or multiplayer confirmation report the same impact. Preserve impact information needed for existing behavior and decal creation before removing the thrown item.

## Decal placement and attachment

Use Unity URP `DecalProjector` rendering through the existing active decal renderer feature. Place the splat at the reported contact point and orient its projection to the struck surface.

- Static surfaces retain the splat at the impact location.
- Moving objects and golf carts carry their splats with them.
- Player splats attach to a suitable nearby avatar bone, following the existing avatar tattoo approach.

Use shallow projection volumes to reduce projection onto surrounding geometry. Native decal limitations are accepted: projectors may affect adjacent receiving surfaces inside their volume, transparent surfaces do not display the decal, and a bone-attached player splat does not deform perfectly with skin.

The splat's lifetime is independent of the thrown item's lifetime. Destroying the thrown item leaves its splat alive on the receiving target. If the receiving target disappears, despawns, or is returned for reuse, remove its attached splats rather than leaving them floating or carrying them into another target instance.

## Animation and cleanup

At creation, scale the splat into view with LeanTween `easeOutElastic` over **Pop Duration**. Its settled size is **Splat Size**.

Begin shrinking when **Lifetime Before Shrinking** has elapsed since creation. Shrink smoothly to zero over **Shrink Duration**, then remove the decal and release its presentation resources. With defaults, the pop lasts 0.35 seconds, shrinking starts at 30 seconds, and removal occurs at 33 seconds.

Early target removal also cleans up the decal and any active tweens. The thrown item's removal must not interrupt a splat attached to another target.

## Multiplayer behavior

All clients present for a splat event receive the same impact outcome: the splat style, receiving target, placement, and optional removal of the thrown item. Each client presents one decal for that event and runs the definition's animations locally.

Follow the project's existing client simulation and prediction model for responsive impact presentation. Reuse the existing registry event and item-removal patterns. Prediction, accepted events, and repeated collision notifications must not create duplicate splats. Keep network messages compact; continuous decal animation updates are unnecessary.

Splats follow the existing one-shot cosmetic event model. Players joining later receive future splat events; still-visible splats from before they joined are not reconstructed.

## Integration boundaries

The existing systems provide the following integration points:

| System | Responsibility |
| --- | --- |
| `ItemDefinition` and `ItemDefinitionEditor` | Item opt-in, destruction setting, conditional fields, asset reference, and configuration warning. |
| `PlayerInventory` release intent | Distinguishing deliberate throws from drops. |
| `WorldItem` collision handling and `ItemPlayerContact` | Detecting eligible world and player impacts. |
| `WorldItemRegistry` | Per-throw lifecycle integration, replicated impact outcomes, cauldron precedence, and item removal. |
| Existing potion impact flow | Reference pattern for responsive effects and preserving impact processing through item removal. |
| `AvatarCosmeticPresentation` | Reference pattern for runtime decal projectors attached to avatar bones. |
| Existing URP decal renderer and LeanTween | Decal rendering and scale animation. |

Splat consumption state must remain independent of potion arming and must not disable potion sensors or activation. Shared splat presentation logic should handle decal creation, attachment, animation, and cleanup without making avatar cosmetics or potion gameplay responsible for splats.

Reuse the active renderer setup and create projectors at runtime. The feature requires no manual addition of splat components to individual item GameObjects or edits to the game scene.

## Content ownership

Implementation provides the runtime behavior, `SplatDefinition` asset type, and inspector support. Content authors create splat definition assets, author and assign compatible decal materials, and opt selected item definitions into splatting. Initial sample assets and changes enabling splats on existing items are outside this implementation scope.

## User visual acceptance checks

After implementation, the user checks the following in the game:

1. Enable **Spawn Splat** on an item definition and assign a splat asset by dragging it into the reference field. Confirm conditional fields and missing-configuration warnings.
2. Throw a configured item at ground, environment, another player, and a golf cart. Confirm one splat at the first eligible impact, including gentle contact after launch immunity.
3. Drop, hold, and leave spawned items resting. Confirm these actions produce no splats.
4. With **Destroy on Splat** disabled, let the item bounce repeatedly, then pick it up and throw it again. Confirm one splat for each deliberate throw and none from later bounces.
5. Enable **Destroy on Splat**. Confirm item removal leaves the splat visible and existing impact behavior still occurs.
6. Observe the elastic pop, configured size, delayed shrink, and removal. Confirm default shrinking starts 30 seconds after creation and completes 3 seconds later.
7. Move the struck player or golf cart. Confirm the splat follows the target within the accepted projection limitations. Remove the receiving target and confirm its splat disappears.
8. Observe the same throws on multiple clients. Confirm consistent impact outcomes, target attachment, item removal, and no duplicate splats. A later joiner sees only subsequent splat events.
9. Throw an accepted ingredient into a cauldron. Confirm normal intake occurs without a splat.
10. Hit an unsupported decal surface and confirm the throw's splat is consumed and optional destruction still occurs. With a missing splat definition or material, confirm splat-driven destruction is disabled.
