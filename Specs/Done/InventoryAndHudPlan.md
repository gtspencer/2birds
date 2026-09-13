# Inventory and HUD implementation plan

Status: implemented September 13, 2026 following the user's implementation request. See [InventoryAndHudResults.md](InventoryAndHudResults.md) for evidence and remaining validation.

Implement against [InventoryAndHudSpec.md](InventoryAndHudSpec.md). The spec is the behavior and acceptance authority; this document orders the work. The user confirmed 24 slots arranged as three rows of eight, with the first row as the hotbar, and confirmed that this delivery defines and tests inventory add/drop interfaces while deferring visible world items and pickup interactions.

## 1. Establish data and transaction rules

Create item definition/catalog types and a deterministic fixed-slot inventory model beneath `Assets/Game/Runtime/Inventory`. Use stable definition and entry IDs, canonical empty slots, the documented insertion priority, and move/swap/merge operations. Expose trusted add and all-or-nothing drop boundaries. Keep UI and FishNet concerns out of the model.

Add focused Edit Mode tests for A3-A4 and A6: priority when a hotbar vacancy competes with a partial storage stack, capacity/remainder, unique identity, full/partial merges, invalid input, and drop acceptance/failure. Use in-memory definitions and fake sinks, cleaned up by fixtures. Tests should assert conservation and externally visible outcomes.

Exit: operations are deterministic; failed operations preserve state; quantity and unique identity conservation pass.

## 2. Connect authoritative player state

Add `PlayerInventory` and `PlayerHealth` network components to `Assets/Game/Prefabs/Player.prefab`. Implement owner-readable initial snapshots and revisioned updates, owner-required move/drop commands, result handling, duplicate suppression, and health clamping. Use the existing server/session lifecycle; leave motor prediction and public player descriptors unchanged unless an actual consumer requires a change.

Verify the exact FishNet settings/serialization against installed `SyncVar.cs`, `SyncTypeSetting.cs`, `ReadPermissions.cs`, and `WritePermissions.cs` under `Assets/FishNet/Runtime/Object/Synchronizing`. Allocate new immutable snapshot contents on commit. Do not edit imported FishNet source.

Exit: host and remote owner initialize correctly; other observers receive no private inventory; stale or repeated commands cannot mutate twice. Cover A7-A10 with network fixtures and separate-process validation.

## 3. Build reusable UI and input coordination

Extend `Assets/Game/UI/Session.uxml` with HUD/inventory structure or referenced templates; add scoped USS and a reusable slot element. Add a HUD presenter under `Assets/Game/Runtime/UI` that binds the session's local player, shows health, and shares slots 0-7 between both presentations.

Consolidate modal state/input routing across `SessionController`, `SessionOverlay`, and `PlayerInputReader`. Add persistent inventory toggle bindings and direct 1-8 selection in the existing action asset, resolving Previous/Next conflicts. Preserve session loading/stopping behavior and existing UI action requirements.

Exit: A1-A2, A10-A11 work with empty and fixture-filled inventories; input gating includes spawn/focus and close paths. No duplicate cancel subscribers or per-frame visual-tree queries are introduced.

## 4. Add drag and pending-operation presentation

Implement a runtime pointer manipulator/controller using pointer capture, panel-space target detection, a non-pickable ghost, and five-unit drag threshold. Distinguish slot targets, panel padding/other UI cancellation, and valid viewport drop release. Connect move/drop requests to the network wrapper; keep confirmed contents visible while pending.

Handle capture loss, Escape, panel close, focus loss, updated snapshots, document detach, ownership loss, and disconnect. Show unavailable drop feedback with the default absent provider. Do not add pickup prompts, world objects, item use, or holdable visuals.

Exit: A4-A6 and A8 pass with pointer interaction and fake sinks. No cancellation path loses items or leaves capture/cursor stuck.

## 5. Integrate saved assets and validate delivery

Update `Game.unity` wiring and extend `MvpAssets` generation deliberately so a later intentional regeneration includes the HUD and new player components. Keep its destructive regeneration command explicit. Extend `MvpChecks` or add an inventory-specific check for references, catalog IDs/limits, components, input bindings, and slot counts. No extra enabled scene or spawnable world prefab is needed.

Run the targeted test suite, configuration checks, and a Windows development build. Use Editor plus standalone during iteration; validate authority with two standalone processes. Reuse `Specs/Validation/run-pair.ps1` and its latency tooling where suitable, without assuming its existing movement routes exercise inventory. Add bounded development-only fixture commands if needed; no unrestricted production debug RPCs.

Exercise A1-A13 and record results, including a 100 ms RTT/1% packet-loss pass for repeated move/drop attempts, pending state, grants racing a drag, and disconnect. Confirm final authoritative state converges after delivery resumes and no item is duplicated or silently discarded. Check 1280x720, 1920x1080, and 2560x1440 plus ten session/open-close cycles. Re-run existing movement/session smoke checks because modal input behavior changes.

Remove temporary scene objects, prefabs, catalog assets, injected starting items, and their orphan `.meta` files. Durable test code and fixtures may remain in test assemblies; they must not seed production gameplay. Validate the final saved scene/prefab after cleanup.

Exit: evidence for A1-A13 is recorded, D1-D2 remain explicitly deferred, no test content ships, and build/configuration checks pass.

## Proposed change map

| Location | Intended responsibility |
| --- | --- |
| `Assets/Game/Runtime/Inventory/` | Definitions/catalog, slot/snapshot types, pure inventory rules, add/drop contracts, FishNet wrapper. |
| `Assets/Game/Runtime/Player/PlayerHealth.cs` | Server health state and owner synchronization. |
| `Assets/Game/Runtime/UI/` | HUD presenter, reusable slot view, drag handling, shared modal/input coordination. Keep classes proportional to responsibility. |
| Existing `SessionController`, `SessionOverlay`, `PlayerInputReader` | Local player binding and a single gameplay/cancel decision path. |
| `Assets/Game/UI/` | Inventory/HUD templates and scoped styles sharing existing PanelSettings. |
| Existing input action asset, Player prefab, Game scene | Bindings and saved component/document references. |
| `Assets/Game/Editor/`, `Assets/Game/Tests/` | Deliberate asset generation, configuration validation, focused model/network/UI tests and assembly setup as needed. |

## Unity execution and handoff

Prefer the installed Unity CLI for tests, configuration validation, and builds. The executable exists at `C:\Program Files\Unity\Hub\Editor\6000.5.7f1\Editor\Unity.exe`. With the project available for batch use, the existing non-regenerating commands are:

```powershell
& 'C:\Program Files\Unity\Hub\Editor\6000.5.7f1\Editor\Unity.exe' `
  -batchmode -nographics -quit -projectPath . `
  -executeMethod TwoBirds.Editor.MvpChecks.Run -logFile inventory-checks.log

& 'C:\Program Files\Unity\Hub\Editor\6000.5.7f1\Editor\Unity.exe' `
  -batchmode -nographics -quit -projectPath . `
  -executeMethod TwoBirds.Editor.MvpAssets.Build -logFile inventory-build.log
```

Run newly added tests using Unity Test Framework's CLI for the selected test assemblies, saving XML results and logs. Do not invoke `GenerateAndBuild` just to validate a change. If the active Editor/project lock prevents the CLI operation, use the Unity MCP when available; do not force-close the user's Editor. Pointer and visual checks require a rendered player or Editor session, not a headless success claim.

Record implementation results beside these documents when the work is performed. The implemented plan, spec, and results are archived together in `Specs/Done`. The results retain any unmet acceptance checks explicitly.
