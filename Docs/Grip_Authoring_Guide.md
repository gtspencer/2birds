# Grip Authoring Guide

How to author how items sit in the hands, using the Grip Authoring window in Play Mode.

## How a grip works

An item's held pose is built from three layers:

`item = G ∘ D ∘ O`

| Layer | Where it lives | What it controls |
|---|---|---|
| **G: grip frame** | The class hand pose (`HoldClass` pose clips) | Where the hands are. OneHand and Slingshot: the right palm. TwoHand: a frame between the palms. |
| **D: avatar item offset** | `ItemDefinition.AvatarGrips` (per avatar, TP/FP) | An extra move or rotation of the item for one avatar only. |
| **O: item offset** | `ItemDefinition.ThirdPersonGrip` / `FirstPersonGrip` | Where the item sits in the hand, on every avatar. |

### Hold classes
A `HoldClass` asset (`Assets/Game/Settings/HoldClasses/`) holds the arm pose shared by every item of that class:
- `Mode` picks behaviour: `OneHand`, `TwoHand` or `Slingshot`.
- `ThirdPerson` / `FirstPerson` each hold a `Hold` and a `Charged` pose clip. TwoHand classes also store `HoldSpread` / `ChargedSpread`, the palm-to-palm distance measured when the pose was baked.
- Timing: `ChargePoseDuration`, `MaximumFollowDuration`, `EndPosePauseDuration`, `ReturnBlendDuration`, `FollowReachFraction`.

The shipped classes are `Regular` (OneHand), `Heavy` (TwoHand) and `Slingshot`. `ItemRegistry.CarryHold` (Heavy) supplies the player-carry release timing.

A view with no `Hold` clip falls back to the base animation's arms.

### Frames and units
- **Offsets** (O and D) are the item root in the grip frame: metres and Euler degrees.
- **Palm frame axes** (generated per avatar):

  | Axis | Direction |
  |---|---|
  | +Z | Along the fingers, from the wrist toward the middle knuckle |
  | +Y | Out of the palm face |
  | +X | Toward the thumb on the right hand, toward the little finger on the left hand |

- **TwoHand grip frame:** centred between the palms. +X points from the left palm to the right palm; +Z is the palms' average finger direction. Every avatar's palms are squeezed or spread to the class's baked spread, so the item keeps the same size in every pair of hands.
- **Slingshot pouch:** `SlingshotDefinition.PouchOffset` is the pouch position in the left-palm frame, in metres.

### Which thing to change

| Symptom | Change |
|---|---|
| Every item of a class is held too high, too low or too far out | The class **hand pose** |
| One item sits wrong in the hand, on every avatar | That item's **item offset** |
| One item looks wrong on one avatar only | That item's **avatar offset** for the avatar |
| Every item looks off on one avatar | That avatar's palm correction (`AvatarSettings` → `Left/RightPalmCorrection`, editable live in the Inspector) |
| Fingers don't wrap the item | The item's `GripFingers` |

## Starting a session

1. Open **Two Birds → Grip Authoring**.
2. Click **Start Authoring**. It offers to save modified scenes, opens `AvatarPresentationDemo` and enters Play Mode.
3. Keep the window and a **Scene** view open side by side. Gizmos only appear in the Scene view.

The Scene view shows:
- The first-person rig at the player camera.
- The **observer**, a third-person copy of the selected avatar, 1.2 m to the player's right.
- The **strip** of every other avatar beyond it, 0.9 m apart, mirroring the same item and action.

The player's own third-person body is hidden from the Scene view while authoring, so it doesn't cover the first-person rig.

## The window

```
[Start Authoring] [Frame] [Look through FP camera] [Save (n)] [Revert]
Item ▾   Avatar ▾
View   ○ Third person  ○ First person
Phase  ○ Live  ○ Hold  ○ Charged
Edit   ○ Hand pose  ○ Item offset  ○ Avatar offset  ○ None
── layer panel ──
Actions · Readouts · Unsaved
```

- **Item / Avatar:** picks what is held and who holds it. Changing the item equips it.
- **View:** Third person edits the observer. First person edits the first-person rig.
- **Phase:**
  - **Live:** normal gameplay.
  - **Hold** / **Charged:** freezes the arms in the class's hold or fully charged pose on every rig.
- **Edit:** picks the Scene view gizmo. Gizmos follow the Scene view tool: **W** move, **E** rotate, **Y** both. Pivot rotation (Local/Global) sets the gizmo axes.
- **Frame:** points the Scene view at the active gizmo.
- **Look through FP camera** (First person view only): keeps the last active Scene view locked to the first-person camera's pose and FOV.

### Actions

