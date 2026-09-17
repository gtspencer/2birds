# Eight-player networking plan review

## Findings summary

The plan is broadly accurate in its analysis of the codebase and its proposed implementation order. The review findings match real code behavior, the capacity changes are correctly identified, and the dependency ordering between steps is sound. Below are corrections, risks, and suggestions grouped by severity.

---

## Incorrect assumptions

### 1. Multipass `GetConnectionState(server: true)` is unsupported

`SessionController.StopSession` (line 157) polls `transport.GetConnectionState(true)` in a loop to wait for the server socket to drain before returning to the menu. `Multipass.GetConnectionState(bool server)` **logs an error and returns `Stopped` immediately** when called for the server side (Multipass.cs line 264–269). It only works for the client via `ClientTransport`.

This means the shutdown-drain loop will silently exit on the first frame when using Multipass, skipping the transport-quiesce wait the coroutine depends on. The plan does not address this — it only says to "replace `SessionController`'s unconditional Tugboat calls with the selected session connection path."

**Impact:** The coroutine will race through shutdown without waiting for the actual transport to stop, potentially triggering socket reuse errors or stale callback delivery on the next session attempt.

**Fix needed:** Use `Multipass.GetConnectionState(server, transportIndex)` for the active transport, or track which transport index is in use and query it directly.

### 2. Multipass `GlobalServerActions` conflicts with "start only the selected server listener"

Step 1 says: "Start only the selected server listener." Multipass with `GlobalServerActions = true` (its default) starts **all** child transports when `ServerManager.StartConnection()` is called (Multipass.cs lines 826–841). Setting `GlobalServerActions = false` makes `StartConnection(server: true)` fail with an error (line 519–521).

The workaround is to call `Multipass.StartConnection(true, transportIndex)` directly for the desired transport, bypassing `ServerManager`. But `StopConnection(server: true)` through `ServerManager` also requires `GlobalServerActions = true`, so shutdown has the same problem. This is a design constraint of the bundled Multipass implementation, not just a configuration knob.

**Impact:** Without addressing this, either both transports start simultaneously (wasting a listener and potentially conflicting on ports/Steam state), or server start/stop breaks entirely.

**Fix needed:** Either manage transport start/stop per-index directly on the Multipass component (bypassing `ServerManager` for those calls), or remove the unused transport from Multipass's list at runtime before starting. The plan should specify which approach to take.

### 3. `Assets/Game/README.md` does not exist

Step 3 says: "Update the two-player description in `Assets/Game/README.md`." No such file exists in the project. This line item should be removed.

---

## Potential bugs in the plan's proposed changes

### 4. `incidentPending` fix may need more than "accepted/rejected outcome"

The plan correctly identifies that `AcceptIncident` can reject a report (mismatched `revision != StateRevision`) without clearing `incidentPending`. But the proposed fix — "Give incident reports an explicit accepted/rejected outcome" — understates the problem.

`incidentPending` is set on the **simulating client** (line 395), then the report travels via `ServerRpc` to the server's `AcceptIncident`. On rejection, the server never sends anything back to the client. The client's `incidentPending` remains `true` until `TargetResume` (line 193) or `InstallBaseline` (line 272) happens to clear it — both of which require a driver handoff or epoch change, not just a state revision bump.

**The fix must clear `incidentPending` on the reporting client**, not just on the server. This requires either a targeted RPC response to the reporter, or piggybacking the rejection on the next state broadcast. Simply adding accepted/rejected to the server-side `AcceptIncident` method won't reach the client that set the flag.

### 5. Seat request rejection under Multipass transport switching

`GolfCartNetwork.Request` (line 131) checks `cartRevision != StateRevision` and rejects immediately. With more players and transport-layer latency differences between Steam and Tugboat paths, rejection rates will increase. The plan acknowledges this in "Concurrency and lifecycle decisions" but does not propose any concrete change for step 4 beyond "Return an occupied/busy result."

For eight players, the probability of two players requesting different empty seats from the same revision is non-trivial. The plan should specify whether to relax the revision check for non-conflicting seat requests (different empty seats) during step 4, or defer it. Currently, step 4 says "An unrelated passenger change should not invalidate a request for another still-empty seat" — but without specifying the mechanism, the implementation could easily miss this.

---

## Risks and omissions

### 6. `SessionController.Start` casts `GetComponent<Tugboat>()` directly

Line 45: `transport = GetComponent<Tugboat>()`. The plan says to replace Tugboat calls but doesn't explicitly call out that the `transport` field type is `Tugboat`, not `Transport`. When Multipass replaces Tugboat as the NetworkManager's transport, this `GetComponent<Tugboat>()` will return `null` (Multipass is not a Tugboat), and every subsequent `transport.*` call in `StartSession`, `ServerState`, and `StopSession` will throw `NullReferenceException`.

