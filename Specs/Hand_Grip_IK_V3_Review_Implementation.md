# Hand Grip IK Review Implementation

Response to `Hand_Grip_IK_Review.md`, item by item.

## Fixed

### 1. Slingshot release snaps the left hand forward (High)
Confirmed. `draw = 0` on the first recovery frame moves `Pouch/LeftHand` to the forks while `leftWeight = grab` is still about 1.
- `ActionChanged` (slingshot recovery) captures the left target relative to `rig.Frame`. It only does this when the previous state was `Charging` on the same item, so a late join or a missed charge falls back to the old behaviour instead of a stale transform.
- `PoseHands` holds the left target at that captured pose for the whole recovery while `leftWeight` fades. The pouch and bands still use `draw = 0` and `SlingshotPresentation`'s own recoil.
- Recoil amplitude now uses `releaseDraw` (the draw value at release) instead of `releaseCharge`.

### 2. `holdWeight` snaps outside a selection blend (Medium)
Confirmed for emote start/end in third person, and for throw release and recovery end (`HoldBob`) in first person.
- `RefreshWeights` tracks `shown`. It starts a `BeginBlend(ReturnBlendDuration)` whenever the item's visibility flips outside a selection change.
- During Following, `holdWeight`/`leftWeight` lerp from their value at release (re-captured on `BeginReturn` retargets) toward 1 when returning to an item, or 0 otherwise, using the return progress. This matches what the old `ArmWeight` did. When recovery ends while returning to an item, the weight is already 1, so the visibility-flip blend is a no-op.

### 3. Authoring: unsaved drags discarded (Medium)
Confirmed.
- `SetPhase` no longer re-applies on Hold ↔ Charged. It only updates `AuthoringPhase` on each state. Entering or leaving Live still re-applies.
- Item, avatar, view and mode changes still re-apply, per plan decision 8. The window now checks the edited rig (or the contacts, in contact mode) for dirty rows first and asks Discard/Cancel. On Cancel, the radio buttons are restored.

### 4. Saving below the resolved layer looks like a revert (Low–Medium)
Confirmed (`TryResolve` precedence is Avatar > Item > Slot). `SaveHeld` now refuses and lists the dirty targets whose source layer is above the chosen layer. That way it no longer silently changes a shared default that the current avatar doesn't use. Auto-clearing the higher layer was rejected because it destroys data (for example, clearing an item layer affects every avatar).

### 5. Owner body overlaps the observer in contact mode (Low–Medium)
Confirmed. `HideOwner(scene)` now runs in both modes. The "held mode only" rule came from before plan decision 1.

### 6. Clavicle rotated twice per arm (Low)
Confirmed (latent). `Solve` takes a `clavicle` flag, and the Free pre-solve under an Item claim passes `false`.

### 8. `LiveView` re-set during teardown (Low)
Confirmed. `OnDestroy` clears `AvatarHandContact.LiveView` after the final `ApplyPhase()`.

### 9. Undo after Save doesn't refresh live contacts (Low)
Confirmed. `Reset` no longer clears `pairs`. `SyncPairs` removes pairs whose asset or live contact is gone, so stale play-session entries don't build up.

### 10. Slingshot readout reports an unengaged left hand (Low)
Confirmed. `hasLeft = leftWeight > 0f`.

### 11. Dead charge bookkeeping during throw follow (Low)
Confirmed. `chargeItem = 0` makes `ChargeActive` false, so the charge was never read. Removed the Following branch of `RefreshCharge` and the `releaseCharge`/`charge`/`startCharge` assignments in the non-slingshot recovery branch.

### 13b. Bone-constructor `HeldItemBodyFrame.Hips` is the shoulder midpoint (Low)
Confirmed (unreachable today). It is now computed from the skeleton's hips offset, using the same convention as `leftShoulder`. The field can't be left out of a readonly struct constructor, and a zero default would be worse.

## Not fixed

### 7. `PrepareHands` uses last frame's hint weights and follow destinations (Low)
Partly valid, not worth the change.
- **Hint weights:** they change continuously (lerps over blend or return progress), so a one-frame lag can't be seen.
- **Follow destinations:** these are one frame stale, which shows only as an error of one frame's body motion on the single frame the return completes while moving. The fix means splitting follow sampling across the Prepare/Pose phases while keeping the body-relative rebase (`AvatarHandTargets.Rebase`) correct. That adds real restructuring and risk for a sub-frame artifact.

### 12. Contact poses re-resolved every frame (Low, performance)
Declined. The cost per contact hand is one scan of a short per-avatar list and one or two scans of a few entries. It only runs for players bound to a cart contact. A cache keyed by contact, avatar and view would need invalidation for runtime authoring edits (`CopyPoses`, `ApplyAuthored`, `LiveView`) and avatar changes while seated. The bookkeeping would cost more than the lookup it saves.

### 13a. `ItemDefinition.GripPoses` / `AvatarGripPoses` share names with types (Low)
Declined. Nothing is miscompiled today. A future conflicting call inside `ItemDefinition` fails at compile time rather than silently, and can be qualified (`TwoBirds.GripPoses`). Renaming serialized fields means `FormerlySerializedAs` plus touching every item asset and the authoring code, just for style.

### 14. Authoring leaves contact transforms modified (Low)
Declined. The leftover authored palm only matters to a presenter reading an unauthored view's fallback while seated. Leaving contact mode calls `ExitSeat()`, re-entering contact mode re-applies from the captured `rest`, and `ApplyAuthored` always resolves from `rest` for an unauthored view. The only remaining case is the non-live view's fallback during contact authoring. There, showing the other view's palm is a fallback for missing data, not a wrong authored value. Nothing persists past the session.

### 15. Unrelated working-tree files (hygiene)
No code change. `Assets/Game/Materials/Ghost.mat` is unreferenced (the only "Ghost" match is `HudController.dragGhost`, which is unrelated). The `UniversalRenderPipelineGlobalSettings.asset` diff is a single re-added `rid`. Both sit outside the committed grip change, so they're left for the user to keep or discard.
