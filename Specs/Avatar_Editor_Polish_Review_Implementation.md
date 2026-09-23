# Avatar Editor Polish Review Implementation

## Fixed

### P2: Preserve tattoo framing when switching preview poses

The finding is credible. `AvatarEditorPreview.SetPlacement` evaluates the new pose immediately, moving the selected tattoo's attachment bone without moving the camera target.

`SetPlacement` now captures the selected tattoo's world position before the transition and shifts the camera target by its displacement after pose evaluation. This preserves its screen position while keeping orbit and zoom unchanged. The controller supplies the selection for placement transitions, including the temporary transition used when adding a tattoo and its failure rollback. Transitions without a resolvable tattoo leave the camera target unchanged.

## Not fixed

None. The review contains one finding, which is addressed above.

## Visual checks

- Select an arm or hand tattoo, zoom in closely, and orbit the preview. Enter **Edit placement** and confirm the tattoo stays at the same screen position with its gizmo aligned.
- Exit placement using Back or a panel control. Confirm the pose transition preserves framing, orbit, and zoom.
- Move the tattoo during placement, then exit and re-enter placement. Confirm framing follows the tattoo's new position across both transitions.
