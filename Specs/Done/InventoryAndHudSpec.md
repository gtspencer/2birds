# Inventory and HUD specification

Status: implemented September 13, 2026. Automated validation and remaining acceptance checks are recorded in [InventoryAndHudResults.md](InventoryAndHudResults.md). Full A1-A13 acceptance is not claimed.

Source: [initial request](../Initial/InventoryAndHud.md). Implementation sequence: [InventoryAndHudPlan.md](InventoryAndHudPlan.md).

## Outcome and scope decisions

Provide a local player's UI Toolkit health indicator, eight-slot hotbar, and toggleable inventory. Support stackable and unique items, deterministic insertion, drag reordering, and a safe inventory-to-world transfer boundary. Work within the existing Solo/Host/Join session and FishNet server authority.

The user confirmed both the inventory layout and the interface-only scope for world pickup/drop:

| Source wording | Decision and status |
| --- | --- |
| Hotbar is the "first slot"; hotbar row is numbered 1-8; "8 rows of 3" | **Confirmed by the user:** three rows of eight slots, 24 total, with the first row as the hotbar. Slots 0-7 are shared data, not eight extra slots or item copies. Treat "first slot" as "first row." |
| World interaction and visible dropping are success criteria, but pickups and holdables must come later | **Confirmed by the user:** define and test inventory add/drop interfaces; defer visible world items and pickup interactions to [PickUpAndHoldables.md](../Initial/PickUpAndHoldables.md). Implement the inventory-side contracts and test their transactions. Without a world-drop provider, dropping fails visibly and preserves the item. |

World pickup/drop acceptance is limited to the inventory interfaces and their transaction tests in this delivery. Visible world pickup/drop remains deferred and must not be reported as complete.

In scope: inventory state, item definitions, owner synchronization, health state sufficient to drive the HUD, reusable UI slots, pointer dragging, local hotbar selection, input/modal coordination, and focused validation.

Out of scope: world item prefabs or spawning, interaction rays/prompts, equipping/held visuals, item use/throwing, physics replication for items, damage sources, death/respawn rules, persistence, containers/trading, crafting, weight, sorting, stack splitting, and a controller inventory manipulation system. Existing controller movement and session navigation must continue to work.

## Inspected project baseline

| Existing asset or code | Integration implication |
| --- | --- |
| `ProjectSettings/ProjectVersion.txt`, `Packages/manifest.json`, FishNet `NetworkManager.cs` | Unity 6000.5.7f1, Input System 1.20.0, Test Framework 1.7.0, FishNet 4.7.3. No additional runtime package is required. |
| `Assets/Scenes/MainMenu.unity`, `Assets/Scenes/Game.unity` | The two enabled build scenes already exist. Extend the game scene. |
| `Assets/Game/UI/Session.uxml`, `Shared.uss`, `SharedPanel.asset` | UI Toolkit session overlay already exists; panel scaling uses a 1280x720 reference. Reuse the document/panel and existing visual style. Scope new styles to avoid changing menu buttons. |
| `Assets/Game/Runtime/UI/SessionOverlay.cs` | Handles `UI/Cancel` directly and renders the session panel. Consolidate cancel routing so one press cannot affect two panels. |
| `Assets/Game/Runtime/Networking/SessionController.cs` | Exposes `LocalPlayer`, `Changed`, `PanelOpen`, and `SetPanel`. Currently `SetPanel` directly enables/disables gameplay. Extend this input decision to include inventory state. |
| `Assets/Game/Runtime/Player/PlayerInputReader.cs` | Owner-only input; `SetGameplay` toggles the entire Player map, clears movement/jump, and controls cursor locking. Inventory toggle must remain available while that map is disabled. |
| `Assets/Game/Runtime/Player/PlayerNetworkState.cs` | Public state currently contains only spawn slot and revision. Keep the full inventory out of public player state. No inventory or health implementation exists under `Assets/Game`. |
| `Assets/InputSystem_Actions.inputactions` | Existing Player `Previous`/`Next` bind keyboard 1/2. Resolve these conflicts when adding direct slot selection. Preserve required UI actions. |
| `Assets/Game/Editor/MvpAssets.cs`, `MvpChecks.cs` | Asset generator replaces scenes/prefabs; configuration checks expect only Player in the spawn registry. Extend generation deliberately so it preserves this feature. No world prefab registration is needed here. |

## Inventory and item rules

The server owns a fixed 24-slot array. UI reads snapshots and submits intentions; it never edits authoritative slots. New players start empty. Contents survive opening/closing panels and the current motor's fall reset. They end with player despawn/session exit; reconnect starts a new inventory.

