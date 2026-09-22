# Slingshot implementation plan

## Outcome and scope

Implement `Slingshot_Spec.md`: a nonstacking inventory weapon that remains equipped while firing unlimited, transient pebbles. Each pebble uses gravity, rebounds once, and disappears with dirt particles on its second distinct impact. The simulator decides physical motion, bounce, bird contacts, and terminal impact. Player hits use the same victim-local detection and damage application as rocks. Preserve ordinary rock, inventory, health, bird, and crafting behavior.

This plan assumes temporary frame and pebble geometry, the current player action/hand presentation pipeline, and the existing session host. Simulator handoff means transferring a disconnected shooter's live projectiles to the session server, matching the rock convention; it does not introduce session-host migration.

Reuse the rock system's player-hit detection, damage application, bounce/contact handling, impulse suppression, and bird-hit reporting. Extract their item-independent parts when necessary so rocks and pebbles call the same implementation. Preserve victim-local player detection, including the existing host collision path, remote/predicted sweeps, and contact re-arming/repeat-hit rules. Slingshot-specific additions are the retained weapon, two-handed draw, transient ammunition lifecycle, two-impact limit, and fixed damage. Do not introduce a replacement physics simulation, shooter-resolved player-hit transport, a parallel damage detector, or a once-per-pebble player-damage limit.

### Required behavior

| Setting | Value and ownership |
| --- | --- |
| Launch speeds | Configurable 8–28 m/s; interpolate linearly by charge |
| Full charge | Configurable 1 second; hold indefinitely |
| Player damage | Configurable 10, independent of charge and speed |
| Recovery | Configurable 0.5 seconds, beginning only after a successful shot |
| First rebound | Configurable 0.75 × incoming speed |
| Lifetime | Fixed 30 seconds from firing, preserved across handoff |
| Frame centering | Approximately 0.15 seconds, independent of charge duration |
| Visual return | Approximately 0.2 seconds, independent of recovery duration |

Use existing Use press/release input, crosshair, charge HUD, movement permissions, cancellation paths, and player/cart velocity inheritance. An ignored press during recovery never becomes a queued draw. A clear quick tap launches immediately from the currently presented loaded pebble. Blocked release and cancellation create neither a projectile nor a new cooldown.

No ammo inventory, reload, spread, aim assistance, elevation compensation, or general weapon framework is part of this feature.

## Relevant code and integration points

All paths below are relative to the project root.

| Area | Files and integration |
| --- | --- |
| Definitions and use | `Assets/Game/Runtime/Items/ItemDefinition.cs`, `ItemUseBehaviour.cs`, `ThrowableItemUse.cs`: retain the use contract and inherited launch-speed/charge fields; add a slingshot-specific definition and behavior. |
| Equipment and input | `Assets/Game/Runtime/Player/PlayerEquipment.cs`, `PlayerInputReader.cs`; `Assets/Game/Runtime/Inventory/PlayerInventory.cs`: edge-triggered Use already exists. Rock release currently removes the inventory item, so firing needs its own submission path. |
| Replicated actions | `Assets/Game/Runtime/Player/PlayerNetworkState.cs`: `ItemActionSnapshot`, `PredictRecovery`, `AcceptRecovery`, `CompleteRecovery`, and `ActionAge`. `CmdItemAction` rejects recovery snapshots; server acceptance must use the firing transaction. |
| Held visuals | `Assets/Game/Runtime/Player/PlayerHeldItemPresentation.cs`: `CanShowHeldItem`, `ReadyForUse`, `TryPrepareRelease`, hand prepare/commit/clearance, and recovery stages. Recovery currently hides the held item and tracks a thrown `WorldItem`. |
| Hand evaluation | `Assets/Game/Runtime/Player/PlayerHandPresentation.cs`, `Assets/Game/Runtime/Avatars/AvatarHandTargets.cs`, `LocalFirstPersonHands.cs`, and `Assets/Game/Runtime/Items/HeldItemPose.cs`: one shared target collection supports both hands and both avatar presentations. |
| Release clearance | `Assets/Game/Runtime/Items/ItemReleaseClearance.cs`: reuse the environment accessibility and reach correction rules; add a loaded-pebble release sample. |
| Motion and lifecycle | `Assets/Game/Runtime/Items/WorldItem.cs`, `WorldItemRegistry.cs`, `WorldItemRegistry.Motion.cs`, `WorldItemMessages.cs`: Rigidbody capture, prediction integration, snapshots, interpolation, correction, reliable boundaries, batching, disconnect takeover, and pooling. |
| World startup | `Assets/Game/Runtime/Pickup/PickupRegistry.cs` calls `WorldItemRegistry.BeginWorld`, `JoinWorld`, and `EndWorld`. The projectile service will be owned by that registry and participate in these existing calls. |
| Contacts and player hits | `Assets/Game/Runtime/Items/WorldItem.cs`: reuse/extract `SamplePlayerContact`, `SweepRemoteContacts`, `SweepContact`, incoming/contact sampling, and the host-local collision path. `Assets/Game/Runtime/Player/PlayerItemHitbox.cs` supplies `SweepSphere` and `Damage`; `PlayerHealth.cs` supplies damage application. Retain the victim-owner decision and existing health replication. |
| Bird contacts and reporting | `Assets/Game/Runtime/Birds/BirdRegistry.Physics.cs`, `BirdRegistry.Events.cs`, `BirdHitReporter.cs`, `BirdRewardLedger.cs`: existing CCD/contact modification with target inverse-mass/inertia suppression, shooter-reported contacts, moving bird shapes, speed/species rules, prediction, rewards, and per-release scoring. |
| Cauldron | `Assets/Game/Runtime/Crafting/Cauldron.cs`, `CauldronIntake.cs`; `Assets/Game/Runtime/Items/WorldItemRegistry.Crafting.cs`, `WorldItemRegistry.Effects.cs`: both held insertion and physical intake currently accept general items. |
| Authoring | `Assets/Game/Editor/ItemSetup.cs`, `BakeTools.cs`; `Assets/Game/Runtime/Pickup/BakedPickup.cs`; `Assets/Game/ScriptableObjects/ItemRegistry.asset`. |

