# VRM Avatar Presentation Implementation Plan

## 1. Implementation contract

Implement [VRM_Avatar_Presentation_Spec.md](VRM_Avatar_Presentation_Spec.md) using the decisions below. This document defines the initial implementation, numerical defaults, content requirements, integration boundaries, and developer acceptance review. It is not authorization to implement deferred hand/item poses, local arms, avatar-selection UI, or additional gameplay systems.

Preserve Unity 6000.5.7f1, FishNet 4.7.3, and the installed UniVRM v0.131.2 dependencies. Use Playables, Mecanim Humanoid retargeting, and native Humanoid IK. Do not add Animator Controllers, Animation Rigging, Addressables, runtime VRM loading, or a custom limb solver.

Use the existing URP rendering configuration and the avatars' authored MToon appearance. Preserve materials, expressions, meshes, skeletons, and secondary-motion data. Do not change global lighting, post-processing, camera FOV, or gameplay dimensions to accommodate an avatar.

All values below are implementation defaults, not unanswered design questions. Expose only the specified content settings. Further aesthetic tuning belongs to developer visual review; do not substitute unspecified algorithms or leave placeholder behavior.

## 2. Required content and asset layout

### Supplied content

Use these supplied assets for backward locomotion and the second demonstration avatar:

| Input | Asset and implementation requirement |
| --- | --- |
| Backward walk | `Assets/Art/Animations/Walking Backwards.fbx`. Configure as a forward-playing, in-place Humanoid backward walk cycle. |
| Backward run | `Assets/Art/Animations/Running Backward.fbx`. Configure as a forward-playing, in-place Humanoid backward run cycle. |
| Second avatar | `Assets/Art/Avatars/175 Genesis Gerbil.vrm`. Process as the distinct second demonstration avatar, preserving its configured proportions and native secondary-motion data. |

Keep these source paths and Unity GUIDs. Assign the supplied backward clips directly; use Genesis Gerbil alongside Chill in the demonstration. Do not reverse forward clips, duplicate Chill as the second character, download replacement content, or silently omit required states. Apply the processor's content requirements to both avatars; spring-motion acceptance still requires visible native secondary motion.

The default source remains `Assets/Art/Avatars/138 Chill.vrm`. It contains VRM 0.x data and no spring chains. Use UniVRM's existing `VrmScriptedImporter.MigrateToVrm1 = true`; runtime presentation supports the resulting `Vrm10Instance`. Do not install a second VRM 0 runtime or rewrite the source file. An avatar without springs is supported, but it cannot establish spring-motion acceptance.

### Animation assignment

Create one shared `AvatarAnimationSet` at `Assets/Game/Settings/Avatars/AvatarAnimationSet.asset` with exactly these initial slots:

| Slot | Source file under `Assets/Art/Animations` | Loop |
| --- | --- | --- |
| Idle | `idle.fbx` | Yes |
| Walk forward | `walking.fbx` | Yes |
| Walk backward | `Walking Backwards.fbx` | Yes |
| Walk left | `left strafe walking.fbx` | Yes |
| Walk right | `right strafe walking.fbx` | Yes |
| Run forward | `running.fbx` | Yes |
| Run backward | `Running Backward.fbx` | Yes |
| Run left | `left strafe.fbx` | Yes |
| Run right | `right strafe.fbx` | Yes |
| Jump | `jump.fbx` | No |
| Fall | `Falling.fbx` | Yes |
| Seated | `Seated Idle.fbx` | Yes |

The non-walking strafe files are assigned to running. Leave all turn files unused. Do not delete them.

Each slot stores a direct `AnimationClip` reference. Each locomotion slot also stores nominal metres per second, reference Humanoid scale, and a normalized cycle offset. Default cycle offsets to zero. Initialize nominal speed from the unbaked source clip's horizontal `averageSpeed` when it exceeds 0.05 m/s; otherwise use 1.6 m/s for walk and 3.8 m/s for run. Capture these values before changing root-motion import settings, and preserve authored values on subsequent processing. Reference Humanoid scale is the source Animator's `humanScale` at unit model scale. These fields describe source motion; they do not change the player's 5 m/s walk or 8 m/s sprint speeds.

Configure the mapped FBXs through `ModelImporter`: Humanoid, Avatar from that model, animation enabled, animation events empty, no hierarchy optimization, loop time/loop pose as above, original facing retained, and root rotation/XZ/Y motion baked into pose. Import the full source take. For Jump, the animation set additionally stores an ascent sample interval, initially normalized time 0–0.5, played over 0.25 seconds and held at the interval's end while ascending; descent uses Fall. This excludes the second half of a full jump take from the ascent pose. Those three consumed authoring values survive processing and can be adjusted during developer clip review. Reject a missing or ambiguous source take instead of selecting a preview clip or arbitrary subasset. Preserve existing Humanoid mappings. Do not write importer `.meta` YAML directly.

### Asset paths

| Asset | Path / naming rule |
| --- | --- |
| Central registry | `Assets/Game/Settings/Avatars/AvatarRegistry.asset` |
| Shared clips/settings | `Assets/Game/Settings/Avatars/AvatarAnimationSet.asset` |
| Per-avatar settings | `Assets/Game/Settings/Avatars/<avatarId>.asset` |
| Processed prefab | `Assets/Game/Prefabs/Avatars/<avatarId>.prefab` |
| Demonstration scene | `Assets/Scenes/AvatarPresentationDemo.unity` |
| Demonstration materials | `Assets/Game/Materials/AvatarDemo/` |

Use the 16-character hexadecimal ID for generated filenames. Friendly names are inspector labels, not identity. Keep the scene outside the shipping build list. The registry references all shipped processed assets directly; load that small curated set through normal Unity asset dependencies.

## 3. Code boundaries and existing integration points

Use namespace `TwoBirds`, existing assemblies, and files under `Assets/Game/Runtime/Avatars`, except the player adapter and editor files identified below. Do not introduce a service locator, dependency-injection layer, general animation framework, or per-feature managers.

