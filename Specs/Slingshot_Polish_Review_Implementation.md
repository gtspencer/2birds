# Slingshot Polish Review Implementation

## 1. Interrupted first-person equip transition — fixed

The captured equip pose supplied the charging destination as well as its starting pose, preserving an intermediate grip rotation, height, and depth through full charge.

`PlayerHeldItemPresentation.ChargePose` now supplies the normal hold and captured start separately. `HeldItemPoseCalculation.SlingshotCharge` derives the centered destination from the normal hold, then interpolates both position and rotation from the captured start. Charging from an uninterrupted hold retains its sideways-only first-person movement.

## 2. Missing arm-reach intersection — fixed

The previous interval check discarded forward extension whenever either arm missed the aim line or their intervals did not overlap. Clamping the reported reach fraction did not constrain the actual wrist target.

The calculation now clamps the pulling interval's forward endpoint into the holding interval. Overlapping intervals still yield their furthest shared point; disjoint intervals favor the holding arm. A missed sphere reduces its interval to the closest point on the aim line, so crossing the feasibility boundary no longer switches extension to zero. The final third-person wrist position is constrained to 98% of holding-arm length, including during the charge blend and when the holding sphere misses the line.

The suggested previous-extension cache is omitted: the clamped interval calculation is continuous at the feasibility boundaries and requires no history across repeated pose samples, avatar candidates, or recovery reconstruction. In infeasible poses, body centering and pulling-hand contact may yield to holding-arm reach. Neither finding is rejected.

## Visual checks

- Switch from a rock to the slingshot and immediately charge at several points during equip. Full charge should converge on the same upright, horizontally centered first-person pose; firing or canceling should not expose an unfinished equip rotation.
- On a remote client, observe partial and full charges while the shooter rapidly turns and pitches the view. The frame should not snap back to the shoulder-center plane as reach intervals stop overlapping, and the holding wrist should stay within arm reach. Pulling-hand contact should resume when reachable.
- Repeat with both avatar types, including ordinary charging, cancellation, and recovery.
