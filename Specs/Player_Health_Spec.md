# Player Health Specification

## Purpose and scope

Give players responsive, networked health with item, golf-cart, failed-brew, and fall damage; healing; local damage feedback; remote health bars; ragdoll death; cooperative revival; and respawning.

Prioritize immediate feedback on the affected player's client, consistent health and life state across clients, and minimal network traffic. Trust client-reported simulation. Health changes must not add recurring data to movement synchronization.

## Domain

| Term | Meaning |
| --- | --- |
| Owner | The client controlling the affected player. It computes that player's health changes and simulates their ragdoll position. |
| Downed | A player at zero health, represented by a ragdoll and eligible for revival during a rescue window. |
| Revival | Another living player completes an interaction that restores the downed player to 50% health. |
| Respawn | Return to the player's assigned spawn position with full health and stamina and cleared potion effects. |
| Contact episode | A qualifying impact followed by continuous contact. Separation allows a later impact to begin another episode. |
| Item definition | The existing `ItemDefinition` settings asset shared by items of that definition, including shove eligibility and an optional collision-damage override. |
| Root position | The owner-reported body position shared by ragdoll presentation, camera tracking, and revival. Individual limb poses are locally simulated. |

## Health and healing

- Maximum health is 100. Players begin at full health, and health remains between zero and 100.
- Health uses whole points. Continuous healing retains fractional accumulation until it contributes another whole point.
- There is no passive health regeneration. Healing requires an explicit source.
- Small, medium, and large health potions restore 5, 10, and 15 health respectively. Healing stops at maximum health.
- Preserve Pulse and Zone effect behavior, including the existing strongest-overlapping-rate rule for health Zones. Connect those effects to actual health changes.
- Failed brewing deals 25 damage to eligible players in the existing blast area, preserving its range, occlusion, and effect-receiver rules. Seated and carried players remain eligible while alive.
- A failed-brew blast applies its explicit damage once. Its initial shove does not add another collision-damage event. A later dangerous landing can still hurt.
- Downed players ignore further damage and ordinary healing. Health potions cannot revive them.

## Damage rules

Damage is fixed per qualifying event. Impact speed determines eligibility, not damage amount.

| Source | Initial damage | Trigger |
| --- | --- | --- |
| Shove-enabled item | 10, unless its item definition overrides it | Existing shove eligibility plus the item-damage speed threshold |
| Cart hitting a pedestrian | 25 | The existing qualifying pedestrian launch/contact episode |
| Crash ejecting a rider | 25 | A qualifying major crash that causes ejection |
| Hard landing | 25 | Impact speed into an eligible supporting surface exceeds 8 m/s |
| Failed-brew blast | 25 | Existing blast eligibility |

### Item impacts

- All shove-enabled item definitions, including rocks, basketballs, and mushrooms, are eligible for collision damage. Items with shove disabled do not cause collision damage.
- Use one shared item-damage default of 10. Each item definition exposes an optional nonnegative, whole-point damage override.
- An unset override inherits the shared default. An explicit zero preserves shove while disabling that item's direct collision damage. It does not protect the victim from a later damaging landing.
- Require both the item's approach speed toward the player and its relative closing speed to reach the configurable 3 m/s damage threshold. Preserve the existing shove rules independently.
- Ordinary drops and walking into nearly stationary items must not become damaging merely because the items are still awake.
- Apply damage once per qualifying contact episode. Sustained contact does not repeatedly damage the player. A separate impact after separation can hurt again, without an additional cooldown.
- Distinct item impacts remain distinct damage events even when their movement impulses are combined. Opposing impulses must not cancel their damage.

### Golf-cart impacts and ejections

- Pedestrian damage follows the accepted cart launch/contact episode, including the existing speed threshold and contact rearming rules. A cart resting against a player does not cause continuous damage.
- Preserve the existing pedestrian launch threshold, initially above 3 m/s, and the existing major-crash threshold, initially 7 m/s of crash severity.
- Qualifying crash ejections deal 25 damage regardless of severity above the threshold. Preserve whether an ejection was caused by a qualifying crash so rollover-only and ordinary ejections remain harmless at launch.
- Ordinary exits, rollover-only ejections, and other non-crash ejections cause no immediate damage.
- Throwing or releasing a carried player causes no immediate launch damage. Bouncy launches and rebounds likewise cause no immediate damage.
- Dangerous landings after any of these launches can cause fall damage independently of the initial event.