An `ItemDefinition` ScriptableObject holds a stable definition ID, display name, optional icon, and positive maximum stack size. Maximum stack size 1 means unique/non-stackable. A runtime catalog resolves IDs on server and clients. Missing/duplicate IDs or invalid limits fail asset validation. Missing icons use a readable name/initial fallback. Do not send Unity asset references or icon data as network item identity.

A slot contains definition ID, quantity, and a server-issued entry ID, or one canonical empty value. Nonempty quantities are 1 through the definition's maximum. Unique items have distinct IDs even when they share a definition; quantity is always 1. Moving/swapping preserves IDs. Merging compatible stacks retains the destination ID and the source ID if a remainder remains; an emptied source ID is retired. Allocate a fresh ID when an insertion creates a stack. IDs need only be unique within the active server session; future world transfers must preserve unique item identity. No per-item durability/modifier system is required now.

Stack compatibility means equal definition ID with a maximum greater than 1. A server add operation visits these groups in order, each in ascending slot index:

1. Compatible non-full hotbar stacks, slots 0-7.
2. Empty hotbar slots, slots 0-7.
3. Compatible non-full storage stacks, slots 8-23.
4. Empty storage slots, slots 8-23.

This deliberately fills an empty hotbar slot before merging into storage. Fill each visited stack up to its limit before advancing. Return accepted quantity and remainder; partial insertion is permitted. Zero capacity leaves state unchanged. Invalid definitions, nonpositive quantities, duplicate unique IDs, or numeric overflow are rejected without mutation. A future pickup consumes only the accepted quantity and retains the remainder in the world.

| Drag source and destination | Result |
| --- | --- |
| Empty source or same slot | No mutation. |
| Occupied source to empty slot | Move the entire entry. |
| Compatible stack with spare destination capacity | Transfer as much as fits; retain any remainder in the source. |
| Compatible stack with full destination | No mutation; show that the stack is full. |
| Different definitions, or two unique items | Swap the complete entries. |
| Inventory padding, gaps, title, or other non-slot UI | Cancel; preserve contents. |
| Outside inventory panel over the game viewport | Request transfer of the entire source entry through the drop boundary. |

No automatic compaction follows a move, merge, or drop. Selection refers to a slot index: moving its contents changes the selected item, without making selection follow an entry.

## Authority and synchronization

Add `PlayerInventory` to the existing Player network prefab. Keep the deterministic inventory model separate from its FishNet wrapper so capacity, conservation, and transaction rules can be tested without UI or a live session.

Use a server-written, owner-readable FishNet SyncVar carrying one revisioned inventory snapshot. With only 24 slots and infrequent user actions, a complete compact snapshot is the initial implementation choice; no per-tick inventory transmission is needed. Allocate/copy snapshot contents on commit rather than mutating a published array in place. Verify serialization and owner-only initial state with the installed FishNet source, including host callbacks. Health uses a separate small owner-readable snapshot. The server necessarily retains authoritative data for every player.

Move/drop requests contain request ID, expected inventory revision, source index and entry ID, and destination index for a move. Quantity and item definition are derived from server state. Use reliable owner-required Server RPCs. The server validates live ownership, operation kind, indices, revision, entry identity, and transfer availability before applying one transaction. There is no client-accessible arbitrary add or health-write RPC.

Successful state-changing transactions increment revision once. Invalid and no-op requests do not. Return a typed owner-only result with request ID, status, and authoritative revision; rejected stale requests include the current snapshot for recovery. Expected failures include stale state, invalid request, full destination, unavailable drop provider, and failed transfer. Convert these to short player-facing messages without exposing protocol details.

Permit one in-flight mutation per local inventory. Keep the last confirmed contents visible with a pending source indication until the result and its state revision are available. Handle result/snapshot arrival in either order and never replace newer state with older state. Cache the latest completed request/result per player to make a repeated request ID harmless; older IDs cannot mutate state. Requests that race a server grant fail revision validation and can be retried as a new user action after refresh.

After five seconds without a result, show a connection/pending message and request current state; do not infer failure, remove items, or automatically resend the mutation. Closing the panel cancels an unsubmitted drag but does not roll back a submitted server transaction. Despawn and disconnect clear bindings and pending presentation. Host must not apply operations twice through server/client callbacks.

## Add/drop extension boundary

Provide a server-only add entry point returning accepted/remainder quantities and the resulting revision. Keep granting items behind trusted game code or test fixtures. Future interaction code is responsible for validating reach, world-item availability, and exclusive ownership before invoking it.

