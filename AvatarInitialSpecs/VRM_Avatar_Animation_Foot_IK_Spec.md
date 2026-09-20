# VRM Avatar, Animation, and Foot IK Spec

## Goal

Replace the current remote-player capsule visuals with VRM avatars while preserving the existing FishNet player/networking architecture.

This phase establishes the stable avatar system that later held-item IK and first-person hands will build on.

The implementation should make avatar replacement cheap and isolated: changing a player's avatar at runtime should not require rebuilding the gameplay/network player object.

---

## Scope

This phase includes:

- VRM 1.0 avatar support using the already-installed UniVRM packages.
- A project-wide avatar registry.
- Per-avatar settings.
- Stable avatar IDs replicated over FishNet.
- Runtime avatar swapping.
- An editor-side avatar processing workflow.
- Remote-player VRM rendering.
- Native VRM spring bones.
- Playables-based animation.
- Locomotion animation derived locally from replicated/current player state.
- Remote-player foot IK.
- Planning for distance-based avatar feature LOD.
- Architecture that does not block later held-item IK or first-person hands.

This phase does **not** include:

- Held-item hand IK.
- Throw poses.
- Two-handed item poses.
- Local first-person hands.
- Runtime downloading of avatars.
- User-provided arbitrary VRMs.
- Networking bone transforms or IK solutions.

---

## Existing Constraints

- Networking uses FishNet.
- The player object is already network-spawned.
- Every client ships with the same avatar registry and avatar content.
- Avatar selection is replicated using an `avatarId`.
- Avatar IDs are assigned during the editor avatar processing flow.
- VRMs are imported into the Unity project ahead of time.
- Supported avatars must use a valid Unity Humanoid rig.
- Unity Mecanim retargeting should be used for locomotion animation compatibility.
- VRMs are primarily a visual representation of the player.
- Gameplay/network state must remain stable when an avatar changes.
- The exact player/avatar hierarchy may be changed during implementation if the planner determines a better structure from the existing codebase.

---

## Avatar Registry

Create a project-level avatar registry as a Unity `ScriptableObject`.

Each registered avatar must have:

- A stable opaque ID.
- A reference to the processed avatar/prefab or equivalent runtime asset.
- A reference to an avatar-specific settings object.
- Enough metadata for editor validation and lookup.

The stable ID is the value replicated across the network.

Clients must resolve:

```text
avatarId
    ↓
local Avatar Registry
    ↓
processed VRM + avatar settings
```

The network must not replicate prefab references, asset paths, or VRM data.

### Requirements

- IDs must remain stable across normal asset moves/renames.
- Duplicate IDs must be detected.
- Missing registry entries must be detected.
- Runtime avatar changes must replace only the avatar visual layer and preserve the existing network/gameplay player.
- The planner should inspect the existing player architecture and decide the cleanest ownership/lifecycle boundary.

---

## Per-Avatar Settings

Each avatar must have its own settings `ScriptableObject`.

The settings object must support avatar-specific tuning without modifying the original imported VRM.

At minimum, it must include:

- Configured visual height / scale behavior.
- Any avatar-specific alignment offsets required by the avatar system.
- IK-related avatar tuning needed by later phases.
- Animation/retargeting tuning if required.
- Optional future LOD/performance tuning.

The system must respect the configured avatar height rather than normalizing all avatars to a universal visual height.

The planner should keep this settings object extensible because later phases will need avatar-specific hand/arm/IK tuning.

---

## Editor Avatar Processing Tool

Add an editor tool under:

```text
Two Birds
└── Process Avatar
```

The tool should turn an imported VRM into a game-ready registered avatar.

The planner should inspect the current project's editor tooling patterns and determine the exact UX.

### Processing Responsibilities

The processing flow must:

1. Accept/select an imported VRM avatar.
2. Validate that it is a usable Humanoid.
3. Create or assign a stable avatar ID.
4. Create the avatar settings asset if one does not already exist.
5. Create any processed prefab/assets needed by the runtime avatar system.
6. Register the avatar in the central avatar registry.
7. Configure the avatar for the project's runtime animation system.
8. Preserve UniVRM functionality required for spring bones.
9. Validate required bones/components.
10. Report actionable errors/warnings.

Re-processing an existing avatar should preserve its stable ID unless the developer explicitly requests otherwise.

The process should be safe to repeat after the source VRM changes.

---

## Runtime Avatar Resolution

Remote players should display the VRM corresponding to their replicated `avatarId`.

A runtime avatar swap should conceptually behave as:

```text
existing network/gameplay player
        ↓
avatarId changes
        ↓
old visual avatar removed/released
        ↓
new avatar resolved locally
        ↓
new avatar initialized
        ↓
animation / IK / spring systems rebound
```

The exact hierarchy and component boundaries are intentionally not specified here.

The planner should prefer a design where the avatar visual is replaceable without reconstructing the network player.

---

## Local Player Behavior in This Phase

This phase primarily targets remote-player presentation.

The local first-person player does not need a rendered full-body VRM.

However, the implementation must preserve enough access to the selected avatar's Humanoid skeleton and avatar-specific settings that a later phase can derive first-person hands/arms from the selected avatar.

