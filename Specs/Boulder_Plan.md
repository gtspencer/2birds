# Boulder and Heavy Hold Implementation Plan

## Objective and scope

Implement `Boulder_Spec.md` using the existing inventory, item actions, hand IK, world-item physics, motion replication, player impacts, and cauldron systems. The result is a non-stackable, collectible boulder with a reusable two-hand Heavy presentation mode.

Heavy is a presentation choice for the single selected item. It does not imply damage, physical mass, simulator ownership, or sleeping collision policy. Those behaviors belong to the item definition. Keep walking, sprinting, jumping, inventory capacity, equipment permissions, and interruption rules unchanged.

Crafting supplies the initial boulders. Modify the designated Boulder prefab in place; do not add scene placements or avatar anchors. Required authoring includes two grip children, standard item components, a definition, an icon, Heavy pose defaults, and one recipe. Use Unity CLI for Unity authoring where supported, with Unity MCP as the fallback. Let Unity create metadata. Apply the asset changes directly without adding a one-time editor setup or migration tool.

This document is the implementation handoff. It does not authorize automated validation or play-mode testing; the user performs the visual acceptance checks at the end.

## Relevant code and integration points

Paths below are relative to the project root.

| Area | Existing implementation | Planned integration |
| --- | --- | --- |
| Item configuration | `Assets/Game/Runtime/Items/ItemDefinition.cs` | Add hold mode and the missing per-item behavior switches. Reuse existing throw, damage, and physics values. |
| Pose configuration and math | `Assets/Game/Runtime/Items/HeldItemSettings.cs`, `HeldItemPose.cs` | Add Heavy defaults and shared item-to-two-palms calculations. Preserve Hand and slingshot calculations. |
| Held state and release | `Assets/Game/Runtime/Player/PlayerHeldItemPresentation.cs` | Extend its existing charge, commit, cancellation, and recovery lifecycle to both hands. |
| Local and remote hands | `Assets/Game/Runtime/Player/PlayerHandPresentation.cs`, `Assets/Game/Runtime/Avatars/LocalFirstPersonHands.cs` | Give Heavy a body-relative local arm frame and preserve the prepare/evaluate/commit pipeline. |
| Hand targets and IK | `Assets/Game/Runtime/Avatars/AvatarHandTargets.cs`, `AvatarHandIK.cs` | Reuse both Item targets, palm calibration, finger layers, and existing reach limits. |
| Inventory and throwing | `Assets/Game/Runtime/Inventory/PlayerInventory.cs`, `Assets/Game/Runtime/Items/ThrowableItemUse.cs`, `ItemReleaseVelocity.cs` | Keep existing charge strength and movement inheritance; replace fixed-radius drop clearance with cached item geometry. |
| World item | `Assets/Game/Runtime/Items/WorldItem.cs` | Cache grips and release geometry, support item-driven attachment, configure sleeping collisions and impacts. |
| Clearance | `Assets/Game/Runtime/Items/ItemReleaseClearance.cs` | Extend existing obstruction correction to an offset sphere and two reach constraints. |
| Contact episodes | `Assets/Game/Runtime/Items/ItemPlayerContact.cs`, `Assets/Game/Runtime/Player/PlayerItemHitbox.cs` | Retain victim-client reporting, contact deduplication, fixed damage, and capped impulse application. |
| Simulation ownership | `Assets/Game/Runtime/Items/WorldItemRegistry.cs`, `WorldItemRegistry.Motion.cs` | Extend simulator selection in both predicted and accepted releases; retain replication and takeover. |
| Recipes and presentation | `Assets/Game/Runtime/Crafting/CraftingRecipeBook.cs`, `CauldronPresentation.cs`, `CauldronIntake.cs`, `Assets/Game/Runtime/Items/WorldItemRegistry.Crafting.cs` | Add recipe data; reuse ordinary insertion, output, and physical intake. |
| Authoring references | `Assets/Game/Editor/ItemDefinitionEditor.cs`, `ItemSetup.cs`, `WorldItemSetup.cs`, `IconCaptureWindow.cs` | Extend inspector presentation where needed; follow the existing component and icon conventions without running broad setup workflows. |

