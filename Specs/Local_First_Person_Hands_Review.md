Review of commit `56dede5406b95c641d739813a33bf5a8b350d074` (`first person arms`) against `Local_First_Person_Hands_Spec.md`. P2 findings are functional defects; P3 findings concern maintenance or less common authoring workflows.

1. **[P2] An unequipped item can start its remote release transition from an obsolete held position.**

   Locations: `Assets/Game/Runtime/Items/WorldItem.cs:219`, `:309`, `:348`.

   `hasHeldPose` survives unequipping an item while it remains in the same player's inventory. `ApplyHeldAttachment()` hides and detaches it without invalidating that pose, and `SetRecord()` only clears it on removal or a different holder. If that player subsequently drops the unequipped item, `beginHandoff` accepts the old pose because the holder still matches the releaser. Observers can see the item briefly appear where it was held earlier and sweep toward the current drop position. There is no distance or pose-age limit on that offset.

   Invalidate the cached departure pose on actual unequip, and require a current displayed item or a matching pending release before applying a handoff. Preserve the brief recovery-related hide that precedes receipt of a legitimate throw. This follows spec §10.3's requirement that corrections stop applying when their displayed item/release is no longer current.

2. **[P2] Ending spatial follow can jump the hand back to its release pose.**

   Location: `Assets/Game/Runtime/Player/PlayerHeldItemPresentation.cs:580–605`.

   While the projectile remains reachable, `pose` advances but `retainedPosition` and `retainedRotation` still describe the release pose. On the first frame at the follow deadline, or when the projectile becomes unavailable, the `else` branch overwrites `pose` from those retained values before the stopping branch saves it. Consequently, a slow projectile that remains reachable through the follow interval, or a projectile picked up during follow, makes the hand snap backward before pause/return begins. The common recovery deadline remains correct, but the transition loses its spatial continuity.

   Keep the latest reachable follow pose separately from the original interpolation start, and freeze that latest pose when following stops. Likewise, reject an obstructed follow candidate before replacing the last usable pose. Spec §10.4 explicitly requires retaining the reachable pose until the fixed deadline.

3. **[P2] The reach limiter moves poses after clearance has resolved them.**

   Locations: `Assets/Game/Runtime/Player/PlayerHeldItemPresentation.cs:556–563`, `:508–517`; `Assets/Game/Runtime/Items/HeldItemPose.cs:125–129`.

   `Hold()`/`Charge()` already apply `Resolve()`. The owner then passes their result through clearance and applies `Resolve()` again, even when clearance returns the original pose unchanged. The exponential soft reach calculation is not idempotent: every additional application moves a wrist target in its soft zone farther toward the shoulder. This adds unintended retraction to first-person spatial overrides near maximum reach. When clearance actually moves the item to a safe position, the second reach pass can also move it away from that position and back into an obstruction. `CorrectCommittedPose()` repeats the same operation, so a reachable clearance candidate can still end in charge cancellation.

   Apply soft reach shaping once to the authored target. Reconstruct the palm/item pair from the clearance result without shaping it again, and explicitly handle any remaining hard reach constraint together with clearance. Spec §§8–10 require one coherent resolved pose for both display and release.

4. **[P2] Interrupting a finger transition produces a pose discontinuity.**

   Location: `Assets/Game/Runtime/Avatars/AvatarFingerLayers.cs:51–80`.

   A new clip toggles the active slot, destroys its previous node, and resets `Blend` to zero. If the preceding transition is unfinished, this discards a clip that still contributes to the displayed pose and promotes the preceding destination to almost full weight. For example, during relaxed → grip at 25%, selecting open changes the transition's starting point from a mostly relaxed hand to a fully gripped hand. Equipping and immediately tap-throwing within the 0.12-second blend can trigger this on both representations.

   Preserve the current weighted pose when retargeting a blend, including when returning to a clip already participating in that blend. Spec §6.2 calls for blended gripping/opening through the existing action progression.

