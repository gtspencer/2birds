# Pickup and holdable rocks: implementation plan

Status: ready for implementation. This document plans the work; it does not report an implemented or tested feature.

Source: `Specs/Initial/PickUpAndHoldables.md`. Code and assets reviewed on 2026-09-13, including uncommitted inventory work in the workspace. Recheck the named integration points before editing; preserve unrelated changes. Do not consult `Specs/Done` to infer behavior. Leave this plan in `Specs/Generated` until implementation and validation are complete, then move it to `Specs/Done` as required by `AGENTS.md`.

## 1. Outcome and scope decisions

Players can target a grounded rock, see a device-appropriate pickup prompt, put it into their existing inventory, equip it through the hotbar, charge and throw it, or drop it. Every client agrees on its lifecycle. Resting and held rocks generate no continuous item transform replication; only active thrown/dropped rocks participate in movement prediction and reconciliation.

Resolve the source's unspecified or conflicting details as follows:

- Rocks are unique items with `MaximumStack = 1`. One pickup occupies one of the existing 24 slots. Preserve support for other stackable inventory definitions; rock stacking is outside this change.
- Pickup adds to inventory without automatically equipping. Explicitly selecting a hotbar slot equips its current rock. There is one equipped item per player, still contained in inventory. Selecting an empty/non-holdable slot unequips. Items in backpack slots 8–23 must first be moved into slots 0–7 using the existing inventory UI.
- Pickup uses E / gamepad north button. Use uses left mouse button / right trigger: press starts charging, release throws, and a tap throws at minimum power. Interpret “hold interact to charge” in the source as holding **use**, keeping pickup distinct. State this decision in the controls documentation.
- Drop uses Q / gamepad west button and drops the equipped item without charging. Dragging a supported item out of the inventory uses the same world-transfer implementation, including for unequipped rocks.
- Keyboard 1–8 and the existing gamepad previous/next actions select hotbar slots; add a controller inventory toggle on Select/Back. Existing keyboard inventory controls remain.
- “Persist across all clients” means authoritative state for the lifetime of the session, including late joins. No disk saves, host migration, crafting, damage, avatar work, IK, or other item-use implementations.
- Only settled rocks are pickup targets. Active rocks cannot be caught or picked up in flight. Resting rocks cannot be passively kicked awake. Rock collision initially includes static ground/world geometry, excludes players and other rocks, and causes no gameplay damage. This avoids new physical interactions with predicted players; include walls and slopes in validation.
- On guest disconnect, its inventory rocks return to safe grounded positions near its last server position; in-flight rocks continue. Session shutdown discards the session world. Out-of-bounds rocks recover at their last safe grounded position, or their launcher's last safe position if none exists.

## 2. Existing implementation to extend

