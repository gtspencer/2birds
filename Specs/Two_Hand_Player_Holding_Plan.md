# Two-Hand Player Holding Plan

## Objective and scope

Implement [Two_Hand_Player_Holding_Spec.md](Two_Hand_Player_Holding_Spec.md). A carrier grips two animated palm targets on the carried avatar, using Boulder's heavy-item hand conventions and throw recovery. Existing player attachment, charge timing, placement, launch velocity, and release physics remain responsible for moving the carried player.

Accurate grip tracking applies on the carrier's client and other observing clients. On the carried player's own client, show the carrier's arms outstretched in front instead; this view does not require accurate palm contact or an instance of the carried player's full-body avatar.

Starting a carry genuinely deselects the carrier's inventory slot. Carrying prevents selection and pickup auto-equipping without preventing otherwise eligible inventory collection. Release leaves the carrier deselected. The carried player's existing selection and temporary item hiding/restoration remain intact.

This plan assumes the existing accepted carry transition is the start of carrying; it does not add pickup prediction. Throw presentation begins immediately at the existing local release preview. Player carrying uses the shared `HeldItemSettings.HeavyHoldSettings` tuning, which Boulder currently uses without an override, and the shared avatar grip/open finger clips.

## Relevant implementation

All paths below are relative to the project root.

| File | Integration point |
| --- | --- |
| `Assets/Game/Runtime/Player/PlayerCarry.cs` | `Install`, `RequestRelease`, `ApplyTransition`, `Finish`, preview and survivor cleanup; owns replicated carry relationships and player release physics. |
| `Assets/Game/Runtime/Player/PlayerSeating.cs` | Applies carry/control state, updates the motor control revision, then calls `PlayerInventory.ApplyControlPermissions`; its presentation event covers accepted role changes. |
| `Assets/Game/Runtime/Inventory/PlayerInventory.cs` | `CanEquip`, selection storage, request prediction/replay, authoritative commit, inventory snapshots, and control-permission changes. |
| `Assets/Game/Runtime/Player/InventoryInputHandler.cs` | Hotbar, scroll, and cycle already share `inventory.CanEquip`. |
| `Assets/Game/Runtime/Player/PlayerEquipment.cs` | Routes use to player charge/throw before checking item eligibility. `PlayerInventory.DropSelected` similarly routes carry drops. |
| `Assets/Game/Runtime/Player/PlayerHeldItemPresentation.cs` | Heavy body frame, palm submission, and follow/pause/return recovery currently live here and depend on item-specific state. |
| `Assets/Game/Runtime/Items/HeldItemPose.cs` | Palm-to-wrist conversion, `HeldItemBodyFrame`, heavy two-palm poses, reach checks, and body-relative pose conversion. `HeavyItemGrips` stores static item-local poses, unsuitable for caching animated player grip poses. |
| `Assets/Game/Runtime/Avatars/AvatarHandTargets.cs` | Shared hand-target arbitration consumed by full-body and first-person IK and finger layers. |
| `Assets/Game/Runtime/Player/PlayerHandPresentation.cs` | Existing hand coordinator, first-person placement, remote preparation, candidate preparation, and context lifecycle. |
| `Assets/Game/Runtime/Avatars/AvatarPresentation.cs` | `AvatarBinding`, committed avatar identity, `WillUnbind`/`DidBind`, and staged replacement. |
| `Assets/Game/Runtime/Avatars/AvatarPresentationSystem.cs` | Orders avatar input, evaluation, the shared spring batch, and commit. |
| `Assets/Game/Runtime/Avatars/AvatarInstance.cs` | Full-body bone binding, animation evaluation, and per-avatar `CarriedOffset`. |
| `Assets/Game/Runtime/Player/PlayerAvatarPresentation.cs` | Supplies player context and appearance; living owners' full-body presentations remain unregistered. |
| `Assets/Game/Editor/AvatarProcessor.cs` | Replaces generated prefabs and emits the successful-processing completion log. |

## 1. Make deselection part of the accepted carry transition

Change `PlayerInventory.CanEquip` to also reject `carry.IsCarrying`. Preserve `CanAct`, so this restriction does not disable otherwise valid collection, inventory storage, or the player drop route.