| File / type | Responsibility |
| --- | --- |
| `AvatarId.cs` | Serializable 64-bit identity, equality, hexadecimal formatting, FishNet reader/writer. |
| `AvatarRegistry.cs` | Entries, default identity, shared animation set, one cached ID lookup per registry asset. |
| `AvatarSettings.cs` | Authored tuning plus separately identified generated skeleton/source metadata. |
| `AvatarAnimationSet.cs` | The thirteen clip slots and eight locomotion calibration records. |
| `AvatarPresentation.cs` | Stable non-network presentation host: requested/resolved identity, active instance, body facing, state continuity, replacement notifications, feature switches. |
| `AvatarInstance.cs` | Component on the processed Animator object; cached bindings, initialization/disposal, sole `OnAnimatorIK` callback, and native VRM runtime reference. |
| `AvatarAnimationGraph.cs` | Plain owned C# object implementing the graph, phase clock, weights, and state transitions. |
| `AvatarHumanoidIK.cs` | Plain owned C# object implementing foot contact, pelvis correction, head goals, and optional hand goals. Called only by `AvatarInstance`. |
| `AvatarPresentationSystem.cs` | Scene-local list of active presentation hosts, bounded preparation queue, ordered frame evaluation, and one shared native spring batch. |
| `Assets/Game/Runtime/Player/PlayerAvatarPresentation.cs` | Root `NetworkBehaviour`: identity/look replication, ownership lifecycle, and translation of existing player state into presentation input. |
| `AvatarPresentationDemo.cs` | Standalone scene input, hand target, and automatic replacement using the production host. |
| `Assets/Game/Editor/AvatarProcessor.cs` | Reusable `Two Birds > Process Avatar` editor window and callable processing method. Include shared clip preparation in this workflow. |

Keep small supporting value types in their owning files. There is one animation graph and one IK callback component per instantiated avatar, not per animation state or limb.

Integrate surgically with these existing files:

| Existing file | Required integration |
| --- | --- |
| `PlayerPresentation.cs` | Keep `Graphics`, camera, eye height, reset behavior, and release smoothing. Replace broad renderer collection/toggling with an explicitly assigned capsule/eyes fallback renderer list. Expose `SetFallbackVisible(bool)`; only remote clients may show it. |
| `PlayerMotor.cs` | Expose the existing settings' ground mask and walk/sprint speeds read-only. Continue using `Body.linearVelocity`, `Grounded`, `Mode`, `ControlRevision`, and `ResetRevision`. No movement, prediction, collision, or reconcile changes. |
| `PlayerSeating.cs` | Publish a small `PresentationContextChanged` event after `Apply` has installed the complete control transition, and when unresolved attachment context begins/ends. Expose the owner's attachment-relative look yaw without changing input. |
| `PlayerCarry.cs` | Publish presentation context changes for release-preview start/end; retain attachment transforms and throw behavior. |
| `GamePlayerSpawner.cs` | Initialize the avatar adapter with the registry default before `ServerManager.Spawn`, next to `PlayerNetworkState.Initialize`. |
| `PlayerNameLabel.cs` | Accept a presentation-supplied world anchor above the active head. Retain existing text, local hiding, and billboard behavior. Cache the avatar adapter once. |
| `SessionController.cs` | Change protocol from `eight-player-birds-6` to `eight-player-birds-7` because player network behavior and messages change. Steam metadata already uses this constant. |
| `Player.prefab` | Add/wire the avatar adapter and presentation host, registry, mount, and explicit fallback renderers using Unity prefab APIs. |

Do not change `PlayerEquipment`, inventory, item parenting, `EquipSlot`, viewmodel scale, or use/charge state. The current `PlayerSnapshot.LocalVelocity` is relative to the gameplay root, so it is not the input for directional avatar blending. Use world velocity transformed by the visual body's inverse yaw.

## 4. Identity, registry, settings, and processing

### Stable identity

Represent `AvatarId` as two serialized `uint` halves and combine them into a `ulong` for lookup/serialization. Generate a nonzero random 64-bit value from `Guid.NewGuid()` when processing a new source. Check the registry and existing settings for collisions and regenerate before saving. Zero is invalid; it is not shorthand for default.

The settings asset owns the canonical identity and source Unity GUID. A registry entry contains the same ID, the source asset reference/GUID, processed prefab, and settings reference. The processor enforces their agreement. Keep the ID in the settings so an accidentally removed registry entry can be restored without assigning a new identity. Source moves/renames retain Unity GUIDs and therefore retain identity. Reprocessing updates existing assets at their existing paths, preserving their GUIDs.

Do not expose an automatic regenerate-ID option. For an existing source, replacing its identity requires an explicit `Assign New Identity` action in the processor, unavailable during Play Mode, with a confirmation naming the source and old identity. It replaces the canonical settings ID and its registry record together; it does not create a second settings asset claiming the same source. Move ID-named generated files through Unity asset APIs and preserve their asset GUIDs. Ordinary processing can never take that path. Copied settings with duplicate IDs are errors, not permission to silently reassign them.

Build the runtime dictionary once when the registry is first used. Treat duplicate IDs as unresolved for those entries, so lookup never selects an arbitrary duplicate. Reject missing references and invalid IDs with actionable messages. Valid unrelated entries remain usable. Editor edits/reprocessing invalidate the cached dictionary through asset lifecycle callbacks, not a per-frame scan.

### Settings contract

Keep generated metadata separate from authored tuning in the inspector:

| Authored setting | Initial value / meaning |
| --- | --- |
| `VisualHeight` | Measured source renderer height above the rest sole plane on first processing; retain on reprocessing. Uniform scale only. |
| `StandingOffset` | `(0,0,0)` in metres in the visual body's facing space, applied after sole alignment. |
| `SeatedPelvisOffset` | `(0,0,0)` metres relative to the existing seated rider anchor. |
| `CarriedOffset` | `(0,0,0)` metres relative to the existing carried anchor, added to normal standing alignment. |
| `YawOffset` | `0` degrees; import/alignment correction only. Locomotion's logical body forward remains separate from this correction. |
| `PlaybackMultiplier` | `1`; allowed range 0.5–2. |
| Left/right sole-height adjustment | `0` metres; signed correction for shoes or unusual foot geometry. |
| Foot rotation adjustment | Identity per foot, in calibrated Humanoid foot-goal space. |
| Foot correction multiplier | `1`; range 0–1 for curated anatomy needing less correction. |
| Pelvis correction multiplier | `1`; range 0–1. |

