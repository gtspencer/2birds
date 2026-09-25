# Splat_Brainstorm Implementation Plan

## Outcome and scope

Implement `Splat_Brainstorm_Spec.md`: a correctly configured item produces one cosmetic URP decal on the first eligible impact of each deliberate throw. Item settings optionally remove the source item through the existing multiplayer lifecycle. Splats preserve existing damage, shove, potion, bird-contact, and cauldron behavior.

The deliverable is runtime code, a reusable `SplatDefinition` asset type, and item inspector support. Content authors create definition assets, assign authored decal materials, and enable selected item definitions. Sample content and enabling existing items are outside this feature.

Use the existing registry contact/event pipeline, item IDs, explicit release intent, avatar bindings, active decal renderer, and installed LeanTween. Add shared splat presentation and attachment code because splats outlive their source and can attach to several target types. Avatar cosmetics and potion gameplay remain consumers/reference patterns rather than owners of splats.

Implementation choices for unspecified visual details: square projectors whose width and height equal `Splat Size`, fixed shallow projection depth, no random rotation, gameplay-time animation, and a smooth non-elastic shrink. These choices add no authoring fields. Keep the configured size independent of source-item and receiving-target scale.

## Existing code and integration points

Paths below are relative to the project root.

| Location | Relevant behavior and intended integration |
| --- | --- |
| `Assets/Game/Runtime/Items/ItemDefinition.cs` | Add item opt-in, destruction setting, definition reference, and a shared configuration-validity predicate. |
| `Assets/Game/Editor/ItemDefinitionEditor.cs` | Extend its serialized-property iterator and existing conditional-field pattern. It also edits derived potion definitions and supports multiple selections. |
| `Assets/Game/Runtime/Inventory/PlayerInventory.cs` | `ItemReleaseIntent`, `ReleaseSlot`, and `TryReleaseEquipped` already distinguish throws and drops. Preserve this request path. |
| `Assets/Game/Runtime/Items/WorldItemMessages.cs` | Extend `ItemRecord` with independent splat eligibility; leave potion `Armed` semantics intact. |
| `Assets/Game/Runtime/Items/WorldItemRegistry.cs` | `PredictRelease`, `Release`, `SetHeld`, `Rollback`, `ApplyLifecycle`, `Pool`, `Remove`, world startup/shutdown, and player registration own lifecycle integration. |
| `Assets/Game/Runtime/Items/WorldItem.cs` | `OnCollisionEnter`, `ReportImpact`, `IgnorePlayer`/`UpdateIgnore`, `PredictPickup`, `SetRecord`, and `ReturnToPool` are the source contact/lifecycle hooks. |
| `Assets/Game/Runtime/Items/ItemPlayerContact.cs` | `SweepContact` already calculates a contact fraction and direction but its callback omits the contact point. `HostContact` handles the host-owned victim. Extend the contact data passed to `WorldItem`. |
| `Assets/Game/Runtime/Projectiles/PebbleProjectile.cs` | Also constructs `ItemPlayerContact` and calls `HostContact` in two collision branches. Update these call sites for the richer contact signature without adding splatting to pebbles. |
| `Assets/Game/Runtime/Items/WorldItem.Potions.cs` | `PotionReceiverContact`, `PotionCollision`, and the post-physics receiver sweep can activate/remove a potion before later presentation sampling. Preserve their impact data in the shared contact batch. |
| `Assets/Game/Runtime/Items/WorldItemRegistry.Effects.cs` | `ContactFor`, `QueueIntake`, `QueuePotionImpact`, `FlushPotionContacts`, `ReceivePotionContact`, `AcceptContact`, and `CommitActivation` already aggregate contacts and resolve intake before activation. Generalize this narrow pipeline for splats. |
| `Assets/Game/Runtime/Items/CraftingMessages.cs` | Extend the contact/result messages and `CraftingTransition` so splats and source lifecycle changes can be accepted together. |
| `Assets/Game/Runtime/Items/WorldItemRegistry.Crafting.cs` | `ApplyFeature` samples the local victim before removing a potion. Extend that sequencing to any splat-driven removal. Preserve crafting baselines without adding historical splats. |
| `Assets/Game/Runtime/Avatars/Appearance/AvatarCosmeticPresentation.cs` | Reference for runtime `DecalProjector` creation, inward projection, bone attachment, and disposal. Do not put splat state here. |
| `Assets/Game/Runtime/Avatars/AvatarPresentation.cs` | `AvatarBinding.GetBone`, `AvatarPresentation.Binding`, `WillUnbind`, and `DidBind` provide attachment and avatar-instance lifetime hooks. |
| `Assets/Game/Runtime/Player/PlayerAvatarPresentation.cs` | `Presentation` exposes the third-person avatar binding. |
| `Assets/Game/Runtime/Vehicles/GolfCartNetwork.cs` | `EffectLifetime` and `LifetimeChanged` distinguish cart reuse and despawn. |
| `Assets/Game/Runtime/Birds/BirdRegistry.Physics.cs` and `BirdRegistry.Presentation.cs` | Physical bird proxies use `shapeLives`; visible birds use `views` keyed by bird life. A proxy is not a suitable presentation parent. |
| `Assets/Settings/PC_Renderer.asset` | The active `Avatar tattoos` decal renderer feature supplies rendering. |
| `Assets/_3rdParty/LeanTween/Framework/LeanTween.cs` | `value`, `delayedCall`, and cancellation supply local animation and cleanup. |

