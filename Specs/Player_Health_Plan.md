# Player Health Implementation Plan

Implement [Player_Health_Spec.md](Player_Health_Spec.md). The specification controls behavior; this plan supplies the code boundaries, transition rules, asset work, and implementation order needed to execute it independently.

## Scope and execution rules

- Implement health, the specified damage/healing sources, downing, ragdolls, death camera, revival, respawn, and their network/UI integration on the existing player object.
- Follow `AGENTS.md`. Use Unity CLI at `C:\Users\spenc\AppData\Local\Unity\bin\unity.exe` for supported Unity operations, then Unity MCP if needed. Let Unity generate metadata. Do not add a migration tool or edit the game scene.
- Before implementation changes player-prefab components, generated avatar components, layers, or assets, tell the user what is required. The asset changes are listed below.
- Do not run builds, tests, Play Mode, automated validation, or screenshots unless the user explicitly requests validation. The final section is for the user's visual acceptance checks.
- Keep new logic shared where multiple systems call it, cache references during initialization, and subscribe to changes. Continuous work belongs only to active physics, active interactions, cameras, or animated UI.
- Preserve inventory/world-item identity, existing shove physics, potion applications, and cart thresholds. Do not add generic damage to `SubmitWorldImpact`, `QueueWorldImpact`, or replayed movement.

## Implementation decisions

- Use `Alive` and `Downed` as persistent life states. Revival and respawn are transitions back to `Alive`, distinguished by their reset behavior.
- Put shared health/damage/fall settings in the existing `GameSettings` asset. Put revive duration on the player revival component and vignette settings on the existing HUD component. Maximum health 100, revival health 50, rescue duration 30 seconds, and Give Up hold duration two seconds are fixed rules.
- Use the ragdoll pelvis as the body root. Camera targeting, revival targeting, nameplate placement, and retained network position all consume one `PlayerRagdoll.RootPosition` source. Standing placement converts that point into the existing upright capsule coordinates; it must not place the upright capsule centre at ground level.
- Keep the existing TMP nameplate. Add a simple world-space bar beneath it using its existing transform and visibility, while all local overlays use UI Toolkit. Do not convert the nameplate system.
- Preserve the current living-player seating/carry restrictions on item impacts. The spec explicitly extends alive blast eligibility to seated and carried players; do not make the disabled walking/item hitboxes into a new collision-damage path for attached players.
- Health is local immediately. The server trusts health reports and adjudicates claims and terminal transitions. No server recomputation of collision damage or healing is required.

## Code map and responsibilities

Paths below are relative to `Assets/Game/` unless stated otherwise.

| Files | Work |
| --- | --- |
| New `Runtime/Player/PlayerHealth.cs` | Shared health API, local whole-point changes, fractional healing, life-state application, permission events, and runtime fall protection. Cache collaborators here instead of duplicating lifecycle rules in damage sources. |
| `Runtime/Player/PlayerNetworkState.cs`; new partial `PlayerNetworkState.Health.cs` | Separate health from `PublicPlayerState`, retain health/life/claim state, accept owner reports, coordinate server transitions, and expose change events. Keep the existing item-action implementation. |
| New `Runtime/Player/PlayerRagdoll.cs`; new partial `PlayerNetworkState.Ragdoll.cs` | Physical presentation adapter and the single retained root-position stream. |
| New `Runtime/Player/PlayerRevival.cs` | Local held interaction, Give Up progress, rescuer cancellation, shared range/visibility checks, and interaction presentation. Server claims live in the health networking partial. |
| `Runtime/Player/PlayerControlTransition.cs`, `PlayerSeating.cs`, `PlayerCarry.cs`, `PlayerMotor.cs` | Integrate life transitions with existing control revisions, relationship releases, clear placement, suspension, and reset behavior. |
| New `Runtime/Player/PlayerPlacement.cs` | Share capsule clearance/placement primitives currently in `PlayerSeating` between seating, carry, revival, and respawn. Keep existing seating/carry search behavior through thin callers. |
| New `Runtime/Player/PlayerMotor.Health.cs`, existing `.CartContacts.cs` and `.Bouncy.cs` | Forward-simulation damage detection, landing episodes, protection consumption, and accepted cart-launch events. |
| `Runtime/Items/ItemDefinition.cs`, `Editor/ItemDefinitionEditor.cs`, `Runtime/Items/WorldItem.cs`, `Runtime/Player/PlayerItemHitbox.cs` | Definition override and one damage event per item contact episode, independently of impulse batching. |
| `Runtime/Player/PlayerPotionEffects.cs`, `Runtime/Effects/PlayerEffectReceiver.cs`, `PotionArea.cs`, `Runtime/Items/WorldItemRegistry.Effects.cs` | Connect existing effect dispatch to health and suppress effect gameplay while downed. |
| `Runtime/Vehicles/GolfCartController.cs`, `GolfCartNetwork.cs` | Preserve crash cause through incident and ejection transitions; release a downed occupant without a new launch. |
| `Runtime/Inventory/PlayerInventory.cs`, `Runtime/Player/PlayerEquipment.cs`, `PlayerHeldItemPresentation.cs`, `PlayerHandPresentation.cs` | Cancel use, hide equipment, preserve selection and contents, and restore presentation. |
| `Editor/AvatarProcessor.cs`; new `Runtime/Avatars/AvatarRagdoll.cs`; `AvatarInstance.cs`, `AvatarPresentation.cs`, `AvatarPresentationSystem.cs`, `Runtime/Player/PlayerAvatarPresentation.cs` | Process and activate ragdoll physics, handle avatar readiness, suspend pose writers, and render the owner's full body. |
| `Runtime/Player/PlayerPresentation.cs`, `PlayerInputReader.cs`, `PlayerInteraction.cs`, `InputBindings.cs`, root `Assets/InputSystem_Actions.inputactions` | Orbit camera, downed input, Give Up rebinding, and body interactions. |
| `Runtime/UI/HudController.cs`, `InteractionTooltip.cs`, new `HealthVignette.cs` and `ReviveProgressWheel.cs`, `UI/Hud.uxml`, `UI/Hud.uss`, `Runtime/Player/PlayerNameLabel.cs` | Event-driven bars, local damage overlay, rescue timer, progress wheels, and binding-aware prompts. |
| `Runtime/Networking/GamePlayerSpawner.cs` | Initialize health/lifetime and retain assigned spawn information alongside the existing spawn slot. |

