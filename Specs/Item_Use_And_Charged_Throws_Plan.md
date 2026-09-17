# Item Use and Charged Throws

## Intended behavior and assumptions

- Left mouse button and right trigger send use commands to the equipped runtime item.
- Throwable items begin charging on press and throw one item on release. A quick click produces a weak throw; holding increases strength linearly until full charge. Full charge waits for release and does not automatically throw.
- Use applies to the selected item instance, not the entire stack. Another throw requires a fresh press.
- Charge uses elapsed gameplay time, not trigger pressure. Opening inventory or a menu cancels use without throwing.
- Replicate a player-level charging flag from the start of charging until release or cancellation, including time spent holding at full charge. This means "charging or holding a charged item," not specifically "full charge reached." Remote avatar visuals will be added later.
- Throw strength means launch speed in metres per second, consistent with the existing velocity-based physics. Keep this meaning explicit in Inspector labels/tooltips rather than switching to mass-dependent force.
- Configure each item type through its existing `ItemDefinition` ScriptableObject. Proposed starting values: minimum speed 3 m/s, maximum speed equal to the item's existing `ThrowSpeed`, and full charge time 1 second.

## Design

```text
PlayerInputReader: Use press / release / cancel
    -> PlayerEquipment: resolve and retain equipped item instance
        -> WorldItem: forward use commands
            -> ItemUseBehaviour: item-specific behavior
                -> ThrowableItemUse: charge, then request release at a chosen speed
                    -> PlayerEquipment: forward the item release request
                        -> PlayerInventory: release that item through existing networking

PlayerEquipment: charging transitions
    -> PlayerNetworkState: replicate charging flag
```

| Responsibility | Owner |
| --- | --- |
| Charge timing, progress, and throw-strength calculation | `ThrowableItemUse` |
| Input dispatch, active item tracking, and use cancellation | `PlayerEquipment` |
| Replicated charging flag | `PlayerNetworkState` |
| Item storage, selection, and release transactions | `PlayerInventory` |

`BakedPickup` remains the scene authoring marker. Put runtime use behavior on `WorldItem` objects so both baked pickups and spawned items support it.

Use a small component contract and a throwable implementation. A future gun can implement the same contract to fire on press or run its own firing loop while held; input and equipment routing need no gun-specific branches. Keep firing cadence, ammunition, and reloading outside the shared contract. Do not implement weapon behavior in this task.

## 1. Add item-owned use behavior

Create two scripts under `Assets/Game/Runtime/Items/`:

- `ItemUseBehaviour.cs`: abstract `MonoBehaviour` with `BeginUse(PlayerEquipment user)`, `EndUse()`, and `CancelUse()`. Provide read-only virtual `IsCharging` and `Charge01` properties, defaulting to false and zero, for presentation.
- `ThrowableItemUse.cs`: implements those methods, retains the initiating equipment and press timestamp, and computes charge locally. Cache its `WorldItem` component in `Awake`; access the assigned item definition after initialization.

In `WorldItem.cs`, cache the attached use behavior in `Awake` and expose forwarding methods and charge presentation values. An item without a use behavior does nothing on use; being a pickup must not implicitly make an item throwable.

Store mutable charge state on the runtime behavior, never on the shared ScriptableObject. When an active item is disabled, removed, or reset for pool reuse, notify its initiating equipment to cancel use and clear the replicated flag before clearing the holder reference. Reinitialization must not retain the previous holder or charge.

## 2. Route input to the exact held item

In `Assets/InputSystem_Actions.inputactions`, rename `Player/Throw` to `Player/Use`, preserving action/binding IDs and the existing left mouse/right trigger bindings. Keep it a normal button action so a short click is captured; a Hold interaction is unnecessary.

In `PlayerInputReader.cs`:

- Cache `Use` and `PlayerEquipment` at owner startup in place of the throw action reference.
- Forward press and release edges to `PlayerEquipment.BeginUse()` and `EndUse()`. Handle both edges in order if a quick click occurs within one input update.
- Call `PlayerEquipment.CancelUse()` before disabling gameplay, opening inventory, losing focus/ownership, or stopping the client. Turning `InventoryOpen` on must cancel immediately, before the input loop's early return.
- Following cancellation, require the button to be released before accepting another use press. Reopening gameplay while the trigger remains held must not restart charging or throw on release.
- Give a simultaneous drop priority over use and cancel the active use before dropping.

In `PlayerEquipment.cs`:

- Cache inventory, registry, and `PlayerNetworkState` references during initialization. Subscribe to `InventoryChanged` during the owning client's lifecycle and unsubscribe during cleanup.
- Resolve the selected stack's first `WorldId` through `WorldItemRegistry.TryGetItem` when use begins. Retain that ID, slot, and object for the duration of the press.
- Forward release/cancel to that retained target, never whichever item happens to be selected later. Clear the active routing state and reported charging flag before invoking the end/cancel callback so synchronous inventory updates or item lifecycle notifications cannot end use twice.
- Use one idempotent `CancelUse()` method for all cancellation paths. On `InventoryChanged`, compare the selected slot and equipped instance with the retained target; cancel if either differs. Use the owner's predicted inventory view so use remains responsive immediately after pickup or selection.
- Route active-item removal/disable notifications and gameplay interruption through the same cancellation method. Clear active use during equipment ownership loss and client shutdown.
- Do not cancel merely because an unrelated slot changes or an acknowledgement confirms the same equipped item.
- Expose `IsCharging` and `Charge01` from the active target for the HUD. Keep the charge timer and strength calculation on the item behavior.
- Report charging transitions through `PlayerNetworkState` after the item begins use and whenever it ends or cancels. Set the flag only when the item's behavior actually starts charging; pressing use on an empty slot or a non-charging item must not set it.
- Provide a release helper for item behaviors that forwards the requested item identity and speed to inventory. It must work during the end callback after active-use routing has been cleared; inventory confirms that the requested item is still equipped.

In `PlayerInventory.cs`, retain storage, selection, and release operations. Publish inventory changes through the existing event. Do not store active-use targets, charge timers, or charging presentation state there.

## 3. Make release strength explicit

Extend `ItemDefinition.cs` with a throw settings group:

| Field | Meaning | Initial value |
| --- | --- | --- |
| `MinThrowSpeed` | Launch speed for an immediate release | 3 m/s |
| `MaxThrowSpeed` | Launch speed at full charge | Existing `ThrowSpeed` |
| `ThrowChargeTime` | Seconds required to reach full charge | 1 second |

Rename `ThrowSpeed` to `MaxThrowSpeed` using `FormerlySerializedAs("ThrowSpeed")` to preserve existing asset tuning. Keep speeds nonnegative, maximum at least minimum, and charge time positive through Inspector constraints and a short editor-time clamp.

`ThrowableItemUse` calculates on release, using the same calculation for the bar:

```text
charge = Clamp01((currentGameplayTime - pressTime) / ThrowChargeTime)
launchSpeed = Lerp(MinThrowSpeed, MaxThrowSpeed, charge)
```

Refactor `PlayerInventory.ReleaseSlot` to accept an explicit launch speed instead of the `throwing` boolean. Replace the immediate `ThrowSelected()` entry point with a release helper taking the requested item ID and speed from equipment. Resolve the current selected slot and confirm that its equipped instance matches the requested ID before releasing exactly that instance.

Keep drop callers supplying `DropSpeed`, including whole-stack drops. Preserve aim at release time, inherited player velocity, initial spin, wall clearance, stack spacing, and the existing predicted release path. Clear the behavior's charge state before submitting the release.

## 4. Replicate charging state and keep throws responsive

- Add a read-only `IsChargingUse` property to the existing `PlayerNetworkState` component. Keep this presentation flag separate from `PublicPlayerState` so a charge transition does not resend health, spawn slot, and revision.
- `PlayerEquipment` reports local charging transitions to `PlayerNetworkState`; keep the RPCs and replicated flag on `PlayerNetworkState`. Update the owner's local flag immediately and send a reliable owner-only `ServerRpc` carrying one boolean. The server trusts that value, stores it, and relays it with a reliable `ObserversRpc`. Use the buffered latest value so a new observer receives the current state even if charging began before they joined. The application payload is one boolean per transition, plus FishNet's RPC overhead.
- Send `true` when charging begins and `false` on release or cancellation. Send only transitions, with no charge percentage, timestamp, item ID, or per-frame messages. Reliable ordered delivery preserves start/stop order even for quick taps.
- Keep the owner's immediate state independent of echoed observer updates so an older echoed start cannot undo a local release. Handle the host owner without invoking the transition twice. Observers only update their stored flag; they never execute item-use behavior.
- Clear the flag through the same cancellation paths used for item use, including selection changes, drops, inventory/menu opening, focus loss, and item removal. Reset server state and its buffered value when a player loses ownership or is reused; disconnect/despawn cleanup must not leave stale charging state for a later spawn. Initialize it to false.
- Remote presentation can later read `PlayerNetworkState.IsChargingUse` to animate the avatar. Add no remote animation or HUD indicator in this task. The local charge bar still reads the item's immediate local charge, independent of network latency.
- On release, calculate the final launch velocity locally and send it in the existing `InventoryRequest.Releases` / `ItemMotion` payload.
- Retain immediate `PredictRelease`, server acceptance of the reported motion, and existing lifecycle/motion replication to observers. Charging state is presentation data and must not gate or delay the throw request.
- Do not run item-use callbacks again on observers or in the server inventory commit; the host owner's callback must launch only once.
- Preserve existing reconciliation and release collision grace. Never infer throw versus drop from speed: a weak throw can overlap drop velocities. If a separate throw-only cooldown is introduced later, it needs explicit release intent.

