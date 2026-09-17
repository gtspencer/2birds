# Networked Physics Items — Review Decisions

Scope: the item system introduced by commit `571a7b9` (`holdable and throwable rework`). The numbering below matches the original review. Decisions favor immediate local response, consistent item identity, and small changes to the existing implementation.

## 1. Broadcast lists shared by reference — retain

FishNet's installed `ServerManager.Broadcast` overloads serialize the message into a writer before sending its bytes and returning. Clearing `lifecycleBatch` or `motionBatch` afterward cannot alter that serialized payload. Outgoing transport batching does not defer serialization of the original list.

Copying each list would add allocations without fixing a current bug. Revisit this dependency when upgrading FishNet if its serialization contract changes.

Source: [ServerManager.Broadcast.cs](Assets/FishNet/Runtime/Managing/Server/ServerManager.Broadcast.cs).

## 2. Missing definitions and item setup — address during authoring

Invalid item definitions or incomplete prefabs can cause initialization exceptions. A guard only in `Rent` would miss baked scene items, which initialize directly in `BeginWorld`. Silently skipping an item on one client would also leave clients with different world contents.

The explicit **Two Birds > Set Up World Item Physics** command processes registered item prefabs even when `SessionRoot` already has `WorldItemRegistry`. Automatic installation retains its one-time guard so assembly reloads and returning to Edit Mode do not repeatedly reconfigure existing assets.

**Two Birds > Bake World** checks:

- Missing registry entries and `WorldPrefab` references, including definitions not placed in the current scene.
- Required root `WorldItem`, `Rigidbody`, and `OfflineRigidbody` components on registered prefabs and scene pickups.
- Missing `WorldItem` visual-root references and colliders; missing renderers remain warnings.
- Scene pickup IDs that do not resolve to a definition, in addition to the existing definition-assignment checks.

These checks make content mistakes visible where they can be repaired. Runtime content still requires matching, correctly configured registries on both clients; no malformed-broadcast recovery or runtime skip policy is added.

Sources: [WorldItemSetup.cs](Assets/Game/Editor/WorldItemSetup.cs), [BakeTools.cs](Assets/Game/Editor/BakeTools.cs), [WorldItemRegistry.cs](Assets/Game/Runtime/Items/WorldItemRegistry.cs).

## 3. Collision-ignore reference after disconnect — retain

The disconnected player's hitbox is destroyed. Each item can retain at most one managed collider reference, and record changes, initialization, and pooling clear it. This is bounded cleanup, not an accumulating list of disconnected players.

The original suggestion that a destroyed hitbox's collision exclusion could carry into a reused player body is unsupported by this player lifecycle. Adding a fallback to every item tick is unnecessary for the current behavior.

Sources: [WorldItem.cs](Assets/Game/Runtime/Items/WorldItem.cs), [PlayerInventory.cs](Assets/Game/Runtime/Inventory/PlayerInventory.cs).

## 4. Pending inventory request timeout — retain existing recovery

Inventory requests and replies use reliable ordered delivery through Tugboat. The session configures transport timeouts, handles connection loss, and clears pending inventory requests during network shutdown. Packet loss or a disconnected server therefore does not imply requests accumulate forever.

A separate timer that silently discards predictions can conflict with delayed operations the server eventually accepts. It would also need to restore world presentation and pending releases, not just inventory slots. If recovery from an application-level failure while still connected becomes necessary, synchronize inventory and world state together instead of adding an isolated pending-list timeout.

Sources: [PlayerInventory.cs](Assets/Game/Runtime/Inventory/PlayerInventory.cs), [SessionController.cs](Assets/Game/Runtime/Networking/SessionController.cs), [ClientSocket.cs](Assets/FishNet/Runtime/Transporting/Transports/Tugboat/Core/ClientSocket.cs).

## 5. Registry singleton initialization order — retain

`WorldItemRegistry` belongs to the persistent session root that owns and starts networking. That root exists before the gameplay scene and player spawn flow. The original review did not identify a current path where player registration precedes the registry's `Awake`.

Deferred registration would add another initialization path without solving a demonstrated problem. Reconsider this assumption if player spawning or the session-root lifecycle changes.

Sources: [SessionBootstrap.cs](Assets/Game/Runtime/Networking/SessionBootstrap.cs), [SessionController.cs](Assets/Game/Runtime/Networking/SessionController.cs), [GamePlayerSpawner.cs](Assets/Game/Runtime/Networking/GamePlayerSpawner.cs).

## 6. Slot-selection intent — send an absolute selection

