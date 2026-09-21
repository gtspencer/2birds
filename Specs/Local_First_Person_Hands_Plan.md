# Local First-Person Hands Implementation Plan

## Objective and execution contract

Implement [Local_First_Person_Hands_Spec.md](Local_First_Person_Hands_Spec.md). The owner sees arms extracted from the selected VRM; observers retain the complete avatar. Both representations share palm conventions, hand IK, finger poses, item identity, and action timing. First-person placement and movement are independently tuned.

This document is an implementation handoff, not a work log. Paths are relative to the repository root, `C:\Users\spenc\source\repos\2birds`. Read `AGENTS.md` and the specification before implementing. The specification governs product behavior; the decisions below make its implementation concrete.

Follow these constraints throughout:

- Use Unity 6000.5.7f1, FishNet 4.7.3, URP, and the New Input System already installed in the project.
- Use Unity CLI for Unity operations, with Unity MCP as fallback. CLI executable: `C:\Users\spenc\AppData\Local\Unity\bin\unity.exe`. Discover available Editor commands with `unity command --project-path <repository>`, then use the commands supported by that Editor. Do not invent CLI command names.
- Let Unity create `.meta` files. Extend the repeatable avatar processor; do not create one-off migration or finger-authoring tools.
- Announce the content changes listed below before creating assets or adding components. Apply prefab changes without editing the gameplay scene.
- Do not run builds, automated tests, validation commands, or Play Mode unless the user explicitly requests them. Normal import/processing guards belong in the implementation; the acceptance pass below belongs to the user.
- Cache cameras, bones, calibration, dimensions, and context references on binding/start. Use events for identity, equipment, action, and contact ownership changes. Advance continuous animation only during its ordered evaluation.
- Make only feature-related changes. Preserve unrelated working-tree edits. Keep comments brief and use implicit Unity-object lifetime checks.

Scope decisions carried into implementation:

- The existing item hand remains the right hand. The left hand receives free-hand motion unless an authored contact owns it.
- Keep source limb proportions and uniform avatar scaling. Do not stretch bones or resize held items to improve framing.
- Use the existing world camera, FOV, depth testing, and near plane initially. Solve normal framing through shoulder/target placement.
- No two-handed items, passenger/carry contacts, local full-body mesh, local springs, wardrobe pipeline, automatic reaching, or fingertip contact solver.
- No new RPCs, network fields, messages, or transform streams. Presentation helpers are ordinary C# classes or non-network MonoBehaviours.
- Blender is a user-driven cleanup route for unsuitable automatic cuts. Normal onboarding must work through Process Avatar alone.

## Required content and file locations

Existing content to extend:

| Content | Required work |
| --- | --- |
| `Assets/Game/Settings/Avatars/AvatarRegistry.asset` | Keep both identities; add first-person output references through their existing entries and global first-person procedural/placement settings on the registry. |
| `Assets/Game/Settings/Avatars/AvatarAnimationSet.asset` | Add reusable relaxed, grip, and open finger-clip references. Reuse a frozen sample of its Idle clip as the initial local base pose. |
| `Assets/Game/Settings/Avatars/44816e438f2e4775.asset` | Preserve settings for `Assets/Art/Avatars/138 Chill.vrm`; add first-person source override, placement corrections, and generated hand data. |
| `Assets/Game/Settings/Avatars/461d9592f370666a.asset` | Preserve settings for `Assets/Art/Avatars/175 Genesis Gerbil.vrm`; add the same first-person fields. |
| `Assets/Game/Settings/HeldItemSettings.asset` | Add global first-person spatial defaults. Preserve existing third-person settings and shared timing. |
| `Assets/Game/ScriptableObjects/Items/{Rock,Basketball,Mushroom_Amanita}.asset` | Expose optional first-person spatial overrides and finger-clip selection; keep existing grip, throw, and timing values. |
| `Assets/Game/Prefabs/GolfCart.prefab` | Add left/right contact children beneath the visual `steeringWheel`, with contact metadata and references on `GolfCartPresentation`. |

Generate first-person prefabs at `Assets/Game/Prefabs/Avatars/FirstPerson/<avatar-id>.prefab` and mesh assets beneath `Assets/Game/Generated/Avatars/<avatar-id>/FirstPerson/`. Use deterministic renderer identifiers within each generated folder. Save default finger clips under `Assets/Art/Animations/Hands/` as `Relaxed.anim`, `Grip.anim`, and `Open.anim`.

The generated first-person prefab needs a new `LocalFirstPersonHands` component on its Animator root. A small player hand coordinator may be constructed by the existing player presentation lifecycle with an explicit initialization method; it does not require a new scene object or NetworkBehaviour. Prefer runtime creation for this player-side component so `Player.prefab` does not require another serialized dependency. Do not use `AvatarInstance` on local arm prefabs: it requires the remote VRM runtime.