Generated metadata includes source GUID/reference, processor format version, unit-scale render bounds and height, rest sole plane, hips/head/shoulder locations relative to that plane, per-leg and per-arm segment lengths, and the source Humanoid Avatar reference. Obtain bones from Humanoid mappings, never by names. Capture the complete rest skeleton and renderer bindings in the processed prefab; do not duplicate every bone into the settings.

Calculate the source sole plane from Humanoid foot goals and Animator foot-bottom heights at unit scale, with toes as optional orientation information. Calculate height from the highest transformed renderer bounds point to that plane. Transform bounds into the common prefab-root space before combining them. Reject nonpositive dimensions, negative/nonuniform root scale, invalid Humanoid mappings, or degenerate limb lengths. Runtime scale is `VisualHeight / SourceHeight`; do not rescale individual bones.

### Processor behavior

Follow `ItemSetup`'s existing `EditorWindow` and `Two Birds` menu convention. The window accepts a project VRM source and shows resolved identity, settings, prefab, diagnostics, and a Process button. It must not accept arbitrary scene objects as a substitute for the tracked source VRM.

Processing performs these steps in order:

1. Resolve the source importer; use VRM 1 migration and explicitly select the Universal Render Pipeline importer. Reimport only if those settings change.
2. Resolve existing settings by source GUID; create settings/identity only for a new source. Report ambiguity if more than one canonical settings asset claims the same source.
3. Inspect the imported asset and an isolated temporary editor instance. Require one Animator with a valid Humanoid Avatar, `Vrm10Instance`, `UniHumanoid.Humanoid`, and at least one usable renderer.
4. Require hips, spine, head, both upper/lower legs and feet, and both upper/lower arms and hands. Chest, neck, shoulders, toes, eyes, and fingers are optional mappings; retain them when present. Do not claim a missing finger mapping blocks this phase.
5. Reject VRM constraints targeting a Humanoid bone used by animation/IK or an ancestor of such a bone. Reject spring chains that write those primary Humanoid bones. Keep compatible twist/accessory constraints and secondary chains. Name the offending node and the authored feature to remove/fix; do not patch package ordering or silently strip it.
6. Collect generated measurements before runtime processing. Preserve authored tuning. Prepare the shared animation set only when requested or a mapped importer requires its defined settings; never overwrite authored clip speeds/phase offsets on a normal avatar reprocess.
7. Build an ordinary processed prefab containing the complete imported model and retained asset references. Unpack only the temporary imported instance needed for saving; do not strip skins, blends, meshes, VRM metadata, spring colliders, or source bone transforms. Do not call VRM first-person mesh splitting.
8. Keep the Animator and add `AvatarInstance` and `Vrm10FastSpringboneRuntimeProvider` to that same object. Set Animator controller null, `applyRootMotion = false`, `cullingMode = AlwaysAnimate`, animation events disabled, and `Vrm10Instance.UpdateType = None`. Clear imported scene gaze targets. Do not create a UniVRM control rig or attach VRM animation playback.
9. Disable imported Unity physics colliders, rigidbodies, cameras, audio sources, and unrelated behavior on the presentation copy. Preserve VRM spring collider components, which are not gameplay colliders. Set visual objects to the existing Player layer, retain renderer/shadow settings, and remove no source data needed by later arms.
10. Save/update the prefab, then settings metadata, then the registry entry. Complete all input checks before committing. On an exception, remove newly created partial assets and retain existing settings/registry references; report the failed stage. Never register a prefab whose preparation failed.

Use `try/finally` to close isolated editor contents and destroy temporary objects. Keep asset content shared; do not generate per-avatar copies of common clips or materials. UniVRM's owned runtime expression material instances are permitted and must be disposed by UniVRM.

Warnings report renderer count, triangles, material slots, and spring joints. Initial content warnings begin above 100,000 triangles, 12 material slots, 8 skinned renderers, or 128 spring joints per avatar. These are review thresholds, not automatic mesh reduction or unsupported performance guarantees.

## 5. Hierarchy, local ownership, and placement

Keep this hierarchy boundary:

```text
Player                         existing network object, motor, collider, gameplay
  PlayerAvatarPresentation     new component on Player
  Graphics                     existing NetworkTickSmoother/attachment target
    capsule renderer           existing development fallback
    Eyes                       existing fallback renderer
    EquipSlot                  existing item anchor
    NameLabel                  existing persistent label
    AvatarMount                new stable AvatarPresentation host, scale 1
      AvatarInstance           replaceable processed prefab and uniform visual scale
```

Never rotate or scale `Graphics` for head/body follow. Its position/rotation continues to come from FishNet smoothing, `PlayerSeating` (execution order 20), `PlayerCarry` (25), and `PlayerPresentation` release handling (50). Maintain logical body yaw as world-space presentation state and apply the appropriate local rotation to `AvatarMount` after these writers finish. Apply import `YawOffset` beneath that logical yaw.

For free and carried presentation, derive the gameplay sole reference from the cached capsule center and half-height in player coordinates. The current capsule is centred at the root with height 2, so its sole is one metre below the smoothed graphics origin. Align the scaled avatar's rest sole plane to that reference and add the context's authored offset. Do not assume that the imported VRM root and gameplay root have the same origin.

For seated presentation, align the retargeted seated hips to `Graphics.position + Graphics.rotation * SeatedPelvisOffset`. Compute a constant per-instance seated-root correction from a bind-time sample of the seated clip. Apply it as seated weight blends in; preserve the clip's subsequent torso motion. Do not anchor the feet to the floor or change cart rider/eye anchors. Attached pitch/roll follows the anchor; only free body yaw has independent follow.

