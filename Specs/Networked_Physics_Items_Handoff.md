# Networked physics items: agent handoff

## Start here

Continue the networked item implementation from the user's in-game findings below. Read [AGENTS.md](AGENTS.md), this document, and [Networked_Physics_Items_Revised_Plan.md](Networked_Physics_Items_Revised_Plan.md), then inspect the relevant current code before editing. The user's decisions below take precedence over conflicting wording in the plan.

Workspace: `C:\Users\spenc\source\repos\2birds`.

Stack: Unity **6000.5.7f1**, FishNet **4.7.3**, UI Toolkit, New Input System; Windows/PowerShell.

This handoff describes the source implementation, not a successful gameplay test. No builds, automated tests, in-game validation, or performance profiling were run by the implementing agent. The user will supply their own validation results. Do not assume the editor migration has executed successfully merely because its script exists.

### Working constraints

- Do not run validation unless the user explicitly requests it. The plan's validation scenarios are not blanket permission to run tests or profiling.
- Use the Unity CLI when available; otherwise use Unity MCP. Inspect which tools are available in the fresh session. The previous session had this project open in Unity and no callable Unity MCP tools.
- Let Unity generate `.meta` files. Prefer prefab/project-setting edits over gameplay scene edits, and notify the user immediately if component, prefab, or asset changes are needed.
- Keep changes surgical, comments short, and references cached. Do not add a work log to `AGENTS.md`.
- Preserve the user's existing edits. No commit or clean working tree is asserted by this document.
- Surface inconsistencies early and ask only about design questions that materially block progress. Do not introduce additional authority systems or abstractions speculatively.

## Decisions to preserve

### Every physical object keeps its identity

The user's explicit correction was:

> Keep the object's id through stacking; stacking is only an inventory concept, not a world concept (dragging a stack out of the inventory creates 1 of each item in the stack). i don't want to create new network ids.

Consequences:

- Each world record represents one individual object, without a stack quantity.
- `ItemStack.WorldIds` groups existing object IDs; its count comes from the array length.
- Pickup, inventory stacking, slot swaps, equipment changes, drop, and throw preserve those IDs.
- Drop/throw releases the first object in the selected stack. Dragging a stack outside the inventory releases every object in that stack separately.
- Do not merge objects into a new world stack, retire IDs when stacking, or allocate replacement IDs when splitting/releasing.
- IDs originate from `BakedPickup.BakedId`. Zero or duplicate baked IDs produce an error and disable the affected seed; runtime registration does not invent replacements.
- Operation IDs, hit-event IDs, and the session epoch are separate sequencing data, not replacement world-object identities. This implementation does not add disk/save persistence.

### Physics and presentation

- The host/server simulates world collisions as one physics world. The releasing client predicts presentation for immediate response, without taking authoritative ownership of the flight.
- Host world bodies remain dynamic when sleeping. Guests may use kinematic representations for remote/sleeping items.
- Stored objects are held and hidden. Only the first object in the selected slot appears in hand.
- Gameplay knockback is a discrete authoritative impulse routed through player prediction. Damage rules remain separate.
- Use the existing FishNet physics clock. Do not replay or independently advance world item physics during player reconciliation.
- Released objects survive the thrower's disconnect. Held inventory objects follow the existing discard-on-disconnect policy.

### Registry placement and FishNet access

`SessionRoot` is the persistent network manager, not a spawned `NetworkObject`. `WorldItemRegistry` is therefore a **MonoBehaviour using FishNet broadcasts**. The existing gameplay-scene `PickupRegistry` network component bridges scene startup/shutdown to it. Rocks do not each get a `NetworkObject`.

**Leave `NetworkManager.PredictionManager` internal.** The implementation initially accessed that property incorrectly from game code. `WorldItemRegistry` now caches `GetComponent<PredictionManager>()` in `Start()` and uses the cached reference. `PlayerMotor` can use the public accessor inherited from `NetworkBehaviour`. Do not change FishNet's property visibility to address this integration.

## Code map

Paths below are relative to the repository root.