`PlayerEquipment`, `PlayerNetworkState`, and inventory request processing already own use permissions, action snapshots, selection changes, and recovery restrictions. Do not introduce another equipment slot, input action, charge clock, or network action state.

## Implementation decisions

### Definition fields and defaults

Add an `ItemHoldMode` enum with explicit values `Hand = 0` and `Heavy = 1`, and a `HoldMode` field on `ItemDefinition`. Existing assets retain Hand without individual edits.

Add only these missing behavior switches, all defaulting to false:

| Field | Meaning |
| --- | --- |
| `UseSharedImpactThreshold` | Use `MinimumImpactSpeed` as the common directional gate for both damage and shove. Both the item's own incoming speed and relative closing speed must meet it. False preserves the existing separate shove and global damage gates. |
| `CollideWhileSleeping` | Retain player-hitbox and golf-cart collisions when this item sleeps. False preserves the existing sleeping exclusions. |
| `SimulateOnReleasingClient` | Opt this item into the existing releasing-client simulation path. Preserve the existing potion and small-rock selection behavior independently. |

Use the existing `MinimumImpactSpeed`, `OverrideCollisionDamage`, and `CollisionDamage` fields for Boulder; do not add a second boulder speed threshold. A plain `ItemDefinition` is sufficient; no `BoulderDefinition` or boulder-specific use behavior is needed.

### Heavy pose settings

Add `HeavyHoldSettings` to `HeldItemSettings`, using the existing `HeldItemPoseSettings` type and a `HeavyDefault` initializer. Continue using `OverrideHoldSettings` / `HandPose` for per-item timing and pose overrides.

For Heavy, spatial positions describe the held object's reference center relative to the shoulder midpoint in average arm lengths. Spatial Euler values describe object orientation relative to the body, composed with the prefab's authored root rotation. Explain these meanings in the item inspector so the existing palm-specific labels do not mislead authors.

The Heavy default applies in both first and third person. `OverrideFirstPersonPose` may still supply a local spatial override, but its coordinates remain body-relative. Do not use the camera-relative Hand defaults or camera-relative avatar hold offsets for Heavy. Timing still comes from the resolved hold settings.

Initial Heavy values:

| Setting | Initial value |
| --- | --- |
| Hold center | `(0, -0.40, 0.50)` average arm lengths from shoulder midpoint |
| Charge control center | `(0, 0.10, 0.70)` |
| Charged center | `(0, 0.60, 0.25)` |
| Hold / charged object Euler offsets | `(0, 0, 0)` before prefab rotation |
| Charge pose duration | `0.35 s` |
| Follow reach fraction | `0.85` |
| Maximum follow duration | `0.20 s` |
| End pose pause | `0 s` |
| Return blend duration | `0.20 s` |

These spatial values are starting authoring values. Final grip and pose tuning must satisfy both supported avatars without changing the boulder size or extending their arms. The user assesses the resulting stomach placement, overhead pose, and first-person framing.

## Ordered implementation

### 1. Extend definition and pose configuration

Edit `ItemDefinition.cs`, `HeldItemSettings.cs`, `HeldItemPose.cs`, and `ItemDefinitionEditor.cs`.

1. Add the enum and switches above. Keep existing serialized field names and existing default Hand values.
2. Resolve Heavy versus Hand defaults in `HeldItemPoseData`. Cache the hold mode, resolved pose settings, and prefab root rotation alongside the existing cached data.
3. Preserve the existing `ContentChanged` refresh path. Resolve definition/default changes on the existing content, selection, and action events, not by rebuilding configuration each frame.
4. Display the correct Heavy pose semantics and applicable override settings in the inspector. Preserve slingshot-specific inspector filtering.
5. Ensure existing definitions with no new serialized fields continue to resolve the current behavior. Heavy defaults must be explicitly authored into `Assets/Game/Settings/HeldItemSettings.asset` as well as having a code initializer.

### 2. Cache authored grips and release geometry

Edit `WorldItem.cs` and add the shared value data/calculations to the existing item pose and clearance code.

