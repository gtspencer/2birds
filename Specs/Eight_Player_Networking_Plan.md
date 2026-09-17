# Eight-player networking and Steam lobby plan

## Agreed scope

Support up to eight total players, including the host, with a lobby and Steam joining. Steam is the primary platform. Keep a temporary development path for multiple local processes, designed for removal later. Preserve solo play, owner-predicted movement, client-reported impacts, and driver-simulated carts.

Keep the existing four-seat cart; the user will place another later. Only the owner sees their inventory; other players see equipped items. Disconnecting drops held items into the world, and rejoining creates a fresh player. Host departure ends the session for everyone.

Reduce recurring item traffic through compact velocity encoding and a per-item-definition rotation-sync toggle. Suitable rocks may use cosmetic rotation on remote observers while retaining actual host physics and thrower prediction. Preserve full-precision positions, 60 Hz simulation, and approximately 20 Hz motion updates.

The host starts manually, and guests may join during play. Steam discovery uses friends and invitations, with no public browser. Use App ID 480 temporarily in development. Target at least 60 FPS, with selectable 90/120 FPS rendering caps, and budget for 64 simultaneously thrown items across the session. The expected host GPU is approximately a GTX 1080 or RTX 2080. Use an AMD Ryzen 5 3600 at stock settings as the minimum host CPU target.

The capacity change is small. Steam integration requires a persistent Steam lifecycle, a Steam transport, and a lobby phase before loading the game. Keep one shared FishNet gameplay/lobby flow for both Steam and local development.

## Review findings

### Steam integration prerequisites