## Implementation design

### 1. Definition, use behavior, and crafting eligibility

Add these files under `Assets/Game/Runtime/Items/`:

- `SlingshotDefinition.cs`: subclass `ItemDefinition`, following the existing `PotionDefinition` pattern.
- `SlingshotItemUse.cs`: subclass `ItemUseBehaviour`, attached to the slingshot world prefab.
- `SlingshotPresentation.cs`: component holding frame attachment references, procedural band rendering, loaded-pebble visuals, and slingshot pose state.

Use inherited `MinThrowSpeed`, `MaxThrowSpeed`, `ThrowChargeTime`, and `VelocityInheritance` for firing. Set the slingshot asset to 8, 28, 1, and the rock inheritance value respectively. Add only the slingshot-specific damage, recovery, rebound retention, pebble prefab, dirt-effect prefab, and necessary authored pose data. Keep the 30-second projectile lifetime as a constant. Frame mass, drop speed, and dropped-item physics remain ordinary `ItemDefinition` properties and do not tune pebble launch force.

Set `Stackable = false`, `MaxStack = 1`. Use a separate projectile damage field so the frame's ordinary dropped-item collision settings do not accidentally become pebble damage settings.

Introduce `ItemDefinition.CanBeIngredient` as a virtual read-only capability, defaulting to true; override it to false in `SlingshotDefinition`. This is a fixed feature rule, not a new inspector toggle. Apply it in all of these places:

1. `Cauldron.CanInteract`, so an equipped slingshot does not offer an insertion action.
2. `PlayerInventory.ConsumeEquipped` before predicted insertion alters the inventory view.
3. `WorldItemRegistry.AdmitHeld` and the common admission function, before changing the mixture or creating a tombstone.
4. `WorldItemRegistry.QueueIntake` and `AcceptContact` in `WorldItemRegistry.Effects.cs`, before consuming physical intake reports.

Pebbles never have `WorldItem`, `BakedPickup`, an item definition entry, or an inventory record, so `CauldronIntake` cannot collect them. Solid cauldron surfaces still participate in projectile collision queries.

### 2. Shared motion and pooling, separate projectile records

Add a small `Assets/Game/Runtime/Projectiles/` directory. Use the following concrete responsibilities; combine tightly related record/message declarations in one file where practical:

| File/class | Responsibility |
| --- | --- |
| `PebbleProjectile.cs` | One pooled Rigidbody/visual instance using shared rock motion/contact handling; two impact stages, initial shooter clearance, and full shot reset. |
| `PebbleRegistry.cs` | Active projectile records, local prediction, spawn acceptance, impact delivery, lifetime, observer baselines, simulator takeover, and pool ownership. A plain service owned by `WorldItemRegistry`, not another scene singleton. |
| `PebbleMessages.cs` | Only the transient spawn/result, live record, and physical impact/removal fields that the existing motion/contact messages cannot already carry. Player damage uses existing health replication without a new hit message. |

Extract reusable motion and pool mechanisms under `Assets/Game/Runtime/Items/` or a shared runtime directory:

- `RigidbodyMotionState`: extract the applicable capture, sample history, interpolation, path-boundary reset, correction, and visual-offset logic from `WorldItem`. It takes body/visual references, motion settings, and explicit simulation/state inputs. It must not look up inventory, holders, potion effects, or crafting records.
- `RigidbodyInstancePool<T>`: extract the prefab rent/return mechanism used by `WorldItemRegistry`, with reset performed by each concrete instance before return. World items keep their definition-keyed pools; pebbles and dirt effects use their own prefab-keyed pools. Share only the actual lifecycle mechanics.
- Extract the per-motion read/write functions from `ItemMotionBatchSerializer`, retaining the current item wire layout. Both item and pebble batches use those functions and the same `ItemMotion` fields, velocity packing, and optional rotation omission.
- Extract the item-independent player-contact sampling from `WorldItem` into a shared `ItemPlayerContact` helper used by both rocks and pebbles. Preserve the existing `PlayerItemHitbox.SweepSphere` geometry, motion/correction rebasing, victim-owner checks, host collision path, and repeat-hit/contact-separation bookkeeping. Keep damage amount, speed eligibility, and shove settings as caller policies; both callers use the same repeat-hit rules. Reuse the rock contact/impulse and bird-hit helpers as well; do not leave rocks on one implementation and build another for pebbles.

