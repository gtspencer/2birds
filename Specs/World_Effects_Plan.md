# Player World Effects Plan

## Goal

Make a thrown rock visibly move the player it hits, with immediate feedback on the affected player's client and consistent resulting movement across the network. Provide one reusable movement entry point that future cars can use to launch players.

Expose an impulse multiplier in the Inspector so a designer can reduce or increase the shove from each item type and, later, each vehicle without changing code.

## Scope and assumptions

- The first implementation covers one-time impacts and a short movement recovery period.
- Players remain upright dynamic Rigidbody capsules. Ragdolls, tumbling, sustained vehicle pushing, and moving platforms are separate work.
- Trust the affected client's contact report. Prioritize local responsiveness and small messages.
- Keep the existing throwable charging, release, inventory, and pickup behavior.
- Moving world items can cause impacts regardless of whether they were thrown, dropped, or otherwise propelled. Impact speed determines strength; low-speed contacts produce no shove.
- Use the existing player and rock components. No new scene objects, player components, or prefab assets are required for the initial implementation.

## Design

### Shared motor entry point

Generalize the item-specific hit path into a world-impact entry point on `PlayerMotor`.

Its gameplay inputs are a world-space velocity change and a recovery duration. Keep network identity and scheduling in the motor's impact event data. Sources calculate the desired response; the motor handles movement, replay, and confirmation.

- Express the response in meters per second and apply it through `PredictionRigidbody.AddForce(..., ForceMode.VelocityChange)` during `ReplicateMove`.
- Add the change to existing velocity so airborne motion, jumps, and successive impacts combine naturally.
- During recovery, suppress horizontal movement acceleration and braking. Preserve looking and the existing jump rules.
- Start rock recovery at 0.2 seconds, converted to ticks. Allow a later car source to request longer recovery through the same API.
- Store `RecoveryTicks` in the replacement impact struct from the first implementation. For overlapping impacts, add their velocity changes and use `remainingRecovery = Max(remainingRecovery, impact.RecoveryTicks)` after the normal tick countdown. A short rock recovery must not shorten a longer existing recovery.
- Reconcile recovery and impact application progress with the Rigidbody state.
- Preserve the meaning of the existing `QueueImpulse` entry point for its callers: convert physical impulse to velocity change using the motor Rigidbody's mass before entering the shared path. The player movement body is 80 kg; the separate kinematic hitbox is 1 kg. The diagnostic impulse `(800, 320, 0)` therefore remains a velocity change of `(10, 4, 0)` m/s.
- Keep `MovementMode.External` for explicit external control. Impacts continue to apply during this mode, preserving existing behavior. A future seated vehicle controller must define whether an impact moves the vehicle, ejects its occupant, or is suppressed. Enforce any future suppression at the motor boundary so it also covers queued and scripted impacts.

Use a small impact data struct alongside the motor. Do not build a general effect framework, effect inheritance tree, or new registry.

### Designer collision tuning

Add `MinimumImpactSpeed` and a nonnegative `ImpulseMultiplier` to `ItemDefinition`. Show the latter as **Impulse Multiplier** in the Inspector, defaulting to `1`, with a short tooltip explaining that it scales the shove received by a player. Designers edit the existing item definition asset, such as `Rock.asset`. Do not add a second knockback scale for the same calculation.

| Impulse Multiplier | Player response |
| --- | --- |
| `0` | No gameplay shove or movement recovery from this source. Physical item collisions still occur. |
| `0.5` | Half the baseline velocity change. |
| `1` | Baseline velocity change. |
| `2` | Twice the baseline velocity change. |

Apply the multiplier once when the reporting source creates the event: `finalVelocityChange = baseVelocityChange * ImpulseMultiplier`. Send and retain this resolved vector. Server confirmation, spectator application, and prediction replay use it directly; they must not multiply it again or recalculate it from a subsequently edited asset.

The multiplier adjusts gameplay shove strength without changing collider geometry, rock mass, throw speed, or the rock's physical bounce. It scales the full shove vector, including any upward component supplied by a future vehicle. Positive multipliers do not change recovery duration. Skip the gameplay event entirely when the resulting vector is zero.

Use a per-item-definition field initially. Future vehicle impact components expose the same designer-facing multiplier for their own base response. Avoid stacking a global player multiplier with the source multiplier in this implementation.

### Rock response

