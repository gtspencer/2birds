# Emote Review Implementation

## Fixed

### 2. Scrolling the mouse wheel changes the item without ending the emote: fixed
Confirmed. `Player/Previous` and `Player/Next` have empty `Keyboard&Mouse` bindings. The mouse wheel only reaches item cycling through `UI/ScrollWheel` → `InventoryInputHandler.Scroll`.
- `PlayerInputReader` caches `UI/ScrollWheel` in `OnStartClient`.
- The emote cancel check now also ends the emote when `scrollWheel.ReadValue<Vector2>().y != 0f`. This matches how Hotbar/Previous/Next behave: the selection change and the emote end happen in the same frame.

### 4. Each emote sends two reliable look samples: fixed
Confirmed. `Changed` fires with `Active == false` at `End` and again when the owner's pull-in finishes. It also fires from `ResetLocal`. Each of these set `contextDirty`.
- `PlayerAvatarPresentation.EmoteChanged` now tracks `emoteWasActive` and only marks the context dirty on the change from active to inactive.

### 6. Redundant guards and state: fixed
Every point was confirmed:
- `PlayerEmote.Play`: the only caller is `PlayerInputReader.Pick`. It runs in the owner-only `ReadInput`, straight after `UpdateWheel` checks `WheelAvailable` (which includes `CanStart`). `Highlight` can only hold a slot where `catalog.Get` returned a value (`EmoteWheelSelection.Slot`), and that also means `catalog` isn't null. The re-checks have been removed.
- `PlayerEmote.Stop` / `Disturbed`: the motor only raises `Disturbed` when `IsOwner && !IsReconciling`, and the input reader only runs for the owner. The `Disturbed` wrapper is gone, `motor.Disturbed` subscribes to `Stop` directly, and `Stop` no longer checks ownership.
- `AvatarFingerLayers.layerWeight`: `AvatarInstance.Evaluate` calls `SetWeight` after both `Select` calls every frame, and `LocalFirstPersonHands` never calls `SetWeight`. The field has been removed. `Select` writes `1f` again, as it did before the emote change.

### 7. Saved overrides for the removed `ExitVehicle` bindings are never cleared: fixed
Confirmed. The prefs were only rewritten when the Vector2 repair ran, so overrides for removed binding ids were never pruned.
- `InputBindings.Load` now compares `SaveBindingOverridesAsJson()` with the stored string and saves when they differ.
- This makes the separate `repaired` flag redundant, because a repair always changes the JSON, so it has been removed.
- The pruning is general. It also cleans up after any future binding removal, so it isn't a one-off migration.

## Not changed

### 1. Emote clips don't bake root rotation or height into the pose: confirmed, but the fix is a user-side import step
The metas match the review:
- Can Can, Chicken Dance, Dancing Maraschino Step, Dancing Twerk and Wave Hip Hop Dance have `loopBlendOrientation/PositionY/PositionXZ: 0`.
- Female Dance Pose, Female Dance Pose(1) and Jazz Dancing have `clipAnimations: []`.
- `idle` bakes all three.

With `applyRootMotion = false`, root motion that isn't baked is dropped. These are importer settings. Per AGENTS.md, `.meta` files are left to Unity, so this has to be changed in the Inspector. **User action** (Animation tab of each emote FBX):
1. For the three clips that have no clip settings, add the clip (the `+` in the Clips list, or just Apply once so the settings are stored).
2. Root Transform Rotation: **Bake Into Pose** on, Based Upon *Original*.
3. Root Transform Position (Y): **Bake Into Pose** on, Based Upon *Original*.
4. Root Transform Position (XZ): **Bake Into Pose** on for clips that stay in place. Leave it off only for clips that travel.
5. Loop emotes: Loop Time on. Also turn on Loop Pose if the seam pops.

### 3. The owner's camera starts and ends inside the visible full body: deferred until visual validation
The geometry holds up: with `BlendDuration = 0.2 s`, the orbit distance stays under about 0.5 m for roughly the first and last 50 ms. It isn't changed yet, for three reasons:
- The review itself says to confirm the flash visually before changing anything.
- The proposed fix is incomplete. `AvatarInstance.renderers` is collected once when the avatar is set up. Hat and cosmetic renderers are attached later through `AvatarCosmeticPresentation`, so `AvatarInstance.SetVisible` alone wouldn't hide the hat, which is one of the objects the finding names.
- A complete fix touches `PlayerEmote`, `PlayerAvatarPresentation`, `AvatarPresentation` and the cosmetics. It would also have to survive `AvatarPresentation.Commit` re-enabling renderers when the avatar is swapped mid-emote. That is a lot of cross-system state for a flash of 3–6 frames that hasn't been observed yet.

If validation shows the flash, the fix should hide both the body renderers and the body cosmetics until the camera clears the head (`ViewWeight` of about 0.35, which is ≈0.85 m).

### 5. Observers end the emote on a single-frame "airborne" reading: not changed, speculative
The review describes this as something to watch for, not a confirmed bug. The `landingFrames >= 2` check in `AvatarAnimationGraph` is there to delay the landing transition (`grounded && velocity.y <= 0.5`). It isn't evidence that `Grounded` flickers on observers. Adding a debounce without an observed mismatch would be speculative code. Revisit only if observer validation shows emotes ending on one client but not the others.

### 8. The HUD rewrites styles on every pointer move: not changed, not worth it
When a UI Toolkit inline style is set to the value it already has, the write is skipped and nothing is marked dirty. The repeated `display` writes in `RefreshEmoteWheel` / `RefreshCrosshair` therefore cost only a comparison. Tracking the last open state would add state without a measurable benefit.

## Visual validation
1. **Scroll cancel (finding 2):** on mouse and keyboard, start a looping emote, then scroll the mouse wheel. The emote ends and the selected item changes in the same frame. Scrolling while the wheel is open still does nothing.
2. **Single reliable sample (finding 4):** with a network profiler or a breakpoint on `ServerLook` with `Channel.Reliable`, finish an emote. Only one reliable look sample is sent, at the end, not a second one when the camera finishes pulling in.
3. **Play/Stop still work (finding 6):** start an emote from the wheel, then end it by moving, jumping, using an item, and being bumped by a cart (motor `Disturbed`). Each one ends the emote on the owner and on an observer.
4. **Finger layers (finding 6):** hold an item, then start and end an emote. The fingers blend out to the emote pose and back without snapping. The first-person hands' fingers still pose normally.
5. **Stale bindings (finding 7):** if you had an `ExitVehicle` override, launch twice. The "no existing binding" warning appears at most on the first launch.
6. **Camera flash (finding 3):** start and end an emote in third person and watch the first and last few frames of the camera pull-out/pull-in for hair, hat or the back of the head filling the screen. Report back if it's visible.
7. **Root motion (finding 1, after the import changes):** squat or hop emotes keep the feet on the floor, spins rotate the body, and one-shot emotes blend back to idle without a pop.