Ordinary in-flight operations do not inherently make toggling inconsistent: client and server process the same ordered requests. A rejected earlier prediction can, however, change what a later toggle means. For example, a predicted pickup selects slot 1, the player presses 1 to deselect, and the pickup is rejected; toggling the server's unselected state would select slot 1 instead.

`SelectSlot` resolves the toggle once against the current local selection and puts the desired slot, or `-1` for deselection, in the request. Server commit and prediction replay assign that value directly. Pressing the selected slot still deselects immediately, and replay preserves the intent captured at input time. No extra message fields are needed.

Source: [PlayerInventory.cs](Assets/Game/Runtime/Inventory/PlayerInventory.cs).

## 7. Competing pickups — retain immediate prediction

Both clients may predict success, but the server accepts only one claim. Rejection restores the latest recorded item state and ownership. That usually means the winner holds the item; it does not necessarily reappear on the ground as the original review suggested.

The correction is an expected consequence of immediate pickup response. Adding a delay would affect every pickup to hide an occasional competing claim. Preserve the current responsiveness unless gameplay feedback identifies a specific presentation problem.

Sources: [PlayerInventory.cs](Assets/Game/Runtime/Inventory/PlayerInventory.cs), [WorldItemRegistry.cs](Assets/Game/Runtime/Items/WorldItemRegistry.cs).

## 8. Detached hitbox and OfflineRigidbody — no change

The original finding has an incorrect premise. The installed `OfflineRigidbody` obtains its prediction manager through `InstanceFinder.PredictionManager`; it does not search its parents for a `NetworkObject`. Detaching the hitbox does not invalidate that reference.

Source: [OfflineRigidbody.cs](Assets/FishNet/Runtime/Generated/Component/Prediction/OfflineRigidbody.cs).

## 9. Motion before baseline — discard until the epoch is known

`ReceiveMotion` rejects messages while `epoch == 0` and rejects messages from a different established epoch. `earlyMotion` still buffers motion that arrives ahead of its lifecycle record within the accepted epoch.

Revision and tick comparisons cannot establish which session an untagged cached motion belongs to. Discarding motion before the baseline establishes the epoch avoids that ambiguity without adding epoch fields to the cache. The baseline supplies the initial state, followed by ordinary snapshots. Existing teardown already clears the cache and drains connections; this is a small precaution for message ordering, not evidence of a normal reconnect failure.

Source: [WorldItemRegistry.cs](Assets/Game/Runtime/Items/WorldItemRegistry.cs).

## 10. WorldItemRegistry responsibilities — defer splitting

The registry coordinates closely related lifecycle, identity, physics, and networking state. Its line count alone does not justify splitting it into a pool, networking service, and physics service. Such a split would introduce coordination interfaces without a concrete feature requiring them.

Extract a responsibility when independent reuse or a specific change makes the boundary useful.

## 11. PlayerInventory responsibilities — defer extraction

Release calculation currently has one caller and uses the player's camera, movement, item definition, and inventory operation. Moving it into a utility or `ItemDefinition` would not by itself simplify that interaction.

Keep the calculation local until another release mechanism needs it or item-specific behavior makes a separate implementation useful.

## 12. Constants and tuning values — retain

Buffer lengths and batch size are reasonable implementation constants. Several gameplay values, including minimum hit impulse, are already serialized. Exposing every remaining literal would add configuration without an identified tuning need.

Revisit release clearance, cast radius, and stack spacing when introducing differently sized items; those values should then reflect their geometry. Revisit the 12-tick knockback interval if the physics tick rate or desired knockback duration changes.

## 13. Per-request allocations — defer optimization

The arrays represent discrete player operations and stable item-ID lists. Pooling or reusing them would require careful ownership across pending requests, prediction replay, and serialization.

There is no established performance requirement that justifies that complexity. Profile allocation cost if rapid-fire mechanics or larger inventories make operation frequency materially higher.

## 14. Host registration in both callbacks — retain both calls

The second registration is not redundant for ownership initialization. FishNet reports `IsOwner == false` during the host's `OnStartNetwork`, before client initialization. `RegisterPlayer` uses `IsOwner` to assign `LocalInventory`, so registration in `OnStartClient` establishes the host's local inventory reference.

Guarding that call with `if (!IsServerInitialized)` can leave `LocalInventory` unset and break host pickup interaction. Keep both calls; the extra holder refresh is preferable to introducing a separate ownership-registration path for this minor cost.

Sources: [NetworkObject.QOL.cs](Assets/FishNet/Runtime/Object/NetworkObject/NetworkObject.QOL.cs), [PlayerInventory.cs](Assets/Game/Runtime/Inventory/PlayerInventory.cs), [WorldItemRegistry.cs](Assets/Game/Runtime/Items/WorldItemRegistry.cs).
