# Avatar setup and demonstration

Use **Two Birds > Process Avatar** to set up an avatar. The processor creates its `AvatarSettings` ScriptableObject and processed prefab, prepares the shared animation set, and adds the avatar to `AvatarRegistry` automatically. You do not need to create those assets or add a registry entry manually.

## 1. Import and process a VRM

1. Exit Play Mode.
2. Add the `.vrm` file to `Assets/Art/Avatars` and let Unity import it. Keep the source inside `Assets`; the processor accepts a project VRM asset.
3. Open **Two Birds > Process Avatar**.
4. Drag the VRM from the Project window into **Project VRM source**. Do not drag a scene instance or the generated prefab into this field.
5. Normally leave **Prepare shared clips** unchecked. Missing animation assets and import settings that need correction are handled automatically. Checking it forces the mapped animation imports to be prepared again.
6. Click **Process**. Read the window diagnostics and Console messages for content errors or complexity warnings.
7. Select the resulting **Settings** asset to adjust the avatar's appearance and alignment.

The processor configures the VRM importer for VRM 1 migration and URP. A VRM 0 source can therefore use the installed VRM 1 runtime without rewriting the source file.

The imported source must have one root Animator with a valid Humanoid Avatar, VRM metadata, `Vrm10Instance`, `UniHumanoid.Humanoid`, and a usable mesh renderer. Required Humanoid mappings are hips, spine, head, both upper/lower legs and feet, and both upper/lower arms and hands. Fingers, toes, eyes, neck, chest, and shoulder mappings are optional. Root scale must be positive and uniform.

Constraints or spring chains that overwrite animated Humanoid bones or their ancestors are rejected. Fix the named feature in the authored source and re-export it. Compatible accessories, materials, expressions, and native secondary-motion data are retained.

## 2. What gets created

`<avatarId>` is the stable 16-character hexadecimal identity shown in the processor window.

| Asset | Location | Purpose |
| --- | --- | --- |
| Per-avatar `AvatarSettings` | `Assets/Game/Settings/Avatars/<avatarId>.asset` | Owns the identity, source reference, authored tuning, and generated skeleton measurements. |
| Processed prefab | `Assets/Game/Prefabs/Avatars/<avatarId>.prefab` | Contains the presentation model and its runtime components. |
| Shared `AvatarRegistry` | `Assets/Game/Settings/Avatars/AvatarRegistry.asset` | Connects each ID to its source, settings, and processed prefab; stores the default ID. |
| Shared `AvatarAnimationSet` | `Assets/Game/Settings/Avatars/AvatarAnimationSet.asset` | Supplies the animation clips and motion calibration for all avatars. |

The processor adds `AvatarInstance` and `AvatarSpringRuntimeProvider` to the processed model. The presentation host initializes animation and VRM processing at runtime. No Animator Controller or manually added spring service is required.

Do not place a processed prefab directly in the game scene to attach it to a player. The existing `Player.prefab` has a `PlayerAvatarPresentation` adapter and an `AvatarPresentation` host under `Graphics/AvatarMount`; those create the selected remote avatar. The local player resolves its avatar settings but does not create a full-body skeleton.

Treat the processed prefab as generated output: reprocessing rebuilds it from the source. Make persistent appearance changes in the source VRM or the authored settings. Let Unity create and maintain `.meta` files.

## 3. Tune the settings

The `AvatarSettings` inspector exposes authored tuning and shows generated identity/skeleton data as read-only.