### Fall damage and protection

- Fall damage is enabled by default and can be disabled through configuration and per-player runtime protection from potion or item effects.
- Apply fall damage only on hard landings against supporting surfaces on the ground/environment collision layers. Wall and ceiling impacts do not cause environmental damage.
- Measure pre-impact relative speed into the supporting surface, accounting for its motion and contact direction on slopes. Do not use distance fallen as the damage trigger.
- A landing above 8 m/s deals 25 damage. Both values are configurable. Extremely high falls still deal the same fixed amount.
- Apply the damage once for the landing, not repeatedly while grounded. Ordinary jumps remain below the default threshold.
- Active Bouncy protection suppresses fall damage while incoming item, cart, and blast damage remain enabled.
- If protection expires or is removed while airborne, retain it through the next landing, then remove it. This applies to Bouncy and future equipment using the same protection mechanism.

## Life state and belongings

Reaching zero health immediately enters the downed state and begins a 30-second rescue window.

- Suspend normal locomotion and gameplay actions and show the player's ragdoll, including a full-body avatar for the owner.
- Hide equipped items and first-person hands, cancel active item use and charged actions, and preserve inventory contents, quantities, and selected slot. Do not drop inventory items as a death penalty.
- Release seating and player-to-player carry relationships immediately. Preserve existing motion without adding a throw impulse for the release.
- Downed bodies cannot be picked up or carried. Their ragdolls collide with the environment and vehicles without blocking living players.
- Existing potion timers continue to expire normally. Suppress their gameplay effects while downed. A successful revival resumes effects only for any duration still remaining; it does not restart their timers.
- Preserve the stamina held when the player became downed. Revival does not refill it.
- Restore ordinary equipment presentation and gameplay permissions when the player returns to the living state.
- Revival and respawn grant no temporary invulnerability.

## Death camera and input

- The owner sees a third-person orbit camera following the moving ragdoll. Following continues as the body slides, falls, or moves on a slope.
- Mouse movement and the controller right stick control the orbit. Use a fixed normal orbit distance with obstruction handling that keeps the camera out of walls.
- Disable movement, item use, and other gameplay actions while downed. Keep menus available and defer avatar changes until the player is alive.
- Add a dedicated, rebindable Give Up action, defaulting to Space on keyboard and A/controller south. Giving up requires holding it continuously for two seconds.
- Releasing Give Up before completion cancels that attempt. Completing it respawns the player.
- Return to the ordinary first-person camera immediately on revival or respawn.

## Revival

### Eligibility and interaction

- Any living player can revive another player without an item requirement or consumable cost.
- Target a downed body within the existing three-metre interaction range. Replace its normal player pickup action with a Revive tooltip showing the actual interaction binding.
- Start by aiming at the body and holding Interact, default E/controller Y. Require an unobstructed path.
- Completing a revive requires five continuous seconds by default. Expose the duration in the Inspector.
- After starting, the rescuer may look away and move while maintaining range, an unobstructed path, and the held interaction input.
- Block the rescuer's other item interactions during revival.
- Cancel if the rescuer releases Interact, leaves range, loses the unobstructed path, takes damage, or becomes downed. Cancellation resets progress completely.
- Allow one active rescuer per downed player. Other rescuers cannot accelerate progress. After cancellation, another rescuer can start from zero.
- The server resolves competing claims and releases an active claim when its participant is no longer available.

### Deadline and completion

- Starting a revive does not pause or extend the 30-second rescue deadline. The entire revive must complete before that deadline.
- A successful revive restores 50% health and immediately returns the player to an upright idle pose without a recovery animation delay.
- Place the player at the body's position when the standing capsule fits. Otherwise try nearby clear positions.
- If no nearby position fits, restore the player at their assigned spawn position. This remains a revival: restore 50% health, preserve stamina, and resume any remaining potion duration.
- Complete each life-state transition once. A timeout, Give Up, and revive completion must not produce conflicting outcomes across clients.

### Rescue presentation