| Existing code/asset | Relevant behavior and required integration |
| --- | --- |
| `Assets/Game/Runtime/Inventory/InventoryModel.cs` | 24 slots, eight hotbar slots, globally live inventory `EntryId`s, revision checks, staging, and `IInventoryDropSink`. `Add` can accept a unique entry ID. A drop currently calls a synchronous sink before committing removal. Extend its transaction boundary; do not build a second inventory. |
| `Assets/Game/Runtime/Inventory/PlayerInventory.cs` | Owner-only `SyncVar<InventorySnapshot>`, request IDs, pending UI state, duplicate suppression, result/snapshot responses, `ServerAdd`, and teardown. Server code needs access to authoritative contents; `Snapshot` is an owner-facing confirmed view and must not be used for dedicated-server validation. |
| `Assets/Game/Runtime/Inventory/ItemDefinition.cs`, `ItemCatalog.cs` | Definitions currently contain only ID/name/icon/stack limit. `Assets/Game/Settings/ItemCatalog.asset` is empty. Add the basalt definition and optional holdable configuration without invalidating non-holdable definitions. |
| `Assets/Game/Runtime/Player/PlayerNetworkState.cs` | Public state currently contains `SpawnSlot` and `Revision`. Extend it with a small equipped descriptor; never expose the private inventory snapshot. |
| `Assets/Game/Runtime/UI/InventoryHudPresenter.cs` | `SelectedIndex` is currently UI-local; private `Select` only changes highlighting. Move selection/equip intent into a player-owned API and render its state. Preserve hotbar highlighting and inventory drag behavior. |
| `Assets/Game/Runtime/Player/PlayerInputReader.cs` | Owner-only dynamic input sampling, tick-buffered movement/jump, gameplay gating and focus clearing. Add interaction edges and cancellation without disturbing `Consume()` movement behavior. |
| `Assets/InputSystem_Actions.inputactions` | `Interact` currently has a Hold interaction; change it to press pickup. `Attack` currently binds gamepad west; change that binding to right trigger before assigning west to Drop. `Previous`/`Next` already use dpad left/right. UI inventory currently binds Tab/I only. |
| `Assets/Game/Runtime/Player/PlayerMotor.cs` | FishNet `TickNetworkBehaviour`, `PredictionRigidbody`, replicate/reconcile, 60 Hz physics, server authority. Follow this pattern for active rocks. Inventory/world mutations must never execute from replayable physics methods. |
| `Assets/Game/Runtime/Player/PlayerPresentation.cs` | Camera follows smoothed graphics; owner capsule renderers are cached in `Awake` and hidden. Add a camera hold anchor and a graphics hold anchor; do not include held renderers in the body-hide list. |
| `Assets/Game/Runtime/Networking/GamePlayerSpawner.cs` | Scene observation/start-scene acknowledgement and connection cleanup. Use its readiness pattern for world synchronization and arrange inventory recovery before player disposal. |
| `Assets/Game/Runtime/Networking/SessionController.cs` | `GameplayAllowed` gates player input for inventory, session panel, focus, and connection lifecycle. Use this boundary for interaction cancellation. |
| `Assets/Game/Editor/MvpAssets.cs`, `InventoryAssets.cs`, `MvpChecks.cs` | Generators/installers and validation. `MvpAssets.Generate` replaces scenes/prefabs and clears spawnables; avoid running it to install this feature. Update its generation path to retain the feature if intentionally regenerated later. `MvpChecks` currently requires exactly one spawnable and two inventory-toggle bindings indirectly through `InventoryAssets.Validate`; update these obsolete assertions specifically. |
| `Assets/Game/Runtime/Diagnostics/InventoryValidation.cs`, `Assets/Game/Tests/EditMode/InventoryModelTests.cs` | Existing inventory coverage includes unavailable-drop behavior and non-holdable fixture definitions. Keep those cases valid for unsupported items and add supported rock cases. |
| `Specs/Validation/run-pair.ps1`, `udp_profile.py` | Existing two-process LAN/normal/stress harness. Extend this tooling for item scenarios, measurements, and late join/rejoin. The game admits two players; use the guest slot for late-join tests. |

The actual source mesh is `Assets/Art/Rock/Rock_Basalt.fbx`, not a folder named `Rock_Basalt`. Its texture is `Assets/Art/Rock/Textures/Basalt_BaseColor.png`. Build a URP-compatible material and reusable mesh presentation from these assets; inspect mesh bounds in Unity to choose scale, pivot, and collider size.

## 3. Architecture and invariants

### 3.1 Stable identity and representations

Add a server-owned world registry on a single scene `NetworkObject` in `Game.unity`. Scene-authored rocks are ordinary objects with a renderer, simple collider, and serialized stable `WorldItemId`; they are not individual `NetworkObject`s and need no Rigidbody while dormant.

Authoring tools allocate and serialize nonzero `ulong` world IDs. Check duplicates across all markers in the scene, including copied instances. Do not derive IDs from `GetInstanceID`, hierarchy enumeration, object names, or runtime randomness. Reserve a separate range for runtime-created world records. A deterministic manifest hash of authored IDs, definitions, and starting poses detects mismatched world content at session initialization.

Keep `WorldItemId` separate from inventory `EntryId`. The registry's private record associates a world ID with definition, current lifecycle revision, inventory entry ID if assigned, owner if contained, and last safe pose. Extend the add result/server API to return the created inventory entry ID. On first pickup allocate an ordinary inventory entry ID; on later pickups reuse its recorded ID through the existing unique-ID path after inventory removal has released it. Never feed arbitrary world IDs into the inventory allocator. Support server-granted rocks by allocating a world record when first transferred into the world.

Each rock has exactly one logical location. Equipped is an annotation on an inventory item, not a second item. Renderers, flight proxies, and pooled objects never own item identity or quantity.