The partial classes organize existing network/motor components; they do not add separate network objects or competing report paths. Use MonoBehaviours for the new shared player adapters, with RPCs on `PlayerNetworkState`.

## 1. Define configuration and the health API

Add these settings with nonnegative whole-point damage fields:

| Location | Fields/defaults |
| --- | --- |
| `GameSettings`, health section | Item damage 10; item approach and closing-speed threshold 3 m/s; pedestrian cart damage 25; crash-ejection damage 25; fall damage enabled; landing threshold 8 m/s; landing damage 25. |
| `ItemDefinition`, Player Impacts | `OverrideCollisionDamage` default false and nonnegative integer `CollisionDamage`. An enabled override of zero is valid. |
| `PlayerRevival` | Serialized revive duration, initially 5 seconds. |
| `HudController`, damage feedback | Hit edge opacity 0.35; persistent edge opacity 0.15; fade duration 2 seconds; low-health fraction 0.25. |

Keep `PotionDefinition.HealthStrength` and `Cauldron.BlastDamage` in their existing assets/components. Small/medium/large potion assets already use Pulse with strengths 5/10/15. Do not rewrite those assets or move `GolfCartSettings.LaunchSpeed`/`CrashVelocityChange` (3/7 m/s).

`PlayerHealth` should expose the local/replicated current byte health, normalized health, `IsAlive`/`IsDowned`, health/life events, and an owner-only damaging-hit event. Provide shared entry points equivalent to `ApplyDamage(int)`, instant healing, accumulated continuous healing, and flush/end continuous healing. Sources choose their configured amount; clamping and life eligibility belong here.

- Clamp health to 0..100. Ignore zero damage, healing at the cap, ordinary healing while downed, and additional damage while downed.
- Retain fractional Zone healing until it produces another whole point. Discard overflow at maximum health so it cannot be banked against a later hit. Clear the fractional remainder on downing/respawn; never accumulate healing during downed time.
- Preserve the existing strongest-overlapping-rate and expiry segmentation in `PlayerPotionEffects.SettleHealing`; give it a continuous-healing entry point distinct from Pulse healing.
- Route the existing float-valued blast setting through one explicit nonnegative whole-point conversion at the effect boundary. Initial 25 remains exactly 25; do not truncate every fractional Zone contribution.
- A damaging-hit event fires for each actual health loss, including distinct hits in one physics step. It restarts local feedback and immediately cancels that player's active revive.
- Enter local downed permissions as soon as health reaches zero. Capture pose and incoming/resulting impact motion before disabling movement or releasing relationships.

For runtime fall protection, use source-keyed acquisition/release on `PlayerHealth` so Bouncy and future equipment cannot remove each other's protection. Retain a landing latch if the last source is removed while airborne. A real supporting landing consumes the latch after deciding whether that landing is protected. Active sources continue protecting later landings. Do not use a timed damage immunity or the carry pickup-immunity setting.

## 2. Separate health publication and retain lifecycle state

`PublicPlayerState` currently contains `SpawnSlot`, `Revision`, and float `Health`. Remove its health field and migrate health consumers to `PlayerHealth`/the health snapshot. Keep immutable spawn information separate. Retain assigned spawn position on the server; include it in initialization for an owner that needs it rather than relying on the client motor's `Awake` position after late observation.

Define small payloads with explicit roles:

| Payload | Required content |
| --- | --- |
| Health report/update | Player lifetime, life revision, report sequence, byte current health; report cause sufficient to distinguish damage from healing. |
| Down request data | The zero-health report plus synchronized down time, initial root/pose, and captured velocity. Only include this extra data for downing. |
| Life snapshot/transition | Lifetime, life revision, resulting state, rescue deadline when downed, transition kind, transition health, matching control placement/revision, and initial/final root information as applicable. |
| Revive claim snapshot | Target life revision, claim sequence/token, rescuer identity and lifetime, precise start tick and duration; an explicit cleared state. |
| Root update | Lifetime, life revision, root sequence, sample tick, position, and settled flag. No limbs, health, look, or camera fields. |

Reuse the project's lifetime convention: assign a player lifetime at network initialization and retain it across down/revive/respawn, changing it for a new network lifetime. Share the existing `PlayerPotionEffects.Lifetime` identity where practical instead of inventing independent identities for the same player. A life revision advances for each accepted down/revive/respawn. Ordinary seat/carry changes advance `ControlRevision` without resetting health or life revision.

