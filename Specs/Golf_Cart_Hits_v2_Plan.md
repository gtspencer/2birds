# Golf cart hits v2: shared predicted physics

## Objective and decisions

Make players and golf carts solid to one another, let both bodies affect each other, and let carts collide naturally with other carts. Slow cart contact should push pedestrians along the ground. Fast contact should visibly launch them, with strength related to closing speed. Driving and walking must respond immediately on the controlling client.

Use Unity's Rigidbody solver for physical contact and FishNet prediction for synchronization. Migrate carts from driver-authored transform snapshots to replicated driving input and reconciled physics state. The host/server supplies the common state baseline; clients predict immediately instead of waiting for that baseline. This is a consistency mechanism, not an anti-cheat project.

Cart collisions use shared physics and reconciliation. Input ownership follows driver assignment.

Assumptions and scope:

- Players may move a cart with sustained effort, but ordinary walking must not supply engine-like force. Retain approximately 80 kg player mass and 450 kg cart mass as the starting point.
- Occupied and empty carts use the same physical collision rules. Driver ownership controls input routing, not whether other copies simulate.
- Include cart/cart collisions and interactions involving multiple bodies in this implementation.
- Retain existing driving, suspension, handbrake, seating, horn, lights, paint, bird interactions, crash ejection, recovery, and exit placement.
- The user will make the steering-wheel horn collider a trigger. Keep it as a query-only interaction target following the visual steering wheel; it must not contribute physical contact or gameplay trigger effects.
- Shared predicted contact covers players and carts. World items retain their existing simulation model; fully coupled cart/item prediction is outside this migration. Document the resulting limitations and include cart/item interactions in user visual verification.
- Keep the upright player capsule. Ragdolls, damage, new animations, and standing on carts as a supported moving-platform feature are outside scope.
- Predict all observed carts initially. Do not add distance-based physics ownership or a custom rollback framework.
- All source paths below are relative to the repository root. Follow `AGENTS.md`, use Unity CLI when possible and Unity MCP as fallback, and let Unity create `.meta` files. Do not run builds, play sessions, profiling, or automated validation unless the user explicitly requests them.
- During implementation, create `Golf_Cart_Hits_Implementation.md` at the repository root to document the chosen behavior, possible tradeoffs, and concrete tuning points for follow-up after user visual verification. Follow section 15; this is a decision and tuning reference, not a work log.

## Repository map

| Area | Files and relevant behavior |
| --- | --- |
| Cart physics | `Assets/Game/Runtime/Vehicles/GolfCartController.cs`: four suspension raycasts, force-at-position tire model, parking, pre-impact velocity, crash severity, rollover/stuck timers, recovery queries. |
| Cart networking | `Assets/Game/Runtime/Vehicles/GolfCartNetwork.cs`: driver-owned simulator, `Freeze`, motion epochs, stop/ack/resume handoff, seat transactions, reliable incidents, cosmetic RPCs. This is the primary migration target. |
| Cart visuals/settings | `GolfCartPresentation.cs`, `GolfCartSettings.cs`, `CartSeat.cs`, `SteeringWheelHorn.cs` in the same directory; `Assets/Game/Settings/GolfCartSettings.asset`. |
| Player physics | `Assets/Game/Runtime/Player/PlayerMotor.cs`: `MoveInput` including sprint, owner-side stamina processing, `MotorState`, `PredictionRigidbody`, world-impact request/history protocol, recovery, seating/reset barriers. |
| Player seating/visuals | `PlayerSeating.cs`: seat transitions, exit queries, `SampleCartContacts`, `CartOverlap`; `PlayerPresentation.cs`: graphics smoother and camera; `PlayerItemHitbox.cs`: existing item impact protocol consumers. |
| Interaction targeting | `Assets/Game/Runtime/Player/PlayerInteraction.cs`: solid raycast ignores triggers; separate trigger queries currently accept only `CartSeat`. Add targeted horn-trigger selection while preserving obstruction handling. |
| World item boundary | `Assets/Game/Runtime/Items/WorldItem.cs`: uses `OfflineRigidbody`, makes non-simulating copies kinematic, and excludes GolfCart collisions for sleeping items. Reference for integration limits, not a planned prediction migration. |
| Prefabs/settings | `Assets/Game/Prefabs/GolfCart.prefab`, `Player.prefab`, `SessionRoot.prefab`; `Assets/Game/Settings/GameSettings.asset`; `ProjectSettings/DynamicsManager.asset`, `TagManager.asset`. |
| Session protocol | `Assets/Game/Runtime/Networking/SessionController.cs`, `SessionAuthenticator.cs`; Tugboat and FishySteamworks transports remain unchanged. |
| Other consumers | `Assets/Game/Runtime/Birds/BirdHitReporter.cs`, `BirdRegistry.Presentation.cs`, `BirdRegistry.Events.cs` depend on cart simulation/reporting authority, motion epoch, and seat revision. |
| FishNet implementation | `Assets/FishNet/Runtime/Managing/Prediction/PredictionManager.cs`, `StateOrder.cs`; `Managing/Timing/TimeManager.cs`; `Object/NetworkObject/NetworkObject.Prediction.cs`; `Object/NetworkBehaviour/NetworkBehaviour.Prediction.cs`; `Object/Prediction/PredictionRigidbody.cs`; `Generated/Component/Prediction/RigidbodyState.cs`, `NetworkCollider.cs`. |
| Existing motion/smoothing | `Assets/FishNet/Runtime/Generated/Component/NetworkTransform/NetworkTransform.Motion.cs`; `Generated/Component/Prediction/OfflineRigidbody.cs`; `Generated/Component/TickSmoothing/NetworkTickSmoother.cs`. |

The project uses Unity 6000.5.7f1, FishNet 4.7.3, 60 Hz TimeManager physics, `Inserted` prediction state order (`_stateOrder: 0`), prediction state interpolation of two ticks, and an eight-player session limit. Local reconcile history is enabled and reconcile throttling by frame rate is disabled. Player state forwarding is enabled. The cart currently has prediction disabled, an `OfflineRigidbody`, and a customized `NetworkTransform` motion stream, normally publishing every third tick. Remote carts are kinematic. The player limit does not cap the number of observed carts.