Calculate the baseline velocity change from the rock's incoming velocity relative to the player, projected into the contact direction. Let `intoPlayer` be the unit contact direction pointing from the rock into the player and `incomingSpeed = Max(0, Dot(rockVelocity - playerVelocity, intoPlayer))`. Below `MinimumImpactSpeed`, produce no shove; otherwise use `baseVelocityChange = intoPlayer * incomingSpeed`, then apply `ImpulseMultiplier`.

Use incoming motion before the contact changes the rock's velocity. For a remote displayed-motion sweep, use the incoming segment being swept; a post-bounce snapshot velocity must not be treated as incoming velocity. Follow the contact-sampling limits below.

The response should push away from the incoming rock. Preserve meaningful vertical contact direction without adding an unconditional upward launch to every rock hit. Cars can supply their own upward launch later.

Tune the multiplier on the existing `Rock.asset` for a noticeable shove. Keep the initial rock recovery duration fixed at 0.2 seconds in code; the impact event still carries its converted recovery ticks.

#### Rock contact batches and limits

Collect eligible item contacts on the affected owner's existing hitbox until its next movement tick. Add their resolved velocity changes, then limit the combined vector to 40 m/s before submitting one shared motor request with 0.2 seconds of recovery. Use the same batching path for host collisions and remote sweeps. Discard an unfinished batch when the player generation changes.

The ceiling applies after source multipliers and preserves ordinary single and paired default rock hits. Large stacks and extreme multipliers saturate at the ceiling. It limits the item contribution, not the player's existing velocity; scripted impulses and future vehicle submissions use the generic motor API independently. Server confirmation and replay retain the bounded vector without regrouping contacts or applying the limit again.

Use presented shapes for contact geometry and timestamped motion for strength. Sweep snapshot boundaries separately and use each segment's incoming velocity calculated from its snapshot interval. For locally predicted rocks, retain physics segments and the player's simulated incoming velocity for their ticks. For remote snapshot segments, interpolate sampled simulated player velocities across the visible contact interval. Never derive player velocity from graphics displacement. Rebase immediate player reconciliation shifts and item correction displacement without clearing ordinary contact travel during smoothing.

Editor and development builds retain a recent impact trace on the motor, available through **Dump Impact Trace** in its component context menu. Contacts identify the source and release, measured velocities, and batch. Motor entries identify requests, confirmations, state acknowledgements, replay applications, and physics versus graphics positions. Capped batches and exceptional post-impact speeds dump the trace automatically. Existing MVP diagnostics also write an impact log when enabled.

### Contact ownership

The affected player's owner is the single reporter for rock impacts, including when that owner is the host.

1. Detect a contact locally against that player's capsule.
2. Queue the contact in the owner's item batch for the next local movement tick.
3. Send the event to the server.
4. The server accepts the reported response, schedules it in the player's simulation, and forwards its confirmation to observers.

Use one detector per affected owner:

- **Host-owned victim:** Use the actual `WorldItem.OnCollisionEnter` callback as the sole local detector. Capture incoming velocities before the physics step and use the callback's contact geometry. Route this contact through the host owner's shared impact submission path. Do not also sweep for this victim.
- **Remote-owned victim:** Run the displayed-motion sweep on that victim's owning client. The server's collision callback must not independently queue a shove for this player.

The host's local player is client-owned too. The server continues to simulate physical collisions and bouncing for all rocks, but physical contact only creates a gameplay shove through the designated owner's detector. Server-originated scripted impulses remain available through the shared motor path.

On the host, owner submission and server acceptance refer to the same pending event. Promote it in place and apply it once; do not enqueue separate local-prediction and server copies.

### Remote contact sampling

Remote rocks and the separate player hitbox are kinematic, so enabling the existing `OnCollisionEnter` on clients is insufficient. Add explicit swept contact detection to the existing item/player integration:

- Follow the rock's presented motion on the affected client; account for the player's movement between samples as well.
- Sample after item presentation updates, with a defined order rather than relying on arbitrary `LateUpdate` ordering. Use the local player's presented capsule pose for visible contact and queue the resulting response into its motor on the following movement tick.
- Sweep the rock's collision sphere against the player's capsule, including initial overlap handling. Use cached shapes and references.
- Detect contact outside prediction replay, then record an event. During replay, apply recorded events without running detection or sending messages again.
- Suppress repeated reports while the same item remains in contact. Rearm after separation so a later bounce can cause another hit.
- Consult the same ignored-player state used by the physical collision-grace path. `Physics.IgnoreCollision` does not filter a manual geometric sweep. Share its lifetime: grace ends when the timer expires or the collider pair separates, whichever happens first. Do not maintain an independent thrower timer for sweeps.
- Ignore held or sleeping items. Clear contact tracking when they stop being eligible, so an old contact does not suppress a later eligible hit.
- Add a shared contact-state reset and call it from `ResetPresentation`, which both `Initialize` and `ReturnToPool` already invoke. Explicitly clear previous poses, incoming-motion samples, contact suppression, and release tracking; clearing the existing snapshot arrays alone is insufficient.
- Also reset contact samples on pickup, release, removal, player reset, and explicit position corrections that bypass initialization. A correction must not become a swept flight path through the player. Rebase samples at the corrected pose before resuming ordinary-motion sweeps.
- Use the release identity rather than a motion revision alone when tracking an item's flight; ordinary lifecycle changes also increment revisions.