Implement one reliable owner report RPC and one distribution path for current health. Retain the accepted health record for observation baselines. Life transitions set health through the same internal health installer; their mandatory starting health is part of atomic transition application, not another source of damage.

- Damage and Pulse healing publish changed health immediately. Continuous healing updates the owner immediately and publishes at most every 0.25 seconds, with one final changed-value flush when healing stops, membership ends, health reaches the cap, or eligibility ends. The final flush is the endpoint exception to regular batching.
- Immediate damage publication includes any locally accumulated healing in the current value and supersedes an older pending healing report. Never later send that older value.
- Do not publish unchanged health. Damage of zero produces neither damage feedback nor revive cancellation.
- Apply server reports in sequence within the same lifetime/life revision. Gate prediction/replay at the source and publication boundary. On a host, use the same accept function directly and avoid applying its echo a second time.
- The owner ignores old acknowledgements that precede newer local changes. Life transitions override the old life, clear pending healing, and restart health report sequencing for the new revision.
- For zero health, predict local downing without guessing a new server control revision. Send the down request against the current life revision. Queue root publication until the accepted new life revision is known; keep simulating locally. Confirming the down must preserve that already-moving owner body.
- Old seat/carry placements must not temporarily resume walking during locally predicted downing. Their permission application includes the pending local life state. The server builds accepted down placement from its latest control revision, so a simultaneous seat request cannot overwrite the down.
- Use synchronized FishNet precise tick/fraction timing, following `PlayerNetworkState.ActionAge`. Trust the owner's reported synchronized down time and set the accepted deadline to that time plus 30 seconds. Receipt must not restart the rescue window. A late request already beyond its deadline goes directly through timeout resolution.
- In `OnSpawnServer`, send a coherent retained baseline containing spawn/lifetime, current health/life, claim timing, and latest root position/sequence. Apply life/control context before buffered health/root/claim data. This is the observation snapshot, not a recurring publication bundle.
- Pending newer-revision data waits for its transition/baseline. Drop older lifetimes/revisions. Clear retained buffers, claims, timers, and event subscriptions on network stop or ownership loss, using existing disconnect hooks.

## 3. Make life transitions atomic across control systems

Use one server transition function in `PlayerNetworkState` that checks the expected lifetime/life revision and commits once. Run active rescue deadlines on server ticks independently of `PlayerMotor.TimeManager_OnTick`, which returns early while suspended.

| Trigger | Result | Health | Stamina | Potion state |
| --- | --- | --- | --- | --- |
| Alive reaches zero | Downed at captured body pose; deadline begins | 0 | Preserve | Keep expiry times; suppress gameplay |
| Accepted revive before deadline | Alive at clear body/nearby placement or spawn fallback | 50 | Preserve | Resume only unexpired effects |
| Deadline or completed Give Up | Alive at assigned spawn using clear placement | 100 | Full | Clear temporary effects |
| Below lower world boundary, alive or downed | Immediate spawn transition | 100 | Full | Clear temporary effects |

Terminal actions require the current down revision/claim where applicable. A revive can commit only when its full duration has elapsed and server time is strictly before the rescue deadline. Timeout wins at equality. Processing the first valid terminal transition advances the revision and clears claims, so every other completion is obsolete. Do not add invulnerability.

Extend the application of `PlayerControlTransition` so life state, placement, impact generation, reset markers, item-action reset, and permissions are installed as one logical transition. Keep `ControlRevision`, `ImpactGeneration`, and `ResetRevision` consistent on all participants; do not leave the old motor-only boundary increment as a competing reset path.

Downing order:

1. Capture world pose and motion: walking body velocity including the accepted launch, seated `PointVelocity`, or carrier/preview velocity for a carried player.
2. Cancel item/throw charging, held use, interaction, and revive claims involving the player. Hide held items and first-person hands. Gate new inventory, crafting, seat, carry, horn, and equipment actions on living permissions on both local and server paths.
3. Release cart occupancy and carry relationships. Add explicit life-release methods: ordinary `RequestExit` adds placement behavior, and `BreakForReset` currently uses zero release velocity, so neither is a sufficient downing release. Preserve inherited motion without adding a throw/ejection impulse. Update the surviving carry partner's state and preserve its motion too.
4. A downed driver must be removed from the cart occupant array and trigger the existing driver ownership/baseline handoff. Invalidate pending seat/carry requests referencing old revisions. Do not eject unrelated occupants or clear their belongings.
5. Suspend walking replication/reconciliation, normal root capsule, item hitbox, animation writers, and tick smoothing; start physical ragdoll presentation. The effect receiver becomes ineligible because of life state, independently of seating/carry suspension.

Preserve `serverSelection` and `confirmedSelection` when hiding equipment. `PlayerInventory.SelectedSlot`/`RebuildView` currently mask equipment with `-1`, and `ApplySeatPermissions` clears the stored selection. Lifecycle releases must not call that selection-clearing path merely because the player was a driver. Keep effective equipment visibility separate from retained selection, including unconfirmed selection operations resolved in their original ordering. Already-committed inventory operations remain committed; cancel/roll back pending use according to existing operation acknowledgements.

