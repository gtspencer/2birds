# Slingshot Review

Commit: `92cb495` (`initial slingshot`). Markdown changes excluded.

## [P2] Present impact transitions on the projectile's interpolation timeline

**Location:** [PebbleProjectile.cs:122](Assets/Game/Runtime/Projectiles/PebbleProjectile.cs#L122), `Boundary`; [PebbleRegistry.cs:192](Assets/Game/Runtime/Projectiles/PebbleRegistry.cs#L192), `ApplyTransition`.

**Problem:** Remote pebbles normally render behind their received motion samples using `InterpolationDelay`. When an impact transition arrives ahead of that presentation time, `Boundary` immediately applies the impact position, advances `presentedTick` to the impact tick, and discards the earlier samples. A terminal transition also emits dirt and retires the shot immediately; `Present` then returns its body to the pool before another rendered frame.

**Why it matters:** Observers see the pebble jump to its first bounce and disappear before visibly reaching its final impact. With the configured 0.1-second interpolation delay and the slingshot's 28 m/s full-charge speed, a transition can skip roughly 2.8 metres of visible travel. Player-contact sampling also jumps across that interval in a single call instead of following the displayed trajectory.

**Recommended fix:** Accept the network transition immediately, but retain its motion boundary for presentation. Advance the visible pebble through the incoming segment to the boundary tick before switching to the rebound segment. For terminal impacts, perform the final contact sweep, emit dirt, and return the body to its pool only when presentation reaches that boundary. Handle transitions that arrive behind the presentation clock separately as corrections.