1. Author and discover direct children named `LeftHandGrip` and `RightHandGrip` on the item root. Their transforms represent palm contact positions and orientations in the same palm coordinate convention used by `AvatarHandTargets`.
2. Cache both transform references during setup and cache their root-relative poses at the prefab's authored scale. Copy the grip pose data into the active action's presentation data when an action starts, so recovery can continue after pickup, consumption, removal, or pooling of the released item.
3. Do not infer grip positions from geometry. Convert authored grip positions through the root/child transform chain once, with root scale applied exactly once. Compose grip rotations with the item rotation; wrist conversion remains avatar-specific.
4. Cache a release sphere containing a root-relative center offset in world-sized metres and a radius. For Boulder's single non-trigger sphere, use the collider's actual center and scaled radius. For other collider arrangements, keep the existing conservative envelope fallback.
5. Keep the current root-centered `ReleaseRadius` where existing handoff or legacy Hand code depends on that envelope. Expose the offset release sphere to Heavy clearance and inventory drops without silently changing unrelated envelope consumers.
6. Capture geometry at authored item scale, independently of current parenting, visibility, or disabled colliders. Stored items must have usable cached geometry without being equipped.

The existing Boulder prefab has root scale `(9, 9, 9)`, approximately -90 degrees X rotation, sphere radius `0.034000017`, and local sphere center approximately `(0, 0, 0.028499536)`. Its physical radius is approximately `0.306 m`, and the center is approximately `0.2565 m` from the root. Using the existing root-centered envelope alone would make the clearance sphere unnecessarily large. Preserve the model, collider, authored root transform, and size.

### 3. Add shared Heavy pose calculations and body framing

Extend `HeldItemPose.cs` with narrowly scoped Heavy pose data and a shared calculation class. The shared calculations serve held presentation, first-person placement, reach evaluation, and release correction; keep that math out of duplicated player-component branches.

1. Represent a Heavy pose as the item root pose plus left and right palm poses. Calculate the item pose first and derive both palms from the cached grips.
2. Derive a yaw-only body frame from the existing avatar placement, `StandingOffset`, `YawOffset`, seated placement, generated hips and shoulders, and avatar scale. Use shoulder midpoint and each arm's own length; use their average only for spatial tuning units. Do not derive Heavy placement from camera pitch or require new avatar anchors.
3. Reuse the standing/seated placement rules in `HeldItemBodyFrame`. Keep the item reference frame separate from the current IK binding's measurements, so candidate avatar bindings can resolve their own palms and reach without making the object camera-relative.
4. Use the cached collider reference center for Boulder's object-center tuning. Calculate root position by subtracting the rotated center offset. This geometry calculation determines placement, not hand contact locations.
5. Compose body rotation, tuned object Euler offset, and authored prefab rotation in that order. Transform authored grips through that final item pose.
6. Convert each palm to a wrist using the matching generated left/right wrist-to-palm position and rotation. Compare each wrist with its shoulder and its own arm-length reach limit.
7. If a candidate can be brought into reach, translate the whole item and both grips together. Do not independently clamp a palm and then move or resize the item to follow it. For fixed rotation, reachable item translations are the intersection of the two wrist-reach spheres. Use that shared constraint for pose placement and clearance candidates.
8. Keep IK's existing final reach clamping. If authored geometry cannot satisfy both hands, retain a finite pose and refuse an unreachable release; correct the authoring rather than stretching arms or shrinking the boulder.
9. Reuse the existing eased charge curve and visual timing to move the item's center and rotation from its current pose through the control pose to overhead. Hold the final pose once visual progress reaches one.

### 4. Integrate Heavy with local and remote hand presentation

Edit `PlayerHeldItemPresentation.cs` and `PlayerHandPresentation.cs`; adjust `LocalFirstPersonHands.cs` only where needed to supply the shared placement.

