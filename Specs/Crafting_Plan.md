# Crafting and Potions Implementation Plan

Implement [Crafting_Spec.md](Crafting_Spec.md) in Unity 6000.5.7f1 with FishNet 4.7.3, the New Input System, and UI Toolkit. Read the root [AGENTS.md](AGENTS.md) before implementation. This document supplies the implementation order, ownership rules, integration points, asset work, and user acceptance scenarios; the specification remains the source of gameplay requirements.

## Scope and implementation decisions

Deliver the shared three-slot cauldron, exact and override recipes, three health potion definitions, the bouncy potion, thrown and direct activation, player effect receivers, cart-mounted zones, predicted rebounds, crafting UI, buff HUD, and the reusable Potion Setup extension.

Health application and failed-brew damage end at short TODO methods. Do not change `PlayerNetworkState.Health`, health networking, the health HUD, death, or respawning. Failed-brew knockback and bouncy movement must function.

Use these decisions where the specification leaves implementation details open:

- Extend `WorldItemRegistry` with focused partial files for crafting and effects. It remains the owner of item identities, lifecycle ordering, feature events, and the join baseline. Do not introduce another inventory or item registry.
- Give each cauldron a host-owned FishNet `NetworkObject` and a `Cauldron` behaviour. Use its `ObjectId` in feature messages. Items, insertion visuals, potion areas, and hovering outputs do not get individual `NetworkObject`s.
- Store recipes in one `CraftingRecipeBook` asset with ordered override entries and exact entries. Each entry has ingredient-definition/count requirements and an ordinary `ItemDefinition` output. Separate recipe assets per row are unnecessary.
- Use the existing `Mushroom_Amanita.asset` for the initial mushroom recipes and `Basketball.asset` for the override. Assign references in assets; do not encode their numeric item IDs in matching code.
- Preserve existing equipment permissions. Seated potion use means a passenger who can equip an item; this feature does not enable equipment for the driver. Being carried and carrying another player both exclude cauldron actions.
- Keep existing item throwing and pickup prediction. Predict reversible insertion/direct-use presentation locally; the host commits irreversible consumption. Movement following an applied bouncy dose remains predicted.
- Use one brief, configurable result-presentation interval for failure and disposal, initially 0.5 seconds. This is an implementation default for the spec's unspecified brief interval. The specified insertion, brewing, and output-rise defaults remain 0.6, 1.5, and 0.5 seconds.
- Leave supplied VFX references assignable and optional. Missing particles must not prevent gameplay or leave a cauldron locked. The user supplies VFX, chooses final potion colors, and positions the display for the future sign.

Notify the user before implementing the prefab/component/asset work listed below. Apply changes to prefabs through Unity, minimizing scene edits. Let Unity generate `.meta` files. Do not add a migration editor tool or run tests, builds, Play Mode, screenshots, or agent-driven validation unless the user explicitly requests validation.

## Code map

Paths in this table are under `Assets/Game/` unless stated otherwise.

| Existing integration | Required change |
| --- | --- |
| `Runtime/Items/ItemDefinition.cs` | Unseal the definition; retain inherited fields and `ContentChanged` behavior. |
| `Runtime/Items/ItemRegistry.cs` | Continue resolving all definitions by byte ID; the base-type array already accommodates derived definitions. |
| `Runtime/Items/WorldItemRegistry.cs` | Extend `BeginWorld`, baseline send/receive, lifecycle application, pooling, `SetHeld`, `PredictRelease`, `Release`, rollback, and `EndWorld`. |
| `Runtime/Items/WorldItemRegistry.Motion.cs` | Exclude attached outputs from motion simulation; reuse simulator ownership and disconnect takeover for potions. |
| `Runtime/Items/WorldItemMessages.cs` | Extend lifecycle state for armed releases and cauldron outputs; add compact feature records/messages in a nearby file. |
| `Runtime/Items/WorldItem.cs` | Definition initialization, layer/collider handling, collision dispatch, pickup availability, and output presentation. |
| `Runtime/Inventory/PlayerInventory.cs` | Extend `InventoryRequest`, `Commit`, `RebuildView`, acknowledgements, and cancellation for single-item consumption and cauldron actions. |
| `Runtime/Player/PlayerEquipment.cs`, `Runtime/Items/ThrowableItemUse.cs` | Direct use through the equipped definition; preserve charged throwing. |
| `Runtime/Player/PlayerInteraction.cs`, `IInteractable.cs` | Optional secondary action using the same target, range, obstruction, and input context. |
| `Runtime/Player/PlayerInputReader.cs`, `InputBindings.cs`, root `Assets/InputSystem_Actions.inputactions` | Direct-use and secondary-interaction actions, default bindings, rebinding, and context suppression. |
| `Runtime/Player/PlayerMotor.cs` | Integrate temporary bounce state into `ReplicateMove`, `CreateReconcile`, and `ReconcileState`. |
| `Runtime/Player/PlayerSeating.cs`, `PlayerCarry.cs`, `PlayerControlTransition.cs` | Current physical attachment poses, bounce interruption, and explicit out-of-world effect reset. |
| `Runtime/Vehicles/GolfCartNetwork.cs`, `GolfCartPresentation.cs` | Reuse cart identity, `Controller.Body`, `VisualPose`, and cart lifetime. |
| `Runtime/UI/InteractionTooltip.cs`, `HudController.cs`, `UI/Hud.uxml`, `UI/Hud.uss` | Two action prompts and local timed-buff display. |
| `Editor/ItemSetup.cs`, `ItemDefinitionEditor.cs`, `IconCaptureWindow.cs` | Shared setup mode, inherited Inspector, definition-aware preview, and distinct icon paths. |
| `Runtime/Pickup/PickupRegistry.cs` | Preserve its existing entry into `WorldItemRegistry.BeginWorld`, `JoinWorld`, and `EndWorld`; feature lifecycle belongs inside those calls. |

