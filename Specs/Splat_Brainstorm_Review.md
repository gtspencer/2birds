# Splat_Brainstorm Review

## 1. [P1] A consumed splat prevents later cauldron intake

- **Location:** `Assets/Game/Runtime/Items/WorldItemRegistry.Effects.cs:134`, `AcceptContact`.
- **Problem:** The `acceptedSplats` early return skips the entire contact resolver, including cauldron intake. Throw an ingredient with **Destroy on Splat** disabled, let it splat against the ground, then let it bounce into an accepting cauldron. `QueueIntake` reports the same throw identity, so `AcceptContact` returns without calling `Admit`.
- **Why it matters:** Consuming a cosmetic splat disables existing ingredient behavior for the rest of that throw. The specification requires surviving items to retain their existing gameplay behavior.
- **Recommended fix:** Deduplicate only the splat outcome. Validate the current item and process a new intake candidate even when that throw already has an accepted splat. Preserve the accepted response for duplicate splat reports without admitting an ingredient or removing an item twice.

## 2. [P1] Removal sampling can lose the incoming impact velocity

- **Location:** `Assets/Game/Runtime/Items/WorldItem.Splats.cs:20`, `SampleSplatRemoval`; called from `Assets/Game/Runtime/Items/WorldItemRegistry.Crafting.cs:159`.
- **Problem:** A non-simulating observer normally has no `incomingSampled` velocity, so terminal contact sampling uses `Record.Motion.Velocity`. That is the latest motion snapshot, which can already contain the stopped or reflected velocity after collision. `AfterTick` publishes post-physics motion before the contact transition is flushed in `LateUpdate`. If removal reaches the victim before interpolation reaches the impact, the ordinary presentation sweep has not hit yet, and the terminal sweep uses this post-impact velocity.
- **Why it matters:** A fast thrown item can splat and disappear on a remote player without applying the damage or shove that the incoming collision qualifies for. `ReportImpact` checks velocity toward the victim; a stopped or reflected velocity fails those checks.
- **Recommended fix:** Capture the incoming velocity with the first contact and retain it through the accepted removal outcome. Use that contact velocity for terminal victim sampling instead of the latest motion velocity, preserving the existing damage and shove deduplication.

## 3. [P2] Physical player contacts are attached in the wrong presentation frame

- **Location:** `Assets/Game/Runtime/Items/SplatAttachment.cs:49`, player capture branch, and `SplatAttachment.cs:109`, local-pose conversion.
- **Problem:** World collisions and potion receiver contacts supply positions in the player's physical frame. The player branch chooses a bone from the smoothed avatar and directly converts that physical point into bone-local coordinates. `PlayerItemHitbox.FollowMotor` follows `Motor.Body`, while `PlayerAvatarPresentation.CurrentPlacement` follows `PlayerPresentation.Graphics`; those poses can differ. The player branch performs no conversion between them.
- **Why it matters:** Hitting a moving player can permanently bake the smoothing offset into the decal attachment. With only a 0.05 m projection depth, the splat can sit outside the avatar and fail to render. The nearest-bone choice can also be distorted by that offset.
- **Recommended fix:** Distinguish physical contacts from presentation-space player sweeps. Convert physical contact points and normals into the corresponding avatar presentation frame before selecting the bone and recording its local pose. Keep already-presented sweep contacts in their existing frame.

## 4. [P2] An earlier held acknowledgement cancels a newer predicted throw's contact

- **Location:** `Assets/Game/Runtime/Items/WorldItemRegistry.cs:242`, `ApplyLifecycle`.
- **Problem:** Every incoming held record calls `CancelItemContacts(id)` before the pending-release identity check at lines 249–252. A remote client can predict pickup, throw, and hit something before the pickup's held acknowledgement arrives. That earlier held record deletes the newer throw's retained contact and provisional decal, even though the subsequent identity check correctly declines to apply it to the predicted item.
- **Why it matters:** A legitimate first impact can disappear while release confirmation is pending. A later bounce can become the splat instead, or the throw can produce no splat if there is no further contact. This defeats the plan's requirement to retain the first candidate until that release is confirmed or rejected.
- **Recommended fix:** Apply the pending-release identity check before cancelling presentation/contact state, or scope cancellation to the throw superseded by the held record. An acknowledgement preceding the pending release must not cancel that newer throw's candidate.

## 5. [P2] A destroyed provisional decal suppresses a valid accepted attachment

- **Location:** `Assets/Game/Runtime/Items/WorldItemRegistry.Splats.cs:90` and `WorldItemRegistry.Splats.cs:102–112`.
- **Problem:** `SplatDisposed` removes the presentation from `splatPresentations` but leaves its entry in `predictedSplats`. If the provisional target disappears before confirmation, that entry references a destroyed presentation. When another reporter's accepted event selects a different, still-live target, `AcceptSplat` finds the entry and returns at `if (!predicted)` instead of presenting the agreed result. If destruction is still pending, `Place` likewise does nothing because the presentation is already disposed.
- **Why it matters:** The reporting client permanently misses the accepted splat while other clients display it. The identity has already been added to `acceptedSplats`, so repeated accepted events cannot repair the missing visual.
- **Recommended fix:** Retain prediction timing separately from the projector's lifetime. If a provisional presentation was lost through target invalidation, allow the accepted event to create a presentation on its agreed live target for the remaining animation lifetime. Preserve suppression for genuinely expired events and avoid restarting the animation.
