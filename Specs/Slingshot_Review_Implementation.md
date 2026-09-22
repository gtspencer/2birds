# Slingshot Review Implementation

## Fixed: Present impact transitions on the projectile's interpolation timeline

The finding is credible. `PebbleProjectile.Boundary` advanced the visible position and contact sampling to an incoming impact tick immediately, discarding the incoming segment. `PebbleRegistry.ApplyTransition` also emitted terminal dirt and retired the presentation before interpolation reached the impact.

Impact samples now remain in the interpolation history. Departure correction ends when presentation reaches the first impact boundary. Terminal presentation stops at the exact terminal position and performs its final player-contact sweep before ending. The registry accepts retirement immediately while retaining the body until that presentation completes, then emits impact dirt and returns the body to its pool. Transitions behind the presentation clock remain explicit corrections with contact rebasing.

## Findings not implemented

None.

## Visual validation

- With two clients, observe a full-charge shot: it should travel continuously into the first bounce, follow the rebound, and reach the second impact before disappearing with dirt.
- Observe shots with both impacts close together and shots crossing the observing player's hitbox near an impact; contact should follow the displayed path.
- Repeat with network delay and jitter to exercise late corrections, and fire repeated shots to check pooled projectile reuse.