## 1. Authoring data and inspector

Create `Assets/Game/Runtime/Items/SplatDefinition.cs` in namespace `TwoBirds`, with a `CreateAssetMenu` entry under `Two Birds` and these fields:

| Field | Default and constraints |
| --- | --- |
| `Material SplatMaterial` | Author assigned. |
| `float SplatSize` | `0.5f`; positive world-space width/height. |
| `float PopDuration` | `0.35f`; nonnegative. |
| `float LifetimeBeforeShrinking` | `30f`; nonnegative, measured from creation. |
| `float ShrinkDuration` | `3f`; nonnegative. |

Add `SpawnSplat`, `DestroyOnSplat`, and `SplatDefinition` to `ItemDefinition`; both booleans default to false. Add one predicate such as `CanSpawnSplat` that requires opt-in, a definition, and its material. All runtime arming and additional destruction use that predicate.

In `ItemDefinitionEditor.OnInspectorGUI`, show `Spawn Splat` normally, and show the other two fields only for enabled or mixed-value selections. Inspect the selected assets after applying serialized changes and show a warning if any opted-in asset lacks its definition or material. A disabled item needs no warning. Preserve derived-definition fields and the existing collision-damage conditional.

## 2. Independent per-throw state

Add `bool SplatArmed` to `ItemRecord`. Use the existing throw identity `(epoch, item world ID, releaser object ID, operation)` everywhere: queued contact, prediction, accepted event, and duplicate suppression. Motion revisions and paths can change during one throw and must not rearm splatting.

Set `SplatArmed = intent == ItemReleaseIntent.Throw && definition.CanSpawnSplat` in both `PredictRelease` and `Release`. Keep the existing `SimulateOnReleaser` decision: the configured simulator reports world collisions, while the existing local-victim contact path may report player impacts. There is no need to change every splatting item's physics ownership.

Clear eligibility on accepted pickup, drop, cauldron output, removal, and source reuse. Connect cancellation of pending local predictions/reports to `PredictPickup`, `SetHeld`, `Rollback`, `Pool`, and world shutdown. A rejected predicted pickup restores eligibility from the accepted record unless that same throw was already accepted as consumed.

Do not store this state in `ResetContactState`: that method also runs for sleeping and other motion changes within a throw. Preserve a consumed/pending identity through same-throw motion corrections, release confirmation, and cart simulator transfer. A later deliberate throw creates a new identity and can splat again.