| Setting | Use |
| --- | --- |
| **Visual Height** | Desired visual height in metres. Initially measured from the source; applies uniform visual scaling. |
| **Standing Offset** | Placement correction in metres in the visual body's facing space, after aligning its soles. |
| **Seated Pelvis Offset** | Pelvis alignment relative to the existing seated rider anchor. |
| **Override Driver Seated Offset** | Enable to replace the registry's driver body offset for this avatar. |
| **Driver Seated Offset** | Additional driver-only offset in seat-relative metres, added to Seated Pelvis Offset. Positive Z moves forward; passengers do not receive it. |
| **Override Driver Hand Offset** | Enable to replace the registry's steering hand offset for this avatar. |
| **Driver Hand Offset** | Offset both steering palm targets in seat-relative metres. Positive Y raises them; negative Z brings them toward the driver. |
| **Carried Offset** | Additional offset for the carried pose. |
| **Yaw Offset** | Import-facing correction in degrees. |
| **Playback Multiplier** | Animation speed adjustment, from 0.5 to 2. |
| **Left/Right Sole Adjustment** | Signed sole-height corrections in metres, such as for shoes. |
| **Left/Right Foot Rotation** | Rotation corrections in calibrated foot-goal space. Leave at identity unless the foot orientation needs adjustment. |
| **Foot Correction** | Ground foot-IK strength, from 0 to 1. |
| **Pelvis Correction** | Pelvis correction strength, from 0 to 1. |

Use these settings to fit the visual model. They do not resize the gameplay capsule, change camera eye height, or change movement speed. Prefer making persistent tuning changes outside Play Mode, then restart the demo to recompute its shared target and camera framing.

Shared driver defaults are on `Assets/Game/Settings/Avatars/AvatarRegistry.asset`: **Driver Seated Offset** starts at `(0, 0, 0.08)` and **Driver Hand Offset** at `(0, 0.04, -0.04)`. They apply to remote avatars and local driving arms. Each avatar uses these defaults unless its corresponding override is enabled; an override replaces that default rather than adding to it. Hand offsets follow the seat's orientation, independently of camera look and steering-wheel rotation. Contact-point rotations remain authored on the wheel.

### Author spring chains in Unity

Spring chains can be saved in `AvatarSettings` without changing or re-exporting the source VRM. They are added to the generated prefab each time the avatar is processed; springs already in the source are retained.

1. Process the source once to create its settings and prefab, then click **Edit Settings / Spring Chains** in the processor window.
2. Lock the settings Inspector. Open the generated prefab in Prefab Mode to access its bone hierarchy. You only need to drag references from this hierarchy; do not edit the generated prefab.
3. Under **Additional Spring Chains**, click **+** below **Chains** to add a fresh chain, and give it a name such as `Tail`.
4. Drag the first bone into **Root** and a descendant into **Tip**. For Gerbil, use `tail01` and `tail03`. Every bone on that parent-to-child path is included in order. The tip supplies the terminal endpoint; its own rotation is not simulated by that chain.
5. Adjust **Stiffness**, **Drag**, **Gravity Power**, **Gravity Direction**, and **Joint Radius**. These settings apply across the chain. Gravity Direction is normalized during processing.
6. Optionally add **Colliders** to the chain. Each entry defines a sphere: drag its attachment **Bone**, set its bone-local **Offset**, and set its **Radius**. Radii and offsets use source-model units and scale with the avatar. These spheres affect this additional chain only.
7. Exit Prefab Mode and click **Save and Process Avatar** in the settings Inspector. Restart the demo or respawn the avatar to observe the generated springs.

Each new chain starts with these defaults, regardless of the previous chain's tuning. Assign Root and Tip for the model you are editing; Gerbil's tail uses `tail01` and `tail03`.

| Setting | Default | Effect |
| --- | --- | --- |
| **Name** | `Spring` | Identifies the chain in the Inspector and processing errors. |
| **Root** | Unassigned | First bone of the chain; use `tail01` for Gerbil's tail. |
| **Tip** | Unassigned | Terminal endpoint; use `tail03` for Gerbil's tail. |
| **Stiffness** | `1.0` | Pulls the chain toward its resting pose. Higher values make it more rigid. |
| **Drag** | `0.4` | Damps movement. Higher values reduce continued swinging. |
| **Gravity Power** | `0` | Adds no downward pull, preserving the authored pose at rest. Increase gradually for droop. |
| **Gravity Direction** | `(0, -1, 0)` | Applies gravity downward when Gravity Power is greater than zero. |
| **Joint Radius** | `0.02` | Collision thickness in source-model units; used against the chain's assigned spring colliders. |
| **Colliders** | Empty | Adds no collision spheres. Add them after tuning movement if body collision is needed. |