The cart Rigidbody exclusion value `5184` includes Player (64), PlayerItemHitbox (1024), and CartSeat (4096). `GolfCartController.Awake` now adds only PlayerItemHitbox and CartSeat with `|=`; it does not clear the Player bit inherited from the prefab. The project layer matrix already permits Player/GolfCart and GolfCart/GolfCart contact. The existing pedestrian sampler requests occasional impulses but never resolves penetration. Its touching latch also skips subsequent impacts while contact continues.

`GolfCart.prefab` has a solid windshield `BoxCollider` under `Art`, outside the controller's four-entry `chassis` array. Include it in the physical hierarchy migration and the solid geometry used for contact separation and recovery clearance. The cart instance in `Assets/Scenes/Game.unity` currently has no component or collision-mask overrides obstructing source-prefab migration. Preserve current seat/horn tooltip overrides and text visibility fields, headlight material switching, and their serialized references.

## 1. Simulation and authority contract

Every active, observed cart participates in the same Unity physics scene and FishNet replay cycle as predicted players. A remote cart is a dynamic predicted body, not an interpolated kinematic obstacle. A parked cart can sleep but remains dynamic and can wake from contact.

| Participant | Driving input | Physics | Shared state |
| --- | --- | --- | --- |
| Driver client | Samples local controls and predicts them immediately | Simulates its cart and observed interacting bodies | Receives reconciles |
| Host/server | Consumes driver input; creates neutral input for ownerless carts | Simulates all relevant carts/players together | Publishes reconcile state and commits seat/recovery changes |
| Other clients | Consume forwarded input; use a bounded missing-input policy | Simulate observed carts, including replay | Receive the same state baseline |

Unity resolves ordinary player/cart and cart/cart contact impulses on each simulation copy. Reconciliation corrects prediction differences. Do not send those impulses as separate events and then apply them again.

Local prediction cannot know another client's future steering. Under latency, some corrections remain inevitable. Do not promise identical contact timing on every screen or deterministic PhysX results across machines.

World items do not participate in this shared replay contract. An active item can be dynamic on its simulator, kinematic on other copies, and paused during cart replay by `OfflineRigidbody`; sleeping items exclude cart contact. Preserve the existing item behavior and accept that cart/item contact may require visible reconciliation. Do not promise consistent two-way cart/item momentum transfer or silently migrate item networking. Record this boundary in `Golf_Cart_Hits_Implementation.md` for assessment during user visual verification.

## 2. Cart prediction data and ownership

Implement the cart's `[Replicate]` and `[Reconcile]` methods in the existing `GolfCartNetwork` component, using a partial file such as `GolfCartNetwork.Prediction.cs` if useful. No additional cart simulation component is needed. Follow `PlayerMotor`'s tick lifecycle or subscribe explicitly; ensure tick callbacks are configured if changing to `TickNetworkBehaviour`.

Use a compact `CartInput : IReplicateData`:

- Steering and throttle, initially packed signed bytes normalized to [-1, 1].
- Handbrake flag.
- Cart control generation/epoch and driver seat revision needed to reject stale controls.
- FishNet-managed replicate tick. Do not manually replace it with another client's tick.

Create input only on the driver owner, or on the server when the cart is ownerless. Other copies call the replicate method with default input so FishNet can consume forwarded/history input. Preserve steering/braking responsiveness locally after an accepted seat transition.

For created input, validate generation and driver assignment before applying controls. For missing/default input, do not treat a zero generation as a reason to skip physics. Hold the last valid continuous controls for at most a short fixed interval, initially three ticks, then use neutral throttle/steering and release handbrake. Reconcile the held controls and their age. Never extrapolate seat changes, toggles, ejections, or other one-shot actions. Ownerless carts always use neutral controls plus their passive parking/braking behavior.

Keep the current `Inserted` state order initially. In the checked-in FishNet implementation, spectators receive default input during forward simulation and may receive defaults through much of replay; this is normal prediction, not evidence of packet loss. Age held controls by simulated steps consistently across forward and replay, restoring their age on reconcile. Three ticks is a 50 ms initial tuning hypothesis: a longer prediction interval can cause remote carts to neutralize steering/throttle before correction. Document the responsiveness/correction tradeoff and the hold-interval code location for tuning after user visual verification. Do not switch prediction mode or change global session settings as an incidental fix.

`CartState : IReconcileData` must contain the `PredictionRigidbody`, generation, and every persistent value that affects future physics. Use value snapshots; never store references to a live mutable array/list as historical state.

| State | Treatment |
| --- | --- |
| Position, rotation, velocities, pending explicit forces | Rigidbody reconcile plus explicit pending state described below |
| Last controls and missing-input age | Reconcile |
| Driven/parking state, rear grip | Reconcile; driven state derives from the accepted control generation's driver assignment; restore its corresponding linear damping |
| Rest/sleep decision and any settling countdown that gates forces | Explicit reconcile state; `PredictionRigidbody`'s serialized Rigidbody state does not include sleep |
| `previouslySupported`, `landingGrace`, `stuckOrigin`, `stuckTime` | Keep server-only while used solely for incident decisions; reconcile any value also used to gate predicted forces or sleep |
| `rolloverTime`, `settledTime`, `clearTime`, `rolloverReported`, recovery origin | Keep server-only decision state; clients receive committed recovery transitions |
| Heading | Recompute from physical orientation when defined; preserve/reconcile any retained fallback needed by predicted behavior; recovery-only state may remain server-only |
| Compression, wheel ray hits/origins, pre-impact pose/velocities, per-step collision severity | Recompute every simulation step; do not transmit ray hits or collider references |
| Seat occupancy, request feedback, lights, color | Keep the existing discrete synchronization mechanism |