| State | Server representation | Client representation | Network traffic |
| --- | --- | --- | --- |
| Dormant | Registry identity and static pose | Authored/static rock visual and collider | None after initialization/last transition |
| Inventory | Owner inventory plus private world-to-entry mapping | Ground visual hidden; only owner sees inventory | Reliable transition and owner inventory change |
| Equipped | Same inventory entry plus public equipped descriptor | One mesh attached to the appropriate player anchor | Descriptor on change; no held transform stream |
| Active | Registry plus pooled predicted flight `NetworkObject` | Predicted/reconciled rock graphics | Reliable lifecycle changes; unreliable active movement states |
| Dormant again | Registry stores final authoritative pose | Static visual restored at final pose | One reliable settle transition; no continuing movement packets |

Required invariants:

1. Every world ID is in one logical state; one entry cannot be owned by two inventories or simultaneously exist in inventory and flight.
2. Only the server commits pickup, equip, release, recovery, or settlement. Client requests carry intent, never authoritative inventory contents, speed, or position.
3. Every lifecycle change increments an item revision; each activation also has a unique generation. Session epoch + world ID + generation identify a flight, independent of recycled FishNet object IDs.
4. An equipment descriptor always resolves to an entry the server player owns. Inventory mutation reevaluates equipment in the same transaction.
5. State application is idempotent on host and clients. Host callbacks must not produce duplicate visuals or physics bodies.
6. No inventory mutations, network spawns/despawns, or permanent registry writes occur during FishNet prediction replay.

### 3.2 World replication and late joins

Use a reliable observer-readable `SyncDictionary<ulong, WorldItemPublicState>` on the registry for **changed** world records. Untouched authored rocks come from the local manifest. Store the latest record per touched item, not an append-only event history. Public records contain revision, definition, lifecycle (`Hidden`, `Active`, `Dormant`), activation generation, and a pose only where required. Do not include inventory slots or quantities; ownership remains private except for the public equipped descriptor.

FishNet's initial collection serialization supplies the current sparse snapshot to new observers; active flight objects and player descriptors supply current flight/equipment state. Use a registry initialization/ready acknowledgement after its initial state has been applied and the manifest checked. Reject mismatched content with a clear session error instead of allowing incompatible world IDs. Keep local authored visuals hidden and pickup input disabled until ready, so collected rocks do not flash or become interactable during loading. Subscribe to collection changes and explicitly apply current contents on client start; do not depend solely on change callbacks firing for initial state.

Do not assume ordering between collection changes, spawn/despawn messages, private inventory snapshots, and public equipment updates. A presentation registry keyed by world ID arbitrates visible representation using lifecycle revision and generation. An active object's newer spawn descriptor may hide the dormant mesh before its dictionary delta arrives. A reliable newer settle/hidden record overrides old flight data, even if the despawn arrives later. Suppress a stale equipped mesh when a newer lifecycle record says its item has been released. Re-pickup/rethrow must invalidate all prior activation data. Retain the reliable final pose after despawn so packet loss and late joins cannot resurrect an old flight.

Verify the installed FishNet version's full collection serialization and reliable fragmentation with a large changed-world snapshot; do not send the dictionary repeatedly as a full observer RPC. Keep the registry attached to the Game scene and subject to the existing scene observation rules. Clear caches, subscriptions, mappings, and epoch-scoped visual state on session teardown.

### 3.3 Atomic requests and inventory integration

Extend the existing owner request channel to cover move/drop, pickup, equip, begin-use, cancel-use, and release-use. Use one monotonic request sequence, duplicate-result handling, authoritative snapshots in responses, and a shared pending mutation gate. A charge token can outlive completion of begin-use; it must not keep the UI request gate locked for the entire charge.

Requests include expected inventory revision, relevant entry/slot, expected item revision for pickup, and a request/charge token. Return explicit full-inventory, out-of-range, obstructed, stale-target, unsupported-use, busy/capacity, and invalid statuses. Retry refreshes/resends the **same** request ID; never issue a new pickup or throw merely because an acknowledgement is delayed. Duplicate release returns the original activation/result. Bound result caching and request rate per connection. Charge cancellation must still clear local feedback immediately and reach the server if begin-use is pending; queue it behind the same request or mark that charge token canceled.

