**[P1] Add the carry grip transforms to the generated avatar prefabs.**

**Location:** [AvatarPresentation.cs:33](/Users/spencer/source/repos/2birds/Assets/Game/Runtime/Avatars/AvatarPresentation.cs:33), [PlayerHandPresentation.cs:232](/Users/spencer/source/repos/2birds/Assets/Game/Runtime/Player/PlayerHandPresentation.cs:232), and the generated full-body prefabs [44816e438f2e4775.prefab](/Users/spencer/source/repos/2birds/Assets/Game/Prefabs/Avatars/44816e438f2e4775.prefab) and [461d9592f370666a.prefab](/Users/spencer/source/repos/2birds/Assets/Game/Prefabs/Avatars/461d9592f370666a.prefab).

**Problem:** Neither full-body prefab contains `LeftHandGrip` or `RightHandGrip`. The new binding requires exactly one of each beneath the humanoid hips, so `HasCarryGrips` is false for both avatars. When the carried player is not the local owner, `CarryHolding` consequently stays false and `PrepareCarry` clears both carry targets every frame.

**Why it matters:** The carrier's first-person hands and a third client's view never display the new grip pose with the supplied avatars. `carryDisplayed` also remains false, preventing throw-recovery capture. Only the carried player's view takes the separate outstretched-hand path. The processor's new log message does not provide the required transforms.

**Recommended fix:** Author and save one `LeftHandGrip` and one `RightHandGrip` under the appropriate animated bones in each generated full-body prefab. This requires manual prefab authoring; re-author the grips after reprocessing, as the processor replaces those prefabs.
