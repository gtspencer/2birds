# Local First-Person Hands Brainstorm

## Goal

Add first-person hands/arms for the local player while preserving the same selected avatar identity used for remote third-person VRM presentation.

The local player does not need to render their full third-person body.

The first-person hands should be derived from the selected avatar's Humanoid skeleton/avatar data so that avatar selection remains visually consistent.

---

## Dependency

This phase assumes the following systems already exist:

- stable avatar IDs and avatar registry;
- per-avatar settings;
- processed Humanoid VRM avatars;
- runtime avatar swapping;
- PlayableGraph animation infrastructure;
- held-item presentation/IK architecture.

This spec should reuse those systems rather than creating a parallel avatar identity pipeline.

---

## Scope

This phase includes:

- local first-person hand/arm visuals;
- deriving those visuals from the selected avatar;
- rebinding after runtime avatar changes;
- first-person held-item presentation;
- integration with existing equip/throw behavior;
- reuse of avatar/item pose data where appropriate.

This phase does **not** require:

- rendering the full local third-person VRM body;
- networking first-person hand transforms;
- matching the remote third-person pose exactly;
- using the exact same camera-relative item placement as remote avatars.

---

## Core Principle

The local and remote representations serve different presentation needs.

They share:

- avatar identity;
- source Humanoid avatar/skeleton;
- avatar-specific settings;
- equipped item/gameplay state;
- reusable item pose metadata where useful.

They do **not** need to share the exact same visual hierarchy, transforms, camera-relative placement, or IK solve.

First-person presentation should be optimized independently for camera view and feel.

---

## Avatar-Derived Hands

The local player's hands/arms should come from the selected avatar rather than from one universal generic hand model.

The planner should determine the best technical approach after inspecting how processed VRMs are represented.

Possible implementation approaches may include:

- a dedicated first-person skeleton/render representation derived during avatar processing;
- a selectively rendered subset of the avatar;
- a cloned/reduced Humanoid arm hierarchy;
- another approach that preserves the selected avatar's hand/arm appearance.

The spec intentionally does not mandate which approach is used.

The solution should avoid requiring the developer to manually rebuild first-person hands every time a VRM avatar is added.

---

## Avatar Processing Integration

The existing:

```text
Two Birds
└── Process Avatar
```

workflow should eventually prepare whatever data/assets are required for first-person hands.

This phase may extend that processor.

Processing should remain repeatable and should preserve the avatar's stable ID.

If additional generated first-person assets are required, they should be produced automatically or with minimal explicit authoring.

If there is not suitable overlap with the existing avatar processing script, we can create a new one for the first person processing.

---

## Runtime Behavior

For the local player:

```text
selected avatarId
    ↓
avatar registry
    ↓
avatar-specific first-person hand/arm representation
    ↓
camera-relative first-person presentation
```

Changing avatar at runtime should:

- preserve the same gameplay/network player;
- replace/rebind the first-person hand representation;
- preserve current equipped-item state;
- reapply the correct item presentation.

---

## Full Body

A full local VRM body is not required.

The planner should avoid rendering unnecessary local body geometry if it creates:

- camera clipping;
- duplicate visuals;
- unnecessary performance cost;
- conflicts with first-person item presentation.

The architecture from the initial avatar phase should make it possible to access avatar skeleton/data without requiring a visible full local avatar.

---

## First-Person Held Items

First-person held-item placement should be independent from remote third-person held-item placement.

The two systems may share:

- item identity;
- item pose metadata;
- grip semantics;
- action state;
- throw/charge state;
- generic holdable/throwable behavior.

But first-person tuning should be allowed to use distinct:

- offsets;
- trajectories;
- hand positions;
- camera-relative positioning;
- IK weights.

This prevents third-person pose requirements from compromising first-person feel.

---

## Rock Throw Behavior

The local first-person system should integrate with the same underlying rock equip/charge/throw gameplay flow.

The exact first-person visual motion may differ from the remote avatar.

Desired behavior:

```text
rock equipped
    ↓
local hands present rock naturally
    ↓
charge starts
    ↓
first-person hand/item presentation moves toward configured throw pose
    ↓
throw occurs
    ↓
rock returns to authoritative world/physics behavior
    ↓
hands recover
```

The implementation should consume existing gameplay state/events rather than add a second throw state machine solely for visuals.

---

## Animation and IK

The planner should determine how much of the third-person IK architecture can be reused for first-person hands.

The solution may use:

- IK;
- procedural targets;
- PlayableGraph animation;
- masked animations;
- a combination.

First-person quality and responsiveness take priority over forcing identical third-person and first-person posing logic.

No first-person hand/bone transforms need to be networked.

---

## Avatar Proportions

Because the hands originate from different VRM avatars, proportions may vary.

The per-avatar settings object should be extended where needed to support first-person tuning.

Potential settings include:

- first-person arm scale/reach compensation;
- camera-relative hand offsets;
- clipping-safe positioning;
- item alignment overrides.

The planner should minimize per-avatar manual tuning but permit overrides when unusual avatars require them.

---

## Camera Interaction

The planner should inspect the existing first-person camera architecture before choosing implementation details.

The first-person hands system should account for:

- near-plane clipping;
- field of view;
- camera-relative motion;
- held-item readability;
- avoiding unwanted interaction between world/body transforms and viewmodel transforms.

This spec does not prescribe whether a separate camera, renderer layer, viewmodel FOV, or equivalent technique should be used.

That is an implementation decision for the planner.

---

## Networking

First-person hands are entirely local presentation.

Do not network:

- hand positions;
- arm transforms;
- IK targets;
- viewmodel transforms;
- local first-person animation timing.

The existing replicated gameplay state remains the source of truth for actions that remote players need to observe.

---

## Relationship to Remote Avatar

The local player should still have a valid selected `avatarId` and avatar settings even if their full third-person body is not rendered locally.

Remote clients should continue rendering that player's full VRM using the normal remote-avatar system.

Conceptually:

```text
same player / same avatarId

local client:
    first-person hands/arms

remote clients:
    full third-person VRM
```

---

## Performance

The first-person representation should avoid unnecessary duplication of expensive VRM systems.

In particular, the planner should assess whether the local first-person representation needs:

- spring bones; (editor note -- it does not need spring bones)
- a full duplicate Animator/PlayableGraph;
- hidden full-body renderers; (editor note -- ideally we don't have this)
- duplicate physics.

Only systems required for the visible first-person presentation should run.

---

## Acceptance Criteria

This phase is complete when:

1. The local player sees first-person hands/arms derived from their selected avatar.
2. A full local third-person VRM body is not required for normal first-person play.
3. Remote clients still see the player's full selected VRM.
4. Runtime avatar switching updates the local first-person hand/arm representation without rebuilding the gameplay/network player.
5. Equipped items appear correctly in the local hands.
6. First-person item placement can be tuned independently from remote third-person placement.
7. The rock equip/charge/throw flow works with the existing gameplay/network state.
8. No first-person hand or IK transforms are networked.
9. The system reuses the avatar registry, avatar settings, and generic held-item architecture rather than creating parallel identity/configuration systems.
10. The `Two Birds > Process Avatar` pipeline can prepare any required first-person avatar assets/data with minimal manual work.