Keep `WorldItem`'s held attachment, release handoff, rock damage rules, bird lift, potion, pickup, and inventory decisions on `WorldItem`. Share the underlying motion, contact detection, damage application, and bird-reporting mechanisms without copying those decisions into the projectile service. Pebbles use sphere-center motion and omit cosmetic rotation in routine snapshots.

`WorldItemRegistry` owns construction, startup, physics/tick/presentation callbacks, baseline participation, connection changes, and shutdown of `PebbleRegistry`. Reuse its epoch, time manager, prediction manager, motion tuning, observer set, world scene, bounds, batch relay, and connection handling. Keep the new service limited to transient record/lifecycle decisions; do not copy the registry's transport or motion loop into a parallel implementation. Ensure bird physical shapes and player hitbox poses are ready before projectile contacts are evaluated. Both systems skip replayed gameplay effects during FishNet reconciliation.

A pebble is absent from `WorldItemRegistry.items`, `records`, and `pendingReleases`, but participates in the same extracted victim-local contact sampling. Call that helper from the projectile presentation/physics lifecycle with the local victim, just as the existing item loop does. This reuses hit detection without adding inventory semantics or permanent item tombstones. Do not achieve the separation by adding a projectile flag to otherwise unchanged `WorldItem` records.

### 3. Projectile identity and live state

Use immediate local spawn prediction with server-assigned runtime IDs:

1. Identify a pending shot by shooter object ID, existing player lifetime, and a monotonically increasing equipment shot sequence. Selection, dropping, and unequipping do not reset this sequence.
2. Allocate the accepted projectile's `uint` ID from a shared runtime-ID allocator extracted from `WorldItemRegistry.Crafting.nextCraftedId`. Seed it above baked IDs and let both crafted outputs and pebbles allocate from it. IDs are not reused within the world epoch.
3. The predicted object keeps its current flight when the result associates its pending key with the accepted ID. It is not despawned/recreated or returned to its launch pose.
4. Before acceptance, keep motion locally and retain ordered impact/removal reports. After acceptance, send those reports and the latest motion under the accepted ID. If the pebble already ended, retain only the pending result data needed to finalize the transaction and suppress a duplicate local effect.

Use the accepted projectile ID as the bird reward source ID as well. This prevents collisions with rock release identities without changing every existing bird report key.

Each live record contains:

| State | Purpose |
| --- | --- |
| `ItemMotion` | ID, motion revision, tick, sequence, path, position, velocity, and boundary flags |
| Definition ID | Resolve the slingshot's projectile tuning and visuals |
| Shooter ID and lifetime; source weapon world ID; shot sequence | Prediction association and provenance |
| Simulator connection ID | Shooter simulation, then server takeover using the existing negative-ID convention |
| Bird player token | Reward attribution that survives transfer of simulation |
| Original launch tick/fraction and expiry | 30-second deadline from the original release, not acceptance or takeover |
| Impact ordinal | Zero before contact, one after the rebound; second contact is terminal |
| Shooter-cleared flag | Whether self-collision has become eligible |
| Current contact/separation state | Prevent takeover inside a first-impact manifold from inventing a second impact |

Store stable player/bird identities in replicated state, not Unity instance IDs. For static surfaces, the contact position/normal and a rebase-overlap suppression set reconstructed by the new simulator are sufficient; do not send scene collider instance IDs. A handoff seeds suppression only for the current overlap/contact episode and releases it after separation.

Player-hit state is the existing rock contact bookkeeping on the victim's client. Reuse its separation/re-arming and correction/handoff rebasing, and clear instance contact state on pool return. Do not add local or replicated per-shot damaged-player lists. Stable shot identity is still needed for lifecycle/network ordering and bird rewards.

### 4. Use transaction and recovery

`SlingshotItemUse` follows the `ThrowableItemUse` Begin/End/Cancel contract, but releases ammunition instead of calling `TryReleaseItem`:

1. `BeginUse` starts charge only through `PlayerEquipment`'s existing permission and readiness checks. The loaded pebble becomes visible when the action enters Charging.
2. Charge is clamped elapsed time divided by `ThrowChargeTime`. Local HUD, band extension, and launch speed read the same value. Remote visuals derive that value from the replicated start time and the same duration.
3. `EndUse` samples charge and requests a committed, clear loaded-pebble pose. On success, submit a shot through `PlayerEquipment` to `PebbleRegistry`, spawn locally, and predict recovery. Do not mutate inventory slots, release the frame's world ID, arm the frame, or start its pickup cooldown.
4. On failure or cancellation, clear the charge and loaded pebble and return the hands toward idle. Do not invoke the firing snap or start recovery.

Use the existing `ItemActionSnapshot` states. Its `WorldId` continues to identify the slingshot frame; `Operation` identifies the shot. Carry the predicted recovery snapshot in the reliable fire request, and have the server call `AcceptRecovery` as part of accepting that request. Do not route Recovering through `CmdItemAction`, which intentionally rejects it. On rejection, remove the predicted projectile and complete only the matching recovery operation.

The transaction carries the source frame, player lifetime/control revision, shot sequence, definition, original fire time, initial motion, and recovery snapshot. Apply normal equipment/lifecycle consistency checks without re-solving the owner's aim or imposing server-authoritative trajectory decisions.

In `PlayerHeldItemPresentation`, separate recovery's use deadline from its presentation mode:

- For a rock, retain its existing follow/pause/return behavior and duration.
- For a slingshot action, the use deadline is the release start time plus the firing definition's `RecoverySeconds`. Read the firing definition even if another item is now selected.
- Slingshot recovery never enters `FindProjectile`/rock Follow, hides the still-held frame, opens the gripping right hand, or treats the frame's world record as a pending release.
- Return the frame and pulling hand over approximately 0.2 seconds. Keep the action Recovering until the 0.5-second gate ends. A longer configured visual return must likewise not extend the gameplay gate.
- If selection changes during recovery, show and pose the current selection normally while retaining the original use deadline. Do not continue applying slingshot grip offsets to a newly selected item.
- `CancelUse` during recovery must not cancel it merely because there is no active charge. Preserve the existing permission-loss, ownership, and lifecycle reset behavior separately.

All selected-item use entry points, including direct potion use, continue checking the shared readiness gate. Selection, inventory movement, and dropping remain available. `PlayerInputReader` already uses `WasPressedThisFrame` and `WasReleasedThisFrame`; preserve those edges and add no held-button retry after recovery.

### 5. Frame, two hands, band, and launch sample

The slingshot prefab exposes these explicit transforms beneath `VisualRoot`:

- Left and right fork tips.
- Band rest center and full-draw center.
- Pulling-hand palm orientation/offset relative to the band center.
- A collider-free loaded-pebble visual at that center.

Keep the existing item grip transform/data as the right-hand frame grip. Author local first-person and remote avatar frame poses separately, using the existing first-person spatial pose and avatar-space pose data. The charge frame pose centers beneath the crosshair over about 0.15 seconds. The band center interpolates independently from rest to full draw over the full charge duration; do not use the rock's charge-arc easing as the charge-strength fraction.

`SlingshotPresentation` evaluates the slingshot-specific pose and band state. `PlayerHeldItemPresentation` remains responsible for selecting presentation mode, obtaining the body frame, committing the held item, and applying obstruction correction. Add a left Item hand target through `AvatarHandTargets`; use the current hand-binding generation and left-palm measurements when converting to the left wrist. Preserve Contact-source priority for seating/control transitions.

The hand pipeline must evaluate in this order:

1. Compute the desired right-hand/frame pose for the action and viewpoint.
2. Apply existing frame reach/obstruction correction.
3. Compute band center and left-hand target from that corrected frame.
4. Evaluate both hands, commit the actual right-palm/frame pose, and update the band center from that committed pose.
5. If clearance correction requires another pass, update both targets before re-evaluating and committing. The left palm, loaded pebble, and band center must use the same final point rather than independently clamped positions.

Constrain the authored draw to the pulling hand's reach. If a correction moves the frame, recompute the shared draw center consistently so the pebble cannot detach from the drawing hand. Clear the slingshot's left Item target on selection changes, cancellation completion, permission loss, binding changes, disable, and pool return; leave unrelated hand sources intact.

Render two simple band segments from the fork tips to the shared center. On a successful release, hide the loaded pebble, snap the band toward rest, and apply a short damped wobble driven from release time. The left hand releases and returns to its idle/free pose. The right hand keeps its frame grip. Cancellation blends toward idle without a release snap or wobble.

Use `ItemActionSnapshot.ReleaseArcProgress` as a quantized release draw fraction for slingshot recovery; retain its rock meaning for rocks. The definition identifies which interpretation applies. Together with charge duration this is enough to reconstruct partial centering at a tap. Do not transmit hand targets, band endpoints, or progress every frame.

Add a slingshot release-sampling method alongside `TryPrepareRelease`:

- Call `PlayerHandPresentation.SampleImmediately` before deciding launch position, so same-frame press/release uses the current presented pose.
- Return the actual loaded-pebble center/radius and charge fraction after hand evaluation and obstruction correction. Never substitute the final fully centered or full-draw pose.
- Resolve both the frame envelope and pebble sphere using `ItemReleaseClearance`. Apply any permitted positional correction back to the frame/band/hand sample, then commit again. Do not correct only an invisible spawn point through a wall.
- Check accessibility from the aim/body reference and the final sphere's overlap. If correction cannot provide a clear launch, return failure before creating a shot or recovery snapshot.

Aim from that final loaded-pebble position to the nearest eligible point on the camera-center ray. Use `PlayerPresentation.AimPose` and include solid scenery, world items, carts, living-player hitboxes, downed bodies, and living birds. Exclude the shooter, held items, other pebbles, and ordinary triggers. A miss uses the corresponding camera view direction. This aim query is separate from the existing environment-only clearance mask.

The initial velocity is:

`aimDirection * Lerp(MinThrowSpeed, MaxThrowSpeed, charge) + inheritedMovementVelocity * VelocityInheritance`

Extract the movement-velocity lookup used in `PlayerInventory.ReleaseSlot` into a small shared release helper callable by inventory throws and slingshot fire. Preserve the current `PlayerSeating.PointVelocity`/motor-body behavior, including cart motion. Do not add a force multiplier.

Remote first-person and avatar offsets differ. When an observer receives a spawn, use its matching displayed loaded-pebble position as a short visual departure offset, using the shared correction machinery. The authoritative physics origin remains the shooter's resolved origin. End that cosmetic offset at the first trajectory boundary, and never follow the flying projectile with the pulling hand.

### 6. Reuse rock Rigidbody contacts and impulse suppression

