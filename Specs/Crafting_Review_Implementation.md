# Crafting Review Implementation

## 1. [P1] Preserve active potion doses across seat and carry transitions — Fixed

`PlayerMotor.BeginGeneration` ends the rebound sequence without clearing the active dose or its reconciliation history. Placement retains the existing dose synchronization, so potions received while seated or carried remain available after release. Explicit effect resets and network teardown still clear the effect and scheduled doses.

## 2. [P2] Keep the effect reset counter monotonic through control changes — Fixed

`PlayerSeating` captures the reset counter from `PlayerPotionEffects.Reset`, retains that value when applying control transitions, and increments it for respawn. Cart entry, paired entry, exit, occupant snapshots, carry pickup, and carry release transmit the appropriate player's reset counter. Carry pickup retains its compact RPC payload.

## Unfixed findings

None. Both findings describe defects in the current control-transition paths.

## Visual validation

- On a host and remote client, use a bouncy potion, enter and exit a cart, then jump before expiry. Confirm enhanced jumps and rebounds remain consistent with the HUD countdown. Repeat with pickup and release by another player, including receiving the potion while seated or carried.
- Enter a cart or get carried during a rebound sequence. After release, confirm the old sequence has ended and a fresh jump starts a new enhanced sequence.
- Respawn once, enter and exit a cart or complete a carry interaction, acquire a fresh buff, and respawn again. Confirm the HUD clears and subsequent jumps use normal height. Repeat another cycle.
- Join with another client while a previously respawned, buffed player is seated or carried. Release that player and confirm the joining client sees the enhanced jump and rebounds until expiry.