Bone references are saved as hierarchy paths rather than references to scene instances. Drops from a different skeleton are rejected. Renamed or missing bones, ambiguous paths, overlapping spring chains, and conflicts with Humanoid animation stop processing with an error. Reassign the affected fields and process again; bone names are never guessed. Removing an additional chain from settings and reprocessing removes that generated chain.

Additional chains cannot replace springs embedded in the source VRM. To change an existing source spring, edit the source's spring configuration. Reprocessing preserves the avatar's identity and authored tuning while rebuilding its prefab from the source plus these settings.

Visually check tail response while moving and turning, settling after stopping, body clipping, and spring behavior after swapping or reprocessing. For avatars with source-authored springs, check those chains still behave as intended.

### Shared animation settings

The animation set contains **12 clips**: idle; forward/backward/left/right walk; forward/backward/left/right run; jump; fall; and seated idle. The processor assigns the supplied FBXs under `Assets/Art/Animations`. The non-walking strafe files are used for running; turn clips are unused.

Each locomotion slot has **Nominal Speed**, **Reference Human Scale**, and **Cycle Offset**. These describe the clip's source motion and supporting-foot timing. They do not set the player's walk or sprint speed. Jump uses **Ascent Start**, **Ascent End**, and **Ascent Duration**, initially `0`, `0.5`, and `0.25` seconds respectively; the first two values are normalized clip times.

Processing preserves existing positive speed/scale calibration, cycle offsets, and jump interval settings. It reassigns clip references from the processor's fixed source-file mapping, so manually substituting a clip in the asset is not a persistent change across processing.

## 4. Reprocess, rename, or change identity

To update a model, replace its source contents while retaining its Unity asset identity, then process that source again. Reprocessing keeps the avatar ID and authored tuning and updates the generated measurements and prefab. In particular, it retains **Visual Height** even if the re-exported source has a different size.

Move or rename sources through Unity's Project window so their GUIDs are preserved. The processor finds existing settings by source GUID. If a registry entry was accidentally removed but its settings remain, processing the source restores the entry with the same ID.

Do not duplicate a settings asset to create another avatar: duplicate IDs or multiple settings claiming one source are errors. Process the new VRM source instead.

Use **Assign New Identity** only when deliberately replacing an existing avatar's identity. It requires confirmation, updates the canonical settings and registry entry, and renames the generated files through Unity. Any explicitly stored IDs elsewhere, including the demo's **Avatar A/B** fields, must then be updated.

## 5. Select an avatar for players

Processing makes an avatar available in the registry. It does not automatically select every newly processed avatar for players.

To change the default for newly spawned players:

1. Open `Assets/Game/Settings/Avatars/AvatarRegistry.asset`.
2. Find the desired entry by its **Settings** or **Source** reference.
3. Expand that entry's **Id** and copy both **High** and **Low** values into the registry's **Default Id** fields.
4. Start a new session or spawn new players to use that default.

The inspector stores IDs as two decimal unsigned halves; the processor displays the combined hexadecimal value. Copy both halves from the entry rather than typing the hexadecimal filename into one field. Zero is invalid. Adding subsequent avatars preserves the existing default.

There is no avatar picker UI. Gameplay code can select an already processed avatar through the network adapter, using a reference to its settings:

```csharp
// Called for the locally owned player after client startup.
playerAvatarPresentation.RequestAvatar(avatarSettings.Id);
```

Server-side code can use `SetAvatarServer(avatarSettings.Id)` on a spawned player's adapter. Use the adapter for networked player selection; calling the presentation host directly only changes local presentation. All clients need the corresponding registry content in their build.

## 6. Open and run AvatarPresentationDemo

1. Exit Play Mode and open [AvatarPresentationDemo.unity](Assets/Scenes/AvatarPresentationDemo.unity) as the standalone scene. Close other loaded gameplay scenes so their cameras and session objects do not participate.
2. Select **AvatarShowcase** in the Hierarchy.
3. On its **Avatar Presentation** component, assign the shared `AvatarRegistry.asset` to **Registry** if needed.
4. On **Avatar Presentation Demo**, ensure these references are assigned:

| Field | Assignment |
| --- | --- |
| **Presentation** | `AvatarShowcase`'s `AvatarPresentation` component. |
| **Registry** | `Assets/Game/Settings/Avatars/AvatarRegistry.asset`, the same registry as the host. |
| **Avatar A / Avatar B** | IDs of two processed entries, entered using their High/Low halves. |
| **Hand Target** | The scene's `HandTarget` Transform. |
| **Showcase Camera** | The Camera on `ShowcaseCamera`. |
| **Game Settings** | `Assets/Game/Settings/GameSettings.asset`, which supplies the ground mask. |

5. Enter Play Mode and view the Game window. No network session or input controls are needed. Avatar instances are created at runtime, so the scene does not need an avatar model placed in it beforehand.

The configured pair is:

| Avatar | Hexadecimal ID | High | Low |
| --- | --- | --- | --- |
| `138 Chill.vrm` | `44816e438f2e4775` | `1149333059` | `2402174837` |
| `175 Genesis Gerbil.vrm` | `461d9592f370666a` | `1176343954` | `4084229738` |

Use the registry as the source of truth if an identity has been reassigned.

### What the demo does

The showcase displays A for approximately eight seconds, then B for eight seconds, and repeats. Both use grounded idle with fixed body facing and an oscillating look yaw. Replacement uses the production host, animation graph, and IK path.

At startup, the demo positions the orange **HandTarget** from both avatars' shoulder locations and the shorter arm length. The right hand follows that shared target with position weight `1` and rotation weight `0.35`. The target remains fixed through swaps while the feet adapt independently to the stage and shallow ramp. The orthographic camera frames the configured bounds of both avatars with a margin.

Both supplied VRMs have zero spring joints. They demonstrate replacement, gaze, and hand/foot correction; visible secondary motion requires an avatar with authored native spring chains. The processor reports spring-joint counts in the Console.

## 7. Use the demo with another avatar

1. Process the new source through the menu workflow.
2. Outside Play Mode, copy its registry ID's High/Low values into **Avatar A** or **Avatar B** on the demo component. Changing the registry default alone does not change this pair.
3. Keep `AvatarShowcase` at unit scale. Use the avatar's **Visual Height** for size changes.
4. Keep the ground and contact surfaces on layers included in **Game Settings > Ground Layers**. The existing stage uses **Ground**; hand-target collision is disabled.
5. Restart Play Mode so the target and camera are recalculated for the new pair.

If the Console reports that the shared hand target is unreachable, adjust the avatars' authored size/offset settings or choose a pair with compatible proportions. The demo requires its computed target to be within 90% of each arm's reach and stops if that requirement fails.

The marker's position and camera framing are assigned at startup, so editing them before Play does not override those calculations. During Play, you can move `HandTarget` or adjust `ShowcaseCamera` to inspect contact and other views. The demo captures the host's origin at startup: reposition `AvatarShowcase` before Play, or move the contact surfaces during Play, to examine the separate step. Keep the right foot on flat ground when comparing the left-foot ramp correction.

## 8. Troubleshooting and visual review

| Symptom | Action |
| --- | --- |
| Process button is unavailable | Exit Play Mode and assign a project VRM source. |
| Missing or ambiguous animation take | Restore the named FBX or correct its source take; the processor requires one unambiguous take per mapped file. |
| Unknown ID or no demo avatar | Check both registry references, both ID halves, the entry's prefab/settings references, and the Console. Process the source to restore generated content. |
| Wrong size or floor alignment | Adjust Visual Height and Standing Offset, then review sole adjustments. Keep the host and gameplay hierarchy at their intended scale. |
| No hair/accessory motion | Check the processor's spring-joint count and whether the source actually contains compatible native spring chains. |
| Local player has no visible body | Expected: full avatars are remote presentation. Use another client or the standalone demo to see the model. |

Review several A/B cycles from the front, side, and behind. Look for independent foot contact, comfortable head motion, a stable shared hand target, and swaps without T-pose flashes, double rendering, or stale hand bindings. With suitable content, also inspect native secondary motion through swaps.

Review locomotion, jumping/falling, seating/carrying, name labels, and remote/local visibility separately in multiplayer. The idle showcase does not exercise those states or establish the seven-remote performance target. Use the Unity Profiler's `Avatar.*` markers when assessing that workload.