- Steamworks.NET is already installed through `Packages/manifest.json`, but no Steam networking transport or lobby implementation is present. `SessionController` directly accesses Tugboat, IPv4 addresses, and ports; it also loads `Game` immediately when the server starts. Those assumptions must change for a Steam lobby.
- **The HUD's glyph provider currently owns the global Steam lifecycle.** [HudController.cs](Assets/Game/Runtime/UI/HudController.cs#L56) creates one `SteamInputGlyphs` instance shared by `InteractionTooltip` and `ControlsHintPanel`, pumps it in `LateUpdate`, and disposes it on disable. [SteamInputGlyphs.cs](Assets/Game/Runtime/UI/SteamInputGlyphs.cs#L34) lazily initializes Steam and Steam Input when a controller glyph is requested and shuts both down in `Dispose`. Networking must not depend on controller use or HUD lifetime.
- **Local Steam processes cannot substitute for distinct peers.** FishySteamworks documents using the default transport for multiple builds/editors on one device. Retaining Tugboat for local development directly addresses that limitation. [FishySteamworks setup and local testing](https://github.com/FirstGearGames/FishySteamworks/blob/main/README.md).

### Required capacity changes

1. **The transport allows only two players.** [SessionController.cs](Assets/Game/Runtime/Networking/SessionController.cs#L70) sets the limit to two outside solo mode, and [SessionRoot.prefab](Assets/Game/Prefabs/SessionRoot.prefab#L65) stores the same default. With Tugboat, the host connects through a local client socket and consumes one transport slot. The session capacity must include the host regardless of the selected transport's counting rules.
2. **The scene provides only two spawn positions.** [Game.unity](Assets/Scenes/Game.unity#L427) assigns two transforms. [GamePlayerSpawner.cs](Assets/Game/Runtime/Networking/GamePlayerSpawner.cs#L41) disconnects a connection if all assigned positions are allocated. Raising the transport limit alone still leaves players three through eight unable to play.
3. **The session display says `/ 2`.** [SessionOverlay.cs](Assets/Game/Runtime/UI/SessionOverlay.cs#L41) duplicates the capacity assumption. The spawner's connection dictionary, slot allocation, player count, and byte-sized spawn slot otherwise accommodate eight players.

### Correctness issue: a rejected cart incident can remain pending

[GolfCartNetwork.cs](Assets/Game/Runtime/Vehicles/GolfCartNetwork.cs#L389) sets `incidentPending` before reporting a crash, rollover, or recovery incident. `AcceptIncident` rejects a mismatched cart revision without acknowledging the rejection or clearing that flag on the reporting driver.

Example: a passenger enters a seat on the server; before receiving that occupancy revision, the remote driver reports a crash. The server rejects the driver's previous revision. Further incident reports return early because `incidentPending` remains true. A later motion reset can clear it, but normal continued driving does not.

This race already exists with two players. More simultaneous passengers make it more relevant. Fix it before treating the cart as ready for a larger group.

### Unnecessary inventory traffic

[PlayerInventory.cs](Assets/Game/Runtime/Inventory/PlayerInventory.cs#L179) broadcasts the complete 24-slot inventory after every operation, including selection changes, and sends it to every new observer. Slot contents include arrays of world-item IDs.

Other players' equipped visuals already use `ItemRecord.Holder` and `ItemRecord.Equipped` through [WorldItem.AttachHolder](Assets/Game/Runtime/Items/WorldItem.cs#L195). If inventory inspection is not a feature, full inventory responses can be limited to the owner without adding another public equipment protocol.

### Joining is not gated on the item baseline

[PlayerInputReader.cs](Assets/Game/Runtime/Player/PlayerInputReader.cs#L45) enables input and calls `PlayerReady` when the local player starts. [WorldItemRegistry.SendBaseline](Assets/Game/Runtime/Items/WorldItemRegistry.cs#L122) separately sends the world records in reliable batches, with a start message but no completion message.

A guest can therefore enter the playable state while item initialization is still arriving. Larger inventories, more moving items, and several guests joining increase the relevance of that gap. The existing item revisions, motion buffering, and seat-state resolution are useful foundations to retain.

### Concurrency and lifecycle decisions

- **Independent seat requests can reject each other.** `GolfCartNetwork.Request` requires an exact shared cart revision. Two guests requesting different empty seats from the same revision can result in only the first succeeding. Rejections only clear the pending request; they do not explain the result to the player.
- **Disconnecting deletes held items.** [WorldItemRegistry.UnregisterPlayer](Assets/Game/Runtime/Items/WorldItemRegistry.cs#L220) removes every held record belonging to the departing player. Change this to world drops. Rejoining should continue to create a fresh player with an empty inventory.
- **Host departure ends the session.** [SessionController](Assets/Game/Runtime/Networking/SessionController.cs#L149) shuts down the server when its host leaves. Preserve this behavior and also close/leave the Steam lobby; do not interpret a change of Steam lobby owner as gameplay host migration.
- **There is one four-seat cart in the Game scene.** `GolfCartNetwork` has four-entry arrays and seat-index bounds, and `CartSeat` exposes indices 0–3. Four seats are a vehicle rule, not a session limit. The cart registry already supports multiple instances.
- **A full server produces a generic connection failure.** Tugboat rejects the connection before the game's authenticator can supply a reason. A precise “session full” message requires an admission/rejection protocol change; changing menu text alone cannot distinguish full capacity from connection failure.

### Scaling risks, not measured bottlenecks

- [SessionRoot.prefab](Assets/Game/Prefabs/SessionRoot.prefab#L83) runs at 60 ticks per second. [Player.prefab](Assets/Game/Prefabs/Player.prefab#L381) enables prediction and state forwarding. Each observing client maintains and replays remote player state. FishNet documents the extra CPU cost and the need for an alternative movement stream if forwarding is disabled: [prediction configuration](https://fish-networking.gitbook.io/docs/guides/features/prediction/configuring-networkobject).
- Movement fan-out grows with both player count and recipient count. For an equal-size state from each player to each external guest, the delivery count grows from `2 × 1` to `8 × 7`. This illustrates the scaling shape; it is not a measured bandwidth multiplier for the whole game.
- [WorldItemRegistry](Assets/Game/Runtime/Items/WorldItemRegistry.cs#L353) sends awake-item motion at approximately 20 Hz to all registered guests. Batches of eight limit individual message size, not total per-tick traffic. Sleeping items already stop generating motion updates.
- Every joining guest receives all item records. Several simultaneous joins can create a reliable-traffic burst alongside movement. Additional item counts matter as much as additional players.
- If more carts are added, separately driven carts simulate on different clients against remote kinematic copies. Consistent cart-to-cart collision outcomes need a design decision if those collisions are important.

## Implementation order

### 1. Establish Steam lifetime and transport selection

- Add one persistent Steam lifecycle owner to `SessionRoot`. Initialize the API before lobby requests, pump callbacks throughout menus and gameplay, and shut it down after network teardown on application exit. The normal callback pump belongs in the frame loop. [Steam API reference](https://partner.steamgames.com/doc/api/steam_api).
- Give the persistent Steam lifecycle owner responsibility for Steam Input initialization/shutdown too. Retain the HUD's shared `SteamInputGlyphs` instance for `InteractionTooltip` and `ControlsHintPanel`, but make it consume the persistent Steam state and own only its glyph textures. Remove callback pumping from the HUD's glyph path. HUD disposal releases the shared glyph textures without shutting down Steam or Steam Input; neither UI consumer owns the shared provider.
- Integrate FirstGearGames' FishySteamworks transport using the installed Steamworks.NET dependency. Select and pin a revision compatible with the vendored FishNet 4.7.3 APIs; preserve the game's custom cart motion extension. Use Steam peer-to-peer hosting and connect using the host's Steam ID. [FishySteamworks setup](https://github.com/FirstGearGames/FishySteamworks/blob/main/README.md).
- Account for the selected FishySteamworks revision's host counting and startup configuration. In revision `0320459`, the host uses a separate in-process client, the socket limit counts remote Steam connections, and server startup overwrites an earlier `SetMaximumClients()` call with the serialized `_maximumClients` value. Derive the Steam remote limit from the total capacity (seven guests for eight players), configure the value used at startup or adapt the setter to preserve it, and retain the independent FishNet admission limit including the host. [Transport startup](https://github.com/FirstGearGames/FishySteamworks/blob/0320459da446a95fc109aef66d42e1337e3b5fff/FishNet/Plugins/FishySteamworks/FishySteamworks.cs#L420), [connection counting](https://github.com/FirstGearGames/FishySteamworks/blob/0320459da446a95fc109aef66d42e1337e3b5fff/FishNet/Plugins/FishySteamworks/Core/ServerSocket.cs#L280).
- Use the bundled Multipass transport to select FishySteamworks for Steam sessions or the existing `GameTransport` component, which inherits Tugboat, for local development. Cache the transport references at startup and retain the selected child/index throughout each session attempt. Keep the configured child list stable. Multipass supplies transport routing. [Multipass.cs](Assets/FishNet/Runtime/Transporting/Transports/Multipass/Multipass.cs#L632).
- Set `GlobalServerActions = false`, call `Multipass.SetClientTransport(selectedIndex)` before connecting, and start only the selected server listener with `Multipass.StartConnection(true, selectedIndex)`. Continue using `ClientManager` for client start/stop. The unindexed server start/stop methods require global actions and otherwise operate on every child. [Multipass.StartConnection](Assets/FishNet/Runtime/Transporting/Transports/Multipass/Multipass.cs#L826).
- For host/solo shutdown, preserve explicit disconnect notification: send and flush messages through `ServerManager.SendDisconnectMessages(..., iterate: true)`, stop the local client through `ClientManager`, then call `Multipass.StopConnection(true, selectedIndex)`. Track the selected client's state through connection-state callbacks, including its in-process host client, and wait for that state and `Multipass.GetConnectionState(true, selectedIndex)` to be `Stopped` before accepting another attempt. Filter callbacks to the selected transport. FishySteamworks revision `0320459` queries only its remote-client socket in `GetConnectionState(false)`, so even an indexed Multipass query cannot establish host-client shutdown. Multipass's unindexed server query is also unsupported. [Disconnect messages](Assets/FishNet/Runtime/Managing/Server/ServerManager.cs#L339), [indexed server state query](Assets/FishNet/Runtime/Transporting/Transports/Multipass/Multipass.cs#L264), [Steam client state query](https://github.com/FirstGearGames/FishySteamworks/blob/0320459da446a95fc109aef66d42e1337e3b5fff/FishNet/Plugins/FishySteamworks/FishySteamworks.cs#L197).
- Add one explicit development startup switch for local networking. In that mode, skip Steam API, lobby, identity, and overlay startup entirely. The shared glyph provider must use text fallbacks for both tooltips and controller hints without calling Steam. Connect local instances through `127.0.0.1` and a selected port, using connection-scoped display names. Keep this selection in session/bootstrap code so it can be removed without editing players, inventory, items, or vehicles.
- Replace `SessionController`'s unconditional Tugboat calls with the selected session connection path. Keep IPv4/port and local socket timeout setup on the `GameTransport` child inside the temporary local path; Steam players should use lobby/invite UI. `Transport` declares `SetServerBindAddress`, `SetPort`, and `SetTimeout`. Multipass forwards the address/port setters, but inherits the no-op `SetTimeout`, so configure local timeouts directly on `GameTransport`. [Transport setters](Assets/FishNet/Runtime/Transporting/Transport.cs#L168), [Multipass setters](Assets/FishNet/Runtime/Transporting/Transports/Multipass/Multipass.cs#L769).

**User Unity work:** add/configure the Steam lifetime, lobby, FishySteamworks, and Multipass components on the existing `SessionRoot` prefab, and assign the transport references. No additional Game-scene component is needed for the lobby. Unity must generate imported asset metadata.

### 2. Add a shared pre-game lobby and Steam joining

| Path | Discovery and connection | Shared behavior |
| --- | --- | --- |
| Steam | Steam lobby/invitation, then FishySteamworks connection to the recorded host Steam ID | FishNet admission, lobby roster, start, scene loading, gameplay |
| Local development | Direct loopback/IP connection through Tugboat | The same FishNet lobby and gameplay flow |

- Add a pre-game `InLobby` phase. Server startup should establish the host connection and lobby, then wait for the chosen start rule before `LoadGlobalScenes("Game")`. Solo may continue directly into the game.
- Replace `ClientState()`'s immediate transition from socket connection to `LoadingGame`. Successful admission to a pre-game session enters `InLobby`, with no connection or gameplay-loading deadline while waiting for the host; normal network liveness checks remain active. Starting the game moves the host and each guest into `LoadingGame` with a fresh `LoadTimeout` before their scene load begins. Solo and guests joining an already-starting or active game also receive a fresh loading deadline. Enter `InGame` only through the readiness gate in step 6.
- Maintain the admitted-player roster through reliable FishNet broadcasts on the persistent session object. Send changes only when membership/readiness changes. This gives local instances the same lobby behavior without reproducing Steam services or creating a lobby scene.
- Extend the existing UI Toolkit menu with eight roster slots, host indication, host-only start, leave, friends' lobbies, and Steam invitations. Start is available once the host is admitted; neither eight players nor a ready vote is required.
- Create a Steam lobby with an eight-member limit. Publish a game/protocol identifier, host Steam ID, and session phase after the host is able to accept connections. Use the same capacity for FishNet admission; lobby membership alone does not mean scene loading has finished. Steam supplies lobby creation, metadata, and membership callbacks. [Steam matchmaking](https://partner.steamgames.com/doc/features/multiplayer/matchmaking).
- Handle both invitations received while running (`GameLobbyJoinRequested_t`) and launch invitations (`+connect_lobby`). After entering, obtain the host metadata, check the game/protocol, and establish the FishNet connection. [Steam invitation APIs](https://partner.steamgames.com/doc/api/ISteamMatchmaking#InviteUserToLobby).
- Use a friends-only Steam lobby and retain membership during gameplay. Keep joinability and session phase aligned with loading, capacity, and shutdown. Preserve the original game host's identity throughout the session. Route late arrivals through the existing FishNet global-scene load/acknowledgement path, including arrivals while the host is starting the game.
- Configure App ID 480 for development and require this game's identifier/protocol in lobby metadata before connecting, including direct invitations and friends' lobby results. Keep the App ID in one platform configuration location for replacement by the game's own ID before release.
- Carry the current attempt/session guard through asynchronous Steam callbacks. Cancellation or a newer invitation must invalidate old results; a late successful lobby join after cancellation must be left. On failure, clear partial lobby/network state and show the actual failure stage.
- A host leaving or losing its game connection ends the session for all peers, including while in the lobby. Guests leave the Steam lobby and return to the menu. Finish shutting down the selected transport before accepting another session attempt.

### 3. Raise capacity and supply spawn positions

- Define one shared multiplayer capacity of eight in the session code. Use it for Steam lobby membership, FishNet admission, spawn allocation bounds, and the session display; derive transport limits using each transport's host-counting rules from step 1. Keep solo's total admitted capacity at one.
- Update the existing `SessionRoot` GameTransport default to eight and configure FishySteamworks' effective startup limit for seven remote guests when its host client is counted separately. Preserve the host-first authentication rule and scene-load acknowledgement before spawning. Keep a shared admitted-connection limit even if multiple transports are enabled in a future development setup.
- Keep the existing slot registry and reuse released slots. Bound allocation to the session capacity even if the scene later contains extra markers.
- Detect insufficient configured spawn markers when starting a session and explain the configuration problem locally, rather than admitting guests who cannot spawn.
- Cache the session-info label when binding the overlay while changing its capacity display.
- Update the existing [Assets/Game/README.md](Assets/Game/README.md#L3) description of a host and one guest to describe the eight-player session and joining flow.

**User Unity work:** add six clear spawn markers in `Game` and assign all eight to the existing spawner. Place them far enough apart for the two-metre-tall, one-metre-wide player capsules, with clear ground and room for respawns. This uses the existing component and requires no new prefab or asset. Authored markers are the simplest continuation of the current spawning design.

### 4. Fix cart concurrency

- Send incident accepted/rejected outcomes back to the reporting simulator, using a targeted response for remote reports and local handling for host reports, correlated with their motion epoch and report sequence. Rejection must release the matching pending state on that simulator and allow it to reassess the incident against current occupancy.
- Preserve protection against old authority epochs and duplicate incidents. Do not apply an old report's seat velocities blindly to a changed occupant set.
- Keep one simulator per cart and the existing stop/commit/start ownership handoff.
- For ordinary passenger changes, evaluate the current destination occupancy and the requesting player's revision. An unrelated passenger change should not invalidate a request for another still-empty seat. Retain the guards needed during driver handoffs and recovery.
- Resolve competing claims to the same seat by server processing order. Return an occupied/busy result to the unsuccessful requester so the interaction is understandable.

### 5. Reduce inventory traffic

- Send full inventory snapshots and operation responses only to the owner, including the initial snapshot and driver-seat permission changes. Replace normal `ObserversInventory` responses with a targeted response to the guest owner for every processed operation, including selection changes and rejections.
- Preserve revision, acknowledged operation, acceptance result, rejected-operation ID, prediction replay, and pickup/release rollback. Retain the host owner's immediate local response and the server guard in the targeted receiver to prevent duplicate host processing. Extend `TargetInventory` to carry acceptance/rejection details; its current signature only supplies an accepted snapshot.
- Do not use `ExcludeOwner` on the existing observer RPC as an intermediate optimization: the direct `AcceptInventory` call only handles the host owner. Guests currently depend on `ObserversInventory` to acknowledge normal operations, clear pending requests, and roll back rejected predictions. Replace that response path atomically. [PlayerInventory.ProcessRequest](Assets/Game/Runtime/Inventory/PlayerInventory.cs#L181), [inventory receivers](Assets/Game/Runtime/Inventory/PlayerInventory.cs#L246).
- Continue broadcasting world-item ownership/equipment lifecycle and charging state for remote presentation.
- Start with complete owner-only snapshots. Introduce changed-slot messages only if the later traffic measurements justify them.

### 6. Make late joining and departures explicit

- Add a reliable item-baseline completion message scoped to the current world epoch. Send it even for an empty world, after the baseline records on the same ordered channel.
- Signal world readiness locally for host and solo after `BeginWorld()` finishes initializing all world records, including an empty world. `JoinWorld()` returns immediately on the host, so these paths must not wait for a baseline-completion message. Guests signal world readiness when they receive the matching completion message.
- Track local-player initialization and world readiness separately, allowing either to finish first. Enable gameplay and enter `InGame` only after both are ready for the current session. Reset both during leave/rejoin, reject old-session completion messages, and retain the fresh loading deadline established in step 2.
- Preserve revision-based item updates and pending seat resolution so arrivals in different object-spawn orders converge correctly.
- Release held items before player cleanup using the departing player's current pose and the existing item lifecycle messages. Place stack contents without mutual overlap, including a player who disconnects while seated. Use the host's last known pose when the departed client cannot report a final one.
- Clear each released record's holder/equipped state, add it to active world simulation, and prevent subsequent unregister cleanup from deleting it. Apply drops once for an individual guest departure while the world continues, not during whole-session teardown.
- Rejoining allocates a free spawn slot and starts with an empty inventory. Steam identity is for joining/display, not reconnect persistence.

### 7. Compact item motion while preserving gameplay

- Complete the transport correctness fixes in the [final review](Eight_Player_Networking_Final_Review.md#recommended-fixes) before changing the item message format.
- Add a serialized rotation-sync toggle to the existing ItemDefinition, defaulting to enabled. Disable it for suitable rocks only when their collision shape and gameplay hit volumes do not meaningfully depend on orientation. Keep synchronization for directional tools and strongly asymmetric objects. This uses the existing definition assets and visual root; no new prefab or component is planned.
- For definitions with rotation sync disabled, omit both rotation and angular velocity from regular unreliable motion snapshots. Encode their absence explicitly so mixed batches and motion arriving before lifecycle records remain decodable. Sending zero values in the existing fields is not the optimization.
- Preserve actual host physics and the thrower's locally predicted rotation. Animate cosmetic tumble on remote observers using the existing visual root, initial spin, and motion; do not rotate gameplay hit volumes with the cosmetic animation. Exact airborne orientation may differ between clients.
- Retain initial and settled orientation in reliable lifecycle/baseline state. Smooth the visual mesh into the settled orientation so resting items agree without a visible snap. Initialize late joiners from the baseline, stop cosmetic spin when sleeping or held, and retain revision ordering so stale motion cannot restart it.
- Adapt corrections for omitted rotation fields explicitly. Preserve the predicted body's local rotation and angular velocity when those fields are absent; never apply default zero/identity values as authoritative corrections. Keep full launch and reliable lifecycle data.
- Compact linear velocity for all items and angular velocity only for items whose rotation remains synchronized. Use 16-bit fixed-point components with proposed steps of 0.01 m/s and 0.01 rad/s, respectively, and a full-precision fallback for values outside the representable range. Keep simulation values unchanged and do not clamp physical speeds to fit the encoding.
- Scope the compact layout to ItemMotionBatch rather than changing ItemMotion's global serializer, which also serves release requests and reliable lifecycle records. Keep positions at full precision and retain FishNet's existing quaternion compression for rotation-synchronized items.
- Keep each snapshot independently decodable despite packet loss, and increment the session protocol identifier for the incompatible wire-format change. Establish expected serialized sizes, including format flags, before implementation. A full hardware benchmark is not a prerequisite for this focused encoding change.

### 8. Preserve timing and address further measured limits

- Keep the current 60 Hz movement simulation, owner prediction, client-reported impacts, and 20 Hz item/cart motion as the initial target behavior.
- Add a persisted rendering-cap choice of 60, 90, or 120 FPS to the menu settings. Define its interaction with v-sync so the selected cap is effective. Keep the network tick rate and physics step at 60 Hz for all choices; raising a rendering cap must not increase network traffic or change vehicle/player behavior.
- Use the existing prediction diagnostics for a later, user-requested performance pass. `GameTransport` counters cover Tugboat only; include the selected Steam transport in payload accounting before comparing the two paths. Payload counters exclude underlying packet overhead and retransmissions.
- Measure host upload, guest download, frame time, replay time, and join bursts at 2, 4, and 8 players, with representative item activity.
- After the planned item encoding changes, consider position quantization or adaptive motion rates only if further bandwidth reduction warrants their precision and presentation tradeoffs. If replay CPU dominates on clients, consider cheaper remote-player simulation only with a concrete plan for player collisions, impacts, animation, and replacement movement replication.
- Consider distance-based replication only if the map and measured load need it. The custom world-item broadcasts have their own observer set, so changing FishNet observer conditions alone would not reduce those messages.
- Preserve the custom FishNet cart motion extension when touching networking dependencies.

## Performance assessment and acceptance budget

Eight players plus 64 active thrown items is a reasonable target for this architecture. A source review cannot establish a minimum frame rate on a GTX 1080/RTX 2080 system: physics, prediction replay, CPU model, scene rendering, and network conditions determine the result. Treat 60 FPS as the required target; 90 and 120 are selectable caps for machines that can sustain them.

The minimum CPU target is an **AMD Ryzen 5 3600 at stock settings**, an older six-core, twelve-thread processor with a 3.6 GHz base clock. It provides a concrete lower-cost gaming-PC baseline for the physics and prediction budget. [AMD specifications](https://www.amd.com/en/support/downloads/drivers.html/processors/ryzen/ryzen-3000-series/amd-ryzen-5-3600.html). Evaluate that target with one host game process on the target PC and seven guests on other machines; running eight local processes is a separate development workload.

| Area | Proposed budget/scenario |
| --- | --- |
| Host CPU | AMD Ryzen 5 3600 at stock settings; target 60 FPS while hosting and playing |
| Players | One host and seven guests |
| Active items | 64 total across the session; exercise both distributed throws and a dense collision cluster |
| Simulation | 60 Hz, unchanged at every rendering cap |
| Rendering | 60 FPS minimum target; 90/120 FPS options, corresponding to approximately 16.7/11.1/8.3 ms per frame |
| Network latency | 100–150 ms round-trip design envelope; 200 ms stress case |
| Network disturbance | Separate stress scenarios with 20–30 ms jitter and 1–3% packet loss |
| Join pressure | Guests joining while the existing group drives, throws, and picks up items |

The latency figures are proposed engineering scenarios, not measurements or a guaranteed upper bound for Atlanta-to-Los Angeles connections. Run future Steam checks across real connections as well as controlled network conditions. Establish the sustained host-upload requirement and demonstrate the 60 FPS target on the selected Ryzen 5 3600 baseline before publishing minimum requirements.

### Item traffic estimate

`ItemMotion` contains three 32-bit integers, three `Vector3` values, and one `Quaternion`: 64 nominal field bytes per sample. At 20 samples per second with 64 awake items:

- Per guest: `64 × 64 × 20 = 81,920 bytes/second`, approximately **0.66 Mbps**.
- Host to seven guests: `81,920 × 7 = 573,440 bytes/second`, approximately **4.6 Mbps**.

This is field-size arithmetic, not a wire measurement. Serializer packing can change it; batch headers, player movement, reliable lifecycle events, cart motion, Steam/UDP overhead, and retransmissions are additional traffic. The present eight-item batches mean eight motion batches per snapshot for 64 active items. Item count is therefore not a hard capacity blocker, but host upload deserves an explicit budget after total traffic is measured.

FishNet already compresses the default quaternion to four bytes and packs integer fields, so the nominal 64-byte calculation overstates the serialized sample size. The planned reductions below are relative to the existing field encodings, before format flags, fallback samples, and transport overhead:

| Item motion encoding | Bytes saved per item update | Host payload upload saved at 64 active items, 20 Hz, seven guests |
| --- | --- | --- |
| Omit rotation and angular velocity for cosmetic-rotation items | 16 | Approximately 1.15 Mbps |
| Omit those fields and compact linear velocity | 22 | Approximately 1.58 Mbps |
| Retain rotation sync and compact linear/angular velocity | 12 | Approximately 0.86 Mbps |

These alternatives are not additive. Actual savings depend on the mix of eligible items. Once both velocity vectors are compacted, omitting rotation and angular velocity saves another 10 bytes per eligible sample, approximately 0.72 Mbps in the all-eligible target workload.

CPU risks include 60 Hz physics and dense collisions on the host, plus prediction replay on remote clients. Remote clients generally interpolate item motion, while the host simulates world items and throwers predict their own releases. Preserve that division while implementing the planned encoding changes. Reduce motion frequency or change remote-player simulation only after identifying a cost that warrants those behavioral tradeoffs.

## Adding the second cart later

For basic spawning, seating, ownership, and motion, drag another existing `GolfCart` prefab into `Game` and save the scene in Unity. Each instance registers itself by its own network object ID; there is no singleton cart reference to replace or player-count field to change. FishNet generates scene-object IDs in its editor serialization path. Ensure the saved scene includes the new instance for every client build.

The current cart is a scene object, and the spawnable-prefab collection contains only the player. A second scene-placed cart follows the existing pattern. Registering the cart as a runtime-spawnable prefab is needed only if carts are later instantiated dynamically.

The networking caveat is cart-to-cart collision consistency: two different drivers simulate their own dynamic carts against remote kinematic copies. Merely placing another cart does not add a shared collision resolution rule. Keep this as a separate follow-up if mutually consistent crashes, pushing, or momentum transfer between carts are required; normal per-cart driving and seating use the existing architecture.

## Remaining follow-up decisions

- Before adding inter-cart gameplay, decide how consistent pushing/crash outcomes must be between separately driven carts. Adding the second scene instance itself does not require an eight-seat redesign.

## User visual acceptance checks after implementation

- In Steam mode, create/join the lobby, invite a friend while their game is running and while it is closed, and confirm roster/start/leave behavior. Check cancellation, a full lobby, an incompatible build, and host departure before game start.
- Wait in the lobby longer than `LoadTimeout`, then start. Confirm the host and guests stay connected while waiting and receive a full loading window after Start. Repeat leaving and hosting again to check that Steam host-client shutdown completes before the next attempt.
- Run one local host and seven local guests without Steam initialization. Confirm they use the same lobby, start, and gameplay flow. Use distinct Steam accounts/devices for the separate Steam joining checks.
- Switch between menu, lobby, and gameplay using both keyboard and controller. Steam joining must work before requesting any controller glyph and must survive HUD disposal/recreation. Check both interaction tooltips and cart driver hints after HUD recreation and driver handoffs, including text fallbacks in local development mode.
- Join with one host and seven guests. Confirm eight distinct players, separate spawn positions, one local camera per client, and an accurate `/ 8` display. Attempt one additional join; existing players must remain unaffected. Check solo separately and confirm both solo and the host finish loading without waiting for a guest baseline message.
- Have all players move, jump, collide, throw 64 items collectively, and take impacts together. Compare responsiveness and visible positions across host and guests under the agreed latency scenarios. Check both scattered throws and dense contacts.
- Compare cosmetic-rotation rocks with rotation-synchronized items during throws, bounces, slow rolls, and settling. Exact airborne orientation may differ; check for sliding, clipping, rotation snaps, changed hit behavior, or altered thrower prediction. Late joiners should see consistent resting orientations. Repeat with packet loss and a mix of item types.
- Compare compact-velocity throws from moving carts and high-speed impacts, including full-precision fallback values. Confirm smooth trajectories and unchanged impact response, then compare payload traffic under the same active-item workload.
- On the Ryzen 5 3600 host baseline, watch the frame-rate display and camera motion during the eight-player, 64-item scenario, including dense collisions and a late join. Confirm the 60 FPS target and look for visible stutters or repeated position corrections.
- Select 60, 90, and 120 FPS caps. Confirm the cap persists, camera/cart motion stays smooth, and movement speed, jump height, throw behavior, and network simulation frequency do not change with the rendering cap.
- Join an active session containing held, thrown, sleeping, and removed items and an occupied moving cart. Confirm the playable world is ready, inventory contents agree, and seats/equipped items resolve correctly.
- Join with the cart headlights already on and confirm the arriving guest sees the current state. Toggle lights and use both the driver horn action and physical steering-wheel interaction before and after a driver handoff; compare lights and horn playback across clients.
- Race two players for one item and one seat; only one should obtain each. Check that the losing guest's inventory corrects and subsequent pickup/drop/selection operations still complete. Request different empty seats simultaneously and confirm both can succeed.
- Change passenger occupancy while a remote driver crashes or rolls over. Confirm ejections/recovery still work on later incidents. Fill all four seats and exercise driver handoffs and driver disconnects.
- Disconnect while holding items, including from a cart seat, then rejoin. Confirm the items remain as world drops, the returning player has an empty inventory, the player count is correct, and no ghost occupant remains. Leave the host and confirm everyone returns to the menu and leaves the lobby.
- When the user adds the second cart, drive both simultaneously, collide them, and compare outcomes on each driver's client and an observer.

These are future acceptance checks for the user; implementing or running automated validation requires a separate request.
