# VRM Avatar Presentation, Animation, and IK Specification

## Objective

Replace remote-player capsule visuals with curated VRM avatars while preserving the existing FishNet gameplay and networking objects. Support runtime avatar replacement, Humanoid locomotion, believable ground contact, head tracking, and native secondary motion.

Establish the avatar identity, asset processing, skeleton access, animation composition, and replacement lifecycle that future held-item posing and local first-person hands will reuse.

## Platform and dependencies

- Unity 6000.5.7f1.
- FishNet 4.7.3.
- Unity Mecanim Humanoid rigs and Avatar assets.
- UniVRM v0.131.2
- Unity Playables for animation orchestration.
- Unity's built-in Humanoid IK as the initial solver.
- Windows and Steam as the target platform.

## Scope

This phase includes:

- A central avatar registry and per-avatar settings.
- Repeatable editor processing of imported VRM assets.
- Stable avatar identity replicated over FishNet.
- Remote avatar rendering and runtime replacement.
- Directional locomotion, jump/fall, seated, and carried presentation.
- Independent foot correction with limited pelvis adjustment.
- Head tracking and smooth visual body follow.
- Compact replication of otherwise unavailable look information.
- Native UniVRM spring bones.
- Extension points for later hand posing, first-person representations, and feature LOD.
- A separate scene demonstrating simultaneous hand/foot IK through avatar swaps.

This phase excludes:

- Production held-item hand poses, rock charge/throw poses, and two-handed item behavior.
- Steering-wheel grips and arms supporting carried players.
- Local first-person hands or a rendered local full body.
- A hidden local avatar instance used only to reserve future skeleton access.
- Gameplay avatar-selection controls, an avatar picker, and saved selection preferences.
- Runtime avatar downloading and arbitrary user-provided VRMs.
- Persistent world-space foot planting, procedural turning steps, and dedicated turn animations.
- A landing animation.
- Standing-on-moving-platform gameplay support.
- Network synchronization of animation clips, normalized times, bones, or IK solutions.
- Initial distance tiers, renderer LOD generation, or reduced animation evaluation rates.

## Supported avatars and visual size

Support a curated set of avatars with valid Unity Mecanim Humanoid rigs and usable limb anatomy. A valid Humanoid mapping alone does not guarantee suitable foot contact or future first-person arm geometry. Permit small per-avatar adjustments and explicit exclusions for unsuitable anatomy or geometry rather than requiring universal compatibility.

VRMs are imported ahead of time.  There is the default one at `Assets/Art/Avatars/138 Chill.vrm`, but move this/edit it as you need. Every client ships with the same registry and avatar content.

Respect each avatar's configured visual height and proportions. Do not normalize every avatar to a universal visual height. Avatar choice and visual scale do not change gameplay collision dimensions, camera height, interaction reach, or throw origin.

Extreme proportions may require presentation compensation or exclusion. Later hand presentation may need to bridge differences between visual anatomy and gameplay attachment positions; it must not silently alter gameplay dimensions to do so.

## Registry and settings

Create a project-level avatar registry as a Unity `ScriptableObject`. Each entry contains:

- A stable opaque `avatarId`.
- A processed avatar prefab or equivalent runtime asset.
- A per-avatar settings `ScriptableObject`.
- Source/lookup metadata needed for repeatable processing and actionable diagnostics.

Assign IDs during editor processing. Preserve them across normal asset moves, renames, and reprocessing. Reprocessing must not assign a new identity unless the developer explicitly requests it. Detect duplicate IDs and missing entries. Runtime lookup must not depend on asset paths or registry ordering.

Replicate the stable identity, not prefab references, paths, or VRM data. Use a compact representation without sacrificing identity stability.

Per-avatar settings must support visual height/scale, alignment offsets, required foot/IK tuning, and animation or retargeting adjustments where needed. Settings must be editable without modifying the imported source VRM and must survive reprocessing.

