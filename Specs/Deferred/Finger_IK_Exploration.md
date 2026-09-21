# Finger IK Exploration

## Recommendation

**Finger conformance is viable for this game, and a constrained implementation should have a modest runtime cost.** The main difficulty is producing believable thumb placement and contact across different hands and objects. It is not the number of finger bones.

Start with **contact-limited finger curling**, using simple representations of the object's surface and a cached result for each grip. Add authored exceptions for awkward objects. Full fingertip IK is a possible later extension, but it does not automatically choose good contact points or prevent fingers passing through the object.

This recommendation assumes cosmetic right-hand gripping of rigid held items. Physically simulated grasping, arbitrary moving/deforming objects, and two-handed interaction are substantially larger features. Performance expectations below are engineering estimates based on bounded work, not measured frame times.

## Fit with the current project

| Existing feature | Consequence for finger conformance |
| --- | --- |
| Both avatar prefabs reference all 15 right-hand finger joints: three each for thumb, index, middle, ring, and little finger. | There are usable skeletal mappings to start from. Mapping alone does not establish skinning quality or usable curl limits. See the humanoid maps in [Chill](Assets/Game/Prefabs/Avatars/44816e438f2e4775.prefab) and [Genesis Gerbil](Assets/Game/Prefabs/Avatars/461d9592f370666a.prefab). |
| [AvatarInstance.Stage](Assets/Game/Runtime/Avatars/AvatarInstance.cs) already caches every available humanoid bone in `AvatarBinding`. | Reuse those references; no bone-name searches during animation. Missing fingers on future avatars can be skipped. |
| Items attach to a wrist-based palm frame, with separate item grip offsets. | Finger motion can leave the palm and item attachment intact. There is no need for finger motion to reposition the object. |
| The item-to-palm transform stays fixed during holding and charging. | Solve in palm space when the item, avatar, grip, or scale changes; reuse the result while the arm moves and the wrist rotates. |
| [WorldItem](Assets/Game/Runtime/Items/WorldItem.cs) disables item colliders while held. | A solution that raycasts against the live held item's ordinary colliders does not fit the current lifecycle. |
| The avatar graph is evaluated manually, followed by UniVRM processing and a centralized spring update. | Finger posing needs an explicit place in that evaluation sequence. A separate, unordered `LateUpdate` is unsuitable. |
| [SessionController](Assets/Game/Runtime/Networking/SessionController.cs) caps sessions at eight players; [PlayerAvatarPresentation](Assets/Game/Runtime/Player/PlayerAvatarPresentation.cs) hides the owner's avatar. | Normal gameplay has at most seven visible remote gripping hands per client. Avatar swaps temporarily evaluate an additional candidate. Local first-person fingers would require a visible hand rig separately. |

Unity's built-in `AvatarIKGoal` only provides hands and feet, so there is no built-in fingertip target to turn on. Finger bones can nevertheless be posed directly, including through `Animator.SetBoneLocalRotation` during an IK pass. See [AvatarIKGoal](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/AvatarIKGoal.html) and [SetBoneLocalRotation](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Animator.SetBoneLocalRotation.html).

## Approaches worth considering

| Approach | Visual result | Runtime work | Engineering cost / limitation |
| --- | --- | --- | --- |
| Authored open, cupped, and gripping poses | Consistent stylized hands; approximate contact | Blend up to 15 rotations per active hand | Smallest scope. Needs avatar calibration and item pose selection; does not automatically conform to arbitrary surfaces. |
| Contact-limited curl against simple shapes | Fingers close until they meet the object | Fit on grip changes, then apply cached rotations | **Recommended first implementation.** Moderate scope; limited by grip-shape accuracy and finger curl paths. |
| Constrained fingertip IK against chosen surface targets | More control over individual contact and reach | Small bounded solver per finger; cache stable solutions | Higher scope. Needs contact selection, anatomical constraints, intermediate-segment collision checks, and thumb handling. |
| Full mesh contact and physically simulated fingers | Potentially richer interactions | More collision detection and potentially iterative physics | Largest scope, with jitter and tuning risks. Disproportionate to cosmetic remote-player gripping. |

LeanTween remains useful for blending grip strength on equip and opening fingers on release. It does not solve surface contact or joint constraints. Prefer its easing functions on the existing action clock, as with the current hand presentation.

## Proposed first implementation

### 1. Describe the hand

Extend avatar processing to collect finger rest rotations, segment lengths, curl axes, joint limits, and fingertip offsets. Store these with the generated avatar data, and cache the mapped transforms at binding time.

Do not assume every imported finger curls around its local X axis. Derive a consistent frame from the skeleton and palm, then allow avatar-specific corrections where needed. The thumb needs its own opposition direction: moving it across the palm is a different motion from bending the other fingers.

The distal bone is a joint, not the skin's fingertip. Use an existing terminal child where appropriate, or a virtual tip offset. A conservative finger thickness is also needed; bone centerlines touching the item would put part of the visible finger inside it. Neither virtual tips nor thickness require new GameObjects or physics colliders.