After accepting a splat, clear the host's stored record and current item's `SplatArmed`. Apply the same change on clients from the accepted event, even if the item survives. This state-only change must preserve current body motion, interpolation, simulator, potion sensors, and potion arming. Keep accepted consumption monotonic for the same identity when applying later lifecycle/motion records, so an in-flight record carrying `SplatArmed = true` cannot restore it. Baselines contain the current eligibility bit but never reconstruct an old decal.

## 3. Capture complete impact information

Create `WorldItem.Splats.cs` for small splat-specific contact adapters and `WorldItemRegistry.Splats.cs` for registry state, prediction, and accepted presentation. Keep placement/target conversion in shared `SplatAttachment.cs` rather than duplicating it across contact adapters.

For solid collisions, capture `Collision.GetContact(0).point`, outward surface normal, receiving collider, and source throw identity before any callback can remove or reuse a participant. Queue this independently of shove/damage thresholds. Existing cart physics, bird contact, and potion processing still run.

Extend the `ItemPlayerContact` callback to include a point and outward normal. For a sweep, derive the point from `Lerp(from, to, fraction) + intoPlayer * sphereRadius`, with outward normal `-intoPlayer`. Pass the actual collision point through `HostContact`. Keep the current velocity inputs for damage and shove. Queue the splat from `WorldItem.ReportImpact` independently of its speed checks.

Update the shared callback consumer in `PebbleProjectile.ReportImpact` and both of its `HostContact` calls, passing their captured collision points while leaving pebble gameplay unchanged. A small contact-value struct can carry the added geometry if it keeps the shared callback clear; it must retain the existing into-player direction used by shove calculations.

`PlayerItemHitbox` is detached from the player hierarchy in `PlayerInventory.OnStartNetwork`; resolve its player using `Hitbox.Motor.ObjectId`, not `GetComponentInParent<PlayerInventory>()`. Likewise use `PlayerEffectReceiver.Effects` for potion receiver contacts.

Honor `ignoredPlayer` and the existing launch-immunity behavior. Reuse each contact path's existing immunity gate; do not add a second fixed delay that prevents a splat after the physical collision ignore has ended. For a splat-enabled potion, pass the receiver identity, closest contact point, and corresponding outward direction with the existing receiver impact report so activation and its eligible splat can be resolved together. Keep potion-specific immunity and sensor activation independent of splat consumption.

A non-simulating client's local-player contact is a valid report; do not copy the simulator-only gate from `QueuePotionImpact` into that adapter. Other clients must not invent world impacts from interpolated poses. Ignore replay callbacks. Do not require speed, impulse, decal receiver support, or a visible material on the struck surface to consume the splat.

Store the first eligible splat candidate per throw. Later bounces cannot replace it. Continue collecting potion and intake information even after a splat candidate has been captured. Preserve the candidate until predicted release confirmation; cancel it if that release is rejected.

## 4. Resolve intake, effects, splat, and removal together

Generalize the existing contact batch instead of adding a competing collision resolver. Rename `PotionContact`/`PotionContactResult` and their narrow queue/handler names to `ItemContact`/`ItemContactResult` as they now carry ordinary item contacts. Update their registrations and callers. Keep potion activation data separate from the optional splat candidate: `Impact` must continue to mean potion impact, not generic splat consumption.

Add optional splat data to `ItemContact` and `CraftingTransition`. Keep the existing crafting transition channel/name; it already transports compound item-removal and effect outcomes. `SplatEvent` carries source throw identity, source definition byte, target descriptor, and target-local pose. Resolve style through `GetDefinition(definitionId).SplatDefinition`; no material, texture, or new style catalog travels over the network.

