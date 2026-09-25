# Emote Spec

## Summary

Players open an emote wheel by holding the Emote input and pick one of up to eight emotes. The emote plays on the full-body avatar for every client. Emotes are stationary, have no root motion, and keep foot IK. They can loop or play once. Movement, other gameplay actions and physical disruption end them. The emoting player sees their own avatar through a third-person orbit camera. The network cost is one reliable byte per start or stop.

## Terms

- **Emote**: one `EmoteDefinition` playing on a player's avatar.
- **Loop emote**: an emote with `Loop` enabled. It repeats until something cancels it.
- **One-shot emote**: an emote with `Loop` disabled. It ends by itself when the clip finishes.
- **Wheel**: the radial emote menu on the HUD.
- **Owner**: the client that controls the emoting player.
- **Observer**: any other client viewing that player.
- **Dormant body**: the owner's own full-body avatar instance while it is loaded but not animated or rendered.

## Data

### `EmoteDefinition` (ScriptableObject, `Two Birds/Emote Definition`)

| Field | Type | Notes |
| --- | --- | --- |
| `Clip` | `AnimationClip` | Humanoid clip. |
| `DisplayName` | `string` | Always shown on the wheel. |
| `Loop` | `bool` | Loop emote or one-shot emote. |
| `Speed` | `float` | Playback multiplier. Default `1`. |
| `Icon` | `Sprite` | Optional. Shown on the wheel next to the name when set. |

Blend durations are not configurable.

### `EmoteCatalog` (ScriptableObject, `Two Birds/Emote Catalog`)

- An ordered list of up to 8 `EmoteDefinition` references.
- **The list index is the emote's network id and its wheel slot.** Slot 0 is at the top of the wheel, and slots go clockwise.
- Slots without an entry are not drawn and cannot be selected.
- `SessionController` references the catalog through a serialized field and exposes it as `Emotes`, the same way it handles `HatCatalog` and `TattooCatalog`.

## Input

### Actions and bindings (`InputSystem_Actions.inputactions`, Player map)

| Action | Keyboard&Mouse | Gamepad |
| --- | --- | --- |
| `Emote` (new, Button) | `<Keyboard>/r` | `<Gamepad>/dpad/left` |
| `SecondaryInteract` | `<Keyboard>/f` (was `r`) | `<Gamepad>/dpad/right` (unchanged) |
| `ExitVehicle` | **removed** | **removed** (`rightStickPress` becomes free) |

- **`SecondaryInteract` does two jobs:**
  - While seated, it exits the vehicle. Exiting takes priority, and it never triggers a targeted secondary interaction on the same press.
  - On foot, it is the targeted secondary interaction, such as the cauldron's Dispose.
- **Vehicle-exit handling** in `PlayerInputReader` moves to `SecondaryInteract` and keeps the existing blocked-until-released behavior.
- **`InputBindings`:**
  - Remove the `ExitVehicle` entry.
  - Rename the `SecondaryInteract` entry to `Secondary Interaction / Exit Vehicle`.
  - Add an `Emote` entry labeled `Emote` so it can be remapped.
- **The `HudController` cart driver and passenger hints** use `SecondaryInteract`, still labeled `Exit`.
- **Saved binding overrides** for the removed `ExitVehicle` action are dropped. This is accepted.

### When the wheel can open

The wheel opens when `Emote` is pressed and all of these are true:

- the player is alive,
- the player is grounded (`MovementMode.Walking`),
- the player is not seated, not waiting on a seat transition or placement, not carried, and not carrying another player,
- the inventory and session panel are closed and gameplay input is available,
- the player is not charging a Use (item or player throw).

If any of these stops being true while the wheel is open, the wheel closes without playing anything. The same happens when gameplay input is interrupted (session panel, focus loss, overlay).

The wheel can open while an emote is already playing. That emote keeps playing, and a new pick replaces it.

### While the wheel is open

- **Look is frozen.** `Yaw` and `Pitch` don't change. The look input drives selection instead.
- **Movement stays live.**
- **Blocked:**
  - Jump, on both devices.
  - Use, DirectUse, Drop, Interact, SecondaryInteract, Hotbar1–8, Previous, Next and Inventory.
- **LMB, RT and A (South) only confirm.** A button pressed as a confirm doesn't trigger its gameplay action after the wheel closes, until it is released.
- **Escape / Pause** closes the wheel without playing and opens the session panel as usual.
- There is no on-screen prompt for the Emote action.

### Selection

- **Mouse:**
  - The cursor stays locked.
  - Look deltas add up into a virtual pointer, clamped to the wheel's radius.
  - Past a center deadzone, the pointer's angle picks the slot.
  - A small indicator shows where the pointer is.