1. Generalize the existing left item target currently named `LeftSlingshotPalm` to support Heavy as well. Continue using `AvatarHandSource.Item` for both hands; preserve Contact priority and the existing slingshot branch.
2. On selection, bind the selected Heavy item's cached grip data. On action changes, retain the action definition and grip data independently from the newly selected item.
3. Branch the existing hold/charge pose evaluation by hold mode. Heavy sets both palm targets from its item pose, with the same resolved reach fraction and grip finger clip on both hands.
4. Route Heavy through all evaluation paths: local `PrepareHands`, remote preparation, `PrepareCandidate` during avatar binding changes, and the unbound/non-evaluating remote fallback.
5. Place the local first-person arm rig from the shared Heavy body frame while Heavy occupies the hands. Use the corresponding generated skeleton and scale to align its shoulder midpoint to the derived avatar shoulder midpoint. Camera movement remains free and camera aim still controls launch direction.
6. Apply that placement in `Place`, `TryBody`, initial candidate binding, and immediate release sampling. Replacing only `TryBody` is insufficient because `Place` currently rotates and positions the rig from the camera every frame.
7. Keep Hand and slingshot first-person placement unchanged. During Heavy recovery, keep the appropriate Heavy frame even if the selected slot changes; transition to the destination presentation through the existing return blend. Do not snap the arms to a camera-relative frame at release or slot change.
8. In Heavy `CommitHands`, commit the calculated item root pose directly to `WorldItem.CommitHeldPose`. Read solved palms as needed for the committed sample, but do not reconstruct the item's position from the solved right palm.
9. Extend the release/prepared sample to retain the Heavy item pose and both palms, along with the existing world ID, avatar generation, clearance result, and quantized visual progress.
10. Adapt `WorldItem.ApplyHeldAttachment` and the existing fallback attachment so Heavy is parented to an item frame and receives its committed pose without a transient right-palm grip offset. Preserve authored world scale through all parenting changes. Continue recording the actual visible pose for release handoff.

### 5. Preserve immediate release, cancellation, and two-hand recovery

Extend the existing `PlayerHeldItemPresentation` action lifecycle rather than adding another state machine.

1. Keep `ThrowableItemUse` as the source of gameplay charge strength. Its one-second strength ramp is independent of the 0.35-second lift.
2. On release, retain `SampleImmediately()` and the existing prepare/evaluate/commit sequence. Release from the final committed Heavy root pose at that instant, including during the lift. Never wait for overhead.
3. Preserve `ReleaseArcProgress` in the action snapshot for remote reconstruction. Use existing release `ItemMotion` and the visible-release handoff to settle the actual remote departure; no new pose or grip messages are needed.
4. At recovery start, retain both released palm poses and the action's Heavy data. Avoid reconstructing a Heavy recovery pose through the one-hand `HeldItemPose` constructor in `ActionChanged`, late recovery initialization, or `TryPrepareRelease`.
5. Select OpenFingers for both hands immediately on release. Follow the projectile through both cached grip offsets for at most 0.20 seconds, using the existing follow easing and release identity/operation checks.
6. End tracking for both hands together if the projectile becomes unavailable, either hand exceeds its reach, or either follow path is obstructed. Preserve the last valid two-hand pose until the shared recovery schedule advances.
7. Use no end-pose pause and a 0.20-second return. Keep both hands open during recovery. Returning to Heavy targets both destination grips; returning to Hand preserves its normal right-hand destination and blends the left Item target away. With no selected item, release both targets to the existing free-hand presentation.
8. Preserve selection changes during return by capturing the current poses and blending toward the new selection within the original recovery deadline. Keep `ReadyForUse` and `CompleteAtDeadline` restrictions unchanged.
9. Cancellation before a successful release uses the existing return blend, restores both hands' held presentation, and never enters throw recovery. Reuse existing slot, UI, input, carry, seat, downing, death, and cauldron interruption paths.
10. Clear both Item targets and any retained Heavy references on unbind, stop, or loss of equipment eligibility. Preserve existing slingshot cleanup and action rejection handling.

### 6. Extend clearance and both inventory drop paths

Edit `ItemReleaseClearance.cs`, the Heavy clearance integration in `PlayerHeldItemPresentation.cs`, and `PlayerInventory.ReleaseSlot`.

#### Throw clearance