Implement carrier deselection inside inventory control-state handling, after the new carry role and motor control revision have been installed. Use the existing selection value `-1` and existing equipment refresh/notification paths. Do not implement this by merely returning `-1` from `SelectedSlot`: its current permission mask hides a stored selection and would restore it later.

Specific changes:

1. On entering `CarryRole.Carrying`, clear authoritative `serverSelection` and the owner's confirmed/predicted selection. Leave all slot contents and world-item ownership unchanged. Never clear selection for `CarryRole.Carried`.
2. Perform the transition-driven clear through a small inventory-owned method or branch in `ApplyControlPermissions`. Do not call `SelectSlot(-1)` after changing the role: that public method rejects calls when `CanEquip` is false. Preserve the ordinary manual deselection behavior outside carrying.
3. Keep cancellation of item use, stale pending-operation removal, `UpdateEquipment`, `RebuildView`, holder refresh, and inventory notifications in their existing transition flow. Carry startup must also cancel any item hand recovery so it cannot resume after release.
4. Publish the authoritative deselection through the existing inventory reply path, advancing `serverRevision` when the stored selection changes. This must work on a host and on a server whose carrier is remotely owned.
5. Prevent an inventory reply created before pickup from resurrecting the old selection. Include the producing control revision in the existing inventory snapshot/reply arguments and retain the last carrier-deselection control revision locally. Ignore only the selection portion of an older snapshot across that boundary; continue processing its valid contents, acknowledgements, and rollback information. If a snapshot arrives before its carry transition, applying that transition must still clear its selection. Do not replace this with a permanent selection lock after release.
6. Gate predicted `InventoryOperation.Select` replay with `CanEquip`, matching `SelectSlot` and authoritative `Commit`. Keep pickup auto-selection gated by both `CanEquip` and the request's `AutoSelect` flag on both sides. A pickup submitted while carrying must remain non-selecting even if its acknowledgement arrives after release.

The existing input handlers should inherit the restriction without new input flags. Keep `PlayerEquipment.BeginUse`/`EndUse` carry routing before item eligibility checks, and keep `DropSelected` routing to `carry.Drop`. Release, reset, death, seating, or disconnect must not restore the carrier's previous slot. Ordinary item selection becomes available again when the existing control rules permit it.

## 2. Bind authored grip transforms to the full-body avatar

Extend `AvatarBinding` with cached left/right carry-grip references and a complete-pair predicate. Resolve exact names `LeftHandGrip` and `RightHandGrip` once when constructing a full-body binding. Search descendants under the avatar skeleton, including inactive descendants and nested bones; the item's existing direct-child `Transform.Find` approach is insufficient.

First-person bindings do not search for grips. A usable pair consists of two distinct, unambiguous named transforms beneath the animated skeleton. Missing or ambiguous names yield no pair and must not fail avatar initialization or player pickup. A pair outside a carrier's reach is an authoring problem handled by the existing IK reach limit, not a reason to reposition or reject the carried player.

Cache transforms, not their local or world poses. Every hand sample reads the current `position` and `rotation` from both transforms. These rotations are palm orientations, so the existing `AvatarHandIK` wrist-to-palm conversion remains responsible for wrist orientation.

In `PlayerHandPresentation`, first determine whether the carried partner is locally owned. Use the outstretched presentation in section 3 for that view. Otherwise, bind the carried partner's `PlayerAvatarPresentation.Presentation` from the accepted carry relationship. Subscribe to that presentation's `WillUnbind` and `DidBind`, immediately consume an already committed binding, and detach when the partner changes or the hand presentation stops. Avatar replacement must clear the old pair before destroying it and acquire the new pair on commit. Keep any release-follow target separate from `carry.Partner`, which is cleared on release.

## 3. Use outstretched carrier arms on the carried player's client

When the carried partner is locally owned, use a simple body-relative pose for the remote carrier's arms. This is a deliberate view-specific presentation, independent of whether the carried avatar has authored grips.

