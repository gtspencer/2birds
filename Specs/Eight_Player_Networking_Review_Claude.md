# Eight-player networking code review

Review of the working changes on main, covering the new Steam integration, lobby flow, capacity raise, inventory/item changes, departure drops, and supporting UI/diagnostic work.

---

## Bugs

### 1. `FinishIncident` never clears `incidentPending` on acceptance

`GolfCartNetwork.FinishIncident` (line 441) only clears `incidentPending` when the incident was **rejected**:

```csharp
if (motionEpoch != epoch || sequence != eventSequence || !incidentPending || accepted) return;
incidentPending = false;
```

When `accepted` is true the method returns early and `incidentPending` stays true. The host path clears it through `TargetResume` after the handoff completes, but if the handoff times out or the pending change is overwritten by another accepted incident (`AcceptIncident` line 420 sets `pending.Eject = true` on an existing pending), the simulator can get stuck with `incidentPending = true` and never report another incident.

The plan doc (section "Correctness issue: a rejected cart incident can remain pending") identified this class of bug. The implementation added `FinishIncident` and `TargetResume` / `ReassessIncident`, but the acceptance path still relies on the full handoff round-trip completing to clear the flag. A timeout at line 228–234 does clear it (`incidentPending = false`), but only after a 2-second deadline — during which the driver cannot report new crashes.

**Risk:** Driver hits a wall, gets ejected, lands, and immediately hits another wall within 2 seconds — the second crash is silently dropped.

### 2. `SteamLobby.MembershipChanged` operator precedence

Line 188–189:
```csharp
if (message.m_ulSteamIDUserChanged == originalHost && change != EChatMemberStateChange.k_EChatMemberStateChangeEntered ||
    SteamMatchmaking.GetLobbyOwner(lobby).m_SteamID != originalHost)
```

C# `&&` binds tighter than `||`, so this reads as:
```
(host AND change != Entered) OR (owner != originalHost)
```

The second clause fires unconditionally — if Steam temporarily reassigns lobby ownership (e.g., during a network hiccup or for any internal reason) before the host actually leaves, the guest would end the session. The intended logic is likely:
```csharp
if ((message.m_ulSteamIDUserChanged == originalHost && change != EChatMemberStateChange.k_EChatMemberStateChangeEntered) ||
    SteamMatchmaking.GetLobbyOwner(lobby).m_SteamID != originalHost)
```

This is what the current precedence produces, so the code is **technically correct** but fragile — a future reader adding another condition could easily misread the grouping. Consider explicit parentheses.