Allow later extensions for arm reach, hand alignment, first-person offsets, unusual-proportion compensation, and performance tuning. Add fields when they have a consumer; extensibility does not require speculative configuration now.

## Editor processing

Provide `Two Birds > Process Avatar`, following the project's editor-tool conventions. This is a reusable content-authoring workflow.

The processor must:

1. Accept an imported VRM avatar.
2. Check its Humanoid Avatar, required bones, and components.
3. Create or preserve its stable identity.
4. Create its settings asset if needed and preserve existing authored settings.
5. Generate or update the processed prefab/assets needed by runtime presentation.
6. Register the processed avatar and settings centrally.
7. Configure the asset for Playables-driven Humanoid animation and IK.
8. Preserve UniVRM components and data required for spring bones.
9. Preserve complete Humanoid and mesh information needed to derive first-person hands later.
10. Report actionable errors and warnings rather than leaving an apparently usable broken entry.

The workflow must be safe to repeat after source changes. First-person mesh extraction and generation of a dedicated first-person representation are deferred; the processor must remain extendable to perform them later with minimal manual authoring.

## Presentation ownership and lifecycle

The FishNet player owns stable gameplay state and avatar identity. A replaceable presentation instance owns the remote avatar's renderers, skeleton bindings, animation graph, IK, and spring initialization.

Keep visual body follow and avatar sizing separate from gameplay and camera transforms. The exact hierarchy may be adjusted to achieve this boundary without rebuilding the network player. Respect existing smoothing and seated/carried attachment behavior when positioning the presentation.

Expose the resolved avatar settings and active Humanoid skeleton to presentation consumers, with clear bind/unbind or replacement notifications. Cache bones, camera references, and other stable dependencies when initialized or rebound. Prefer state-change events for identity and lifecycle changes; continuous animation and look updates remain frame-driven where necessary.

### Default and runtime replacement

Use a developer-supplied default avatar (available at `Assets/Art/Avatars/138 Chill.vrm`). Provide runtime replacement capability in production code and the demonstration, without adding a gameplay selection UI or developer-facing gameplay swap control.

On an avatar identity change:

1. Resolve the new entry and settings locally.
2. Prepare the replacement while keeping the old avatar visible.
3. Apply configured sizing and initialize its animation, IK bindings, and springs.
4. Bind it to the player's current presentation state.
5. Switch the visible presentation once it is ready and release the previous instance and its owned resources.

Do not recreate the network player, reset gameplay state, or recreate equipped items solely because the avatar changed. Future hand/item consumers must rebind and resume the current equipped/action state.

If resolution or initialization fails, retain the previous avatar and report the failure. If no working avatar exists at initial spawn, display the existing capsule as a development fallback. Gameplay and equipment state continue in either case.

### Local player

Resolve and retain the local player's selected identity, registry entry, processed asset, and settings. Do not instantiate a hidden local skeleton, Animator, IK solver, renderers, or spring simulation in this phase.

The processed asset provides source skeleton and mesh access for later first-person work. That phase will instantiate the representation it needs. Other clients render the same player's complete selected avatar normally. Preserve the current local camera and held-item appearance until first-person presentation is implemented.

## Animation

Use a Unity `PlayableGraph` as the core orchestration layer, with Mecanim Humanoid retargeting. A monolithic Animator Controller is not the orchestration mechanism. An Animator remains the Humanoid animation target.

Use in-place clips. Gameplay movement drives the player; animation root motion must not move the gameplay object.

### Supplied clip set

The developer supplies (at `Assets/Art/Animations`):

| State | Required clips |
| --- | --- |
| Idle | Idle |
| Walk | Forward, backward, left strafe, right strafe |
| Run | Forward, backward, left strafe, right strafe |
| Airborne | Jump for takeoff/ascent, looping fall for descent |
| Seated | Seated pose |

Plan for the complete directional set upfront. Do not depend on forward-only fallbacks as the intended initial presentation. Cardinal clips should have reasonably compatible foot timing within their groups. Blend neighboring directions for diagonal motion; separate diagonal clips are not required.