Use reliable delivery through the registry's `observers`. Serialize the optional contact/event payload only when its flag is set; pack enum/boolean fields and use the existing FishNet packed quaternion facility for orientation. Do not send decal animation updates. If a custom serializer is added for `CraftingTransition`, preserve all existing cauldron, item, activation, blast, and snapshot fields and their readers symmetrically.

At the existing end-of-frame contact flush, resolve in this order:

1. Merge reports for the same throw. Queue received reports until this flush as well, allowing local physics/intake callbacks from the same frame to contribute. Merge intake and potion flags without overwriting an already chosen splat candidate. Keep reports separate across throw identities.
2. Check epoch, current item state/throw identity, accepted consumption, and current definition validity. Preserve the existing tolerance for a newer current motion revision; simulator transfer does not create a new throw.
3. If the reported cauldron can accept the ingredient, call `Admit` and finish with no splat. An intake candidate that is refused does not suppress an otherwise eligible collision.
4. Capture/build the potion activation and splat outcome from the still-available item record and contact data. A physical splat contact on an armed potion must include the corresponding potion impact in this same resolution, including a local-victim report that arrives before the simulator's report. Preserve the existing potion-only sensor path when no eligible splat is present.
5. Clear splat eligibility. Determine source removal as existing potion/removal behavior OR a consumed valid splat with `DestroyOnSplat` enabled. Create at most one tombstone.
6. Commit one transition containing the effect(s) and optional tombstone. Refactor `CommitActivation` just enough to build an activation without immediately committing/removing; direct use still commits its normal potion outcome.

Extend `ApplyFeature`'s pre-removal local-victim sampling to splat-driven removal too. Retain source record, point, velocities/contact history, and resolved target before `ApplyLifecycle` pools the source. Suppress creation of a second splat report while sampling an already accepted transition, while still executing the existing local damage/shove logic.

A later potion-only report for an already activated throw must not activate it again. A repeated splat report must not tombstone again. Transparent or otherwise unsupported surfaces still produce an accepted consumption/removal outcome; rendering success never controls gameplay lifecycle.

## 5. Prediction and confirmation

Create one provisional decal on the reporting client once its retained contact batch has no accepted-intake precedence. Withhold provisional presentation when the batch contains a cauldron intake candidate until the result is known. The host presents its own committed event directly.

Maintain predicted instances keyed by throw identity and accepted event identities independently of projector lifetime. The accepted event promotes/repositions the existing provisional projector to the agreed target and pose without creating another projector or restarting its animation. If a different reporter won, correct the provisional attachment. Other clients create one projector and run the same definition locally.

The contact result must distinguish rejection/intake from accepted splat consumption. A duplicate report for an already accepted throw must return that accepted outcome rather than deleting its decal as a rejection. Send the accepted transition before its result on the reliable channel. Route local-host resolution through the same idempotent presentation method so a host echo cannot duplicate it.

Cancel rejected predictions on the result, release rollback, accepted pickup before impact, or world teardown. Confirmed decals attached to another target survive source pooling/removal. Keep accepted identities until world teardown so an event repeated after its animation has expired still cannot recreate the decal. Clear all epoch-scoped pending/accepted state on `EndWorld`.

Use the existing reconciliation deferral pattern for accepted events. Do not replay creation during FishNet resimulation. `SendCraftingBaseline` and item baseline completion send no past splat events; a joining observer receives future events only.

## 6. Target identity, local pose, and attachment

Define a compact `SplatTarget` descriptor and shared capture/resolve functions in `SplatAttachment.cs`. Capture the receiving identity and its local pose on the reporting client. All peers resolve that same identity and attachment; do not choose a fresh nearby target by proximity on each client.