#### Contact information between snapshots

The first implementation defines a remote owner's hit from the motion presented on that client. A sweep catches crossings along that motion, but it cannot reconstruct every contact from the server's simulation. A rock may contact a player and separate between 20 Hz snapshots, leaving both sampled endpoints outside the capsule. Connecting those endpoints can omit the contact and its incoming velocity.

Treat this as a limit of the initial contact policy, not a problem that interpolation or an impact flash automatically solves. Do not force a second, server-generated shove for a contact absent from the affected client's view.

Before accepting the remote detector, the user must assess short contacts and ricochets using the visual checks below. If missed contacts are unacceptable, extend item presentation with compact contact/trajectory samples carrying the source release identity, server contact tick, contact geometry, and incoming motion. Those samples supply missing path information to the affected owner's detector; they do not independently apply another shove. Specify how a sample matches an already detected contact and how duplicate samples are suppressed before adding that extension. Keep the affected owner as the sole gameplay reporter.

The host's rock presentation follows physics, while remote rock presentation follows delayed snapshots. Each detector follows its local representation; the host is not inherently ahead of its own rock visuals. Account separately for player graphics smoothing, transport delay, and sample timing.

### Event timing, reliability, and replay

Keep reliable impact events separate from ordinary unreliable movement updates. Use one compact event per impact; do not broadcast a force every frame.

Use separate identities for a provisional owner request and a confirmed application:

| Data | Required fields |
| --- | --- |
| Owner request | Player generation, monotonically increasing owner request ID, intended owner movement tick, resolved velocity-change vector, `RecoveryTicks`. |
| Confirmed impact | Player generation, server-assigned application sequence, matching owner request ID (`0` for a scripted server impulse), actual owner and server application ticks, resolved velocity-change vector, `RecoveryTicks`. |
| Reconciled impact state | Player generation, last applied confirmed sequence, last incorporated owner request ID, remaining recovery, and any pending movement effect required by that state. |

The generation changes on player reset or ownership change; object identity scopes it across spawns. The target player is implicit in the motor RPC. Keep source-specific contact suppression local unless the contact/trajectory extension requires source data.

All confirmed impacts, whether owner-reported or server-originated, share one server sequence assigned in actual application order. Assigning IDs merely in receipt order would not justify a highest-applied-ID comparison when events are scheduled for different ticks.

Owner requests arrive over one reliable ordered RPC path and are applied in request order, with nondecreasing owner application ticks. This makes the last incorporated owner request ID a cumulative acknowledgement. Interleave scripted server impulses using the shared confirmed sequence. Match owner predictions by generation and owner request ID; never compare a provisional owner request ID against a confirmed sequence.

Implement these rules explicitly:

1. **Use the correct timeline.** The owner and the server's simulation of that owner's input use owner movement ticks. Spectators use server ticks. Never compare an owner tick directly with a spectator's movement tick.
2. **Schedule against the actual application tick.** A contact discovered after physics applies on a following movement tick. Distinguish contact time from application time.
3. **Handle late reports.** If the server has already processed the requested owner tick, apply at the next eligible player simulation step and confirm the actual owner/server tick pair. Do not pretend the force was applied in the past or rewind the whole world.
4. **Replace predictions on confirmation.** Match by generation and owner request ID. Replace the pending predicted record with its confirmed sequence and actual schedule without adding another impulse. If the schedule changed, replay it from the applicable reconciliation state. If authoritative state already incorporates it, confirmation only resolves the record's identity.
5. **Handle message ordering.** A reconcile can arrive before the reliable confirmation. Its incorporated owner request ID tells the owner which provisional shoves are already contained in the Rigidbody state. Its last applied confirmed sequence does the same for confirmed events on every recipient. Restore these acknowledgements with the state being replayed.
6. **Retain replay history.** Keep unacknowledged events and events still needed by retained movement history. A local replay must not permanently consume them. Prune once authoritative state has incorporated them and earlier replay states no longer need them.
7. **Support simultaneous events.** Apply and replay confirmed events in server application sequence. Use the highest-applied-sequence comparison only for this ordered confirmed stream, never across independent owner/server ID spaces. Prune confirmed and provisional histories according to their own acknowledgements and retained replay states.
8. **Reset cleanly.** Discard events from an earlier reset generation after respawn, ownership change, or despawn. A delayed impact must not launch a freshly reset player.