In the carrier's existing remote hand preparation, place each wrist forward from its respective shoulder along the carrier's body-forward direction, using that arm's length and the shared heavy reach fraction. Convert these to palm targets using the carrier binding's existing wrist-to-palm measurements, with mirrored inward-facing palm orientations. Submit both targets through the carry hand source and use the shared grip finger clip. The arms stay outstretched as the carrier turns, including while charging.

On a throw, open the fingers and use the shared recovery timing to return from the retained outstretched palms to rest, without tracking the locally owned released player's body. Capture this presentation mode before the carry relationship clears. On a drop or other termination, clear it through the ordinary carry cleanup. Ownership and carrier-avatar changes refresh the mode and measurements through existing lifecycle events.

Keep the local player's existing full-body registration, visibility, and cosmetics lifecycle unchanged. No hidden avatar, extra prefab, authored animation clip, or additional pose settings are needed for this view.

## 4. Extract the shared two-hand presentation behavior

Add a managed helper at `Assets/Game/Runtime/Avatars/TwoHandHoldPresentation.cs`. It is used by the existing heavy-item presentation and the player carry path in `PlayerHandPresentation`.

Keep its boundary limited to presentation:

- Inputs: two live palm poses, a `HeldItemBodyFrame`, existing heavy pose settings, the environment mask, target availability, elapsed release time, and the destination palms/weights for return.
- State: release-start and retained body-relative palms, follow/pause/return stage, hand weights, and heavy-frame weight.
- Outputs: the two palm poses, reach, weights, finger state, and frame weight for the current sample.
- Operations: begin release from the last displayed palms, sample the shared follow/pause/return behavior, reset, and submit/clear two targets for an explicitly supplied `AvatarHandSource`.

Move the heavy two-hand recovery calculations out of `PlayerHeldItemPresentation.Recover` and related palm-retention code into this helper. Preserve the existing short follow interpolation, both-arm wrist reach calculation, four obstruction linecasts, retained pose after following stops, configured follow deadline, pause duration, smooth return, and open fingers. Extract the heavy body-frame calculation for use by both callers; preserve its standing, seated, scale, sole-plane, yaw, and avatar-offset semantics.

The heavy-item caller supplies palms calculated from its released item's presented root and item-local grips. It retains item identity/operation matching, projectile lookup, action timing, item clearance, charge poses, release commits, and recovery completion. Its return destination must still support no selected item, a newly selected one-hand item, or another heavy item, including a selection change during return. Do not migrate the one-hand and slingshot action paths into the helper.

The carry caller supplies palms directly from the animated carried avatar, or the outstretched palms from section 3 on the carried player's client. It does not construct an `ItemDefinition`, `WorldItem`, inventory action, static `HeavyItemGrips` snapshot, or item release sphere. Holding and charging do not call heavy-item placement, charge arcs, reach projection of an item root, or item clearance. Only the hands are affected.

Add a distinct `AvatarHandSource.Carry` between `Item` and `Contact`, resizing target storage and updating resolution accordingly. This gives carry explicit ownership of both targets and prevents item cleanup from clearing carry IK. Preserve contact priority and item/free behavior. Both users of the helper supply their own source, so clearing one source never clears the other.

## 5. Integrate carry hands and first-person framing

Use the existing `PlayerHandPresentation` lifecycle and hand preparation callbacks to own carry presentation. No additional player component is required.

While carrying with a complete pair:

1. Sample both live palm poses and submit both carry targets with the shared grip finger clip and the heavy reach fraction, using the same clamp as `HeldItemPoseData.Reach`.
2. Keep the hold pose identical during player charge. `PlayerCarry.Charge01` continues to drive existing charge UI and launch speed only.
3. Choose the carry helper's heavy-frame weight when carrying or recovering; otherwise use the held item's existing frame weight. Apply this consistently in `PrepareRemote`, `TryBody`, `BodyFor`, and `Place`, including staged first-person and full-body bindings.
4. Reuse `HeldItemBodyFrame.WithReference` and `LocalFirstPersonHands.Place` so the first-person shoulders adopt the same body-relative heavy frame and suppression of camera-relative placement offsets used by Boulder.
5. Keep carry preparation independent of `inventory.CanEquip`. Disallowing item equipping must not disallow carry IK or its frame. Do not extend the existing early-return guards in `PlayerHeldItemPresentation` to control player carrying.

