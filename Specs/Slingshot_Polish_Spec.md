# Slingshot Polish Specification

## Purpose

Correct world collisions and pebble presentation errors, improve the slingshot grip and charging pose, and give the bands a smooth rest shape and visible release motion. Support both first-person hands and remote avatars through the existing held-item presentation system.

## World collisions

- Enable the global `ItemWorld` ↔ `Environment` pair in **Project Settings → Physics → Layer Collision Matrix**. This setting is stored in `ProjectSettings/DynamicsManager.asset`.
- Apply this policy to all objects on `ItemWorld`, including pebbles and loose world items. Do not substitute a pebble-only collision override for the global change.
- Preserve the enabled `Ground` and `Default` pairs and the remaining collision matrix settings.
- Keep existing held-item and temporary per-object collision suppression behavior.

## Pebble contact handling

Pebble overlap checks must support the non-convex `MeshCollider` geometry used by the world. Replace the unsupported `Collider.ClosestPoint` checks in `PebbleProjectile` with compatible overlap queries, using the existing sphere-overlap approach where appropriate.

Preserve shooter separation handling, contact deduplication, the existing separation tolerance, and the current bounce lifecycle, including removal after the second impact. The contact fix must not change projectile trajectory or impact rules.

## Pebble rotation error

Correct rotation omission handling throughout pebble motion serialization, interpolation, and presentation. An omitted rotation must never become an invalid quaternion that is interpolated or assigned to a `Rigidbody`.

- Omit rotation consistently from every pebble transport path: fire, spawn, movement snapshots, baselines, shooter-separation updates, impact transitions, and terminal updates.
- Fire serialization must honor rotation omission; setting an omission flag is insufficient if the serializer still writes rotation or discards the flag.
- Interpolation must respect whether each input contains rotation. It must not interpolate an omitted zero quaternion with a supplied rotation and then treat the result as valid.
- Initialize reused pebble bodies with a valid local rotation and reset angular velocity for each shot.
- Add no cosmetic tumble animation or new rotation-inference system. Existing shooter-side physics may rotate pebbles naturally.
- Preserve position and velocity presentation, and preserve normal rotation handling for other world items that use it.

The relevant existing boundaries are `PebbleProjectile`, `PebbleRegistry`, pebble message serialization, and shared `RigidbodyMotionState` / `ItemMotion` handling. Fix the omission contract rather than relying only on normalization immediately before assignment.

## Holding-hand grip

- Rotate the holding wrist 90 degrees so its palm faces toward the avatar's own left, in both first-person and remote presentation.
- Apply the corrected grip throughout holding, charging, and recovery.
- Compensate the item grip rotation and position so the slingshot remains upright and sits in the hand rather than intersecting the palm center.
- Preserve the pulling hand's existing orientation.

Use the existing `FirstPersonPose`, `HandPose`, and grip settings in the slingshot item definition where they fit these requirements.

## Charging pose

Charge movement must not introduce an upward lift. Blend into the charged pose as charge increases.

### First person

- At full charge, place the midpoint between `LeftFork` and `RightFork` on the rendered camera's horizontal centerline.
- Preserve the uncharged pose's camera-relative height and depth during charging. The charge translation is sideways only.
- Account for avatar proportions, grip offsets, and fork anchor placement so centering remains correct across avatar types. A single fixed shoulder-relative offset is not sufficient.

### Remote avatars

- Center the charged slingshot relative to the avatar's body using its shoulder measurements.
- Extend the holding arm nearly straight, retaining a small elbow bend.
- Preserve the band draw distance. Allow the pulling arm to remain more bent so its hand stays attached to the pouch.
- Follow the existing replicated look direction, including pitch. This may approximate the actual shot direction; exact convergence on the owner's camera raycast target is unnecessary.
- Reuse the available look yaw and pitch. Add no network messages or payload fields for this pose.
- Relax held-item reach limits only where needed for the charged slingshot pose. Preserve other items' reach behavior and the local pose's height/depth requirements.

Retain the existing held-item pose and avatar IK systems, with the targeted centering and reach adjustments needed for this behavior.

## Band shape and motion

Use the existing two `LineRenderer` components with additional sampled points.

### Idle and charging

- At rest, form smooth curves with a gentle downward sag between the forks and pouch. Avoid jagged segments or an angular relaxed shape.
- Base the rest shape on the authored anchors. A disengaged pulling hand must not displace the resting pouch through an arm-reach clamp.
- Progressively remove slack as the band is drawn, reaching a taut shape at full charge.
- Keep the pulling hand attached to the pouch while drawing.

### Release and cancellation

- After firing, show a clearly visible elastic wobble that diminishes and settles completely into the relaxed shape in roughly 0.3–0.4 seconds.
- Scale wobble strength with release charge. Weak shots retain a small visible response; stronger shots produce more motion.
- Fit the motion within the existing recovery lifecycle.
- Use existing action timing and `ReleaseArcProgress` data. Add no network messages or payload fields for band motion.
- Keep cancellation's existing smooth return without the firing wobble.
- Keep band motion cosmetic; it must not affect the projectile's trajectory.

## Model replacement contract

`SlingshotPresentation` must remain independent of mesh names, mesh-renderer names, and the temporary model's visible geometry.

- Retain explicitly assigned `LeftFork`, `RightFork`, `RestCenter`, `DrawCenter`, and `LoadedPebble` transforms, plus the two band `LineRenderer` references.
- When replacing the model in the prefab, preserve or reassign these references and adjust their positions and grip settings to fit the new geometry.
- Preserve the existing `WorldItem.visualRoot` wrapper; place model-specific import transforms beneath it.
- Ordinary model replacement must not require changes to presentation code when the anchors and references remain correctly configured.

Use the existing anchor contract. This scope does not require a new model-discovery system, runtime model swapping, new prefabs, new components, or game-scene edits.

## User acceptance checks

Perform these checks in Unity and in a host/client session with a remote client running a standalone build. Check both avatar types where pose proportions matter.

| Area | Expected result |
| --- | --- |
| Global collisions | Pebbles and representative loose `ItemWorld` objects, such as rocks, basketballs, and dropped slingshots, collide with Environment walls and ramps. Ground and Default collisions still work. |
| Contacts | Pebbles contact non-convex world geometry without the `Physics.ClosestPoint` error. Rebounds and second-impact removal retain their existing behavior. |
| Grip | Local and remote holding palms face the avatar's left; the slingshot stays upright and seated in the hand through idle, charge, and recovery. |
| Local charging | The fork midpoint reaches the horizontal screen centerline without rising or changing camera distance, across both avatar types. |
| Remote charging | The slingshot centers on the body and follows visible look direction when aiming level, up, and down. The holding elbow retains a slight bend and the pulling hand stays on the pouch. |
| Bands | Idle curves sag gently; drawing removes slack smoothly; weak and strong shots show appropriately scaled wobble that settles within roughly 0.3–0.4 seconds. Cancellation returns smoothly. |
| Pebble presentation | A remote observer sees stable launches and impacts. Standalone client logs contain no unit-quaternion errors during firing, shooter separation, impacts, or repeated shots using pooled pebbles. |
| Replacement model | When the replacement model is authored, correctly assigned anchors and renderers preserve grip, draw, and band behavior regardless of the visible meshes' names. |