Keep presentation corrections on the existing graphics smoothing path. Server processing and observer presentation still incur network delay; the immediate guarantee is the affected client's response to its local contact. Late scheduling can require correction, so avoid promising identical contact frames on every client.

Defer an observer impact flash. It could improve readability later, but it would require contact-position data and does not fix detection or timing.

## Implementation order

### 1. Generalize movement response

- Introduce the request, confirmed-impact, and reconciled-state fields above alongside the motor, including per-event recovery ticks.
- Replace item-specific names in the motor and reconcile data where they become obsolete.
- Apply velocity changes and maximum remaining recovery inside the predicted movement step. Preserve impulse application during `External` mode.
- Preserve physical-impulse semantics for existing scripted callers.
- Remove the motor's dependency on `WorldItemRegistry` for generic impact handling.

### 2. Correct scheduling and acknowledgement

- Separate owner and server application ticks in the existing server-originated hit path.
- Implement the shared confirmed application sequence, owner request matching, and both reconciliation acknowledgements before adding owner-predicted rock reports.
- Add the event lifetime, acknowledgement, reset, and replay rules above.
- Keep force application in one place so the later local prediction path uses the same behavior.

### 3. Add local rock contact reporting

- Add host-owner collision detection with pre-physics incoming-motion capture, and remote-owner displayed-motion sweeps with explicit sampling order.
- Share release-grace state and implement the contact resets at all listed lifecycle boundaries.
- Implement the baseline incoming-speed response, `MinimumImpactSpeed`, and designer-facing `ImpulseMultiplier` on `ItemDefinition`.
- Add reliable owner reports and server confirmations.
- Use the host collision callback only for the host-owned victim; suppress server-generated rock shoves for remote-owned victims and suppress host-victim sweeps.
- Set the multiplier on the existing rock definition. Store the multiplied vector in the event so every simulation uses the same value.
- Make the remote contact-sampling limitation part of user acceptance before treating the detector as complete. If needed, plan the compact contact/trajectory extension and its contact-matching rule before implementing it.

### 4. Refine presentation from user feedback

- Compare the rock's displayed contact with movement of the local player and observed players.
- Address duplicate application, replay timing, and invalid correction sweeps before adjusting smoothing.
- Keep current snapshot rates initially. Increase traffic only if visual feedback establishes that timing cannot be made acceptable with the existing presentation.

### 5. Connect cars when vehicle movement exists

- Add an impact-producing component to the vehicle and configure its collision layers when the car feature is implemented. Notify the user before that prefab/component work.
- Calculate a velocity change from relative vehicle/player motion, including upward launch where appropriate.
- Expose a nonnegative **Impulse Multiplier** on the vehicle impact component, with the same `0`, `1`, below-`1`, and above-`1` behavior as items. Multiply the complete base launch vector once before submitting it to the motor.
- Use the same affected-owner reporting, event scheduling, and motor recovery path.
- Ensure a vehicle contact does not both directly push the motor Rigidbody through physics and apply the same gameplay impact again.
- Reuse the motor API; keep vehicle-specific contact calculations on the vehicle side.
- Define seated-player impact behavior with the vehicle controller. If impacts are suppressed in that state, implement the policy at the motor boundary and reconcile the controlling state.

## Expected files

| File | Planned responsibility |
| --- | --- |
| `Assets/Game/Runtime/Player/PlayerMotor.cs` | Shared impact structs/API, per-event recovery, request matching, confirmed sequence, reliable reporting, scheduling, and reconciliation acknowledgements. |
| `Assets/Game/Runtime/Items/WorldItemMessages.cs` | Remove item-specific hit data after moving the replacement alongside the motor. |
| `Assets/Game/Runtime/Items/WorldItem.cs` | Host-owner collision routing, remote-owner sweeps, shared grace checks, lifecycle resets, and incoming motion. |
| `Assets/Game/Runtime/Items/WorldItemRegistry.cs` | Supply cached player access and sampling hooks; remove the competing server hit route for remote-owned players. |
| `Assets/Game/Runtime/Items/ItemDefinition.cs` | Inspector fields for `MinimumImpactSpeed` and nonnegative `ImpulseMultiplier`, with a concise designer tooltip. |
| `Assets/Game/ScriptableObjects/Items/Rock.asset` | Initial minimum-speed and impulse-multiplier tuning. |
| `Assets/Game/Runtime/Player/PlayerItemHitbox.cs` | Expose cached capsule geometry if needed by contact queries. |

