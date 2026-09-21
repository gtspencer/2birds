# Held Item and Hand IK Review Disposition

Assessment of `Held_Item_Hand_IK_Review.md` against commit `dde98becac50e2ada72a9364c1f3352616a534f7`, the current matching implementation, the specification, and the implementation plan committed with the change. This document records review decisions and the appropriate scope of any subsequent fixes; it does not implement them.

| Item | Decision |
| --- | --- |
| 1. Item pose while carrying a player | **Flag: persistent suppression assumes a requirement contradicted by the plan's explicit visibility rule.** |
| 2. Rotation-driven projectile-origin movement | Retain; the position calculation contains cosmetic spin. |
| 3. Scene scale retained in throw clearance | Retain; throw clearance uses an envelope that can retain discarded scale. |
| 4. Pickup before projectile tracking starts | Retain with qualification: only the remaining follow interval is wasted. |
| 5. Cancellation uses stale return duration | Retain as P3; current equal asset settings conceal a small, concrete defect. |

The review's introductory scope statement is stale: the implementation is now committed, and `65f3297` is its parent. Its five findings still refer to the relevant code.

## 1. Carrying a player reinstalls the item pose

**Flag the proposed persistent suppression; do not apply it as an established bug fix.** The control-flow observation is accurate, but the conclusion equates permission to use an inventory item with permission to display one.

Evidence:

- [PlayerInventory.cs](Assets/Game/Runtime/Inventory/PlayerInventory.cs), lines 53–55, excludes the carried player from `CanAct`, not the carrier. A carrier can retain an equipped selection.
- [PlayerNetworkState.cs](Assets/Game/Runtime/Player/PlayerNetworkState.cs), line 48, restricts item charging to `CarryRole.Free`. [PlayerEquipment.cs](Assets/Game/Runtime/Player/PlayerEquipment.cs), `BeginUse` and `EndUse`, route the carrier's input to `PlayerCarry`.
- [PlayerHeldItemPresentation.cs](Assets/Game/Runtime/Player/PlayerHeldItemPresentation.cs), lines 152–165, clears action/target state when charging becomes forbidden. Lines 36–39 and 385–426 then allow ordinary holding under the separate equip permission. The target can therefore return, as reported.
- [The committed plan](Specs/Held_Item_Hand_IK_Plan.md), line 19, explicitly preserves separate use/equip permissions. Line 147 explicitly makes visibility depend on equip permission, snapshot readiness, and recovery, while readiness **additionally** excludes incompatible carry roles. The implementation follows that rule.

There is conflicting wording: the specification's context-change table and the plan's line 302 call for clearing target ownership in incompatible carry contexts. That supports clearing the interrupted action, but does not unambiguously override the explicit rule allowing held visibility when equip remains permitted. Clearing a previous charge/recovery and establishing ordinary holding are different operations. The carry transition also supplies an Idle action through `PlayerSeating.Apply` and `ApplyControlState`; this finding does not demonstrate resumption of the old recovery.

**Decision:** the review has identified a contract ambiguity, not established that carrying must hide the item and permanently relinquish its holding target. Adding `CanCharge` to the visibility/holding gates would change the plan's stated behavior. Preserve the existing distinction unless the intended product behavior is explicitly changed. Do not add a pose-permission abstraction or change inventory/carry routing for this item.

**Visual decision for the user:** equip an item, carry another player, and release that player. Confirm whether retaining the ordinary held item during carrying is desired, while ensuring an interrupted charge/recovery does not resume.

## 2. Follow position contains projectile spin

**Retain.** Freezing the grip offset and wrist orientation does not remove rotation already embedded in the position being followed.

[PlayerHeldItemPresentation.cs](Assets/Game/Runtime/Player/PlayerHeldItemPresentation.cs), lines 435–439, follows `PresentedOrigin + followOffset`. [WorldItem.cs](Assets/Game/Runtime/Items/WorldItem.cs), line 53, returns `visualRoot.position`; lines 511–512 set that position to `center - cosmeticRotation * bodySphereCenter`. Consequently the hand position contains the same rotation-dependent term even though `followOffset` is constant.

