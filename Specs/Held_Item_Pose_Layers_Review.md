# Held Item Pose Layers Review

Scope: commit `1b83156` (initial hand IK stability) and the working-tree changes built on it: arm pose layers, two-hand anchor, `HandHoldPresentation`, first-person clearance via `TranslateRig`, and the grip authoring pose editor. Markdown files were not reviewed.

Intended behavior, as read from the code: held-item arm poses come from per-mode hold/charged clips (`AvatarArmLayers`) instead of procedural IK targets. IK is used only for two-hand grips (anchored between the posed palms), the release follow, carry, and contacts. First-person clearance moves the whole rig. The pose editor authors the clips from IK-posed palms.

---

## 1. No fallback when a mode has no pose clip; the shipped settings have none (High)

**Where:** `Assets/Game/Settings/HeldItemSettings.asset` (all 12 clip slots are `{fileID: 0}`), `HeldItemPresentationState.cs:359-372`, `AvatarArmLayers.cs:61`, `PlayerHandPresentation.cs:525-527`

**Problem:** `SubmitTargets` always installs a zero-weight `Item` target on the right hand (and on the left for Slingshot) to supply fingers. That happens whether or not the mode has a clip. In first person, any claimed target sets the free-hand weight to 0. With no clip, `AvatarArmLayers` adds nothing, so the arm falls back to the FP basis, the Idle clip at t=0 (arms hanging).
- One-hand and slingshot items render at hip height, out of view.
- Two-hand items anchor between the hanging palms.
- The slingshot pouch never attaches or draws, because `attach` is 0 without a hold clip.
- `ArmWeight` and `HeavyFrameWeight` still ramp to 1, so the rig still moves to the heavy frame and bobs.

A partially authored set fails the same silent way, for example a TP hold clip but no FP clip.

**Why it matters:** With the settings as committed, every held item regresses in first person. Partially authored settings also degrade per view with no warning.

**Fix:** Author the clips before merging. Also make a missing clip a no-op: only claim a hand, and only count a mode toward `ArmWeight`/`HeavyFrameWeight`, when `defaults.Poses(mode).Hold(firstPerson)` exists. Otherwise leave the free-hand IK in charge. A one-time warning for a missing clip would make partial authoring visible.

## 2. Free-hand to clip handoff snaps the arm to the idle basis in first person (Medium)

**Where:** `PlayerHandPresentation.cs:527`, `HeldItemPresentationState.cs:368-371`

**Problem:** `freeWeights[i]` drops to 0 on the first frame any other target exists (`owned ? 0f`). Equipping installs the zero-weight Item target right away, but the arm layer starts at weight 0 and ramps over `ReturnBlendDuration`. For that window the FP right arm (and the left, for two-hand and slingshot) drops to the hanging idle pose, then rises into the hold clip. Unequipping blends better, because the free weight ramps back up with `MoveTowards`.

**Why it matters:** Every equip shows a visible dip in first person. The base commit had the same snap, but the layered design now makes it easy to remove.

**Fix:** Derive the free-hand weight from the claiming layer instead of snapping it. For example, for held items use `freeWeight = 1 - layerWeight(hand)`, from `ArmWeight` or the per-arm layer weight. Alternatively, blend `owned` toward 0 with the same blend time.

## 3. "Save pose clip" overwrites whatever clip is in the slot, in place and irreversibly (Medium)

**Where:** `GripAuthoringPersistence.cs:74-79`

**Problem:** If the slot already holds any project clip (`AssetDatabase.Contains`), `EditorUtility.CopySerialized` replaces that clip's contents. Three consequences:
- If the slot references a clip that isn't a generated pose clip, such as an FBX sub-asset or a clip shared with `AvatarAnimationSet`, the source clip is replaced in memory for the rest of the session and is not saved back to the FBX. Every avatar using it breaks until reimport.
- The clip file is written immediately, but the settings reference is only a draft. Reverting the shared-defaults draft does not restore the old clip contents.
- `CopySerialized` also copies `m_Name`, so a file created as `X 1.anim` by `GenerateUniqueAssetPath` gets a mismatched object name.

**Why it matters:** This can corrupt source animation assets, and draft revert no longer means revert.

**Fix:** Overwrite only main-asset `.anim` files under `Assets/Art/Animations/HeldPoses`; otherwise create a new asset. Set `clip.name = existing.name` before copying. Either save the settings reference in the same step, or document that pose saves bypass drafts.

## 4. Muscle-space capture is lossy and nothing shows the round-trip error (Medium, verify)

