# Golf cart and player collision plan

Implement continuous, predicted cart contact constraints for walking and pushing, with a separate speed-based launch response. Let the pedestrian's owner determine contact against the cart they see, and carry that contact through the player's existing FishNet prediction stream.

Assumptions: an on-foot player should not appreciably move a 450 kg cart by walking into it; a moving unmanned cart should affect pedestrians too; launches use the existing upright player body, without adding ragdolls, damage, or animations. The scope is solid chassis contact, pushing, and launching, not standing/riding on the roof as a moving platform.

## Design constraints from the existing code

| Source | Consequence for the implementation |
| --- | --- |
| `Assets/Game/Prefabs/GolfCart.prefab`, root Rigidbody | `m_ExcludeLayers: 5184` includes Player (64), PlayerItemHitbox (1024), and CartSeat (4096). The Player exclusion disables physical player/chassis contacts. `GolfCartController.Awake` adds exclusions with `|=`, so it preserves Player. Unity documents this behavior in [Rigidbody.excludeLayers](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Rigidbody-excludeLayers.html). |
| `PlayerSeating.SampleCartContacts` / `CartOverlap` | The existing owner-only LateUpdate sampling detects overlap but discards penetration depth. It never blocks walking or separates the player from the cart. Contacts below `MinimumHitSpeed` receive no response. |
| `PlayerSeating.Contact.Touching` | Only a transition from not touching can generate an impact. An initial slow contact prevents subsequent contact processing while the cart continues pushing or accelerates. Initial samples and epoch/revision changes also skip the hit sweep. |
| `PlayerSeating.SampleCartContacts` | Detection combines smoothed `presentation.Graphics.position` with simulation-body velocity and render-time cart motion. These represent different points in time. `CartOverlap` also returns the first overlapping chassis box, rather than the earliest swept contact. |
| `PlayerMotor.ReplicateMove` | Locomotion pulls horizontal velocity toward walk speed or zero. Impact recovery currently suspends this for 0.2 seconds. Ground acceleration/braking is 25 m/s²; air acceleration is 4 m/s². Launch momentum needs deliberate protection, particularly on shallow hits. |
| `GolfCartSettings.asset` | Existing pedestrian settings are 1.5 m/s minimum closing speed, 1.2 multiplier, and 0.3 lift. At the threshold, the nominal vertical change is only 0.54 m/s: approximately 1.5 cm of ballistic rise. Increasing these values alone does not supply blocking, sustained pushing, or missed contact handling. |
| `GolfCartController.AccumulateCollision` | Player contacts are excluded from crash severity. Preserve the distinction between hitting a pedestrian and a crash that ejects riders. |

These code paths explain missing collision behavior and weak low-speed lift. They do not establish that every reported failed launch has the same cause.

## Networking model to preserve

`SessionRoot.prefab` runs FishNet at 60 ticks/second with TimeManager-controlled physics and prediction state interpolation of two ticks. `Player.prefab` enables Rigidbody prediction and state forwarding. `PlayerMotor` replicates movement, reconciles body state, and retains a generation/sequence/request-ID impact history for replay.

`GolfCartNetwork` instead assigns one simulator: the driver client while occupied, otherwise the server. Other copies are kinematic. Moving carts normally publish motion every third tick, approximately 20 Hz; resting and parking changes use reliable delivery. The customized `NetworkTransform.Motion.cs` interpolates these frames and resets motion by epoch during handoff. `OfflineRigidbody` pauses the cart during reconciliation; it does not reconstruct historical cart movement for each replayed player tick.

`SessionController` selects Tugboat for local/solo sessions and FishySteamworks through Multipass for Steam sessions. Collision behavior belongs above these transports. Keep both transports and the cart ownership/handoff model.

The smallest physical change would be removing the Player exclusion. It would restore ordinary contacts, but the player would then collide against different cart poses on different machines, including during replay. A driver-side dynamic cart and remote interpolated kinematic cart would also transfer motion differently. Prefer an explicit, one-way player/cart solver, retaining the exclusion to prevent duplicate Unity impulses. Existing terrain and environment collisions remain Rigidbody-driven.

## 1. Move contact detection into player ticks

