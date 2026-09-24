# Grip / Hand IK Research — Follow-up

Updated decisions after a design review of `Grip_Hand_IK_Research.md`. Where the two disagree, this document wins. The work is split into two specs:
- `Hand_IK_Stability_Spec.md` (A): stability. Lands first.
- `Held_Item_Pose_Layers_Spec.md` (B): hold modes, pose layers and authoring.

## Diagnosis status
- **2.1 (out-of-reach Boulder + reach projection):** confirmed as the main cause. With the Boulder hold back in reach, only a rare single jitter remains.
- **2.2 (last-frame bones) and 2.3 (Mecanim hand IK):** treated as the likely cause of that remaining jitter. Spec A removes both.

## Decisions that change the research
| Research proposed | Updated decision | Why |
|---|---|---|
| Unity Animation Rigging `TwoBoneIKConstraint` | **One custom two-arm `IAnimationJob`**, last in the graph, reusing the analytic solve in `AvatarHandIK.Correct`. | The research already needed a custom job for soft-IK and for building targets from the stream chest. A custom job avoids the package, rig GameObjects on VRM avatars, `RigBuilder` integration with the manual graph, and `AvatarProcessor` changes. |
| IK drives the whole arm to palm targets baked per avatar from archetype poses | **A masked arm-pose layer** (in the style of `AvatarFingerLayers`) plays the pose clips. One-hand items ride the posed hand with no IK. Two-hand items use IK only to close the last few cm. The slingshot uses no IK. | This removes most of the IK instead of making the solver better at it. The arm can't be out of reach or jitter when nothing is solved. |
| New `HoldArchetype` ScriptableObject | **`ItemHoldMode { OneHand, TwoHand, Slingshot }`**, with per-mode clips and timings in `HeldItemSettings`. | Each mode's anchor rule is code anyway, and `HeldItemSettings` already holds the default pose sets. |
| Grips as child transforms on the world prefab | **Grips stay as data**: the existing `RightPalmContact`/`LeftPalmContact`/`PullingPalmContact`, edited through ghost-hand handles. | These fields already mean "palm pose in item-root metres". Keeping them avoids any data migration. |
| `HoldOffset` and `FirstPersonHoldOffset` per item | **Removed.** | Once items anchor to the posed palms, the grips fully determine where the item sits. A different placement means a new mode. |
| Critically damped spring on the item pose | **Layer and charge weight blends**, driven by the existing network timing. | This follows directly from the pose layer. |
| Per-pose elbow hints baked from the pose | **Keep the incoming animated elbow plane.** No hint data. | With the pose layer, the incoming elbow is the authored pose. |
| `AimUp` / `AimDown` poses | **Pitch only during charge, third person only.** The job rotates the posed arm chain about the shoulder. | One Charged clip per view instead of three. |
| Save-in-place for `HoldOffset` | **Dropped.** | There is no offset to save. |

## Decisions that confirm the research
- First person and third person stay separate presentations, each with its own pose clips.
- "Arms stretch": held items are never moved to fit reach. Soft-IK near full extension, no bone stretch.
- The reach projection is removed from the hold path.
- Carry and cart contacts keep feeding `AvatarHandTargets`. The `CorrectHands` / `CorrectCarry` pass is deleted, and the `HandDependency` pass order stays.
- Existing items are migrated by hand. No migration tool.

## Refinements found while writing the specs
- **`TryProject` is also used by first-person clearance every frame.** It uses the same absolute 1e-6 tolerance and the same backward-pointing `Cross(axis, up)` fallback. Spec A fixes both, because clearance keeps using it until spec B.
- **First-person clearance moves the whole first-person rig** (capped at `MaximumCorrection`, 0.20 m) instead of moving the item within a reach sphere and IKing the hands in a second evaluation. Spec B deletes `ItemReleaseReach`, `CorrectCommittedPose` and `CommitCorrection`. Item drop (`TryDrop`) never used a reach constraint, so it's unaffected.
- **First-person bob, fall and landing** move the whole rig while an item is held. `FreeHands` IK stays for an empty hand only.
- **The settings-built body frame** (`HeavyBody`) no longer feeds hand targets. It stays for positioning the first-person rig (the `HeavyFrameWeight` blend and the no-rig fallback).
- **Ordering with Mecanim foot and look-at IK.** Foot IK lowers the pelvis and look-at turns the spine, so both move the shoulders.
  - The job is placed after them, and the ordering is confirmed at the start of implementation.
  - If it's wrong, the fallback is head-only look-at plus passing the pelvis drop to the job, where it compensates world-space targets.
- **Throw follow-through** keeps briefly IKing the released hand toward the thrown item's grips. The arm layer holds the Charged clip meanwhile. No extra follow-through clip.
- **Player carry** reads its timings from the `TwoHand` mode, because `HeavyHoldSettings` is deleted.
- **`SlingshotDefinition.RecoverySeconds`** stays. It's the slingshot's recovery timing, not a pose override.
- **During spec A** the Boulder is re-authored to an in-reach hold that looks roughly like arms hanging. Its final look comes from the `TwoHand` pose clips in spec B.
- **Pose clips to author:** 12 (3 modes × TP/FP × Hold/Charged), made by the user in the new pose editor.
- **Pose editor elbow control** is a swivel slider per arm (an angle around the shoulder → wrist line), not a second 3D handle. It is used only in the editor; the saved clip stores the resulting muscles.
- **Held-item hand targets reach up to 0.98 of arm length** for heavy/`TwoHand` items. The pose clip, not a reach cap, keeps the elbows bent.
- **A missing pose clip** leaves that arm layer at weight 0, so the arms stay on the base animation during migration.
- **The first-person rig keeps its `HeavyBody` anchor** for `TwoHand` items and carry, so a held boulder stays low whatever the look pitch.