The `transport` field must become either a `Transport` reference (losing Tugboat-specific methods like `SetServerBindAddress`, `SetPort`) or the code must conditionally access the Tugboat child transport through Multipass when in local mode. This is a foundational change that step 1 must address explicitly.

### 7. `SetServerBindAddress`, `SetPort`, `SetTimeout` are Tugboat-specific

`SessionController.StartSession` calls `transport.SetServerBindAddress`, `transport.SetPort`, and `transport.SetTimeout` (lines 65–72). These are `Tugboat`-specific methods, not part of the `Transport` base class. Multipass does not expose them. FishySteamworks likely has a different connection configuration API (Steam ID–based, not IP/port).

The plan says "Replace `SessionController`'s unconditional Tugboat calls with the selected session connection path" and "Keep IPv4/port setup inside the temporary local path." This is correct directionally but should specify that `StartSession` needs two entirely different configuration branches, not just a transport swap.

### 8. `SteamInputGlyphs` creates a second `SteamAPI.Init` / `SteamAPI.Shutdown` path

The plan correctly identifies that `SteamInputGlyphs` lazily calls `SteamAPI.Init` (line 34). But the plan says: "Make `SteamInputGlyphs` consume that existing Steam state and retain ownership only of its glyph textures."

The subtlety: `SteamInputGlyphs` also initializes **Steam Input** (`SteamInput.Init`, line 35) and shuts it down in `Dispose` (`SteamInput.Shutdown`, line 113). Steam Input initialization is separate from `SteamAPI.Init`. If the new Steam lifecycle owner calls `SteamAPI.Init`, `SteamInputGlyphs` must still independently manage `SteamInput.Init`/`SteamInput.Shutdown`, or the new lifecycle owner must also own Steam Input initialization. The plan doesn't mention `SteamInput` at all.

### 9. `InteractionTooltip` instantiates its own `SteamInputGlyphs`

`InteractionTooltip.cs` (line 13) creates `new SteamInputGlyphs()` directly. Each `InteractionTooltip` instance carries its own Steam API lifecycle. If the HUD is disposed and recreated (which happens on scene transitions), a new `SteamInputGlyphs` may try to re-initialize Steam after the persistent lifecycle owner already has it running.

The plan mentions `HudController` disposing the tooltip but doesn't account for `InteractionTooltip` directly owning a `SteamInputGlyphs` instance. The glyph provider needs to become a shared service, not a per-tooltip allocation.

### 10. Inventory `ObserversRpc` sends all 24 slots including world-item ID arrays to every client

The plan correctly identifies this (review finding "Unnecessary inventory traffic") and step 5 proposes sending full snapshots only to the owner. However, `ObserversInventory` is called on **every** `ProcessRequest`, including `Select` operations that only change the selected slot index. With eight players each making selection changes, this is `7 × N` unnecessary full-inventory broadcasts per selection.

Step 5 says "Start with complete owner-only snapshots" — but the plan should note that changing `ObserversInventory` to `TargetRpc` also means the host path (line 192: `if (IsOwner) AcceptInventory(...)`) needs adjustment. When the host is the owner, `AcceptInventory` is called directly *and* `ObserversInventory` fires. The `IsServerInitialized` guard inside `ObserversInventory` (line 249) prevents double-processing on the host, but switching to `TargetRpc` changes that flow.

### 11. No baseline completion message means late joiners race items

The plan identifies this (review finding "Joining is not gated on the item baseline") and step 6 proposes a completion message. However, the plan should note that `WorldItemRegistry.SendBaseline` (line 122) adds the connection to `observers` **before** sending any lifecycle batches. This means the new observer immediately starts receiving live motion updates and lifecycle changes while the baseline is still being sent. A lifecycle update for an item that hasn't been baselined yet will be processed via `earlyMotion` buffering, but a **removal** of an item mid-baseline means the client receives a removal for an item it may not have created yet — which is handled (the removal just sets state to Removed), but the ordering guarantees are fragile.

Step 6 should specify whether the observer should be added to the broadcast set before or after the baseline is complete.

### 12. `GameTransport` subclass relationship

`GameTransport` extends `Tugboat` (line 10). The `SessionRoot.prefab` likely has `GameTransport` as the component, not raw `Tugboat`. The plan references both names but doesn't clarify that `GameTransport` is the actual component on the prefab. When step 1 says "the existing `GameTransport`/Tugboat," this is fine, but implementers should know that `GetComponent<Tugboat>()` in `SessionController` returns the `GameTransport` instance (since it inherits from Tugboat) — but `GetComponent<GameTransport>()` would be more precise and matters if Multipass also has a Tugboat child.

