# Authoring item grips and hand poses

A hand pose has three parts: where the palm sits on the object, how the fingers curl, and where the arm holds the object relative to the body. Author these separately, then connect them through the fields below. Local first-person arms and remote avatars share the palm convention and finger clips.

## What you plug into

| What you want to author | Where to assign it |
| --- | --- |
| How an inventory item fits in the right palm | `ItemDefinition` > **Grip Position** and **Grip Euler** |
| Custom finger curl for that item | `ItemDefinition` > **Grip Fingers** |
| Third-person hold and charge placement | `ItemDefinition` > **Override Third Person / Timing** > **Hold Settings** |
| First-person hold and charge placement | `ItemDefinition` > **Override First Person Pose** > **First Person Spatial Pose** |
| A palm point on an object's handle | Child transform with `AvatarHandContact`; set **Hand** and optional **Fingers** |
| Activation of one or both world-object contacts | The object's presentation integration, connected to `PlayerHandPresentation` and `AvatarPresentation.HandTargets` |
| A supporting hand on an inventory item | Requires an extension to `PlayerHeldItemPresentation`; there is no second-grip Inspector field yet. |

The existing inventory workflow drives the right hand. Left-hand-only and two-handed inventory holding need runtime integration. The underlying IK supports both hands, but adding points or a clip alone does not activate another hand.

## 1. Create a new inventory item

1. Open **Two Birds > Item Setup**.
2. Assign the model or prefab to **Source**, enter **Item Name**, choose the appropriate item options, and click **Create Item**.
3. Open the resulting `ItemDefinition` in `Assets/Game/ScriptableObjects/Items`.
4. Set **Grip Euler** to orient the item in the right palm. For example, orient a handle along the fingers or turn a bottle upright.
5. In **Two Birds > Item Setup**, assign the definition to **Existing Item** and click **Recompute Held Offset** for a starting placement based on mesh bounds.
6. Refine **Grip Position** until the intended surface or handle sits in the palm.
7. Assign your custom finger clip to **Grip Fingers**, using the workflow below. Leave it empty to use the shared grip.

**Grip Position** describes the item origin in metres along the palm's axes. **Grip Euler** describes the item's rotation relative to the palm. These values are shared between first and third person. They do not position the hand relative to the player's shoulder.

**Recompute Held Offset** replaces Grip Position, including manual tuning. Use it before fine adjustment or when deliberately recalculating placement after changing the item's orientation or geometry.

## 2. Author a custom finger pose

Create a static Humanoid animation clip with the desired finger pose at time zero. The runtime uses only the selected hand's finger channels from that clip. Arm, wrist, and body posing belong to the grip and hand-placement settings.

An authoring route using the installed UniHumanoid tools:

1. In a temporary authoring scene, place a working copy of a Humanoid avatar with mapped finger bones. Use a source avatar rather than editing its generated gameplay prefab. See [Avatar setup](Avatar_Setup.md) for avatar processing.
2. Place a copy of the item next to the hand at its intended size as a visual reference.
3. Keep animation playback from overwriting the working pose. Rotate the mapped thumb and finger bones to wrap around the item, including the thumb's opposition and each finger's joints. Pose the right hand for the existing inventory workflow; pose both hands if the clip will supply both grips.
4. On the GameObject with the avatar's Animator, add **UniHumanoid Human Pose Transfer**. Set **Avatar** to the Animator's Humanoid Avatar and leave **Source Type** as **None**.
5. Click **Pose to AnimationClip** and save the `.anim` under `Assets/Art/Animations/Hands`, with an item-specific name such as `LanternGrip.anim`. Choose AnimationClip export; the runtime fields do not accept a HumanPoseClip asset.
6. Assign the exported clip to the item's **Grip Fingers**, or to a contact point's **Fingers**.

Author on the temporary copy; the helper component is not needed on the gameplay item or player. Let Unity create metadata for new clips.

You can use one clip containing poses for both hands, or separate clips for the left and right. Each hand samples its own channels; a right-hand pose is not automatically mirrored into the left hand. Assigning a both-hand clip to an inventory item's **Grip Fingers** still only affects its active right-hand item target.

The runtime holds the clip at time zero and blends between selected finger poses. It does not play a closing animation or automatically wrap fingers around collision geometry. A full-body pose exported by Human Pose Transfer is acceptable because runtime finger masks isolate the appropriate channels.

The shared fallback is **Grip Fingers** on `Assets/Game/Settings/Avatars/AvatarAnimationSet.asset`. Use a per-item or per-contact clip for a special grip so other objects retain their existing poses.

## 3. Set the arm's holding pose

On the item's `ItemDefinition`:

- Enable **Override Third Person / Timing** to expose **Hold Settings**. Tune **Hold Position** and **Hold Wrist Euler** for the resting hold, then the charge positions and rotation if the item uses charging. Despite their names, the wrist-Euler fields describe the desired palm orientation relative to the body.
- Enable **Override First Person Pose** to expose **First Person Spatial Pose** and tune the corresponding local hold/charge placement.
- Leave either override off to use the defaults referenced by the item registry's held-item settings.

Hold and charge positions use shoulder-relative arm lengths: X is right, Y is up, and Z is forward in the body's frame. Grip Position uses metres in the palm frame. Use the holding pose to raise or lower the whole item; use the grip to fit the item within the hand.

## 4. Author one or two palm points on an object

Use explicit points for a world object with a handle, lever, or paired grips. They can also describe the intended support grip for a future two-handed inventory integration.

1. Open the object's prefab in Prefab Mode.
2. Create an empty child at each grip location, named `LeftHandContact` or `RightHandContact`. Parent it beneath the visual part it should follow, including any moving handle.
3. Keep local scale at `(1, 1, 1)`. Parent scale still affects placement, so fit against the final object size.
4. Add `AvatarHandContact`. Explicitly set **Hand** to **Left Hand** or **Right Hand**; the enum's default is a foot goal.
5. Position the transform at the palm's contact surface, rather than the wrist or center of the object.
6. Use Local transform handles to orient it according to the palm axes below. Fit each hand independently.
7. Assign a custom clip to **Fingers** if needed. Start with **Maximum Reach = 0.98** and **Blend Time = 0.15**.
8. Assign the component reference to the object's hand-presentation integration. For two hands, supply both components and activate both when the object is used.

| Point's local axis | Meaning |
| --- | --- |
| +Z / blue | From wrist toward fingers. |
| +Y / green | Out of the palm's gripping surface, toward the surface being held. |
| +X / red | The remaining axis of the palm frame. |

Avatar processing records each skeleton's wrist-to-palm calibration. Author the same palm convention for every avatar; do not copy one model's raw wrist-bone rotation.

### Where these points connect

`AvatarHandContact` is authoring data. It does not discover the user, start IK, or implement blending by itself. `IInteractable` does not bind hands either.

For a new world object, its presentation integration must supply the selected contacts to the player's `PlayerHandPresentation` flow and `AvatarPresentation.HandTargets` using the **Contact** source. That integration selects the user, blends into and out of use, applies the finger fallback and reach settings, positions local shoulders appropriately, and releases references when use ends. It must feed both local-arm and remote-avatar evaluation.

There is no universal Inspector socket for arbitrary usable objects yet. As one existing example, `GolfCartPresentation` exposes **Left Hand Contact** and **Right Hand Contact**; its driving integration already consumes them. A new object type needs equivalent binding before its points become active.

Each hand has its own target selection, with priority **Contact > Item > Free**. Activate only the hand or hands involved in the interaction. Multiple contact systems need coordinated selection because each hand has only one Contact slot.

## 5. Set up a two-handed inventory grip

Author the content using this arrangement:

1. Set the right-hand item attachment through **Grip Position**, **Grip Euler**, and **Grip Fingers** on `ItemDefinition`.
2. Add a left palm point at the supporting grip on the item prefab, following the point-authoring steps above. Set its **Hand** to Left Hand and assign the appropriate **Fingers** clip.
3. Pose both hands in one clip or provide separate clips. Assign the right pose to the item definition and the left pose to the support point.

To make that support point work in gameplay, the developer connects it through **PlayerHeldItemPresentation** into the left hand's **Item** target on **AvatarPresentation.HandTargets**, for both local and remote presentation. The extension needs a serialized reference for the support point and must define when the support hand releases during use, charging, throwing, or unequipping. No such support-point field exists on `ItemDefinition` today.

The right hand continues to determine the held item's attachment; the left hand follows the support grip on that item. The presentation integration must keep those poses coherent so the support target does not lag behind the item or form a circular dependency. Putting an `AvatarHandContact` on the item alone does not provide this integration.

Left-hand-only inventory holding likewise requires changing the existing right-hand-specific item presentation and attachment path. It is not an Inspector hand-selection option at present.

## Reach and visual checks

For **Contact** targets, Maximum Reach is a fraction of upper-arm plus forearm length. At `0.98`, distance influence fades between 90% and 98% of arm length and reaches zero at 98%. The solver measures the required wrist position after palm calibration, so shoulder-to-marker distance alone is only an approximation.

Keep grips comfortably reachable with bent elbows throughout the intended movement. Adjust the holding pose, object arrangement, or body placement when needed. Increasing Maximum Reach above `0.98` cannot extend the arm; the solver caps it there. Contact settings and references may be cached by the integration, so end and restart use after tuning those fields during Play Mode.

Check each supported avatar in local first person and from a second observing client. Confirm the item sits in the palm, fingers wrap without clipping, wrists face correctly, and both hands remain reachable through the full use/charge motion. Equip, use, release, and switch items; any supporting hand should let go at the intended time and return to its normal pose.