This applies to existing content: [Rock.asset](Assets/Game/ScriptableObjects/Items/Rock.asset) disables rotation synchronization and supplies nonzero spin. [Rock.prefab](Assets/Game/Prefabs/Items/Rock.prefab), lines 102–111 and 228–229, supplies scale 3 and a collider-center offset of approximately 0.0285 local units, giving approximately 0.0855 metres at release scale. That is the orbit radius, not a claim that every brief follow moves the hand by that entire distance.

The plan's line 153 prescribes this origin-based formula and incorrectly states that it stays independent of projectile spin. Following the plan explains the implementation but does not fix that mathematical assumption. The specification explicitly says not to follow projectile spin. Wrist rotation already complies; positional motion is the problem.

**Worthwhile scope:** expose a distinct presented translation anchor for centered-motion items, retaining existing interpolation/correction. Cache the follow-point-to-anchor offset using the release pose and its placement correction. Use the same anchor convention for owner preparation and observer reconstruction. Do not silently redefine an item-origin API as a sphere center, recenter the prefab, or alter network motion/rotation synchronization. The existing presented sphere-center calculation in `WorldItem` provides a starting point.

**User visual check:** observe weak rock throws with different initial spins. The wrist should remain body-relative and the hand should follow departure without orbiting the projectile's center.

## 3. Throw clearance retains discarded scene scale

**Retain.** This is a concrete input error in the new throw-clearance path, even though the original diameter estimate predates the commit.

[WorldItem.cs](Assets/Game/Runtime/Items/WorldItem.cs), lines 109–112, seeds `DropDiameter` from scene collider bounds. Lines 162–164 replace `defaultScale` with prefab scale but only increase the diameter through `Mathf.Max`. World release uses that prefab scale at line 227; held attachment uses it at lines 324 and 330. Neither `ResetPresentation` nor `ReturnToPool` clears the diameter, and subsequent initialization skips its calculation once `Definition` exists.

[ItemPlacementZoneEditor.cs](Assets/Game/Editor/ItemPlacementZoneEditor.cs), lines 88–92, supports randomized instance scales. [WorldItemRegistry.cs](Assets/Game/Runtime/Items/WorldItemRegistry.cs), lines 108–143, initializes those baked scene objects directly. The larger initial bound is therefore a supported input, not an invented configuration. [PlayerHeldItemPresentation.cs](Assets/Game/Runtime/Player/PlayerHeldItemPresentation.cs), line 338, uses the retained diameter for throw clearance after actual release geometry has reverted to prefab scale.

**Worthwhile scope:** cache a separate prefab-scale release envelope and use it for the new throw policy. Preserve `DropDiameter` for its existing departure-drop and fallback presentation consumers. The plan's instruction to preserve a larger diameter for departure drops does not require using it for throws. Avoid changing scene scales, placement tooling, or ordinary drop behavior.

**User visual check:** collect normal and enlarged instances of the same item and throw beside the same obstruction. Matching released geometry should produce matching clearance decisions.

## 4. Pickup before tracking starts leaves pending follow active

**Retain, with a narrower timing claim.** This can occur for an observer that first reconstructs recovery after pickup, including local pickup prediction.

[PlayerHeldItemPresentation.cs](Assets/Game/Runtime/Player/PlayerHeldItemPresentation.cs), lines 208–221, sets `tracking` only for a matching available release and marks a failed lookup unavailable only for a `Removed` record. [WorldItemRegistry.cs](Assets/Game/Runtime/Items/WorldItemRegistry.cs), lines 438–443, changes picked-up records to `Held` and clears their operation/releaser. [WorldItem.cs](Assets/Game/Runtime/Items/WorldItem.cs), lines 52 and 259–266, also makes a predicted pickup unavailable without changing its record to `Removed`. Neither case ends an as-yet-untracked follow.

The one-second pickup cooldown does not make the scenario impossible: `StartPickupCooldown`, lines 119–123, applies only on the releaser's local client. Another player can pick up the projectile during follow.

