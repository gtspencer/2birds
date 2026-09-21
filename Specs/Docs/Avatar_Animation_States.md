# Adding avatar animations and states

Adding a new state means connecting **a gameplay condition** to **a shared animation clip and presentation pose**. The current system requires code changes for a new pose; importing a clip does not automatically make it selectable.

This guide uses one looping swimming animation as the example. Water detection, buoyancy, movement controls, and swimming physics are separate gameplay work. The snippets below describe additions to make; swimming is not an existing implemented state.

For importing an avatar and using the showcase scene, see [Avatar_Setup.md](Avatar_Setup.md).

## The path from player state to animation

```text
Gameplay knows whether this player is swimming
    -> PlayerAvatarPresentation.CaptureInput()
    -> AvatarPresentationInput.Swimming
    -> AvatarAnimationState selects AvatarPose.Swimming
    -> AvatarAnimationGraph blends to the shared Swim clip
```

Each responsibility has a specific home:

| File | Change for swimming |
| --- | --- |
| [AvatarAnimationSet.cs](Assets/Game/Runtime/Avatars/AvatarAnimationSet.cs) | Add a shared `Swim` clip reference and include it in completeness checks. |
| [AvatarProcessor.cs](Assets/Game/Editor/AvatarProcessor.cs) | Add the source-file mapping and looping import/assignment. |
| [AvatarPresentation.cs](Assets/Game/Runtime/Avatars/AvatarPresentation.cs) | Add `Swimming` to `AvatarPresentationInput`. |
| [PlayerAvatarPresentation.cs](Assets/Game/Runtime/Player/PlayerAvatarPresentation.cs) | Read the gameplay swimming state into that input. |
| [AvatarAnimationGraph.cs](Assets/Game/Runtime/Avatars/AvatarAnimationGraph.cs) | Add the pose, selection/exit rules, blend weights, clip clock, and playable node. |
| [AvatarHumanoidIK.cs](Assets/Game/Runtime/Avatars/AvatarHumanoidIK.cs) | Suppress ground foot/pelvis correction while swimming. |

You do not need another `AvatarSettings` asset, an `AvatarRegistry` entry, or a prefab per animation state. The existing animation set is shared by all processed avatars and uses Humanoid retargeting. Avatar IDs identify characters, not states. No Animator Controller or Animator trigger parameter is involved.

## 1. Add the animation asset

Add a Humanoid swimming cycle, for example `Assets/Art/Animations/Swimming.fbx`. Start with one cycle playing at its authored speed. This initial state will use the same cycle whether the swimmer is moving or stationary; separate treading-water and movement clips can come later if needed.

Add a field to `AvatarAnimationSet`:

```csharp
public AnimationClip Swim;
```

Extend `IsComplete` so a missing, non-Humanoid, or zero-length Swim clip makes the set incomplete. The graph will require this slot, so assign it before running any avatar presentation. The shared set will then contain 13 clips.

In `AvatarProcessor`:

1. Append `Swimming.fbx` to `ClipFiles`.
2. Keep it outside the eight walk/run calibration records. A fixed-speed swim loop only needs an `AnimationClip` reference.
3. Assign the new imported clip to `draft.Swim` in `PrepareAnimations`.
4. Use the existing Humanoid import policy: full source take, looping enabled, root motion baked, and animation events removed.

**Update the assignment chain explicitly.** It currently ends with `else draft.Seated = clip;`. If you only append a filename, the swimming clip will overwrite Seated. The end of the chain should distinguish both slots:

```csharp
else if (i == 11) draft.Seated = clip;
else if (i == 12) draft.Swim = clip;
```

The current loop rule makes every mapped clip except Jump loop, so an appended swimming cycle follows that rule. For another type of state, choose its loop behavior deliberately.

Once the code changes are in place, open **Two Birds > Process Avatar**, select an existing VRM, enable **Prepare shared clips**, and click **Process**. This populates the shared animation asset. All registered avatars use that same set; adding a clip does not require regenerating every avatar prefab.

## 2. Supply a gameplay swimming condition

Gameplay must expose whether a player is swimming. The presentation system consumes that answer; it should not independently detect water or change the player's movement mode to make an animation play.

For a simple presentation input, add this field to `AvatarPresentationInput`:

```csharp
public bool Swimming;
```

In `PlayerAvatarPresentation.CaptureInput()`, populate it from the component that owns the player's swimming state. For example, assuming gameplay has introduced a cached `swimmingState` component with an `IsSwimming` property:

```csharp
Swimming = swimmingState.IsSwimming,
```

`swimmingState` is an example name, not an existing project API. Cache the actual component in the adapter's initialization, alongside its other gameplay references.

If swimming genuinely becomes a movement mode in gameplay, derive the presentation flag from that mode instead. `MovementMode` currently contains only Walking, Airborne, and External. Appending `Swimming` to that enum alone does not implement swimming: the motor also determines and updates its mode. Choose one gameplay source of truth and translate it in the adapter.

### Remote players need the same condition

`AvatarPresentationInput` is local presentation data. Adding a field to it does **not** replicate that field.

Use an existing gameplay state visible on remote players when one is available. Otherwise, the gameplay feature needs to communicate its swimming state, including the initial value for late observers. Update that state when swimming begins or ends; animation selection and time remain local. Do not send clips, animation weights, normalized times, or bone poses over the network.

## 3. Add the presentation pose and selection rules

In `AvatarAnimationGraph.cs`, extend the existing enum. A count sentinel makes the state-sized allocations and loops explicit:

```csharp
internal enum AvatarPose
{
    Locomotion, Jump, Fall, Seated, Swimming, Count
}
```

Update every place that currently assumes **four base poses**:

| Location | Required adjustment |
| --- | --- |
| `AvatarAnimationState.Weights` and `from` | Allocate `(int)AvatarPose.Count` entries. Initialize the Locomotion weight to 1. |
| State initialization and transition loops | Iterate over the full weight array. |
| `Array.Copy(Weights, from, 4)` | Copy the full weight array. |
| Base `states` mixer construction | Allocate `(int)AvatarPose.Count` inputs. |
| `SampleSeated()` | Set every base-state weight, leaving only Seated at 1. |
| Graph evaluation's state-weight loop | Write every base-state mixer weight. |

Do not replace every `4` in the file: the directional mixers and `Direction` array still have four cardinal directions. The eight walk/run clips also remain unchanged.

Choose where swimming belongs in the state precedence. A reasonable initial policy is:

1. Pending attachment context retains the previous pose.
2. Seated uses Seated.
3. Carried without release preview uses idle locomotion.
4. Swimming, when no release preview is active, uses Swimming.
5. Otherwise use the existing grounded and airborne rules.

This adds a branch before ordinary grounded/airborne selection:

```csharp
else if (input.Swimming && !input.ReleasePreview)
{
    next = AvatarPose.Swimming;
    landingFrames = 0;
}
```

Decide the exits as well as the entry. Leaving swimming onto land should return to locomotion through the landing policy. Leaving into the air should select Jump for ascent or Fall for descent/neutral vertical speed. Include the previous Swimming pose in the airborne-entry condition; the current condition only recognizes initial entry, Locomotion, Seated, or a renewed upward crossing. Without that adjustment, a swimmer leaving water at zero vertical speed can retain the swimming pose.

Handle the first grounded frame after swimming explicitly too: reset the landing counter on entry and ensure the existing two-frame landing confirmation resolves to Locomotion. Do not let an old counter or stale Swimming pose bypass those exit rules.

Use the existing smoothstep weight transition, starting from the current weight vector. For this example, `0.18` seconds is a reasonable initial enter/exit blend to review visually; it is a proposed swimming default. Preserve the existing attachment transition precedence.

## 4. Add the clock and playable

Keep the swimming clock in `AvatarAnimationState`, which is owned by the stable host:

```csharp
internal float SwimTime;
```

Advance it in `Advance()` using the provided, already bounded frame delta:

```csharp
SwimTime = Mathf.Repeat(SwimTime + dt, clips.Swim.length);
```

This follows the existing looping idle/fall/seated clocks. Continuing the clock across avatar replacement lets the new skeleton resume the same swimming cycle. Do not reset it in the graph constructor or when a new avatar binds.

In `AvatarAnimationGraph`, add a `swim` playable field. Construct and connect it once when building the graph:

```csharp
swim = Create(clips.Swim);
graph.Connect(swim, 0, states, (int)AvatarPose.Swimming);
```

In `Evaluate()`:

```csharp
swim.SetTime(state.SwimTime);
```

The expanded state-weight loop supplies its blend weight. Keep using `Create()` so the node follows the existing manual clock and IK policy. Keep the existing single `graph.Evaluate(0f)` call; entering swimming only changes weights and time, and does not create another graph or evaluation loop.

## 5. Apply the appropriate IK policy

Swimming feet should not seek the ground. Add swimming to the condition in `AvatarHumanoidIK.Apply()` that zeros foot and pelvis correction and skips probes. Do this explicitly, even if gameplay usually reports the swimmer as ungrounded; otherwise swimming near shallow ground can activate correction.

Reset foot-contact history once when entering or leaving swimming so a previous land contact is not reused. Keep this state policy inside the IK evaluation. Calling `SetFeatures(...)` every frame from gameplay would overwrite the host's independent feature switches and add unnecessary transition work.

Head look and native springs can remain enabled for the initial example. Active hand targets will still override the swimming arm pose. The demo's right-hand target is one such target, so clear it when reviewing the complete stroke.

A prone swimming pose may need different head-look limits or visual placement than standing. Review the imported clip first. Any necessary visual adjustment belongs in presentation; avoid rotating `Graphics`, changing the gameplay capsule, or using `YawOffset` to encode a temporary swimming pose.

## 6. Preview the new state

The existing `AvatarPresentationDemo` always supplies grounded idle input. To use it for a swimming preview after implementing the additions, change its input producer to supply `Swimming = true` and `Grounded = false`. This exercises animation selection without requiring water gameplay. Its automatic avatar swaps will then exercise state continuity while swimming.

For a clear view of both arms, have the demo omit its initial right-hand target registration or call `presentation.ClearHandTarget(AvatarIKGoal.RightHand)` after registration. Changing the host input once elsewhere will not work: the demo's `CaptureInput()` supplies it again each frame.

To preview transitions, have that input producer alternate the swimming flag and the appropriate grounded/airborne inputs. Those are explicit demo changes to make for a swimming showcase; there is currently no inspector swimming toggle. Keep changes to the preview separate from the real gameplay source of swimming state.

## Visual review

Review entry from idle, exit onto land, exit into ascent/descent, and interruption by seating or carrying. Look for a smooth loop, blended transitions, free-moving feet, and arms that follow the stroke when hand targets are absent. Watch both avatars through repeated swaps while Swimming remains active. In multiplayer, confirm observers and late joiners select swimming from the gameplay state without replicating animation time or poses.