Use the rock's dynamic Rigidbody simulation, project gravity, `OfflineRigidbody` integration, physics cadence, incoming-velocity capture, and `ContinuousDynamic` collision detection. Keep an enabled sphere collider on the simulator. Use its actual radius for release clearance. The loaded visual remains collider-free. Do not replace this with a new query-driven flight integrator.

Extract the target impulse-suppression mechanism already implemented in `BirdRegistry.Physics.ModifyRockContacts` into a shared contact helper callable by rock/bird handling and pebbles. Preserve its ordinary and CCD callback handling, normal orientation correction, contact suppression, thread-safe contact capture, and zero target inverse-mass/inertia scales. Leave bird shape creation/motion in `BirdRegistry`.

Extend that same mechanism to pebble contacts with all eligible dynamic targets. Ensure only the projectile receives the contact response. Keep shared contact metadata available to the modifier without accessing mutable Unity object state from a physics worker callback. Avoid having an old bird callback reject pebble contacts because the projectile is absent from `physicalRocks`; route both through the same registration/dispatch and retain rock-only settings as a contact policy.

`ItemDefinition.DontPushPlayer` suppresses the scripted player shove; it does not by itself provide the complete all-target solver policy. For pebbles, suppress both paths. Non-simulating projectile colliders must be disabled or excluded from solver interaction entirely, so remote kinematic bodies cannot push props. When a client takes over simulation, restore the simulator's collider/contact registration.

`GolfCartController.AccumulateCollision` currently uses `collision.impulse` and marks dynamic contacts. A contact solver can report an impulse associated with the pebble's rebound even when target mass scaling prevents a cart velocity change. Reuse the shared no-impulse classification to exclude pebble contacts before either cart accumulator is changed. Apply this only where the existing rock/contact policy does not already exclude them.

Layer filtering and per-shot collision exclusions must ignore held items and other pebbles while including scenery, world items, carts, player hitboxes, ragdolls, and bird hit shapes. Register initial shooter ignores using the existing release-clearance mechanism, restoring self-collision after geometric separation. Keep that cleared state through handoff. Ordinary triggers do not count as impacts; reuse the existing `ItemKillVolume` removal path. Use a swept kill-volume check only if the current fast-item path cannot detect a crossing at projectile speed.

Add the following pebble policy to the shared resolved-contact path:

1. Capture the incoming projectile velocity and resolved surface point/normal before any rock-specific response changes it.
2. Coalesce simultaneous contact points/manifolds into one physical impact episode. Resolve the earliest episode before a farther target and report eligible bird reactions through the existing bird path. Player damage remains with the shared victim-local detector; do not also apply it from a simulator contact with a remote player.
3. On first impact, reflect incoming velocity about the resolved normal and retain the configured fraction of its full magnitude: `Vector3.Reflect(incomingVelocity, normal) * retention`. Keep ordinary Rigidbody gravity after rebound. Do not add bird lift, change target velocity, or multiply an already damped rebound again.
4. Restitution alone only scales the normal velocity component. Use the existing contact-response hook to enforce the required full-speed retention, including tangential speed, before publishing the boundary. Rocks keep their existing restitution/lift policy.
5. On the second distinct episode, report eligible bird reactions and terminate at the resolved contact. Preserve the final valid motion/contact segment for victim-local sampling before resetting it. Suppress physical contacts beyond the terminal trajectory.

Reuse the existing touching/separated-contact bookkeeping, extending it from bird contacts where needed. Repeated contact-stay callbacks and multiple points from the same episode do not advance the impact ordinal. Geometric separation re-arms that collider, so returning to the same floor or object counts as the second impact. Do not implement a permanent per-collider ignore set, a contact cooldown, or a blanket one-impact-per-tick flag. A distinct second episode in the same physics step must remain possible; use the existing CCD/contact ordering and item sweep support where needed to disambiguate it, not a separate flight simulation.

Seed overlap/contact suppression on takeover using the existing rebase pattern so an inherited first-contact manifold is not counted twice. Do not disable gravity or let sleep freeze a still-live airborne pebble. Terminal causes are second impact, lifetime expiry, kill-volume entry, and the existing world/fall-boundary cleanup.

| Target | Contact handling |
| --- | --- |
| Scenery, terrain, solid cauldron, carts, loose items, solid props, downed bodies | Count an impact; no damage or force except eligible living targets below |
| Living player, including shooter after clearance | Simulator contact counts as a physical impact; existing victim-local detector independently applies fixed damage |
| Living bird | Count an impact; report existing rock-style bird reaction/reward using pre-impact speed |
| Held item, another pebble, ordinary trigger | Ignore |
| Item kill volume | Remove without dirt |

Ignore all shooter collision representations until the projectile has geometrically cleared the launch body. Then retain a `ShooterCleared` flag and allow a returning pebble to hit the owner. Adapt the shared release-ignore helper for this clearance rule without changing rock timing. Do not retain a timed grace interval that prevents a valid quick return hit. Downed player ragdolls remain solid targets but receive no damage.

### 7. Reuse rock player-hit detection and damage application

Use the existing rock detection authority and contact timing. The local victim detects damage against locally simulated or presented projectile motion. The simulator still decides physical bounce, impact ordinal, and removal; a local damage detection does not change those decisions. As with rocks, clients may differ about an individual player contact. Do not add confirmation messages to force the victim's damage result to match the simulator's contact list.

