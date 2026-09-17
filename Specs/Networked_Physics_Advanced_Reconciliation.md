# Advanced reconciliation techniques for networked physics items

## Purpose

This document contains optional reconciliation techniques that are deliberately **not** part of the initial implementation.

Start with the simple reconciliation system in the main networked-physics plan:

1. compare authoritative state against the locally predicted state from the same simulation tick
2. ignore small errors
3. correct physics immediately and smooth the rendered `VisualRoot`

Only add techniques from this document when playtesting reveals a specific visible problem.

---

# 1. Multiple reconciliation thresholds

The simple system uses one positional error threshold.

If that proves too blunt, introduce multiple tiers.

Example:

```text
Very small error
    → ignore

Small error
    → gentle correction

Moderate error
    → authoritative physics correction
    → short VisualRoot smoothing

Large error
    → authoritative reset
    → stronger but still brief visual smoothing
```

Possible starting values for a typical rock:

| Position error | Behavior |
|---|---|
| `< 0.05 m` | Ignore |
| `0.05–0.20 m` | Soft correction |
| `0.20–0.50 m` | Strong correction |
| `> 0.50 m` | Immediate authoritative reset |

These values are tuning starting points only.

Scale thresholds with item size if the game contains objects with very different physical dimensions.

---

# 2. Velocity-aware reconciliation

Position alone does not always reveal whether two simulations are still meaningfully aligned.

Example:

```text
Predicted:
position error = 0.08 m
velocity = left

Authoritative:
position error = 0.08 m
velocity = right
```

The positional error is small, but the trajectories are about to diverge dramatically.

If needed, evaluate:

- position error
- rotation error
- linear velocity error
- angular velocity error
- velocity direction

A large velocity-direction disagreement can trigger stronger reconciliation even when positional error is still small.

---

# 3. Rotation-specific tolerance

Irregular tumbling rocks often tolerate large rotational disagreement without looking wrong.

If unnecessary rotation corrections create visual noise:

- use a much larger rotation threshold than position threshold
- prioritize position and linear velocity
- correct spin only when visibly significant or gameplay-relevant

For many rocks, exact rotation is less important than keeping the trajectory and collision body believable.

---

# 4. Authoritative impact sequence

If normal snapshots are too slow to identify collision divergence, add an `impactSequence`.

Example snapshot:

```text
itemId
simulationTick
position
rotation
linearVelocity
angularVelocity
impactSequence
```

Increment `impactSequence` after a meaningful authoritative collision.

The client can then identify:

```text
Client predicted:
    no collision

Host:
    impactSequence changed
```

and reconcile immediately instead of waiting for position error to grow.

This is useful when collision branches are visibly different but should not be added unless needed.

---

# 5. Collision-aware reconciliation

A locally predicted collision can disagree with the host because remote dynamic objects are displayed from delayed network state.

For example:

```text
Client:
Rock A hits Rock B and bounces left.

Host:
Rock B had already moved.
Rock A misses and continues forward.
```

If this becomes visually objectionable, classify authoritative impact disagreements separately from ordinary drift.

On a major collision disagreement:

1. immediately adopt authoritative physics position and velocity
2. preserve the current rendered pose through `VisualRoot`
3. decay the visual correction quickly
4. optionally use impact sound/particles/camera response to mask the transition

Do not gradually lerp the physical collider between incompatible collision outcomes.

---

# 6. Impact-time masking

Collisions are natural moments for stronger visual correction because the player already expects abrupt motion changes.

Useful masking effects include:

- impact sound
- dust or debris
- brief particles
- squash/stretch or impact animation if stylistically appropriate
- small camera response
- hit flash or decal

If an authoritative impact causes a trajectory correction, applying the correction during the impact is often less noticeable than correcting gradually afterward.

Do not add effects solely to conceal networking defects; use them where they also improve normal game feel.

---

# 7. Visual error spring instead of fixed-duration decay

The simple system can linearly or exponentially decay the `VisualRoot` offset over approximately 50–100 ms.