## Code map and integration points

All runtime paths below begin with `Assets/Game/Runtime/`; editor paths begin with `Assets/Game/Editor/`.

| Area | Files and relevant entry points |
| --- | --- |
| Avatar processing | `Editor/AvatarProcessor.cs`: `Process`, `Measure`, `MeasureRightPalm`, `PrepareModel`, `ProcessingChanges`, `AssignNewIdentity`. The processor clones authored settings, then regenerates measurements and the remote prefab. Its rollback currently stores only one prefab. |
| Avatar data/guards | `Avatars/AvatarSettings.cs`: `GeneratedSkeleton`, `CurrentFormatVersion = 3`, `AvatarContentValidation`. `Avatars/AvatarRegistry.cs`: `Entry`, `TryResolve`, `Invalidate`. Current validation requires remote VRM/spring components. |
| Identity/lifecycle | `Player/PlayerAvatarPresentation.cs`: `ResolveSelected`, `RequestAvatar`, ownership callbacks, `CurrentPlacement`. `Avatars/AvatarPresentation.cs`: `IdentityResolved`, `WillUnbind`, `DidBind`, candidate generation, `PrepareTargets`, `Commit`. Owners already resolve identity without enabling remote visuals. |
| Remote evaluation | `Avatars/AvatarInstance.cs`, `AvatarAnimationGraph.cs`, `AvatarHumanoidIK.cs`, `AvatarPresentationSystem.cs`. Manual graph evaluation invokes a guarded `OnAnimatorIK`; the system orders remote evaluation, springs, then commit. |
| Item pose/action | `Items/HeldItemPose.cs`, `HeldItemSettings.cs`, `ItemDefinition.cs`; `Player/PlayerHeldItemPresentation.cs`: `ActionChanged`, `RefreshPose`, `TryPrepareRelease`, `Recover`, `Bind`. This component already owns local and observer action presentation. |
| Item attachment/release | `Player/PlayerEquipment.cs`: `InitializeOwner`, `HeldTransform`; `Items/WorldItem.cs`: `ApplyHeldAttachment`, `ApplyRecord`, `Present`, `CorrectBody`. Owner attachment currently uses a half-scale `ViewmodelSlot`; observers use the palm fallback transform. |
| Gameplay contract | `Inventory/PlayerInventory.cs`: `ReleaseSlot`, `Submit`; `Player/PlayerNetworkState.cs`: `ActionAge`, `PredictRecovery`, `CompleteRecovery`; `Items/WorldItemRegistry.cs` and `.Motion.cs`: pose lookup, prediction, lifecycle, motion presentation. |
| Clearance | `Items/ItemReleaseClearance.cs`: `EnvelopeRadius`, `TryResolve`. Uses the normal-size item envelope, environment mask, accessible path, 0.01 m padding, and a maximum 0.20 m correction. |
| Movement/context | `Player/PlayerMotor.cs`: `Simulated`, `Body`, `Grounded`, `Mode`, reset/control revisions. `Player/PlayerSeating.cs`: `PresentationContextChanged`, `Cart`, `SeatIndex`, `CanEquip`. `Player/PlayerCarry.cs`: context events and release-preview state. |
| Cart | `Vehicles/GolfCartPresentation.cs`: visual steering-wheel rotation. `GolfCartNetwork.cs`: cached presentation and `GetSeat`. `CartSeat.cs`: `VisualRider`, `VisualEye`. Driver seat index is zero. |
| Inspectors/tools | `Editor/AvatarSettingsEditor.cs`, `ItemDefinitionEditor.cs`; `Avatars/AvatarPresentationDemo.cs` also calls the hand-target API. Installed UniHumanoid package provides `MuscleInspector` and `HumanPoseTransfer`. |

The existing LateUpdate order is cart presentation `0`, seating `20`, camera/player presentation `50`, player avatar look updates `100`, world-item presentation `150`, held-item presentation `175`, and remote avatar evaluation `200`. Preserve these dependencies when introducing local evaluation.

## Shared data and ownership design

Implement only the boundaries needed by local and remote hands:

1. **Hand binding and solver:** add `Avatars/AvatarHandIK.cs` with per-hand cached bones, arm length, wrist-to-palm calibration, and Unity wrist-to-IK-goal conversion. It accepts resolved palm targets, weights, and elbow hints without requiring `AvatarPresentation`, feet, head look, locomotion state, or VRM runtime. Remote full-body IK delegates its hand work to this class.
2. **Finger layers:** add `Avatars/AvatarFingerLayers.cs`, used by both graphs. It owns per-hand finger masks, clip mixers, transitions, and cleanup. It does not select gameplay behavior.
3. **Hand target ownership:** add a small shared `AvatarHandTargets` data/helper class. Keep separate item/action, authored-contact, and free-hand candidates for each hand; choose one active behavior per hand. Clear by source, not by hand alone. Contacts and item actions outrank free motion; the existing driver restriction prevents their normal conflict. Give active contacts deterministic precedence if a seat-transition frame contains stale item data, without altering equipment permissions.
4. **Local representation:** add `Avatars/LocalFirstPersonHands.cs`, a minimal manually evaluated graph and bound arms rig. It owns local shoulder placement and free-hand target animation. Keep its graph helper in that file unless a separate file improves readability.
5. **Player coordination:** add `Player/PlayerHandPresentation.cs`, constructed/initialized by `PlayerAvatarPresentation`. It consumes existing identity/context events, manages the local rig lifecycle, binds remote hand consumers, and selects contact/finger behavior. It calls the existing held-item presenter for item/action poses; it does not own a second action clock.
6. **Authored contacts:** add `Avatars/AvatarHandContact.cs` with hand, target transform/palm pose, optional finger clip, reach limit, and blend information. A component on each target is sufficient; avoid an interaction discovery framework.

Extend `AvatarBinding` to carry representation-specific hand measurements and uniform scale, or expose a compact hand binding from it. A companion may have a different hierarchy and wrist axes; consumers must use the active representation's hand binding instead of assuming `Settings.Generated` describes every rig. Candidate rigs need their own bindings and targets during preparation.

### Palm and item conventions

Use the existing palm axes: X right, Y out of palm, Z toward fingers. Left and right palms must share those semantics; mirror the cross-product construction for the left hand rather than copying the right-handed bone ordering blindly.

For a wrist-to-palm local pose `(p, r)` and uniform representation scale `s`:

```text
palm.rotation = wrist.rotation * r
palm.position = wrist.position + wrist.rotation * (p * s)
wrist.rotation = targetPalm.rotation * inverse(r)
wrist.position = targetPalm.position - wrist.rotation * (p * s)
item.rotation = palm.rotation * grip.rotation
item.position = palm.position + palm.rotation * grip.position
```

Grip translation is in world metres and is not multiplied by avatar scale. Convert the wrist pose to Unity's IK goal using the cached goal calibration. The final visible attachment is derived from the solved palm, not an independently approximated item transform.

### Spatial settings versus timing

Preserve serialized `HeldItemPoseSettings`, `HeldItemSettings.HoldSettings`, and `ItemDefinition.OverrideHoldSettings/HandPose`. This avoids an unnecessary migration of existing item tuning.

Add a spatial-only struct containing hold/charge positions and rotations. Store global first-person defaults on `HeldItemSettings` and `OverrideFirstPersonPose` plus that struct on `ItemDefinition`. It must contain no durations. Resolve:

```text
common settings = existing per-item HandPose if OverrideHoldSettings, otherwise HoldSettings
remote spatial pose = spatial fields from common settings
local spatial pose = explicit first-person item override, otherwise global first-person defaults
timing/reach = common settings
grip = ItemDefinition.GripPosition / GripEuler for both representations
```

Refactor the effective `HeldItemPoseData` so callers cannot accidentally obtain timing from first-person spatial overrides. Extend `WorldItemRegistry.GetHeldPose` accordingly. Update `ItemDefinitionEditor` to show the two override choices and their defaults clearly.

Store global camera/shoulder placement and free-hand motion tuning in one serializable first-person settings block on `AvatarRegistry`; no separate settings lookup or identity registry is needed. Include per-hand target poses, blend times, bounce, fall, and landing strengths. Add optional avatar placement/reach offsets to `AvatarSettings`. Express target distances in the bound arm's dimensions and placement frame; preserve actual limb lengths.

## Implementation sequence

### 1. Extract hand solving and establish finger layers

Dependencies: existing avatar code only.

- Move `AvatarHumanoidIK.ApplyHand` and hand calibration into `AvatarHandIK`. Leave all foot probing, pelvis correction, and head look in the remote class. Avoid changing remote locomotion or spring evaluation.
- Cache both shoulders/upper arms, elbows, wrists, arm lengths, and palm data at binding. Sample Unity goal-to-wrist offsets in the first valid IK callback for each Animator, once per binding. Never call IK calibration APIs outside their supported evaluation phase.
- Give both hands deliberate mirrored elbow hints. Resolve item targets within reachable arm lengths; authored contacts fade out independently when unreachable. Preserve the base pose for zero weight and blend-out. Do not let one bad contact disable driving or the other hand.
- Replace the asymmetric left wrist/follow-bone contact semantics with generated left palm data. Update callers of `SetHandTarget`, including `AvatarPresentationDemo`, to the new palm semantics. Remove `LeftHandFollowBone` only as part of this replaced feature and update its serialized content directly if removed.
- Add left and right finger-only layer mixers after the base graph. Enable only the corresponding Humanoid finger mask section; disable body, arms, wrist/hand IK, root, and other channels. Finger playables must not trigger another hand IK pass. Keep the existing per-evaluation IK guard.
- Add relaxed/grip/open references to `AvatarAnimationSet`, and an optional grip clip on `ItemDefinition`. Each contact may provide its own clip. Pose changes blend per hand without changing attachment or action timing.
- Reuse the existing Idle clip frozen at a stable sample for the local graph. Do not instantiate `AvatarAnimationState` or the full walk/run/jump graph for local arms.

