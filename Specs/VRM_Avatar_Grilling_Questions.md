# VRM Avatar Design Agreement

Review this agreement, then answer the confirmation question at the end. It defines the intended revision to `VRM_Avatar_Animation_Foot_IK_Spec.md` and the foundation shared with `Held_Item_Hand_IK_Spec.md` and `Local_First_Person_Hands_Spec.md`.

## Avatar identity, processing, and sizing

- Support curated VRM 1.0 avatars through UniVRM, using valid Unity Mecanim Humanoid rigs and Avatars.
- Require usable limb anatomy. Permit small per-avatar adjustments and explicit exclusions rather than promising support for every valid Humanoid mesh.
- Use a central registry with stable opaque avatar IDs, processed avatar assets, and per-avatar settings. Replicate avatar identity rather than prefab references, paths, or VRM data.
- Provide the repeatable `Two Birds > Process Avatar` workflow. Preserve identity across reprocessing and normal asset moves/renames, preserve authored settings, and report invalid rigs, missing references, and duplicate IDs.
- Preserve complete source Humanoid and mesh information needed by the later hands phase. Defer first-person mesh extraction to that phase.
- Respect configured visual height. Avatar choice does not change gameplay collision dimensions, camera height, interaction reach, or throw origin. Extreme proportions may require presentation adjustments or exclusion.
- Establish a compatible UniVRM dependency during implementation; do not rely on the original spec's claim that it is already installed.

## Runtime lifecycle

- Keep the existing FishNet gameplay/network player stable. Replace only its avatar presentation when the replicated avatar ID changes.
- Use a developer-supplied default avatar. Do not add gameplay avatar selection controls, a player-facing picker, or saved preferences in this phase.
- Keep runtime swap support available to production code and the integration demonstration.
- Keep the old avatar visible until its replacement is initialized. Preserve it on failure and report the error. Use the existing capsule as the development fallback if initial avatar creation fails.
- Preserve gameplay and equipped-item state through swaps. Rebind animation, IK, and springs to the replacement skeleton.
- For the local player, resolve avatar identity, asset, and settings only. Do not instantiate or animate a hidden local avatar in this phase. Remote clients still render that player's full avatar.

## Animation

- Use a PlayableGraph with Humanoid retargeting and in-place clips. Gameplay movement drives the player; root motion does not move the gameplay object.
- The supplied clip set consists of idle; forward, backward, left, and right walk; forward, backward, left, and right run; jump; looping fall; and a seated pose.
- Blend cardinal directions for diagonal movement and blend walk/run according to observed movement. Preserve look-oriented strafing and backward movement.
- Adapt locomotion playback to speed and avatar scale within sensible limits, with optional avatar tuning. Accept residual sliding when needed rather than changing gameplay speed or stretching limbs.
- Derive locomotion and airborne phases locally from available movement state. Distinguish jumping from walking off a ledge. Blend directly back to grounded locomotion on landing; no landing clip is required.
- Use the supplied seated pose when seated. Use idle while carried. Carriers retain normal locomotion for now. Ground foot IK is disabled on seated and carried players.

## IK, head tracking, and springs

- Begin with Unity's built-in Humanoid IK driven during Playables evaluation for feet, head look, and the demonstration hand target. Introduce Animation Rigging only if a concrete requirement exceeds native IK.
- Correct each foot independently for nearby ground position and orientation. Include limited pelvis adjustment and avoid leg overextension.
- Fade ground correction out during jumps/falls. Respect the animated stride rather than forcing both feet onto the ground throughout the gait.
- Do not require persistent world-space foot planting or procedural turn steps. Some foot sliding is acceptable.
- At ledges or unreachable ground, reduce the affected foot's correction instead of distorting the body.
- Use the existing grounded-movement surface policy for probes. Ignore triggers, other players, and loose items. Standing on moving platforms is outside this phase.
- Let the head turn within a comfortable range while stationary, then smoothly turn the visual body. Follow facing direction more closely while moving. Keep visual body rotation independent of gameplay and camera transforms.
- Keep seated/carried bodies constrained by their attachment and clamp head look appropriately.
- Preserve native UniVRM spring behavior. Apply animation and IK before the relevant UniVRM processing, and initialize/rebind springs correctly for configured scale and avatar changes.

## Networking

- Reuse existing movement, seating, carrying, and equipped-item state.
- Add only the missing look-direction information needed for remote pitch and attached-player free look. Keep messages compact and smooth the received look direction locally.
- Do not network clips, animation timing, bone transforms, IK targets, or solved poses.
- Keep local presentation responsive and preserve consistency across clients without replacing existing gameplay authority solely for animation.

## Foundations for later hands

- Expose the active Humanoid skeleton, resolved settings, and avatar replacement lifecycle to presentation consumers. Cache bone references when binding, not each frame.
- Keep animation composition open to later upper-body layers and item-defined hand targets.
- Later held-item presentation must rebind without rebuilding the networked player or item.
- Steering-wheel grips, carrying-arm poses, rock poses, and two-handed item behavior belong to the held-item phase.
- First-person hands share avatar identity, source data, settings, and useful grip metadata. Their hierarchy, placement, and solver can differ from remote presentation.
- Do not instantiate expensive local avatar systems merely to reserve future access.

## Demonstration and performance

- Create a separate demonstration scene with a reusable demonstration component, ground surfaces, one hand target, and two configured avatars.
- Use production avatar presentation code and automatically alternate avatars to expose stale bindings and show simultaneous hand/foot IK across swaps.
- This requires a new demonstration scene and its objects/component. It does not require editing the game scene or adding gameplay avatar-selection controls.
- Target 60 FPS on the listed development machine with all seven remote avatars nearby in an eight-player session, using representative curated avatars, foot IK, and springs.
- Establish feature-disable hooks for future distance/visibility control. Defer distance tiers and reduced evaluation rates until measured cost warrants them.
- The developer performs visual review and performance assessment; this agreement does not request automated validation runs.

## Visual acceptance review

Review ground contact on slopes, steps, and ledges; directional walk/run blends; jumps and long falls; stationary head/body turns; seated and carried players; short and tall avatars at the same movement speed; spring motion; and swaps while moving or holding the demonstration target. Check that local appearance remains unchanged and remote clients display the chosen avatar. Review the seven-remote-avatar performance target separately from the single-avatar demonstration.

## Confirmation

Does this capture our shared understanding, so the main VRM avatar spec can be revised to match, with any necessary foundation clarifications in the two dependent specs?

Confirmation authorizes the documentation revision. Implementation is a separate next step.

**Your answer:**


