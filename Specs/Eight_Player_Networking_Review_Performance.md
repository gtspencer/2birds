# Eight-player networking: scalability and performance review

Review of the working changes on `main` for host CPU/bandwidth scalability at eight players, extensibility for future network messages, adherence to industry conventions, and concrete opportunities to reduce traffic.

---

## Overall assessment

The architecture is solid and will work reliably for eight players on the target Ryzen 5 3600 hardware. The design follows industry-standard patterns: client-predicted movement with server reconciliation, owner-simulated vehicles with epoch-based motion replication, and a lean custom item broadcast system. Host upload bandwidth is the primary scaling concern, not CPU. None of the issues below are blockers for shipping; they are ordered by impact.

---

## 1. Bandwidth: item motion fan-out is the largest single cost

**File:** `WorldItemRegistry.cs:435–442`

The host broadcasts `ItemMotionBatch` to every observer at ~20 Hz. Each `ItemMotion` struct is 64 bytes (3 × `uint` + 3 × `Vector3` + `Quaternion`). At 64 awake items and 7 guests, the plan's own estimate is **~4.6 Mbps upload** from the host for item motion alone, before player state, cart motion, lifecycle events, and UDP/Steam overhead.

That figure is sustainable on a symmetric broadband connection, but it occupies a significant fraction of a typical residential upload pipe (10–20 Mbps). Adding a second cart, more items, or higher item activity pushes this closer to the limit.

### Recommendations

| Change | Estimated savings | Effort |
|---|---|---|
| **Quantize motion fields.** Compress `Vector3` position to 3 × `half` (6 bytes vs 12) for items within a ±1000 m range and quantize velocity/angular velocity to `short` with a known max. Rotation as smallest-three quaternion (6 bytes vs 16). Drops ~64 bytes → ~30 bytes per sample, cutting item motion traffic roughly in half. | ~45–50% of item motion bandwidth | Medium |
| **Delta compression for sleeping transitions.** When an item goes to sleep, send only its ID + position + rotation (no velocity fields). Currently the full `ItemMotion` is broadcast on sleep, including zero velocity. | Small per-item, meaningful at 64 items | Low |
| **Adaptive send rate based on item velocity.** Slow-rolling items don't need 20 Hz updates. Items below a velocity threshold could drop to 10 Hz or 5 Hz. The existing `activePhysicsItems` set already partitions sleeping vs awake; add a second tier. | 20–40% for typical play (most items slow after initial throw) | Medium |

**Not yet recommended:** Distance-based observer culling. The plan notes this correctly — `WorldItemRegistry` uses its own observer set (`HashSet<NetworkConnection> observers`), so FishNet's built-in observer conditions wouldn't reduce item traffic without custom integration. Worth revisiting only if the map grows large enough that players are spatially separated.

---

## 2. Bandwidth: inventory is now owner-only (good), but `LobbyRoster` is full-snapshot

**File:** `SessionController.cs:333–338`