Provide a server-only authoritative snapshot/read API and a committed-change notification on `PlayerInventory`. Refactor the world drop handoff to a prepared transfer contract (`Prepare`, non-failing domain `Commit`, `Abort`, followed by publication), updating its model tests and callers. Preflight allocations, launch clearance, supported definition, identity, and active-capacity availability before removing the entry. On prepare failure, both domains remain unchanged. Commit registry and staged inventory together on the main thread before emitting snapshots/events. Reserve flight objects before commit; if network publication is temporarily unavailable, retain the committed pending world record and retry or recover it to dormant state, never lose the item or grant it twice. Keep the current unsupported-item failure behavior.

Pickup transaction: validate dormant revision and target -> reserve the world item -> stage one inventory addition -> commit the item as contained and inventory addition together -> publish owner result/snapshot and reliable hidden record. A full inventory or losing a simultaneous pickup race leaves the rock unchanged. Releasing the reservation must be exception-safe. Do not hide the authoritative ground item until the transaction succeeds; the owner may show pending feedback.

Drop/release transaction: validate entry and equipment if required -> prepare world record/flight -> commit removal, clear equipment/charge, and activate -> publish. A rock dropped from the inventory UI may be unequipped. All existing moves/swaps/merges still work: equipment follows its entry when moved within the hotbar; moving it into the backpack, dropping it, or removing it unequips. Do not automatically equip a replacement item moved into the vacated slot. Explicit selection is the equip action.

Server validation checks RPC sender/ownership, active authenticated session, revision and identity, finite normalized aim, pickup range (start at 3 m), and line of sight from the server player's eye position. The owner supplies aim direction, not a trusted camera origin; derive origin from the server body plus the shared eye offset. Raycast against both world occluders and pickup layers, ignoring triggers and the player's own collider. Choose the first unobstructed rock. Allow a small configured distance tolerance (initially 0.25 m) for player prediction; never accept arbitrary remote pickup positions. Do not require a dedicated-server camera.

### 3.4 Equipment and input

Add `PlayerHoldables` as the player-facing selection, targeting, and use component. Route 1–8, UI slot clicks, and gamepad dpad cycling through its selection API. Selection is local UI state; confirmed equipment comes from the server. Initially equipment is empty even though slot zero is highlighted. Equip requests name the current entry and inventory revision. If an equip is pending, show it as pending and prevent use of an unconfirmed entry.

Extend `PublicPlayerState` with equipped world ID, definition ID (or catalog code), item revision, and equipment revision. Server setters preserve `SpawnSlot` and other state. Expose change notifications for presentation. Clear equipment in all removal, disconnect, and stop paths.

Use separate render-only instances for world, held, and predicted preview roles, sharing mesh/material assets. A local camera child anchor starts near `(0.35, -0.30, 0.65)` meters; a remote anchor is a child of the player's smoothed `Graphics` at a comparable right-side hand position. Offsets/rotation/scale belong in holdable configuration. Both anchors are replaceable by future avatar hands. The local view displays only its camera-held mesh; observers display only the graphics-held mesh. Held meshes have no Rigidbody, collider, or NetworkObject. Exclude them from `PlayerPresentation`'s capsule renderer hiding and verify clipping near walls and camera near plane. No per-frame networked hand position or camera pitch is required for this capsule placeholder.

Add a centered crosshair/pickup prompt and charge feedback to `Session.uxml`/`Inventory.uss`, rendered by `PickupPromptPresenter`. The prompt is separate from the inventory hover tooltip. Use Input System binding display strings for the last actively used supported device; do not hardcode keyboard text while using a controller. Hide prompt/charge state when UI is open or gameplay is unavailable. Scan one owner camera ray per rendered frame (or a configurable lower rate), not every rock.

Input edges are buffered once and consumed once. Clear buffered pickup/use/drop edges and cancel charge on inventory/session open, focus loss, ownership loss, unequip, disconnect, and disable. Input-map cancellation callbacks must **cancel**, not release a charged throw. Require release and a fresh press when resuming with a button still held. Add Select/Back to the UI inventory action, update its binding validator, and keep menu Submit/Cancel and gameplay Attack from firing together.

Charge is server-bounded: begin-use creates a token for the equipped entry and records the server tick; release consumes that token once and computes power from elapsed server ticks, clamped to the configured maximum. No trusted client-provided launch speed. The owner predicts charge locally, with correction to the accepted begin tick when available. Very short press/release must remain ordered even if both occur before begin acknowledgement. Cancel on entry/revision changes; an abandoned token expires. Drop while charging cancels the token and performs one ordinary drop.

