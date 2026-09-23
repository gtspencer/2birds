# Two-Hand Player Holding Spec

## Purpose

When a player carries another player, both hands grip designer-authored points on the carried avatar. Hand presentation follows the heavy-item holding behavior used by Boulder, while player placement and throw physics continue to use the existing player-carry system.

The carrier deselects their held item when carrying begins, keeps their hands available throughout the carry, and remains empty-handed after release.

## Terms

- **Carrier:** the player holding another player.
- **Carried player:** the player being held.
- **Grip pair:** the carried avatar's authored left and right palm targets, including position and rotation. Left and right identify the carrier's hands.

## Carry and hand presentation

### Placement

- Preserve the existing upright carry pose, carrier-yaw alignment, shared `CarryOffset`, and per-avatar `CarriedOffset` behavior.
- Continue using the existing player-carry attachment and movement rules. Heavy-item reuse applies to hand presentation; it does not introduce automatic carried-player repositioning to satisfy arm reach or item clearance.
- Use one fixed grip pair per avatar, regardless of the direction from which the avatar was picked up.
- Grip authoring must account for the existing carry spacing and the reach of the supported carrier avatars.

### Holding

- Drive both hands from the grip pair's live world positions and rotations so they follow the carried avatar's bone animation.
- Use the heavy-item palm-target conventions, shared grip finger pose, and first-person arm placement.
- Support the carrier's first-person hands and the carrier's full-body hands as seen by other clients.
- Resolve and cache grip references when binding an avatar. Follow their current transforms through the existing hand-presentation updates; avoid repeated hierarchy searches.
- Refresh or clear bindings through the existing avatar bind/unbind and carry transitions.

### Charging and release

- Charging a player throw keeps the normal carry placement and grip tracking. Do not apply the heavy-item charge motion to the carried player.
- Preserve the existing throw charge timing, direction, speed, inherited velocity, and release physics.
- On a throw, use Boulder-style follow-through: both hands briefly follow the released player, open their fingers, and return to rest, using the existing heavy-item reach, obstruction, and recovery behavior.
- Preserve the existing player drop action and other carry-ending conditions.

## Inventory and input

- When carrying begins, genuinely clear the carrier's selected slot using the existing deselection behavior.
- The item remains in its existing inventory slot. No item transfer, world drop, or free inventory slot is required.
- Only the carrier's selection is cleared by this feature. Preserve the carried player's existing selection, temporary item hiding, and restoration behavior.
- While carrying, block manual item selection and equipping through hotbar buttons, scrolling, and cycling. Continue blocking item use while allowing the existing player throw and drop controls.
- Preserve the existing rules for collecting world items into inventory. Collection must not automatically select or equip an item while carrying.
- Ending the carry leaves the carrier deselected. Do not automatically restore the previously selected item.
- Apply the same selection restriction to owner prediction and authoritative inventory handling through the existing inventory rules.

## Prefab authoring

### Grip objects

- Designers author the grip pair directly on each generated full-body avatar prefab using Unity's normal transform tools.
- Add one transform named `LeftHandGrip` and one named `RightHandGrip`, parented beneath the appropriate animated bones.
- Each transform's local position and rotation define the palm contact pose using the same orientation convention as heavy-item grips.
- The runtime must resolve the named grip objects beneath the avatar's skeleton; they are not restricted to direct children of the avatar root.
- Store the authored poses in the prefab. `AvatarSettings` is not the source of grip positions or rotations.
- Grip objects belong to the carried avatar's full-body prefab. They do not require duplicates in that avatar's first-person arm prefab.
- Author the pair for each supported avatar, including Chill and Genesis Gerbil.

### Avatar processing

- Preserve the current generated-prefab replacement workflow. Processing does not preserve previously authored grip objects.
- After successful avatar processing, log a completion message that identifies the generated full-body prefab and tells the designer to author `LeftHandGrip` and `RightHandGrip` beneath the appropriate bones.
- The message must make clear that reprocessing requires the grip points to be re-authored.
- Designers author and save the grip pair after each processing pass.

### Missing grips

- An avatar without a complete, usable grip pair remains pickable.
- Disable the two-hand carry IK for that avatar and use the existing carry presentation until both grips are configured.
- Normal carry and throw behavior, carrier deselection, and the restriction on equipping still apply.

## Integration

- Build on the heavy-item holding implementation used by Boulder (`ItemHoldMode.Heavy`). Reuse its hand-target, finger-pose, first-person arm-frame, and throw-recovery behavior.
- Keep player placement and release physics in the existing player-carry system.
- Integrate deselection and equip restrictions with `PlayerInventory` and existing carry/control transitions. The inventory's existing equip eligibility checks also govern pickup auto-selection.
- Use the existing `AvatarHandTargets` presentation path. Put logic shared by heavy items and player carrying in shared code rather than duplicating it.
- Drive each client's hand presentation from the existing replicated carry relationship, release data, and locally animated avatar grips. Do not introduce per-frame hand-target replication.
- Extend `AvatarProcessor` with the successful-processing authoring reminder.

## Manual acceptance checks

The user performs these checks in Unity:

1. Author both grips on Chill and Genesis Gerbil. Check every carrier/carried-avatar combination in first-person and from another client. Both palms should meet the intended body locations with appropriate finger poses.
2. Observe idle animation and camera movement while carrying. Hands should follow the animated grip points while the existing carry placement remains intact.
3. Charge and throw a player. Charging should add no carried-player pullback; release should retain existing throw physics and show the heavy-style hand follow-through.
4. Begin carrying with an item selected. The carrier's item should disappear from their hands but remain in the same inventory slot. Check hotbar buttons, scroll, cycle, and item-use inputs while carrying.
5. Collect a world item while carrying wherever the existing collection rules permit it. It should enter inventory without equipping. After dropping or throwing the player, the carrier should remain empty-handed until selecting an item.
6. Confirm the carried player's own item behavior is unchanged, including its existing restoration after release.
7. Try an avatar missing one or both grips. Pickup and throwing should remain available, with the existing carry presentation and the same carrier inventory restrictions.
8. Process an avatar with authored grips. Confirm the generated prefab is replaced and the completion log identifies the prefab and both grip names, with a clear instruction to re-author them. Re-author and save the pair before checking its two-hand presentation again.