- Show the local downed player the remaining rescue time.
- Show both the rescuer and downed player a wheel that fills with revive progress, with the remaining revive seconds inside it. Keep the downed player's rescue timer visible during revival.
- Other players see the downed player's name and empty health bar. Targeting a body with an active rescuer shows Being revived.
- Progress and countdowns derive from shared timing state rather than networked per-frame or per-second UI updates.

## Respawn and world boundary

- Rescue timeout and completed Give Up return the player to their assigned spawn position.
- Crossing the existing lower world boundary immediately respawns either a living or downed player. Do not leave an inaccessible body waiting for rescue.
- Respawn restores full health and stamina, clears temporary potion effects, and retains inventory contents and selection.
- Use the existing player object and spawn-placement behavior, including clear placement. Clear obsolete carry, seating, movement, and ragdoll state as part of the transition.
- A revived or respawned player must not be returned to an obsolete body position or life state by delayed movement or health messages.

## Health presentation

### Bars

- Drive the existing local health bar from health changes.
- Show a health bar directly beneath each remote player's name, above their avatar. Follow the nameplate's visibility and positioning, including while at full health.
- Display bars only: no numeric health values on local or remote players.
- A downed player's remote nameplate follows their ragdoll and shows an empty health bar.
- Keep bar styling consistent with the existing HUD and nameplate presentation.

### Local damage vignette

- Only the affected local player sees damage feedback.
- Each damaging hit starts or restarts a red edge vignette, leaving the centre clear. Default maximum edge opacity is 35%.
- Fade over two seconds after the hit. Repeated hits restart the fade from the configured flash strength without accumulating unlimited opacity.
- While health is strictly below 25%, fade back to a persistent vignette with 15% maximum edge opacity. At or above the threshold, remove the persistent effect.
- Hide the vignette while downed so the orbit view remains clear.
- Expose hit strength, persistent strength, fade duration, and low-health threshold in the Inspector.

## Networking and consistency

### Health and life state

- The owner computes and immediately applies their damage and healing. The server trusts those reports, distributes current health, and coordinates revive claims, deadlines, and life-state transitions.
- Encode the whole-point health value in one byte. Send the current value when it changes; health is not a recurring field in movement updates.
- Report damage immediately. Batch continuous healing reports to at most four per second and send the final changed value when that healing ends. Local health and feedback update immediately.
- Do not send unchanged health. Keep health publication separate from unrelated spawn or presentation fields so a health change does not resend those fields unnecessarily.
- Use one health-reporting and distribution path. Do not apply damage again during movement prediction replay, reconciliation, or repeated collision callbacks.
- Retain current health, life state, rescue deadline, and active revive timing for clients joining or beginning to observe a player.
- Coordinate transitions using shared timing and the existing revision/lifetime conventions so obsolete reports cannot undo revival or respawn.

### Ragdoll motion

- The downed player's owner simulates the authoritative body position. Other clients simulate limb physics locally while following the owner's root position. Exact agreement between limb poses is not required.
- Preserve motion on entering ragdoll so impacts, falling, and seat/carry release continue visibly.
- Use one root-position stream, capped at ten updates per second. Send routine updates only after the position has moved at least 5 cm from the last transmitted position.
- Send a final settled position even if the remaining movement is below that distance threshold. Send no recurring messages while settled, and resume updates when movement resumes.
- Retain the latest root position so joining clients can present an already-moving or settled downed player.
- Share the same root-position source across camera tracking, revival, and remote presentation. Do not add separate camera, interaction-position, or limb synchronization streams.
- Suspend normal walking replication and reconciliation while ragdoll motion controls the player. End the ragdoll stream when the player revives or respawns.
- Discard root updates from a previous life-state transition. Keep state transitions and the final retained position consistent without requiring idle heartbeat messages.

## Inspector configuration

| Setting | Initial value |
| --- | --- |
| Shared item collision damage | 10 health |
| Item-definition collision-damage override | Unset; explicit zero is valid |
| Item approach and relative closing-speed threshold | 3 m/s |
| Pedestrian cart-hit damage | 25 health |
| Qualifying crash-ejection damage | 25 health |
| Fall damage enabled | Enabled |
| Hard-landing speed threshold | 8 m/s |
| Hard-landing damage | 25 health |
| Revive duration | 5 seconds |
| Damage vignette maximum edge opacity | 35% |
| Persistent vignette maximum edge opacity | 15% |
| Damage vignette fade duration | 2 seconds |
| Persistent vignette health threshold | Below 25% |