Define a small server-side drop sink contract accepting the authoritative entry payload and player identity. The future sink will determine a valid position from the server player state; the UI does not supply a trusted world transform. Transfer must be all-or-nothing: the sink accepts the whole entry exactly once and inventory removes it, or both remain unchanged. Stage the removal and let the sink accept synchronously before publishing the inventory commit; disallow reentrant mutations during this transaction. A sink must report failure without side effects and must not return success until it owns the transferred payload. Asynchronous world creation would require a reservation/rollback design in the later spec.

No provider is installed in normal gameplay in this delivery. An outside release shows "Dropping items is not available yet" and leaves the slot intact. Test sinks exercise acceptance, rejection, and failure without spawning production world objects. Do not discard an item or treat a logged event as successful world creation.

The later pickup/holdables feature must connect both directions, preserve unique identity and stack quantities, validate world placement, and verify that all clients see the corresponding world changes. Its end-to-end pickup/drop cases remain deferred here.

## HUD and inventory presentation

Use UXML for structure, USS for layout, and reusable `InventorySlotView` elements for both HUD and inventory. A presenter binds one local owner's inventory and health. Cache element queries and update from state/selection changes. Remote players must never create additional HUDs or substitute their values into the local display.

Closed inventory: show eight bottom-center hotbar slots and a health bar plus `current / maximum` near the bottom left. Show key labels 1-8, icon/name fallback, quantity for stacks greater than 1, and an outline for the selected slot. Empty slots remain visible. Health is readable without color alone.

Open inventory: display a centered fixed grid, title, close button, and short drag guidance. Its first row carries key labels 1-8. Hide the standalone hotbar during this view so the same entries are not presented as two separate drag destinations; the numbered first row remains the visible hotbar. Keep health and existing session status visible where space permits.

```text
                 Inventory                        [Close]
  Hotbar          [1] [2] [3] [4] [5] [6] [7] [8]
  Storage         [ ] [ ] [ ] [ ] [ ] [ ] [ ] [ ]
                  [ ] [ ] [ ] [ ] [ ] [ ] [ ] [ ]
                 Drag to move; drag outside to drop

  Health 100 / 100
```

Maintain an eight-column layout at 1280x720, 1920x1080, and 2560x1440 using panel scaling. Verify no clipped slots, health, close control, or overlap with session controls. Use scoped classes for empty, occupied, selected, focused, hovered, pending, and valid/invalid target states. Show the item name on hover/focus with a runtime label/tooltip element.

Use runtime pointer down/move/up handling and pointer capture for dragging; UI Toolkit reports capture loss, which must cancel an unsubmitted drag. A five-panel-unit movement threshold distinguishes click selection from dragging. The ghost ignores picking; hit-test destination geometry in panel coordinates rather than trusting the captured event's target. Release outside the application viewport, focus loss, or document detach cancels rather than drops. Clear capture and ghost on every exit path. [Unity pointer capture behavior](https://docs.unity3d.com/6000.5/Documentation/Manual/UIE-Capture-Events.html).

## Input, focus, and session integration