1. Add an overload to the existing resolver that accepts the cached offset sphere and a second wrist-reach constraint. Preserve the legacy Hand entry point and its behavior.
2. Perform overlap and swept-path checks at the actual world sphere center, calculated from each candidate item root pose. Convert successful center corrections back to a root translation without changing rotation.
3. Retain the existing environment mask, trigger exclusion, 0.01 m padding, anchor search, and maximum 0.20 m correction. Every Heavy candidate must satisfy both wrist-reach constraints. Do not choose the best one-hand correction and reject it afterward when another two-hand candidate could work.
4. Apply accepted corrections to the visible item pose and both palm targets before the correction IK pass. The committed release pose must match the corrected visible object, not an invisible physics-only relocation.
5. If no valid candidate exists, leave the item held and let `TryPrepareRelease` cancel the charge through the existing path. Held/charging clipping remains an accepted limitation when correction is impossible.

#### Drops

1. Keep `DropSelected` and inventory drag-out `DropSlot` converging on `ReleaseSlot` with `ItemReleaseIntent.Drop`. Cancel any active charge before calculating the ordinary drop.
2. Retain camera-forward root placement at the existing 0.65 m target distance, the existing prefab-relative drop rotation, forward nudge, and `ItemReleaseVelocity.Movement` inheritance. Do not equip a stored item or route a drop through held-pose preparation.
3. Replace the fixed 0.12 m collision query with the cached item release geometry, keeping at least the existing small-item clearance. Sweep/check the actual offset sphere rather than treating the root as its center. Reuse the clearance helper's obstruction handling without hand-reach constraints for drops.
4. Check the final placement as well as the route. Starting overlaps or a sphere cast clamped back into an obstacle must not produce a successful drop.
5. Resolve every requested item before `Submit`; if placement fails, leave the inventory request unsubmitted and retain the items. Boulders are single-item slots, so no new stack spreading behavior is needed for them. Preserve the existing small-item stack spacing and release ordering.
6. Reuse the existing pickup eligibility, release collision grace, and releaser pickup cooldown. Do not add speed-based pickup rejection or post-grace immunity.

### 7. Configure physics, impacts, and simulation ownership

Edit `WorldItem.cs` and `WorldItemRegistry.cs`.

#### Physics and sleeping

1. Use `WorldItem.Initialize` to apply definition mass, damping, sleep policy, collision detection, material, and velocity settings on crafted, pooled, predicted-release, and rollback instances. Boulder must resolve its own definition rather than ordinary item defaults.
2. In `SetRecord`, add the player-hitbox/golf-cart sleep exclusions only when `record.Sleeping && !Definition.CollideWhileSleeping`. Preserve unrelated exclusions and the existing awake behavior.
3. Keep the simulator's sleeping rigidbody dynamic so physical player/cart contacts can wake it. Keep non-simulating copies kinematic, and reuse existing sleep/wake motion boundary replication.
4. Keep held colliders disabled and the held rigidbody stopped. Sleeping collision opt-in applies only to loose physical items.
5. Do not register Boulder as the small rock in `BirdRegistry`, copy the small rock's aggressive sleep override, or add projectile-specific contact suppression. Reuse ordinary player collision-body pushing with the existing mass and impulse rules.

#### Directional impact gate

In `WorldItem.ReportImpact`, calculate:

```text
ownIncoming = dot(itemVelocity, intoPlayer)
closing = max(0, dot(itemVelocity - playerVelocity, intoPlayer))
qualifies = ownIncoming >= MinimumImpactSpeed && closing >= MinimumImpactSpeed
```

For `UseSharedImpactThreshold`, require `qualifies` for both collision damage and special shove. Use the configured fixed collision damage and the existing `intoPlayer * closing * ImpulseMultiplier` shove. Respect `DontPushPlayer` and a zero multiplier. Do not multiply shove by Rigidbody mass.

For definitions without the switch, retain the current relative-speed shove gate and `GameSettings.ItemDamageSpeed` directional damage gate.

