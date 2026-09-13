# Inventory and HUD implementation results

Implemented September 13, 2026 against [the spec](InventoryAndHudSpec.md) and [the plan](InventoryAndHudPlan.md). The feature is implemented and the automated checks below passed. Full A1-A13 acceptance remains pending the interactive and prolonged-outage cases listed below.

## Delivered

- A deterministic 24-slot inventory with the specified insertion order, stack/unique identity rules, move/swap/merge operations, partial grants, detached snapshots, and atomic synchronous drop transactions. Live identities cannot be held by two inventories. Reentrant mutations are rejected during transfer.
- Server-owned `PlayerInventory` and `PlayerHealth` on the saved Player prefab. Inventory and health use server-written, owner-readable FishNet SyncVars. Owner-required reliable requests validate revision and entry identity, suppress completed request IDs, and return typed results with recovery snapshots. One local mutation remains pending until its result and revision are available; after five seconds, the client requests state without replaying the mutation.
- The saved Game scene has one HUD presenter. The closed HUD shares slots 0-7 with the inventory's first row. Health includes text and a bar. Slot views provide names/icons, counts, selection, pending and target styles, and runtime hover/focus text.
- Tab/I toggle inventory; 1-8 select hotbar slots. Conflicting keyboard Previous/Next bindings were removed, retaining controller bindings and existing UI actions. The session controller coordinates gameplay eligibility and cancel precedence. Document detach, focus loss, ownership loss, panel closure, and disconnect cancel local presentation appropriately.
- Captured pointer dragging uses panel coordinates, a five-unit threshold, a non-pickable ghost, and explicit slot/viewport/cancel targets. No world drop sink is installed in ordinary gameplay; unavailable drops retain the item and report that dropping is not available yet.
- `InventoryAssets.Install` updates only the player and game UI wiring. Intentional `MvpAssets.Generate` includes the feature. Configuration checks cover scene scripts, the single HUD, prefab components, catalogs/definitions, bindings, and UXML rows.

No pickup, world-item prefab, holdable, damage/death system, or production starting items were added. Runtime test definitions are created only under the explicit development-build `-inventoryValidation true` switch and are excluded from release builds. The production catalog is intentionally empty.

## Validation evidence

Unity 6000.5.7f1 CLI ran in `.utmp/InventoryValidation`, an isolated copy of Assets, Packages, and ProjectSettings, because the user's Editor held the working project open and no Unity MCP was exposed. The saved prefab/scene and final source changes are present in the working project. Destructive scene regeneration was not used.

| Check | Result and evidence |
| --- | --- |
| Edit Mode tests | **12 passed, 0 failed.** [XML](InventoryAndHudValidation/editmode-tests.xml). Covers insertion priority, capacity and large quantities, moves/swaps/merges, identity preservation, stale/invalid operations, detached snapshots, absent/rejecting/throwing/accepting sinks, reentrancy, and unique transfer between inventories. |
| Configuration and Windows development build | **Passed.** [Build evidence](InventoryAndHudValidation/build.txt). Includes missing-script and duplicate-HUD checks on the saved scene. Final executable: `.utmp/InventoryValidation/Builds/Final/TwoBirds.exe`. |
| Separate-process Host/Join | **Passed** with the normal network profile: 50 ms delay per direction, +/-10 ms jitter, 1% configured loss. [Host](InventoryAndHudValidation/network-host.txt), [join](InventoryAndHudValidation/network-join.txt), [proxy](InventoryAndHudValidation/network-profile.txt). Both peers completed the fixture; actual dropped packets were recorded. |
| Authority and recovery fixture | Verified owner initial state, actual absence of inventory SyncVar payload on a remote observer, nonowner request rejection, client grant/health rejection, merge conservation, duplicate suppression, stale snapshot rejection, unavailable-drop feedback, repeated moves while closing the panel, and a server grant canceling a captured drag. |
| Health | Server clamps/rejects invalid writes. Owners observed 50/100, 0/100, and 25/50 transitions. Normal Solo starts at 100/100. |
| Pointer/modal fixture | Synthetic runtime pointer events exercised capture, a drag beyond the threshold, Escape precedence, panel-close capture release, and grant-driven cancellation. Each fixture also exercised ten modal open/close sequences. |
| Ten complete Solo sessions | **Passed**, including the inventory fixture in all ten. Each exit left one persistent session root and zero players; no exceptions. [Cycle evidence](InventoryAndHudValidation/session-cycles.txt). |
| Rendered layouts | Inspected camera/UI render-texture composites at [1280x720](InventoryAndHudValidation/1280.png), [1920x1080](InventoryAndHudValidation/1920.png), and [2560x1440](InventoryAndHudValidation/2560.png), plus the [empty production HUD](InventoryAndHudValidation/default.png). Eight columns, health, title, close control, and session status remain visible. These are rendered captures, not headless visual claims. |
| Existing movement/session smoke | Host/Join walk route entered and left successfully. [Metrics](InventoryAndHudValidation/movement-summary.json): owner reconciliation error remained zero in this run, with roughly 60 Hz simulation. This is a short regression smoke run, not a replacement for the previous movement validation suite. |

The final explicit health-bar layout adjustment and pointer snapshot allocation reduction were compiled in the final build and rechecked with rendered Solo fixtures. The network and ten-session evidence preceded those two changes; inventory protocol/model and modal behavior were unchanged.

## Remaining acceptance checks

The following are validation gaps, not silently passed criteria:

- **A5/A12:** physical pointer releases onto padding, unrelated UI, the viewport, and outside the application at all three resolutions; capture loss caused by another control; ghost tracking across panel edges. Automated capture/cancellation checks do not cover every release target.
- **A8:** deliberately suppress delivery beyond five seconds to exercise pending-state refresh, then restore delivery; force both snapshot/result arrival orders and disconnect during an outstanding mutation. The normal-loss run exercised reliable delivery and racing grants, but not a prolonged outage.
- **A11:** physical Tab/I/1-8 and controller cancel/navigation, text-field focus suppression, application focus return, and buffered jump/look behavior across every close path. Router and synthetic-pointer checks passed; a full interactive input pass remains.

Thus A3, A4, A6, A9, A10, and the automated portions of the other criteria have evidence; A5, A8, A11, and A12 are not fully accepted. **Deferred D1/D2 remain deferred:** real world pickups and visible/networked world drops require the later pickup/holdables implementation.

## Reproduce

Use Unity CLI with a project available for batch access:

```powershell
& $unity -batchmode -nographics -projectPath $project -runTests -testPlatform EditMode -assemblyNames TwoBirds.Inventory.Tests -testResults inventory-tests.xml -logFile inventory-tests.log
& $unity -batchmode -nographics -quit -projectPath $project -executeMethod TwoBirds.Editor.MvpChecks.Run -logFile inventory-checks.log
& $unity -batchmode -nographics -quit -projectPath $project -executeMethod TwoBirds.Editor.MvpAssets.Build -logFile inventory-build.log
./Specs/Validation/run-pair.ps1 -Executable $exe -Inventory -Profile normal -Route idle -Seconds 18
```

Normal builds start empty. Test grants require both a development build and `-inventoryValidation true`. The test fixture creates no saved assets. `ServerAdd`, `ServerSet`, and `IInventoryDropSink` are the trusted extension boundaries for future gameplay.