- **Controller:**
  - The right stick's direction past a deadzone picks the slot.
  - When the stick goes back to center, the last highlighted slot stays selected.
- **Hovering only highlights.** It has no other effect.

### Confirming

- **Confirm button** (LMB, RT or A): plays the highlighted emote and closes the wheel immediately. The wheel does not reopen until `Emote` is released and pressed again. If no slot is highlighted, confirm does nothing.
- **Releasing `Emote`:** plays the highlighted emote if there is one. Released in the deadzone with nothing highlighted, it plays nothing.
- **A pick while movement input isn't neutral does not play.** The wheel still closes.

## Wheel UI

- A new wheel element in `Hud.uxml`, styled in `Hud.uss`. It is drawn with `painter2D`, the same approach as `ReviveProgressWheel`.
- It shows up to 8 ring slices, following the catalog order and slot layout above.
- Each slice always shows its `DisplayName`. It also shows its `Icon` when one is set.
- The highlighted slice is visibly distinct.
- **The wheel is hidden when closed, and the crosshair is hidden while it is open.**
- The wheel only repaints when the highlight or pointer changes. No per-frame work happens while it's closed.

## Animation

### Graph

- **`AvatarAnimationGraph` gets an emote input on its `states` mixer**, as a new `AvatarPose` entry. `AvatarAnimationState` controls it. The emote's clip playable is rebound when the emote changes.
- **The clip playable uses the same settings as the other state clips:** playable IK on, foot IK off, time driven manually.
- **No root motion is applied.** `applyRootMotion` stays off, and the avatar's placement still comes from `AvatarPresentation`.
- **Playback:**
  - Blending in and out takes a fixed 0.2 s.
  - Emote time advances at `dt × Speed`.
  - Loop emotes wrap forever.
  - One-shot emotes start their 0.2 s blend back 0.2 s before the clip ends, so they finish fully blended out on the last frame.

### Layers and IK while an emote plays

- **Arm layers (`AvatarArmLayers`)**: weight 0.
- **Hand IK (`AvatarArmIK` / `AvatarHandTargets`)**: weight 0.
- **Finger layers (`AvatarFingerLayers`)**: fade to weight 0 with the 0.2 s emote blend so the clip drives the fingers. They fade back afterwards.
- **Head look**: off on every client, including the owner's own body.
- **Foot IK (`AvatarHumanoidIK`)**: stays on. A foot counts as planted using the height-based stride test instead of the standing-still rule. IK only corrects feet that are actually near the ground, and raised feet are not pinned down. Pelvis correction and slope handling are unchanged.
- **Spring bones**: stay on.

### Body facing

- **The existing turn logic in `AvatarPresentation` still runs.**
- **The owner holds `MoveInput.Facing` at the emote-start yaw while emoting.** As a result, the body doesn't turn while the camera orbits.
- When the emote ends, `Facing` goes back to `Yaw`. The body then turns to it, with observers seeing the usual body-yaw smoothing.
- Sliding during the turn is accepted.

## Held items

- **During an emote, the held item is hidden on every client**, and its arm pose and hand IK are removed.
- The equipped item and inventory state don't change.
- The item's visuals come back when the emote ends.

## Emote lifetime and cancellation

### Owner-side ends

The emote ends, and the owner sends stop, when any of these happens:

- **Movement:** any movement input, or a Jump press.
- **Gameplay actions:** Use, DirectUse, Drop, Interact, SecondaryInteract, Hotbar1–8, Previous or Next. The emote ends first, then the action runs as normal.
- **World impact:** the motor applies any world impact to the player, including a sideways nudge that doesn't make them airborne.
- **Other states:** the player becomes carried, seated or downed.

**These don't end an emote:**

- Sprint on its own
- camera look or orbit
- opening the inventory or session panel
- damage without an impulse

### Natural end

A one-shot emote ends on every client when its clip finishes at `Speed`. No message is sent.

### Replacement

Picking a new emote while one is playing replaces it. The owner sends the new id, with no separate stop.

## Networking

- **One reliable message with a one-byte payload:**
  - `0`–`7` means play that catalog index.
  - `0xFF` means stop.
  - Observers ignore invalid indices.
- **Route:** the owner applies the change locally right away, then sends a `ServerRpc(byte)`. The server relays it through an `ObserversRpc(ExcludeOwner = true)`, following the existing `ServerLook` / `ObserversLook` pattern. That includes applying it on a host that is observing, not owning.
- **Observers:**
  - Start or replace the emote on a play message, and end it on stop.
  - Also end the emote locally, without a message, when that player *changes into* airborne, carried, seated or downed after the emote started. A play message that arrives while the observer briefly shows the player in one of those states still plays.
  - End one-shots locally when the clip finishes.