| File | Responsibility |
|---|---|
| [ItemDefinition.cs](Assets/Game/Runtime/Items/ItemDefinition.cs) | `ItemStack` with individual IDs; item release speed, mass, damping, spin, material, collision mode, speed limit, and sleep tuning. |
| [WorldItemMessages.cs](Assets/Game/Runtime/Items/WorldItemMessages.cs) | Lifecycle/motion records, baseline broadcasts, and hit-event data. |
| [WorldItemRegistry.cs](Assets/Game/Runtime/Items/WorldItemRegistry.cs) | Baked registration, authoritative records, active bodies, broadcasts, baselines, held/release transitions, pending release association, cleanup, and pooling. |
| [WorldItem.cs](Assets/Game/Runtime/Items/WorldItem.cs) | Rigidbody/visual presentation, interaction, prediction history, interpolation, corrections, collision exceptions, and impact forwarding. |
| [PlayerInventory.cs](Assets/Game/Runtime/Inventory/PlayerInventory.cs) | Atomic pickup/release operations, ID reservations, optimistic inventory, operation acknowledgments, selection, swapping, and rollback. |
| [PlayerEquipment.cs](Assets/Game/Runtime/Player/PlayerEquipment.cs) | Hand/viewmodel attachment transforms; uses the logical item's existing representation. |
| [PlayerPresentation.cs](Assets/Game/Runtime/Player/PlayerPresentation.cs) | Exposes the player's cached local camera. |
| [PlayerInputReader.cs](Assets/Game/Runtime/Player/PlayerInputReader.cs) | Drop/throw input through Input Actions. |
| [HudController.cs](Assets/Game/Runtime/UI/HudController.cs) | Slot selection, swapping, and dragging a complete stack into the world. |
| [PlayerInteraction.cs](Assets/Game/Runtime/Player/PlayerInteraction.cs) | Cached camera and pickup raycast mask; environment still occludes pickups. |
| [PlayerItemHitbox.cs](Assets/Game/Runtime/Player/PlayerItemHitbox.cs) | Separate kinematic capsule following the player's motor body. |
| [PlayerMotor.cs](Assets/Game/Runtime/Player/PlayerMotor.cs) | Ticked hit history, deduplication, reconciled hit progress, and temporary knockback protection from movement acceleration. |
| [BakedPickup.cs](Assets/Game/Runtime/Pickup/BakedPickup.cs) / [PickupRegistry.cs](Assets/Game/Runtime/Pickup/PickupRegistry.cs) | Authoring IDs/definitions and scene lifecycle bridge; the old collected-pickup set is gone. |
| [ItemKillVolume.cs](Assets/Game/Runtime/Items/ItemKillVolume.cs) | Marker for a trigger volume that removes host world items. No kill volume is automatically created. |
| [WorldItemSetup.cs](Assets/Game/Editor/WorldItemSetup.cs) | Automatic editor setup for prefabs, layers, collision matrix, and input bindings. |

## Runtime flow

### Inventory transactions

`Collect`, `DropSelected`, `ThrowSelected`, and `DropSlot` submit operations through `PlayerInventory`. The guest predicts its inventory and presentation before sending a reliable `CmdOperate` request. The host processes its own input directly through `ProcessRequest` for immediate release.

The host checks pickup state/capacity before changing ownership. Release checks cover all requested IDs before removing any from inventory. Ordered operation IDs prevent duplicate commits. Authoritative inventory snapshots acknowledge operations; remaining pending requests are reapplied to construct the local view. Rejected operations restore presentation from registry records.

The inventory now uses explicit snapshots rather than the former `SyncList` flow. Consider the order of inventory acknowledgments and world lifecycle records when diagnosing disappearing items, duplicates, or rollback problems.

### Motion and lifecycle

- Host post-tick processing observes sleep/wake transitions and populates `activePhysicsItems`. It still scans registered representations to discover newly awakened bodies.
- Ordinary motion uses unreliable batches of at most **8 entries**, at a default **20 Hz** on the existing **60 Hz** physics clock.
- Lifecycle changes, late-join baselines, and the final sleep pose use reliable delivery. Sleeping bodies stop contributing continuous motion traffic.
- Lifecycle revisions and simulation ticks order records; `earlyMotion` retains motion that arrives before its lifecycle record. Removed records remain as tombstones until world teardown.
- `pendingReleases` associates authoritative confirmations with the same already-predicted object ID.
- Local prediction retains **128 tick samples**. The comparison maps host launch-relative ticks into local prediction history using `LaunchTick` and `localLaunchTick`.
- A sufficiently large positional error snaps the physics body to the received state and preserves a temporary offset on `VisualRoot`. Only the visual offset decays.
- Remote presentation uses a **16-sample** buffer with brief extrapolation. Locally predicted throws do not use that interpolation path.

### Player impacts and replay

`WorldItem.OnCollisionEnter` forwards host item/proxy impulses through the registry to `PlayerMotor.QueueItemHit`. Events carry source ID, server tick, player tick, event ID, and impulse. `MotorState.LastHitId` and `KnockbackTicks` reconcile along with the body. Movement acceleration is suppressed for 12 ticks after an applied hit.

`OfflineRigidbody` pauses item/proxy physics during player replay. The proxy is authored under the player, then detached in `PlayerInventory.OnStartNetwork`. On the client, the player's `RigidbodyPauser` is restricted to the motor body. Preserve this separation when addressing replay or duplicate-collision problems.

## Unity setup and tuning

`WorldItemSetup.Install()` runs after editor assembly load and on returning to Edit Mode. It is also exposed through **Two Birds > Set Up World Item Physics**.

The setup code:

- Adds `WorldItemRegistry` and asset references to `Assets/Game/Prefabs/SessionRoot.prefab`.
- Adds/configures Rigidbody, `WorldItem`, `OfflineRigidbody`, and `VisualRoot` on registered item prefabs, including `Rock.prefab`.
- Adds the item hit proxy and `Graphics/EquipSlot` to `Player.prefab`.
- Uses layer **8 = ItemHeld**, **9 = ItemWorld**, **10 = PlayerItemHitbox**. Held items have no physics collisions; world items ignore the primary Player layer; hit proxies collide with world items.
- Updates `Assets/InputSystem_Actions.inputactions`: **Q / D-pad down** drops; **left mouse / right trigger** throws. The HUD's duplicate raw Q handling was removed.
- Uses Unity asset APIs; it does not explicitly edit the gameplay scene.

**Setup caveat:** `Install()` returns immediately if the SessionRoot prefab already has `WorldItemRegistry`. The menu command has the same guard. Re-running it does not repair a partial setup once that component exists. If the user's results indicate missing components, references, layers, or bindings, inspect the specific affected assets before choosing a repair.

These are C# defaults, not assertions about the user's current Inspector values:

| Setting | Default |
|---|---|
| Throw / drop speed | 14 / 1.5 m/s |
| Player velocity inheritance | 1 |
| Item mass | 1 kg |
| Linear / angular damping | 0.05 / 0.1 |
| Initial angular velocity | `(3, 1, 2)` |
| Collision detection / maximum speed | ContinuousDynamic / 50 m/s |
| Positional correction threshold / visual decay | 0.05 m / 0.075 s |
| Remote interpolation / maximum extrapolation | 0.1 s / 0.1 s |
| Thrower collision grace | Until separation or 0.3 s, whichever comes first |
| Minimum hit impulse / knockback protection | 0.5 / 12 ticks |
| World bounds | Center zero, size `(2000, 2000, 2000)`, plus `GameSettings.FallBoundary` |

Release placement currently uses 0.65 m forward clearance and a 0.12 m sphere cast. Whole-stack drops use a four-column grid with 0.24 m spacing. These constants are useful starting points if the user reports overlaps or wall-adjacent release problems; they are not derived from arbitrary prefab sizes.

## User's in-game validation report

Fill this section in, or attach a separate report and point the fresh agent to it. Blank results mean no outcome has been supplied.

### Test environment

- Revision/build or approximate test time:
- Host: Editor or build; machine:
- Guest: Editor or build; machine:
- Solo/host/join flow used:
- Simulated latency, packet loss, or out-of-order settings:
- Item or physics Inspector overrides:
- Console errors/warnings and full relevant stack traces:

### Observations

| Scenario | Expected visible behavior | Result / issue reference |
|---|---|---|
| Host and guest drop/throw | Immediate local release; smooth remote motion. | |
| Rapid inputs and hotbar changes | No duplicated objects, overspent counts, or stale held models. | |
| Stack drop | Every object appears separately; existing IDs are preserved. | |
| Simultaneous pickup / full inventory | One claimant wins; rejected pickup restores the correct representation and count. | |
| Moving pickup | Rolling/bouncing object enters inventory without duplication or disappearance. | |
| Piles and simultaneous throws | Hit items wake; support removal allows falling; clients converge. | |
| Ricochet and player impact | Initial self-ignore expires; later hit gives one lasting knockback response. | |
| Latency and packet loss | Corrections remain acceptable; no repeated snaps, duplicate hits, or lost items. | |
| Late join | Moved, sleeping, held, and removed items match the host; active motion continues. | |
| Thrower disconnect / session restart | Released items continue; held contents follow discard policy; no stale objects on restart. | |
| Out-of-bounds / configured kill volume | Host removes the object and guests agree. | |
| Large item counts | Record visible jitter or frame hitches; performance claims require measurements. | |

For each issue, supply:

```text
Issue:
Who performed the action: host / guest
Steps to reproduce:
Expected:
Host observed:
Guest observed:
Frequency and network conditions:
Inventory counts / object IDs, if available:
Relevant console output or clip reference:
Priority:
```

## Instructions for the next agent

1. Read the supplied report first. Address startup errors and object loss/duplication before motion feel or tuning.
2. Trace each reported symptom to the relevant paths above. Treat this document as orientation; current source and actual observations are authoritative.
3. Preserve individual IDs, host world physics, immediate local response, and the separation between item simulation and player replay.
4. Distinguish a demonstrated defect from a tuning issue or an untested possibility. Do not assume the migration, reconciliation, or scale targets have passed validation.
5. Apply focused fixes within the user's requested scope. Do not replace the architecture or edit FishNet internals solely to bypass a game-code integration mistake.
6. Do not run tests, gameplay sessions, or profiling unless explicitly authorized in the new conversation. End with the specific visual checks the user should repeat.