Deliverable: both representations can use the same solver and finger layer implementation without pulling remote full-body services into local code.

### 2. Extend avatar processing and generate arms

Dependencies: hand data/component types from step 1 and the local component shell.

Modify `AvatarSettings`, `AvatarRegistry.Entry`, `AvatarContentValidation`, `AvatarSettingsEditor`, and `AvatarProcessor`. Put mesh extraction in `Editor/AvatarArmMeshExtraction.cs` as a helper used only by the repeatable processor.

Data changes:

- Add an authored first-person source override to the original avatar settings. Prefer an imported companion VRM; never register it as a second selectable identity.
- Store the first-person prefab in the existing registry entry. Store generated local source/Avatar references, both arm lengths/shoulders, and both wrist-to-palm poses alongside existing generated data.
- Preserve existing right palm values/convention; add symmetric left measurements. Bump generated format version and reprocess both supported original VRMs.
- Separate shared Humanoid/measurement guards from representation-specific requirements. The local guard requires its matching valid Humanoid Avatar, mapped skeleton, retained renderers, and local binding component; it must not demand feet calibration, `AvatarInstance`, `Vrm10Instance`, or a spring provider. Continue enforcing the remote requirements on remote prefabs.

Processing algorithm:

1. Resolve the original source GUID/settings/ID exactly as `Process` does now. Clone authored settings before replacing generated data. Preserve offsets, springs, first-person override, and all existing tuning.
2. Prepare the remote source normally. Create a separate local working instance from the original source or the authored override, in a preview scene. Do not modify the imported source or remote prefab to cut its mesh.
3. Keep the complete matching Humanoid hierarchy and Animator Avatar. Keep additional bones referenced by retained skin weights. Measure arm/palm data from this actual local hierarchy.
4. For automatic extraction, seed relevant deformation bones from mapped upper arms, lower arms, hands, and fingers; include their associated intermediate/twist/helper descendants. For each skinned renderer, classify vertices by summed influence from those bones. Use a simple deterministic starting cut: retain triangles whose three vertices each have majority arm/hand influence. This is a heuristic cut, not an artist-quality boundary guarantee.
5. Process each submesh independently and retain its material mapping. Compact surviving vertices with an old-to-new index map. Copy all retained vertex channels, skin weights including variable influence counts, bind poses, and relevant blend-shape deltas. Preserve the original bone table if convenient; compacting vertices does not require trimming the skeleton. Do not recompute normals/UVs in ways that change appearance.
6. Include rigid mesh attachments parented to retained arm/hand bones and relevant skinned attached details through the same source processing. Remove empty renderers and all unwanted geometry. Do not use renderer/material boundaries as a proxy for anatomical boundaries or retain the full body vertex buffer.
7. For an authored companion, retain its authored arm geometry rather than cutting it again. Bind to the companion's own imported Avatar/bones/materials. Require source-compatible scale/orientation/rest proportions; use the original avatar's uniform display scale. Never derive display scale from arms-only mesh height, and never overwrite the remote source's full-body measurements with companion bounds.
8. Strip local runtime scripts for VRM expressions/constraints/springs and gameplay physics while retaining required transforms. The local prefab contains Animator, meshes/materials, skeleton, and local binding. No hidden full-body renderer, duplicate Rigidbody/collider simulation, or active spring runtime.
9. Save generated meshes and local prefab at stable paths, then save settings/registry references. Reuse generated asset identities where possible so reprocessing does not break references. Remove obsolete generated meshes only within this avatar's generated output folder after successful replacement.
10. Extend `ProcessingChanges` to capture all modified prefabs and generated assets. A failed local generation must restore the previous remote/local outputs, settings, and registry together. The current single-prefab backup is insufficient. Keep Unity-managed asset metadata intact.
11. Extend the processor's UI to show the first-person override/output. Keep `AssignNewIdentity` internally consistent with local output paths if that existing command is invoked; normal reprocessing must never invoke it.

Use conservative skinned-renderer bounds covering local arm movement so hands do not disappear at camera extremes. Automatic extraction may expose a poor shoulder cut or miss a loosely weighted accessory; report that particular avatar for companion cleanup instead of creating a clothing pipeline or ever more elaborate heuristics.

