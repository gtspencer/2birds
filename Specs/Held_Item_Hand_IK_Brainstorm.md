# Held Item and Procedural Hand IK Spec

## Goal

Add a composable held-item posing system for remote VRM avatars.

Items should visually occupy the player's hands naturally while continuing to use the existing networked item/gameplay system.

The first polished target is the rock/throw workflow, but the architecture must support increasingly complex holdables such as two-handed rocket launchers.

---

## Dependency

This spec assumes the VRM Avatar, Animation, and Foot IK phase is complete or sufficiently established.

It relies on:

- stable runtime VRM avatars;
- Humanoid skeleton access;
- avatar-specific settings;
- PlayableGraph-based animation;
- existing FishNet player state;
- existing networked equipped-item behavior.

---

## Existing Behavior

Equipped items already exist as networked objects.

Currently, equipped items are displayed in front of the player for both local and remote players.

When dropped/thrown, the item stops following the player and returns to its normal world/physics behavior.

This work replaces/improves the **remote third-person visual presentation** of equipped items.

It should not unnecessarily rewrite authoritative equip/drop/throw gameplay.

The planner must inspect the current equip/throw code and integrate with its existing state/RPC/event model.

---

## Design Principle

Gameplay/network state should communicate **what the player is doing**.

Each client should derive **how the avatar looks while doing it**.

For example:

```text
remote state:
- item equipped
- charging throw
- throw/release occurred

        ↓

local presentation:
- place item at grip
- solve hand/arm IK
- move item/IK target through procedural trajectory
- stop IK/release presentation
```

Do not network arm bone transforms, IK target transforms, or solved poses.

Approximate/cartoonish local presentation is acceptable.

---

## Composable Item Behavior

The held-item system must be composable.

There should be a generic reusable concept for a holdable item, with additional capabilities layered on for behavior such as throwing or two-handed poses.

A rock should use the generic held/throwable system rather than receiving a one-off avatar-specific implementation.

The exact component/inheritance design is intentionally left to the planner after inspecting the codebase.

The design must allow items to define their own presentation requirements without hardcoding every item type into the avatar controller.

---

## Item Pose Data

Items should be able to provide item-specific pose information.

Examples include:

- default hold/grip pose;
- right-hand grip;
- optional left-hand grip;
- elbow/pole-vector guidance if needed;
- use/action pose;
- throw trajectory data;
- item-specific offsets;
- avatar-relative positioning constraints.

The exact representation may be transforms, ScriptableObjects, prefab markers, configuration data, or another planner-selected approach.

Pose data should be reusable and authorable without requiring hand-authored skeletal animation for every item.

---

## Handedness

Default player handedness is right-handed.

The architecture must support:

- one-handed right-hand items;
- two-handed items.

Example:

```text
Rocket Launcher
- right hand: trigger/grip
- left hand: support tube
```

Left-handed player support is not required by this spec.

---

## Initial Rock Behavior

The first implementation should focus on rocks.

### Idle Held State

When a rock is equipped:

- the rock should appear naturally associated with the right hand;
- the right arm should solve toward the rock/grip relationship;
- the presentation should work across supported Humanoid VRM proportions;
- the solution may be stylized rather than physically exact.

### Charge Start

Charge amount does **not** control the pose.

Instead, when the existing network/gameplay layer indicates that the player has started charging a throw:

1. Begin the procedural throw-pose movement.
2. Move the relevant target/rock through a configured trajectory toward a throw position.
3. Solve the throwing arm so the hand follows the rock/grip relationship.
4. Continue until the configured maximum throw-pose distance/position is reached.
5. Hold that pose until the throw/release state is observed.

Conceptually:

```text
charge starts
    ↓
rock / grip target moves toward throw position
    ↓
right hand follows
    ↓
arm IK solves locally
    ↓
target reaches configured limit
    ↓
hold pose
```

The exact mathematical representation of the trajectory is an implementation detail.

It must be tunable, and it should go in a arc from default hold position, to somewhere above the head/shoulder.

At minimum, the system should support configuring how far the hand/item can move away from the player into the throw pose.

---

## Throw / Release

When the existing gameplay/network layer indicates that the rock has been thrown/released:

- stop the charged hold behavior;
- stop treating the rock as hand-following;
- allow the existing gameplay/physics throw system to take over;
- wait a configurable amount of time before disconnecting the hand IK, this will give the impression the arm is throwing it
- transition the arm back toward its normal locomotion/idle presentation.

