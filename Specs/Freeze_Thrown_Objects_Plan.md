# Freeze Thrown Objects Plan

## Problem

Rocks use sphere colliders and roll for a very long time after being thrown, even at barely-visible speeds. The current freeze mechanism relies entirely on Unity's `Rigidbody.IsSleeping()`, which checks kinetic energy against `sleepThreshold`. Sphere colliders maintain angular energy above that threshold far longer than other shapes, so rocks keep rolling when they should have stopped.

Current rock damping values (`ItemDefinition`): `LinearDamping = 0.05`, `AngularDamping = 0.1` — very low, almost no rolling friction.

## Changes

### 1. Increase angular damping on the rock ItemDefinition

**No code change — data only (ScriptableObject field).**

Bump the rock's `AngularDamping` from `0.1` to ~`1.5–3.0`. This makes rolling friction feel realistic and is the single biggest win. Tune in-editor until the roll duration feels right.

### 2. Add host-side "slow enough for long enough" force-sleep

Even with better damping, a sphere on a slight slope or receiving micro-impulses can nudge around near-zero velocity indefinitely. Fix this with a custom velocity check that force-sleeps the body after a configurable delay.

#### ItemDefinition — add two fields

```csharp
[Header("Freeze")]
[Min(0f)] public float FreezeSpeedThreshold = 0.15f;
[Min(0f)] public float FreezeDelay = 0.5f;
```

- `FreezeSpeedThreshold` — linear and angular speed (m/s) below which the timer starts.
- `FreezeDelay` — seconds both speeds must stay below the threshold before forcing sleep.

#### WorldItem — track slow time, check in Tick

Add a field:

```csharp
private float slowTime;
```

In `Tick()`, after the existing ignore/history logic, add the host-side check:

```csharp
if (registry.IsHost && Record.State == WorldItemState.World && !Body.isKinematic && !Body.IsSleeping())
{
    bool slow = Body.linearVelocity.sqrMagnitude < Definition.FreezeSpeedThreshold * Definition.FreezeSpeedThreshold
              && Body.angularVelocity.sqrMagnitude < Definition.FreezeSpeedThreshold * Definition.FreezeSpeedThreshold;
    slowTime = slow ? slowTime + (float)registry.TickDelta : 0f;
    if (slowTime >= Definition.FreezeDelay)
    {
        Body.Sleep();
        slowTime = 0f;
    }
}
```

Reset `slowTime` in `ResetPresentation()` and when the item is re-launched (already covered by `Initialize` → `ResetPresentation`).

#### No networking changes

`AfterTick` already detects `Body.IsSleeping()` going true, publishes the sleeping flag, and clients make the body kinematic. `Body.Sleep()` feeds directly into that existing path.

## What was considered and rejected

**Progressive damping ramp after ground contact** — requires contact-time tracking, deciding whether to sync damping state across clients during prediction, and resetting on re-throw. More complex for the same end result, and the damping mismatch between host and predictor would cause unnecessary corrections.

## Validation

- Throw a rock on flat ground: should stop rolling within ~2–3 seconds, not 10+.
- Throw a rock down a slope: should still roll naturally while on the slope, freeze shortly after reaching flat ground.
- Re-throwing a frozen rock should behave identically to a fresh throw (no leftover state).
- Clients should see the rock stop at the same position/time as the host (existing sleep replication).