Deliverable: processing either supported original VRM produces both representations under its existing ID and preserves authored settings on repeat runs.

### 3. Add local lifecycle and explicit evaluation order

Dependencies: local prefab and shared hand binding.

- Keep `PlayerAvatarPresentation` as the network identity owner. Feed its already-resolved `AvatarPresentation.IdentityResolved` entry into `PlayerHandPresentation`, including an initial read of `Resolved` when subscribing. Optimistic avatar selection and server corrections must reach the same local lifecycle.
- Instantiate local arms only for the client owner. Keep `presentation.SetVisual(false)` for that owner; do not enable or initialize the full-body instance to get hands.
- Prepare replacements hidden. Cache bone/mesh/settings data, create the manual base/finger graph, calibrate hands, and apply the existing item/contact state before showing the replacement. Use a request generation so rapid selections cannot commit an obsolete candidate.
- Keep active and candidate bindings separate until commit. At commit, rebind the existing item attachment, targets, fingers, and release-pose generation atomically; then hide/dispose the previous rig. A candidate preparation failure leaves the active rig intact.
- Avatar swaps must not call `PlayerHeldItemPresentation.StopPresentation`: it currently resets the item action. Rebind presentation only, preserving selected item, action snapshot/age, recovery deadlines, and seating context.
- Ownership loss, client stop, disable, and destruction must detach items before destroying anchors and dispose graphs, runtime masks, target objects, and subscriptions. Dedicated servers do not create arm rigs.

Use this ordering contract:

| Phase | Owner/local | Observer/remote |
| --- | --- | --- |
| World/camera preparation | Existing cart `0`, seating `20`, camera `50`, item trajectory presentation `150`. | Same existing world presentation sequence. |
| Common action clock | `PlayerHeldItemPresentation` at `175` advances semantic action age/deadlines once, even if no rig is available. | Same clock/deadline logic using the received action age. |
| Pose preparation | Local coordinator at `210` prepares its camera/body frame, owned hand targets, reach, clearance, and fingers. | Existing `AvatarPresentation.PrepareTargets` at `200`, after `UpdateInput`, prepares the current remote frame and targets. |
| Evaluation | Explicit local manual graph evaluation, shared finger layers, then guarded shared IK. | Existing graph/full-body IK calls the shared hand solver; preserve VRM/spring ordering. |
| Commit | Commit solved palms, world-sized item attachment, and release sample immediately after local evaluation. | Add a narrow post-evaluation/commit hook after the remote system's spring batch and binding commit to publish final palms and attachment. |

Separate action advancement from pose evaluation inside the existing held-item presenter. Event callbacks update semantic selections/dirty bindings; they must not become independent bone/item writers. `PrepareTargets` and the local coordinator read the same cached action age without advancing it again. Candidate warmup and synchronous release sampling use zero temporal advancement. Maintain a frame guard for ordinary advancement.

The local graph is driven by its own ordered coordinator, not registered with `AvatarPresentationSystem` or `AvatarSpringBatch`. The remote post-evaluation hook is also responsible for making a newly bound candidate's item/contact pose coherent before that candidate becomes visible.

If a rig is unavailable, keep a unit-scale fallback palm attachment resolved from the selected identity's generated hand dimensions. Show no substitute full body, but keep item presentation and the shared recovery clock working. A missing mesh must not lengthen or shorten gameplay recovery.

Deliverable: identity changes, owner changes, and arm preparation do not restart gameplay or produce a frame of stale attachment.

### 4. Connect first-person spatial tuning and world-sized held items

Dependencies: steps 1–3.

- Introduce the spatial-only settings described above and update `HeldItemPoseCalculation` to accept a representation-specific shoulder frame and palm calibration. Keep hold/charge curve progression in the existing implementation.
- For ordinary local poses, derive shoulder placement from the current world camera and generated local shoulder/arm dimensions. Apply only the avatar's requested placement corrections. Camera-relative targets must remain reachable for both supported avatars without changing item size.
- Remove `viewmodelSlot` construction and its `0.5` scale from `PlayerEquipment`. Remove its now-unused references as part of this change; leave unrelated prefab children alone unless they are explicitly migrated.
- Make `WorldItem.ApplyHeldAttachment` use the same palm/grip path for owner and observers. Preserve `defaultScale` in world space through parent scaling. Do not instantiate another item or network object.
- Commit the item's pose from the solved right palm and shared grip after IK. Do not let a later LateUpdate independently rewrite the item or hand. Free-hand animation owns only the unoccupied hand.
- Cache effective item settings on selection/action changes. If tuning changes are supported live, use content-change notifications rather than resolving all settings each frame.