**Qualification:** `ActionChanged` initializes observer age from the shared timestamp, and `Recover` compares that age against the maximum. An observer already past `MaximumFollowDuration` skips follow immediately. The erroneous wait is at most the **remaining** follow interval, normally less than 0.20 seconds, and a newer owner Idle transition supersedes it. This is a bounded presentation issue, not an indefinite equipment lock or a fresh full-duration wait for every late observer.

**Worthwhile scope:** distinguish positive evidence that the matching release ended from an unresolved release. In particular, recognize unavailable matching presentation and later pickup/lifecycle evidence. Preserve waiting when the original held record or no record has arrived yet. Do not classify every nonmatching holder/operation as a completed release: it can be an older baseline. `SetHeld` erases release identity, so a general history classifier would require more evidence than a bare mismatch. A new lifecycle history or network timestamp stream is not justified for this bounded visual defect.

**User visual check:** begin observing before the follow maximum expires but after another player has picked up the item; repeat with local pickup prediction. The hand should advance toward return while replacement visibility continues to respect the owner's action.

## 5. Cancellation inherits another item's blend duration

**Retain as P3.** The trace is deterministic; equal current settings mask it rather than eliminate it.

[PlayerHeldItemPresentation.cs](Assets/Game/Runtime/Player/PlayerHeldItemPresentation.cs), lines 183–197, updates selection during recovery without calling `BeginBlend`. Recovery correctly uses the thrown item's `actionData` at line 472. Completion, lines 291–295, refreshes the new hold pose without updating `blendDuration`; beginning a charge also leaves that field unchanged.

[PlayerNetworkState.cs](Assets/Game/Runtime/Player/PlayerNetworkState.cs), lines 82–85, publishes cancellation as Idle with definition ID zero. `ActionChanged` then clears `actionDefinition` at line 236 before choosing the duration at line 297, which falls back to the stale `blendDuration`. Thus throwing A, selecting B during recovery, then charging/cancelling B can reuse A's duration. All three current item assets set `ReturnBlendDuration` to 0.2 seconds, so differing per-item settings are necessary to expose it.

**Worthwhile scope:** retain the cancelled action's cached duration before replacing its definition, or explicitly select the current item's duration for cancellation. The former directly identifies the action being cancelled. Leave the active throw's cached recovery timing untouched. This is a small correction to existing per-item configuration, not a reason for a timing-system refactor.

**User visual check:** use visibly different return durations for A and B. Switch during A's recovery, then charge/cancel B. A's recovery should use A's duration and B's cancellation should use B's.

## Other review statements

| Statement | Assessment and implementation consequence |
| --- | --- |
| Owner/server throw transaction ordering | Supported by `PlayerInventory.Submit` and `Commit`: operation assignment and recovery precede predicted rebuilding and accepted release/equipment updates. No corrective work follows from this statement. |
| Shared suppression, explicit release success, matching rejection | Supported by `WorldItem.ApplyHeldAttachment`, the `TryReleaseEquipped`/`TryReleaseItem` return path, `ThrowableItemUse.EndUse`, and `CompleteRecovery`'s identity check. This does not independently prove every network ordering at runtime. |
| Compact transition replication | Supported by `ItemActionSnapshot` and `PredictRecovery`: pose progress and intermediate recovery stages remain local, and release metadata travels in the inventory request. No new per-frame arm RPC is present. |
| Cached references and evaluation ordering | Supported by controller startup/change handlers and `AvatarPresentationSystem` calling `PrepareTargets` immediately before `Evaluate`. Held attachment uses the solved bone. These are source properties, not measured performance guarantees. |
| Whole-dictionary registry scans | Correct for `EquippedPresentation`, `RefreshHolder`, and `DetachHolder`. **Do not turn this consideration into an optimization requirement:** without a demonstrated cost, an additional holder index and its lifecycle maintenance are not justified. |
| Bounded clearance queries | Supported by `ItemReleaseClearance.TryResolve`'s fixed candidate loops. Bounded work does not establish its runtime cost. No profiling infrastructure or query redesign is warranted by this observation alone. |

The review's broader host/client, input, rebinding, and late-observer visual checks remain acceptance scenarios for the user, not additional demonstrated defects. No scene, prefab, or asset authoring is required for this assessment.