Keep `ItemPlayerContact.Damage` and its separation/generation handling, host contact reporting, victim-client sweeps, `PlayerItemHitbox.QueueItemImpact`, and combined impulse caps. Do not add damage polling, thrown/armed eligibility, total-speed-only checks, team filtering, or releaser filtering. A rolling or cart-driven loose boulder qualifies through exactly the same path.

#### Simulator selection

1. Put the releasing-client eligibility decision in one method shared by `PredictRelease` and accepted `Release` in `WorldItemRegistry`.
2. Eligibility is the new definition opt-in OR the existing potion/small-rock condition. Boulder opts in; existing items retain their current behavior.
3. Use the existing connection ID in `ItemRecord.Simulator`, including the current host fallback when a releasing connection is unavailable. Preserve prediction-to-confirmation motion continuity and release rollback.
4. Reuse `WorldItemRegistry.Motion` for rotation, sleep/wake boundaries, late snapshots, and disconnect takeover. No message schema change is required.
5. Accept that remote player/cart pushes reach the simulator after their movement replication, and that another client's cart may briefly react against a kinematic copy. Do not add contact ownership transfer, push RPCs, or local push prediction.

### 8. Author Boulder and register its definition

Create `Assets/Game/ScriptableObjects/Items/Boulder.asset`, a plain `ItemDefinition`, and append it to `Assets/Game/ScriptableObjects/ItemRegistry.asset`.

Item IDs 1 through 8 belong to the existing items. Use ID 9 if still available at implementation time; otherwise allocate the next unused nonzero byte across both registered and existing definition assets. Do not renumber existing items.

| Field | Boulder value |
| --- | --- |
| Item name / hold mode | `Boulder` / `Heavy` |
| Stackable / max stack | `false` / `1` |
| Min / max throw speed | `2` / `5` m/s |
| Throw charge time | `1` second |
| Velocity inheritance | `1` |
| Drop speed | `1.5` m/s |
| Minimum impact speed | `8` m/s |
| Use shared impact threshold | `true` |
| Override collision damage / damage | `true` / `25` |
| Don't push player / impulse multiplier | `false` / `1` |
| Mass | `10` kg |
| Linear / angular damping | `0.05` / `0.1` |
| Override sleep threshold | `false` |
| Collide while sleeping | `true` |
| Simulate on releasing client | `true` |
| Synchronize rotation | `true` |
| Collision detection | `ContinuousDynamic` |
| Initial spin / physics material | Existing ordinary defaults `(3, 1, 2)` / no override |
| Maximum speed | `0`, using the existing no-cap sentinel; no boulder-specific speed cap |
| Pose overrides | Use Heavy defaults initially; apply only the pose overrides needed for authoring |
| Ingredient eligibility | Inherited `CanBeIngredient = true` |

Update `Assets/Game/Prefabs/Items/Boulder.prefab` in place:

1. Preserve its existing `VisualRoot`, mesh, material, root transform, Rigidbody, and non-trigger SphereCollider.
2. Add `WorldItem`, `ThrowableItemUse`, `FishNet.Component.Prediction.OfflineRigidbody`, and `BakedPickup` to the root. Assign `WorldItem.visualRoot` to the existing `VisualRoot` and `BakedPickup.Item` to Boulder. Leave `BakedId` at zero on the prefab; later scene placements use the existing baking workflow.
3. Keep the standard ItemWorld layer on its hierarchy. Set the existing Rigidbody's mass/damping/collision mode to agree with the definition. Do not add another collider or Rigidbody.
4. Add the two oriented grip children. Place each at the actual side of the visible model near its center; account for the root's rotation and scale when positioning them. Orient their palm normals inward and fingers suitably for the two-hand hold. Use these authored transforms as the only contact-position source.
5. Keep Heavy grip authoring separate from `ItemSetup.GenerateHeldOffset`, which generates a one-hand offset from mesh bounds. Disable or skip that operation for Heavy definitions in the existing editor UI so it does not misleadingly overwrite Heavy authoring.
6. Do not invoke `ItemSetup.CreateDefinition`: it chooses a unique prefab path and would duplicate Boulder. Do not run the broad `WorldItemSetup.Install` operation, which can touch other prefabs and input assets. Follow their existing conventions using direct Unity asset edits.
7. Capture the icon through `IconCaptureWindow.CaptureAndSave` or the existing Two Birds/Icon Capture window. Use the Boulder prefab, the standard 128 px capture settings, and `Assets/Game/UI/Icons`; assign the resulting sprite to the Boulder definition.