**However**, the `SteamMatchmaking.GetLobbyOwner` check on every membership change means any ownership transfer (even a Steam-internal one that doesn't involve the host leaving) will kill the session. The plan says "do not interpret a change of Steam lobby owner as gameplay host migration" — but this check is stricter than that: it interprets ownership transfer as host departure. If Steam reassigns ownership when a *guest* leaves, this is a false positive. Steam does reassign ownership when the current owner leaves, but the docs don't guarantee it won't reassign for other reasons.

**Risk:** Spurious session termination if Steam reassigns lobby ownership for any reason besides the host actually leaving the lobby.

### 3. `WorldItemRegistry.DropDepartingItems` accesses `items[record.Motion.Id]` without existence check

Line 263:
```csharp
spacing = Mathf.Max(spacing, items[record.Motion.Id].DropDiameter + 0.05f);
```

If a held record exists in `records` but its corresponding `WorldItem` has already been removed from `items` (e.g., another system removed it in the same frame, or the item was removed and pooled while still marked as held), this will throw `KeyNotFoundException`.

**Risk:** Host crash on disconnect if item state is inconsistent.

### 4. `SessionController.SessionId` is `uint` but used as `SessionAdmission.Attempt` comparison against client's `SessionId`

In `SessionAuthenticator.ClientState` (line 34), the client sends `session.SessionId` as the `Attempt` field. In `SessionController.ReceiveAdmission` (line 245), the admission is validated with `message.Attempt != SessionId`. But `SessionId` increments via `++SessionId` in `BeginAttempt` (line 119) while `attempt` (the internal guard) increments separately at line 118. These are two different counters tracking the same logical concept.

`IsAttempt` uses the `attempt` field, while the wire protocol uses `SessionId`. If they ever diverge (e.g., `BeginAttempt` is called but fails after incrementing `SessionId` but before incrementing `attempt`, which can't happen currently since they're adjacent), the session guards would be inconsistent. Not a current bug, but the dual counters are a latent maintenance risk.

### 5. `SetFrameCap` sets FishNet's render frame rate to the rendering cap

Line 99:
```csharp
Network.ServerManager.SetFrameRate((ushort)FrameCap);
Network.ClientManager.SetFrameRate((ushort)FrameCap);
```

FishNet's `SetFrameRate` sets the **network processing frame rate**, not just a rendering cap. At 120 FPS, FishNet will process network updates at 120 Hz even though the tick rate remains 60 Hz. This means FishNet will poll and flush network buffers twice as often, increasing CPU overhead. The plan says "raising a rendering cap must not increase network traffic or change vehicle/player behavior" — while traffic per tick doesn't change, the polling frequency doubles or triples. This may be intentional to reduce latency at higher frame rates, but it contradicts the plan's stated constraint.

---

## Inconsistencies and design concerns

### 6. Solo mode sets port 0 but `SessionRoot.prefab` default is 8

The prefab's `_maximumClients` was raised to 8. Solo sets `Capacity` to 1 via `SessionController.Capacity` getter, and `BeginAttempt` sets `selectedTransport.SetMaximumClients(UsingLocal ? Capacity : Capacity - 1)`. For solo this correctly sets max clients to 1. No issue here, but worth noting the prefab default (8) is overridden at runtime in every case — the serialized value only matters if `SetMaximumClients` isn't called, which would be a bug.

### 7. `SteamLifetime` and `SessionController` both have singleton patterns with subtly different destruction behavior

`SteamLifetime.OnDestroy` calls `SessionController.Instance.ShutdownPlatform()` (line 49), and `SessionController.OnApplicationQuit` also calls `ShutdownPlatform()`. Depending on Unity's destruction order, `ShutdownPlatform` could be called twice — which is guarded by the `quitting` flag. But if `SteamLifetime` is destroyed before `SessionController` (e.g., during a scene transition that shouldn't happen but could in editor), `ShutdownPlatform` would try to shut down Steam through the `SteamLifetime` reference which may have already nulled its instance.

`ShutdownPlatform` line 429: `if (steam) steam.Shutdown()` — this uses Unity's implicit null check, so if `steam` is destroyed but the C# reference isn't null, this would still work (Unity's fake-null). But `SteamLifetime.Shutdown` then calls `SteamAPI.Shutdown()` which may have already been called by `SteamLifetime.OnDestroy` → `ShutdownPlatform` → `steam.Shutdown()`. Double `SteamAPI.Shutdown()` is documented as safe by Valve, so this is not a crash, but it's a confusing lifecycle.

### 8. `LobbyMember` is a mutable struct passed by value

`SessionMessages.cs` defines `LobbyMember` as a struct with mutable fields. In `ReceiveReady` (SessionController line 317–320):
```csharp
member.Ready = true;
admitted[connection] = member;
```

This works because the struct is explicitly re-assigned to the dictionary. But any code that reads `admitted[connection].Ready` without the re-assignment pattern would silently fail to mutate. This is a common C# mutable-struct footgun.

### 9. No validation that `FishySteamworks` sources are the expected pinned revision

The implementation doc says FishySteamworks is pinned to `0320459`. The `Assets/Plugins/` directory is untracked. There's no compile-time or runtime assertion that the vendored sources match the expected revision. If someone updates the plugin without updating the implementation doc (or vice versa), the transport-specific assumptions (host counting, startup behavior, background thread removal) could silently break.

### 10. `GameSteamTransport.Traffic` is never cleared between sessions

`GameSteamTransport.Traffic` accumulates across sessions because the transport component persists on `SessionRoot`. `MvpValidation.Update` computes per-second deltas, so this doesn't affect diagnostics output. But the dictionary grows unboundedly if peer connection IDs are reused with different values across sessions (unlikely but possible with FishNet's connection ID allocation).