**Where:** `GripAuthoringScene.TryCapturePose`, `GripAuthoringPersistence.SavePose`

**Problem:** The editor poses palms with IK, which can use elbow swivel and arbitrary wrist rotation. `GetHumanPose` then converts the pose to muscles. Wrist and arm-twist limits clamp, and twist gets redistributed, so the saved clip can differ from the edited palm. One-hand and slingshot poses have no IK correction at runtime, so any error ships directly. After saving, `Reset()` re-seeds from the new clip; the palm may jump, but no number is shown.

**Why it matters:** Authors can save a pose that doesn't match what they placed without noticing.

**Fix:** After the clip rebinds, compare the re-seeded palms with the edited palms and show the position and angle error, as the two-hand shortfall readout already does. Alternatively, refuse or warn above a threshold.

## 5. The body frame used for body-relative targets moves with the arm clips (Medium-Low)

**Where:** `AvatarPresentation.cs:55` (`AvatarBinding.Body`), `AvatarArmIK.cs:80`, pose-editor `Rebase` calls

**Problem:** `Body` is the midpoint of the UpperArm bones, with root rotation. The arm layer mask includes the shoulder (clavicle) muscles, so the clips move this frame themselves. Between prepare (last frame's shoulders) and solve (this frame's shoulders), `Rebase` shifts body-relative targets by the clip's own shoulder motion. This affects recovery Pause/Return blending from charged to hold, and it makes pose-editor palms depend on the current clip. Root rotation also ignores spine lean, such as sprint or fall poses.

**Why it matters:** Body-relative targets drift slightly whenever the clips shrug the shoulders, which defeats the stability the rebase was added for.

**Fix:** Build `Body` from a bone the arm layers don't write: Chest or UpperChest position and rotation, or the midpoint of the Shoulder bones' parents.

## 6. Arm layers rebind every frame and tear down playables on `SetArms(default)` (Medium-Low)

**Where:** `AvatarArmLayers.Set`, `HeldItemPresentationState.cs:87,330`

**Problem:** `Set` runs every frame for every avatar and the FP rig. It re-resolves 12 clip slots and calls `Bind`, which is a no-op unless the clips changed. When `Settings` is null (`SetArms(default)` on losing `CanEquip`/`HasAction`, or on dispose), every node is disconnected and destroyed. They are recreated on the next equip, which changes graph topology on each of these transitions.

**Why it matters:** This goes against the project's rule to prefer event-driven changes and avoid per-frame no-ops. Topology changes also force a graph rebind on transitions such as sitting, carrying, or losing the action snapshot.

**Fix:** Bind clips once, and again on `HeldItemSettings.ContentChanged`. Keep the nodes alive. Per frame, only write weights. A default pose should zero the weights rather than unbind.

## 7. First-person two-hand charge ignores look pitch; third person applies it (Low, design)

**Where:** `HeldItemPresentationState.cs:348`, `PlayerHandPresentation.Place`

**Problem:** `Pitch` is forced to 0 in first person, on the assumption that the rig follows the camera. For two-hand items, though, `HeavyFrameWeight` puts the FP rig in the yaw-only body frame. As a result, an FP two-hand throw never aims up or down, while observers see the arms pitched by `LookPitch * charge`.

**Why it matters:** The first-person and third-person views of the same throw disagree.

**Fix:** Apply the same pitch to the FP rig when it is in the heavy frame, for example `Pitch * HeavyFrameWeight`. Otherwise, confirm that the difference is intended.

## 8. `Attachment` recomputes the two-hand anchor differently from `CommitHands` (Low)

**Where:** `HeldItemPresentationState.cs:104-109` vs `:400`

**Problem:** `Attachment` uses `TwoHandAnchor` on post-IK palms. `CommitHands` uses `binding.AnchoredItem`, which is computed from pre-IK palms. When IK is reach-limited these differ, so the item pops for a frame when attached.

**Why it matters:** It's a one-frame visual pop, and the anchor logic is duplicated.

**Fix:** Use `boundBinding.AnchoredItem ?? TwoHandAnchor(...)` in `Attachment`.

## 9. Reach readout mixes corrected palms with uncorrected shoulders (Low, authoring only)

**Where:** `HeldItemPresentationState.cs:447-453`, `InHandReach`

**Problem:** In first person, `requestedRight`/`requestedLeft` are shifted by the clearance correction, but `lastBody` shoulders are not. The shoulders are also prepare-time positions, before the clip moves them.

**Why it matters:** The readout reports false "Unreachable contact" whenever clearance adjusts the item.

**Fix:** Subtract the correction before `InHandReach`, or add it to the shoulders. Ideally measure against the solved shoulder bones.

