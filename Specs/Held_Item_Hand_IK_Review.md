# Held Item and Hand IK Review

The implementation has five actionable findings: four P2 issues and one P3 issue. The main throw transaction and action replication follow the spec, but the cases below need attention before treating the feature as complete.

Scope: the non-Markdown working-tree changes and new implementation files on `main`, compared with `HEAD` (`65f3297`) and `Held_Item_Hand_IK_Spec.md`. The branch currently has no separate committed IK diff. This is a source review; no builds, automated tests, Play Mode sessions, or profiling were run.

The retained camera-relative owner item, approximate skeleton-free release pose, initial grip tuning, generated wrist-to-follow relationship, and the narrowly scoped 85% IK reach clamp are treated as intentional design choices. They are not findings merely because they trade exact visual agreement for a smaller implementation.

## Findings

### 1. [P2] Carrying a player reinstalls the item pose after explicitly clearing it

**Locations:** `Assets/Game/Runtime/Player/PlayerHeldItemPresentation.cs:36`, `:155`, `:388`; `Assets/Game/Runtime/Inventory/PlayerInventory.cs:51`; `Assets/Game/Runtime/Player/PlayerNetworkState.cs:48`.

`ContextChanged()` correctly treats `!networkState.CanCharge` as a reason to reset the item action and clear its hand target. However, neither `CanShowHeldItem` nor the early guard in `RefreshPose()` applies that condition. `CarryRole.Carrying` makes `CanCharge` false while leaving `inventory.CanEquip` true: inventory permissions only exclude the player being carried.

Consequently, picking up another player while an item remains selected clears the target briefly, then the next pose update installs the holding target again. The held item also remains eligible for display. The incompatible carry context therefore does not relinquish item pose ownership as the cleanup code and spec require. This concerns the item overlay; it does not require changing carried-player throw behavior.

**Suggested correction:** use the same item-presentation permission when selecting a held pose, installing its target, and deciding held visibility. Keep inventory selection and carried-player input routing separate from that permission.

**Visual check:** equip a throwable, pick up another player, then drop or throw that player. Observe from another client that item IK relinquishes the hand during carrying and normal item presentation resumes afterward without restoring an old recovery.

### 2. [P2] The followed projectile position still contains rotation-driven movement

**Locations:** `Assets/Game/Runtime/Player/PlayerHeldItemPresentation.cs:438`; `Assets/Game/Runtime/Items/WorldItem.cs:53`, `:511`; `Assets/Game/Prefabs/Items/Rock.prefab:228`.

Recovery freezes `followOffset`, but adds it to `projectile.PresentedOrigin`, which is `visualRoot.position`. For the existing rock, the sphere collider is offset from the item origin. The existing cosmetic motion path positions that origin at `center - cosmeticRotation * bodySphereCenter`. Thus, even with the center held stationary, spinning the projectile moves the position consumed by hand recovery.

The frozen grip offset prevents the hand from following item orientation, but does not remove this rotational component of its position. On observers, rock spin can make the hand trace an arc around the moving center and can change when the reach limit ends following. The rock's collider offset is about 0.0855 m at its prefab scale, so this is relevant relative to the avatars' roughly half-metre arms.

**Suggested correction:** follow a translation anchor independent of projectile rotation, using the existing sphere-center motion for centered-motion items. Cache the hand-to-anchor offset at release, including placement correction, and preserve it through follow. Keep cosmetic rotation in item presentation.

**Visual check:** watch a weak rock throw from another client with enough follow time to see its spin. Compare different initial spins: wrist orientation should remain body-relative, and hand position should follow departure without orbiting the rock's center.

### 3. [P2] Throw clearance can retain the item's original scene scale after the item has returned to prefab scale

**Locations:** `Assets/Game/Runtime/Items/WorldItem.cs:110`, `:163`, `:227`; `Assets/Game/Runtime/Player/PlayerHeldItemPresentation.cs:338`; `Assets/Game/Editor/ItemPlacementZoneEditor.cs:90`.

`Awake()` seeds `DropDiameter` from the instantiated collider bounds. The new initialization then takes the maximum of that value and the envelope calculated at the definition's world scale. A larger scene instance therefore retains the larger diameter indefinitely, even though held presentation and subsequent releases use `defaultScale` from the prefab.

The existing placement tool supports randomized instance scales. After collecting a larger instance, its throw clearance can reject placements that another item of the same definition accepts, despite both items now having identical released geometry. Pool reuse also retains this cached value. The existing scene-bounds estimate predates this work; using it as the new throw rejection radius introduces the gameplay consequence.

**Suggested correction:** cache a separate release envelope calculated solely at the scale actually applied to released items. Leave the existing scene-sized diameter available to its previous consumers if needed. A conservative envelope is reasonable, but it should not depend on a discarded placement scale.