Prefer server-only crash/recovery timers when they do not affect predicted forces. Separate those decisions from replayed physics instead of transmitting every controller field. If a value also influences force evaluation, parking, or sleep, include it in reconcile state. Only the server commits gameplay transactions.

Use `PredictionRigidbody` for suspension, tire forces, torques, and velocity changes, including force-at-position calls. Its `Simulate()` flushes queued forces; it does not advance the physics scene. Do not apply both old raw Rigidbody forces and new prediction-wrapper forces.

The local `PredictionRigidbody` serializer explicitly does not transmit its queued force list. Any effect queued after physics and intended for the next step must therefore be an explicit field in the relevant reconcile state, not only a pending wrapper force.

## 3. Physics ordering and replay

Use FishNet's existing shared replay. `PredictionManager.ReconcileToStates` restores participating objects, invokes their replicate replay methods, then invokes pre-physics, one shared `Physics.Simulate`, and post-physics for each replayed tick. `OnPostTick` is a forward-tick callback, not a substitute for replay post-physics processing.

Implement this order for forward and replay simulation:

| Phase | Work |
| --- | --- |
| Replicate | Resolve input/generation, restore or consume pending gameplay velocity changes, update controller forces once. Player movement and cart driving both participate. |
| `TimeManager.OnPrePhysicsSimulation` | Reset per-step collision accumulators; capture body poses and pre-contact velocities for hit severity; perform designated reporter's bird pre-sample only on forward steps. |
| Shared physics step | Unity integrates all participating bodies and resolves physical contacts. Collision callbacks gather contact data without sending RPCs or changing ownership. |
| `TimeManager.OnPostPhysicsSimulation` | Aggregate contacts; derive launch pending state, motor contact limits, and reconciled rest decisions. Advance server-only crash/recovery decisions and perform bird post-sample/server transactions only on forward steps. |
| Forward `OnPostTick` | Capture final reconcile snapshots on all peers for server publication/local history. |

If force evaluation remains in a pre-physics hook for ordering reasons, the replicate method must stage input for exactly one step, and that hook must run identically during replay. Use one force evaluation path; do not combine force application in both replicate and pre-physics.

Remove blanket `if (PredictionManager.IsReconciling) return` guards from cart physics and physical contact bookkeeping. Retain such guards only around irreversible effects: RPCs, bird reports, sounds, seat transactions, and user feedback. A replayed launch must affect replayed physics, but must not send a second event.

Physics hooks must skip bodies that FishNet has paused because no valid reconcile state exists, and bodies explicitly rejecting stale generation replay. Do not unpause or force dynamic state on those bodies from a generic callback. During normal replay, valid cart bodies remain simulated. Preserve the player's seated/rejected-replay guards.

Use FishNet's owner/non-owner tick mapping: owned inputs use client ticks, remote inputs use server ticks in replay. Use `ClientReplayTick`/`ServerReplayTick` when identifying replay steps, not current `LocalTick`. Use durations/countdowns for contact recovery where an absolute timestamp is unnecessary.

Keep creating local cart reconcile history even at rest. Otherwise FishNet may pause a cart during another body's replay because it lacks state, turning it into the wrong collision partner. Do not optimize away sleeping-state history in the initial migration.

The local FishNet code also contains a small-change correction to the last remote Rigidbody snapshot. Do not modify that imported behavior speculatively. Persistent contact jitter must first be traced to missing input/state, double simulation, or presentation. The migration should use the existing shared machinery rather than fork it.

## 4. Replace transform authority and simplify handoff

Remove the cart's use of `EnableEpochMotion`, `PublishMotion`, interpolated root transforms, and the `OfflineRigidbody` pauser. Disable/remove the corresponding components from the existing prefab. Remove their `RequireComponent` declarations first so Unity does not re-add them.

Remove `simulator`, `stopToken`, `cancelledToken`, `stoppedMotion`, the `TargetStop`/`ServerStopped`/`TargetResume` exchange, and the freeze/resume behavior whose purpose was transferring the sole physics simulator. Keep request IDs, occupant revisions, blocked-exit handling, and transaction completion semantics.

At a server physics boundary, commit a driver change as one discrete generation transition:

1. Resolve the seat/exit request against the server's current physical cart state.
2. Preserve pose and velocities; change driver ownership for input routing.
3. Increment the cart control generation, clear old input history/held controls, and publish an explicit current-state baseline with the seat transition.
4. Install that baseline on clients, rejecting stale generations and old driver controls. Keep dynamic simulation active; there is no round-trip freeze handshake.
5. Handle ownership callbacks and baseline RPCs arriving in either order. Enable local driving input only when both agree on driver and generation.

Use a generation barrier comparable to `PlayerMotor`'s seating/reset barrier. Reject stale snapshots and replay that would cross an installed baseline. Do not simulate a teleported/new-generation body against historical partners while pretending it belongs to their old step; use FishNet's pausing mechanism for the rejected replay interval.

Recovery teleport and driver disconnect use the same atomic baseline path. A disconnect removes input ownership but does not make a moving cart kinematic or zero its velocity. Late joiners receive current body/controller state, occupancy, generation, recovery, lights, and color. A stale spawn baseline must not overwrite a newer reconcile/transition; carry generation and state tick for ordering.

Continue exposing `Epoch` for bird report identity, with its meaning documented as the cart generation. Keep normal seat `StateRevision` separate. Preserve exit point velocity from physical linear/angular velocity, and preserve pre-impact rider velocity for ejections.

## 5. Restore physical contact and finite parking

Enable Player/GolfCart and GolfCart/GolfCart physical contacts. The current project layer matrix already permits both; preserve it and inspect per-body/per-collider overrides. Clear only the Player exclusion on the cart, retaining PlayerItemHitbox and CartSeat exclusions. With the current prefab values, the remaining exclusion is 5120. Also clear the Player bit in initialization so existing scene-instance overrides cannot silently preserve it.