Deliverable: Rock, Basketball, and Mushroom use shared grip data and retain their normal world size in either representation.

### 5. Unify obstruction, visible pose, and immediate release

Dependencies: coherent attachment in step 4.

- Run modest held-pose retraction before IK using `WorldItem.ReleaseRadius` and the existing `ItemReleaseClearance` environment/accessibility rules. Keep the maximum correction bounded by the existing clearance policy. Do not apply camera pullback to authored contacts.
- Treat palm/item pose as a coupled pair. Given a corrected item pose, recover its palm by inverting the grip transform, then obtain the wrist through palm calibration. Reach handling must operate on the same pair; never shift just the item.
- Reach and clearance must agree on the final result. After IK, derive the actual palm/item pose and retain whether that pose is accessible. If necessary use a bounded same-evaluation correction/re-evaluation with zero time advance; do not oscillate between independent reach and clearance solvers. An unresolved pose is unavailable for release.
- Add one committed release sample containing item identity, binding generation, coherent item/palm world poses, action progress, and clearance validity. Keep its storage in the existing held-item presenter so rig replacement cannot leave an old binding authoritative.
- Replace `TryPrepareRelease`'s independent `TryBody(true)` and body-derived charge/hold calculation. Use the displayed committed local sample. If the binding/item/clearance changed since commit, synchronously run the same prepare/resolve/evaluate/commit path without advancing temporal animation twice. Submit immediately afterward; never queue release for a future animation frame or forward swing.
- A clearance check that changes the permitted pose must change presentation through that same path before submission. If no reachable accessible pose exists, preserve `CancelItemCharge` behavior and retain the held item.
- Preserve `PlayerInventory.ReleaseSlot`'s aim-derived launch direction, charge-to-speed calculation, velocity inheritance, spin, root-origin `ItemMotion`, optimistic prediction, and simulator selection. Keep stack-drop placement outside this change.

Deliverable: throwing starts from the visible, normal-size local item with no launch-only teleport or added input delay.

### 6. Make recovery deadlines independent of spatial following

Dependencies: shared action/spatial split; can be implemented before the visual handoff.

Change `PlayerHeldItemPresentation.Recover` so normal phases derive from action age alone:

```text
followEnd = max(0, MaximumFollowDuration)
pauseEnd  = followEnd + max(0, EndPosePauseDuration)
returnEnd = pauseEnd + max(0, ReturnBlendDuration)
```

- Stop spatial tracking on reach exhaustion, obstruction, missing projectile, pickup, or removal. Retain a reachable pose for the remainder of the current fixed phase. These events must not set an earlier pause/return start.
- Derive the phase directly from age so late observers and large frame gaps can skip expired phases correctly. Keep follow/pause/return deadlines tied to the released item's effective common timing.
- If selection changes during return, interpolate from the current pose to the new destination over the remaining interval; never extend or shorten `returnEnd`.
- Call existing owner `CompleteRecovery(worldId, operation)` once at the common deadline after a submitted release, even when there is no mesh or no valid hand target. Retain explicit rejection/cancellation and control-context teardown paths separately.
- Keep the existing next-item visibility/use gates. With defaults, normal recovery remains `0.20 + 0 + 0.20 = 0.40` seconds regardless of avatar, reach, or obstruction.

Deliverable: spatial reach controls the hand path, while shared action time controls reuse and next-item visibility.

### 7. Add the observer's bounded visual release handoff

Dependencies: steps 4–6 and the final-palm commit hook.

Implement this within `WorldItem` presentation and the existing held-item presenter. Reuse visual-offset machinery where it fits, but retain distinct release identity/lifetime so ordinary correction snapshots cannot restart the effect.

1. Cache the final displayed held-item world pose during held commits. Keep it through the brief recovery-hide transition: action state can arrive before the item lifecycle record, and `ApplyHeldAttachment`/`ClearVisualOffset` can otherwise erase the starting pose.
2. On a matching held-to-world release for another player, capture the cached pose before record replacement, detachment, `CorrectBody`, or visual reset. Key the correction by world ID, releaser, and operation. A client that never displayed the held item starts directly on the trajectory.
3. Begin physics immediately from the received `ItemMotion`. Apply the held-to-projectile difference to the render-only visual transform, converging over the existing short correction duration, initially 0.075 seconds. Use a finite deadline that does not restart on repeated snapshots or late action messages.
4. Let the recovering remote hand follow the same corrected presented item pose during the handoff, using inverse grip/palm conversion. Then continue the existing reachable follow/pause/return presentation. The item trajectory must never be displaced to force the remote hand to reach it.
5. Apply this for observers even on a host that simulates that item; distinguish local thrower from simulation owner. Do not add this correction to the local thrower's coherent release.
6. Handle both rotation-synchronized and centered/cosmetic-rotation items. Preserve root-versus-sphere-center conversions in `FollowAnchorOffset`, `MotionBodyPosition`, and existing sample presentation.
7. Cancel or sharply truncate the visual displacement on pickup, removal, pooling, a different release operation, sleep/collision, or an existing motion path boundary. For observers without a collision callback, use available path changes and an environment sweep of the correction envelope to prevent a false path through walls. Do not add network collision notifications.
8. Keep this cosmetic offset out of rigidbody transforms, payloads, velocities, and impact sweeps. `WorldItem.SamplePlayerContact` currently reads `PresentedSpherePosition`; separate the release-only render offset from that gameplay sampling while preserving existing reconciliation behavior. Bird physics samples must continue to use physical positions. Expose the corrected pose only to rendering/hand following.