`ThrowableItemUse.cs` remains the charge/release entry point. `BakedPickup.cs` remains placement identity data. Neither needs impact responsibilities.

Use Unity CLI for Unity-side asset work when possible, then Unity MCP if necessary. Let Unity generate `.meta` files. Apply changes directly; do not create a migration editor tool or regenerate game scenes.

## Rough effort

| Deliverable | Estimate |
| --- | --- |
| Shared motor response and rock tuning | 0.5-1 day |
| Network scheduling, local contact prediction, and replay handling | 2-4 additional days |
| Impact hookup for an existing networked car | 0.5-2 additional days |

Estimates depend on iteration from visual feedback. They exclude vehicle movement, ragdolls, and sustained pushing.

The network/contact estimate assumes the initial displayed-motion contact policy is acceptable. Adding contact/trajectory samples and matching them to predicted contacts requires a separate estimate if user feedback makes that extension necessary.

## Visual validation by the user

Implementation does not authorize automated validation. The user performs these checks; run builds, tests, or validation tools only when explicitly requested.

- **Basic shove:** With a host and remote client, swap thrower and victim roles. Compare tap/full-charge throws and stationary/moving players. The shove should follow incoming contact and be clearly visible.
- **Designer multiplier:** In the Inspector, try `0`, `0.5`, `1`, and `2` on the rock definition using comparable incoming speed and contact direction. Zero should produce no gameplay shove or recovery, lower values should reduce the initial shove, and higher values should increase it. Compare the initial velocity change; walls, gravity, and braking mean total travel distance need not scale linearly. Confirm physical throwing/bouncing remains intact and network confirmation does not multiply the shove a second time.
- **Host single application:** Hit the host-owned player and compare with a remote victim. The host's physics callback must produce one shove, with no additional sweep or server-acceptance copy.
- **Movement recovery:** Hold movement toward and against the shove, look around, and jump. Knockback should persist briefly, then normal movement should resume without a sudden velocity reset.
- **Airborne and blocked motion:** Hit jumping players and players beside walls, slopes, and ledges. Movement should respect world collisions and combine with existing momentum.
- **Repeated contacts:** Throw multiple rocks, cause a rock to bounce back, and rest a rock against a player. Distinct contacts should stack up to the 40 m/s item-batch ceiling; sustained overlap should not repeatedly launch the player. Compare one and two default rocks at 30, 60, and 120 FPS, then a larger stack. Inspect the impact trace if a shove is capped or the player moves unexpectedly far.
- **Short contacts:** Compare brief grazes and ricochets on host and remote victims, including lower frame rates and network delay. Assess whether the remote displayed path omits contacts often enough to require contact/trajectory samples.
- **Release behavior:** Compare throws, drops, self-contact during release grace, sleeping rocks, pickup, and rethrow. Grace should end on separation or timeout consistently for physics and sweeps. Only eligible moving contacts should apply an impact.
- **Pool and correction behavior:** Reuse an item after pickup/pooling and observe a corrected rock position. No stale contact should suppress a new hit, and no correction should create a phantom swept hit.
- **Latency and loss:** With added delay, jitter, and packet loss, check immediate local response, eventual agreement, and the absence of duplicate shoves. Assess corrections when a report reaches the server late.
- **Mixed event sources:** When both sources can be triggered, overlap a scripted impulse and a rock hit. Both should contribute regardless of their separate request IDs, and confirmation/reconciliation should not repeat either effect.
- **Observer timing:** Use a third client if the session supports it. Compare the visible rock contact and player displacement. If the session is limited to two players, defer this check to a supported setup rather than expanding session scope for this feature.
- **Reset:** Get hit near the fall boundary and re-enter the world. Earlier events should not affect a newly reset or spawned player.
- **Future car:** Compare slow contact, a fast strike, an airborne strike, and repeated bumper contact. Repeat the multiplier checks on the vehicle. During a longer car recovery, hit the player with a rock and confirm the shorter recovery does not replace the longer remaining one. Confirm the vehicle's chosen seated-player policy and a natural return of control.