On clients using accurate grip tracking, a missing pair clears both carry targets and the carry frame override, letting existing free/contact presentation resolve normally. Deselect/equip restrictions and player carry/throw behavior still apply. Acquiring a complete replacement binding enables the hold presentation without another pickup. The carried player's own client uses the outstretched pose without requiring a grip pair.

For an ordinary drop or forced termination, clear carry targets and return the hand frame to its ordinary destination without starting throw-follow. For an explicit item selection after release, cancel remaining carry recovery before item preparation so the selected item's hand targets and frame can take over immediately. A new carry, incompatible control state, life change, ownership change, disable, or despawn also cancels stale recovery.

### Evaluation ordering

The current full-body loop evaluates hosts in registration order, so the carrier can sample the carried avatar's previous pose. Cache the grip-source dependency with the carry binding and order the relevant existing avatar evaluations so the source avatar is animated before the dependent carrier prepares its hand targets. This dependency also applies during release following, when neither player has a carry role.

The outstretched presentation has no grip-source dependency and does not register or evaluate the locally owned carried avatar.

Use the existing rule that players cannot form carry chains to keep scheduling narrow. Prepare inputs and staged instances once, evaluate grip-source/nondependent hosts before dependent carriers, and preserve the existing single shared spring batch and final commit. Do not recursively advance another avatar from a hand callback. A staged binding must never be mistaken for the committed grip source.

Grips must be sampled after animation has updated their parent bones. If final spring processing moves a grip parent, refresh the affected carrier's hand solve from that final pose without advancing its animation or finger time again; keep that correction inside the existing avatar evaluation path. First-person hands already run at order 210, after the full-body system at 200, and should sample the finalized source poses there. Account for a source binding replaced during commit by refreshing the dependent target references before the next solve.

## 6. Start throw recovery from carry release data

Player carrying already has separate throw (`EndUse`) and drop entry points, which supply different velocity/recovery values to the same release path. Its replicated `PlayerRelease` payload does not preserve an explicit throw/drop classification. Ordinary inventory items already carry `ItemReleaseIntent.Drop` or `ItemReleaseIntent.Throw` in `InventoryRequest.ReleaseIntent`.

Reuse that existing `ItemReleaseIntent` enum for an intent field in `PlayerRelease`, propagating it through `RequestRelease`, `ServerRelease`, `AcceptRelease`, and `CarryTransition`. Set `Throw` only from `EndUse`; drop, impact, reset, survivor, and life-release paths use `Drop` or ordinary nonthrow cleanup. Do not infer intent from velocity or recovery duration.

Expose a local presentation notification through `PlayerCarry` containing the released partner, release identity, release intent, and existing release data. Use the carrier's accepted control revision and partner identity to identify an accepted release; retain the existing request ID for the owner's pending preview. This is presentation state, not another replicated action system.

Release sequence:

1. If local placement fails, do not begin recovery.
2. For a non-host owner's valid throw request, capture the last displayed carry palms and retain the partner binding before preview begins. Begin recovery with the existing preview, following the released avatar's animated grip transforms as its graphics move along the existing preview trajectory.
3. For host and observer clients, start recovery from the accepted throw transition. Capture the old carry target and presentation mode before applying the transition clears `Partner` and invokes control/presentation cleanup. Publish/start the recovery after the accepted control state is installed, so ordinary context cleanup does not immediately erase it. The carried player's client recovers from retained outstretched palms without a body-follow target.
4. Reconcile the owner's accepted transition with its pending preview instead of restarting recovery. Stale or duplicate transition callbacks must not restart it. Only transitions actually accepted by the control-revision flow may trigger observer recovery; retain a pending presentation release if reference/life readiness delays application.
5. A rejected release clears preview recovery and restores grip tracking if the carry relationship still exists. Other carry-ending transitions clear it instead. Reacquiring or rebinding a different avatar must never make a previous release follow that new target.
6. The helper follows both live grips while reachable and unobstructed, switches to open fingers at release, retains the last valid palms when tracking stops, and returns to rest using the heavy durations. Missing/destroyed bindings end following safely using retained poses. Both hand weights and the frame weight finish at zero for an empty-handed carrier.