The name label's world anchor is active head position plus `0.12 * VisualHeight` world-up; sample it after animation/IK. Its transform is not a child of a replaceable bone. When no avatar is active, retain its original fallback position. Local labels remain hidden.

Local owners resolve `RequestedId`, registry entry, processed asset, and settings, and receive identity-resolution notifications. They create no `AvatarInstance`, Animator, graph, spring runtime, or invisible skeleton. Pure servers create none either. Remote client instances register for presentation evaluation. Ownership gain disposes the remote instance and hides fallback; ownership loss creates the remote representation from current identity. Never perform visual work twice on a host's server and client paths.

## 6. Runtime identity and look networking

### Avatar identity

Keep identity in a separate reliable `SyncVar<AvatarId>` on `PlayerAvatarPresentation`, rather than enlarging `PublicPlayerState`. Initialize it with the registry default before spawn. Subscribe to changes once; also resolve the initial synchronized value in `OnStartClient`. Coalesce duplicate host/startup notifications.

Expose `RequestAvatar(AvatarId)` for the owner and `SetAvatarServer(AvatarId)` for server-side callers. Requests use an ownership-required reliable ServerRpc. The owner resolves its selected descriptor immediately; the server accepts registered IDs and updates the SyncVar. A rejected unknown request receives a reliable correction to the previous selected ID. No selection input binding, picker, console command, saved preference, or per-frame request is added.

Serialize the ID with FishNet `WriteUInt64Unpacked`/`ReadUInt64Unpacked`: exactly eight identity bytes, excluding SyncVar/RPC framing. Do not use `WriteUInt64`, which is variable-length packed in this FishNet version. Do not allocate strings for network identities or transmit registry indices.

Resolution failure never silently selects a different default. Keep requested/selected identity separate from the actually displayed identity so diagnostics and consumers can distinguish a failed swap from success. Late observers obtain the current identity from the SyncVar baseline.

### Look sample format

Free-player yaw already travels in `MoveInput.Facing` and the smoothed graphics rotation. Send only pitch while free, plus attachment-relative yaw while seated or carried.

Use a small value type and custom FishNet serializer:

| Field | Encoding |
| --- | --- |
| Sequence | Unpacked `ushort`, wrapping monotonic sequence per owner. |
| Control revision | Existing `PlayerMotor.ControlRevision`, unsigned packed integer. |
| Angles | Unpacked `ushort`: bits 0–6 pitch, bits 7–14 attachment-relative yaw, bit 15 attached flag. |

Pitch quantizes Unity camera pitch from -89 to +89 degrees into 0–126; 127 is unused. Attached yaw quantizes the wrapped [-180,180) range into 256 steps. Free samples set yaw bits and attached flag to zero. This is 5–9 payload bytes depending on revision size, before RPC/transport framing.

At most every 50 ms, send an unreliable owner ServerRpc if the quantized value changed. Send a 500 ms heartbeat while unchanged so a lost final sample recovers. Sample after seating/carry placement using the cached local `AimPose`; relative yaw is the signed difference from the attachment's current visual heading. The cadence uses unscaled time. Do not add network look fields to prediction/reconcile data.

The server stores the latest accepted sample and relays it with an unreliable ObserversRpc excluding the owner. On `OnSpawnServer`, send a reliable TargetRpc with the latest sample for that observer, including a neutral sample when none exists. Send an immediate reliable sample at an owner control-context change. Normal heartbeats remain unreliable.

Reject older sequences with wrap-aware comparison. Discard older control revisions. Keep at most one newest sample for a newer revision until the corresponding existing control transition arrives; do not interpret attached yaw against an unrelated anchor. Reset sequence reception on ownership changes, while preserving the current displayed direction until new input arrives. This is ordering/lifecycle handling, not anti-cheat validation.

Smooth received angles toward the newest target using exponential smoothing with 0.06-second half-life and shortest-arc yaw. Start from the baseline rather than blending from zero at spawn. Free yaw follows the already smoothed graphics; attached yaw is relative to the current anchor, so cart turns do not require redundant yaw traffic. Do not extrapolate look velocity. On a stale stream, hold the last look direction; reset on a new identity-independent control context as above.

## 7. Ordered evaluation and replacement lifecycle

### Frame ownership

`AvatarPresentationSystem` runs at execution order 200 in `LateUpdate`. Create it lazily for the first remote avatar or demonstration host; register/unregister hosts through lifecycle events. Local descriptors do not create this system. It caches UniVRM's shared `FastSpringBoneService`, sets its update type to Manual, and owns its batch call. No other avatar component has an autonomous pose-writing `Update`/`LateUpdate`.

Each rendered frame follows this sequence:

1. Existing gameplay smoothing and seated/carried attachment placement finish.
2. The system obtains each host's current input, updates world body yaw, and places the mount.
3. Update graph weights and clip times. Evaluate each active graph exactly once with `DirectorUpdateMode.Manual` and `Evaluate(0)`; the graph's explicit clocks already advanced by frame delta.
4. During evaluation, `AvatarInstance.OnAnimatorIK(0)` reads the retargeted animated pose and applies pelvis, foot, head, and optional hand goals.
5. Supply the gaze target to `Vrm10Runtime.LookAt.LookAtInput`, then call `Vrm10Runtime.Process()` once for compatible constraints, eyes, and expressions. `ControlRig` and `VrmAnimation` remain absent. The shared spring runtime's `Process` is intentionally a no-op.
6. Call `FastSpringBoneService.ManualUpdate(deltaTime)` once, after all avatars' pose work. Complete it before rendering and before disposing any registered spring resources.
7. Update name-label anchors and commit prepared replacements at the visibility boundary.

Use `min(Time.deltaTime, 0.05)` for animation smoothing/phase and spring stepping; reset spring state after a pause/gap above 0.25 seconds. A paused application must not integrate one enormous secondary-motion step. Do not advance animation or run native processing for disabled evaluation hosts.