Replace the cart collision portion of `PlayerSeating` with motor-owned contact handling. Keep seating requests and exit placement in `PlayerSeating`. Use a `PlayerMotor.CartContacts.cs` partial if needed to keep the motor readable; this adds no GameObject component.

Cache the player's capsule and each cart's chassis geometry, local poses, and settings when initialized/registered. Reuse the existing cart registry and four chassis boxes. Subscribe and unsubscribe through network lifecycle events. Avoid per-frame component lookup and scanning all carts from LateUpdate.

For each newly created owner movement tick:

1. Sample nearby cart display poses and their linear/angular velocities once. Use the motor body's capsule position for physical contact. Keep smoothed graphics out of authoritative movement calculations.
2. Retain the previous sampled pose for each nearby cart. Sweep relative player/cart travel, including cart rotation and the player's intended movement for this tick. Broad-phase against swept bounds first.
3. Find the earliest contact across the chassis boxes. Use bounded translation/rotation subdivisions and refine the first intersecting interval; include rotation's swept corner distance when choosing subdivisions. If the subdivision budget is exhausted, conservatively stop at the last clear position rather than skipping a possible collision.
4. Resolve starting overlaps separately, including first samples, spawn, and handoff. Use the outward normal and penetration distance, not just an overlap boolean. Unity's [ComputePenetration](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Physics.ComputePenetration.html) supports supplied poses without moving the actual colliders.
5. Produce a small contact manifold for this tick, merging redundant normals from compound boxes. Retain distinct blocking directions at corners and between carts. Process contacts in a stable order.

Sample virtual cart poses without moving or rewinding the actual cart. Interpolate only within a single epoch; a handoff or teleport starts a fresh sample. In frames containing several physics ticks, do not interpret one rendered cart displacement as fresh motion for every tick.

Use a small separation skin, initially about 2 cm. Contact retention can use a slightly larger separation tolerance, but that tolerance must not suppress ongoing pushes or create an early launch.

## 2. Carry contact through prediction

Extend `MoveInput` with an optional, compact contact manifold. A contact describes the pedestrian owner's sampled constraint: cart ID/epoch, outward normal, a plane offset for the capsule center, and cart surface normal speed. Encode the capsule's support radius/height into the plane offset so consumers do not mistake it for the raw chassis surface. Include the player's impact generation alongside the existing seating revision to discard pre-reset input.

The plane describes the permitted capsule-center boundary for the current tick's swept contact. It expires with that tick; it must not become an infinite persistent wall after the player leaves a bumper or corner. Bound the manifold to a small fixed capacity and use a conservative stop when independent constraints exceed it.

Inside `ReplicateMove`, all peers consume the same owner-reported constraints. They must not re-query their own current cart transforms during replay. Recompute the response against the reconciled player position/velocity, rather than replaying a raw positional displacement that may no longer fit.

Apply movement in this order:

1. Accept the matching movement generation/revision and apply scheduled world impacts.
2. Compute ordinary movement intent, accounting for pending impact velocity and recovery.
3. Project intended movement/velocity against contact constraints and resolve any existing penetration.
4. Apply the resulting forces through `PredictionRigidbody`, then let TimeManager run world physics.
5. Reconcile the resulting body and any persistent recovery state through `MotorState`.

`PredictionRigidbody.Simulate()` flushes queued forces; actual physics runs later in `TimeManager`. Contact code must respect that ordering. Account for this tick's gravity and pending impulses when predicting travel. Do not apply the same push once before and again after physics, or mutate the body only in LateUpdate.

No contact means only an empty-contact flag/count in the input payload. Send manifold data only during contact or an imminent swept collision. Use the existing replicate redundancy and state forwarding; do not send a reliable RPC each tick for pushing. Start with normal/vector packing supported by the existing serializers, without creating a general compression framework.

Only created input carries new contact information. Missing/default input must not extrapolate penetration corrections or manufacture launches. A short missing-input interval can require ordinary reconciliation; it must never accumulate repeated impulses. Preserve historical contacts for replay even if the live cart has since changed epoch; stop accepting new old-epoch samples. Seating/reset generations discard stale player input.