### 13. Spawn slot stored as `byte` limits future expansion

`PlayerNetworkState.Initialize` takes a `byte slot` (line 25–27), and `GamePlayerSpawner` casts `(byte)slot` (line 57). For eight players this is fine, but if spawn points ever exceeded 255 this would silently truncate. Minor, but worth noting since the plan proposes defining capacity in one place.

---

## Suggestions

### 14. Step 1 and 2 ordering could cause a long period without a working game

Steps 1 and 2 together are a large change that replaces the session startup, transport, and connection flow before any gameplay changes. During this period the game cannot be tested in its original two-player mode (Tugboat calls are replaced) until the new transport selection is fully working. Consider keeping the original `SessionController` startup working alongside the new code until the transport switch is validated, rather than replacing it in step 1.

### 15. Consider `ExcludeOwner` before full owner-only refactor

FishNet's `[ObserversRpc(ExcludeOwner = true)]` could be added to `ObserversInventory` as an intermediate step. The owner already receives its inventory state through the direct `AcceptInventory` call (line 192) and through `TargetInventory` for new observers. Adding `ExcludeOwner` would eliminate the duplicate delivery to the owner without changing the RPC to `TargetRpc`, reducing risk.

This doesn't fix the broadcast-to-all-observers problem, but it's a one-line change that removes one copy of the full inventory from every operation on the host's own inventory.

### 16. The `SessionOverlay.Update` UI query is called every frame

`SessionOverlay.Update` (line 39–41) calls `root.Q<Label>("session-info")` every frame to update the player count label. The plan's step 3 says "Cache the session-info label when binding the overlay" — this is correct and should be done, but the plan lists it after the capacity change as if it's a minor cleanup. It's actually a per-frame allocation/query that matters more with the overlay visible during longer lobby phases.

### 17. Item traffic estimate may undercount due to lifecycle messages

The performance section estimates item motion traffic at ~0.66 Mbps per guest for 64 active items. But `ItemLifecycleBatch` messages (pickup, release, held-state changes, removals) are sent on the reliable channel to all observers. With eight players actively picking up and throwing items, lifecycle traffic could be significant — each pickup generates at least one lifecycle broadcast (the `SetHeld` call in `WorldItemRegistry`), and each throw generates a release lifecycle broadcast plus the motion snapshot.

The estimate only covers `ItemMotionBatch` (unreliable). The reliable lifecycle overhead should be included in the budget.

### 18. `PlayerSeating.Players` is a static dictionary

`PlayerSeating.Players` (line 19) is `static`. In a single-process host+client setup this is fine since the host and client share the process. But it means the dictionary is shared across all NetworkManager instances if multiple exist. This isn't a bug for the proposed architecture, but it's worth noting when reasoning about the local development path where multiple processes each have their own dictionary.

---

## Confirmed correct

The following plan claims were verified against the code:

- Transport limit is hardcoded to 2 in `SessionController.StartSession` (line 70) and `SessionRoot.prefab` (value `_maximumClients: 2`)
- `SessionOverlay` hardcodes `/ 2` in the display string (line 41)
- `GamePlayerSpawner` disconnects when no spawn slot is available (line 63)
- `SteamInputGlyphs` lazily calls `SteamAPI.Init` only on first glyph request (line 34)
- `WorldItemRegistry.UnregisterPlayer` removes (deletes) held items instead of dropping them (lines 223–229)
- `PlayerInputReader.OnStartClient` calls `PlayerReady` directly with no baseline gate (line 65)
- `WorldItemRegistry.SendBaseline` sends a start message but no completion message (line 126 sends `ItemBaselineStart`, no end)
- Steamworks.NET is installed (`manifest.json` line 3)
- No FishySteamworks transport is present in the project
- `steam_appid.txt` contains `480`
- Multipass transport is bundled with FishNet (files exist under `Assets/FishNet/Runtime/Transporting/Transports/Multipass/`)
- `GolfCartNetwork.EmptySeats` creates exactly 4 seats (line 77–81), `seats` array is length 4 (line 89)
- `incidentPending` is only cleared by `TargetResume` (line 193), timeout (line 228), and `InstallBaseline` (line 272) — not by rejection in `AcceptIncident`
- `ObserversInventory` has no `ExcludeOwner` or `ExcludeServer` attribute
- The cart registry (`GolfCartNetwork.Carts`) supports multiple instances (line 42, keyed by `ObjectId`)
- `SessionAuthenticator` requires the host to authenticate first before admitting guests (line 41)
