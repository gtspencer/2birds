# Emote Review

Scope: the working-tree changes that implement `Emote_Plan.md`. Out of scope: `profiler-skill-v2.md` and `Assets/Game/Materials/Ghost.mat`, which are unrelated, and the `.fbx.meta` diffs, which are Unity importer output (they are covered only in finding 1).

The code follows the plan closely. No crashes or blocking logic errors were found in the input, networking, dormancy, ragdoll or HUD flows. The biggest risk is in the clip import settings, not the code.

Before validating: the `EmoteDefinition` assets, the `EmoteCatalog` asset and the `SessionController` → `Emote Catalog` reference don't exist yet. Until they do, the wheel opens empty and nothing can play.

---

## 1. High: emote clips don't bake root rotation or height into the pose

**Location**: `Assets/Art/Animations/Emotes/*.fbx.meta` (Animation tab of each clip)

**Problem**
- The avatar plays clips with `applyRootMotion = false` (`AvatarInstance.Stage`). Any root motion that isn't baked into the pose is dropped.
- All existing locomotion clips (`idle`, `Falling`, strafes) bake Rotation, Y and XZ into the pose (`loopBlendOrientation/PositionY/PositionXZ: 1`).
- Can Can, Chicken Dance, Dancing Maraschino Step, Dancing Twerk and Wave Hip Hop Dance bake none of them (all `0`).
- Female Dance Pose, Female Dance Pose(1) and Jazz Dancing have no clip settings at all (`clipAnimations: []`). They use importer defaults: nothing baked and Loop Time off.

**Why it matters**
- Vertical body motion is lost: squats, hops and twerks keep the hips at a fixed height. Feet lift off the floor, and foot IK won't pull them down, because the spec says raised feet are not pinned.
- Yaw motion is lost: spins and hip twists get locked to the root's facing.
- As a result, acceptance item 8 ("feet still planted by IK") and the one-shot "blends back without a pop" check will likely fail visually, even though the code is correct.