The spec does not require networking a procedural forward arm swing.

If a local procedural release follow-through materially improves presentation, the planner may propose it as an optional presentation layer, but it must not interfere with authoritative throw physics.

---

## IK Strategy

The preferred direction is IK/procedural posing rather than authoring unique animation clips for every held item.

The planner should evaluate suitable IK solutions, including good free options.

The solution must support:

- right-hand position/orientation solving;
- future left-hand solving;
- elbow/pole-vector control where required;
- layering over Humanoid locomotion;
- compatibility with the project's PlayableGraph animation architecture;
- avatar-specific proportions;
- per-item pose data.

If investigation shows that some polished poses are impractical with IK alone, the architecture may support masked animation layers in addition to IK.

However:

- rock support should begin with the IK/procedural approach;
- animation should not become a requirement for every holdable;
- any future animation layer should remain composable with item-defined IK.

---

## Avatar-Specific Tuning

Different avatars may have substantially different proportions.

The per-avatar settings system established in the avatar spec should be extensible to support held-item tuning where necessary.

Examples may include:

- shoulder/arm reach adjustment;
- IK weight or constraints;
- item alignment offsets;
- unusual-proportion compensation.

Avoid requiring per-avatar configuration for every item unless necessary.

Prefer item defaults plus optional avatar overrides.

---

## Two-Handed Future Support

The architecture must explicitly allow a later item to define both hands.

Example:

```text
rocket launcher
    right-hand grip
    left-hand support grip
    item-defined default pose
    item-defined use pose
```

The system should allow the item to act as the anchor for both hand targets while the avatar solves toward those targets.

The rock implementation does not need to implement two-handed behavior.

---

## Networking

Use existing network/gameplay state whenever possible.

The planner should inspect how current equip/charge/throw state is communicated and consume those signals.

Desired model:

```text
network/gameplay:
- which item is equipped
- relevant action state/event
- authoritative drop/throw

presentation only:
- IK targets
- arm pose
- local pose timing
- target trajectory
```

Do not introduce continuous network synchronization of:

- arm positions;
- hand transforms;
- IK targets;
- pose weights.

A "charging throw" state/event is sufficient to initiate the local procedural movement.

The release/throw state/event ends it.

---

## PlayableGraph Integration

Held-item posing must compose with the animation architecture established in the avatar spec.

Desired conceptual order:

```text
locomotion animation
        ↓
optional future upper-body layers
        ↓
held-item procedural / IK solve
        ↓
final remote-avatar pose
```

The planner should determine the exact graph/rig evaluation order required by the selected IK approach.

---

## Runtime Avatar Swaps

If a remote player changes avatars while holding an item:

- the item presentation must rebind to the new Humanoid skeleton;
- current equipped state should remain intact;
- the new avatar should resume the correct hold/action presentation.

The networked item/gameplay object should not need to be recreated solely because the visual avatar changed.

---

## Performance

IK should be evaluated locally and only where useful.

The architecture should support future distance/visibility LOD consistent with the avatar system.

Possible future reductions include:

- disabling hand IK beyond a distance;
- lowering evaluation rate;
- using simplified item attachment when far away.

Do not require these optimizations for the initial rock implementation unless profiling shows they are necessary.

---

## Acceptance Criteria

This phase is complete when:

1. Remote avatars can display an equipped rock at the right hand rather than generically in front of the player.
2. The right arm solves procedurally toward the rock/grip.
3. Starting the existing charge action begins a configurable local throw-pose trajectory.
4. The rock/hand reaches a configured throw position and remains there while charging.
5. Charge amount is not required to define the visual pose.
6. Receiving/observing the existing throw/release action stops the held/charge IK behavior and allows the existing throw physics to proceed.
7. No arm, hand, or IK transform synchronization is added to the network.
8. The implementation is generic enough that the rock is configured as a holdable/throwable rather than hardcoded into the avatar controller.
9. The architecture supports future two-handed items.
10. Runtime avatar swaps correctly rebind held-item presentation.
11. The system composes cleanly with PlayableGraph locomotion and remote foot IK.

## Late Additions
- we should ensure support for holding the steering wheel while driving
  - we likely want hand attach points on the steering wheel, and to auto solve the arm positions.  we'll use this same solution to drive the local player arms defined in the Local_First_Person_Hands_Spec.md
  - ideally we have a generic hand IK solution that is easily extensible (can drop right hand hold point and an optional left hand hold point on interactable to auto use the hand IK)