### 3.5 Active rock physics and prediction

Create one pooled `ActiveRock.prefab` with a root Rigidbody/simple sphere collider, FishNet predicted `NetworkObject`, `RockFlight : TickNetworkBehaviour`, and one child `NetworkTickSmoother`. Use `PredictionRigidbody`, replicate/reconcile data, state forwarding, and the existing TimeManager physics mode, following `PlayerMotor` and `Assets/FishNet/Demos/Prediction/Rigidbody/Scripts/RigidbodyPrediction.cs`. No NetworkTransform, Rigidbody interpolation, or independent `FixedUpdate` simulation should also drive the root. Do not manually simulate the global physics scene during prediction.

The server spawns the flight after the transfer commits, assigning the thrower as prediction owner; authority remains on the server. Flight initialization includes world ID, revision, generation, request token, authoritative launch pose/velocities, and launch tick, available before the first simulation. Launch velocity is derived from validated aim, configured charge curve, and a configurable fraction of the server player's velocity. Sweep the rock volume from the authoritative eye/hand region to the launch origin to avoid spawning through a wall; fall back to a clear closer origin or reject without inventory removal. Drops use a short downward/forward release and the same settlement system.

After launch the owner's replicate payload contains no steering or collision decisions. The body evolves from the accepted launch state, gravity, and collisions. Ignore client attempts to change its speed/pose/identity. Reconcile `PredictionRigidbody` plus all extra simulation state that affects the next tick: airborne/contact damping mode, relevant contact/support state, and activation identity. Lifecycle effects are queued only from a non-replayed authoritative post-tick boundary. Client collision callbacks cannot settle or transfer items. Prevent launch impulses from applying again on replay or pool reuse.

Start with 60 Hz simulation and a 30 Hz authoritative reconcile cadence for active rocks; preserve FishNet's local reconcile/history requirements on skipped send ticks. Prove this cadence in the installed version before proceeding; if selective sending cannot be separated safely from local state creation, use 60 Hz reconciles and measure the stated bandwidth budget. One active rock should not require a bespoke per-frame RPC. FishNet already combines prediction states into MTU-sized writers in `Assets/FishNet/Runtime/Connection/NetworkConnection.Prediction.cs`; use that path instead of building a second transport. No tick subscription or predicted object survives settlement.

Give a non-host thrower immediate visual feedback: on valid local release, start one non-networked cosmetic preview associated with player/request token, using the locally predicted launch state. It cannot collide with or mutate gameplay objects. Predict free flight with the same gravity/damping tuning and swept queries against static geometry; authoritative collision/reconciliation remains decisive. On accepted spawn, adopt the preview's rendered pose as a temporary graphics offset, remove the preview, and let the authoritative predicted flight converge through a bounded smoothing window (initial target 100–150 ms). Show only one flight mesh. Rejection removes the preview and restores the confirmed held view. Host uses its authoritative flight directly. Cache either side of the request-to-spawn association so arrival ordering cannot duplicate a rock.

The initial server launch occurs at its validated execution tick; do not silently rewind global physics or accept a client historical collision outcome. Startup correction under latency is therefore expected and must be measured separately from steady-flight error. The thrower follows its current predicted flight, while spectators use their smoothed delayed view. These are not expected to have equal render-frame positions; compare matching authoritative ticks. Bit-identical PhysX trajectories across machines are not an acceptance requirement. Visible sustained divergence, repeated corrections, or a long apparent backwards flight on preview handoff is a failure to tune/fix, not something to dismiss as normal latency.

Configure air and contact linear/angular damping separately. Initial tuning: mass 0.4 kg; air linear/angular damping 0.02/0.05; contact linear/angular damping 3/5; throw speed 8–24 m/s over 1.0 s charge; drop speed 1 m/s; low restitution and moderate friction. These are starting values, not measured results. Use a simple collider fitted to a roughly 0.2–0.3 m rock and CCD. Derive grounded/contact mode using replay-safe queries/state rather than a one-shot collision callback; restore air damping after a bounce loses support.

