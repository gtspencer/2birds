**1. [P2] Finish the first-person pose transition when charging interrupts equip**

Location: [HeldItemPose.cs:157–162](Assets/Game/Runtime/Items/HeldItemPose.cs#L157), with the captured starting pose supplied by [PlayerHeldItemPresentation.cs:418–420](Assets/Game/Runtime/Player/PlayerHeldItemPresentation.cs#L418).

Problem: Select the slingshot from another item and start charging before its 0.2-second equip blend finishes. `chargeFromBlend` makes `hold` use the captured intermediate palm pose. The first-person branch then only translates that pose sideways; it never interpolates its rotation, height, or depth toward the slingshot's normal pose. Consequently, even a fully charged shot retains the interrupted equip pose. For example, the default item palm roll is 15 degrees while the slingshot requires 90 degrees, so switching from a rock can leave the frame substantially tilted throughout the charge.

Why it matters: The holding hand, frame, and pulling-hand orientation depend on exactly when the player presses charge during an ordinary item switch. Releasing then returns to the normal hold pose, exposing the unfinished transition.

Recommended fix: Treat the captured pose as the start of the transition, and calculate the fully charged first-person destination from the slingshot's normal hold pose. Interpolate position and rotation toward that destination while applying the camera-centering offset.

**2. [P2] Preserve a reachable pose when the two arm intervals do not intersect**

Location: [HeldItemPose.cs:179–187](Assets/Game/Runtime/Items/HeldItemPose.cs#L179).

Problem: The third-person calculation first places the fork at the shoulder-center aim plane, then adds forward extension only if both reach intervals exist and intersect. If either interval fails or `near > far`, extension silently becomes zero. At full charge the final lerp uses this position directly. Rapid aim changes can produce this case while the avatar torso is still catching up; the torso deliberately turns more slowly than the look direction in `AvatarPresentation.UpdateInput`.

Why it matters: Crossing the feasibility boundary replaces the previous forward extension with zero in one frame, pulling the visible slingshot toward the torso. `SlingshotReach` only clamps the reported reach fraction, so it does not make this fallback pose reachable or preserve continuity. The pulling hand can also disengage when the resulting pouch exceeds its reach.

Recommended fix: Handle a missing intersection explicitly. Use a reachable fallback based on the holding arm, relaxing the pulling-hand constraint as necessary, and preserve continuity with the last feasible extension instead of snapping to the shoulder-center plane.
