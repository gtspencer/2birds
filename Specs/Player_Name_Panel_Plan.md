# Player Name Panel Plan

## Goal
Show player names above remote players' heads. Use Steam name when launched through Steam, otherwise generic "Player N" fallback.

## Existing Infrastructure

**Names already exist on every client.** The auth handshake sends `SteamFriends.GetPersonaName()` as `Hello.Name`. The server stores it in `admitted[connection].Name` (with fallback `"Player {ClientId+1}"` for local/solo). The `LobbyRoster` broadcast syncs `LobbyMember[]` to all clients via `SessionController.Roster`. Each `LobbyMember` carries `.Connection` (clientId) and `.Name`.

Each spawned `NetworkObject` has `Owner.ClientId`, which maps directly to `LobbyMember.Connection`.

## Approach: Roster Lookup + World-Space TextMeshPro Billboard

No new networking, no SyncVars, no changes to existing network code. One new component, one prefab child object, one prerequisite step.

### Files

| File | Action |
|------|--------|
| `Assets/Game/Runtime/Player/PlayerNameLabel.cs` | **Create** — MonoBehaviour on a child GameObject under Graphics |
| `Assets/Game/Prefabs/Player.prefab` | **Edit** — add child `NameLabel` GameObject with TextMeshPro + PlayerNameLabel |

No USS/UXML changes. The label lives entirely in world space.

### 1. Player.prefab — New Child Object

Add a new child GameObject `NameLabel` under the `Graphics` transform (sibling of `Eyes`, `EquipSlot`):

- **Transform:** local position `(0, 1.3, 0)` — just above the player's head (capsule top is y=1.0 in Graphics space).
- **TextMeshPro** component (3D variant, not UGUI):
  - Font size: ~3
  - Alignment: center/middle
  - Color: white with slight transparency
  - No wrapping
  - Material preset: default SDF with a slight outline or shadow for readability against varying backgrounds
- **PlayerNameLabel** component (see below)
- **Layer:** set to Default (layer 0) so it's visible to all cameras, not layer 6 (Player) which the local camera might cull.

Because it's parented under Graphics (which has `NetworkTickSmoother`), the label automatically follows the smoothed player position with no extra code.

### 2. `PlayerNameLabel.cs`

A simple MonoBehaviour (not NetworkBehaviour — it doesn't need network callbacks itself; the parent Player object handles that).

**Fields:**
- Serialized `TMP_Text` reference (assigned in prefab to the sibling TextMeshPro component, or fetched via `GetComponent`).

**Initialization (`OnEnable` or called from `PlayerPresentation.OnStartClient`):**
- Get the parent `NetworkObject` via `GetComponentInParent<NetworkObject>()`.
- If `networkObject.IsOwner`: disable the `NameLabel` GameObject entirely (local player doesn't see own name). Return.
- Look up the display name from `SessionController.Instance.Roster` by matching `networkObject.Owner.ClientId` to `LobbyMember.Connection`. Fallback: `"Player {ClientId + 1}"`.
- Set `tmp.text` to the resolved name.
- Cache the local player's camera reference.

**`LateUpdate` — Billboard:**
- Get the local camera (`SessionController.Instance.LocalPlayer?.GetComponent<PlayerPresentation>().ViewCamera`). Cache on first find.
- If no camera, return.
- Rotate the label to face the camera: `transform.forward = camera.transform.forward`. This keeps the text always facing the viewer without mirroring artifacts (matching `forward` rather than `LookAt` avoids the text appearing reversed).
- Optional: fade `alpha` based on distance. Hide beyond ~30m, fade between 25–30m.

That's the entire script. No networking code, no HUD references, no screen-space math.

### Why This Is Simpler Than Screen-Space

| Concern | World-space TMP | Screen-space UI Toolkit |
|---|---|---|
| Distance scaling | Automatic (perspective) | Must calculate manually |
| Depth/occlusion | Natural (renders in 3D) | Always on top, no occlusion |
| Follows player | Parented under Graphics — free | Must project every frame per player |
| Billboard | One line in LateUpdate | N/A |
| Multiple players | Each has its own label — no management | Must track/create/destroy UI elements |
| Smoothing | Inherits NetworkTickSmoother | Must read smoothed position |

## How Names Resolve

| Launch context | `SessionController.Admit()` receives | Result |
|---|---|---|
| Steam multiplayer | `SteamFriends.GetPersonaName()` via `Hello.Name` | Steam display name (e.g. "xSpencer") |
| Local networking / Solo | `UsingLocal` is true → `$"Player {connection.ClientId + 1}"` | "Player 1", "Player 2", etc. |
| Steam but name blank | Fallback in `Admit()` | "Player" |

## Edge Cases

- **Solo mode:** Only one player, `IsOwner` is true, label GameObject disabled. Correct.
- **Late joiner:** Roster is broadcast before spawn messages (both reliable, in order). Name available by the time the label initializes.
- **Player disconnects:** FishNet destroys owned objects → label destroyed with player. No cleanup needed.
- **Name too long:** Already clamped to 32 chars in `Admit()`.
- **Camera not ready yet:** LateUpdate skips billboard rotation if camera is null; caches it once found.
- **Local player's own Graphics hidden:** `PlayerPresentation.OnStartClient` disables body renderers for the owner. The label disable for owner happens independently (we disable the whole NameLabel GameObject).

## Validation (User Must Verify)

1. Confirm the `NameLabel` child object appears under Graphics in the Player prefab with a TextMeshPro component.
3. Launch a multiplayer session with two clients.
4. Confirm the remote player shows a name label floating above their head.
5. Confirm the label always faces the viewer (billboards correctly).
6. Confirm the local player does NOT see their own name.
7. Confirm the label tracks smoothly as the remote player moves.
8. If launched through Steam, confirm it shows the Steam persona name.
9. If launched via local networking, confirm it shows "Player N".