Settle only on the server after valid ground support plus linear speed below 0.15 m/s and angular speed below 0.3 rad/s for at least 0.5 s (30 server ticks). Never settle based only on speed: a throw apex is not ground contact. On settlement, commit the final pose/revision reliably, replace active presentation with its dormant representation, despawn/pool the active object, zero velocities, and clear prediction/graphics history. The final static pose must be visible even if unreliable flight packets were lost. Out-of-bounds or maximum-flight-duration recovery follows the same reliable transition; start with a 20 s maximum and project the recorded safe position onto valid ground.

Set an initial global active-flight cap of 32 and server release cooldown of 0.2 s. Reject before inventory removal when capacity is exhausted; show feedback and restore any preview. Pool active objects and cosmetic/held views, never discard existing airborne items to make room. On owner disconnect, remove flight ownership before FishNet destroys owned objects and let server prediction continue. Verify actual callback ordering; integrate a pre-disposal recovery hook with player/server teardown, guarded to run once. If the available disconnect callbacks run after FishNet starts owned-object destruction, configure active flights to survive owner disconnect using the installed FishNet lifecycle facility, and validate that behavior before relying on an ownership-removal callback. Recover contained items directly as dormant records, independent of active-flight capacity; find non-overlapping supported poses with bounded ground queries and use the authored spawn area as the final fallback. If server shutdown is already underway, skip recoveries and clean up directly.

## 4. Implementation sequence and deliverables

Complete these steps in order. Each step must compile and meet its checkpoint before expanding the feature.

1. **Definitions, identity, and transactions.** Extend `ItemDefinition` with optional holdable settings/use kind; add `HoldableDefinition.cs` (or an equivalent serialized config), world record types, ID authoring/validation, and the prepared transfer boundary in `InventoryModel`/`PlayerInventory`. Add authoritative inventory access and committed-change events. Test identity conservation, full inventory, stale requests, duplicate pickup/release, failed preparation, and rollback before networking/presentation work.
2. **World lifecycle and pickup.** Add `Runtime/Items/WorldItem.cs`, `WorldItemRegistry.cs`, and a presentation lookup; implement sparse reliable state, readiness, server target validation, and atomic pickup through the owner request channel. Add `PlayerHoldables` targeting/input intent. Check two clients racing one rock and a late join after pickup. There must be zero per-rock NetworkObjects for dormant placements.
3. **Equipment and controls.** Extend `PlayerNetworkState`; connect HUD selection, gamepad controls, and input gating; add `HeldItemPresentation.cs` and `UI/PickupPromptPresenter.cs`; expose camera/anchor access from `PlayerPresentation`. Check explicit equip, private inventory, remote held view, switching slots, moving the equipped item, and every charge cancellation path.
4. **Flight vertical slice.** Add `Runtime/Items/RockFlight.cs`, active prefab registration, launch/charge validation, supported drop sink, prediction/reconcile, preview adoption, damping, and settlement. First prove one guest throw under the normal network profile, including spawn handoff and collision, then pooling/repeated throws and the active cap. Confirm no domain effects run during replay.
5. **Recovery and synchronization hardening.** Complete join while held/in-flight/settled, stale-generation handling, ownership removal on disconnect, inventory recovery before disposal, blocked drop, out-of-bounds recovery, and repeated-session cleanup. Exercise host callback duplication and dedicated-server-safe validation.
6. **Assets and repeatable installation.** Add `Assets/Game/Editor/HoldableAssets.cs` with public static `Install()` and `Validate()` entry points. Install idempotently with targeted prefab/scene edits; preserve existing authored content/tuning and IDs. Add `Settings/Items/BasaltRock.asset`, shared holdable tuning, `Prefabs/Items/RockVisual.prefab`, `DormantRock.prefab`, `ActiveRock.prefab`, and basalt render/physics materials. Add the registry and a small set of reachable rocks to Game. Keep a separate explicit performance fixture command for 1,000/5,000 placements. Register only the active prefab alongside Player in `Settings/SpawnablePlayers.asset`; preserve stable existing prefab IDs. Set stable unique scene NetworkObject IDs using the same explicit approach as `MvpAssets`. Update validators for required spawnables, registry, bindings, layers, collision matrix, unique world IDs, catalog references, anchors, materials, and one smoother per active body. Hook the intentional full-generation path to recreate holdable content without recursive installers.
7. **Verification and documentation.** Add focused EditMode tests under `Assets/Game/Tests/EditMode`, development-only `Runtime/Diagnostics/HoldableValidation.cs`, and extend `Specs/Validation/run-pair.ps1` with a `-Holdables` mode and scenario selection. Update `Assets/Game/README.md` with controls, tuning, lifecycle/network design, installation, and reproducible validation commands. Record actual results and remaining manual limitations in `Specs/Generated/PickUpAndHoldablesResults.md`. Move the completed plan and results to `Specs/Done` only after implementation is complete; update newly introduced documentation links accordingly.