Keep existing potion strengths, failed-brew damage settings, and cart impact/ejection thresholds in their existing configuration. Provide runtime fall-protection control for potion and item effects. Do not add a player-facing tuning menu for these settings.

## Integration and asset preparation

| Integration area | Required behavior |
| --- | --- |
| `PlayerNetworkState` and shared player health/lifecycle logic | Own health publication and coordinate accepted life-state changes through a common interface used by damage, healing, and revival. |
| `PlayerPotionEffects`, effect receivers, and failed-brew effects | Apply actual healing/damage and respect downed state and fall protection. |
| `ItemDefinition` and its item-settings editor | Expose the optional collision-damage override without changing inventory identity or world-item ownership. |
| `PlayerItemHitbox`, item contacts, and `PlayerMotor` | Apply eligible item and landing damage once, preserving separate impact events and existing movement behavior. |
| Cart contacts, `GolfCartController`, and `GolfCartNetwork` | Apply pedestrian damage and preserve the distinction between harmful crash ejections and harmless ejections. |
| `PlayerSeating`, `PlayerCarry`, and `PlayerInventory` | Release relationships, preserve belongings, and apply permissions for downed, revived, and respawned players. |
| `PlayerInteraction`, input actions, and binding presentation | Support held revival, cancellation, occupied-body tooltips, and rebindable Give Up. |
| `AvatarProcessor`, avatar instances, and avatar presentation | Generate ragdoll physics and switch between animated and physical presentation, including an owner-visible full body. |
| `PlayerPresentation` and camera control | Switch between first-person and the following orbit camera. |
| Local HUD, `PlayerNameLabel`, and `InteractionTooltip` | Present health bars, vignette, rescue timer, revive progress, and contextual prompts. |

- Extend the existing Process Avatar workflow to generate the physics components and joints required for ragdolls. Reprocess avatars through that workflow; do not create a one-off migration tool.
- Coordinate ragdoll activation with avatar availability. Suspend animation and other pose writers while physics controls the body, then restore normal presentation when alive.
- Use UI Toolkit for local HUD feedback, timers, progress, and prompts. Integrate remote health bars with the existing nameplate presentation.
- Keep health/lifecycle logic shared across the systems that invoke it. Do not make every generic shove automatically deal damage or duplicate health rules across individual sources.
- Prefer event-driven health, UI, and lifecycle changes. Cache required references during initialization or transitions, and keep recurring work limited to active simulation or presentation.
- Minimize scene edits. Identify any required player-prefab additions when implementing; generated ragdoll components belong in processed avatar assets. Let Unity generate metadata.

## Visual acceptance checks

The user performs these checks after implementation using host and guest clients:

- Both clients show consistent health bars without health numbers. Damage flashes appear only for the injured local player; low-health persistence and healing across its threshold behave correctly.
- Ordinary jumps, drops, and walking into nearly stationary items remain harmless. Qualifying item and cart contacts damage once while distinct subsequent impacts can damage again.
- A default-damage item, an overridden item, and a zero-damage item produce the expected bar changes while retaining their shove behavior.
- Hard landings apply the fixed damage, including after another damaging impact. Bouncy protects landings and retains protection through the first landing after midair expiry.
- Health potions heal up to the cap, and failed brewing deals its damage once without doubling it through knockback. Neither restores a downed player.
- Death while walking, seated, carrying, or being carried releases relationships cleanly and preserves inventory. The owner's full-body ragdoll and orbit camera follow motion without clipping through walls.
- Remote moving ragdolls follow a consistent root position and settle without visible correction jumps. A joining client sees the current body, health, rescue timer, and revive state.
- Revive progress appears for both participants. Looking away and moving within range work; release, damage, obstruction, and leaving range reset progress. A second rescuer sees Being revived.
- A revive must finish before timeout. Completion restores 50% health and a clear upright placement, including nearby placement or the spawn fallback when the body cannot stand where it lies.
- Give Up, timeout, and crossing the lower world boundary restore the expected spawn, full health/stamina, retained inventory, and cleared potion effects. Delayed movement does not return the player to their old body.