On return to living, stop ragdoll physics and apply the final placement before restarting the walking smoother. Clear previous-body interpolation, impulses, contact episodes, queued actions, and obsolete root samples. Restore the normal capsule, permissions, selected equipment, first-person hands, and camera. Set an upright idle animation immediately. Revival must not reset stamina or effect expiry even when its placement falls back to spawn.

Move lower-boundary handling from `ReplicateMove` into lifecycle orchestration. Read the living physical/attachment position or the downed root, never a stale suspended motor position. The owner requests immediate boundary respawn; the server can also detect it from current retained position. Both paths use the same revision-checked transition. Reset stamina and its recovery delay explicitly on respawn.

Extract capsule geometry and clearance into `PlayerPlacement`. For revival, try a standing capsule centred over the body root at the correct ground height, then a bounded nearby search (up to 2 metres), then the assigned spawn search. Do not reuse the current `TryExitNear` search unchanged around a body: it can search out to 16 metres. Preserve the existing spawn clear-placement/pending retry behavior if the spawn area is occupied, with gameplay disabled until clear. A pending placement must not extend a rescue deadline; transition kind remains revival or respawn as already decided. Exclude the player's own disabled ragdoll and query shapes from clearance.

## 4. Connect damage and healing at their existing acceptance points

### Items

`WorldItem.ContactEligible`, `SweepContact`, `ReportImpact`, and host `OnCollisionEnter` already supply sphere contacts and pre-impact velocity. Preserve the existing shove eligibility (`!DontPushPlayer`, positive impulse multiplier, enabled non-trigger sphere, valid awake world item, and `MinimumImpactSpeed`).

- Compute `approach = dot(itemVelocity, intoPlayer)` and `closing = dot(itemVelocity - playerVelocity, intoPlayer)`. Require both to be at least the shared 3 m/s damage threshold, in addition to existing shove eligibility. A stationary awake item fails approach speed.
- Resolve damage as the enabled definition override or shared 10. Expose the override value conditionally in `ItemDefinitionEditor`, including multi-object editing and explicit zero. Do not change item IDs, stack behavior, or simulator ownership.
- Share the contact episode between host collision and swept/presented detection. Multiple callbacks, sphere samples, or corrections for the same ongoing contact must not reapply damage. Rearm on genuine separation, not presentation correction or prediction replay. Keep the episode until separation even if contact slows or the item sleeps.
- Emit each accepted item's damage before combining vector impulses in `PlayerItemHitbox`. Keep damage events separate even when the total impulse is zero. Do not condition health loss on `SubmitWorldImpact` returning a nonzero request ID.
- Carry the accepted impact motion into a lethal ragdoll transition even if downing prevents the pending movement impulse from being flushed. Resolve this through the common captured-motion interface, not an extra post-death shove.

### Pedestrians and riders

In `PlayerMotor.CartContacts.AfterContactPhysics`, record damage for each newly accepted cart launch/contact episode. Keep the current launch threshold, exit-grace, generation, and separation rules. Because `CartContactSet.Launched` is reconciled/replayed, maintain the owner's damage-consumed episode bookkeeping outside the rewindable state. Only forward owner physics can emit damage, and replay cannot rearm it. A later true separation/new launch can damage again without a cooldown.

In `GolfCartController.AfterPhysics`, capture whether `collisionSeverity >= CrashVelocityChange` caused the incident. Thread that flag through `ReportIncident`, `ServerIncident`, `QueueIncident`, `PendingChange`, and `CreateExit` into the accepted rider transition. Merge pending incidents without losing a qualifying crash flag. Rollover/recovery alone must not set it.

Apply the configured crash damage once on the affected owner's accepted live ejection event. Clear the one-shot flag in retained/captured placement snapshots and retries so a late observer or delayed clear placement cannot reapply it. Apply the ejection's inherited motion and launch to the lethal ragdoll seed even when damage reaches zero. Non-crash ejection, ordinary exit, carry throw/release, and Bouncy launch do not call damage.

### Landings

Add landing handling to the motor's existing pre/post-physics hooks and collision callbacks; do not declare a second conflicting `OnCollisionEnter` in a partial file. Separate environmental contact gathering from cart contact gathering.

- Gather support contacts only on the named Ground and Environment layers (not vehicles, players, or items). Require an upward-facing support normal, initially `dot(normal, up) > 0.5`, and contact on the supporting portion of the capsule. Wall and ceiling impacts fail this test.
- Capture incoming player velocity before solver impulses, including forces applied for this physics step. Capture moving support linear/angular motion before the same step and use velocity at the contact point. Use the collision callback's pre-solver relative velocity where available; do not substitute a support velocity already changed by the solver.
- Landing severity is `max(0, -dot(playerIncoming - supportIncomingAtContact, supportNormal))`. Use the largest qualifying supporting contact for one landing event. Strictly above 8 m/s deals exactly 25, regardless of fall distance or higher speeds.
- Rearm only after leaving support. Do not use the motor's broad ground SphereCast as proof of a new landing; it can become grounded before collision. Contact bookkeeping and damage consumption must not rewind with reconciliation.
- Resolve fall protection before damage and consume an expiry latch after that landing. This must work for a Bouncy rebound in the same simulation step; latch handling follows the landing event rather than a later `Grounded` poll.
- Route all landing damage through `PlayerHealth` outside replay. Reset landing bookkeeping at explicit life placements to avoid a teleport creating a landing hit.

### Potions and failed brewing