Add focused files with these responsibilities; helper records can live beside their consumers rather than becoming separate frameworks:

| Proposed file | Responsibility |
| --- | --- |
| `Runtime/Items/PotionDefinition.cs` | Effect type, application mode, per-definition tuning and presentation references. |
| `Runtime/Items/PotionPresentation.cs` | Definition-driven, per-instance appearance shared by runtime and icon preview. |
| `Runtime/Items/PotionContactSensor.cs` | Receiver contact reporting from the bottle's isolated trigger. |
| `Runtime/Items/WorldItemRegistry.Crafting.cs` | Crafted IDs, cauldron registration/state, host crafting transitions, and output association. |
| `Runtime/Items/WorldItemRegistry.Effects.cs` | Accepted activations, active-area lifetime, contact arbitration, and feature baseline integration. |
| `Runtime/Items/CraftingMessages.cs` | Cauldron, activation, and current-effect records and broadcasts. |
| `Runtime/Crafting/CraftingRecipeBook.cs` | Authored recipe entries and multiset resolution. |
| `Runtime/Crafting/Cauldron.cs` | Serialized anchors/settings, availability, network registration, and interaction entry points. |
| `Runtime/Crafting/CauldronIntake.cs` | Intake-trigger callbacks forwarded to the shared transition path. |
| `Runtime/Crafting/CauldronPresentation.cs` | Curved insertion visuals, contents panels, phase VFX, and output animation. |
| `Runtime/Effects/PlayerEffectReceiver.cs` | Dedicated collider, player identity, eligibility, and membership cleanup notifications. |
| `Runtime/Effects/PotionArea.cs` | Common pulse/zone placement, overlap membership, expiry, and cart-follow presentation. |
| `Runtime/Player/PlayerPotionEffects.cs` | Recipient applications, strongest healing rate, TODO hooks, and replicated timed-buff doses. |
| `Runtime/Player/PlayerMotor.Bouncy.cs` | Bounce simulation and dose history used by predicted movement. |

## 1. Define potion and recipe data

1. Make `ItemDefinition` inheritable without replacing its identity, inventory, physics, hold, or throw settings. If derived validation is needed, make base `OnValidate` callable through a protected virtual method and retain its clamping and content-change notification. Do not add a second item identity.
2. Add `PotionDefinition : ItemDefinition` with only the two required effects, Health and Bouncy, and Pulse/Zone application modes. Include radius, zone lifetime, buff duration, health strength/rate, initial jump multiplier, rebound retention, minimum rebound multiplier, color, and supplied impact/cloud VFX references. Keep applicable fields on each definition, including all health-tier radius/lifetime/rate values.
3. New potion definitions start stackable with `MaxStack = 5` and `DontPushPlayer = true`. Apply these as creation defaults, including newly created derived assets, without overwriting later Inspector edits. Health tiers are separate definitions and IDs.
4. Add `PotionPresentation` with authored renderer/material-slot/color-property targets. Cache targets and shader property IDs; apply color using per-instance property blocks. Reapply on every `WorldItem.Initialize`, including reuse from the pool. Expose the same definition-application method to icon capture without requiring a live registry or network session. Do not modify shared source materials.
5. Define recipe entries as requirement pairs (`ItemDefinition`, quantity), output (`ItemDefinition`), and minimum total ingredient count for overrides. The maximum mixture size remains three in the cauldron, not a generalized crafting-capacity system.
6. Resolve the accepted definition IDs as a multiset. Evaluate override entries in array order, requiring every quantity and the minimum total. Then compare exact entries against the whole multiset, including total count. An unmatched nonempty mixture fails; empty mixtures never enter recipe resolution.

Author this data in step 10:

| Recipe | Kind | Requirements | Minimum total | Result |
| --- | --- | --- | --- | --- |
| Small health | Exact | Mushroom × 1 | — | Small health potion |
| Medium health | Exact | Mushroom × 2 | — | Medium health potion |
| Large health | Exact | Mushroom × 3 | — | Large health potion |
| Bouncy | First override | Basketball × 1 | 2 | Bouncy potion |

Two basketballs satisfy the override. Mushroom plus unrelated ingredients fails unless the basketball override or another authored recipe matches.

## 2. Establish shared transition and replication contracts

Implement these contracts before individual activation or cauldron call sites can consume items.

### Identity and ordering

- Preserve the world epoch, world-item ID, lifecycle `Revision`, motion `Sequence`/`Path`, inventory operation number, and player `ControlRevision`.
- Thread the existing `ItemReleaseIntent` through `PredictRelease` and accepted `Release`; it currently stops at the inventory request. Store armed state in the accepted item record. Only a thrown potion arms; ordinary drops and departure drops remain unarmed. `SetHeld`, consumption, removal, and pooling clear local contact/arming state.
- Identify a release using `(epoch, world ID, releaser player ID, inventory operation)`. Keep motion revision as an additional lifecycle barrier, not the sole release identity: simulator takeover can change revision without constituting a new throw.
- Include the expected item state/release in pickup, insertion, and activation requests where needed. A request for an earlier release must not collect or activate the same item after a catch/rethrow. The first valid transition accepted by the host wins; later requests fail without effects.
- Identify direct use by `(epoch, player ID, inventory operation)`. Give accepted effects a compact host-allocated effect ID and retain the source key to merge local predictions and suppress echoes.
- Use an incrementing cauldron revision and phase start/deadline ticks. Brew/dispose requests name the expected revision so two empty-hand requests cannot resolve the same mixture twice.
- Derive gameplay seconds and remaining lifetimes from network ticks and `TickDelta`. Keep a real, non-replay clock for area scheduling and presentation. Historical motor replay uses historical simulation ticks, not current wall time.