Extract `WorldItem.SamplePlayerContact`, `SweepRemoteContacts`, `SweepContact`, and their required sampling state into `ItemPlayerContact`. Both `WorldItem` and `PebbleProjectile` use that helper with their own motion/shape data and damage policy. Keep `PlayerItemHitbox.SweepSphere`, correction-offset compensation, per-physics-step segments, and presentation rebasing. The existing host-simulator collision shortcut feeds the same helper for the host's own player. Preserve the current branch conditions so collision callbacks and sweeps do not independently damage the same victim.

The helper takes explicit active-shot eligibility and cached sphere geometry. Pebble observer colliders are disabled to prevent impulses, so their damage sweeps must not depend on an enabled physical collider; retain the existing collider eligibility check in the rock caller. Reuse the current local-victim selection in `WorldItemRegistry.LateUpdate` and sample pebbles through the same helper after their motion presentation.

Use the existing `PlayerItemHitbox.Damage`/`PlayerHealth.ApplyDamage` path with zero pebble velocity change. This retains owner checks, replay suppression, damage notifications, downing, and health replication. Keep rock speed thresholds unchanged. The pebble policy supplies its configured fixed damage without a speed threshold and never queues a shove. Both callers share the rock contact and repeat-hit behavior, so a later eligible contact can damage the same player again.

Keep the rock detector's existing touching-player, damaged-player/generation, separation, and rebase handling in the shared helper. It prevents duplicate handling of one contact while allowing subsequent eligible contacts. Do not add a once-per-shot flag, a damaged-player set, or player targets to `BirdHitReport`.

Before applying a bounce boundary or terminal record that resets motion samples, finish the shared detector's pending valid segment. Preserve path breaks and correction rebasing: never replace a bent trajectory with one long straight sweep from an old visible position to the terminal point. A terminal record caps the path used for damage and prevents further extrapolation. Complete this local sampling before clearing/returning the contact state; if receipt occurs during reconciliation, defer that final sampling/cleanup to the next normal sampling phase, following the existing replay exclusions. There is no new resolved-damage delivery queue.

Late joins seed contact sampling at the received live baseline, as the current rock rebase does; they do not replay earlier trajectory segments. The shooter becomes an eligible local victim after launch clearance through the same helper. Rejection, terminal removal, and world shutdown retire pending detection state so stale packets cannot revive a finished shot.

Replicate one ordered physical impact transition per episode through the existing reliable trajectory-boundary flow. Add only impact ordinal, resulting motion/path, resolved contact position/normal, and terminal reason where necessary. These events synchronize bounce/removal/VFX and delimit valid motion; they do not direct player damage.

### 8. Bird rules and scoring

Keep `WorldItem.Birds.cs`'s rock-specific rebound behavior unchanged. Pebbles use the same extracted contact modifier and bird physical hit shapes, selecting their uniform rebound policy instead of the rock lift/underside policy.

Adapt the existing source acceptance/completion and resolved-contact entry points in `BirdRegistry` so both rocks and pebbles can call them. Reuse `BirdHitReporter.RockContact`, kill/scare threshold, species overrides, predicted death, `BirdRewardLedger`, and reward delivery. Factor the source data needed by those entry points into a small shared value rather than fabricating an inventory `ItemRecord` for ammunition. Rename a rock-specific shared entry point only where it now actually serves both callers.

- Accept the bird reward source when the projectile spawn is accepted, using its globally unique runtime ID, shooter bird-player token, and shot sequence.
- For a predicted hit before spawn acceptance, show the normal local bird prediction and retain the report; associate/submit it when the projectile ID is accepted. Rejection resolves that prediction through the existing hit-result path.
- Both direct and ricochet hits use incoming impact speed and contribute to the same shot's kill count and multi-kill bonus.
- Complete the reward source only after all ordered terminal-contact outcomes are submitted. Preserve the ledger's short completion grace for in-flight reports; prune it normally afterward.
- Reuse the existing rock near-projectile scare sampling cadence and species rules through the shared source entry point; exclude dead bird lives and never transfer momentum into `BirdBody`.

Bird hit reports and projectile impact transitions must have a single submission point. Include the resolved bird result/report in the reliable contact flow so retry, host handling, and handoff cannot independently score the same hit twice.

### 9. Network cadence, ordering, late join, and handoff

Use the current item snapshot cadence (15–20 Hz setting, currently 20) and batch-size convention. No per-frame transform RPC or per-frame band replication is added.

| Message | Delivery and meaning |
| --- | --- |
| Spawn request | Reliable; pending shot key, source/tuning identity, original fire time, initial motion, and predicted action snapshot |
| Spawn result/live spawn | Reliable; accepted projectile ID or rejection, simulator, initial state, and original expiry; associate the existing predicted instance |
| Motion batch | Unreliable between boundaries; shared packed `ItemMotion` format and world epoch |
| Impact transition | Reliable; ordered physical impact count, resulting boundary motion, and resolved contact; existing bird-hit reporting handles bird outcomes |
| Removal transition | Reliable; reason; second-impact removal includes final contact; other removals omit impact VFX |
| Live baseline/handoff | Reliable; full current physical record, including impact count, original expiry, and clearance/contact state; local player sampling uses existing rock rebase rules |
| Player health changes | Existing victim-owner health replication after the shared rock detector applies damage; no player-hit delivery message |