Launches continue through `SubmitWorldImpact`, whose request IDs and sequence numbers already prevent duplicate application after acknowledgement. The owner predicts immediately; the server accepts and distributes the event. Generate launches only during fresh owner sampling, never during replay. The driver and server must not independently submit the same pedestrian hit.

Increment `SessionController.Protocol` for the changed movement wire format, using the existing compatibility mechanism.

## 3. Solid walking and sustained low-speed pushing

Use a normal pointing from the chassis toward the player. For a contact point `p`, compute cart surface velocity from the sampled frame:

```text
vCart = linearVelocity + cross(angularVelocity, p - worldCenterOfMass)
closing = max(0, dot(vCart - vPlayer, normal))
```

Use a point on the chassis near the actual capsule contact, not the player's center, for the rotational term.

For a stationary cart, remove only the player's inward movement component. Keep tangential movement so the player slides around the cart and can immediately walk away. Walking into a parked cart must not add upward velocity.

For a slowly moving cart, enforce the moving contact boundary and raise the player's normal velocity only enough to avoid being overtaken by that surface. Reapply this constraint each active tick after locomotion intent. Do not add a fresh full cart velocity every tick and do not repeatedly invoke world-impact recovery for ordinary pushing.

Handle forward motion, reverse, sideways sliding, and turning with the same contact geometry. Preserve outward player motion and allow steering out of the cart's path. A co-moving player should remain stable rather than repeatedly accelerating.

Sweep any positional separation against solid world geometry. Never teleport the player through a wall or the ground to satisfy a cart contact. If trapped, prioritize world collision and stop at the nearest valid position; allow temporary cart overlap rather than unbounded correction or an artificial launch. A fully coupled crushing/stopping response would require cart feedback and is outside this one-way design.

## 4. Speed-based launching

Measure launch severity before applying the low-speed constraint; otherwise matching cart/player velocities first would erase the closing speed.

Require cart motion into the player as well as relative closing motion. A player sprinting into a parked cart should collide without being launched. Glancing contacts should transfer less energy than head-on contacts at the same cart speed.

Suggested initial response, using the horizontal outward contact direction `n`:

```text
cartApproach = max(0, dot(vCartHorizontal, n))
relativeApproach = max(0, dot(vCartHorizontal - vPlayerHorizontal, n))
severity = min(cartApproach, relativeApproach)
blend = smoothstep(launchThreshold, 2 * launchThreshold, severity)
normalDelta = relativeApproach * lerp(1, collisionMultiplier, blend)
targetLift = hitLift * severity * blend
verticalDelta = max(0, targetLift - currentVerticalVelocity)
```

Start with a 3 m/s launch threshold, 1.2 collision multiplier, and 0.4 lift multiplier. Below the threshold, use only the continuous push constraint. Above it, submit one velocity-change impact using the horizontal normal and vertical delta. Preserve unrelated tangential velocity and existing upward motion. Limit launch eligibility to chassis side impacts; vertical roof/underside contact uses separation, not a sideways fallback launch.

For a stationary pedestrian hit head-on at 8 m/s, these initial values target roughly 9.6 m/s horizontal velocity and 3.2 m/s upward velocity. At 15 m/s, they target roughly 18 m/s and 6 m/s. Ideal ballistic rise is approximately 0.52 m and 1.83 m respectively, before terrain or other contacts. These are tuning targets, not promises about the rendered result.

Contact state must distinguish touching from already launched. Continue evaluating low-speed contact so a new substantial closing impact can become a launch while touching. Launch once per contact episode; rearm after genuine separation. Matching the cart's speed during a smooth push should not spontaneously launch the player merely because both are now fast.

Compute cart launch recovery from the intended airtime, initially `clamp(2 * targetLift / gravity, 0.35, 1.2)` seconds. Reuse the existing reconciled recovery timer so walking/braking does not immediately erase the launch. Keep the 0.2-second behavior for item impacts and seat ejections. Finish the cart recovery early on a genuine landing after takeoff, with the small cart-specific recovery flag included in `MotorState`; avoid treating the launch tick's ground probe as a landing.

The push constraint must consider pending launch velocity and only fill any remaining normal-velocity deficit. Never stack both a full push and a full launch for the same impact.