### Host commits

Add narrow registry methods for admission, activation, crafted creation, and output collection. Route held operations through `PlayerInventory.Commit` so inventory mutation and registry mutation occur in one synchronous host transaction. Check all preconditions before mutation; do not consume an ingredient and then discover that the cauldron is full.

Publish one feature transition containing its associated item lifecycle change(s), cauldron state, and/or effect creation as applicable. Reuse the lifecycle application function from ordinary item broadcasts. An insertion or direct-use transition must not independently send a redundant removal event that competes with its feature event. The inventory acknowledgement remains necessary to resolve predicted slots.

Apply a transition's item tombstone and associated feature state together before raising local presentation callbacks. Capture any source visual needed for insertion before pooling the item. On host, apply the accepted transition once locally and ignore its network echo.

Register reliable client contact reports through the existing registry/network-manager broadcast path. Reports from a locally predicted release must be ordered after its release operation; if transport delivery crosses those paths, hold only reports for that exact pending release until acceptance or rejection. Never reinterpret an early/stale report as belonging to the next release. Clear pending reports on terminal item state, rejection, disconnect, or world end.

### Crafted-item allocation and output state

Add a host allocator initialized above the largest baked item ID in `BeginWorld`. Allocate monotonically without reusing IDs during the epoch; removed records remain tombstones. Rent/initialize the configured world prefab and publish its ordinary definition ID. All recipe outputs go through this path.

Add `CauldronOutput` to `WorldItemState` and a cauldron association in the item record. This explicit state excludes the result from ordinary physics, intake, armed contacts, motion transmission, and kill-volume removal. It still uses the ordinary inventory pickup path once ready. Do not instantiate a second decorative copy of the result.

Output collection commits only after inventory space is found and the cauldron still owns that ready output ID. Add the ID to one inventory slot, transition it to Held, clear its association, and empty the cauldron in the same accepted transition. Failed or rejected pickup preserves the output and lock. Extend rejection handling to restore an optimistic pickup to its anchored output pose.

### Baselines and teardown

Extend the existing item-baseline handshake with feature snapshots before `ItemBaselineComplete`:

| Current state | Snapshot data |
| --- | --- |
| Cauldron | Object ID, revision, phase, accepted slots in insertion order, current insertion start poses/times, phase deadline, pending result/output ID. |
| Output | Ordinary item ID/definition plus `CauldronOutput` association; cauldron phase supplies rise/ready timing. |
| Live area | Effect ID, definition ID, original start/expiry ticks, stationary position or cart object ID/local impact offset. |
| Active buff | Recipient object ID/lifetime, dose revision, definition ID, expiry, and simulation scheduling data. |

Do not include completed pulses or blasts as applications. A snapshot reconstructs current state without replaying historical arrival, success, damage, or buff-dose events. A live zone seeds current overlaps; a reconstructed buff retains its remaining time rather than receiving a fresh duration.

Buffer state that references a cauldron, cart, player, or output whose network/lifecycle object has not arrived yet. Resolve it on registration/spawn, not through repeated scene searches. Do not show an unassociated output as an ordinary loose item while waiting. Initial baseline application must not overwrite newer revisions received during joining.

Expire/destroy local areas, clear feature dictionaries, cancel scheduled work, remove pending references/predictions, and reset allocators in `EndWorld`. Cauldron destruction retires any associated output; cart despawn removes its attached areas. Use epoch/lifetime checks so delayed messages cannot bind to reused network IDs in another session.

## 3. Extend inventory, equipment, and input

1. Add inventory operations for insert-one, direct-use-one, brew, and dispose. Insert/direct use identify exactly the selected stack's first world ID, expected holder/selection, and current control revision. Brew/dispose carry the target cauldron ID/revision and require truly empty hands, including no carried avatar.
2. Share the single-item consumption mechanics inside the inventory/registry boundary. Remove the ID from `serverSlots` on acceptance and from predicted `viewSlots` while pending. Preserve `confirmedSlots`, acknowledgements, and rollback. Do not decrement only a count or call `registry.Remove` on a held item without removing its stack ID.
3. Add pending-consumption presentation handling, separate from a physical release. Cancel any charge first; show the next item in a remaining stack. Ensure `RefreshHeldPresentation` and older acknowledgements cannot redisplay an ID that has an accepted Removed tombstone. Rejection restores the last accepted state without creating a replacement ID.
4. Add `Player/DirectUse` with right mouse and gamepad left trigger, and `Player/SecondaryInteract` with R and D-pad right. Register both in `InputBindings` so rebinding and actual-device prompts work. Retain `Player/Use` for left mouse/right-trigger charged throws and `Player/Interact` for E/gamepad north.
5. Route direct-use press through `PlayerEquipment`, its cached inventory, and the equipped `PotionDefinition`. Respect `ReadyForUse`, `CanEquip`, carry/seat transitions, gameplay panels, ownership, and input suppression. Resolve a simultaneous direct-use/throw input once: cancel the charge and submit direct use, then ignore that throw release.
6. Add the same context-change and held-button suppression used by existing actions. Closing inventory, exiting a menu, rebinding, changing seats, or regaining control while a trigger is held must not consume a potion or dispose a mixture automatically.
7. Compute direct-use origin at the player's physical feet, using the existing player capsule dimensions/physical seat pose rather than the camera or visual smoothing pose. Submit that position once. Never attach direct use to a cart.
8. Extend `IInteractable` with optional secondary-action members with defaults, preserving all existing implementations. `PlayerInteraction` resolves/caches both actions for the current target, dispatches the gated primary or secondary edge, and rechecks availability after an action. Keep targeting the cauldron's ordinary solid collider; the intake trigger is not a new aiming system.
9. Extend `InteractionTooltip` with a second prompt row driven by `InputPrompt` and `InputPresentation.Changed`. Show both Brew and Dispose only when both are available, and one Insert prompt while an insertable item is equipped. Keep binding glyphs out of gameplay strings.