Keep the player capsule and cart chassis solid. Do not include the duplicate item hitbox in cart contact; it would apply extra impulses. The root bodies drive simulation, with Rigidbody interpolation disabled when graphical tick smoothing is used. Keep a suitable existing CCD configuration initially; adjust only for an identified tunneling case, without changing global solver settings speculatively.

The parking code currently sets both velocities to zero and calls `Sleep()` each step while holding. Replace this hard lock with finite wheel braking. A body awakened by collision must be allowed to move even when the parking flag remains set. Let sleep occur only when stable, with no drive input or pending gameplay force; do not repeatedly erase externally acquired motion.

Provide an explicit idle path. Once a stable cart sleeps, skip suspension/tire force evaluation while it remains asleep with no driving input or pending gameplay force; nonzero suspension forces would otherwise wake it every step. Resume evaluation when input, a pending force, or physical contact wakes the body. Keep required snapshots and contact bookkeeping active. Capture the rest/sleep decision and any force-gating countdown explicitly, restore them after the Rigidbody reconcile, and apply the same rules during replay. Never use the parking flag alone to put a newly awakened body back to sleep or override FishNet's replay pauser.

Use a distinct, modest parking resistance instead of the full 4200 N driving brake if parked carts are to be nudged by pedestrians. Initial tuning target: roughly 150 N total longitudinal parking resistance versus a player's approximately 240 N contact-limited effort. Keep tire grip/lateral resistance and stronger active handbrake behavior, so pushing sideways or uphill can remain harder. This is a starting design value, not a guarantee against every slope. Add only the parking-force setting needed for this behavior.

Retain the asset's actual driven/unmanned linear damping of 0.4/0.75 initially; these differ from the C# defaults. A simplified flat-ground estimate with 240 N pushing effort, 150 N resistance, and 450 kg mass gives about 0.27 m/s sustained movement under unmanned damping. The 150 N resistance alone cannot hold that mass on a longitudinal slope much above roughly 2 degrees. These are tuning estimates, not measured results. Document the pushability/slope-holding tradeoff and use user visual verification before changing these values.

## 6. Player movement that permits pushing

Retain normal walking acceleration away from carts. In contact, prevent the motor from acting like a powerful brake or engine against the cart.

Correct `PlayerMotor.ReplicateMove`'s input revision gate as part of this change. It currently rejects mismatched `SeatingRevision` before processing pending effects, and FishNet-generated default input carries revision zero. After a seat transition this can skip motor processing on normal spectator/replay steps. Validate the input revision when `state.ContainsCreated()` is true; default steps must still process current-generation physics, pending lift, contacts, and recovery. Setting a revision on the argument passed from `TimeManager_OnTick` does not populate FishNet's internally generated defaults. Preserve the separate seated/placement-pending and stale-reconcile/reset/generation barriers.

Preserve `MoveInput.Sprint` and owner-side `UpdateStamina` processing. Do not move stamina consumption into replay or remove sprint while changing the force calculation.

`GroundAcceleration`/`Braking` currently reach 25 m/s²; for the 80 kg player this can supply roughly 2000 N. Reducing cart mass or simply enabling contacts would not solve the motor's continuous resistance.

Maintain a small per-player set of active cart contact normals using physics callbacks, keyed by the cart Rigidbody/network identity, not by individual chassis collider. Orient each normal outward from cart toward player. Cache the motor/cart association at initialization/registration; do not call `GetComponent` for every contact point.

When constructing the next movement force:

- Preserve tangential walking and walking away from the cart.
- Limit intentional acceleration into a cart to `CartPushAcceleration`, initially 3 m/s² (about 240 N for the current player).
- Suppress automatic braking that would cancel a moving cart's outward push when there is no opposing input. Intentional opposing input still receives only the bounded pushing effort.
- Evaluate multiple independent contact directions in a stable order; do not multiply the push allowance by the number of chassis boxes.
- Suspend normal motor braking during launch recovery as described below.

Contact normals/identities that affect the next movement tick are simulation state. Include an immutable compact snapshot in `MotorState`, or derive them from explicitly restored same-tick contact data before using them. Do not rely on a non-rewound live dictionary. Store locally derived contact state in reconcile data; keep `MoveInput` limited to player controls and the seating revision.

The first contact tick can use the ordinary motor limit; subsequent touching ticks must use the bounded rule. Do not add a global cart search just to preempt that first tick.

## 7. Launch enhancement built on physical collisions

Unity supplies blocking, continuous pushing, horizontal momentum transfer, and the cart's physical reaction. Add only a deliberate upward launch enhancement for sufficiently fast pedestrian side impacts. Apply the enhancement once and let Unity determine the horizontal collision response.

Own pedestrian hit aggregation in `PlayerMotor` (a `PlayerMotor.CartContacts.cs` partial is appropriate). Cart callbacks continue gathering crash severity. Do not have both components independently add pedestrian lift.

Before each physics step, capture cart linear/angular velocity and world center of mass, plus player velocity. Queueing `AddForce` does not necessarily change the velocity read before Unity integrates it; consistently use pre-step velocities as the severity estimate, rather than depending on callback order or post-solver velocity. Keep this same definition on forward and replay steps.

For each capsule/chassis contact, use its actual point and horizontal normal pointing into the player:

```text
cartPointVelocity = cartLinearVelocity + cross(cartAngularVelocity, point - cartCenterOfMass)
cartApproach = max(0, dot(cartPointVelocityHorizontal, normalHorizontal))
relativeApproach = max(0, dot(cartPointVelocityHorizontal - playerVelocityHorizontal, normalHorizontal))
severity = min(cartApproach, relativeApproach)
blend = smoothstep(LaunchSpeed, 2 * LaunchSpeed, severity)
targetUpwardSpeed = LaunchLift * severity * blend
pendingLift = max(0, targetUpwardSpeed - playerPostPhysicsVerticalVelocity)
```

Start with `LaunchSpeed = 3 m/s` and `LaunchLift = 0.4`. Below the threshold, physical pushing continues with no artificial lift. A stationary cart has zero approach speed, so a player running into it is blocked rather than launched. A glancing hit or a player already moving with the cart produces a smaller response.