Replace `PlayerPotionEffects.ApplyHealing`/`ApplyDamage` TODOs with the shared health API. Preserve `PotionArea` pulse deduplication and the strongest overlapping Zone rate; flush final continuous healing when the last eligible membership ends, expires, or is removed.

Make `PlayerEffectReceiver.Eligible` require living health while retaining its separate attachment-compatible receiver collider. Explicitly refresh eligibility at down/revive/respawn so zone membership does not linger or charge time spent downed. Keep the healing time cursor current while ineligible; reseeding on revival starts from the revival time, with no catch-up healing.

Keep active dose expiry timestamps and `BuffChanged` expiry presentation running while downed. Suspend Bouncy movement/protection application without calling `ResetEffects`; on revival reinstall only the unexpired dose and no old bounce sequence. On respawn advance `EffectReset`, clear current doses, fractional healing, and obsolete area memberships, then allow fresh effects at the new position. Potion-owned protection is released without clearing unrelated equipment-owned protection.

`WorldItemRegistry.Effects.ApplyBlast` already deduplicates receivers and preserves radius/occlusion, and crafting replay/snapshot dispatch guards the blast. Keep those rules. Apply 25 once through its explicit effect call; keep the shove health-neutral. Capture the existing eligible blast shove before a lethal hit disables movement so the ragdoll preserves it. Seated/carried alive receivers still take blast damage; their existing attached shove behavior remains intact.

## 5. Extend Process Avatar and implement physical presentation

Extend `AvatarProcessor.PrepareModel` and the existing Process Avatar workflow; do not add a migration menu. Generate ragdoll components after the existing component-disabling pass, and only on full-body processed prefabs. Leave generated first-person arm prefabs without ragdoll physics.

`AvatarRagdoll` stores generated rigidbody, collider, joint, and rest-pose references. Use Humanoid bone mappings and measured bone lengths for a pelvis, torso, head, upper/lower arms, and upper/lower legs, adding hand/foot bodies where needed by the mapped skeleton. Give the chain sensible fixed joint limits, scaled capsule/box dimensions, and mass distribution. All generated colliders start disabled and bodies kinematic. Disable adjacent self-collisions explicitly. Do not repurpose VRM spring-bone colliders as rigidbody shapes.

Add a named `PlayerRagdoll` layer through Unity project settings. Enable collision with Ground, Environment, and GolfCart, disable physical collision with Player and PlayerItemHitbox, and exclude irrelevant query/held/effect layers. Keep the layer queryable for revival but out of ordinary item-pickup obstruction tests. Include generated physics in Process Avatar's normal content contract so incomplete processed content identifies the need to reprocess, without creating a separate validation tool.

`PlayerRagdoll` binds/unbinds through `AvatarPresentation.DidBind`/`WillUnbind` and guards pending activation by player lifetime, life revision, and avatar binding generation.

- At downing, call the existing full-body presentation path for the owner as well as observers. `SetVisual(false)` currently releases instances, so the owner's avatar must be prepared when first shown. A delayed bind must activate the latest downed state; a bind after revival must not resurrect an old ragdoll.
- Keep a small physical body-root proxy on the player prefab for the brief interval before the full-body avatar is ready. It has its own Rigidbody and simple collider on the ragdoll layer, is normally inactive, and starts at the captured pelvis estimate with inherited velocity. It supplies the same root API and stream while loading, so camera following, world-boundary detection, and revival remain usable. On bind, align the generated pelvis to that root, transfer motion, and disable the proxy. Do not add a second network stream for this handoff.
- Sample the current standing/seated/carried pose once before enabling limb physics. Preserve the corresponding owner full-body pose using `CurrentPlacement`; do not start a seated death from an unrelated standing pose.
- Suspend every writer: animation graph evaluation, `AvatarInstance.Place`, Humanoid IK, hand targets/finger poses, VRM constraints/look processing, native springs, and `AvatarPresentation.UpdateInput` transform placement. `SetFeatures(false, ...)` alone is insufficient because the host still moves its transform and candidate warmup/commit can evaluate poses.
- Detach the physical avatar from the moving graphical parent while active, retaining its normal parent/local pose for restoration. Moving the logical player root or nameplate must not translate physical limbs a second time.
- The owner simulates the whole ragdoll. Observers simulate limbs locally with a pelvis that follows the interpolated owner root. Do not force a remote pelvis back to its locally simulated position or send its limb transforms.
- Prevent ragdoll physics from advancing during other objects' prediction replays. Use the existing FishNet `OfflineRigidbody`/`RigidbodyPauser` support around the cached generated bodies, separate from the player's walking-body pauser. Refresh its cached body set after binding; do not modify FishNet package code.
- On revival/respawn, disable physical colliders, stop velocities, restore parent/rest transforms and ordinary pose writers, then immediately sample idle at the accepted placement. Restore the owner's first-person presentation and release its downed full-body instance through the existing visibility lifecycle.
- Defer avatar-selection requests while downed, keeping only the latest requested identity. Also defer an in-flight candidate commit/identity arrival so it cannot replace an active physical body. Apply the latest selection once alive. Keep menus usable.

## 6. Publish a single ragdoll root stream

Keep root replication separate from health and walking prediction. `PlayerRagdoll.RootPosition` is the owner pelvis/proxy position locally and the interpolated retained root on observers. The server uses the latest accepted root for revival placement and world-boundary checks without requiring an avatar instance.