**Visual check:** collect ordinary and enlarged instances of the same throwable, then attempt throws from the same position beside a wall or under a ceiling. Once their held/released sizes match, clearance decisions should match too.

### 4. [P2] A projectile already picked up before tracking starts waits out the follow maximum

**Locations:** `Assets/Game/Runtime/Player/PlayerHeldItemPresentation.cs:208`, `:435`, `:443`; `Assets/Game/Runtime/Items/WorldItemRegistry.cs:438`.

`FindProjectile()` only marks a failed match as unavailable when the registry record is `Removed`. If an observer receives the recovery state with the projectile already held by another player, or unavailable through a predicted pickup, tracking never starts and `releaseUnavailable` remains false. `Recover()` then keeps the reconstructed release pose until `MaximumFollowDuration` expires.

This differs from losing a projectile after tracking has begun, which correctly ends follow through `!Matches(projectile)`. It also violates the late-observer requirement to skip inappropriate follow time when the physical release is already over. The owner's eventual Idle update prevents a permanent visibility lock, but the observer can show a stale extended pose or snap directly to the newer state.

**Suggested correction:** distinguish a release whose lifecycle has not arrived yet from one whose lifecycle has already ended or been superseded. End/skip follow for the latter, while preserving immediate suppression and waiting correctly for an original still-held item whose release message is pending.

**Visual check:** begin observing a recovering player after someone else has picked up the projectile, and repeat with pickup prediction on the observing client. The hand should advance to the remaining recovery stage instead of waiting for a projectile that cannot match.

### 5. [P3] Cancellation can use another item's return duration after a recovery-time selection change

**Locations:** `Assets/Game/Runtime/Player/PlayerHeldItemPresentation.cs:187`, `:236`, `:297`, `:359`.

Selection changes during recovery correctly avoid restarting the recovery, but also bypass updating `blendDuration`. When recovery finishes, the selected item's hold pose is installed without updating that cached duration. A later charge cancellation sets `actionDefinition` to null for the Idle snapshot before choosing the blend duration, so it falls back to the stale `blendDuration`.

For example, throw item A, select item B during recovery, then charge and cancel B. If their `ReturnBlendDuration` values differ, B's cancellation uses the old cached duration instead of B's setting. The identical current asset defaults conceal this inconsistency until per-item tuning begins.

**Suggested correction:** retain the cancelled action's definition long enough to obtain its return duration, or explicitly select the appropriate current item's duration for cancellation. Continue using A's cached action settings for A's still-active recovery.

**Visual check:** give two items noticeably different return durations. Switch from A to B during A's recovery, then charge and cancel B. A's recovery should retain A's duration; B's cancellation should use B's duration.

## Transaction, networking, and performance assessment

- Owner recovery starts after assigning the existing inventory operation ID and before predicted removal/rebuilding can display a replacement. Server acceptance publishes the recovery action before release and equipment lifecycle notifications. Drops retain their separate path.
- Both held attachment entry points reach the same suppression gate. Successful release is explicit, pickup cooldown only starts after a submitted release, and rejection cancellation is associated with the matching world ID and operation.
- Charge progress, target transforms, IK weights, and intermediate recovery stages stay local. A normal successful throw has Charging, Recovering, and Idle action transitions; owner recovery metadata travels with the inventory release request. There is no new continuous arm-state stream or per-frame RPC in these paths.
- The controller caches its principal references and uses binding/action/selection events. Target updates run before the manual IK evaluation, and bound items move with the solved bone. No obvious unbounded allocation or network-send loop was identified in the added per-frame path.
- `EquippedPresentation`, `RefreshHolder`, and `DetachHolder` scan the whole item dictionary. These are event-driven, so they are not an immediate per-frame regression, but their cost depends on total world items rather than a player's inventory. Large baselines with many held records can multiply those scans. This is a scaling consideration, not a measured performance failure.
- Clearance performs a bounded set of physics queries on release attempts. Runtime profiling would be needed to quantify its cost; the source review does not establish a frame-time or bandwidth guarantee.

## Additional visual acceptance checks

Run a host/client session with rocks, mushrooms, and basketballs. Check rapid taps, partial/full charges, looking sharply up/down, moving and permitted seated throws, blocked releases, dropping a hidden replacement, changing slots during return, throwing the last item, and holding use across recovery. Each successful throw should launch once and require a fresh press for the next use.

Also swap avatars while holding, charging, and recovering; join while another player is charging or recovering; and use a nonzero end-pose pause while moving and turning. Check that the item survives rebinding, replacement visibility follows the owner's action, and the hand stays visibly posed through the pause.
