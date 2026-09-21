# Crafting and Potion Review

## 1. [P1] Preserve active potion doses across seat and carry transitions

**Location:** [PlayerMotor.cs:261](Assets/Game/Runtime/Player/PlayerMotor.cs#L261), `BeginGeneration`; also `ApplyPlacement` at lines 373–381 and [PlayerMotor.Bouncy.cs:41](Assets/Game/Runtime/Player/PlayerMotor.Bouncy.cs#L41).

**Problem:** `BeginGeneration` now calls `ClearBouncy`, which clears both the active bounce state and every scheduled dose. Every `ApplyPlacement` calls `BeginGeneration`, including entering/exiting a cart and being picked up/released by another player. Calling `SynchronizeBouncy` first does not preserve the dose because it is immediately cleared afterward.

**Why it matters:** Use a bouncy potion, enter and exit a cart before it expires, then jump: the enhanced jump and rebounds are gone, while `PlayerPotionEffects.current` still drives an active HUD countdown. Receiving a potion while seated has the same problem upon exit. The server also clears its dose schedule, so subsequent reconciliation cannot restore the effect.

**Recommended fix:** Separate ending a bounce sequence from clearing the potion dose. Ordinary placement/impact-generation changes should retain the active definition, expiry, and doses needed for reconciliation while ending the current rebound sequence. Clear the effect itself on explicit effect reset and network teardown.

## 2. [P2] Keep the effect reset counter monotonic through control changes

**Location:** [PlayerSeating.cs:201](Assets/Game/Runtime/Player/PlayerSeating.cs#L201), `Apply`, and [PlayerSeating.cs:242](Assets/Game/Runtime/Player/PlayerSeating.cs#L242), `ResetToSpawn`. Related transition constructors are in `GolfCartNetwork.cs:257–301` and `PlayerCarry.cs:254–278`.

**Problem:** `EffectReset` is incremented from the stored control transition, but the cart and carry paths construct new transitions without copying it. After a respawn sets it to `1`, an ordinary seat/carry transition replaces `current` with a transition containing `0`. `PlayerPotionEffects.Reset` remains `1` because `ResetEffects` rejects older values. The next respawn increments the stored `0` back to `1`, and its effect reset is therefore rejected too.

**Why it matters:** Respawn once, enter/exit a cart or carry another player, acquire a fresh buff, and respawn again. The second respawn leaves the buff countdown, remembered effects, and registry dose intact. Previously reset players also advertise a stale reset counter in control snapshots, which can prevent a joining observer from accepting their current potion dose.

**Recommended fix:** Preserve the current effect reset value in every control transition and snapshot. Derive the next respawn reset from `PlayerPotionEffects.Reset`, rather than a transition field that can return to its default value, and ensure all peers receive that monotonic value.