- Send routine owner root updates no more than once every 0.1 seconds and only after moving at least 0.05 metres from the last transmitted position. Position is sufficient; limb pose and orientation remain local after the initial pose seed.
- Use the same root RPC path with unreliable routine samples and reliable terminal settled samples. Retain the newest accepted sequence on the server. A delayed routine packet must not overwrite a newer settled sample.
- Detect settlement from cached rigidbody sleep/quiet motion. Send one final reliable position even when displacement is below 5 cm, respecting the 10 Hz cap by deferring at most until the next eligible send slot. Include an explicit settled state so observers end interpolation exactly at that position.
- Do not repeatedly send the same settled state. Resume movement checks when physics wakes/moves; the next routine update still obeys the distance threshold. A subsequent settle after sub-threshold movement still gets a final changed position.
- Interpolate observer root samples and move the remote pelvis in the physics step while local limbs settle around it. Do not run walking smoothing/reconciliation against this same presentation. Camera/nameplate/interaction use this shared result rather than each implementing a positional buffer.
- The accepted down transition provides the initial root. During local prediction the body keeps moving while its first publication waits for the server's life revision. Do not snap the owner to the older initial root when confirmation arrives; publish the current root under the accepted revision.
- On revive/respawn, reliably publish the life transition with its final placement and invalidate the previous root revision before restoring walking. Retain the alive baseline, clear pending root interpolation, and ignore root packets from the previous down.
- New observers receive the latest retained root, settled state, and matching life/claim timing as one baseline. Do not rely on an unbuffered last movement packet or idle heartbeat to populate joining clients.

## 7. Add held revival and server claims

Keep normal interactions in `PlayerInteraction`, but resolve ragdoll colliders to their owning `PlayerRevival` explicitly. The current player target is a root `PlayerCarry` collider and `GetComponentInParent<IInteractable>`; avoid putting ambiguous competing interactables on that same root. A cached ragdoll-owner mapping lets both proxy and generated-body colliders produce the revive target without per-frame hierarchy searches.

Expose the existing `pickupRange` (3 metres) for shared revive geometry. Initial targeting remains the current aim ray and obstruction query. Resolve a downed collider to its shared root and show `Revive` with `Player/Interact`. Make a claimed target displayable as `Being revived` even though another start request is disallowed; the current tooltip requires `CanInteract`, so separate target visibility from start eligibility for this case.

When Interact begins on an eligible body:

1. Cache the target, target life revision, current rescuer lifetime/revision, and a new local attempt sequence. Request a claim on the target through the rescuer's owned network component. Start requests must not depend on ownership of the victim.
2. Server checks that both players exist and are alive/downed as appropriate, the rescue deadline has not passed, and no rescuer owns the target claim. Use current shared body position and unobstructed range; trust owner-reported geometry where current client simulation differs. Do not introduce server movement rewinds or anti-cheat checks.
3. Grant exactly one claim, with a token and precise server start tick plus the configured duration. Retain/broadcast that timing. Do not shorten the duration by the request travel time or extend the rescue deadline. Suppress other item interactions while awaiting/granted, but retain movement and look.
4. The rescuer checks held Interact, range to the current shared root, and line of sight from its current interaction origin throughout the attempt. After starting, aim direction is irrelevant. Ignore its own colliders, the target's ragdoll, and trigger-only/query shapes in the obstruction check; real environment/vehicle obstruction cancels.
5. Release, range loss, obstruction, actual damage, downing, target leaving downed state, unavailable participants, menu/focus/controller interruption, or network stop cancels and resets to zero. Cancel item use at start and block pickup, drops, hotbar/inventory mutation, potion use, crafting, seat/carry requests, and other item interactions until the attempt ends. Do not block locomotion or look.
6. Server accepts cancellation only for the matching attempt/claim. Clear the claim on disconnect/despawn/ownership loss, and cancel claims on the server's accepted rescuer damage report. A delayed cancel from rescuer A must not clear rescuer B's later claim.
7. At the end, the owner sends completion only while its held input/geometry are still valid. Server requires the matching claim, elapsed duration, current life revision, eligible participants, and `now < rescueDeadline`, then uses the atomic revive transition and placement rules. It must not auto-complete a claim solely because a timer elapsed after the rescuer disappeared.

Use reliable request/cancel/complete messages and retained start/end events. Do not send per-frame or per-second progress or input heartbeats. A pending claim whose local input was already released must immediately cancel if its grant arrives later. A player cannot hold two active rescue claims; cache the rescuer's active target for direct cleanup.

## 8. Add downed input and orbit camera

Add `Player/GiveUp` as a dedicated Button action in `Assets/InputSystem_Actions.inputactions`, using new stable action/binding GUIDs and the existing groups. Defaults are `<Keyboard>/space` and `<Gamepad>/buttonSouth`. Register it in `InputBindings` so the current controls-remap UI and saved binding overrides include it. Do not reuse the Jump action as the semantic Give Up action.

Extend `PlayerInputReader` with held Interact and downed input handling:

- Distinguish session/menu input availability from living gameplay permissions. Do not call `SetGameplay(false)` merely for downing, because it disables the entire Player action map, including Look and Give Up. Keep menu/UI actions available and gate living action dispatch/`Consume` instead.
- Cache Give Up and Look references alongside the current actions. Downed input updates orbit angles from the existing mouse delta/right-stick sensitivity handling but dispatches no movement, sprint, jump, use, drop, interaction, vehicle, or inventory action.
- Require a fresh press after entering downed state if Space/A was already held. Give Up accumulates only while continuously held and input is available. Release or menu/focus/input interruption resets it. After two seconds, submit one revision-tagged Give Up request and wait for the resulting respawn transition; continued holding does not resubmit.
- Use the existing context clearing/release gating on revival/respawn so Give Up does not become an immediate jump and held Interact does not pick up an item after revival ends. Subscribe active hold cancellation to `InputPresentation.Interrupted`.
- Keep revive-held state outside target refresh: `RefreshTarget()` currently clears the aimed target every frame, and `InteractPressed` only reports the initial edge.

In `PlayerPresentation`, add a downed camera branch driven by `PlayerRagdoll.RootPosition`. Reuse the existing local Camera/AudioListener. Use a fixed normal orbit distance of 3 metres, a modest upward target offset, and a sphere cast against Ground/Environment/GolfCart to shorten the distance when obstructed. Use a radius that protects the near clip plane, exclude the player's own body, and handle the target starting near a wall. Restore normal distance as the obstruction clears; do not move the ragdoll to solve camera obstruction.

Camera tracking continues every frame while downed, including when the root moves without input or the rescue claim changes. Looking uses local orbit yaw/pitch only; suspend the player's recurring avatar-look messages while downed. On revival/respawn switch directly back to first person after applying the accepted placement. Do not blend from an obsolete death-camera/body position.

## 9. Implement health and rescue presentation

### Local health and vignette

Move health-bar updates out of the inventory-only `HudController.Refresh` dependency. Bind/unbind health and life events in the existing `Bind` method, and initialize from the current local snapshot at bind. Remove `health-text` from `Hud.uxml`, its backing field/assignment, and orphaned health-number styling. Keep existing bar sizing/colour.

Implement `HealthVignette` as a UI Toolkit `VisualElement` with a cached edge mesh whose centre vertices have zero alpha. Generate geometry on size/geometry changes and redraw only while its fade or persistent visibility changes. Use vertex alpha for a continuous edge fade; four opaque rectangles are not a vignette. Set picking mode Ignore and place it behind interactive HUD/menu content.

- Actual local damage starts/restarts a fade at configured hit strength. Do not sum hit opacities.
- Over two seconds, blend from hit strength toward 0.15 only while current health is strictly below 25/100; otherwise toward zero.
- Healing across the threshold removes the persistent component immediately while any remaining hit fade can finish toward zero. At exactly 25 there is no persistent vignette.
- Downing hides and clears the active flash. Remote health changes and late baselines do not trigger a local flash. Stop scheduled updates once static or hidden.

### Nameplate bars

Add a thin background/fill pair beneath the existing TMP name text on `Player.prefab`, using two child SpriteRenderers with one shared solid-white sprite, tinted to the existing HUD palette, without colliders. Scale/anchor the fill from the left by normalized health. Keep visibility, billboard orientation, and placement under `PlayerNameLabel`, including at full health; the local player's nameplate stays hidden.

Subscribe to health/life events for fill changes. While downed, position the label/bar above the shared ragdoll root instead of an independently simulated head or stale walking Graphics transform. When alive, preserve `TryGetNameAnchor` placement. Cache camera/reference changes at binding/ownership transitions rather than extending the current per-frame camera lookup.

### Timers, prompts, and wheel

Add a downed rescue overlay and a shared `ReviveProgressWheel` element to the existing HUD document:

- Downed player: remaining rescue seconds, binding-aware Hold Give Up prompt, and local Give Up hold progress. During revival, keep the rescue timer visible beside the revive wheel.
- Rescuer and victim: a wheel filling from `(sharedNow - start) / duration`, clamped to 0..1, with remaining revive seconds inside. The rescuer uses the target's claim state; the victim uses its own.
- Others: name plus empty health bar, with `Revive` or `Being revived` only when targeting the body.

Use `InputPrompt` and the existing device/binding-change events for the actual Interact/Give Up glyphs. Update countdown text only when the displayed second changes, and schedule wheel updates only during an active attempt. Hide/reset progress immediately on cancel or life revision change. At visual completion, keep the UI awaiting the authoritative transition rather than inventing a successful revival locally.

Close inventory and cancel drag/charged-use UI on downing; hide crosshair and ordinary interaction/hotbar action hints as appropriate. Ensure the new downed overlay is not skipped by `HudController.LateUpdate`'s existing `GameplayActive` early return. Menus can overlay it while rescue time keeps advancing.

## 10. Apply Unity asset changes

Perform these through supported Unity CLI operations, falling back to Unity MCP or giving the user exact manual editor steps if the operation is unavailable. Do not write `.meta` files or introduce a one-time setup tool.