Ignore predominantly vertical contacts for launch eligibility; retain their normal physical response. Aggregate all boxes/contacts for one cart/player pair and select the strongest eligible contact, not the sum. For simultaneous contacts from several carts, use the strongest upward target for the step; Unity still resolves all physical horizontal reactions.

At 8 m/s head-on closing speed against a stationary player, the initial target is 3.2 m/s upward, approximately 0.52 m ideal rise. At 15 m/s it is 6 m/s, approximately 1.83 m. Horizontal travel follows actual mass, contact geometry, tires, and locomotion recovery rather than a promised fixed multiplier.

Store `pendingCartLift`, cart-launch recovery duration, and launch/contact bookkeeping explicitly in `MotorState` after post-physics. Consume the pending lift once during the next replicate through `PredictionRigidbody`; then clear it. This gives a one-tick local response delay at most and makes the pending effect survive reconciliation correctly.

Apply/recompute the enhancement in normal simulation and replay on every participating copy. **Do not call `SubmitWorldImpact` or `QueueWorldImpact` for this cart-contact enhancement.** Those APIs intentionally reject replay and would introduce a second authoritative application path. Preserve them for existing item impacts and seat ejections.

This design adds gameplay lift without an equal artificial downward impulse on the cart. Ordinary physical collision remains two-way; the enhancement need not conserve energy. Avoid adding an extra horizontal cart reaction for an impulse Unity already solved.

## 8. Contact episodes, recovery, and replay safety

Use separate concepts for touching and already launched. A slow contact may later become a launch if a substantial new closing impact occurs. Once launched, the pair stays latched until actual separation; multiple boxes and `OnCollisionStay` must not generate repeated lift.

Track generation-safe cart identities and a compact contact episode state in the player's reconcile snapshot. Reconcile the launch latch, contact normal needed by movement, pending lift, and recovery fields. Unity's internal contact cache is not your gameplay history: a replay can produce Enter where the forward step produced Stay. An Enter callback by itself must never bypass the restored latch.

Use collision callbacks to gather solver contact points and update pairs, but do not rely on native Enter/Exit to define gameplay episodes. Those callbacks can be missing during prediction; FishNet's [Network Collider guidance](https://fish-networking.gitbook.io/docs/fishnet-building-blocks/components/prediction/network-collider) explains this limitation. Its helper supplies collider identities, not the solver contact points/impulses required here, so simply adding it does not replace contact aggregation.

Do not infer separation solely from one step without Stay: sleeping bodies can omit Stay callbacks, as documented by [Unity](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Rigidbody.OnCollisionStay.html). After state restoration/physics transform synchronization, rebuild relevant nearby pairs with a bounded local query even when the restored set is empty, then confirm geometry before letting a pair limit movement or rearm a launch. For retained pairs, confirm separation using all solid cart colliders, including the windshield, and a separation tolerance. Exit from one box must not clear the cart/player pair while another box remains touching. Exclude the horn trigger and seat triggers. Limit checks to retained pairs and local restoration queries; do not add a player-by-all-carts polling loop or generate launch solely from overlap reconstruction.

Use a small separation tolerance, initially 2–5 cm, to prevent launch rearming from tiny contact gaps. Ignore callbacks caused by disabling a seated capsule as physical hit events. Clear contact state on despawn, player reset, seating/placement transition, and incompatible cart generation changes. On generation changes during overlap, establish a contact baseline without generating an artificial impact from the discontinuity.

Use separate cart recovery from the existing general world-impact timer so item hits and seat ejections keep their 0.2-second behavior. Initial cart recovery duration is `clamp(2 * targetUpwardSpeed / gravityMagnitude, 0.35, 1.2)` seconds. Suppress horizontal motor braking during it. End early only after actual takeoff followed by grounded landing; do not cancel recovery because the launch was queued while the capsule still touched the ground. Include the takeoff flag and countdown in reconcile state.

Keep the existing item impact IDs/generations, `BeforeOwnerMove`, `PresentationCorrected`, and item hitbox batching. Remove only the old cart sampling subscription and cart-generated use of the impact protocol.

## 9. Cart crashes, seat transitions, and external effects

Cart/cart contacts naturally affect both dynamic bodies. Retain `collision.impulse / cartMass` crash severity for ejection, aggregated once per collision contribution. Continue excluding pedestrian contact from crash severity so nudging a person does not empty the cart.

Replay physics updates physical contact state and any reconciled controller values that affect future forces. Crash/recovery timers used only for server decisions advance on forward server steps and are not transmitted or replayed on clients. Only the forward server step commits crash ejection, recovery state changes, and seat occupancy. Remove the old driver-only `ReportIncident` RPC/ack path if it is superseded by server simulation; do not preserve two incident authorities. Keep local driving and collisions predicted even though a discrete seat ejection is confirmed by the server. An extra predicted seat-ejection subsystem is outside this migration.

Apply seat transactions after physical contact processing at a defined server boundary, preserving body motion. Reject stale input by generation/revision. Seated/placement-pending capsules remain disabled and their player bodies kinematic. Exit uses physical cart point velocity and world clearance queries. Exit grace suppresses the extra launch from the cart just exited until separation; it does not disable normal solidity or ignore other carts.

For bird interactions, replace the overloaded `Simulating` meaning with explicit concepts:

- `SimulatesPhysics`: this body participates locally, including remote prediction.
- `ReportsWorldEffects`: driver owner for driven carts, server for ownerless carts, and forward simulation only.

Apply the reporting predicate to `BirdHitReporter` cart threat scans as well as `CartBefore`/`CartAfter`; changing only one call site would cause every predicted copy to report. Preserve `RememberCart`, `Epoch`, `StateRevision`, and driver reward attribution across transitions. Do not run bird reporting during replay.