No player prefab components or generated avatar assets are required. Supported avatar settings are `Assets/Game/Settings/Avatars/44816e438f2e4775.asset` and `461d9592f370666a.asset`; use their existing measurements for pose calculations and the user's visual review.

### 9. Add the cauldron recipe

Append an entry to `Exact` in `Assets/Game/ScriptableObjects/Crafting/CauldronRecipes.asset`:

```text
Ingredients: Rock.asset, Quantity 3
Output: Boulder.asset
MinimumTotal: 3
```

Leave `Overrides` and existing Exact entries intact. `CraftingRecipeBook.Resolve` already gives override recipes priority, and `WorldItemRegistry.Crafting` already automatically brews three ingredients. The Basketball override requires Basketball plus at least one additional ingredient, so Basketball plus Boulder continues producing BouncyPotion without a new recipe.

Keep Boulder eligible for held insertion and `CauldronIntake` physical insertion. `CauldronPresentation.AddArrival` already combines VisualRoot and prefab scale, and output presentation uses the initialized WorldItem at its authored scale. Reuse these paths without shrinking the boulder or adding special animations. Unmatched mixtures keep existing failed-brew/disposal behavior.

## User visual acceptance after implementation

The user should exercise these scenarios in the existing game with both supported avatars and, for networking checks, a host and a second client:

1. Craft three small rocks into one Boulder. Confirm ordinary brewing/output, a readable inventory icon, one slot per boulder, multiple boulders in separate slots, and normal pickup at rest or in motion.
2. Equip at stomach height. Confirm both palms meet the authored sides, size stays approximately 0.61 m across, and only the top is visible at normal forward view. Look fully up/down and turn: pitch must not reposition the hold, while avatar yaw follows normally.
3. Tap use during the lift, release midway, and hold beyond both charge durations. Confirm immediate departure from the visible position, an overhead hold after 0.35 s, increasing throw strength through one second, and inherited running/cart movement.
4. Watch both hands open, follow, and return together. Switch between Heavy, Hand, slingshot, and empty slots during recovery. Confirm the next use stays restricted until the ordinary recovery deadline and no left-hand target remains stuck.
5. Cancel charging by slot/UI/control interruptions. Exercise carrying another player, being carried, driver/passenger seating, seat transitions, downing, and death. Confirm existing equipment visibility, permissions, and inventory lifecycle remain intact.
6. Throw near walls and low ceilings. Confirm small corrections visibly move the boulder and both hands together; an impossible release retains the item and returns from charge. Repeat selected-item drops and inventory drag-out drops, including a blocked origin, at the full boulder size.
7. Walk/run into stationary and slowly moving boulders. Confirm ordinary pushing without damage or special shove. Observe fast glancing or separating contacts that do not meet both directional thresholds, and fast incoming contacts that do.
8. Confirm a qualifying contact removes 25 health and gives a speed-dependent shove, sustained contact does not repeatedly damage, and separation allows another hit. Include teammates and the releaser after ordinary grace/cooldown expire. Faster falls, inherited movement, and cart-driven motion may qualify.
9. Push a sleeping boulder with a player and a golf cart, then let it roll downhill. Confirm waking and sustained rolling. Watch position and rolling rotation on the other client, including the accepted delay for remote pushes and cart reactions. After the simulator disconnects, confirm ordinary takeover continues the loose boulder.
10. Insert Boulder by hand and physically into the cauldron, observing full-size arrival/output presentation. Confirm Basketball plus Boulder produces BouncyPotion and unmatched mixtures retain normal failure/disposal behavior.
11. Revisit ordinary Hand throws/drops, slingshot use, potion use, and sleeping small rocks. Confirm their existing presentation, recovery, collisions, and simulation behavior remain intact, with no movement or inventory-capacity changes from carrying Boulder.