Native Playables request `OnAnimatorIK` using `SetApplyPlayableIK(true)`; the callback is on the Animator's object and its layer index is zero. Enable it on all animation clip nodes, set `SetApplyFootIK(false)` to avoid a second automatic foot policy, and make the callback idempotent within a host evaluation serial. Do not use Animator Controller IK-pass settings. See [Unity's Playables IK API](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Animations.AnimationClipPlayable.SetApplyPlayableIK.html).

### Replacement transaction

The stable host retains animation phase, state-transition weights, world body yaw, smoothed look, feature flags, and registered hand targets. The replaceable instance owns only skeleton-specific resources. Expose `IdentityResolved`, `WillUnbind`, and `DidBind` events; an immutable binding contains displayed ID, settings, Animator, cached Humanoid bone array, and a monotonically increasing binding generation. Local owners receive `IdentityResolved` but no skeleton events. Consumers may read a binding only until `WillUnbind`; any deferred callback must compare its captured generation to the host's active generation before using bones.

On a changed requested ID:

1. Resolve it immediately. On failure, log once for that failed request and retain the previous instance/descriptor as appropriate.
2. Enqueue preparation with a host request generation. Coalesce rapid requests to the newest one. Globally permit only one candidate instantiation per frame; no unbounded concurrent swaps.
3. Instantiate beneath an inactive staging parent. Keep all candidate renderers disabled, set uniform scale and current placement, and cache all references. Disable automatic Animator/VRM driving before activation. Do not access `Vrm10Instance.Runtime` before scale and rest-pose setup.
4. On the following preparation frame, activate the candidate while hidden, initialize its VRM runtime in the rest pose, build/bind its graph, and bind the host's current state and hand-target references. Ensure the VRM `Start` path cannot initialize first at an incorrect scale.
5. Evaluate the current state, IK, and native stages while hidden. Allow one completed shared spring batch at the correct world pose. The old avatar remains normally animated and visible throughout preparation.
6. Immediately before a later visibility commit, update the candidate from the host's latest phase/context again. If its request generation became obsolete, dispose it instead of publishing it.
7. Invoke `WillUnbind(old)` while old bones are still valid; set active binding and invoke `DidBind(new)`. Enable the new renderers and disable the old renderers in the same frame. Hide the capsule/eyes fallback. No dissolve, alpha crossfade, or one-frame double rendering is required.
8. Destroy the old graph, unregister/dispose its native runtime, and destroy its GameObject after the completed spring batch. Do not destroy shared meshes, materials, clips, Avatar assets, registry entries, items, or the player.

Failures during preparation destroy the candidate and leave the previous visible avatar and bindings intact. Initial failure shows only the existing remote capsule/eyes fallback. Do not retry every frame: retry on a different identity request, a subsequent explicit same-ID request, or registry content rebind. Despawn/scene unload cancels preparation and disposes both active and pending instances. Disposal is idempotent and cannot recreate `vrm.Runtime` by reading the lazy property after it has been disposed.

Use a synchronous Instantiate in the bounded queue; do not claim it is frame-budgeted internally. Splitting subsequent preparation across frames reduces compounded work, but the prefab's Instantiate cost remains a content-dependent spike to assess.

## 8. Locomotion and animation composition

### Input and state precedence

Presentation input is a value struct containing world velocity, smoothed facing/attachment pose, grounded/movement mode, seated/carried/carrying context, look angles, control/reset revisions, and walk/sprint speeds. The network adapter produces it from cached existing components. The demonstration produces the same struct directly.

Select the base state in this order:

1. Unresolved attachment or placement pending: retain the last applicable attached pose, with foot correction off; on initial spawn use idle and no foot correction.
2. Seated: seated clip, foot correction off, body constrained to anchor.
3. Carried without a release preview: idle clip, foot correction off, body constrained to anchor.
4. Release preview: airborne presentation using the existing preview trajectory velocity, not the still-suspended rigidbody velocity.
5. Free and grounded: locomotion, including normal carrier behavior.
6. Other free motion: Jump when entering ascent with world vertical speed above 0.5 m/s; otherwise Fall. Treat `MovementMode.External` as airborne for presentation even if it is near the floor.

Latch the airborne phase: once descending below -0.1 m/s, use Fall until grounded or until vertical velocity crosses from nonpositive to above 0.5 m/s on a later frame. Walking off a ledge enters Fall directly. Joining while ascending starts at the end of Jump's configured ascent interval; joining while descending starts Fall. A normal airborne transition starts at the interval's beginning. Do not replay Jump repeatedly due to a reconcile, an avatar swap, or a single-frame grounded flicker. Require two consecutive presentation frames of grounded state for a landing transition; upward velocity over 0.5 m/s cancels that landing candidate.

### Graph topology

```text
Walk F/B/L/R -> directional mixer --+
                                   +-> walk/run mixer --+
Run  F/B/L/R -> directional mixer --+                    +-> locomotion mixer
Idle ---------------------------------------------------+

Locomotion / Jump / Fall / Seated -> base-state mixer
base-state mixer -> AnimationLayerMixerPlayable -> Animator output
```

Use one layer initially. This final layer mixer is the concrete extension point for later masked upper-body clips; do not implement unused item layers, an upper-body mask asset, or item pose selection now. Hand targets compose in the one native IK callback after the base animation.

Construct nodes once per instance; do not create/disconnect graphs during movement. Use normalized mixer weights. Skip time bookkeeping for irrelevant non-looping states; keep a single host gait phase for all eight directional clips so changing direction or swapping avatars never randomly changes the supporting leg.

### Direction and speed

Transform horizontal world velocity by inverse logical body yaw, ignoring avatar import correction. Let that vector be `(x,z)` and `d = abs(x) + abs(z)`. For `d > 0.001`, use forward `max(z,0)/d`, backward `max(-z,0)/d`, left `max(-x,0)/d`, and right `max(x,0)/d`. Diagonals blend adjacent cardinals. Never rotate the body toward travel direction.