Apply identity, epoch, revision, sequence, and path ordering consistently:

- Ordinary snapshots may advance motion but never mark an impact ordinal or effect as processed; local player contacts use the shared rock bookkeeping.
- An impact message that arrives after a newer motion sample still applies its previously unseen physical event exactly once; it must not rewind presentation or replay an already sampled player-contact segment.
- A second impact can follow the first within one physics step. Send both ordered transitions, preserving the trajectory segments needed by the existing player detector instead of collapsing the entire path to one final point.
- Reliable spawn/impact/removal events are the only messages allowed to create or retire a live projectile. Late unreliable motion cannot recreate a retired shot.
- Host simulation, predicted owner simulation, and echoed reliable events share the same ordinal/effect deduplication. The shooter sees its local bounce/dirt immediately and does not play them again on confirmation.
- Keep bounded early-motion buffering only for a known pending spawn/baseline or higher known handoff revision. Do not create a permanent dictionary entry for every unknown late motion packet.

Add active projectile records to the existing world baseline sequence before its completion notification. A late join receives only live shots with current impact state and remaining lifetime; it does not replay old hits, rewards, dirt bursts, or charging starts. Apply a newer terminal event over an older baseline entry. Pebble baselines do not enter the item lifecycle table.

On simulator disconnect, transfer each remaining live record to server simulation. Increment motion revision and publish the full handoff record reliably. Start from the latest accepted position/velocity and restore impact count, original expiry, shooter-clearance state, and suppression for an unresolved physical contact episode. Never grant another rebound or replay accepted bird hits. Each victim uses the existing rock contact rebase rules to avoid replaying the old contact while allowing later eligible contacts. Observers continue following motion and checking local player hits; they never choose a rebound or terminal impact.

Simulator-local physical outcomes that were never delivered before a disconnect cannot be reconstructed by the server; handoff continues from the latest accepted state, matching the existing rock model. Preserve every accepted physical boundary and the existing bird-report sequencing. Player damage continues through the existing independent victim-owner health path.

### 10. Termination and reset

Second impact completes eligible bird reporting, ends the physical shot, and plays one dirt burst at the transmitted surface point, oriented to the transmitted normal. Each client finishes its valid local player-contact sampling before retiring detection state. Use a separately pooled particle object so returning the pebble does not erase the burst. Lifetime, kill-volume, rejection, world shutdown, and out-of-bounds cleanup do not create dirt.

On return, clear velocity/angular velocity, simulator/owner/source identity, prediction association, motion revision/history/samples/correction offsets, impact ordinal, instance contact state, shooter ignore state, launch/expiry times, pending boundaries, and visible state. Finish pending valid local contact sampling before clearing its bookkeeping. Clear particle state on VFX reuse. Reset before making an instance available to a later shot.

Remove terminal records from the active projectile table and baseline source. Retain only expiring or bounded metadata needed for pending spawn results, physical event/effect deduplication, and late packet rejection. A per-source accepted sequence watermark can replace historical spawn records; do not retain every terminal `ItemRecord` or every shot ID indefinitely. Clear outstanding buffers and pools on `EndWorld`.

## Assets and scene authoring

| Asset | Required content |
| --- | --- |
| `Assets/Game/ScriptableObjects/Items/Slingshot.asset` | `SlingshotDefinition`, unique item ID (8 is available in the presented registry), name/icon, nonstacking settings, tuning above, frame grip and local/remote pose data, pebble/VFX references |
| `Assets/Game/Prefabs/Items/Slingshot.prefab` | Root `Rigidbody`, `OfflineRigidbody`, `WorldItem`, `BakedPickup`, `SlingshotItemUse`, and `SlingshotPresentation`; pickup/drop colliders; assigned `VisualRoot`; temporary Y frame, fork/draw anchors, two band segments, collider-free loaded pebble; no `ThrowableItemUse` |
| `Assets/Game/Prefabs/Projectiles/Pebble.prefab` | `PebbleProjectile`, Rigidbody, `OfflineRigidbody`, explicit visual root, temporary sphere mesh and SphereCollider; shared no-impulse contact registration with continuous collision detection on the simulator, disabled contact collider on observers |
| `Assets/Game/Prefabs/Effects/PebbleDirt.prefab` | Small one-shot dirt burst, no gameplay collider, pool return on completion |
| `Assets/Game/ScriptableObjects/ItemRegistry.asset` | Append the slingshot definition; do not register ammunition |
| `Assets/Scenes/Game.unity` | One starting-area slingshot prefab instance with `BakedPickup.Item` assigned and a unique baked ID |

Use the existing item-authoring conventions and `Two Birds/Bake World` workflow for the pickup. `ItemSetup.CreateDefinition` currently creates only base items or potions, so create the slingshot subclass asset directly; reuse its prefab layout/conventions without first creating an unnecessary second base definition. The existing `ItemDefinitionEditor` enumerates derived fields; extend it only if required to present slingshot tuning without misleading duplicate launch controls.

No Player or SessionRoot prefab component is required by this design: player action/hand behavior integrates into existing components, and the existing registry owns the projectile service. New runtime presentation targets follow the current hand-target lifetime. Reuse existing item/contact layer conventions and collision exclusions; adjust only the filters needed to ignore other pebbles and suppress observer contacts.

## Execution order and deliverables

