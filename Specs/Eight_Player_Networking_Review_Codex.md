# Eight-player networking review

Scope: the uncommitted tracked changes and new source files, including the vendored FishySteamworks transport. P1 means a high-impact correctness issue; P2 means a functional defect or a failure under the stated conditions.

## Findings

### 1. [P1] Steam send congestion permanently discards reliable messages

**Locations:** [ServerSocket.cs:463](Assets/Plugins/FishySteamworks/Core/ServerSocket.cs#L463), [ClientSocket.cs:157](Assets/Plugins/FishySteamworks/Core/ClientSocket.cs#L157).

Both send paths only log most unsuccessful `SendMessageToConnection` results. In particular, `k_EResultLimitExceeded` means Steam did not accept the message because its outgoing queue is full. The transport retains no copy for retry, while FishNet resets the outgoing packet bundle after calling the transport. The connection stays active even when the discarded packet was reliable.

A temporarily congested Steam connection during scene loading, a large item baseline, or active gameplay can therefore lose scene messages, inventory outcomes, seat transitions, or item lifecycle records permanently. Later messages can still arrive. Losing a baseline batch but subsequently receiving `ItemBaselineComplete` can even enable gameplay with an incomplete world.

**Suggested fix:** preserve rejected reliable messages and retry them in order before subsequent reliable traffic, or explicitly disconnect when reliable delivery can no longer be maintained. Logging and continuing violates the reliability assumptions throughout the game. FishNet's bundle reset is in [TransportManager.cs:699](Assets/FishNet/Runtime/Managing/Transporting/TransportManager.cs#L699); the installed Steamworks.NET API comments document the queue-full result in `Runtime/autogen/isteamnetworkingsockets.cs:303`.

### 2. [P2] The Steam packet resize path uses the original array

**Location:** [CommonSocket.cs:132](Assets/Plugins/FishySteamworks/Core/CommonSocket.cs#L132).

When a packet has zero or one spare byte, `Send` resizes a local `arr` and writes the channel marker into that new array. It then constructs the outgoing segment from the original `segment.Array`, discarding both the resized buffer and the channel marker.

With no spare byte, the new `ArraySegment` exceeds the original array bounds and throws. With one spare byte, the packet includes an unwritten or stale channel byte, which can misclassify the received packet. The shared [ByteArrayPool](Assets/FishNet/Runtime/Utility/Performance/ByteArrayPool.cs#L18) guarantees only a minimum length, so pooled-buffer reuse must not depend on extra capacity always being available.

**Suggested fix:** construct the outgoing segment from the actual resized buffer and write the channel at `segment.Offset + segment.Count`. Correct the capacity check so one available byte is sufficient.

### 3. [P2] Accepting an invite to the current lobby tears down the current game

**Location:** [SessionController.cs:167](Assets/Game/Runtime/Networking/SessionController.cs#L167).

`JoinSteamLobby` queues every nonzero lobby ID and calls `Leave()` whenever a session is active. It never checks whether the requested lobby is already the current lobby. Every lobby participant can open the invite dialog, so a guest can send an invitation back to the host or to another current participant.

If the host accepts that invitation, the host closes the lobby and stops the server for everyone, then tries to rejoin a session whose original host has left. A guest accepting a duplicate invitation unnecessarily disconnects, drops their inventory, and rejoins as a fresh player. Repeated invitations to an in-progress join also cancel and restart that attempt.

**Suggested fix:** ignore invitations targeting the current lobby or the lobby already being joined. Compare IDs before mutating `pendingInvite` or beginning teardown.

### 4. [P2] Disconnect drops can appear inside or beyond level geometry

**Location:** [WorldItemRegistry.cs:265](Assets/Game/Runtime/Items/WorldItemRegistry.cs#L265).

Departing players' items are placed in a grid 1.5 metres forward and at least one metre above the player or seat. Collider-derived spacing separates the dropped items from each other, but there is no environment sweep or overlap check. Disconnecting while facing a nearby wall, under a low ceiling, or seated beside geometry can create items inside solid objects or on the far side of a wall. Large inventories extend the grid farther sideways and upward.

Those records immediately become world items and the returning player gets an empty inventory, so inaccessible drops can become permanent item loss. The existing manual release path already checks the environment in [PlayerInventory.cs:145](Assets/Game/Runtime/Inventory/PlayerInventory.cs#L145).

**Suggested fix:** find clear nearby drop positions using each item's cached bounds, checking both the destination and the path from the departure pose. Preserve spacing between successfully placed items and provide a clear-position fallback when the initial grid is obstructed.

### 5. [P2] Steam joins canceled before Connected leave native handles open

**Location:** [ServerSocket.cs:318](Assets/Plugins/FishySteamworks/Core/ServerSocket.cs#L318).

The server adds a native connection to `_steamConnections` only after receiving `Connected`. If a guest cancels or loses connectivity during the handshake, `ClosedByPeer` or `ProblemDetectedLocally` can arrive first. Those branches call `CloseConnection` only when the dictionary lookup succeeds, so the native handle is never closed in this case. An unsuccessful `AcceptConnection` is also only logged.

Repeated canceled or failed joins can accumulate native connection resources during a long-running hosted session. Steamworks.NET's `ESteamNetworkingConnectionState` comments explicitly require closing handles in both terminal states, including connections that never became usable.

**Suggested fix:** close the native handle for terminal callbacks even when no FishNet connection ID was allocated. Only perform FishNet removal/notification for entries that actually reached the connection dictionary, and release handles when acceptance fails.

### 6. [P2] The existing multiplayer development runner never starts the new lobby

**Locations:** [MvpValidation.cs:60](Assets/Game/Runtime/Diagnostics/MvpValidation.cs#L60), [SessionController.cs:229](Assets/Game/Runtime/Networking/SessionController.cs#L229).

The runner calls `StartSession(Host)` and waits up to 45 seconds for `InGame`. Multiplayer hosting now intentionally stops in `InLobby`, and nothing in the runner calls `StartGame`. Even with `-localNetworking`, correctly connected host and guest processes remain in the lobby until the runner records `DID_NOT_ENTER_GAME` and leaves.

Without `-localNetworking`, the runner's direct `StartSession(Join, ip, port)` call is also rejected by the new Steam-only join flow. The README still advertises the Host/Join runner modes, and the traffic-counter changes imply these paths remain useful.

**Suggested fix:** adapt the existing development runner to the shared lobby flow, including an explicit host start condition and the local-networking launch requirement. Keep the normal interactive host's manual Start behavior.

## Required Unity integration

The implementation note already assigns these steps to the user. They remain blockers in the current assets:

- **All session modes:** [SessionRoot.prefab:170](Assets/Game/Prefabs/SessionRoot.prefab#L170) still assigns GameTransport directly to TransportManager. The prefab has none of the new SteamLifetime, SteamLobby, GameSteamTransport, or Multipass components, and none of SessionController's new transport assignments. [BeginAttempt:110](Assets/Game/Runtime/Networking/SessionController.cs#L110) rejects Solo, Host, and Join until this wiring is saved.
- **Multiplayer gameplay:** [Game.unity:427](Assets/Scenes/Game.unity#L427) still assigns only two spawn markers. Once transport setup is complete, [GamePlayerSpawner:24](Assets/Game/Runtime/Networking/GamePlayerSpawner.cs#L24) ends an eight-player session when Game starts loading until six additional markers are created and assigned.

Follow the [implementation note's required Unity assignments](Eight_Player_Networking_Implementation.md#required-unity-assignments) and save those assets before distributing a build.

## Cancellation limitation

[StopSession:397](Assets/Game/Runtime/Networking/SessionController.cs#L397) waits for every pending Steam lobby operation without a deadline. This is an explicit implementation decision, but it means the connection timeout does not bound recovery time: after cancellation or timeout, a delayed Steam result keeps the session in `Stopping`, blocking another join, hosting, and Solo. If bounded recovery becomes a requirement, separate stale-callback cleanup from the ability to return to a usable menu while preserving the protections against stale results affecting a newer lobby.

## User visual validation

- After completing Unity assignments, check Solo and a host with seven guests, including eight distinct spawn positions, a long lobby wait, Start, late joining, and repeated leave/rehost cycles.
- Send an invitation back to the current host and to an existing guest. Accept it and watch for unintended session termination, inventory drops, or respawning.
- Disconnect while carrying several items beside a wall, under a ceiling, and from a moving cart. Compare drop positions across remaining clients and check that every item remains reachable.
- On a congested Steam connection, compare late-join items, inventories, and cart seats across clients; watch for an active session with missing state after send errors.
- Race players for seats and items, then change passenger occupancy during a rollover. Check rejection feedback, inventory correction, later ejections, and controller navigation through menu/lobby/game transitions.