Smooth horizontal speed and directional weights with a 0.06-second half-life. Below 0.05 m/s use idle. Idle-to-motion weight reaches one at 0.25 m/s using SmoothStep. Above this threshold locomotion remains fully weighted even when playback reaches a clamp.

Run weight is `SmoothStep(0,1,InverseLerp(WalkSpeed,SprintSpeed,speed))`; use the existing settings values of 5 and 8 m/s. Sprint input does not directly select animation; slower motion caused by collisions or stamina reflects actual speed.

For each contributing locomotion clip, effective nominal speed is its stored nominal speed multiplied by `(target unit-scale Animator.humanScale * configured uniform scale / stored reference humanScale)`. Cache the target's unit-scale value during binding; do not assume the Animator property itself includes transform scale or multiply that scale twice. Multiply the desired playback ratio `speed / effectiveNominalSpeed` by `PlaybackMultiplier`, then clamp to 0.65–1.8. Compute a common cycles-per-second value by weighting each contributing clip's clamped ratio divided by clip duration across direction and gait weights. Advance the shared normalized gait phase with that value. Set each clip time to `(phase + slot cycle offset) mod 1 * clip duration` and its playable speed to zero.

This synchronizes feet across walk/run/direction blends. It cannot make every diagonal or extreme speed/size combination slide-free. Do not increase the clamp, stretch legs, or lower gameplay speed to hide that tradeoff.

Initialize each player's gait phase from its existing spawn slot divided by eight, and idle phase from the same seed, to avoid a row of perfectly synchronized idles. Phase is local presentation data and is never replicated. Preserve it across replacements.

### Transition durations

| Change | Blend duration |
| --- | --- |
| Idle/direction/walk-run weights | Exponential smoothing above |
| Grounded to Jump/Fall | 0.10 seconds |
| Jump to Fall | 0.12 seconds |
| Landing to locomotion | 0.15 seconds |
| Enter/exit seated or carried pose | 0.18 seconds |

Use smoothstep interpolation from the current weight vector to the next target vector. An interrupted transition starts at its current weights, not the previous state's full weight. Attachment transforms follow the existing placement immediately; the pose and seated-root correction blend. Do not blend position toward a departed seat or carrier.

## 9. Feet, pelvis, head, and demonstration hands

### Foot contact algorithm

Use current retargeted Humanoid IK positions/rotations inside `OnAnimatorIK`. Cache leg transforms/lengths and foot-goal calibration at bind; do not call `GetBoneTransform` during each frame. All metric thresholds below use configured avatar height `H` in world metres.

Each foot performs one downward `Physics.Raycast` from its animated sole position plus `0.12H` world-up, for a distance of `0.24H`. Use exactly the motor's cached `GroundLayers` and `QueryTriggerInteraction.Ignore`. The shipped mask is 8320, Ground plus Environment. It excludes players, loose items, and carts; do not reuse seating-clearance or item-placement masks. Grounded gameplay state gates the solver; probes never make a player grounded.

For a valid hit, construct a sole target at hit point plus `0.003H` along the normal, then convert to the ankle/IK goal using the calibrated sole-to-foot offset and authored sole adjustment. Preserve the animated foot heading projected onto the surface normal. Limit tilt relative to the animated sole to 35 degrees; smoothly reduce weight for surface normals between 45 and 55 degrees from world-up, and reject steeper hits. This is a presentation limit, not a new motor slope limit.

Derive stride contact from each animated sole's height above the uncorrected standing sole reference: full weight below `0.015H`, smooth falloff to zero at `0.075H`. At idle use contact weight one. Blend these envelopes with the same idle/motion weights as the graph. Do not use a fixed both-feet-on switch during a stride, previous-frame solved foot positions, or persistent world-space targets.

Additional per-foot weight factors are:

- Grounded fade: zero immediately on seat/carry; airborne fade-out 0.08 seconds; grounded fade-in 0.12 seconds.
- Contact displacement: full through `0.06H` vertical correction, SmoothStep to zero at `0.12H`; reject beyond that range or a missing hit.
- Reach: full through 98% of cached upper-plus-lower leg length, fading to zero at 103%; clamp the effective goal to 99% reach before applying it, so the solver never stretches to the requested out-of-range point.
- Authored foot correction multiplier.

For pelvis correction, collect only feet whose contact/displacement weights exceed 0.25. Choose the smaller supported vertical correction, multiply by its contact confidence, and clamp to `[-0.06H,+0.025H]`. With no supported foot, target zero. Smooth with a 0.08-second half-life, multiply by the authored pelvis multiplier, and apply to `Animator.bodyPosition` during IK. Recompute each leg's reach factor against the adjusted hip position before setting final foot goals. An unsupported foot cannot lower the pelvis.

Smooth each target's vertical correction and surface normal with a 0.04-second half-life, relative to the current animated foot; never smooth its world XZ position across strides. Fade invalid contacts to zero weight without continuing to chase the last ground point. Reset history on reset revision, attachment change, teleport, or new skeleton. Do not carry a previous skeleton's leg lengths/foot offsets into a replacement.

The steady maximum is two ground queries per active grounded avatar: fourteen for seven remotes. No scene-wide searches, `RaycastAll`, allocations, or per-frame collection building are permitted in this path.

### Head and body follow

Use the player's look orientation, not travel direction. Unity camera pitch is positive downward; respect that convention when constructing the head target.

| Rule | Value |
| --- | --- |
| Stationary yaw comfort region | ±45 degrees from visual body |
| Stationary turn release | Continue turning until residual look yaw is within ±15 degrees |
| Stationary body half-life / maximum speed | 0.16 seconds / 180 degrees per second |
| Moving body half-life / maximum speed | 0.06 seconds / 360 degrees per second |
| Moving threshold | Enter above 0.15 m/s; leave below 0.08 m/s |
| Final head yaw clamp | ±70 degrees |
| Final head pitch clamp | -40 degrees up, +50 degrees down |
| Head-target smoothing half-life | 0.05 seconds |
| Look target distance | `3H` from the animated head |