## 5. Show charge beneath the crosshair

In `Assets/Game/UI/Hud.uxml` and `Hud.uss`, add a small horizontal track and fill, approximately 60 by 5 pixels, centered about 14 pixels below the crosshair. Start hidden and use `picking-mode="Ignore"`.

In `HudController.cs`, cache both elements during `OnEnable` and cache `PlayerEquipment` when binding the local player. Read active charge through equipment and update fill width from `Charge01` in the HUD update path. Show the track only while the local throwable is charging and gameplay is active. Keep it full while holding beyond the charge time.

Hide and reset it immediately on release, cancellation, inventory/menu opening, loss of the local player, and HUD disable. Apply hiding before early returns so a partially filled bar cannot remain on screen. Keep it clear of the interaction tooltip.

## 6. Unity authoring and setup

**Required prefab change:** add `ThrowableItemUse` beside `WorldItem` on every current item world prefab, including `Assets/Game/Prefabs/Rock.prefab`. Update the existing item definition assets under `Assets/Game/ScriptableObjects/Items/` with the charge settings. No new settings asset or player component is necessary.

Ensure baked scene pickups inherit the behavior from their prefab. If any are standalone objects or have overrides preventing inheritance, identify the specific objects and notify the user before making those scene changes.

Update `Assets/Game/Editor/WorldItemSetup.cs` so input setup creates/migrates `Use` and cannot recreate `Throw` or duplicate the drop binding. Perform throwable-component assignment as an explicit migration of current item prefabs; do not make the generic setup automatically assign throwing to future items. Use a targeted migration entry point because the existing installer can return before configuring input in an already configured project.

During implementation, use the Unity CLI where available and Unity MCP otherwise for prefab/asset authoring. Let Unity generate `.meta` files. Avoid game scene edits if possible.  Instead, request the user to update the scene if needed.

## Implementation order

1. Add the behavior contract, throwable implementation, and ScriptableObject fields.
2. Add runtime item dispatch, exact-instance use coordination in `PlayerEquipment`, centralized cancellation, and explicit-speed inventory release.
3. Rename the input action and wire press, release, and cancellation.
4. Add player charging-state replication and connect all start/end/cancel paths.
5. Add the HUD charge bar.
6. Migrate current item prefabs/settings and the editor input setup.

## Visual validation for the user after implementation

- With mouse and gamepad, compare quick taps, partial holds, full holds, and holds beyond full charge. The bar fills smoothly; distance increases to a cap; only release throws.
- Aim elsewhere while charging and release: the throw follows the final aim. Repeat near a wall and while moving.
- Charge a stacked item and release: exactly one leaves the stack, and the next item requires a new press.
- While charging, switch slots, drop, swap the active slot, open inventory/menu, or change application focus. The bar disappears and no delayed throw occurs after returning.
- Use an empty slot: nothing happens. Drop one item and a full stack to confirm their ordinary release behavior.
- In a host/client session, throw from each player. The local throw starts immediately, other players see one consistent release, and one player's charge never appears on another player's HUD.
- Inspect the remote player's charging flag in the debugger while another player holds use: it becomes true at charge start, remains true at full charge, and returns to false on release or cancellation. Join while someone is charging and confirm the received state is true. Repeat with rapid taps and ownership/disconnect cleanup; no flag should remain stuck. Remote avatars have no new charge visual yet.
- Change minimum/maximum speed and charge time in an item definition and observe the corresponding throw strength and bar duration.

Automated validation and play-mode checks are deferred until explicitly requested.