## 4. Implement the cauldron lifecycle

Use the following phases. Insertion is represented by per-slot arrival deadlines within Occupied; it does not block admission to other free slots.

| Phase | Admission | Player actions | Exit |
| --- | --- | --- | --- |
| Empty | Up to three accepted ingredients | Insert when holding an item | First accepted ingredient → Occupied. |
| Occupied | Remaining slots | Insert; Brew/Dispose only with empty hands and all arrivals complete | Brew → Brewing; Dispose → Disposing. |
| Brewing | No | None | Resolve after 1.5 seconds. |
| Rising | No | None | Output reaches anchor after 0.5 seconds → Ready. |
| Ready | No | Ordinary output pickup | Successful pickup → Empty. |
| Failed / Disposing | No | None | Brief result interval finishes → Empty. |

1. Serialize the recipe book, intake/interaction colliders, curved-path control anchors, output/blast anchors, insertion/brew/rise/result timings, failed-blast settings, and VFX references on the prefab components.
2. Accept both intake reports and held insert requests through the same host admission method. Intake accepts world-state items, including dropped/thrown potions; held admission requires the requesting player's selected inventory ID. Reject removed items, outputs, carried avatars, busy cauldrons, and excess capacity.
3. Reserve a slot and permanently consume its individual item in the accepted transaction. Store its definition, insertion order, source pose, start tick, and arrival deadline. Immediately update its icon. Other slots remain available during the animation.
4. Animate only a temporary visual from the actual source presentation through the authored high control point(s) into the cauldron. Reuse the source visual hierarchy/definition appearance without active item scripts, colliders, Rigidbody, or pickup identity. Do not keep the consumed item alive as an interactable world object. On a joining client, recreate only the unfinished portion from the snapshot.
5. Drive settlement from the host deadline, never a particle callback or a client's frame count. Play arrival VFX once per accepted slot transition on clients present for that arrival. Dispose of the temporary visual on completion, rejection, disable, or teardown.
6. Brew snapshots the complete settled mixture, resolves its recipe once, clears icons at start, and locks actions. Keep occupied contents active throughout insertion/occupation/brewing. At success create exactly one output record, switch off contents presentation, and animate the actual output to its anchor. Ready gets separate VFX.
7. On failure emit one blast event keyed by cauldron revision, play failure smoke, and enter the brief Failed phase. Clear the mixture permanently and unlock at the result deadline. Empty brew requests are rejected before recipe resolution.
8. Disposal clears icons and settled contents, plays its brief effect, and unlocks at its deadline. It never calls the blast or damage path and never refunds items.
9. Use a local prediction keyed by the requesting inventory operation for insertion animation and input feedback. An accepted transition adopts it rather than spawning another visual; rejection removes it and restores accepted state. Shared icons and slot capacity represent accepted admissions. Do not predict a second permanent output.

## 5. Author isolated receivers and potion contacts

### Layers and physical roles

Add layers named `PlayerEffectReceiver` and `PotionEffect` in unused slots. Configure the collision matrix so each interacts only with the other, with self-collision disabled. Preserve other layer pairs.

Put a dedicated trigger collider and `PlayerEffectReceiver` on a child of the player root, outside `ItemHitbox` and the visual/graphics hierarchy. It shares `PlayerMotor.Body`; do not add another Rigidbody, `OfflineRigidbody`, or `NetworkObject`. Cache the player's identity, motor, seating, carry, and effects component at initialization.

The existing item hitbox is detached in `PlayerInventory.OnStartNetwork` and suspended during seating/carrying. The new receiver must not follow that lifetime. Seating/carrying suspend the movement capsule and item-hitbox collider only. Receiver eligibility depends on the receiver component/collider being explicitly enabled and its player still existing, not on `Motor.Suspended`, recovery, or movement mode.

Potion bottles keep a solid sphere collider on `ItemWorld` for ordinary physics and the existing shove path. Add a separate child sphere trigger on `PotionEffect` with `PotionContactSensor`. In `WorldItem.Awake`/sphere caching, select the non-trigger physical sphere explicitly; adding a child trigger must not change which sphere drives release clearance or shove calculations.

Preserve sensor layers when `WorldItem.SetLayer` traverses children. Disable the sensor while held, pooled, removed, or attached as output, and restore it only for an armed world release. Physical-collider settings and material assignment must not turn the sensor into a solid collider.