`PublishRoster()` broadcasts a `LobbyRoster` containing all `LobbyMember` structs to every client on every membership or readiness change. Each `LobbyMember` carries a `string Name` (up to 32 chars = up to 66 bytes UTF-8 with FishNet's length prefix) plus 3 other fields.

At eight players this is small (~600 bytes), but it fires on every guest connect, disconnect, and readiness change. During a join storm (multiple guests connecting), this creates `O(n²)` reliable messages (each join triggers a broadcast to all current members).

### Recommendation

This is fine for eight players and doesn't need immediate action. If you ever scale beyond 8, switch to delta roster updates (send only the changed member). For now, the reliable channel handles this gracefully.

---

## 3. CPU: prediction replay at 60 Hz scales with player count

**File:** `MvpValidation.cs:181–183` (diagnostics), player prediction configuration on `Player.prefab`

FishNet's prediction replays `N` ticks of physics on every reconciliation for every predicted object. At 60 Hz with 100 ms RTT, that's ~6 replayed ticks per reconcile. The plan notes this correctly as the primary CPU risk.

With 8 players, the host runs its own prediction plus simulates 7 remote players. Each remote player with state forwarding causes the host to maintain and replay their state. This is `O(players × replay_ticks)` physics steps per frame.

### Current state

The existing diagnostics (`PredictionDiagnostics`) already track `tickMs` and `replayMs` and warn after 30 consecutive overloaded ticks. This is the right approach — measure before optimizing.

### Recommendations

- **Run the 8-player benchmark on the Ryzen 5 3600 before shipping.** The diagnostics infrastructure exists; use it. The `MvpValidation` automation can drive this.
- **If replay CPU is a problem**, the cheapest fix is reducing the tick rate from 60 Hz to 30 Hz for the network simulation while keeping rendering at 60+ FPS. FishNet supports this split. This halves replay cost. Movement would need interpolation at the rendering layer, which player prediction already handles.
- **If that's still not enough**, disable state forwarding for remote players and replicate their movement via snapshot interpolation instead of prediction. This trades visual smoothness for CPU headroom. Don't do this preemptively — it requires reworking player collisions.

---

## 4. Extensibility: adding new network messages

The current message architecture is clean and extensible:

| Pattern | Files | Assessment |
|---|---|---|
| `IBroadcast` structs for session/lobby messages | `SessionMessages.cs` | Standard FishNet pattern. Adding a new message = define struct + register/unregister. No routing table or message ID allocation needed. |
| `IBroadcast` structs for item lifecycle/motion | `WorldItemMessages.cs` | Same pattern. Batched correctly. |
| `ServerRpc` / `ObserversRpc` / `TargetRpc` for gameplay | `GolfCartNetwork.cs`, `PlayerInventory.cs`, `PlayerSeating.cs` | Standard FishNet RPCs. Adding new cart/player interactions follows the same pattern. |

**Verdict:** Adding more network messages (chat, emotes, game events) is straightforward and doesn't require architectural changes. Each new `IBroadcast` or RPC is independent. The registration/unregistration lifecycle in `Start()`/`OnDestroy()` is consistent and correct.

**One thing to watch:** `SessionController.Start()` registers 5 broadcast handlers. `WorldItemRegistry.Start()` registers 5 more. These are all on the same `NetworkManager` and don't conflict, but as the count grows, consider grouping related handlers into their own `NetworkBehaviour` components to keep registration manageable. This is a code organization suggestion, not a performance concern.

---

## 5. Host CPU cost: cart physics and collision detection

**File:** `GolfCartController.cs:71–150`

Each cart runs 4 wheel raycasts per physics step, plus collision accumulation in `OnCollisionEnter`/`OnCollisionStay`. The `TryRecovery` method does `OverlapBoxNonAlloc` for each chassis collider at each candidate position, but it only runs on recovery attempts (rare).

With one cart, this is negligible. With a second cart, the physics cost roughly doubles but remains small compared to player prediction replay. The real concern the plan identified — consistent cart-to-cart collision outcomes — is a correctness problem, not a performance one.

### Assessment

Cart physics will not be a bottleneck at 8 players with 2 carts. No changes needed.

---

## 6. Memory allocation patterns in hot paths

Several hot-path methods allocate on every call:

| Location | Allocation | Frequency |
|---|---|---|
| `PlayerInventory.AddId()` (:352–359) | `new uint[]` on every stack change | Every pickup/drop |
| `PlayerInventory.RemoveId()` (:363–378) | `new uint[]` when removing from a stack > 1 | Every release |
| `GolfCartNetwork.Commit()` (:290) | `new List<SeatTransition>(4)` | Every seat change |
| `GolfCartNetwork.EmptySeats()` (:79–83) | `new CartOccupant[4]` | Every ejection |
| `WorldItemRegistry.FlushLifecycle()` (:450–457) | Allocates via `new ItemLifecycleBatch` containing the `List<ItemRecord>` | Every lifecycle publish |

### Assessment

None of these are in per-frame loops. Pickups, drops, and seat changes are player-initiated and capped at human input speed. The lifecycle batch reuses a cached `List<ItemRecord>` and only creates the broadcast struct wrapper. These allocations won't cause GC pressure at 8 players.

The one pattern to watch is `ItemMotionBatch` creation in `FlushMotion()` — this runs at ~20 Hz on the host with up to 8 batches per snapshot (64 items ÷ 8 per batch). The `motionBatch` list is reused, but each `new ItemMotionBatch { Items = motionBatch }` passes the list by reference, so FishNet serializes it immediately before `motionBatch.Clear()`. This is fine.

---

## 7. Transport: FishySteamworks and Multipass configuration

**File:** `GameSteamTransport.cs`, `SessionController.cs:106–133`

The transport setup is correct:
- `GlobalServerActions = false` prevents Multipass from starting both transports simultaneously.
- `SetClientTransport(selectedIndex)` before connecting ensures the right child is used.
- `StartConnection(true, selectedIndex)` starts only the selected listener.
- The selected transport's max clients is set correctly (8 for local, 7 for Steam since the host counts separately).

### Assessment

This follows Multipass best practices. The transport layer won't be a bottleneck — FishySteamworks uses Steam's reliable/unreliable channels which handle congestion and retransmission at the platform level.

**One minor concern:** `GameSteamTransport` overrides `SendToServer`, `SendToClient`, `HandleServerReceivedDataArgs`, and `HandleClientReceivedDataArgs` for payload counting. These virtual dispatch calls happen on every packet. The overhead is negligible (one dictionary lookup per packet), but the `#if UNITY_EDITOR || DEVELOPMENT_BUILD` guard correctly excludes this from release builds.

---

## 8. Session lifecycle and shutdown correctness

**File:** `SessionController.cs:371–410`

`StopSession` is a coroutine that:
1. Waits for scene loading to finish
2. Stops the network
3. Waits for client and server transport states to reach `Stopped` and pending Steam operations to complete
4. Loads MainMenu
5. Resets to `Idle`

This is the correct shutdown sequence. The `attempt` counter guards against stale callbacks. The `StopNetwork` method sends disconnect messages with `iterate: true` before stopping, giving clients a clean disconnect notification.

### Assessment

The shutdown sequence is robust. No race conditions visible. The `while` loop waiting for transport states won't spin-lock because Unity yields frames between iterations.

---

## 9. Steam lobby and async operation safety

**File:** `SteamLobby.cs:39–117`

Steam async operations (lobby create/join) capture the current `attempt` number and validate it on completion. Stale results leave orphaned lobbies. New invitations are queued until the current session is idle.

### Assessment

This is the correct pattern for Steam async operations. The `pending` list tracks outstanding `CallResult` objects for disposal on shutdown. The `IsAttempt` check prevents acting on results from a cancelled session.

**One edge case to verify:** If a player receives a Steam invitation while in `SessionPhase.Stopping`, `JoinSteamLobby` sets `pendingInvite` and calls `Leave()`. But `Leave()` returns early if already stopping. The `pendingInvite` is then picked up in `Update()` when the phase reaches `Idle`. This is correct — the invitation is deferred, not lost.

---

## 10. World item registry observer management

**File:** `WorldItemRegistry.cs:128–131, 490–493`

The `observers` HashSet is populated when a client requests a baseline and cleaned up on disconnect via `ConnectionChanged`. Item motion and lifecycle batches are broadcast only to this set, not to all clients.

### Assessment

This is efficient — clients that haven't loaded the game scene don't receive item updates. However, the observer set is never pruned during gameplay except on disconnect. If a client somehow re-requests a baseline (e.g., after a reconnect), it would be added twice, which `HashSet` handles correctly. No issue.

---

## 11. `SessionOverlay.Update()` runs string formatting every frame

**File:** `SessionOverlay.cs:44–46`

```csharp
sessionInfo.text = $"{session.Mode} · {(spawner ? spawner.PlayerCount : 0)} / {session.Capacity} players";
```

This allocates a new string and updates the label text every frame. The `sessionInfo` label is cached (good), but the string interpolation runs unconditionally.

### Recommendation

Cache the previous player count and only update when it changes:

```csharp
int count = spawner ? spawner.PlayerCount : 0;
if (count != lastCount) { lastCount = count; sessionInfo.text = ...; }
```

This is a micro-optimization but demonstrates good practice for UI that runs every frame.

---

## 12. Does the host need a beefy PC?

**Short answer: No, but upload bandwidth matters more than CPU or GPU.**

| Resource | Assessment |
|---|---|
| **CPU** | The Ryzen 5 3600 target is reasonable. Prediction replay is the main cost, and the existing 60 Hz tick rate is standard. Dense 8-player collisions during replay are the stress case, but this is already budgeted for in the plan. |
| **GPU** | Rendering 8 players and 64 items is trivial for a GTX 1080+. The physics simulation runs on CPU. GPU is not a factor in networking performance. |
| **RAM** | Negligible networking overhead. The largest data structure is the `records` dictionary in `WorldItemRegistry`, which at 64 items × ~100 bytes per `ItemRecord` = ~6.4 KB. Player prediction history is 128 × ~40 bytes per player = ~40 KB total for 8 players. |
| **Upload bandwidth** | The real constraint. At 64 active items, item motion alone is ~4.6 Mbps. Add player state replication (~0.5 Mbps for 8 players at 60 Hz), cart motion (~0.1 Mbps per cart), and reliable lifecycle/lobby messages, and the host needs a sustained **6–8 Mbps upload**. Most modern connections support this, but older DSL or mobile hotspots will struggle. |
| **Download bandwidth** | Each guest receives ~0.7–1 Mbps (item motion + other players + cart). This is not a concern. |

---

## 13. Industry standard comparison

| Aspect | This implementation | Industry norm | Assessment |
|---|---|---|---|
| Network model | Client-predicted movement, server-authoritative inventory/seating | Standard for competitive and cooperative games (Overwatch, Rocket League use similar) | Correct |
| Vehicle replication | Owner-simulated with epoch-based motion frames | Similar to Rocket League's car replication; epochs are a clean abstraction over ownership transfers | Good |
| Item replication | Custom broadcast system with batching and sleep optimization | Most engines use their built-in replication; custom is justified here because FishNet's NetworkObject per-item would be much heavier | Good tradeoff |
| Transport | Steam P2P via FishySteamworks with Multipass fallback to Tugboat | Standard for Steam games (Rust, Among Us use Steam networking) | Correct |
| Lobby | Steam friends-only lobby with FishNet admission | Standard Steam lobby pattern | Correct |
| Tick rate | 60 Hz simulation, 20 Hz item motion broadcast | Varies: Fortnite 30 Hz, Valorant 128 Hz, typical co-op 30–60 Hz. 60 Hz is on the higher end for a co-op game, providing smooth prediction but costing more bandwidth/CPU | Reasonable, could drop to 30 Hz if needed |
| Snapshot interpolation | Used for remote items with configurable delay | Standard (Source engine, Overwatch) | Correct |

---

## Summary of recommended changes by priority

### Should do before shipping

1. **Run the 8-player, 64-item benchmark on target hardware** using the existing `MvpValidation` infrastructure. The diagnostics code is already there — use it to establish real numbers for host upload, frame time, and replay cost.
2. **Fix `SessionOverlay.Update()` string allocation** — trivial change, good practice.

### Should do if bandwidth is tight

3. **Quantize `ItemMotion` fields** for the unreliable motion channel. This is the single highest-impact optimization, potentially halving item motion bandwidth.
4. **Add a velocity-based adaptive send rate** for items. Slow items don't need 20 Hz updates.

### Nice to have

5. **Delta roster updates** instead of full snapshots. Only matters if you ever exceed 8 players.
6. **Consider dropping the network tick rate to 30 Hz** if prediction replay CPU is a problem on the target Ryzen 5 3600. This halves both replay cost and bandwidth, at the expense of slightly less responsive remote player positions (mitigated by interpolation).

### Don't do yet

- Distance-based item observer culling (map isn't large enough to justify the complexity)
- Object pooling for broadcast structs (allocations aren't in hot paths)
- Replacing prediction with snapshot interpolation for remote players (trades visual quality for CPU, measure first)