Deliverable: observers see a short continuous hand/item departure that quickly joins the actual projectile trajectory and cannot create extra gameplay contacts or stale release visuals.

### 8. Add free-hand movement and steering-wheel contacts

Dependencies: shared ownership, local lifecycle, and shared finger layers.

Free-hand movement:

- Subscribe to `PlayerMotor.Simulated` and context events, caching effective world velocity, grounded/mode state, and last airborne descent speed. Do not read movement/jump input to animate hands. Account for motor suspension and reset/control revisions explicitly.
- At rest, use low reachable targets with relaxed fingers. Walking/running continuously scales restrained positional/rotational bounce from horizontal speed relative to walk/sprint speed.
- Rising motion blends to a restrained airborne target. On downward motion, smoothly introduce the same fall gesture family immediately at weak strength; increase its strength with descent speed/time. Do not gate all fall motion behind a long-fall threshold. Blend relaxed toward open fingers with that strength.
- On a grounded transition, use the cached pre-impact downward speed to produce a short dip/recovery. Grounded velocity alone loses impact severity. Advance this visual envelope once per local animation evaluation.
- Clear fall/landing accumulators on seating, being carried, unresolved attachment/placement, reset, and teardown. Seed state again when returning to normal movement so stale motor data does not trigger a false landing/flail. Carry release preview may use its effective preview velocity under the existing context semantics.
- Each hand resolves ownership before applying motion. Held/charging/recovering right hand gets no free bounce/flail; the left may still gesture. Contact-owned hands get no free-hand offsets. Remote full-body movement animations remain unchanged.

Contacts:

- Add `LeftHandContact` and `RightHandContact` children beneath the cart prefab's actual visual `steeringWheel` transform. Author their palm axes, grip pose, reach, and blend settings. The wheel currently rotates its local Y from `displaySteering`; child targets must inherit that exact visual rotation and cart smoothing.
- Serialize those contacts on `GolfCartPresentation` and expose them through the cart's cached presentation reference. Resolve them on `PlayerSeating.PresentationContextChanged` using `Cart`, driver index zero, and existing pending/transition flags. Do not search the hierarchy every frame.
- Apply contacts to both local and remote hand targets/fingers. Keep `CanEquip` driver restrictions and passenger behavior unchanged. Clear only the contact candidate on exit, cart replacement/destruction, ownership loss, or teardown.
- While local contacts own the hands, blend shoulder placement to a frame derived from `CartSeat.VisualRider`/seated body orientation and avatar dimensions. Keep the target poses fixed to the wheel when the camera turns. Blend back to the ordinary camera-relative frame after contacts end.
- Fade an unreachable contact independently toward the stable relaxed/base pose; never scale the rig to reach the wheel or disable steering.

Deliverable: free hands express motion, wheel hands remain attached to the visual wheel, and context transitions cannot leave competing targets installed.

### 9. Author content and finish the supported-avatar integration

Dependencies: data/processor in step 2 and finger-layer APIs in step 1. Create the finger assets before the final processor/content pass; complete pose tuning after the runtime steps.

- On a temporary working avatar copy, use installed UniHumanoid `MuscleInspector` to set fingers and `HumanPoseTransfer > Pose to AnimationClip` to export the three static Humanoid poses. These tools are in the `com.vrmc.gltf` package's `Runtime/UniHumanoid` and `Editor/UniHumanoid` directories. Do not add authoring components to gameplay objects or modify package code.
- Assign the three clips on `AvatarAnimationSet`, then choose custom item/contact clips only where the defaults are unsuitable. Runtime finger masks must isolate fingers even if the exported clip contains full-body curves.
- Populate global first-person hold/charge spatial defaults and low rest/rise/fall/landing targets. Use both avatars' dimensions for initial placement; preserve all existing third-person grip/timing values.
- Author the two cart contacts in prefab mode using the palm convention. Do not alter wheel animation or the game scene.
- Run the extended **Two Birds > Process Avatar** for `138 Chill.vrm` and `175 Genesis Gerbil.vrm`, preserving IDs `44816e438f2e4775` and `461d9592f370666a`. These are required content-generation operations, not a Play Mode validation run.
- If the user identifies unacceptable automatic geometry, have them retain the original VRM and make a rigged arms companion on a Blender copy, keeping the full armature/rest pose/scale and materials. Assign that companion on the original settings and reprocess the original. Do not manually repair the generated prefab or replace the remote source.
- Give the user the affected asset/settings locations and the visual acceptance pass below. Apply any subsequently requested pose or asset corrections within this design.