Use the clips in the animation folder `Assets/Art/Animations`.  Tell me if you need any more.  Feel free to move/edit these as you see fit.

### Locomotion derivation

Derive animation locally from available velocity, movement speed, facing, grounded/airborne state, and attachment context. Reuse gameplay state rather than networking a separate animation state machine.

Preserve look-oriented movement: avatars can strafe or move backward without turning to face their travel direction. Derive directional blending relative to the visual body's facing. Blend walk/run according to movement speed.

Adapt clip playback speed to movement speed and configured avatar scale within sensible limits. Use each clip's intended movement speed and optional per-avatar tuning. Accept residual sliding when a size/speed combination exceeds those limits; do not stretch limbs or alter gameplay movement to force perfect contact.

Distinguish takeoff/ascent from descent and walking off a ledge. Use the fall pose for descent or a ledge fall instead of replaying takeoff whenever a player becomes airborne. On landing, blend back to grounded locomotion without a separate landing clip.

### Attachment states

| Player context | Base presentation | Ground foot IK |
| --- | --- | --- |
| Free, grounded | Idle or directional locomotion | Enabled with contact weighting |
| Jumping/falling | Jump or fall | Fade out |
| Seated | Supplied seated pose | Disabled |
| Carried by another player | Idle pose | Disabled |
| Carrying another player | Normal locomotion/airborne behavior | Follow the carrier's own grounded state |

Seated and carried body placement follows existing attachment anchors. Do not infer ordinary grounded locomotion solely from attachment motion. Carry-supporting arms and steering-wheel grips are later hand-presentation work.

### Composition

Support later masked upper-body animation layers and procedural hand targets without replacing the locomotion architecture. Avatar replacement must rebind the graph and resume the applicable current state.

Complete the presentation root's smoothing/attachment placement before the pose is evaluated for rendering. Apply animation and IK before relevant UniVRM constraint, gaze, expression, and spring processing. Establish one owner for each pose-writing stage so package updates do not overwrite gameplay IK unexpectedly.

## IK strategy and foot contact

Start with Unity's built-in Humanoid IK, using the Playables IK callback mechanism. Use it for remote foot goals, head look, and the demonstration hand target. Ground probing, contact weighting, pelvis adjustment, and visual body-follow policy remain presentation logic.

Animation Rigging is a fallback for a demonstrated need such as explicit constraint ordering that native IK cannot provide. A custom general-purpose solver is not required initially. Later first-person hands may use another solver if their representation requires it.

### Foot behavior

- Solve left and right feet independently over the retargeted locomotion pose.
- Adjust contact position and foot orientation for nearby ground and slopes.
- Account for supported differences in configured height and limb proportions.
- Use limited pelvis adjustment to improve contact without excessive leg extension.
- Respect the animated stride. Ground correction must not pin both feet down throughout every frame of a walking or running cycle.
- Fade correction out in the air and disable it while seated or carried.
- At ledges, high steps, or unreachable contacts, reduce the affected foot's correction rather than stretching limbs or pulling the whole body toward an impossible target.

This is ground alignment, not persistent world-space planting or procedural gait generation. Some sliding is acceptable, including during stationary visual body turns.

Use the existing grounded-movement surface policy for probes, including its Ground/Environment surfaces. Ignore triggers, other players, and loose items. Do not inherit unrelated placement-clearance masks or add new walkable-surface behavior.

Foot IK must not expand the motor's slope, step, or platform capabilities. Freely standing on moving carts/platforms is deferred until gameplay movement supports it; seated attachment is handled separately.

## Head tracking and visual body follow

Remote heads should reflect the player's look direction, including pitch and attached-player free look.

While stationary, allow head rotation within a comfortable range. When look direction exceeds that range, smoothly rotate the visual body until the head can return to a natural orientation. While moving, follow the player's facing direction more closely, retaining look-oriented strafing and backward motion.