Horn, lights, paint, and interaction RPCs retain their existing event semantics. The horn trigger is selected by the interaction query; touching it or replaying trigger overlaps must not honk. Preserve seat/horn tooltip overrides and text visibility settings, headlight material switching, and existing audio/material references. No sound, score, bird death, or seat notification should be replayed as a side effect of correcting physics.

## 10. Prefab migration and presentation

This implementation requires changes to the **existing** `GolfCart.prefab` and attachment of a graphical smoother. Notify the user before performing those implementation steps. No new prefab asset or game-scene redesign is required. Prefer changing the source prefab so scene instances inherit it; inspect only relevant instance overrides if they prevent the migration.

Apply these atomic prefab changes through Unity CLI where supported, then Unity MCP if needed. Do not create a one-time migration editor tool or write metadata manually.

1. Enable prediction on the cart `NetworkObject`, select Rigidbody prediction, and enable state forwarding. Keep disconnect persistence. Leave prediction's NetworkTransform reference unset.
2. Remove the root `NetworkTransform` and `OfflineRigidbody` after their code dependencies are removed. Keep the root Rigidbody and chassis colliders under physics control. Enable dynamic state once the initial baseline is installed.
3. Add one `NetworkTickSmoother` for the visual hierarchy, using `Player.prefab`'s smoother setup as the local reference. Use either that smoother or NetworkObject graphical smoothing, never both. Start with fixed, modest interpolation consistent with player presentation.
4. Use a unit-scale `Graphics` child wrapping the existing `Art` child if needed; `Art` currently has scale 3.4. Preserve the model's scale and all wheel/pedal/paint references. Rigidbody interpolation stays off.
5. Keep every solid collider and suspension origin outside the smoothed hierarchy. Relocate the solid windshield `BoxCollider` from `Art` to an unsmoothed physics child, preserving its world shape and placement under the model's 3.4 scale. Include it in the cached solid geometry used for contact separation and recovery clearance; do not assume the four current chassis references cover every solid collider. Purely visual wheel/steering animation remains under Graphics.
6. Keep the user-converted horn trigger and `SteeringWheelHorn` with the visual steering wheel under Graphics; no horn collider relocation is needed. This query-only trigger is an explicit exception to the solid-collider hierarchy rule and must not affect physical contact, crash severity, or launch bookkeeping. Update `PlayerInteraction` to query and select horn triggers as well as seat triggers, using their existing layers and preserving solid obstruction/distance checks. The current solid raycast ignores triggers and the trigger path accepts only `CartSeat`, so changing `isTrigger` alone would make the horn unselectable. Preserve the horn tooltip anchor and settings.
7. Keep seat triggers and exit geometry under the physical root. Cache rider/eye/tooltip poses relative to the root and transform them through the graphics pose for display. Do not add duplicate networked seat objects just to obtain visual anchors.
8. Update cart/player masks as described above and preserve the existing chassis references while including the windshield in the required solid geometry queries. Keep bird hit shapes at their existing scope unless a requested behavior requires expanding them.

Replace dependence on `NetworkTransform.MotionFrame` with a small game-owned presentation snapshot, or equivalent direct properties, containing only pose, velocities, steering, suspension compression, and brake flags needed by existing consumers. This is local display data, not another transform network stream.

Make simulation/display pose APIs explicit:

- Exit clearance, recovery, collision point velocity, and ejection use the physical cart pose.
- The seated player's graphics, camera, aiming presentation, seat tooltip, wheels, lights, and horn location follow the smoothed graphics pose.
- A seated player's disabled physical body follows a physical seat anchor when needed; its displayed graphics follow the visual seat anchor. Do not feed smoothed seat positions back into active physics.
- Convert root-local poses through the Graphics/root relationship correctly, preserving the Art scale. Refactor `PlayerSeating.AimPose`, `PointVelocity`, `UpdateSeatHeading`, and seated LateUpdate accordingly.

On a teleport/new generation, reset the graphics buffer once at the installed baseline. Ordinary contact uses ongoing smoothing; never reset it every collision tick. Keep wheel spin based on presented travel, excluding reconcile/teleport discontinuities so a correction does not spin wheels wildly.

The customized imported `NetworkTransform.Motion.cs` can remain if other users need it. Remove cart references to it; do not edit or delete unrelated imported networking code as cleanup.

## 11. Settings, performance, and protocol

Update existing assets explicitly; changing C# defaults does not update serialized values.

| Setting | Initial value/purpose |
| --- | --- |
| Golf cart `LaunchSpeed` | 3 m/s; replaces old `MinimumHitSpeed` |
| Golf cart `LaunchLift` | 0.4; replaces old `HitLift` semantics |
| Golf cart parking resistance | Approximately 150 N total longitudinal force, separate from driving brake force |
| Player `CartPushAcceleration` | 3 m/s²; affects only intentional movement into carts |

Remove the old pedestrian `CollisionMultiplier` if no remaining consumer needs it; native physics supplies horizontal transfer. Preserve ejection strength, suspension, ordinary driving/braking, and unrelated GameSettings values. Use concise tooltips. No new settings asset is required.

Use packed steering/throttle and flags; send no contact point stream and no cart collision RPC per tick. Reconcile controller state deliberately: omit recomputable raycast results, graphics state, references, and server-only transactions. Serialize pending launch state explicitly. Bound/reuse contact buffers and dispose pooled history snapshots correctly; never reuse a mutable snapshot while FishNet still owns it.

More carts now simulate on each observing client, including during replay. Four suspension raycasts per awake cart per physics step means eight awake carts cost 32 suspension rays per step, or 1,920 per second at 60 Hz before replay/contact solving. With an average of R additional replay steps per forward tick, the suspension-query estimate is 1,920 × (1 + R) per machine for those eight carts. Include solver work, contact restoration queries, local history/serialization, and observer fan-out separately. The eight-player limit is not a cart-count limit. These estimates are not profiling results.