### 2. Describe the object's gripping surface

Use a small set of spheres, capsules, or oriented boxes, stored as item data in item-local coordinates. Transform that data into palm space using the existing grip rotation, position, and actual item scale. Item Setup can initialize it from existing collider parameters or mesh bounds, with authorable corrections.

| Current item | Useful starting point | Expected limitation |
| --- | --- | --- |
| Basketball | Existing sphere shape | Fingers can cup the near surface but cannot close around the whole ball. Its size is not an IK failure. |
| Rock | Existing sphere as a first approximation | The irregular mesh may need a better fitted shape or an authored pose to remove visible gaps. |
| Mushroom | A stem capsule plus a cap approximation | Its existing box collider does not identify a stem grip. A stem grip also requires a suitable item-to-palm offset. |

The generated bounds used for item placement provide a conservative envelope, not a contact surface. A hand fitted to the mushroom's bounding box can float visibly away from its stem. Likewise, a general nearest-point query cannot decide whether a player should hold a handle, stem, rim, or body.

Keep this geometry independent of the live physics collider's enabled state. Unity documents that `Collider.ClosestPoint` returns the supplied position when the collider is disabled; an interior query also does not provide the exterior surface point needed for a useful separation direction. Analytic grip shapes avoid those dependencies. See [Collider.ClosestPoint](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Collider.ClosestPoint.html).

### 3. Fit a bounded curl pose

On a grip change, start from a calibrated open or cupped pose and progressively close each finger along an anatomically limited path. Represent its phalanges as virtual capsules and stop before they penetrate the grip surface. Check intermediate segments as well as the fingertip.

A first version can use one closure parameter per finger, mapping it to the three joint rotations. This is inexpensive but may stop the entire finger when its first segment touches. If that looks too stiff, refine the fit per joint so unblocked distal joints can continue curling. Give the thumb a separate opposition preset and a limited closure search.

Use a fixed sample/iteration budget. Find the first contact interval along the closure path before refining it; do not assume collision stays monotonic over the entire range. If no useful contact is reachable, keep a comfortable partial pose. Do not stretch bones or force all fingertips to touch.

Store the result in the avatar's presentation state, keyed by the item definition, effective grip/scale, and avatar binding. Do not write avatar-specific results into a shared `ItemDefinition`. Discard or refresh the fit on rebind, item changes, or authored-data changes.

### 4. Apply and blend the pose in the existing evaluation pipeline

Keep contact fitting separate from pose application. The contact solution can be cached, but the animation graph may rewrite the fingers every evaluation; applying cached local rotations each animated frame is still necessary.

For a small custom implementation, apply the finger pose inside [AvatarInstance.Evaluate](Assets/Game/Runtime/Avatars/AvatarInstance.cs), after `graph.Evaluate` and `runtime.Process`, before the centralized spring batch. Blend against that frame's animated local rotations, rather than multiplying another curl onto the previous frame's result. This keeps locomotion as the underlying pose and avoids accumulated drift.

An alternative is `SetBoneLocalRotation` inside the existing IK pass. In either integration, account for later bone writers. The installed UniVRM runtime processes its optional control rig and constraints after the animation graph; [AvatarProcessor.CheckWriters](Assets/Game/Editor/AvatarProcessor.cs) already rejects constraints and spring chains that overwrite mapped humanoid bones. Preserve that ownership rule for fingers.

Open the fingers when the action enters recovery, even while the arm briefly follows the projectile. Do not keep chasing the released object's surface. Use the existing action age to reconstruct opening for delayed observers. Restore the underlying animation on unequip, cancellation where appropriate, and avatar teardown; skip pose work once its influence reaches zero.

### 5. Keep the feature cosmetic and local

Each client already receives item selection, avatar identity, and throw-action transitions. These are sufficient to choose the same grip procedure and release timing locally. Additional per-finger network messages are unnecessary for this scope.

Shared content, bounded algorithms, and deterministic initialization should produce visually consistent grips, although floating-point calculations need not be bit-identical. Finger results should not alter throw strength, release clearance, hit detection, or inventory authority.

## Computational cost

The favorable property is **a stable relative grip**. Rotating the wrist 180 degrees during charging moves the hand and object together. That does not require solving finger contact again.

Illustrative work counts for one active hand:

- Applying a cached grip: at most **15 local rotations**, plus blend calculations, each evaluated frame.
- A coarse curl fit: **5 fingers × 8 candidate poses × 3 segment checks × 2 grip shapes = 240 simple geometric checks** when the grip changes.
- Refining individual joints, sampling thumb opposition, and refining contact intervals adds work. These counts describe a proposed bounded search, not a finished algorithm or a timing prediction.

For seven remote players, cached application is at most **105 rotations per frame**. If all seven change grips together, the coarse example is **1,680 geometric checks** for that change. Scheduling those fits across a small number of frames or caching previously used avatar/item pairs is available if transition spikes warrant it.