If that looks mechanical, replace it with a critically damped spring.

Conceptually:

```text
visualOffset → 0
visualRotationOffset → identity
```

using a damped response rather than a fixed lerp.

Benefits:

- avoids overshoot
- handles repeated corrections gracefully
- naturally adapts to varying error magnitude

This is polish, not a first-pass requirement.

---

# 8. Prediction lifecycle

If predicting a rock for its entire flight creates unnecessary complexity, introduce an explicit lifecycle:

```text
LocalPredicted
    ↓
AuthorityConfirmed
    ↓
Reconciled
    ↓
RemoteAuthoritative
```

Possible transition conditions from local prediction to normal authoritative presentation:

- item sleeps
- item is picked up
- major authoritative divergence occurs
- several consecutive snapshots remain within tolerance
- the early latency-sensitive portion of the throw has ended

This can reduce the amount of prediction state retained for long-lived objects.

Do not introduce this state machine unless continuous local prediction creates actual implementation or visual problems.

---

# 9. Dynamic thresholds

If fixed thresholds perform poorly across different objects or speeds, scale reconciliation tolerance according to:

- collider radius
- object speed
- distance from the camera
- screen-space size
- current interaction importance

Example:

```text
large/far-away object:
    tolerate more world-space error

small/near-camera object:
    tolerate less visible error
```

Screen-space error can be more perceptually relevant than raw meter distance, but it is also more complex.

Use only after simpler thresholds have been profiled.

---

# 10. Snapshot impact bursts

The main plan already permits optional collision-triggered snapshots.

If collision corrections arrive too late, make this explicit:

```text
normal motion:
    15–20 Hz snapshots

meaningful authoritative impact:
    send immediate additional motion snapshot
```

This reduces the time a local prediction can follow the wrong post-impact trajectory.

Avoid sending bursts for trivial resting contacts.

Use an impulse or velocity-change threshold to define a meaningful impact.

---

# 11. Short local collision history

If debugging or reconciliation requires knowing why a prediction diverged, retain a very short local history of:

- predicted collision tick
- other collider/item ID
- contact normal
- pre-impact velocity
- post-impact velocity

Compare that with authoritative impact metadata.

This can help distinguish:

- ordinary integration drift
- different collision timing
- collision with a differently positioned network object
- completely missing collision

This is primarily useful for diagnostics and advanced correction logic.

---

# 12. Reconciliation diagnostics

Before adding advanced logic, build good instrumentation.

For locally predicted throws, log or visualize:

- predicted pose
- authoritative pose
- positional error
- velocity error
- correction frequency
- correction magnitude
- time since throw
- whether correction followed a collision
- ping / simulated latency

Useful development overlays:

```text
green:
    prediction within tolerance

yellow:
    soft correction

red:
    major reconciliation
```

Track aggregate statistics:

- percentage of throws requiring correction
- median correction distance
- 95th percentile correction distance
- corrections following dynamic-object impacts
- corrections following static geometry impacts

This data should determine whether advanced techniques are actually justified.

---

# 13. What not to do

Even with the advanced system:

- do not give the client final authority over shared world physics
- do not rollback and replay the entire surrounding physics world
- do not distribute different halves of one collision across different authorities
- do not slowly interpolate physical colliders through geometry
- do not correct every tiny PhysX discrepancy
- do not add complexity without a demonstrated visual problem

---

# Recommended escalation order

If the simple system looks bad, add complexity in this order:

1. tune the basic positional threshold and visual smoothing duration
2. add velocity-aware reconciliation
3. send immediate snapshots after major impacts
4. add separate collision-divergence handling
5. add impact sequence metadata
6. introduce multiple error tiers
7. introduce a prediction lifecycle
8. consider dynamic or screen-space thresholds
9. consider spring-based visual smoothing

Stop as soon as the visual result is good enough.

The goal is not mathematical synchronization.

The goal is a responsive local throw, authoritative shared physics, and corrections that players do not notice.
