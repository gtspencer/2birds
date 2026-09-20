Review of commit `c43f214f9f3257709aa72ee1965204a3f4523907` (player carrying). All line references below refer to that commit. P2 denotes a behavioral issue; P3 denotes a lower-priority state or maintenance issue.

1. **[P2] Successful release confirmation rewinds the throw preview.**

   Locations: [PlayerCarry.cs](Assets/Game/Runtime/Player/PlayerCarry.cs), lines 180–187, 123–132, and 298–305; [PlayerSeating.cs](Assets/Game/Runtime/Player/PlayerSeating.cs), lines 194–212; [PlayerPresentation.cs](Assets/Game/Runtime/Player/PlayerPresentation.cs), lines 96–102.

   A remote carrier immediately advances the carried player's graphics along a ballistic trajectory while waiting for the release RPC. Confirmation clears `preview`, installs the original release position and velocity, and calls `SetSeated(false)`, which snaps the graphics to that original position before restarting smoothing. Nothing preserves the preview's elapsed motion or graphical offset. At the configured maximum throw speed, a 200 ms round trip can produce roughly three metres of forward preview followed by a visible backward snap and another apparent launch. The 250 ms elapsed-time cap also freezes the preview if confirmation takes longer.

   Preserve the current preview pose when handing graphics back to motor smoothing, then blend out its offset from the committed simulation. Keep this correction in presentation so it does not apply launch velocity twice.

2. **[P2] Cart launches discovered on the server or during replay can miss the automatic drop.**

   Locations: [PlayerMotor.CartContacts.cs](Assets/Game/Runtime/Player/PlayerMotor.CartContacts.cs), lines 270 and 282–307; [PlayerMotor.cs](Assets/Game/Runtime/Player/PlayerMotor.cs), lines 589–596.

   The cart-contact path marks contacts `Launched` and installs lift/recovery during simulation, but calls `ImpactDrop` only for an owner outside reconciliation. If latency or prediction differences make a remote carrier's qualifying collision occur first on the server, the server launches that player without ending carry. The owner can then receive the already-launched contact through reconciliation. A collision first encountered during owner replay has the same gap: it records `Launched` while skipping the drop. Subsequent contact processing skips that contact, so there need not be a later live callback to release the passenger. The carrier can consequently be launched while still holding another player.

   Provide a once-only live notification for launches accepted from server state or discovered during replay, or let the server's live qualifying collision enter the existing guarded release path. Preserve the rule against sending requests or changing relationships inside replay itself.

3. **[P2] Forced client-side release assumes every client knows the player's real spawn point.**

   Locations: [PlayerSeating.cs](Assets/Game/Runtime/Player/PlayerSeating.cs), lines 244–264; [PlayerCarry.cs](Assets/Game/Runtime/Player/PlayerCarry.cs), lines 170–187 and 195–207. Supporting code: [PlayerMotor.cs](Assets/Game/Runtime/Player/PlayerMotor.cs), lines 152 and 273; [GamePlayerSpawner.cs](Assets/Game/Runtime/Networking/GamePlayerSpawner.cs), lines 67–72.

   `TryCarryPlacement` now runs on the carrier's client and falls back to the partner's `Motor.SpawnPoint`. That field is initialized from the transform in `Awake`; only the server explicitly assigns the original spawn marker. A late joiner instantiates an existing player at that player's current network position, so its cached fallback can be somewhere entirely different from the server's spawn point. This was less exposed when the seating fallback ran on the server.

   For example, join after another player has moved away from spawn, carry that player elsewhere, and receive an impact where nearby release candidates are blocked. The client can request a forced release at the old observation position. The server accepts that position; even an unsuccessful clearance check sends it as the pending-placement retry origin.

   Synchronize the assigned fallback point, derive it consistently from the synchronized spawn slot, or defer just the spawn fallback to the server when the owner's nearby search fails. Client-reported ordinary release poses can remain trusted.