### 11. `WorldItemRegistry.DropDepartingItems` drops don't respect world bounds

Items are dropped at `origin + offset` without checking if the resulting position is inside `worldBounds`. If a player disconnects near the world boundary, dropped items could spawn outside bounds and be immediately removed by the next `AfterTick` cleanup, effectively deleting them.

### 12. `SessionController.StopSession` loads `MainMenu` scene without checking if it exists

Line 406: `SceneManager.LoadSceneAsync("MainMenu", LoadSceneMode.Single)`. If the scene name changes or is missing from the build, this will throw. Low risk since it's a core scene, but there's no fallback.

---

## Potential issues at scale (8 players)

### 13. `PublishRoster` broadcasts the full roster array on every membership/readiness change

Line 333–338: Every time a player joins, leaves, or sends `SessionReady`, the entire `LobbyMember[]` is broadcast to all clients. With 8 players joining in quick succession, this produces 8 broadcasts of growing arrays. Not a bandwidth concern at lobby stage, but worth noting the O(n²) total data.

### 14. `GamePlayerSpawner.SpawnPlayer` uses linear scan for slot allocation

Line 62–66: For each spawn, it iterates all players to check each slot. With 8 players this is 8×8 = 64 checks at worst. Trivially fine at this scale, but the nested iteration pattern is worth noting.

### 15. Seat request feedback doesn't distinguish "seat was just taken" from "cart is processing another change"

Both return `SeatRequestResult.Busy` or `SeatRequestResult.Occupied`, but the player just sees "Cart is busy. Try again." or "That seat is occupied." With 8 players potentially racing for 4 seats, the feedback could be more specific about *why* the request failed and whether retrying immediately would help.

### 16. `DropDepartingItems` grid layout may stack items vertically

Line 269: Items are laid out on a `width × height` grid with `spacing` between them and 1.0f vertical offset. For a player holding many items, the grid could extend quite high (e.g., 24 items with width=5 means 5 rows, placed at y offsets 1.0, 1.24, 1.48, 1.72, 1.96 above origin). Items at the top rows would fall further and may scatter unpredictably. Not a bug, but the visual result of a player with a full inventory disconnecting could look odd.

---

## Missing pieces called out in the plan but not yet implemented

These are not bugs — they're plan items that the current changes don't address, listed so you know what's still open:

1. **Cart incident `incidentPending` fix** — The plan's step 4 calls for sending accepted/rejected outcomes back to the reporting simulator. The current code partially addresses this with `TargetIncident`/`FinishIncident`, but the acceptance path still doesn't explicitly clear `incidentPending` on the simulator (it relies on the handoff completing and `TargetResume` being called).

2. **Item baseline completion as a readiness gate** — Implemented. `ItemBaselineComplete` is sent and `WorldReady` gates on it.

3. **Departure drops** — Implemented via `DropDepartingItems` / `OnPreDestroyClientObjects`.

4. **Rendering frame cap** — Implemented via `SetFrameCap` and the settings UI.

5. **Spawn marker validation** — Implemented in `GamePlayerSpawner.OnStartServer`.

---

## Minor style/quality notes

- `SessionController` is 452 lines with many responsibilities (session state machine, transport selection, lobby integration, readiness, shutdown). Consider extracting the transport-selection logic if this file continues to grow.
- Several `using` statements in `MvpValidation.cs` are carried over (`FishNet.Transporting.Tugboat`) that aren't used by the current code — the transport is now accessed through `SessionController.PayloadTraffic`.
- `SteamDevelopmentBuild.cs` unconditionally copies `steam_appid.txt` on every Windows dev build. If the file doesn't exist at the repo root, `File.Copy` will throw. Consider a `File.Exists` guard.
- The `MvpValidation` line `FindObjectsByType<SessionController>().Length` (line 78) should use the overload with `FindObjectsSortMode` parameter to match Unity 6 API requirements.
