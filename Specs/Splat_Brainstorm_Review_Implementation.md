# Splat_Brainstorm Review Implementation

All five findings in `Splat_Brainstorm_Review.md` are credible and have been addressed. No findings were rejected or deferred.

## 1. Consumed splat blocks cauldron intake — fixed

`AcceptContact` returned immediately for an accepted splat identity, before checking the live item's cauldron candidate. A surviving ingredient therefore could not enter a cauldron later in that throw.

The resolver now validates the current item and processes intake independently of splat deduplication. Previously accepted splats retain their accepted response, including after removal. The live-state and throw-identity checks prevent duplicate intake or removal.

**Code:** [WorldItemRegistry.Effects.cs](Assets/Game/Runtime/Items/WorldItemRegistry.Effects.cs).

## 2. Removal sampling loses incoming velocity — fixed

`SampleSplatRemoval` fell back to the latest motion snapshot on observers. That snapshot can contain the collision's stopped or reflected velocity rather than the incoming velocity required by impact thresholds.

The first splat candidate now retains its incoming velocity. Physics contacts use the existing pre-physics sample; presentation sweeps supply their sampled velocity. Contact merging, the accepted event, and both custom serializers preserve that value. Terminal victim sampling uses the accepted velocity through the existing contact and damage handling.

**Code:** [WorldItem.Splats.cs](Assets/Game/Runtime/Items/WorldItem.Splats.cs), [WorldItemRegistry.Splats.cs](Assets/Game/Runtime/Items/WorldItemRegistry.Splats.cs), [SplatMessages.cs](Assets/Game/Runtime/Items/SplatMessages.cs), [WorldItemRegistry.Crafting.cs](Assets/Game/Runtime/Items/WorldItemRegistry.Crafting.cs).

The contact/event wire format changed; communicating clients and host must use the updated code.

## 3. Physical player contacts use the wrong attachment frame — fixed

`SplatAttachment.Capture` selected presentation bones using an unconverted physical contact point. Player physics and smoothed graphics can occupy different poses.

Physical points and normals now transform from the motor body's pose into the avatar's presentation pose before bone selection and local-pose capture. The shared player-contact callback identifies presentation sweeps explicitly, so their points are not converted again. The pebble callback accepts the shared signature change without changing its damage behavior.

**Code:** [SplatAttachment.cs](Assets/Game/Runtime/Items/SplatAttachment.cs), [ItemPlayerContact.cs](Assets/Game/Runtime/Items/ItemPlayerContact.cs), [WorldItem.cs](Assets/Game/Runtime/Items/WorldItem.cs).

## 4. Earlier held acknowledgement cancels a newer throw — fixed

`ApplyLifecycle` cleared contacts and provisional visuals before checking whether an incoming held record belonged to the pending release.

Held-state cleanup now follows the pending-release identity check. An earlier pickup acknowledgement preserves the newer predicted throw's first candidate; applicable held records still perform cleanup.

**Code:** [WorldItemRegistry.cs](Assets/Game/Runtime/Items/WorldItemRegistry.cs).

## 5. Destroyed provisional decal suppresses accepted attachment — fixed

The prediction dictionary retained a destroyed or disposed presentation, and acceptance returned without displaying the agreed target.

Prediction entries now retain their start time independently of the presentation object's lifetime. Acceptance replaces a missing or disposed provisional decal on the agreed live target using its elapsed age. Pop and shrink tweens resume at that age, and expired predictions remain suppressed. Live provisional decals are still reused.

**Code:** [WorldItemRegistry.Splats.cs](Assets/Game/Runtime/Items/WorldItemRegistry.Splats.cs), [SplatPresentation.cs](Assets/Game/Runtime/Items/SplatPresentation.cs).