The planner may choose whether the local avatar is instantiated-but-hidden, represented by a lightweight skeleton, or handled another way.

The choice should prioritize:

- minimal unnecessary local rendering cost;
- simple future first-person hand integration;
- shared avatar identity/settings between first-person and third-person representations.

---

## Spring Bones

Support VRM spring bones natively through UniVRM.

The goal is to preserve expected VRM hair/clothing/accessory secondary motion rather than replacing it with a custom system.

### Requirements

- Spring bones should initialize automatically with the avatar.
- Spring bones must survive/reinitialize correctly after runtime avatar swaps.
- The planner should verify that the native UniVRM approach is appropriate for the expected player counts and avatar complexity.
- The architecture must allow distance-based disabling or reduced simulation later.

### LOD Planning

Do not overbuild LOD in the first implementation, but establish a clean hook for distance-based control of expensive avatar features such as:

- spring bones;
- IK;
- possibly Animator/Playable evaluation frequency;
- potentially renderer/detail reduction later.

The planner should determine sensible tiers after inspecting expected multiplayer scale and existing visibility/distance systems.

---

## Animation System

Use a Unity PlayableGraph-based animation system rather than relying on a conventional monolithic Animator Controller as the core orchestration layer.

The avatar animation should be derived locally from player state already available on each client.

Do not network animation clips, normalized times, or individual bone transforms unless the existing codebase demonstrates a clear need.

Conceptually:

```text
replicated / observed player gameplay state
        ↓
local animation-state derivation
        ↓
PlayableGraph
        ↓
Humanoid avatar
        ↓
IK overlays
```

### Initial Animation Set

The developer will provide a basic locomotion set, expected to include at least:

- idle;
- walk;
- run;
- jump.

The planner should inspect the existing player state/network data and determine how to derive appropriate animation state locally.

Examples of usable state may include:

- grounded state;
- velocity;
- movement speed;
- jump/fall state;
- facing direction.

Do not add new network animation state if the existing replicated player state is sufficient.

### Requirements

- Animations must retarget through Unity Humanoid/Mecanim.
- Avatar swaps must rebind cleanly to the animation graph.
- The graph should be extensible for later upper-body/item poses.
- The architecture should support future masking/layering if IK alone is insufficient for certain item interactions.

---

## Foot IK

Remote players require foot IK layered on top of locomotion animation.

The desired result is that remote avatars maintain believable foot contact with the environment.

### Requirements

- Foot IK should be active continuously for remote avatars.
- Both feet should be solved independently.
- The solution should work with retargeted Humanoid avatars of different configured heights/proportions.
- Foot IK must layer on top of the locomotion animation system.
- The planner should evaluate appropriate IK solutions/packages, including good free options, rather than assuming a specific implementation up front.
- The selected approach should be compatible with the PlayableGraph-based animation architecture.
- The planner should consider foot orientation, ground contact, slope handling, and leg overextension as part of implementation design.
- IK results remain local visual presentation and are not networked.

The spec does not mandate a particular IK package.

---

## Performance

The avatar system should be designed for multiplayer use.

The planner should specifically assess:

- cost of UniVRM spring bones across multiple visible players;
- Humanoid animation cost;
- foot IK cost;
- avatar instantiate/swap cost;
- opportunities for distance-based disabling;
- whether expensive systems can be skipped for invisible/off-screen avatars.

Do not prematurely optimize away core visual behavior, but avoid architecture that requires every avatar feature to run at full fidelity at all distances.

---

## Error Handling and Validation

The system should produce useful editor/runtime diagnostics for at least:

- unknown `avatarId`;
- duplicate registry IDs;
- missing avatar asset;
- invalid/non-Humanoid avatar;
- missing required Humanoid bones;
- invalid avatar settings reference;
- failed avatar initialization.

The planner should determine the appropriate development fallback behavior after reviewing current project conventions.

---

## Acceptance Criteria

This phase is complete when:

1. A developer can import a VRM and run `Two Birds > Process Avatar`.
2. The avatar receives/preserves a stable ID and appears in the avatar registry.
3. A per-avatar settings asset exists and controls the avatar's configured visual sizing.
4. Remote players resolve their replicated `avatarId` locally and display the correct VRM.
5. Changing `avatarId` at runtime swaps the remote player's visual avatar without rebuilding the gameplay/network player.
6. UniVRM spring bones work on remote avatars.
7. A PlayableGraph drives Humanoid locomotion animation.
8. Idle/walk/run/jump can be derived locally from existing player state once the animation clips are supplied.
9. Foot IK layers over locomotion and keeps remote feet aligned to the environment.
10. Avatar swapping correctly rebinds animation, IK, and spring-bone systems.
11. The architecture exposes clear extension points for held-item IK and future first-person hands.
12. No avatar bone transforms or IK results need to be networked.


## Late additions
- If not difficult (i.e. if we can do this with the current networking stack), I want the player head to follow the rotation of the player, i.e. remove player's heads should turn based on where the player is looking.  if the head rotation exceeds normal rotation constraints for a neck (i.e. the player is looking 'backwards'), we should seamlessly rotate the actual avatar until the neck rotation look position looks normal again (and ideally, the player 'steps' for this -- if we can use IK for this, we should; we don't want individual rotate/turn animations).