5. **[P2] Recovery rotations retain the previous avatar's wrist basis across a swap.**

   Locations: `Assets/Game/Runtime/Player/PlayerHeldItemPresentation.cs:145–151`, `:331`, `:589–606`, `:624–625`; `Assets/Game/Runtime/Player/PlayerHandPresentation.cs:176`.

   `retainedRotation` and `returnRotation` store wrist rotations relative to the presentation body. Binding a replacement rig changes `Measurements.RightWristToPalmRotation` without converting either cached rotation. If the replacement avatar or authored companion has different wrist bone axes, pause/return applies the previous wrist basis to the new palm calibration. The visible palm can twist during the swap or recover through an incorrect orientation, even though the action age is preserved. The supplied avatars have similar calibration rotations; differently oriented future rigs expose this mismatch.

   Store persistent action rotations in the shared palm convention and convert to the current wrist basis when solving, or explicitly rebase the cached rotations when binding changes. This is required by the shared palm convention and swap continuity in spec §§6.1 and 8.

6. **[P2] Disabling and re-enabling hand presentation leaves it stopped.**

   Locations: `Assets/Game/Runtime/Player/PlayerHandPresentation.cs:259–276`; `Assets/Game/Runtime/Player/PlayerAvatarPresentation.cs:71–87`.

   `OnDisable()` destroys the local rig and targets, removes subscriptions, and sets `running = false`. There is no corresponding `OnEnable()` restart. The only callers of `StartPresentation()` are client startup and ownership changes, which do not run merely because the component is re-enabled. Toggling `PlayerHandPresentation` during an established session therefore leaves local hands absent; remote instances also lose their hand preparation/commit callbacks. Local release sampling no longer runs, so subsequent throws can be rejected for lack of a committed sample.

   Restore presentation on enable once initialization and client startup have completed, with the same guards used for teardown/startup. Spec §8 requires the representation's resources and bindings to follow its lifecycle coherently.

7. **[P3] Rebuilding the shared animation set silently omits all three finger defaults.**

   Locations: `Assets/Game/Editor/AvatarProcessor.cs:526–598`; `Assets/Game/Runtime/Avatars/AvatarAnimationSet.cs:9`, `:47`.

   The committed animation-set asset contains the finger references, but `PrepareAnimations()` only assigns the existing locomotion/state clips. Its supported create-from-missing-asset path produces null relaxed/grip/open references, and `IsComplete` still accepts the result. Processing also cannot repair these references if they are cleared. Both representations then fall back to the base animation's fingers despite reporting a complete shared animation set.

   Fill missing default finger references from the authored clips while preserving explicit replacements, and include the required finger poses in completeness checks. This closes the repeatable processing gap in spec §§4.1 and 6.2.

Smaller inconsistencies and unnecessary logic:

- `AvatarHandContact.Hand` (`Assets/Game/Runtime/Avatars/AvatarHandContact.cs:7`) is never read. `PlayerHandPresentation.Contacts()` assigns the requested hand entirely from the cart's left/right fields. Changing the authored hand metadata has no effect. Use that metadata at the contact binding boundary, or remove the redundant field so authoring has one source of truth; spec §11 describes reusable requested-hand metadata.
- `PlayerHeldItemPresentation.SetTarget()` (`:630–637`) now reinstalls the complete item target every evaluation, including steady holding. `Contacts()` similarly rewrites stable ownership/metadata and repeatedly clears absent contacts. The previous held-item path avoided unchanged installations. Keep the transform updates continuous and change ownership/metadata when their values change, as requested by spec §8.
- The new `LocalFirstPersonHands.Palm()` / `AvatarHandIK.Palm()` path has no consumer, while `CommitHands()` duplicates palm reconstruction. The pre-existing `WorldItem.PresentedFollowPosition` and `FollowAnchorOffset` helpers also lose their callers in this commit. Consolidate palm reconstruction at the shared boundary and remove helpers made obsolete by the change.

For visual validation in Unity, use both avatars and a second client: equip then drop an unequipped item after moving; observe a slow throw and a projectile picked up during follow; try near-reach hold overrides beside walls; equip and immediately tap-throw; swap rigs during recovery; and disable/re-enable hand presentation. Also check wheel contact entry/exit, camera turns while driving, ordinary jump descent versus longer falls, landing response, mesh cuts, and world-sized item/grip alignment. Use a companion with different wrist axes to assess the rotation-swap case, and a disposable copy of the animation-set asset to assess rebuilding its defaults.