1. `Assets/Game/Prefabs/Player.prefab`: add `PlayerHealth`, `PlayerRagdoll`, and `PlayerRevival`; assign the existing GameSettings and required cached collaborators. Add the normally inactive ragdoll loading proxy with Rigidbody/collider and the nameplate bar children. Reference the health adapter from existing collaborators as required. Keep the existing network object and scene placement.
2. `Assets/Game/Settings/GameSettings.asset`: serialize the shared defaults listed in section 1. Configure the current HUD component's new serialized vignette defaults through the prefab/asset that owns it; field initializers suffice where no asset override is required. Do not edit the game scene solely to repeat defaults.
3. `ProjectSettings/TagManager.asset` and `DynamicsManager.asset`: create/configure the named ragdoll layer and collision matrix through Unity. Update relevant query masks in code without changing unrelated collision pairs.
4. Supply one shared solid-white sprite for remote bars, reusing an appropriate existing asset if available. No scene-specific copies, camera prefab, vignette texture, or wheel texture are needed.
5. Reprocess every registry avatar through `Two Birds > Process Avatar` (the existing `AvatarProcessor.Process` entry point). Generated full-body prefabs under `Assets/Game/Prefabs/Avatars` gain ragdoll bodies/joints and replay-pausing support. Preserve avatar IDs, registry references, hand settings, and first-person assets through the existing workflow.
6. Update `Assets/InputSystem_Actions.inputactions`, `Assets/Game/UI/Hud.uxml`, and `Hud.uss`. Keep existing binding IDs intact; only Give Up receives new IDs.

No inventory item migration, spawn marker changes, player respawn instantiation, new game scene objects, per-limb network components, or new player tuning menu are required.

## Implementation order and completion criteria

Execute sections 1–3 first so every subsystem can call stable shared health and transition APIs. Then implement damage/effects (4), ragdoll authoring and motion (5–6), interaction/input/camera (7–8), UI (9), and Unity preparation (10). Carry source motion into the down transition from the outset; adding ragdolls later must not require a second damage protocol.

The feature is complete when all specification behaviors are implemented and required prefab/avatar/input/UI assets are prepared, with one health report path, one root stream, and no health in walking replication. Do not leave setup TODOs disguised as runtime fallback behavior. If Unity asset operations require manual user action, enumerate the exact remaining editor steps rather than claiming the feature is complete.

## User visual acceptance checks after implementation

Use a host and guest, then add a third player for competing rescues and join an additional client while a body is already downed. Reverse host/guest roles for impacts and revival.

| Check | Expected result |
| --- | --- |
| Local/remote bars | Full-health bars remain visible; no health numbers. A default item hit removes 10% of the bar. Only the victim sees the red vignette. |
| Vignette | Repeated hits restart the same-strength flash. Below 25 health it fades to persistent red; at 25 or above persistence disappears. Downed orbit view is clear. |
| Item episodes | Walking into a stationary awake item, ordinary drops, and sustained contact are harmless after any initial accepted hit. Separation/new impacts can hurt again. Two opposing simultaneous damaging items remove health independently. |
| Item settings | Shared default, nonzero override, and explicit zero produce the corresponding bar changes. Zero still shoves and permits later landing damage. Shove-disabled items do no direct collision damage. |
| Cart impacts | A qualifying pedestrian launch and major crash ejection each remove 25%. Resting contact does not repeat damage. Ordinary exit and rollover-only ejection have no launch damage. |
| Landings | Ordinary jumps are harmless. Hard ground/slope/moving-support landings remove exactly 25%, even after an item/cart/blast hit. Walls/ceilings do not. Very high falls still remove only 25%. |
| Protection | Bouncy protects landings while item/cart/blast damage continues. Midair expiration protects the next landing once; a later unprotected hard landing hurts. |
| Potions/blast | Small/medium/large health potions restore 5/10/15 points, capped at full. Overlapping health Zones use the strongest rate. A failed brew removes 25 once with original range/occlusion, including seated/carried players. Downed players cannot be healed by these effects. |
| Downing contexts | Down while walking, falling, seated, carrying, being carried, and charging an item. Motion continues into the body; relationships release, inventory/selection remain, and hands/equipment hide. Living players pass the ragdoll; ground and carts collide with it. |
| Avatar and camera | Owner sees its full-body ragdoll; mouse/right stick orbit and follow sliding/falling motion without wall clipping. Down during avatar preparation and request another avatar while downed; no body reset or obsolete activation occurs. |
| Revival | Hold the actual Interact binding for five seconds. Both participants see progress; rescuer can look away/move within range. Release, obstruction, range loss, damage, interruption, or downing resets it. A competing rescuer sees Being revived and cannot accelerate it. |
| Claim cleanup | Disconnect either participant or cancel and switch rescuers. The new rescuer starts at zero; delayed messages do not cancel/complete the wrong attempt. |
| Revive placement | Finish before the deadline: return upright immediately at the body, nearby if blocked, or spawn if necessary, with half health and preserved stamina/unexpired buffs. No brief old-body snap or refill occurs. |
| Deadline/Give Up | Starting near timeout does not extend it. Releasing Give Up early cancels; a fresh continuous two-second hold respawns. A held jump at death is not an automatic Give Up. Menus work without pausing rescue time. |
| Respawn/boundary | Timeout, Give Up, and lower-boundary crossing restore assigned clear spawn, full health/stamina, retained inventory/selection, and cleared potion buffs. Crossing while downed does not leave a rescue body below the world. |
| Observation/order | Join during moving/settled ragdoll or active revive: body root, empty bar, deadline, and progress agree. Revival/respawn under latency never returns anyone to an old body, seat, life state, or health value. |

If the user additionally requests network validation, inspect existing transport/profiler facilities for health-on-change reports, continuous-heal batching, the 10 Hz/5 cm root policy and final settle sample, no settled heartbeat, and suspended walking traffic. Do not add a diagnostics subsystem solely for this feature.