| Target | Identity and attachment |
| --- | --- |
| Player | Player object ID and current player lifetime/reset, plus a `HumanBodyBones` value. Resolve through `TryGetPlayer` and `PlayerAvatarPresentation.Presentation.Binding`. Choose the closest available major torso/limb/head bone once on the reporter and transmit it. Exclude finger/eye bones. Store the pose relative to that bone. |
| Golf cart | Cart object ID plus `EffectLifetime`; attach to its presented transform or the specifically struck moving child. Convert physics contact pose through the cart body frame to the corresponding presentation frame before storing it. |
| Another world item | Target world ID, distinct from the source ID. Resolve with `TryGetItem`; parent to the receiving item's presented transform. The world ID, not its pool slot or definition byte, identifies the target. |
| Live bird physical proxy | Resolve the collider through `BirdRegistry.shapeLives` to bird life, then resolve the corresponding `BirdView` through a small internal lookup in `BirdRegistry.Presentation.cs`. Attach to that view. If its bird/view has been removed, suppress the visual. Never parent to a reusable hit proxy. Preserve existing bird hit/death reporting. |
| Other networked object | Its existing FishNet network object identity and a relative child path when the struck part moves independently. Resolve through the spawned-object lookup. |
| Authored scene object | A scene-relative hierarchy path resolved from a cache captured at the beginning of `BeginWorld`/`JoinWorld`, before item reparenting/deactivation. Encode original root/child sibling indices and retain the mapping for the world. Static and moving scene objects both retain their actual receiving transform. |

Use world-space contact pose only as transient capture input. In the event, transmit the target-local point and rotation. For a child of an identified object, encode a relative child-index path only when needed. Keep paths stable by capturing authored scene mappings once, rather than recomputing identities after reparenting. Skip runtime item, player, cart, and bird hierarchies in the authored-scene mapping; they use the identities above.

For players, use the same third-person body binding on every client. This feature does not add a camera overlay or a separate first-person-hands decal. If binding is still being established, retain the accepted event until `DidBind`, within its remaining lifetime; resolve the transmitted bone, not a newly selected bone. A removed/replaced lifetime cancels the pending visual. Do not serialize `AvatarBinding.Generation`, which is local presentation bookkeeping.

For any target, a missing/despawned identity suppresses its presentation while preserving consumption and optional source removal. Do not leave a world-space substitute floating after a moving target disappears. A later pooled occupant must never inherit the old decal.

## 7. Runtime presentation and cleanup

Create `Assets/Game/Runtime/Items/SplatPresentation.cs`, a small runtime component on the generated decal object. It owns its projector, animation IDs, and disposal. Create `SplatTargetLifetime.cs` as the shared runtime attachment owner on receiving targets; it owns attached presentations and disposes them on target disable/destruction or explicit lifetime invalidation. Add these components automatically when needed.