The main cost scales with observed carts and replayed steps; there is no additional Cartesian player/cart sweep loop. Avoid render-frame simulation work, per-tick LINQ/temporary arrays, or continuous logging. Keep the existing interaction presentation queries. Use the explicit sleeping path in section 5 without skipping required reconcile history. Retain the initial `Inserted` mode and unthrottled reconcile configuration; document their CPU/correction tradeoff instead of changing global settings speculatively.

Keep the existing 60 Hz simulation and initial normal reconcile cadence. The old 20 Hz transform publishing loop disappears; it is not a second stream to preserve alongside prediction. Bandwidth may increase because carts now send predicted state and forwarded inputs. Do not claim equivalence to the old stream or optimize state frequency before the interaction works correctly.

Nonzero generation/revision fields make otherwise neutral `CartInput` non-default to FishNet's resend logic. Budget for continued replicate/reconcile traffic even when carts rest; do not assume physics sleep suppresses networking. Recording local history and transmitting states are separate decisions. Keep the initial safe behavior and document the actual input fields, state contents, cadence, and potential idle-traffic optimization for later user-authorized measurement. Prefer server-only incident timers as described in section 2.

Bump `SessionController.Protocol` for the changed prediction wire format and NetworkBehaviour layout. Preserve local/Steam transport selection and authentication compatibility checks.

## 12. Implementation sequence and boundaries

1. **Define prediction state and migrate cart forces.** Add input/reconcile data to `GolfCartNetwork`, add controller capture/restore methods, replace raw force writes, and establish forward/replay hooks. Explicitly handle `Inserted` mode default input, ownerless carts, and server-only incident decisions.
2. **Replace old synchronization and handoff.** Remove root transform publication and sole-simulator freezing; install atomic generation baselines for ownership, recovery, disconnect, and spawn. Migrate all `DisplayMotion`, `Simulating`, and incident consumers.
3. **Apply the prefab/presentation migration.** Enable prediction, remove old components, add one graphics smoother, move the solid windshield outside graphics, retain the horn as a visual interaction trigger with matching query support, and separate visual/physical seat poses.
4. **Enable two-way contacts and finite parking.** Clear only the necessary masks, replace hard parking velocity resets, implement explicit sleep/wake restoration, and allow dynamic contacts to move carts.
5. **Implement player contact-aware movement and launch.** Fix created/default input revision handling while preserving sprint/stamina. Add bounded motor effort, physics-driven contact aggregation, contact reconstruction after restore, replayed pending lift, episode latches, and reconciled recovery. Remove `SampleCartContacts`, `CartOverlap`, and their obsolete contact rebasing/state from `PlayerSeating` while preserving exit helpers.
6. **Finish integrations and serialized values.** Adapt birds and crash transactions, update settings assets and protocol, and retain item impact/ejection paths.
7. **Document implementation tradeoffs.** Create and maintain `Golf_Cart_Hits_Implementation.md` according to section 15, with actual tuning locations and visual scenarios for follow-up.

Expected file scope:

- Main changes: `GolfCartNetwork.cs`, optional partial prediction file, `GolfCartController.cs`, `GolfCartSettings.cs`, `GolfCartPresentation.cs`, `PlayerMotor.cs`, optional player contact partial, `PlayerSeating.cs`, `CartSeat.cs` as needed for explicit pose access.
- Integration changes: `PlayerPresentation.cs` for seat display only if required, `PlayerInteraction.cs` for horn-trigger selection, `SteeringWheelHorn.cs` only if trigger targeting needs routing changes, bird reporter/registry cart call sites, `GameSettings.cs`, `SessionController.cs`.
- Serialized changes: existing GolfCart prefab and settings assets; Player prefab only if a required collision setting cannot be handled in initialization. SessionRoot prediction settings should not need a speculative change.
- FishNet source: reference implementation, not a planned modification target.
- Documentation: new root-level `Golf_Cart_Hits_Implementation.md` as the implementation decision/tuning reference.
- Read-only integration reference: `WorldItem.cs` and its `OfflineRigidbody` behavior; item prediction remains outside scope.

Preserve subscriptions used by item impacts and camera correction. Remove only imports/fields/methods superseded by this migration. Do not refactor the transport, inventory, bird simulation, or unrelated player systems.

## 13. Failure modes the implementation must address

| Symptom | First implementation checks |
| --- | --- |
| Players still walk through carts | Player exclusion still inherited from prefab/scene override; collider disabled; replay body paused without valid history |
| Cart behaves like an immovable wall on a client | Remote copy still kinematic, OfflineRigidbody still attached, or transform sync still writing root pose |
| Slow cart cannot push an idle player | Motor braking remains active into cart contact; no restored contact state after reconcile |
| Player moves cart unrealistically fast | Contact-normal effort still uses 25 m/s², or force is multiplied per chassis collider |
| Parked cart snaps back after being pushed | Parking still zeros velocity or forces sleep after contact |
| Parked carts never sleep or repeatedly wake during reconcile | Suspension forces still run while resting; sleep decisions are not restored explicitly; parking flag overrides a contact wake |
| Launch doubles or grows every tick | Native impulse added again; duplicate player/cart callback paths; latch missing from reconcile; world-impact RPC also used |
| Launch disappears on reconcile | Pending lift not serialized, post-physics omitted during replay, or recovery/takeoff state not restored |
| Launch/recovery processing stops after entering/exiting a seat | Player revision gate rejects FishNet-generated default input before current-generation motor processing |
| Remote steering repeatedly straightens and corrects | `Inserted` mode defaults exhaust the held-control interval; held-control age is not restored consistently |
| Moving between chassis boxes rearms launch | Exit from one collider clears a whole cart/player pair; windshield omitted from separation geometry |
| Repeated bump on handoff/teleport | Stale input crosses generation; initial overlap treated as a new high-speed hit; graphics history not reset |
| Camera/rider vibrates while cart looks smooth | Seat display still follows unsmoothed physics anchors or graphics and Rigidbody both interpolate |
| Windshield contact jitters while the cart looks smooth | Solid windshield collider remains under Graphics or lost its world shape during relocation |
| Horn cannot be selected after conversion to trigger | Interaction trigger query still accepts only seats or omits the horn's layer |
| Bird rewards/events duplicate | All dynamic cart copies treated as world-effect reporters; replay reaches external event path |
| Cars diverge only under sustained contact | A controller field is omitted from restore, missing-input policy differs across paths, or colliding bodies do not participate in the same replay interval |
| Cart response differs when struck by a world item | Item is dynamic on one copy, kinematic on another, or paused during replay; this is outside the shared cart/player prediction contract |

