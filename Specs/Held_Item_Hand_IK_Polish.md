# Held Item / Hand IK Polish

## Decisions

| Decision | Justification |
| --- | --- |
| Keep the owner's camera-mounted item presentation; share palm-based release calculations with observers. | The owner has no rendered avatar or first-person hand rig. Enabling avatar IK alone would not add visible local hands. |
| Sample LeanTween easing functions using the existing action clock. | Charge, interruption, late observation, and recovery retain their existing timing without independently running tweens or additional network messages. |
| Use restrained `easeOutBack` for charge and return, `easeOutSine` for the first 80 ms of projectile following, and `easeInOutSine` for IK weight transitions. | Adds overshoot and settling while keeping weights within 0–1. Unclamped pose interpolation preserves the intended bounce. |
| Soften the final 15% of the allowed arm reach, while retaining the existing follow timeout and reach exit. | The hand decelerates near extension instead of hitting a hard positional stop. |
| Return an empty hand toward the item's ordinary hold pose while fading IK. | Recovery includes an arm movement instead of holding the release position while its influence disappears. |
| Replace the configurable right follow bone with a generated palm frame rigidly attached to the wrist. | Wrist pivots sit behind the palm; finger attachments also inherit finger animation. One palm convention makes item tuning portable between humanoids. |
| Roll the charged palm 180 degrees around its finger axis (`ChargedWristEuler = -70, 0, 195`). | Faces the palm and held item toward the throw direction while preserving the raised finger direction and eased charge transition. |
| Calibrate the right hand's bone-to-IK-goal transform once per avatar binding and use a light elbow hint. | Skeleton bone axes can differ from Unity's IK goal axes; the elbow hint reduces unwanted bend changes. |
| Generate item offsets from transformed mesh bounds in Item Setup. | The contact surface, rather than an arbitrary mesh pivot, determines where the object meets the palm. |
| Keep manual `GripPosition` / `GripEuler` adjustments and provide explicit recomputation for existing items. | Bounds are a useful default for props, but cannot identify a handle or intentional grip. Recalculation replaces the position offset and preserves the chosen rotation. |
| Store shared hold and throw poses in `HeldItemSettings`, referenced by the item registry. | One asset controls inherited poses. An item's **Override Hold Settings** checkbox exposes its own pose fields; generated mesh grips always remain per item. |

## Shared hold settings

Edit `Assets/Game/Settings/HeldItemSettings.asset` to tune the system's hold position, palm rotations, charge curve, reach, and recovery timing. `ItemRegistry.HeldItemDefaults` selects the shared asset.

Items inherit these settings unless **Override Hold Settings** is checked in their Inspector. Checking it exposes the item's stored **Hold Settings**. Unchecking it restores inheritance without discarding the override values. **Grip Position** and **Grip Euler** remain visible and item-specific because they describe the mesh's contact with the palm.

Pose settings are cached when an item is equipped or an action begins. Re-equip the item when previewing edits during play.

## Palm convention

Palm-local **+Z points toward the fingers**, **+Y points out of the palm**, and **+X runs across it**. Avatar processing measures the middle knuckle and index-to-little-knuckle span. The attachment sits 60% of the wrist-to-knuckle distance along the hand, plus 12% of that distance toward the palm surface. These proportions are geometric heuristics.

Missing finger mappings fall back to the available knuckles, then a forearm-based hand-length estimate and a palm-down source-pose normal. Finger bones remain optional. The generated frame is stored in wrist-local source units and scaled with the avatar at runtime.

`HoldWristEuler` and `ChargedWristEuler` retain their serialized field names but describe the **palm's** orientation relative to the torso. Shared pose calculations convert that orientation into a wrist target. The existing runtime attachment transform follows the solved wrist; no authored socket, additional component, or scene edit is required.

Generated skeleton format **3** stores `RightWristToPalmPosition` and `RightWristToPalmRotation`. Subsequent avatar processing regenerates these values. The previous `RightHandFollowBone` setting is retired.

Unity documents a standard hand-goal axis convention, which is why passing arbitrary bone rotations directly to IK is insufficient. See [Animator.SetIKRotation](https://docs.unity.com/en-us/engine/6000.7/script-reference/unityengine/animator/setikrotation).

## Item processing

**Create Item** generates the held offset automatically after preparing the visual hierarchy. **Existing Item → Recompute Held Offset** applies the same calculation to an existing definition and supports Undo.

The calculation combines the corners of each enabled mesh renderer's local bounds, including child transforms, prefab root scale, and `GripEuler`. Skinned renderers use their local bounds. It ignores the source object's world translation and rotation.

In palm space, the result is:

`GripPosition = -bounds.center + up * (bounds.extents.y + 0.006 m)`

This centers the object's footprint over the palm and places its lowest bound 6 mm beyond the palm surface. Meshes without usable bounds retain their existing offset. The offset uses world metres, so item size is independent of avatar scale. Recompute after changing meshes, prefab scale, or grip rotation; doing so overwrites a manual position adjustment.

The owner uses the same generated grip when calculating the world-space release pose and clearance. Its camera-mounted display retains its existing position and scale.

## Open decisions

- Should the local camera-mounted item also wind up and bounce, or should local polish wait for a visible first-person hand rig?
- Are the charge/return overshoot and elbow direction appropriate for both avatars, particularly overhead throws?
- Should oversized props use two hands or a different hold position? Bounds prevent palm-plane intersection but do not prevent forearm or torso intersection.
- Do handled or concave items need authored contact points and finger poses? Bounds cannot find a usable handle or conform fingers to the surface.
- Should future items use explicit visual bounds when LODs, skinned bounds, or hidden submeshes make the automatic estimate too conservative?
- Do avatars without mapped fingers need authored palm corrections? The fallback assumes a conventional source pose.

## Visual checks

- Observe rock, mushroom, and basketball holds on both avatars from the side and front. Check palm contact, wrist orientation, elbow direction, and body clearance.
- Compare tap, partial-charge, and full-charge throws. Watch for a small settle during charge, smooth departure, and a natural return with and without another equipped item.
- Cancel charging, switch items during recovery, and repeat while moving, seated, and after an avatar change. Check for pose snaps or stranded attachments.
- Observe another client's throws with latency and a late appearance of its avatar. Check release continuity and that projectile spin does not twist the hand.
- Confirm the local display remains usable and throws near obstructions still release from the expected world position.
- Recompute a held offset after changing grip rotation or prefab scale; inspect contact and exercise Undo.