Create the projector inactive, configure it, then enable it. Assign the authored material directly and leave its texture/color untouched. Use `DecalScaleMode.ScaleInvariant` and animate `DecalProjector.size.x/y` with `LeanTween.value`; keep a shallow fixed depth, initially `0.05 m`, and center the volume on the contact with `pivot = Vector3.zero`. Orient local `+Z` into the surface using `Quaternion.LookRotation(-outwardNormal, stableTangent)`. Preserve elastic overshoot in the width/height animation. Scaling the projector dimensions keeps the requested width in meters while the parent supplies motion. See the [Unity URP projector reference](https://docs.unity.com/en-us/engine/6000.5/manual/visual-effects/decals/renderer-feature-decal/projector-reference) for projection direction, dimensions, pivot, and scale mode.

Use this timeline, all measured from local creation:

- Pop from an effectively collapsed width/height to `SplatSize` over `PopDuration` with fixed `LeanTweenType.easeOutElastic`.
- Independently schedule shrink start at `LifetimeBeforeShrinking`. Do not chain the full lifetime delay after the pop.
- At shrink start, cancel a still-running pop and smoothly shrink the current size to zero over `ShrinkDuration`, then dispose.
- Handle zero pop/shrink duration immediately. A lifetime shorter than the pop cancels the pop at the requested shrink start. Defaults remove the decal at 33 seconds.

Cancel active tween and delayed-callback IDs during disposal, disable the projector immediately, release the generated object, and remove bookkeeping. Do not destroy the shared material. Disposal is idempotent and does not fire animation completion callbacks that create new work.

Connect target invalidation to existing lifecycles in addition to ordinary parent disable/destruction:

- `WorldItemRegistry.Pool` / `WorldItem.ReturnToPool`: clear decals received by that target item before reuse. Decals produced by it on other targets remain.
- `AvatarPresentation.WillUnbind`: remove decals on the outgoing avatar binding before it is released/replaced. Subscribe through the shared attachment owner, not avatar cosmetic state.
- `WorldItemRegistry.UnregisterPlayer`: invalidate player-target presentations and pending attachments. Use `PlayerHealth.LifeChanged` with the cached player's lifetime/reset to clear respawn/reuse decals without treating ordinary health changes as a new target.
- `GolfCartNetwork.LifetimeChanged`: clear old-lifetime attachments on replacement/despawn, including changes that keep the GameObject active.
- Bird `ReturnView` and target `OnDisable`: clear before the view is reused; bird splats do not migrate to a replacement corpse.
- Generic network targets: receive their despawn callback through the runtime attachment owner or a narrow hook in the existing target lifecycle; disabling the renderer alone is not sufficient cleanup.
- `WorldItemRegistry.EndWorld`: dispose attached and static scene splats, deferred attachments, provisional instances, and subscriptions.

## Implementation order

1. Add definition fields, validity predicate, asset type, and inspector behavior.
2. Add `SplatArmed` and wire throw/pickup/drop/rollback lifecycle transitions.
3. Define splat/contact payloads, target capture/resolve helpers, and runtime presentation/target cleanup.
4. Extend world/player/potion contact capture with complete target and pose data.
5. Generalize contact aggregation and compose intake, potion activation, splat consumption, and removal into one accepted transition.
6. Wire prediction promotion/rejection, duplicate suppression, same-throw state merging, reconciliation deferral, and teardown.
7. Finish target-specific lifecycle hooks and author-facing acceptance handoff.

## User visual acceptance

The content author creates a compatible decal material and a `SplatDefinition`, then assigns it to an opted-in item definition. Exercise these scenarios in the game, including host and remote-client throws:

| Scenario | Expected result |
| --- | --- |
| Inspector opt-in and missing configuration | Conditional fields appear correctly; missing definition/material warns; neither splat nor splat-driven destruction occurs until valid. |
| Ground, wall, player, cart, and another physical item | One decal at the first eligible contact, including gentle player contact; size is independent of the thrown item. |
| Drop, held contact, and resting spawned item | No splat. |
| Surviving item bounces, is picked up, and is thrown again | One splat per deliberate throw; no rearming from bounce, sleep/wake, correction, or simulator transfer. |
| Thrower immunity | No launch-overlap splat; a later returning hit after immunity can splat. |
| Destroy enabled/disabled on ordinary items and potions | Correct source removal; existing damage/shove/potion activation still occurs; source removal leaves the receiver's decal alive. |
| Accepted versus refused cauldron intake | Accepted ingredient intake produces no splat; a refused intake can proceed to a normal eligible impact. |
| Pop and lifetime | Elastic pop for 0.35 seconds, shrink starts at 30 seconds, removal at 33 seconds with defaults; short/zero timings finish cleanly. |
| Moving/scaled targets, avatar/cart changes, and pooled reuse | Decal follows the selected target/bone at the configured world width and disappears when that target instance ends. |
| Two or more clients and rapid consecutive throws | Agreed style/attachment/removal and one decal per event, including prediction confirmation and repeated contact paths. |
| Late join | Existing visible splats are not reconstructed; future splats appear. |
| Transparent/non-receiving surface | The throw is consumed and configured source removal occurs even though the decal cannot be displayed. |

Native shallow-projector overlap and approximate bone attachment remain the rendering limitations accepted by the specification.