| Button | What it does |
|---|---|
| **Equip** | Equips the selected item, supplying one if needed |
| **Dequip** | Puts the item away |
| **Hold** / **Release** | Starts a charge, then throws |
| **Throw** | Holds for the full charge, then releases |
| **Cancel** | Cancels the current use |
| **Drop** | Drops the item |
| **Use** | Direct use, such as drinking a potion |
| **Auto re-equip** | When the item leaves the inventory (throw, drop, use), a new one is equipped a second later |
| **Walk (F2)** | Walk and look around to check the grip in motion. Off: the cursor is free and gameplay input is off. |

Every action switches the phase to **Live**.

## Authoring a class hand pose

The pose is shared by every item of the class, on every avatar.

1. Pick an item of the class, choose **Edit → Hand pose**, then choose **Hold** or **Charged**. (Selecting Hand pose in Live switches to Hold.)
2. Pick the **View**. For first person, turn on **Look through FP camera**.
3. Drag the **Right palm** gizmo. TwoHand and Slingshot classes also show a **Left palm** gizmo. While you drag:
   - The arm follows by IK and the item follows the hand.
   - A yellow sphere marks the requested palm and a cyan sphere the reached palm. They separate when a joint limit stops the arm.
4. Release the mouse. The arm pose is baked into the slot's clip:
   - The clip is `Assets/Art/Animations/HeldPoses/<Class>_<TP|FP>_<Hold|Charged>.anim`. It's created if the slot is empty or uses a clip outside that folder.
   - TwoHand classes also record the palm spread.
   - The strip avatars take the new pose immediately.
5. **Right / Left elbow swivel °** turns the elbow around the shoulder-to-wrist line without moving the palm. Releasing the slider bakes and resets it.
6. **Copy Hold → Charged** starts the charged pose from the hold pose. **Clear pose** unassigns the slot for the current phase.

The class's timing and clips are also editable in the panel's embedded inspector.

## Authoring an item offset

1. Choose **Edit → Item offset**. The gizmo sits on the item root.
2. Move or rotate the item. It slides in the hand; the hand doesn't move. This applies to every avatar.
3. Or type **Position** / **Euler** for the current view.
4. **Copy TP → FP** copies the third-person offset to first person. The two views are otherwise independent.
5. Slingshot: a **Pouch** gizmo moves the pouch relative to the left palm (`PouchOffset`).

## Authoring an avatar offset

1. Choose the avatar, then **Edit → Avatar offset**.
2. Move or rotate the item. Only the selected avatar changes; the strip stays put.
3. **Clear** zeroes the current view and removes the avatar's entry once both views are zero. The panel lists every avatar with an offset for the item.

## Two-hand items

- Author the Heavy hand pose with both palm gizmos. The baked spread is what every avatar's palms are pulled to.
- The item hangs off the grip frame and never pulls the hands, so the item offset moves the item between fixed hands.
- The **Strip** readout lists each avatar's shortfall. It should be about 0 cm; a large value means that avatar's arms can't reach the spread.

## Reading the checks

| Text | Meaning |
|---|---|
| `Contact` | Steady state. The numbers are valid. |
| `Active blend` | An equip, charge or recovery transition is running. Wait before judging. |
| `Unreachable palm: Left/Right` | A TwoHand palm is beyond 98% of the arm's reach. |
| `· clearance adjusted` | First person only. The rig was pulled back from nearby geometry. |
| `Right x m / y°` (and `Left`) | Distance and angle between the requested and reached palm. While dragging a palm, this is the drag error. For TwoHand, it's how far IK falls short of the spread. |
| `<Avatar>: short R x cm · L y cm` | TwoHand only. The same shortfall for the observer and each strip avatar. |

## Saving

Edits change the real assets live, with undo (Ctrl+Z covers drags, bakes and field edits).

- **Save (n)** writes every touched asset (items, classes, avatar settings, clips).
- **Revert** restores every touched asset to its state when first touched this session and deletes clips created this session.
- **Unsaved** lists the touched assets.
- Leaving Play Mode or closing the window with unsaved changes asks whether to save or revert.

Inspector edits to an `ItemDefinition`, `HoldClass` or `AvatarSettings` made while the session is attached also update the scene live and are tracked by Save/Revert. Inspector edits made outside Play Mode aren't tracked.

## Troubleshooting

| Problem | Check |
|---|---|
| No gizmo | You must be in Play Mode with the session attached. Hand pose gizmos need phase Hold or Charged. The item must be equipped; try **Equip**. Then **Frame**. Offset gizmos wait until the rig shows the selected avatar. The pouch gizmo needs the Slingshot class's Hold pose for the current view. |
| Arms hang at the sides in Hold/Charged | The class has no clip for that view. Drag a palm to bake one. |
| The item moves but the hand doesn't | Expected for item and avatar offsets. Use Hand pose to move the hand. |
| A wrist or elbow jumps on release | The pose was outside the humanoid muscle limits; the yellow and cyan spheres showed the gap while dragging. |
| Numbers keep changing | `Active blend`: wait. `clearance adjusted`: move away from walls. |