## Networking and preservation requirements

Keep these payloads and gameplay sources unchanged:

| Need | Existing source |
| --- | --- |
| Selected appearance | `AvatarId`, existing selected avatar SyncVar and registry entry. |
| Item identity/selection | Inventory stack/world ID, existing lifecycle records, shared `ItemDefinition`. |
| Charge/recovery | `ItemActionSnapshot`, sequence, control revision, start tick/fraction, operation, and `ActionAge`. |
| Actual launch | Existing `ItemMotion` position, rotation, linear/angular velocity, and existing centered-motion encoding. |
| Wheel activation | Existing cart identity and seat index. |
| Wheel pose | Existing steering state and `GolfCartPresentation` visual transform. |
| Observer handoff | Cached displayed item pose plus existing release/action identity and motion. |

Do not stream palms, targets, finger weights, elbow hints, local rig placement, gesture state, or render corrections. If an unforeseen implementation issue appears to require changing this contract, identify the exact proposed payload/trigger/reason to the user before introducing it.

Do not replace item prediction/simulation ownership, alter aim/throw strength, enable the owner's remote body, reset gameplay on avatar changes, or make remote avatars use local flail targets. Avoid changes to unrelated carry/drop/locomotion systems.

## User visual acceptance pass

The user performs these checks in Unity after implementation, using an owner and a second observing client. Repeat appearance/grip checks with both supported avatars and Rock, Basketball, and Mushroom.

| Scenario | Expected result |
| --- | --- |
| Spawn, equip, unequip, crouched/extreme camera views where available | Selected hands/forearms appear with their own materials/details; no head/torso/legs, normal near-plane clipping, exposed unacceptable cuts, or disappearing arm bounds. |
| Bending, reaching, and strong fall motion | Retained upper-arm geometry deforms acceptably; attached details remain present. Request companion cleanup for unacceptable extraction boundaries. |
| Process original avatar again; process with a companion assigned | ID, remote appearance, authored offsets, and override survive. Companion changes only local arms and uses its own valid binding. |
| Change avatar while idle, holding, charging, recovering, and driving | Old arms remain until replacement is ready; one item remains attached, charge/action age continues, and contacts transfer without stale skeleton targets. |
| Edit global first-person hold values; enable an item override | Default-using items follow the global values; the explicit item override affects only that item's local spatial pose. Grip, third-person pose, and shared timing remain consistent. |
| Walk, run, jump briefly, descend from a height, and land | Low relaxed rest, restrained continuous bounce, modest ascent, visible weak ordinary-jump descent flail, stronger sustained fall, and severity-scaled landing dip. |
| Hold/charge/recover during movement | Occupied right hand follows the action; left hand can gesture. Fingers blend appropriately without pulling the item away from the palm. |
| Enter/exit driver and passenger seats; be carried/released | Driver gets two wheel contacts; passengers retain existing equipment behavior. No stale fall/landing gestures during attached contexts. |
| Drive and turn the wheel while looking around | Both clients see grips follow the visual wheel. Local shoulders remain body-relative; hands leave view naturally when looking away. Shorter arms may fade from unreachable grips without affecting driving. |
| Approach walls/corners with each item | Normal depth occlusion and modest coupled hand/item retraction; world-sized items, no camera overlay effect, no forced movement of contact hands. |
| Tap/charged throws, looking up/down and moving | Immediate release from the displayed item; no size/origin jump or grip separation; existing aim, strength, inherited velocity, and spin remain recognizable. |
| Release in a fully blocked position | Charge cancels and the item stays held; no launch through geometry. |
| Watch another client throw, including a nearby wall impact/pickup/removal | Brief coherent remote handoff converges promptly; no prolonged false path, offset after collision, obsolete item visual, or hand following a different release. |
| Rapid successive throws with different avatars/reach/obstruction | Same configured recovery deadline and next-item visibility; default interval approximately 0.40 seconds. Avatar swapping or unavailable arms does not change it. |
| Ownership transfer, disconnect, and respawn | Local arms/camera bindings and old subscriptions are cleaned up; the new owner receives one representation and observers retain full-body avatars. |

Completion requires the implementation/content above plus the user's acceptance of appearance and pose quality. Do not substitute an automated play session for that visual decision.