Use local elapsed presentation time from the preview or accepted release notification, following the existing carry release flow. No per-frame hand messages or grip transforms are replicated. Late joiners receive the current carry relationship and bind its grips; they do not replay historical throws. Leave player release position, yaw, velocity, inherited movement, recovery physics, immunity, and preview trajectory calculations unchanged.

## 7. Add the processing reminder and author grip content

Extend the final successful `AvatarProcessor.Process` log, after all processing/save work succeeds, to identify `prefabPath` and include this instruction:

> Author and save LeftHandGrip and RightHandGrip beneath the appropriate animated bones in the generated full-body prefab. Reprocessing replaces this prefab; the grip points must be re-authored after every processing pass.

Keep the existing replacement workflow and failure rollback. Do not copy grips from the replaced prefab or emit the success reminder on failure. Do not add grip-pose fields to `AvatarSettings`.

The designer authors the pair in Prefab Mode on each supported generated full-body prefab:

| Avatar | Full-body prefab |
| --- | --- |
| Chill | `Assets/Game/Prefabs/Avatars/44816e438f2e4775.prefab` |
| Genesis Gerbil | `Assets/Game/Prefabs/Avatars/461d9592f370666a.prefab` |

Create one `LeftHandGrip` and one `RightHandGrip` under the intended animated bones. Names identify the carrier's left/right palms, regardless of the side of the carried avatar on which they are placed. Use the same palm-orientation convention as Boulder's grips and save local positions/rotations in the prefab. Choose contact points that work with the existing carry spacing for both carrier avatars. No duplicates belong in `Prefabs/Avatars/FirstPerson`.

## Manual acceptance by the user

Perform the following in Unity after implementation and grip authoring:

| Scenario | Expected result |
| --- | --- |
| Chill→Chill, Chill→Gerbil, Gerbil→Chill, Gerbil→Gerbil | Both palms meet the authored contacts with grip fingers in the carrier's first person and from a third observer's client. Repeat with host and remote carriers. |
| View the carrier from the carried player's own client | The carrier's arms are outstretched in front during holding and charge; on throw the fingers open and arms return to rest. Accurate contact with the carried body is unnecessary, and no local full-body avatar is instantiated for grip tracking. |
| Idle animation, walking, turning, camera pitch/yaw | Grips track the animated body without changing carry spacing, upright pose, carrier yaw, or avatar offsets. |
| Charge then throw at minimum and maximum charge | No player pullback during charge; existing launch behavior; both hands follow briefly, open, and return to rest. |
| Throw near reach limits and obstructions | Following stops at the heavy-hand limits without moving or stopping the released player. |
| Pick up with Boulder or another item selected, including an active charge/recovery | The item remains in the same slot, the actual selection clears, and carry hands take over. Check a full inventory too. |
| Hotbar buttons, scrolling, cycling, item use, direct use while carrying | No item equips or activates; player charge/throw and drop still work. |
| Collect an eligible world item while carrying | Inventory receives it without selecting it. Release leaves hands empty until manual selection. |
| Carried player begins with an item selected | Their existing item hiding and restoration behavior remains unchanged. |
| Drop, blocked release, rejected release, reset, seating transition, death, disconnect | Appropriate carry cleanup or grip restoration; no throw-follow for nonthrows and no carrier item restoration. |
| Rapid pickup/release with an inventory operation in flight | Delayed replies do not restore the pre-carry selection or auto-equip a pickup submitted during carrying. |
| Replace carrier/carried avatars, remove either grip, join during a carry | Bindings refresh without stale targets; an incomplete pair preserves normal carry/throw and inventory restrictions. |
| Select an item immediately after throwing a player | New item hands take over without leftover carry IK. |
| Throw Boulder, switch items during its recovery, use a one-hand item and slingshot, drive with wheel contacts | Existing hand presentation remains consistent after shared-code extraction. |
| Reprocess an avatar with authored grips | Replacement removes the authored pair; the completion log identifies the full-body prefab and requires re-authoring both grips. Author and save them again before retesting. |