- **Emotes in progress are not replayed to late joiners.** There is no SyncVar.
- **There is no rate limit.** Clients are trusted.
- **Look samples are paused while emoting.** `PlayerAvatarPresentation` stops sending look samples while its player emotes, and sends one reliable sample when the emote ends.

## Owner view

### Owner body lifetime

- **The owner's full-body avatar is preloaded at spawn.** It is kept dormant while in first person: animation isn't evaluated, spring bones are disposed and renderers are hidden. While dormant it costs nothing per frame.
- **It wakes up for emotes and for being downed.** On waking, animation, springs and renderers turn back on, and the animation state and motion are snapped and reset so it doesn't blend from a stale pose.
- **It goes back to dormant** after the emote ends or after revival. It is not released.
- **Being downed uses the preloaded body immediately**, so the owner's ragdoll no longer starts as the capsule proxy while the body loads.
- **Changing appearance rebuilds the owner's body** while it is dormant.
- **The owner's full body never picks up first-person hand targets.** Item, carry, contact and free hand poses and IK never apply to it.
- **The first-person hands (`LocalFirstPersonHands`) stay a separate instance.**

### Emote camera

- **`PlayerPresentation` uses a third-person orbit camera while the owner emotes**, reusing the downed orbit logic:
  - 3 m distance with sphere-cast wall collision.
  - The pivot is at the avatar's head height.
  - `Pitch` and `Yaw` from the look input orbit the camera.
- **Start:** the camera pulls back from the first-person eye to the orbit distance over 0.2 s, starting directly behind the player along the current look direction.
- **End:** the camera pulls back in over 0.2 s. When the pull-in finishes, the full body goes dormant again and first-person view resumes, facing the camera's look direction. If the emote ended because the player was downed, the downed camera takes over directly.
- **The first-person hands and crosshair are hidden while emoting, not destroyed.**

## Assets the user creates

- **Eight `EmoteDefinition` assets**, one for each clip in `Assets/Art/Animations/Emotes`, each with `DisplayName`, `Loop`, `Speed` and an optional `Icon`:
  - Can Can
  - Chicken Dance
  - Dancing Maraschino Step
  - Dancing Twerk
  - Female Dance Pose
  - Female Dance Pose(1)
  - Jazz Dancing
  - Wave Hip Hop Dance
- **One `EmoteCatalog` asset** listing them in wheel slot order.
- **The `EmoteCatalog` reference** on `SessionController` in the `SessionRoot` prefab.

## Out of scope

- Customizing which emotes go in which slot
- Replaying emotes for late joiners
- Emote audio
- Per-emote blend times
- Rate limiting
- An on-screen Emote prompt
- Root-motion emotes

## Acceptance

1. **Keyboard, playing:** holding R shows the wheel with every catalog emote's name and any icons. Mouse movement highlights slices without moving the camera. Clicking plays the highlighted emote and closes the wheel. Releasing R over a slice also plays it, and releasing R in the deadzone plays nothing.
2. **Controller, playing:** holding D-pad Left opens the wheel. The right stick highlights a slice, and the highlight stays when the stick recenters. A or RT plays immediately, and releasing D-pad Left plays the highlight.
3. **Blocked while open:** Jump, items, interaction, hotbar and inventory do nothing. WASD still moves. Escape closes the wheel and opens the session panel.
4. **Cancelling a loop:** a loop emote plays until movement starts. Looking around or orbiting doesn't cancel it.
5. **One-shot:** a one-shot plays once and blends back to idle.
6. **Physical disruption:** being hit by a cart, receiving any impulse, being picked up, being seated or being downed ends the emote at once, and the normal animation state takes over.
7. **Owner's view:** the camera pulls out to third person behind the player and orbits with look input. The body keeps its facing, and the full avatar is visible with the held item hidden. Afterwards the camera returns to first person facing where the camera looked, and the hands, crosshair and held item come back.
8. **Observers:**
   - They see the same emote start and stop with no head look, feet still planted by IK, and no root motion.
   - Fingers follow the clip, and the held item is hidden.
   - A player who joins mid-emote sees that player in their normal state.
9. **Downed with a preloaded body:** when the owner is downed, the camera switches to the downed view, and the ragdoll shows the full avatar from the first frame instead of the capsule proxy.
10. **F key:**
    - F disposes at the cauldron and exits a vehicle.
    - R does nothing but open the wheel.
    - The remap panel lists `Emote` and `Secondary Interaction / Exit Vehicle`, and no longer lists `Exit Vehicle`.
    - The cart hints show F (keyboard) or D-pad Right (controller) for Exit.
