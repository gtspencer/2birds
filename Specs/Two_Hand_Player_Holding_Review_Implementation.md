# Two-Hand Player Holding Review Implementation

## P1 — Missing carry grips on generated full-body prefabs

**Disposition: Valid; manual authoring by the user.** No code or prefab changes applied.

Both generated full-body prefabs lack `LeftHandGrip` and `RightHandGrip`:

- [Chill](Assets/Game/Prefabs/Avatars/44816e438f2e4775.prefab)
- [Genesis Gerbil](Assets/Game/Prefabs/Avatars/461d9592f370666a.prefab)

`AvatarBinding` requires exactly one transform with each name beneath the humanoid hips. Without that pair, `CarryHolding` is false for the carrier and third-party observers, `PrepareCarry` clears carry targets, and `carryDisplayed` prevents throw-recovery capture. The carried player's outstretched-hand view uses a separate path.

The finding is not rejected: the user chose to author the grips manually. Add and save one transform with each name beneath the appropriate animated bones in each full-body prefab. Their positions and rotations define the carrier's palm poses. First-person prefabs do not need these transforms. Reprocessing replaces the full-body prefabs and requires re-authoring the grips.

## Visual acceptance

After authoring, check every Chill/Genesis Gerbil carrier and carried-player combination in first person and from a third client. Both palms should meet the intended body locations, follow animation without clipping or excessive reach, and remain attached while charging. Throwing should show the hands following briefly, opening, and returning to rest. Confirm the carried player's view retains the outstretched-hand pose.