**Fix** (user-side import settings; add this to the plan's user steps)
- For every emote clip, turn on **Bake Into Pose** for Root Transform Rotation and Root Transform Position (Y), with Based Upon set to *Original*, to match the locomotion clips.
- Root Transform Position (XZ):
  - Bake it for clips that stay roughly in place. This keeps weight shifts and foot contact.
  - Leave it unbaked only for clips that travel, so the travel is discarded. That matches the spec's "no root motion".
- Add clip settings for the three clips that have none.
- For clips used as loop emotes, turn on Loop Time and Loop Pose if the loop seam pops.

---

## 2. Medium: scrolling the mouse wheel changes the item without ending the emote

**Location**: `Player/PlayerInputReader.cs:198` (the `emoteCancels` check), `Player/InventoryInputHandler.cs:32` / `:73`

**Problem**
- On keyboard and mouse, item cycling uses `UI/ScrollWheel`, not `Player/Previous` / `Player/Next`. Those actions have empty mouse bindings.
- `emoteCancels` only contains the `Player` map actions.
- Scrolling therefore changes the selected item while the emote keeps playing, with the item hidden.

**Why it matters**
- The spec lists Previous/Next among the actions that end an emote.
- Hotbar keys and the controller shoulder buttons do end it, so mouse users get different behavior for the same action.
- Scroll is already treated as Previous/Next elsewhere: the wheel blocks it through `InventoryInputHandler.Available`.

**Fix**
- Cache `InputSystem.actions.FindAction("UI/ScrollWheel")` in `OnStartClient`.
- Add `|| scrollWheel.ReadValue<Vector2>().y != 0f` to the cancel condition at line 198.
- The `performed` callback for `InventoryInputHandler.Scroll` runs earlier in the same input update. The selection change therefore lands in the same frame the emote ends, like Hotbar/Previous/Next.

---

## 3. Medium: the owner's camera starts and ends inside the visible full body

**Location**: `Player/PlayerAvatarPresentation.cs:120-126` (`RefreshBody`), `Avatars/AvatarInstance.cs:191-196`, `Player/PlayerPresentation.cs:128-133`

**Problem**
- `Begin` wakes the full body immediately, and its renderers turn on in the same frame.
- The orbit distance is `3 m × SmoothStep(ViewWeight)`: about 8 cm at `ViewWeight` 0.1 and about 30 cm at 0.2. The camera therefore stays at the eye, inside the head, for roughly the first 3–4 frames of the pull-out.
- The same happens on the last frames of the pull-in, because the body only goes dormant when `ViewWeight` reaches 0.
- Spring bones are rebuilt when the body wakes (`ApplyFeatures` → `ReconstructSpringBone`), so hair is also settling during those frames.

**Why it matters**
- Hair cards, the hat, the neck and shoulders are likely to flash across the view at the start and end of every emote.
- The near clip plane (0.1 m) and backface culling won't hide double-sided hair or the hat.

**Fix**
- Keep the body awake from `Begin` so it animates, but keep its renderers off while the camera is near the head.
- Example:
  - `PlayerEmote` exposes `BodyVisible => ViewWeight > ~0.35f` and invokes `Changed` when that value flips.
  - `RefreshBody` calls a new `AvatarPresentation.SetBodyVisible(bool)`, which forwards to `AvatarInstance.SetVisible`.
- This is event-driven, with no per-frame writes, and also hides the spring settle.
- Confirm the flash during visual validation 4 before changing anything.

---

## 4. Low: each emote sends two reliable look samples, not one

**Location**: `Player/PlayerAvatarPresentation.cs:126`

**Problem**
- `EmoteChanged` sets `contextDirty` whenever `!Emote.Active`. That is true both at `End` and again when the owner's pull-in finishes (the `Presenting` → false `Changed`).
- `LateUpdate` sends a reliable sample each time.

**Why it matters**
- The spec asks for one reliable sample when the emote ends.
- It's a small extra reliable message on every emote.

**Fix**
- Only mark dirty on the change from active to inactive. For example, track `wasActive` and use `if (wasActive && !Emote.Active) contextDirty = true; wasActive = Emote.Active;`.

---

## 5. Low: observers end the emote on a single-frame "airborne" reading

**Location**: `Player/PlayerEmote.cs:92-95`, `:107-108`

**Problem**
- Observers end the emote locally the first frame `motor.Grounded` turns false.
- The avatar animation state already smooths this with `landingFrames >= 2`, which suggests `Grounded` can flicker on non-owned players. A likely cause is reconcile corrections, for example while standing on an uneven cart roof.

**Why it matters**
- If the observer's copy reads airborne while the owner's doesn't, the emote ends only on that observer. It stays out of sync until the next emote, because no message fixes it.
- For a stationary player this is unlikely, so treat it as something to watch for, not a confirmed bug.

**Fix**
- If you see mismatches during observer validation, only count the airborne flag after it has held for a few frames (for example, about 0.1 s).
- Alternatively, require vertical speed above a small threshold.

---

## 6. Low: redundant guards and state

**Locations and fixes**
- **`Player/PlayerEmote.cs:39-45` (`Play`)**: it re-checks `CanStart` and `catalog.Get(index)`.
  - The only caller is `PlayerInputReader.Pick`, which runs straight after `UpdateWheel` checks `WheelAvailable`, and that includes `CanStart`.
  - `Highlight` is only ever set to a slot where `catalog.Get` isn't null.
  - AGENTS.md says not to add handling for impossible cases, so drop these checks.
- **`Player/PlayerEmote.cs:47`, `:110`**: the owner check runs three times: the motor only raises `Disturbed` when `IsOwner`, then `Disturbed()` checks again, then `Stop()` checks again.
  - Both callers of `Stop` are owner-only, so one guard is enough.
- **`Avatars/AvatarFingerLayers.cs:30`, `:90`**: the `layerWeight` field has no effect.
  - `AvatarInstance.Evaluate` calls `SetWeight` right after both `Select` calls every frame, which overwrites both inputs.
  - `LocalFirstPersonHands` never calls `SetWeight`.
  - Remove the field, let `Select` set `1f` as before, and have `SetWeight` only write the inputs.

---

## 7. Low: saved overrides for the removed `ExitVehicle` bindings are never cleared

**Location**: `Player/InputBindings.cs:85-111`

**Problem**
- Players who rebound Exit Vehicle keep overrides for binding ids that no longer exist.
- The Input System logs a warning for each override it can't match. The prefs are only rewritten when `repaired` is set, so the warning comes back on every launch.

**Why it matters**
- It's log noise only. The spec already accepts that these overrides are dropped.

**Fix**
- After `LoadBindingOverridesFromJson`, compare `asset.SaveBindingOverridesAsJson()` with the stored string.
- If they differ, save the new string. That prunes the dead entries once.

---

## 8. Nit: the HUD rewrites styles on every pointer move

**Location**: `UI/HudController.cs:441-452`

`RefreshEmoteWheel` runs on every mouse move while the wheel is open, and each time it rewrites `emoteOverlay.style.display` and calls `RefreshCrosshair`. Only the open/close change needs those writes. You could track the last open state and skip them when it hasn't changed. The cost is small.