This is primarily CPU work. Reposing bones on the existing skinned hand does not require new rendered meshes or draw calls. The RTX 3080 Ti is therefore not the deciding factor; CPU animation evaluation, geometry queries, and allocation behavior matter more. The Ryzen 9 3900X makes this bounded workload a reasonable candidate, but hardware specifications do not establish a millisecond cost.

Avoid these expensive patterns:

- Re-solving unchanged grips every frame, including every charge frame.
- Rebuilding mesh collision data or baking skinned meshes while holding.
- Searching hierarchy, allocating candidate arrays, or using scene-wide physics queries per finger.
- Adding physical joints and colliders to all fingers solely for a visual effect.
- Running full-quality contact searches on every distant avatar.

Start with allocation-free cached arrays and main-thread math. Jobs/Burst are an option if later measurements justify them, not a prerequisite for seven hands. If distance-based simplification is needed, stop refining contacts first; merely reducing bone writes can let the Animator overwrite the held pose on intervening frames.

A future CPU profiling pass should separate grip fitting, pose application, and existing avatar evaluation, with seven visible holders and simultaneous item swaps. Any FPS or millisecond promise needs that measurement; operation counts alone cannot supply it.

## Would a generic IK package help?

Unity Animation Rigging is absent from the current [package manifest](Packages/manifest.json). Its Chain IK constraint can solve a bone chain toward a target with bounded iterations and a distance tolerance. It could handle finger chains, but target selection, finger limits, collision avoidance, and thumb opposition remain game-specific work. See [Chain IK](https://docs.unity3d.com/Packages/com.unity.animation.rigging@1.3/manual/constraints/ChainIKConstraint.html).

The package supports building nodes into an external `PlayableGraph`, so the project's manual graph is compatible in principle. Integration still needs deliberate graph ownership, update ordering, and avatar-rebind cleanup. The usual component workflow also adds a rig hierarchy and targets; the small custom curl approach can use data and existing bones instead. See [RigBuilder.Build(PlayableGraph)](https://docs.unity3d.com/Packages/com.unity.animation.rigging@1.3/api/UnityEngine.Animations.Rigging.RigBuilder.html).

For more precise fingertip placement, constrained CCD or FABRIK are plausible solvers. FABRIK is an iterative geometric chain solver with extensions for joint constraints. Reaching a tip target alone does not establish a valid grasp: the rest of the finger can still intersect the object, and several reachable poses can look anatomically wrong. See the authors' [FABRIK research and constraint extension](https://www.andreasaristidou.com/FABRIK).

**Recommendation:** do not add Animation Rigging solely to get an automatic-grasp feature; it does not supply that whole feature. Reconsider it if the game also needs broader procedural rigging or explicit fingertip targets.

## Implementation scope and decisions

| Stage | Concrete work | Relative effort |
| --- | --- | --- |
| Pose foundation | Calibrate both avatar hands, establish reliable finger application, blend gripping/opening, handle missing bones and rebinding | Small to moderate; establishes animation integration before surface fitting |
| Useful conformance | Add item grip geometry, a shared bounded curl solver, cached results, and contact margins; tune basketball and rock | Moderate; the recommended feature scope |
| Awkward props | Author mushroom stem contact, thumb corrections, and fallback poses | Content-dependent; requires visual judgment |
| Arbitrary mesh grasping | Select contacts, enforce full-chain constraints, handle concavity and finger self-collision, possibly reposition the palm | Large; a separate interaction feature |

Likely code touchpoints are `AvatarProcessor` / `AvatarSettings` for hand calibration, `ItemSetup` / `ItemDefinition` for grip geometry, a shared finger solver for fitting, and `AvatarInstance` / `PlayerHeldItemPresentation` for pose application and lifecycle. Shared blend defaults can live in `HeldItemSettings`; item surface data and exceptions must remain per item. Data stored in the existing assets and virtual finger segments can avoid scene edits and additional prefab components.

Open decisions:

- Is a convincing stylized cup sufficient, or must individual fingertip pads visibly contact the surface at close range?
- Should the mushroom keep its current palm placement or be repositioned for a stem grip?
- How much per-avatar thumb/curl authoring is acceptable for future imported avatars?
- Should very large props remain one-handed? Finger IK cannot make an anatomically impossible grip possible.
- Is local first-person hand rendering planned? It would require closer scrutiny of deformation and contact than remote presentation alone.

## Visual acceptance checks for a future implementation

- Inspect both avatars close up: fingertips and knuckles should bend naturally, the thumb should oppose the fingers, and skin should not collapse.
- Compare the rock, basketball, and mushroom from front, side, and palm views. Check intermediate finger segments, not just tip contact; a large ball should be cupped rather than crushed into a fist.
- Hold still, walk, charge through the 180-degree wrist turn, and release. Contact should remain stable during charging and fingers should open without following the departing item.
- Switch items and avatars, cancel a charge, and repeat with delayed remote observations. Look for stale grips, one-frame animation resets, and sudden curls.
- Compare ordinary gameplay distance with a close-up before committing to the added complexity of full fingertip IK.
