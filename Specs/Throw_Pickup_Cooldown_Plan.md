# Throw Pickup Cooldown Plan

## Behavior

After a throw, hide the "Pick up" tooltip and prevent pickup for one gameplay second on the throwing player's client only. The throw starts no cooldown on other clients. Drops, including stack drops and drops that cancel charging, do not start a cooldown.

Let the deadline expire naturally. A drop or a pooled instance reused before an existing deadline expires can retain the remaining local cooldown; accept this brief carryover unless later testing shows it needs handling.

## Approach

Start a local deadline on `WorldItem` directly from `ThrowableItemUse.EndUse`, after the release call completes. Gate the existing `CanInteract` property with that deadline.

Only `WorldItem.cs` and `ThrowableItemUse.cs` need changes. No network messages, serialized fields, assets, or release API changes are needed. The duration is a fixed `1f`.

Throw intent comes from `ThrowableItemUse`, independent of charge and velocity. Charged throws can start at 3 m/s, and both throws and drops inherit player velocity, so a speed threshold cannot distinguish them reliably.

## Changes

### 1. Add a local deadline and starter to `WorldItem`

```csharp
private float interactableAfter;

internal void StartPickupCooldown()
{
    if (Record.State != WorldItemState.World || registry.LocalInventory == null ||
        Record.Releaser != registry.LocalInventory.ObjectId) return;
    interactableAfter = Time.time + 1f;
}
```

The guard limits the cooldown to a locally released world item and avoids starting it if the release leaves the item held.

### 2. Start it after throwing

In `ThrowableItemUse.EndUse`, immediately after the existing release call:

```csharp
equipment.ReleaseItem(id, speed);
item.StartPickupCooldown();
```

Starting after `ReleaseItem` lets the existing synchronous prediction/initialization or host release finish first. The timer starts on release, not on charging or cancellation. The drop path never calls this method.

### 3. Gate `CanInteract`

```csharp
public bool CanInteract => registry != null && Record.State == WorldItemState.World &&
                           !optimisticPickup && Record.Motion.Id != 0 &&
                           Time.time >= interactableAfter;
```

`PlayerInteraction`, `InteractionTooltip`, and `PlayerInventory.Collect` already check `CanInteract`. Only the thrower's local item instance receives the deadline; server pickup processing needs no change.

## Timer lifetime

Only `StartPickupCooldown` writes the deadline, setting it to one second after each local throw. Do not add reset logic to `SetRecord`, `ApplyRecord`, `ResetPresentation`, or `ReturnToPool`. No release identity tracking or motion-update comparisons are needed.

Confirmation, corrections, sleep, pickup, rollback, and pooling leave the deadline alone. Once `Time.time` reaches it, the time check passes without clearing the field.

If another player picks up and drops the item during that second, the original thrower's remaining cooldown still applies. If the local instance is pooled and reused during that second, the reused item can also retain the remaining cooldown on that client. Neither event extends the deadline. Add targeted reset handling only if later testing makes these cases worth addressing.

## Visual validation by the user

- With a host and a remote client, have each throw an item: only the thrower should lose the tooltip and pickup ability for about one second; the other player should be able to pick it up immediately.
- Compare tap and fully charged throws while stationary and moving forward/backward. The cooldown should be the same.
- Drop individual items and stacks, including while charging. With no existing cooldown on the instance, pickup should remain immediately available.
- Throw into a nearby wall or floor so the item stops quickly. Stopping should not end the cooldown early.
- With latency, confirm server confirmation does not restart the timer. Have the other player pick up and drop the item during the original cooldown; the original thrower's timer should expire at its original deadline.
- If rapid pool reuse occurs, assess whether the remaining local cooldown is noticeable enough to justify a pool reset later.