## 5. Seating, lifecycle, and presentation

Seated players and players awaiting exit placement remain excluded. Exit grace should suppress launch from the cart just exited until separation, while retaining solid blocking and gentle separation. Clear contact/launch state on player reset, seating transition, ownership change, network stop, and cart removal. A cart epoch change resets sweep history without inventing motion across the handoff.

Keep the existing graphics smoother and camera path. The body drives the response; moving graphics alone would conceal rather than fix collision. If smoothing visibly leaves the local player inside the chassis, constrain only the inward graphical offset during actual contact while preserving tangential smoothing. Avoid resetting the whole smoother every contact tick.

The pedestrian owner reacts immediately to their sampled cart. The driver and spectators see that response after normal input/event propagation. This design prioritizes consistent outcomes and responsive pedestrian movement; it cannot make differently delayed cart/player streams depict the exact same contact instant on every client.

## Implementation order and file scope

| Order | Files | Planned change |
| --- | --- | --- |
| 1 | `PlayerMotor.cs`, optional `PlayerMotor.CartContacts.cs`, `PlayerSeating.cs` | Introduce tick contact sampling and replayable constraints; remove the superseded LateUpdate hit sampler. Preserve seating and item-impact hooks. |
| 2 | `GolfCartController.cs` | Cache/expose chassis geometry needed by the contact solver. Preserve driving, suspension, and crash/ejection behavior. |
| 3 | `PlayerMotor.cs` | Add continuous blocking/pushing, launch scheduling, and cart-specific recovery state with reconcile support. |
| 4 | `GolfCartSettings.cs`, existing `GolfCartSettings.asset` | Replace `MinimumHitSpeed` with a clearly named launch threshold; update multiplier tooltips and explicitly migrate serialized values. Keep tuning limited to threshold, collision multiplier, and lift. |
| 5 | `SessionController.cs` | Bump protocol for the changed replicate payload. |
| 6 | `PlayerPresentation.cs`, only if required by visual review | Clamp contact-directed graphical penetration without altering normal smoothing. |

No new prefab, attached component, scene edit, or ScriptableObject asset is required. Retain the existing cart Player exclusion for the explicit solver. Let Unity generate metadata for any new C# file. Apply the small settings migration directly; do not create a migration editor tool. The imported FishNet prediction/transport implementation does not need modification.

## Visual acceptance checks for the user

After implementation, use a host and a remote client, then add a third observer. Swap host/client roles between driver and pedestrian.

| Scenario | Expected visible result |
| --- | --- |
| Walk into a parked cart from every side and diagonally into corners | Capsule stops at the chassis, slides along it, and walks away without sticking, bouncing, or launching. |
| Cart creeps at approximately 0.5–2 m/s, including reverse | Player is pushed continuously along the ground; holding movement into the cart does not allow passage through it. |
| Cart starts against a player, then accelerates | Contact stays solid; a sufficiently strong new closing impact can launch without requiring the pair to separate first. |
| Direct hits near 4, 8, and 15 m/s | Smooth progression from push to clearly visible launch; faster hits travel farther and rise higher. |
| Turning, side-swipes, fleeing pedestrians, and downhill coasting | Motion follows contact direction and relative speed; grazing and co-moving contacts are weaker than head-on hits. |
| Sustain contact, separate, then hit again | No repeated launch during one impact; a later distinct hit works. |
| Hit near walls, slopes, chassis corners, or another cart | No teleport through world geometry, sinking, correction explosion, or unexplained vertical launch. |
| Enter/exit seats, eject, switch driver, disconnect driver, and respawn | No stale launch or sweep across a teleport; on-foot collision resumes correctly. |
| Repeat with latency/loss and different frame rates | Owner response remains prompt, remote motion converges without a second launch, and contact does not depend on render frame rate. |
| Observe the full arc while holding movement or releasing controls | Launch momentum remains visible through takeoff and flight; grounded control returns naturally. |

Use the existing impact trace and development traffic counters only if a visual failure needs diagnosis. Any automated checks or Unity play/build validation should be requested separately; the acceptance criteria above are for the user's visual review.