## 14. User visual acceptance criteria

After implementation, the user should run a host with one remote client, swap driver/pedestrian roles, and add a third spectator. Repeat with local and Steam sessions where available, then with network delay/loss and different rendering frame rates.

| Scenario | Expected result |
| --- | --- |
| Walk into every side/corner of a parked cart | Solid contact, sliding along the chassis, immediate ability to walk away, no artificial jump |
| Contact the windshield and slide across adjoining solid colliders | Stable solid contact; no duplicate launch or separation caused by changing collider within one cart |
| Push an empty cart intentionally | Slow, weighty movement under sufficient effort; no teleport, hard velocity reset, or engine-like acceleration |
| Leave a cart at rest, then push or drive it; repeat on gentle slopes | Stable resting behavior on level ground and immediate wake without motion resets; assess the documented parking/slope tradeoff |
| Creep into an idle or resisting pedestrian at 0.5–2 m/s | Continuous grounded pushing, with some two-way resistance and no repeated launch |
| Start touching, then accelerate into the player | Contact remains solid; a sufficient new closing impact can launch without requiring a fresh Enter |
| Direct hits at approximately 4, 8, and 15 m/s | Progression from small displacement to unmistakable upward/forward launch; stronger hits produce larger arcs |
| Side swipe, reverse, turning contact, player fleeing in the same direction | Response follows actual contact and relative motion; grazing/co-moving hits are weaker |
| Two driven carts collide head-on or at an angle | Both change velocity/rotation; drivers and spectator converge on the same result |
| Driven cart pushes parked cart; several carts remain in contact | Continuous coupled motion without repeated artificial impulses or remote immovable copies |
| Player between cart and wall or between two carts | Physical blocking without passing through world geometry or unbounded launch energy |
| Launch while holding movement, then land | Momentum remains visible; grounded control returns naturally |
| Enter/exit, crash-eject, change driver, disconnect driver, recover, and late join | Correct occupancy, preserved motion, no stale launch, and no cart freeze during ordinary handoff |
| Enter/exit, then receive another cart hit under delay/loss | Default input steps continue launch/recovery processing on remote copies; no stale revision rejection |
| Sprint into/away from carts and exhaust/recover stamina | Existing sprint/stamina behavior remains; contact effort stays bounded and movement away remains responsive |
| Bird hits, horn, lights, paint, thrown-item impacts | Existing behavior remains intact and external effects occur once |
| Aim at the horn trigger while driving, turning, and viewing tooltips | Horn stays selectable and follows the visual wheel; solid obstruction checks remain; touching the trigger never honks or applies force |
| Throw active items at occupied/empty carts, then drive across sleeping items | Existing item behavior remains; compare driver/host/spectator corrections against the documented item simulation boundary, without assuming fully coupled prediction |
| Hold a turn or handbrake through delay/loss with a spectator | Assess neutralization/correction when prediction exceeds the initial 50 ms control hold; use the implementation tuning reference for adjustments |
| Several carts nearby under latency and packet loss | No sustained correction oscillation, frame hitch pattern, or duplicate launch on reconciliation |

Automated validation and profiling are separate user-authorized work. If requested, use existing impact traces and traffic counters and focus measurements on physics/replay time, suspension queries, contact allocations, and state bandwidth. Do not create a benchmark/editor migration tool as part of this implementation.

## 15. Implementation tradeoffs and tuning reference

During implementation, create `Golf_Cart_Hits_Implementation.md` at the repository root. Keep it aligned with the actual code and serialized values so the user can choose focused adjustments after visual verification. Do not create it as an empty placeholder during plan editing.

For each material tradeoff, record the selected behavior/value, its benefit and cost, the visual symptom or scenario that would justify revisiting it, and the exact file plus setting/constant/method to change. Describe a bounded next adjustment and the behavior it may compromise; do not add runtime settings solely to populate this document. Distinguish analytical estimates from user-reported visual results or measurements, and never claim a scenario was verified without that evidence.

Cover at least:

- `Inserted` prediction mode, the initial three-tick control hold, and responsiveness versus correction under latency/loss.
- Parking resistance, player push effort, actual asset damping, slope behavior, and explicit sleep/wake decisions.
- Launch threshold/lift, recovery duration, separation tolerance, and compound-contact latching/reconstruction.
- Graphics interpolation, physical versus visual seat poses, the relocated solid windshield, and query-only horn targeting.
- Reconciled versus server-only state, replay CPU cost, observer/cart count, and idle replicate/reconcile traffic.
- Server-confirmed ejection/seat transitions and the existing world-item simulation boundary, including potentially different cart/item responses between clients.

Use concise decision entries or a table. This file is not a chronological work log: omit session dates, task status, validation-status lists, and a narrative of edits. When the user supplies visual results, use them to refine the relevant tradeoff/tuning entry rather than append a session report. Cross-reference the matching scenarios in section 14 and preserve the requirement for separate authorization before automated validation or profiling.

## Supporting references

The checked-in FishNet source is the implementation reference, especially its shared replay loop and pending-force serializer behavior. Official guides explain the underlying patterns: [controlled prediction](https://fish-networking.gitbook.io/docs/guides/features/prediction/creating-code/controlling-an-object), [passive predicted bodies](https://fish-networking.gitbook.io/docs/guides/features/prediction/creating-code/non-controlled-object), and [PredictionRigidbody](https://fish-networking.gitbook.io/docs/guides/features/prediction/predictionrigidbody). These are building blocks; they do not remove the need to reconcile controller/contact state or synchronize generation transitions.