Keep visual body rotation independent of camera and gameplay transforms. Do not rotate a seated or carried body away from its attachment to satisfy an extreme look direction; clamp head look within sensible limits in those contexts.

Do not add procedural turning steps or turn clips. Smooth head/body follow with some stationary foot sliding is the accepted initial result.

## Networking

Reuse existing movement, seating, carrying, equipment, and relevant action state. Each client derives the avatar's visible pose locally.

Replicate the stable avatar ID so clients resolve the selected content locally, including when a player first becomes visible or joins a session.

Add only the missing look information needed for remote pitch and seated/carried free look. Reuse existing facing information where sufficient. Keep these updates as small as practical and smooth received look information locally; do not send a head transform or redundant bone data.

Do not replicate clips, normalized animation times, hand/foot targets, bone transforms, IK weights, or solved poses. Prioritize responsive local presentation and consistency across clients. Do not rewrite gameplay authority or introduce anti-cheat machinery solely for avatar presentation.

## Native spring bones and performance

Preserve UniVRM hair, clothing, and accessory secondary motion. Initialize springs with each avatar, apply configured scale before initialization where needed, and reinitialize/rebind correctly after replacement.

Do not run a duplicate hidden local spring system. Ensure feature disabling can actually avoid unnecessary simulation work, rather than merely hiding its visible output.

Target 60 FPS on an AMD Ryzen 9 3900X and NVIDIA RTX 3080 Ti in an eight-player session with all seven remote avatars nearby, using representative curated content with animation, foot IK, and springs active.

Consider Humanoid evaluation, spring simulation, ground probes/IK, and instantiate/swap costs. Keep clean feature-control points for future distance or visibility policies covering springs, IK, and potentially animation evaluation or renderer detail.

Do not implement distance tiers, reduced evaluation rates, or renderer/detail reductions merely to anticipate future needs. Introduce them when measured cost warrants them, without making the architecture depend on every feature running at full fidelity at every distance.

## Foundations for held items and interactions

Future held-item presentation must reuse avatar identity, settings, active skeleton access, animation composition, and replacement notifications. It must preserve existing equipped-item gameplay and consume action state rather than networking arm poses.

Keep the foundation compatible with:

- Right-hand item grips and optional left-hand support grips.
- Item-defined targets, offsets, and elbow guidance where needed.
- Generic interaction hand points, including steering-wheel grips.
- Item-defined procedural hold/action trajectories and optional masked upper-body animation.
- Item defaults with optional avatar overrides, avoiding required per-avatar authoring for every item.
- Rebinding while an item remains equipped or an action remains active.

The intended rock workflow is a later consumer: charge start begins a tunable local movement toward a held throw pose; reaching the limit holds that pose; release ends hand-following and lets existing throw physics proceed. Charge amount does not have to drive the pose. One-handed rocks must not prevent future two-handed items whose grip targets are anchored to the item.

Steering-wheel and other interactable hand targets should be reusable by remote avatars and later local arms. Implementing these production poses is outside this phase; the hand-target demonstration establishes initial composition and rebinding compatibility.

## Foundations for first-person hands

Future local hands/arms derive from the selected avatar rather than a universal hand model. The same identity, registry, settings, source Humanoid data, equipment state, and useful item pose metadata must remain available without requiring a visible local full body.

The later processor extension should prepare the first-person representation automatically or with minimal explicit authoring. Curated avatar support does not imply flawless arm extraction from arbitrary geometry.

First-person and remote representations may use different hierarchies, transforms, camera-relative offsets, trajectories, IK weights, and solvers. First-person readability and responsiveness take priority over matching the remote pose exactly. The later design must address camera clipping/FOV and visible arm geometry without duplicating expensive avatar systems unnecessarily.

A local avatar change must eventually replace/rebind the first-person representation while preserving equipment/action state. First-person bone transforms and animation timing remain entirely local. This phase preserves the necessary data and lifecycle; it does not construct the first-person representation.