4. **[P2] Inventory gestures survive being picked up and can execute after release.**

   Locations: [PlayerInventory.cs](Assets/Game/Runtime/Inventory/PlayerInventory.cs), lines 325–342; [PlayerSeating.cs](Assets/Game/Runtime/Player/PlayerSeating.cs), lines 194–215. Integration gap: [HudController.cs](Assets/Game/Runtime/UI/HudController.cs), lines 193–238, 255–280, and 453–460.

   The new `CanAct` guards block actual swaps and drops while carried, and `ApplyControlPermissions` removes submitted requests from the previous revision. They do not cancel the HUD's unfinished mouse drag (`dragFromSlot`) or controller move (`moveSource`). `HudController.CanMove` does not consult `CanAct`, and its inventory-change handler only clears a move when the source item disappears. Carrying preserves inventory contents, so these gestures remain active; a player can also start one while carried.

   Open inventory, start dragging an item, get picked up and released, then release the mouse outside the panel: the old gesture drops the item using the new control revision. Controller destination selection can likewise complete an old move. This bypasses the intended cancellation of actions across carry transitions, even though the final inventory method correctly checks current permissions.

   Cancel mouse and controller move state when control permissions change, and gate starting/completing item gestures with `CanAct`. The inventory may remain open for inspection.

5. **[P3] Applying the initial control snapshot erases valid replicated item-charge state.**

   Locations: [PlayerSeating.cs](Assets/Game/Runtime/Player/PlayerSeating.cs), lines 67–94 and 217; [PlayerNetworkState.cs](Assets/Game/Runtime/Player/PlayerNetworkState.cs), lines 63–78. Delivery order: [NetworkObject.Callbacks.cs](Assets/FishNet/Runtime/Object/NetworkObject/NetworkObject.Callbacks.cs), `OnSpawnServer`.

   FishNet sends buffered RPCs before invoking the behaviours' `OnSpawnServer` snapshot callbacks. When a client joins while an existing free player is charging an item, `ObserversChargingUse(true, revision)` therefore arrives before `TargetCurrent`. For revision zero, the charge is accepted and then erased by the newly unconditional `ClearChargingForSeat()` in `Apply`. For a later control revision, the charge can instead be rejected because the initial control snapshot has not installed that revision yet. The resulting `IsChargingUse` value is false on the new observer for the rest of that charge; change-only replication does not resend `true`.

   Distinguish initial state installation from a transition that cancels use. Deliver current item-charge state with the control snapshot, or retain a charge update until its matching control revision is installed. This concerns existing item-charge state, not replication of the local-only player-throw charge.

6. **[P3] Rejected inventory releases resolve the same bird prediction twice.**

   Location: [PlayerInventory.cs](Assets/Game/Runtime/Inventory/PlayerInventory.cs), lines 279–286. Supporting code: [BirdHitReporter.cs](Assets/Game/Runtime/Birds/BirdHitReporter.cs), lines 169–188.

   For the rejected release operation, the existing call at line 281 already evaluates `accepted || request.Operation != operation` to `false`. The new call at line 285 invokes `ReleaseResolved(id, operation, false)` again for the same item. This is not a side-effect-free setter: `ReleaseResolved` appends to a bounded resolution-order queue and processes pending hit predictions. Duplicate calls consume cache capacity and repeat processing unnecessarily.

   Keep one resolution callback per release outcome and retain the separate item rollback. This also makes the rejection path easier to reason about alongside cancellation in `ApplyControlPermissions`.

User visual checks: use a host, remote carrier, and third observer, swapping carrier/carried roles. With noticeable latency, check that throws never snap backward or pause and that qualifying cart hits always detach the passenger. Join after players have left spawn, then force a blocked release and check the fallback destination. Keep inventory open through pickup/release with a mouse drag or controller move pending; neither gesture should survive. Also check paired cart entry, airborne releases, fall resets, and disconnects for lingering attachment or disabled movement.