Enable missing environment contacts on the potion's physical collider using per-collider layer overrides, without globally changing ordinary `ItemWorld` physics. Include solid Ground/Environment/cart contacts as appropriate and leave the isolated effect trigger pair unchanged. Unity's per-collider override mechanism is documented in [Collider.includeLayers](https://docs.unity3d.com/6000.5/Documentation/ScriptReference/Collider-includeLayers.html).

### Contact reporting and arbitration

1. Reuse owner simulation for thrown potions through the existing registry simulator-motion path, extending its current rock-specific selection only to potion definitions. Ordinary items retain their current behavior. Disconnect takeover preserves armed state and the same release identity.
2. Add potion collision dispatch before the existing shove-specific early returns in `WorldItem.OnCollisionEnter`. Gate by armed release, live item state, simulation/reporting role, thrower grace, and non-replay execution. Activation must not depend on `ContactEligible`, `DontPushPlayer`, impulse strength, minimum impact speed, or a `PlayerItemHitbox` existing.
3. Sensor contacts identify enabled receivers, including seated/carried players. Add potion-only swept receiver queries between real physics poses where needed to avoid a fast sensor passing through a player without a trigger callback. Rebase samples on lifecycle/correction discontinuities; do not interpret a teleport or held pose as a flight segment. Never scan every player's position each frame.
4. Extend thrower grace/separation to the effect receiver as well as the ordinary hitbox. Being unarmed/held is independently ineligible for activation. Reset ignored contacts on catch, reuse, and release changes.
5. Queue intake and impact candidates until the real physics step completes. For a bottle that entered an available intake, attempt admission before a contact from that same step; do not depend on Unity's callback order. Include the crossed intake identity in client reports when applicable. Full/busy intake rejection leaves the bottle armed; only an actual solid/receiver contact may then activate it.
6. Host arbitration rechecks current state/release and consumes the potion exactly once. Pickup/activation races retain first-valid-host-transition semantics. An accepted catch sets Held/disarmed; a rethrow has a new operation key. Duplicate and delayed contacts have no effects.
7. Preserve optional shove alongside activation. Cache/submit an eligible ordinary player impact before pooling the bottle so consumption cannot erase it. Use the existing victim-owner shove path once, without reapplying it on the host or tying activation eligibility to it.
8. Determine cart attachment from the actual solid collision target. A passenger's receiver is a player hit. A nearby/overlapping cart is insufficient. Capture cart ObjectId and impact offset in the physical cart's local coordinates only for a direct solid cart hit.

## 6. Implement shared areas and recipient application

All accepted thrown/direct activations call the same area-creation path with an effect ID, definition ID, start tick, placement, and expiry. Predict reversible local impact/cloud presentation by the source key, then adopt or remove it when the host resolves the source operation. Do not apply irreversible health/damage hooks from speculative visuals.

`PotionArea` creates a sphere using the definition radius and uses the isolated effect layer for zone membership. Runtime area objects are local representations of accepted records, not separately network-spawned entities. Cache supplied visual references and keep a separate visual child where cart display pose differs from gameplay pose.

### Application ownership

Each player owner applies effects to its own eligible receiver; proxies provide overlap geometry and visuals without running that player's healing/damage hooks. A host player follows the same path once. Trust owner reports for discrete timed-buff applications and let the host store their accepted state. Do not add a second host application for remote-owned recipients or a network message per healing interval.

Discrete application keys include effect ID and recipient lifetime. Deduplicate seeded overlaps and trigger callbacks by player identity. Timed-buff reports carry the source effect, definition, dose revision, and timing, and are reconciled as described in step 7. These ownership rules also apply when the host is the thrower.

### Receiver poses and replay

- Extract the physical attachment-pose update from `PlayerSeating.LateUpdate` and the physical portion of `PlayerCarry.UpdateAttachment` into reusable methods. Refresh attached bodies before real physics queries/overlap seeding as well as maintaining their existing visual updates. This is required because the existing attachment updates run in LateUpdate.
- Update cart-attached gameplay spheres after the cart's current physical pose is available. Seed queries after receiver/body poses have been made current. With `m_AutoSyncTransforms` disabled, synchronize changed transforms at this controlled boundary before immediate queries; do not call `Physics.SyncTransforms` once per receiver or per visual update.
- Suppress new contacts, overlap applications, timers, dose reports, and blast dispatch during FishNet reconciliation/replay. Motor bounce calculations still replay as simulation.
- FishNet's `RigidbodyPauser` temporarily changes `detectCollisions`. Do not treat that as explicit receiver disable/immunity or permanently clear membership from replay callbacks. After restoration, refresh/reseed affected overlaps without replaying pulses or refreshing an existing buff as a new dose.

### Pulse and lingering semantics

For Pulse, perform one receiver-layer sphere overlap at activation, deduplicate recipients, and dispatch once. Its remaining visual lifetime is presentation only; do not leave a gameplay trigger that applies to later entrants. A baseline never replays that query.

For Zone, seed overlaps at creation, then maintain membership from trigger enter/exit. Track actual receiver/collider membership so multiple callbacks cannot duplicate a player. Store its original expiry, remove it at expiry, and unregister every remaining membership. Ordinary potion areas ignore walls.

Use a small scheduled application interval for occupied zones, initially 0.1 seconds, with elapsed gameplay time for rate calculations. Run application work only while eligible occupants exist; stop scheduling empty/expired zones. Membership changes immediately recalculate the recipient's effective rate. Keep the shared pulse/zone dispatcher limited to Health and Bouncy semantics; initial assets select Health/Zone and Bouncy/Pulse.

Receiver disable/despawn notifications remove memberships and scheduled eligibility without clearing an existing buff. Also recheck component/collider/player lifetime at scheduled application time: disabling a collider alone does not guarantee `OnTriggerExit`. Remove stale entries then. On re-enable, seed overlaps with active zone triggers so a stationary player already inside resumes eligibility. During active scheduling, reconcile removed/disabled collider membership without a scan of all players.

### Health-zone aggregation

`PlayerPotionEffects` holds the recipient's current healing memberships keyed by effect ID. Select the maximum eligible rate across them; do not sum rates or choose one shared pool for the zone. When a zone ends, an occupant leaves, or eligibility changes, switch immediately to the strongest remaining membership.

Accumulate only the interval actually spent eligible at the selected rate. Settle partial intervals on membership/rate changes, and do not count time outside/disabled or retroactively heal from the zone's creation before entry. Scheduled work calls `ApplyHealing(amount)` with a single short TODO to add player health. `ApplyDamage(amount)` similarly contains only the future decrement TODO. Neither method changes a health value or simulates a health increase.

Healing membership coexists with the timed bouncy buff and does not create a HUD buff timer.

### Cart-mounted zones

Resolve attachment once from cart identity. Set gameplay center to `cart.Controller.Body.position + cart.Controller.Body.rotation * localOffset`. Use `GolfCartNetwork.VisualPose` or its presentation graphics for the visual center. Do not parent both gameplay and VFX to the interpolated graphics transform.

Follow translation/rotation while the area is active, with no extra transform messages. Driver/epoch changes, flips, and recovery teleports do not create a new dose or reset expiry. Refresh overlaps after a recovery teleport rather than waiting on stale memberships. Add a narrow cart lifetime/placement notification if needed; remove attached areas on true despawn. Direct use always has a stationary placement. A pulse on a cart is still applied only once.

## 7. Integrate bouncy into predicted movement

Keep normal jump settings in `GameSettings` unchanged. Add a small bounce-state struct and dose-event history used by the motor; do not implement bouncing through a physics material or repeated world-impact RPCs.

### Dose state and prediction

1. `PlayerPotionEffects` applies a new local dose immediately when its owner receives an accepted pulse/application, updates the local HUD state, and reports the dose to the host. The report is keyed by source effect and recipient/reset lifetime so duplicates cannot refresh the timer twice.
2. Schedule the dose into motor simulation using the owner movement tick and corresponding server timing, following the scheduling pattern used for `WorldImpact`. The host echoes accepted dose state; the owner merges the echo with the already applied dose.
3. Retain accepted dose events over the existing prediction-history horizon. A reconcile to before a dose must replay that dose at its intended movement tick. Do not consult a mutable latest-buff field from every historical replay tick.
4. Include active definition/dose revision, expiry timing, current sequence launch velocity, sequence settings/definition, takeoff/landing state, and last applied dose in `MotorState`. Restore them in `ReconcileState` alongside the body. Apply dose history deterministically; callbacks, VFX, reports, and HUD notifications happen outside replay.
5. Keep buff lifetime on the shared real gameplay timeline while seated/carried, since `PlayerMotor` returns early and does not reconcile while suspended. Replicate current buff/clear state independently through `PlayerPotionEffects`, including join/ownership binding, and synchronize that state into resumed motor simulation.
6. A second dose sets expiry to that dose's configured duration from application time. Do not stack strength or reset the active rebound sequence. Preserve sequence parameters captured at its deliberate launch; any newly selected definition's parameters affect the next fresh sequence.

### Jump and landing algorithm

Use the existing Ground/Environment sphere-cast grounding decision, retaining current air control and horizontal acceleration. Do not add GolfCart to grounding or change ordinary cart-roof behavior.

- If active buff, grounded, not in a sequence, and a fresh `MoveInput.Jump` is allowed, start at `GameSettings.JumpSpeed * InitialMultiplier`. Target that upward velocity; do not add falling velocity to it. Record sequence parameters and require a real subsequent takeoff.
- Receiving a buff alone does nothing to velocity and does not start a sequence for an existing flight.
- During a sequence, consume jump edges without resetting strength or adding another impulse. Holding jump must not queue a jump for the settling landing.
- Record actual takeoff, then recognize a transition back to grounded. On that landing calculate `next = previousLaunch * Retention`. If `next >= GameSettings.JumpSpeed * MinimumMultiplier`, target `next` as the vertical launch velocity and require a new takeoff before accepting another landing. Otherwise stop and require a later fresh jump press.
- Preserve the ordinary jump path when no buff applies. Automatic rebounds must not be prevented by the normal deliberate-jump cooldown, and persistent ground-probe contact must not emit multiple rebounds.
- With normal jump speed 4 m/s and defaults, launch targets must be 8, 4, and 2 m/s, then stop before 1 m/s. These are launch velocities, not height multipliers.

### Interruptions

At buff expiry, clear future rebound eligibility without changing current velocity. On seat entry or becoming carried, cancel the sequence and retain buff expiry; getting free requires a fresh jump. Explicit receiver disable blocks new applications but does not clear this state.

At the existing out-of-world reset, clear both buff and sequence. Carry this explicit reset through the accepted `PlayerControlTransition`/reset lifetime so all clients clear it even when normal motor reconciliation is suspended. Reject delayed pre-reset dose reports. Do not turn ordinary seat exits, carry releases, cart recovery, or generic world impacts into buff resets.

## 8. Implement the failed-brew blast

Emit one accepted blast event from the Failed transition, identified by cauldron ID/revision and activation time. Apply its gameplay once, outside replay; snapshots contain no historical blast application.

Use one receiver-layer sphere query and deduplicate players. For each otherwise eligible receiver, raycast from the authored anchor above the rim to the receiver center. Use solid Ground/Environment/cart geometry (including applicable Default-layer environment), ignore triggers, players, loose items, held items, birds, and the exploding cauldron's own colliders. When ignoring self hits, continue considering other hits on the same ray; a self hit must not hide a wall behind it. Do not reuse a mask that accidentally includes an effect trigger.

The same enabled-receiver and line-of-sight result gates both the future damage hook and knockback:

- Call `ApplyDamage(25)` using the cauldron's editable damage setting; health remains unchanged.
- For recipients neither seated nor carried, calculate `falloff = Clamp01(1 - distance / radius)`. Use a normalized horizontal outward direction (zero at the exact center) and combine `outward * 6 + up * 3`, then multiply by falloff. Defaults are radius 3 m, outward 6 m/s, upward 3 m/s.
- On the eligible player's owner, call `PlayerMotor.SubmitWorldImpact(change, 0.2f)` once, using the existing local prediction/host confirmation. Do not also queue a host-only impact for that player. Preserve `carry.ImpactDrop` behavior in the motor so a knocked-back carrier drops its passenger.
- Seated/carried recipients may reach the damage TODO but stay attached and receive no motor impact.

Disposal does not create a blast event.

## 9. Add crafting UI and extend setup tools

### Fixed ingredient display

Create `Assets/Game/UI/CauldronContents.uxml`, `CauldronContents.uss`, and `CauldronWorldPanel.asset`. Use native world-space UI Toolkit on an independently positionable child of the cauldron. Configure three square image panels in a horizontal row, fixed orientation, visible without targeting, and noninteractive picking. Keep `UI/SharedPanel.asset` as the existing screen-space HUD settings.

Use Unity 6000.5's `PanelRenderer` with a World Space `PanelSettings` asset and the contents visual tree. Bind/cache elements through its UI-reload callback, then update icons on accepted cauldron state changes. Do not assume the older `UIDocument.rootVisualElement` API applies to `PanelRenderer`, add billboarding, or implement a RenderTexture/camera workaround. See the [PanelRenderer API](https://docs.unity3d.com/6000.5/Documentation/ScriptReference/UIElements.PanelRenderer.html) and [world-space setup](https://docs.unity3d.com/6000.5/Documentation/Manual/ui-systems/create-world-space-ui.html).

### Local buff HUD

Add one icon/remaining-seconds element to `Hud.uxml`/`Hud.uss`. Bind `PlayerPotionEffects` in `HudController.Bind`, subscribe/unsubscribe with local-player changes, and hide on expiry/reset/unbind. Use the potion icon and ceiling of remaining seconds. Schedule countdown text refresh only while a buff is active; write text when the displayed second changes. Refresh immediately on a dose. Do not reuse the health bar or show healing zones as buffs.

### Item/Potion Setup

1. Add `Two Birds/Potion Setup` opening the existing `ItemSetup` window with Potion mode selected. Retain `Two Birds/Item Setup` and the existing Project-window creation menus. Use one mode-aware creation path, not a copied window/pipeline.
2. Potion mode accepts item name, effect type, and color. Resolve `Assets/Game/Prefabs/Items/Potion.prefab` as its fixed prepared gameplay prefab. Do not run ordinary `AddComponents` with a definition-specific `BakedPickup.Item` or generate a new prefab per potion.
3. Create the derived definition, choose an actually unused nonzero byte ID, and add it to the existing registry. Replace the current `max + 1` allocation with a bounded search so holes can be used and 255 cannot wrap to zero. Report exhausted IDs before creating an unusable asset.
4. Apply initial effect defaults, reuse `GenerateHeldOffset`, retain icon options, and leave detailed tuning in the Inspector. The shared prefab must not carry a fixed gameplay definition that overrides runtime records. Existing baked placement continues to obtain definitions from `BakedPickup` on actual placed items.
5. Change the custom editor attribute to include derived `ItemDefinition` types. Preserve existing first-person/hand-pose override editing while exposing potion fields.
6. Pass the selected definition into `IconCaptureWindow.Render` and apply its `PotionPresentation` to the temporary instance before `StripNonVisual` removes scripts. Do this for Refresh Preview and Capture & Save, including the setup caller. Add a definition selector for a shared prefab preview; selecting a definition can supply its prefab.
7. Use the definition asset/name for the icon output filename when supplied, with asset-path uniqueness as needed. The current filename uses `target.name`, which would cause all four definitions to overwrite `Potion_icon.png`. Retain ordinary prefab-only capture behavior.
8. Dispose temporary preview objects/resources as the existing capture flow does. Property blocks or any preview-owned materials must survive the render and must not alter shared model/material assets.

## 10. Perform prefab and asset authoring

Use `C:\Users\spenc\AppData\Local\Unity\bin\unity.exe` for Unity authoring where available, with Unity MCP as fallback. Discover command schemas before use; the connected CLI exposes `save_prefab_contents`, component/serialized-field commands, and asset commands. Prefer isolated prefab-content edits over applying unrelated scene overrides. Do not hand-author `.meta` files or add temporary migration scripts.

| Asset | Authoring work |
| --- | --- |
| `Assets/Game/Prefabs/Cauldron.prefab` | Preserve its model; add host-owned NetworkObject, Cauldron, presentation component, suitable body/interaction colliders, intake trigger/relay, insertion control anchors, output anchor, blast anchor above rim, and VFX anchors/references. Add the fixed PanelRenderer child beside the cauldron. |
| `Assets/Game/Prefabs/Items/Potion.prefab` | Prepare the existing model variant as the one shared gameplay prefab. Add a collider-free VisualRoot, Rigidbody, OfflineRigidbody, WorldItem, ThrowableItemUse, PotionPresentation, a fitted solid sphere, and a separately layered receiver sensor. Set physical collider overrides and actual color targets for the supplied model. Preserve model scale while making anchors and world-unit collider sizing consistent. |
| `Assets/Game/Prefabs/Player.prefab` | Add PlayerPotionEffects to the networked player and a receiver child/component/trigger using its existing root Rigidbody. Keep receiver outside the detached item-hitbox subtree. |
| `ProjectSettings/TagManager.asset`, `ProjectSettings/DynamicsManager.asset` | Add the two isolated layers and their single permitted interaction pair through Unity settings. |
| `Assets/Game/ScriptableObjects/Items/SmallHealthPotion.asset` | Health/Zone; radius 3 m, zone 8 s, rate 5/s. |
| `Assets/Game/ScriptableObjects/Items/MediumHealthPotion.asset` | Health/Zone; radius 3 m, zone 8 s, rate 10/s. |
| `Assets/Game/ScriptableObjects/Items/LargeHealthPotion.asset` | Health/Zone; radius 3 m, zone 8 s, rate 15/s. |
| `Assets/Game/ScriptableObjects/Items/BouncyPotion.asset` | Bouncy/Pulse; radius 3 m, buff 10 s, launch multiplier 2, retention 0.5, minimum multiplier 0.5. |
| `Assets/Game/ScriptableObjects/Crafting/CauldronRecipes.asset` | The four recipe entries from step 1, with Basketball override first and ItemDefinition output references. Assign to Cauldron prefab. |
| `Assets/Game/ScriptableObjects/ItemRegistry.asset` | Register all four definitions with distinct available IDs. Each uses the same Potion prefab, max stack 5, and default DontPushPlayer enabled. |
| `Assets/Game/UI/Icons/` | Four distinct captured icons matching definition appearance. |
| `Assets/Game/UI/CauldronContents.uxml`, `CauldronContents.uss`, `CauldronWorldPanel.asset` | Fixed three-panel world display and dedicated native world-space panel settings. |
| `Assets/Game/UI/Hud.uxml`, `Hud.uss` | Secondary action prompt layout and timed-buff icon/countdown. |
| `Assets/InputSystem_Actions.inputactions` | The two actions and default bindings from step 3. |

Apply cauldron behavior to the prefab so its existing scene instance inherits it. Adding a FishNet scene NetworkObject may require saving that scene through Unity to serialize its scene identity; make only that necessary scene change and preserve placement/other overrides. Do not create a duplicate scene cauldron or substitute runtime hierarchy-path hashing for its identity.

No new persistent SessionRoot component is required for registry partial files. Runtime pulse/zone spheres can be constructed by `PotionArea`; no separate effect-network prefab or general effect editor is needed.

Use the extended Potion Setup workflow to author definitions and icons. The user supplies/assigns final VFX and adjusts the display's position for the sign. Assign all gameplay references and timing/layer settings as part of implementation; do not leave required component wiring as an unexplained user task.

## User visual acceptance after implementation

The user performs these checks. Do not run an automated test/validation pass or play the game on their behalf without an explicit request.

1. With two clients, insert held and released items, including one item from a stack, a potion, concurrent third-slot attempts, and another insertion while the first is animating. Confirm three-slot capacity, accepted-order icons, curved arrival, one-time consumption, and intact rejected items. Carrying an avatar must offer no cauldron actions.
2. Brew one/two/three mushrooms; basketball plus another item; two basketballs; basketball plus unrelated extras; and invalid mushroom mixtures. Confirm exact quantities, override priority, and a single shared result. Empty brewing does nothing. Brew/dispose remain unavailable until all arrivals complete.
3. Attempt result collection with a full inventory and simultaneously from both clients. Confirm the output remains anchored and the cauldron locked on failure, and only the winning pickup empties/unlocks it. The output cannot fall into an intake or duplicate itself.
4. Check the fixed display from both clients, move its child for the future sign, and compare empty/occupied/brewing/ready visuals. Confirm the currently assigned keyboard/controller bindings on both empty-hand prompts, including after rebinding and device switching. Disposal has no blast.
5. Compare dropped, thrown, caught, rethrown, and directly used potions. Try available/full/busy cauldrons and ground/wall/player impacts. Confirm one activation, individual stack consumption, thrower grace, rejection recovery, and activation with DontPushPlayer enabled, impulse zero, and low contact speed. Disabling DontPushPlayer should allow the ordinary shove as well.
6. Compare all three health zone sizes/lifetimes, entry/exit, overlapping strengths, and seated/carried recipients. Confirm health/HUD values remain unchanged. Disable the receiver component and its collider separately, then re-enable while already inside a zone; new eligibility should stop/resume while an existing buff expires normally.
7. Apply bouncy while grounded and in an existing jump: neither should launch/start a sequence. Deliberately jump and observe the boosted launch followed by two smaller default rebounds. Press/hold jump during the sequence; it must not reset or add force. After settling, a fresh press can restart while time remains.
8. Refresh bouncy mid-sequence, let it expire airborne, sit/get carried, get free, and fall out of the world. Confirm duration-only refresh, no velocity cut on expiry, sequence cancellation during attachment, fresh-press resumption, reset clearing, and matching local/remote motion and HUD time.
9. Throw a health potion directly at a cart, then drive, rotate, change drivers, flip/recover, and despawn it. Confirm physical overlap follows the cart, visuals follow the displayed impact offset, and expiry never restarts. Passenger impact must not attach; direct use by a seated passenger leaves a stationary zone.
10. Fail brews near/far from the rim, behind walls/carts, while carrying a player, and while seated/carried. Confirm falloff, cover, the carrier dropping its passenger, attached recipients staying attached, no repeated blast, and unchanged health.
11. Join with a mixture still arriving, an occupied cauldron, brewing in progress, an output rising/ready, live stationary/cart zones, and an active buff. Confirm current phase and remaining durations without replayed completed pulses, blasts, or arrival effects. Repeat session teardown/rejoin and a thrower disconnect without stale items/effects.
12. Create/register a potion through Potion Setup and capture its icon. Compare its world, held, pooled/reused, and preview appearance; other potion definitions and icon files must retain their own appearance. Confirm the ordinary Item Setup workflow remains available.