While free and stationary, latch a body turn when the comfort limit is exceeded and follow smoothed gameplay facing until the release threshold is reached. While moving, follow smoothed gameplay facing continuously. Retain logical yaw as world-space state so rotation inherited from `Graphics` cannot bypass the intended lag.

Seated and carried bodies always follow their full attachment orientation; they never turn to satisfy free look. Clamp the relative head direction in attachment space. On detach initialize free body yaw from the last attached pose and resume follow smoothly.

Set native look weights to overall 1, body 0.15, head 1, eyes 0, clamp 0.5; explicit yaw/pitch clamps remain the primary limits. UniVRM owns eye bones/expression gaze from the same world target after native IK. If the avatar has no eye-look feature, it remains head-only. Do not add blink, lip-sync, facial emotes, or a competing eye-bone writer. The separate weights use [Unity's documented look-weight API](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Animator.SetLookAtWeight.html).

### Hand-target foundation

The host exposes `SetHandTarget(AvatarIKGoal hand, Transform target, float positionWeight, float rotationWeight)` and `ClearHandTarget`. Accept only LeftHand or RightHand. Store target references on the stable host; resolve current hand/arm bones in the candidate's binding. Setting/clearing targets is event-driven. The IK callback reads the target's current world pose while it is active, which supports later moving item/interactable targets.

Apply both feet and any active hand targets in the same IK callback. Default absent hand weights to zero and clear them during unbind. Clamp hand reach to 98% of arm length; fade position weight over the last 10% of reach instead of dragging the torso. Do not add elbow-hint authoring, grip metadata, finger posing, or production equipment integration until a consumer requires it.

## 10. Native springs, feature switches, and performance

Use `Vrm10FastSpringboneRuntimeProvider` on processed avatars so all avatars share UniVRM's job-backed spring service. Do not use the default standalone provider, which schedules/completes work separately for each avatar. The implementation details are in the pinned package's `Runtime/Components/Vrm10Runtime/Springbone/` and `Runtime/FastSpringBone/FastSpringBoneService.cs`.

Apply final uniform scale before the runtime captures rest transforms. Initialize each candidate at its actual mount pose; do not initialize springs at the origin and then teleport them into place. At reset/teleport or a frame gap above 0.25 seconds, restore spring initial transforms and clear simulation history through the package API before the next evaluated frame. A teleport is a changed motor reset revision or a mount translation exceeding 2 metres in one frame, outside continuous attachment motion. Scale changes after binding use the normal prepare-and-replace lifecycle, even for the same avatar ID; do not mutate active spring scale every frame.

Expose `SetFeatures(animation, footIk, headLook, springs)` on the stable host, with all enabled initially. Changes take effect once per transition:

- Foot IK off: zero foot/pelvis weights and skip all probes/contact work.
- Head look off: zero native head look and reset the VRM gaze input to neutral once; skip head target work.
- Springs off: restore spring joint rest transforms, then call the shared spring runtime's `Dispose` to unregister its buffer. Leave animation/expressions bound. Its shared `Process` method is a no-op, so subsequent VRM processing remains safe. On re-enable, call `ReconstructSpringBone()` to register a fresh buffer, then restart at the current pose.
- Animation off: suspend all pose-dependent evaluation, including IK, gaze/expressions, and springs; retain the last rendered pose and host state. Unregister spring work. On resume rebind current state, clear contact history, and reconstruct springs only if requested enabled.

Do not use `StopSpringBoneWriteback` for disabling: it suppresses transform output but still computes the simulation. Do not disable `FastSpringBoneService.enabled` while live avatars own buffers, because its `OnDisable` disposes shared resources. Use Manual mode and omit the batch call when there are no registered spring buffers.

The system destroys instances before releasing its owned spring service at scene teardown. Retain/release the service at system lifetime, not once per avatar swap. If a service pre-existed outside this system, restore its prior update policy instead of destroying someone else's instance. Do not modify files in `Library/PackageCache` or vendor FishNet/UniVRM runtime code.

Performance requirements:

- Exactly zero presentation instances for the local owner and pure server.
- One graph evaluation per remote per rendered frame and one shared spring batch.
- Zero managed allocation per steady frame from project avatar code. Use cached arrays, dictionary lookup only on identity resolution, and no LINQ/closures/formatting in the frame path.
- Create/destroy graphs only for instance lifecycle. Share animation clips, meshes, and static materials; do not access `Renderer.material` during updates.
- Keep renderer bounds large enough for supported retargeted motion and springs. Extend each original skinned local bound by 15% of source avatar height during processing. Keep `updateWhenOffscreen = false`; Animator `AlwaysAnimate` preserves skeleton evaluation. Do not add renderer LOD generation or offscreen animation throttling.
- Add static `ProfilerMarker`s for `Avatar.Prepare`, `Avatar.Evaluate`, `Avatar.IK`, `Avatar.Vrm`, `Avatar.Springs`, and `Avatar.Commit`. Markers do not create a gameplay diagnostics UI.

The release performance goal is 60 FPS, 16.67 ms frame time, with seven nearby visible remotes on the Ryzen 9 3900X / RTX 3080 Ti. Allocate a provisional total CPU budget of 4 ms for avatar evaluation/IK/native processing across all seven, leaving the rest to the game. This is a planning budget to measure, not a result or guarantee. Evaluate GPU time and replacement spikes separately; a small default avatar does not establish the cost of representative spring-heavy content.

If developer measurements exceed the budget, first use the supplied markers to address allocations, duplicate evaluation, expensive content/material counts, or preparation spikes. Do not automatically add distance tiers, reduced evaluation rates, mesh reduction, or a new solver in this phase.

## 11. Demonstration scene

Create the authored scene through Unity CLI scene/object/asset operations, with Unity MCP as fallback. Do not create a one-off scene-generation editor script. The reusable `AvatarPresentationDemo` component is part of the product demonstration, not a migration tool.

Include an orthographic camera at a three-quarter front angle, one soft directional light, a neutral dark background, a matte ground stage, one shallow left-foot ramp, one small step, and one conspicuous hand-target marker. Use existing URP/Lit materials for stage objects. No production HUD, network session, input map, menu, runtime debug overlay, or extra renderer camera is needed.

Use an eight-metre square ground stage at Y=0 on the existing Ground layer. Place the demonstration host's sole reference at world origin. A 15-degree ramp on its left side rises no higher than `0.06 * min(avatar heights)` beneath the left foot; the right side remains flat. Place a separate 0.12-metre step visibly beside the stance for manual repositioning. All contacts use the production ground policy.

Assign two registry IDs: avatar A is `138 Chill.vrm` and avatar B is `175 Genesis Gerbil.vrm`. At scene start, read their generated rest shoulder positions/arm lengths after configured scaling. Choose one shared right-hand world target as the midpoint between their right shoulders plus world offset `(0, -0.30L, 0.35L)`, where `L` is the shorter right-arm length and +Z is avatar forward. Require that this point is within 90% of each arm's reach. If it is not, report the two IDs and unreachable target; the content/settings must be adjusted before accepting the demo. Do not move the target with each newly bound hand.

Orient the marker with forward toward -Z and up toward +Y. Apply right-hand position weight 1 and rotation weight 0.35. Keep the marker and target Transform alive across every swap. The hand goal remains fixed while the feet adapt to the uneven stage. Cache the camera; frame both avatars' configured bounds with 20% margin so neither replacement is cropped.

The demo uses a repeating 16-second cycle: render avatar A for eight seconds, avatar B for eight seconds, repeat. Identity changes call the same `AvatarPresentation` replacement request as production. Maintain a grounded idle input, fixed body facing, and a slowly oscillating look yaw of ±25 degrees over eight seconds. Continue that clock across swaps. Do not reset the hand target, gait/idle clock, or native pose state on each switch.

Keep the automatic showcase focused on simultaneous hand/foot correction, gaze, springs, and replacement. Multiplayer locomotion, attachments, packet behavior, and the seven-remote performance target are reviewed separately in the game. The scene is not a replacement for those reviews.

## 12. Implementation sequence and completion conditions

1. **Data and processing:** implement ID/serialization, registry, settings, clip set, and processor. Process Chill and Genesis Gerbil and prepare their full imported runtime data. Assign the supplied backward clips using the animation table above. Complete means repeated processing preserves IDs/settings and all thirteen slots have explicit assignments.
2. **Stable host and lifecycle:** implement player adapter, hierarchy/fallback wiring, descriptor-only local ownership, binding notifications, preparation queue, and disposal. Complete means replacement can change an instance while the same player/item objects and host presentation state survive.
3. **Graph and frame ordering:** implement manual graph, locomotion/state rules, ordered system, and native VRM processing. Complete means there is one owner for every pose-writing stage and all attachment contexts have the specified base pose.
4. **IK and look:** implement independent feet, bounded pelvis, head/body follow, compact look messages, baselines, and control-revision handling. Complete means no animation/IK/bone state is added to the network stream.
5. **Springs and presentation polish:** use shared native batching, real feature disabling, name-label anchoring, bounds, and coherent replacement warmup. Complete means old/candidate resources cannot leak or write to a later binding.
6. **Demonstration assets:** author the separate scene and wire the reusable component, both IDs, ground, fixed hand target, and automatic alternation. Complete means it uses the production host/graph/IK/replacement path.

Before creating or modifying prefabs, adding components, or creating Unity assets, tell the developer which assets/components are required. This scope requires the player prefab, processed avatar prefabs, registry/settings/animation assets, and separate demonstration scene; it does not require changing `Game.unity`, `MainMenu.unity`, or `SessionRoot.prefab`. Use Unity CLI first, Unity MCP if necessary, and let Unity generate `.meta` files. Do not run the MVP asset generator, which can replace existing scenes and prefabs.

Do not run tests, Play Mode, builds, screenshots, benchmarks, or automated acceptance checks unless the developer explicitly asks. Content checks inside the reusable processor are required product behavior. Ordinary source reading and asset authoring do not authorize an implementation agent to perform its own validation run.

## 13. Developer visual and performance review

The developer performs these reviews after implementation. Do not mark them passed in this document or convert this plan into a work log.

| Review | Required visible behavior |
| --- | --- |
| Process and reprocess | Same identity and authored tuning after rename/move/reprocess; clear messages for unusable source data or missing clips. |
| Local versus remote | Local camera/items unchanged and no local skeleton; other clients render the selected full avatar. |
| Directional movement | Forward/backward/left/right walk and run, smooth diagonals, no travel-facing turn, bounded speed adaptation on short/tall avatars. |
| Airborne motion | Takeoff/ascent distinct from descent; ledge falls skip takeoff; landing blends back without a landing clip. |
| Ground contact | Independent foot tilt/contact on floor, ramp, shallow step, and ledge; swinging feet remain free; no stretching or deep pelvis collapse. |
| Look and body | Natural pitch, comfortable stationary head range, smooth body catch-up, attached free look without rotating the seated/carried body. |
| Attachments | Seated hips align to the configured rider-relative point; carried players use idle without foot correction; carriers still animate normally. |
| Replacement | Old avatar remains until the new one is posed; no T-pose flash, double rendering, lost held item, old-bone references, or spring explosion. |
| Demonstration | Repeated A/B swaps maintain the same fixed hand target while both feet are independently corrected; secondary motion is visible on the spring-bearing avatar. |
| Failure cases | Unknown/missing content keeps the previous working avatar; initial remote failure displays capsule/eyes; local player remains visually unchanged. |
| Network observation | Host/guest/late observer agree on identity and look; final look recovers after a dropped update; seat/carry transitions never apply stale relative yaw. |
| Performance | Assess an eight-player session with all seven remotes nearby at 1920×1080 and the game's existing quality settings. Compare steady CPU/GPU frame times, avatar markers, allocations, memory after repeated swaps, and worst replacement frames. Include representative spring-bearing content. |

Run the visual review from the front, side, and behind, with both supplied avatars at their configured sizes. Review the demonstration and the full multiplayer workload separately.