Use `TwoBirds` runtime namespace and the existing `TwoBirds.Runtime` assembly. Add/edit Unity `.meta` files through Unity import and retain their GUIDs. Keep imported `Assets/FishNet` and source art unchanged. New type/file names above are proposed deliverables, not files that already exist.

## 5. Validation and acceptance

### 5.1 Automated correctness

Add meaningful tests for the shared inventory/world transition logic and revision-driven presentation arbitration:

- Pickup -> inventory -> equip -> throw/drop -> settle -> pickup preserves one world identity and one total item. Full inventory, unsupported drop, blocked launch, and capacity rejection leave both domains unchanged.
- Simultaneous pickup has one winner; duplicate request has one result/effect; stale revision/entry, forged owner, invalid ID, NaN/Infinity aim, and excessive range/obstruction are rejected.
- Equip follows entry within hotbar, clears on backpack transfer/removal, and does not auto-equip a newly acquired/replacement item. Observers receive equipped metadata but no inventory payload.
- Press/release in one input update, begin acknowledgement arriving after release/cancel, duplicate release, max charge, use-to-drop, and focus/UI interruption cannot throw twice or throw on resume.
- New-generation spawn, delayed old snapshot/despawn, settle-before-last-flight-packet, and host server/client callbacks cannot resurrect or double-render an item.
- Grounded dwell settles; apex/slow unsupported flight does not; airborne damping returns after bounce. Recovery and disconnect are idempotent; pool reuse resets forces, IDs, owner, contact timers, and history.

Use the existing test assembly or add a narrowly scoped item test assembly referencing the runtime/FishNet assemblies. Keep current inventory and movement tests passing; explicitly retain unsupported fixture-drop coverage rather than deleting it because rocks now support drops.

### 5.2 Two-process and visual scenarios

| Scenario | Required observation |
| --- | --- |
| A picks up, B watches | A receives one rock in inventory; both ground views disappear; B's private inventory stays unchanged. Repeat with guest and host as A. |
| A equips | A sees the basalt rock to the lower right without hiding it with the capsule; B sees it on A's right-side anchor. Unequip removes both views. |
| Tap/charged throws | Guest sees a preview within one rendered frame; one authoritative flight replaces it; B sees the same server trajectory with spectator delay. Charged throw travels farther. |
| Drop and UI drag-out | One rock leaves inventory and settles nearby. Equipped state clears correctly. Full/blocked/capacity failure preserves the entry. |
| Ground/wall/slope impacts | No tunneling at max throw speed; air travel remains useful; contact damping arrests rolling; settled poses agree and remain pickup targets. |
| Late join | Join after pickup, equip, active throw, and settle in separate runs. No original-ground duplicate, missing held mesh, replayed throw, or stale final pose. Also join with a large changed-record snapshot. |
| Disconnect/rejoin | Guest leaves while charging, holding, and during a throw. Contained rocks recover once, flight continues, and rejoin receives current state. Repeat start/stop five times with no leaked views, subscriptions, live inventory IDs, or active objects. |
| UI/devices | Physical keyboard/mouse and gamepad verify prompt labels, E/north, left-click/RT, Q/west, dpad selection, Back inventory, menu Cancel, and button-held resume. Automated input does not replace this check. |
| Network profiles | Run LAN, normal (50 ms delay each direction, 10 ms jitter, 1% loss), and stress (100 ms each direction, 25 ms jitter, 3% loss) using the existing proxy. Verify no loss/duplication and eventual reliable convergence in all profiles. |

Instrument per-flight request/activation IDs, server ticks, launch/impact/settle events, positions, correction magnitude, preview handoff, and active counts. Compare samples at matching server ticks, separately reporting simulation error and rendered smoothing offset. Initial quality targets after startup: free-flight position error p95 <= 0.25 m on normal and <= 0.5 m on stress; final position difference <= 0.02 m and rotation difference <= 2 degrees after reliable settle delivery. Report startup and impact errors separately, including maximum preview handoff displacement and convergence time. Tune or fix repeated visible snaps; do not claim these targets were achieved without logs and rendered checks.