1. **Definition and eligibility:** add `SlingshotDefinition`, fixed ingredient eligibility, all direct/physical cauldron gates, and the shared release-velocity lookup. Deliver one source of tuning and no inventory consumption path for ammunition.
2. **Shared foundations:** reuse/extract rock motion sample/correction/serialization, no-impulse contact/bounce handling, victim-local player detection, bird reporting, and instance-pool mechanics; adapt existing callers; share the runtime ID allocator. Deliver concrete shared code used by rocks and pebbles, with existing rock behavior preserved.
3. **Projectile service:** implement live records, prediction association, spawn/result messages, motion relay, active baselines, world shutdown, disconnect takeover, and expiry. Connect the service to the existing registry callbacks in the required order.
4. **Contacts and outcomes:** extend shared rock contacts with grouping/separation, the pebble rebound/terminal policy, and geometric shooter clearance. Plug pebble fixed damage into the existing victim-local player detector with its existing repeat-hit rules, and reuse the bird source/reward path. Include final-segment sampling, pooled dirt, and silent non-impact removal.
5. **Use and recovery:** implement `SlingshotItemUse`, the equipment firing transaction, replicated recovery acceptance, and presentation-specific recovery handling. Keep selected-item use gated while allowing selection/drop.
6. **Hands and aiming:** implement frame centering, independent draw timing, both hand targets, procedural band snap/settle, cancellation, committed pebble sampling, clearance, crosshair aiming, and remote visual departure correction.
7. **Authoring:** create and wire the definition/prefabs/materials, append the registry entry, and place/bake the single starting pickup. Include these assets in the feature delivery; code alone is insufficient for the acceptance scenarios.
8. **Manual acceptance:** the user exercises the scenarios below against the authored Game scene with two clients, including both host-shooter and client-shooter roles.

## Manual acceptance

| Scenario | Expected visible/gameplay result |
| --- | --- |
| Pick up, store, select, unequip, drop, and recollect | One slingshot per slot; frame persists through firing; no ammunition entries or collectible pebbles |
| Immediate tap, partial release, full charge, prolonged full hold | Immediate release at the current loaded-pebble position; increasing speed up to 28 m/s; visible gravity; no automatic full-charge shot |
| Observe both clients during charge/release/cancel | Right hand grips frame; frame centers in about 0.15 s; left hand, pebble, and band center agree through the one-second draw; release snap/settle and about 0.2 s return; no projectile-following hand |
| Fire then press repeatedly or hold an ignored press | No second draw inside 0.5 s; holding through expiry does nothing until a fresh press |
| Fire then switch, unequip, drop, or select/use a potion | Selection/drop work; original recovery ends at its original deadline; newly selected items cannot be used early |
| Cancel by switching, dropping, opening an input context, losing permissions, or losing the item | Loaded pebble disappears and pose returns; no shot, snap, or new cooldown; permitted movement remains unchanged |
| Walk, sprint, jump, and fire as a cart passenger | Responsive use with inherited movement/cart velocity; driving, carried/downed states, and other existing restrictions still apply |
| Charge and tap near walls/corners | Frame/hand correction precedes launch; impossible releases cancel without recovery; no through-wall origins; a valid immediate nearby hit produces the normal first bounce |
| Aim at close objects and at an empty sky | Shot starts at the pebble and aims toward the crosshair target/view direction; gravity causes natural drop, without automatic compensation |
| Bounce off a floor and return to that floor | Exactly one rebound, then one dirt burst/removal at the same resolved second contact on both clients |
| Hit an edge/corner or a nearby second surface | Multiple simultaneous contact points consume one impact; a genuinely later second contact still terminates, including within one physics step |
| Hit one player twice, or two different players | Each eligible contact deals the configured 10 health; the same player can be damaged again under the rock repeat-hit rules; no speed threshold or knockback |
| Compare rock and pebble player contacts with both host and client victims | Same local collision/sweep approach and health response; pebble damage follows victim-side presentation without a second shooter damage report |
| Return a shot into its shooter; down a player at low health | Shooter becomes hittable after initial clearance; damage can reach zero and down normally; already downed bodies receive no further damage or impulse |
| Hit birds directly and after rebound | Existing speed/species kill/scare rules and rewards, including applicable multi-hit scoring; no upward pebble boost or corpse shove |
| Hit carts, loose items, props, downed bodies, and the cauldron exterior | Impact stages occur without moving the target or triggering a pebble-caused cart crash; observer pebbles also exert no force |
| Try cauldron insertion and drop/shoot into intake | Frame is rejected in direct and physical intake; pebbles never become ingredients; ordinary ingredient recipes continue working |
| Fire into open space or a kill volume | Gravity continues; shot disappears by original 30-second deadline or on kill-volume entry; no empty-space dirt burst |
| Join while a shot is in flight, before and after its rebound | Only the live shot appears with the correct remaining impact state; no historical dirt/damage/rewards |
| Disconnect the shooter while shots are live | Server continues from accepted motion with remaining lifetime and one-bounce limit; rock contact rebasing avoids replaying old contacts and allows later eligible hits |
| Repeat firing through many pool reuses | No stale trail/offset, ownership, bounce count, self-ignore, player-contact state, or particle playback leaks into later shots |
| Use rocks, potions, and ordinary pickups afterward | Existing charge, throw follow-through, pickup/drop, rock bird reactions, health effects, and crafting behavior remain intact |