Add an inventory toggle action bound to keyboard Tab and I in a UI-capable map that remains enabled when Player actions are disabled. Retain the existing project-wide UI action names/types and runtime UI input integration. [Unity runtime UI input handling](https://docs.unity3d.com/6000.5/Documentation/Manual/UIE-Runtime-Event-System.html).

Bind keyboard 1-8 to local selection; initial selection is slot 0. Selection only changes the highlight and a read-only selected-slot accessor for future use; it does not equip or use an item. Gate it to active gameplay or open inventory, and suppress it while a text field owns focus. Remove/remap conflicting unused Player Previous/Next keyboard bindings. Do not bind Q, attack, or interact to new gameplay in this delivery.

Use one modal/input coordinator shared by `SessionController` and `SessionOverlay`, rather than competing callbacks. It decides whether gameplay is allowed: session in game, valid local player, application focused, inventory closed, and session panel closed. Opening inventory clears buffered movement/jump, suppresses movement/look, unlocks the cursor, and focuses the inventory. Simulation/network time continues.

| Input or lifecycle event | Required behavior |
| --- | --- |
| Tab/I in active gameplay | Open inventory. |
| Tab/I or close button in inventory | Cancel unsubmitted drag, close inventory, restore gameplay if permitted. |
| Escape / existing UI Cancel during a drag | Cancel the drag only. |
| Escape / UI Cancel with inventory open and no drag | Close inventory only. |
| Escape / UI Cancel in normal gameplay | Open existing session panel. |
| Resume / UI Cancel in session panel | Close session panel and restore gameplay if permitted. |
| Inventory toggle while session panel is open | Ignore. |
| Session panel opened externally or application loses focus | Cancel drag, close inventory, show session panel; gameplay stays suppressed. |
| Leave, despawn, ownership loss, or document disable | Unsubscribe callbacks, cancel drag, clear local view and pending presentation. |

Only one path consumes each cancel press; stopping UI event propagation alone does not suppress a separate raw InputAction subscriber. Returning focus does not automatically resume. Check the initial `OnStartClient` gameplay enable path as well as `SetPanel`, so spawning cannot bypass a modal restriction. Closing inventory never generates a buffered jump or camera delta.

Keyboard/mouse is the inventory manipulation target. Keep existing controller UI cancel/navigation operational; controller pickup, virtual cursor, and drag equivalents belong to later work.

## Health model

Add a small server-owned `PlayerHealth` on the Player prefab with current and maximum integer values. Default maximum is configurable and defaults to 100; new players start full. Maximum must be at least 1; server writes clamp current to 0 through maximum. Publish state changes and provide an initial snapshot when the HUD binds. Render zero and non-default maximums correctly without division by zero.

Provide a trusted server setter for later gameplay and test fixtures. Do not add damage sources, death behavior, regeneration, or health consequences to the existing fall reset. Zero health changes the display only in this delivery. Inventory toggles leave health unchanged; disconnect/reconnect creates fresh full health.

## Acceptance and completion

| ID | Scenario and observable result |
| --- | --- |
| A1 | Solo and Host/Join each show exactly one local health indicator and eight-slot hotbar after their player is ready; neither appears on the main menu. |
| A2 | Open inventory: exactly 24 slots in three rows of eight; first row numbered 1-8. Changes to slots 0-7 appear in the closed hotbar with identical identity/count. |
| A3 | Grant stacks/unique entries through a server fixture. Verify partial hotbar stack, empty hotbar, partial storage stack, empty storage priority in that order. Full inventory returns an unconsumed remainder; no entry is lost. |
| A4 | Verify move, swap, partial/full merge, same-slot, and unique-item cases. Total quantities and unique identities are conserved; selection remains attached to its slot. |
| A5 | Drag across panel edges/scaling, padding, unrelated UI, and outside viewport. Only a valid viewport release invokes drop. Escape, capture loss, close, and focus loss leave unsubmitted contents unchanged. |
| A6 | Accepting fake drop sink receives the exact whole payload once and clears source once. Rejecting/failing or absent sink preserves state and shows useful feedback. No normal gameplay world object is required here. |
| A7 | Remote client requests cannot mutate another player's inventory, grant items, or set health. Invalid indices, quantities, entry IDs, stale revisions, and repeated request IDs cannot duplicate/remove items. |
| A8 | Under delayed updates, pending contents stay consistent; snapshot/result arrival order, close during request, concurrent server grant, and disconnect do not cause duplicate mutations or stale UI rollback. |
| A9 | Host plus standalone client show independent inventory/health. Initial owner synchronization is correct for a late join; observers receive no inventory payload. Verify host operations execute once. |
| A10 | Server changes health to 50/100, 0/100, and 25/50; owning HUD updates accurately. Invalid maximums are rejected and current values clamp. No damage/death system is introduced. |
| A11 | Inventory suppresses move/look/jump; toggle still closes it. Escape follows precedence; session Resume, focus return, and repeated input never enable gameplay through an open panel. Existing controller session controls still work. |
| A12 | At all specified resolutions, labels, counts, health, and controls are readable; pointer target matches ghost position. Ten open/close and start/leave cycles produce no duplicate HUDs, callbacks, or stuck cursor. |
| A13 | Empty/default production inventory has no debug grants, test objects, or registered test prefabs. Existing build/configuration checks and movement/session smoke checks pass. |
| Deferred D1 | Interact with a real world item and consume only the quantity accepted into inventory; all clients see the world change. Requires later pickup implementation. |
| Deferred D2 | Drop to a valid world location and observe the preserved item/count on all clients. Requires later world-drop provider and networking. |

This feature implementation is complete when A1-A13 pass, temporary test assets are cleaned up, and results explicitly retain D1-D2 as deferred. Record actual validation evidence and any unmet criteria; interface tests alone are not proof of visible world pickup/drop. The implementation documents are archived in `Specs/Done`; the results document identifies the acceptance cases still requiring validation.