## 10. Slingshot recovery edge cases (Low)

**Where:** `SlingshotPresentation.cs:40,64`

**Problem:**
- `releaseLocal` mixes last frame's world `Center` with this frame's `frame`. While moving, the recoil starts offset by one frame of travel (about 13 cm at sprint speed and 60 fps).
- If `RecoverySeconds <= SettleSeconds` (0.35), `InverseLerp` returns 0. The pouch then stays loose for the whole recovery and snaps to the hand on Idle.

**Why it matters:** Both show up as small visual pops on release.

**Fix:** Store the last loaded `Center` in item-local space when `Loaded`. Clamp the settle window, for example with `Mathf.Min(SettleSeconds, recovery * 0.7f)`.

## 11. Follow targets are body-relative but submitted as world once following stops early (Low)

**Where:** `HeldItemPresentationState.cs:384-386`

**Problem:** The code passes `bodyRelative: Stage != Follow`. When `Following` ends inside the Follow window (projectile lost or out of reach), `Left`/`Right` are the retained body-relative poses but are not rebased.

**Why it matters:** The held hands can drift from the body for the rest of the Follow window.

**Fix:** Use `bodyRelative: !hands.Following`.

## 12. Pose-edit state leaks outside the pose editor (Low, authoring only)

**Where:** `GripAuthoringPanel.PoseEditor`, `GripAuthoringScene.BeginPoseEdit/EndPoseEdit`, `HeldItemPresentationState.cs:339-341`

**Problem:**
- The edit stays active after switching the panel away from the shared-defaults context. The controls disappear, but weights stay forced to the edit mode, so item contacts get authored in a forced pose.
- `Swivel` values persist if `PrepareHands` returns early, and are then applied to any IK target, including carry.

**Why it matters:** Authoring can happen in the wrong pose without the author knowing.

**Fix:** End the pose edit when the context or record changes. Clear `Swivel` in `ClearTargets`/`Dispose`.

## 13. `HoldMode = Slingshot` can be set on non-slingshot items (Low)

**Where:** `ItemDefinition.HoldMode`, `ItemDefinitionEditor`

**Problem:** Nothing prevents it. The left hand then follows the slingshot clip with no slingshot presentation.

**Why it matters:** It produces a broken-looking hold with no error.

**Fix:** Clamp or validate in `ItemDefinition.NotifyContentChanged`/`OnValidate`, or hide the option in the editor for non-slingshot definitions.

## 14. Duplicated logic and leftovers (Low)

- `PlayerHandPresentation.HeavyBody` (inlined from the deleted `TwoHandHoldPresentation.Body`) duplicates `HeldItemPresentationState.Body(settings, true)`, with slightly different binding conditions. This change is a good point to share one helper.
- The two-hand reach `0.98f` literal now appears in 6 places in `HeldItemPresentationState` (lines 362, 363, 451, 452, 506, 512) plus the defaults in `AvatarArmIK` and `AvatarHandTargets`, since `HeavyItemPoseCalculation.MaximumReach` was removed. Restore a single constant.
- `WorldItemRegistry.GetHeldPose` has no callers. It was already dead at the base commit and was edited instead of removed.
- `HeldItemPresentationState.HeavyBody` is `internal` but only used internally.
- `HandHoldPresentation.FrameWeight` is public but only seeds `Retarget`.

## 15. `GripGhostHand` shader lookup can throw every frame in instrumentation builds (Low)

**Where:** `GripGhostHand.cs` material creation, `GripAuthoringScene.UpdateGhosts`

**Problem:** If `Universal Render Pipeline/Unlit` is stripped from a player build, `Shader.Find` returns null and `new Material(null)` throws. `UpdateGhosts` retries on every `LateUpdate` because the ghost is never stored. `UpdateGhosts` also allocates every frame (a lambda in `Entries.Find` plus an array).

**Why it matters:** A dev build could spam exceptions every frame while authoring.

**Fix:** Reference the material or shader from a serialized field on the authoring scene. Cache the path array and the selected entry when the selection changes.

---

## Visual validation to do

- **FP equip/unequip** of a one-hand, a two-hand, and the slingshot item, with and without clips assigned: check that the hands don't dip to the hips (items 1 and 2).
- **Two-hand throw in FP, looking steeply up and down, compared with an observer view:** check whether the arms agree (item 7).
- **Pose editor:** save a pose with a strong wrist bend or swivel, and check whether the palm jumps after the automatic reset (item 4).
- **Slingshot fired while sprinting:** check where the pouch recoil starts (item 10).