### 5.3 Scale and traffic

Measure a two-player baseline, then 1,000 and 5,000 dormant rocks with 0, 1, 16, and 32 active flights. Placement/performance fixtures must not replace the normal authored game scene. Include a long sequence of pickup/rethrow operations so the sparse state dictionary and pools are tested after use, not only in an untouched scene.

- After initialization/settlement and reliable delivery, **zero recurring item movement messages** for dormant/inventory/equipped rocks. Player movement and transport heartbeats are baseline traffic, not item movement. Confirm active-rock NetworkObject count returns to zero.
- Holdable networking and physics loops scale with active count, not total dormant count. No per-item Update/network ticks or repeated scene-wide searches. Owner targeting performs one bounded query; static rendering/colliders may still incur ordinary scene costs.
- Measure incremental host outbound bytes/sec, packets/sec, active reconciliation cadence, tick CPU, allocations, and spawn/settle bursts. Initial engineering budget: <= 128 KiB/s incremental host outbound traffic to one guest with 32 active rocks and <= 2 ms p95 incremental host tick CPU on the documented Ryzen 9 3900X after warm-up. Record actual values and fixture/render settings; optimize pooling, payload/cadence, and batching if over budget. Do not lower player tick quality to meet the item budget.
- Idle holdable networking allocates no managed memory per tick after warm-up. Separate rendering/physics/serializer allocations in results rather than asserting the entire application allocates zero.
- Changing dormant count from 1,000 to 5,000 must not increase recurring item network bytes. Large initial/rejoin snapshots are allowed and must complete without missing records or main-thread stalls that break connection timeouts.

### 5.4 Unity commands and completion evidence

Prefer the installed Unity CLI. For batch commands the project must not already be open in another Unity process; use Unity MCP for required editor operations if the CLI cannot operate on the open project and MCP is available. Do not silently close an editor or run the destructive full scene generator. A second Unity process against a locked project is not a successful validation.

From the repository root, after implementing the named entry points:

```powershell
$unityExe = 'C:\Program Files\Unity\Hub\Editor\6000.5.7f1\Editor\Unity.exe'
New-Item -ItemType Directory -Force -Path 'Specs/Validation/Results/holdables' | Out-Null
& $unityExe -batchmode -nographics -quit -projectPath . -executeMethod TwoBirds.Editor.HoldableAssets.Install -logFile 'Specs/Validation/Results/holdables/install.log'
& $unityExe -batchmode -nographics -quit -projectPath . -executeMethod TwoBirds.Editor.HoldableAssets.Validate -logFile 'Specs/Validation/Results/holdables/assets.log'
& $unityExe -batchmode -nographics -quit -projectPath . -executeMethod TwoBirds.Editor.MvpChecks.Run -logFile 'Specs/Validation/Results/holdables/mvp-checks.log'
& $unityExe -batchmode -nographics -projectPath . -runTests -testPlatform EditMode -testResults 'Specs/Validation/Results/holdables/editmode.xml' -logFile 'Specs/Validation/Results/holdables/editmode.log'
& $unityExe -batchmode -nographics -quit -projectPath . -executeMethod TwoBirds.Editor.MvpAssets.Build -logFile 'Specs/Validation/Results/holdables/build.log'
./Specs/Validation/run-pair.ps1 -Executable './Builds/Windows/TwoBirds.exe' -Output './Specs/Validation/Results/holdables/normal' -Profile normal -Route idle -Seconds 125 -Holdables
```

Check each process exit code and its log/test results before proceeding. The installer, item validator, and `-Holdables` switch above are deliverables of this plan and do not exist yet. Repeat the pair run with separate output folders for LAN and stress and use the implemented scenario switch for join/disconnect/capacity/performance cases. Use rendered standalone/editor runs for held meshes, prompt layout, and handoff quality; headless runs cannot prove visual acceptance. Start background helpers with `-WindowStyle Hidden`; only show interactive validation windows when the user needs to control them.

Completion requires a clean Unity compile/build, passing existing and new automated checks, results for both host/guest throwing and all late-join states, traffic/physics measurements, and honest recording of device/visual checks actually performed. The results document must include exact commands, profiles, counts, measurements, and any remaining limitations. Do not mark implementation complete or move this plan merely because the code compiles.