## Integration demonstration

Create a separate demonstration scene with:

- Ground surfaces suitable for observing foot correction.
- One hand target.
- Two configured processed avatars.
- A reusable demonstration component that automatically alternates the avatars.

Use production presentation code for both poses and replacement. Position the hand target so the supported avatars can demonstrate meaningful hand contact while their feet are corrected. The demonstration must expose stale skeleton bindings and show that hand/foot IK can operate together through repeated swaps.

This requires a new scene and its objects/component. It does not require editing the game scene or adding gameplay avatar-selection controls. Keep the demonstration reusable, rather than creating a one-time migration tool or a parallel avatar implementation.

## Diagnostics

Provide actionable editor/runtime diagnostics for:

- Unknown avatar IDs and missing registry entries.
- Duplicate IDs.
- Missing processed assets or settings references.
- Invalid/non-Humanoid Avatars and missing required bones/components.
- Failed initialization or replacement.

Apply the previous-avatar/capsule fallback policy consistently. Editor processing checks are a product requirement; they do not imply that implementation work should run unrequested automated validation.

## Implementation constraints

Keep changes focused on the specified presentation behavior. Prefer the simplest appropriate boundaries over a speculative general animation framework. Preserve compatible gameplay systems; replace conflicting presentation assumptions when necessary.

Use Unity CLI where possible and Unity MCP as the fallback for Unity operations. Let Unity generate `.meta` files. Announce required prefab, component, and asset creation before carrying it out, and avoid editing the game scene where practical.

The planned authored/generated assets include the central registry, per-avatar settings, processed avatar assets, and the separate demonstration scene. Do not create one-off editor migration tools.

Use event-driven lifecycle changes, cached references, concise comments, and implicit Unity-object lifetime checks. Do not run tests or other self-validation unless explicitly requested. Developer visual review and performance assessment determine the acceptance results below.

## Acceptance criteria and developer visual review

1. Import and process a curated VRM through `Two Birds > Process Avatar`. Confirm its registry entry and settings exist, and that reprocessing preserves identity and authored tuning.
2. Review differently sized avatars. Their configured visual sizes must be preserved while gameplay collision, camera height, reach, and throw origin remain unchanged.
3. Observe a multiplayer session from different clients. Remote players must display the resolved avatar while the local camera and held-item appearance remain unchanged.
4. Review idle, forward/backward/strafe walking and running, diagonal blends, and short/tall avatars moving at the same gameplay speed. Bounded residual sliding is acceptable; limb stretching and gameplay speed changes are not.
5. Compare jumping, walking off a ledge, long falls, and landing. Ground IK must fade out in the air, and landing must blend back without requiring a landing clip.
6. Review slopes, steps, and ledges. Feet must orient and align independently within reach, with limited pelvis correction and graceful loss of correction for unsupported feet.
7. Turn the view while stationary and moving. Head look and body follow must remain smooth and anatomically reasonable without rotating the camera or gameplay object. Procedural turn steps are not required.
8. Observe seated, carried, and carrying players. The supplied seated pose, carried idle pose, appropriate foot-IK disabling, and attachment-constrained head look must be visible.
9. Review native spring motion and repeated avatar replacement. Animation, IK, and springs must bind to the new skeleton while gameplay/equipment state remains intact.
10. Run the demonstration and observe simultaneous hand/foot IK through automatic swaps between both avatars. Look for stale references, detached targets, pose jumps, or broken spring initialization.
11. Review failed resolution/initialization behavior: preserve a working previous avatar, or show the development capsule on initial failure, with actionable diagnostics.
12. Assess the eight-player performance target with seven nearby remote avatars and representative curated content. This assessment is separate from the single-avatar demonstration.
13. Confirm the foundation provides shared identity/settings, Humanoid access, animation composition, and replacement notifications for future held-item and first-person consumers without implementing their production presentation early.
