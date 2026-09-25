# Emote Plan

Implements `Emote_Spec.md`. Read the spec first; this plan says where each part goes and how it connects to the existing code.

## Approach

- **Data**: `EmoteDefinition` and `EmoteCatalog` ScriptableObjects. `SessionController.Emotes` exposes the catalog, the same way it exposes `Hats` and `Tattoos`.
- **Playback**: lives in the existing avatar pipeline. `AvatarAnimationState` owns the emote clip, time and blend weight. `AvatarAnimationGraph` binds one emote clip playable to a new `AvatarPose.Emote` input on the `states` mixer. `AvatarInstance` scales arm layers, hand IK, finger layers and head look by `1 - EmoteWeight`.
- **Gameplay state**: a new `PlayerEmote` MonoBehaviour, added at runtime by `PlayerAvatarPresentation` in the same way as `PlayerHandPresentation`. It is the single source of truth for `Active`, the owner's camera `ViewWeight`, `Presenting`, `FacingYaw` and one `Changed` event. Everything else reacts to `Changed`, or reads these values inside loops that already run each frame.
- **Network**: `ServerEmote(byte)` and `ObserversEmote(byte)` sit in `PlayerAvatarPresentation`, next to `ServerLook` and `ObserversLook`. Neither the Player prefab nor any other prefab changes.
- **Owner body**: `AvatarPresentation` gets a *dormant* mode. The instance GameObject is deactivated, springs are disposed, and `AvatarPresentationSystem` skips the host. The owner's body is prepared at spawn and stays dormant until an emote starts or the player is downed.
- **Input**: `PlayerInputReader` handles opening the wheel, selection, blocking, confirm and cancellation inside its existing `ReadInput`. It writes the selection to a shared `EmoteWheelSelection` object, and the HUD redraws from that object's `Changed` event.
- **HUD**: a new `EmoteWheel` VisualElement drawn with `painter2D`, following the `ReviveProgressWheel` pattern: a container in `Hud.uxml` and the element built in code.

### Shared constants

- `EmoteDefinition.BlendDuration = 0.2f`. This one constant drives the animation blend, the one-shot end time and the camera pull.
- `EmoteCatalog.Capacity = 8`.
- `PlayerEmote.StopId = 0xFF`.

## New files

| File | Purpose |
| --- | --- |
| `Assets/Game/Runtime/Emotes/EmoteDefinition.cs` | ScriptableObject |
| `Assets/Game/Runtime/Emotes/EmoteCatalog.cs` | ScriptableObject |
| `Assets/Game/Runtime/Player/PlayerEmote.cs` | Emote lifecycle and state |
| `Assets/Game/Runtime/Player/EmoteWheelSelection.cs` | Wheel open state, pointer and highlight. Written by input, read by HUD |
| `Assets/Game/Runtime/UI/EmoteWheel.cs` | VisualElement drawn with `painter2D` |

---

## 1. Data

### `EmoteDefinition.cs`
```csharp
[CreateAssetMenu(menuName = "Two Birds/Emote Definition")]
public sealed class EmoteDefinition : ScriptableObject
{
    public const float BlendDuration = 0.2f;
    public AnimationClip Clip;
    public string DisplayName;
    public bool Loop;
    [Min(0.01f)] public float Speed = 1f;
    public Sprite Icon;
}
```

### `EmoteCatalog.cs`
```csharp
[CreateAssetMenu(menuName = "Two Birds/Emote Catalog")]
public sealed class EmoteCatalog : ScriptableObject
{
    public const int Capacity = 8;
    public List<EmoteDefinition> Entries = new();
    public EmoteDefinition Get(int index)
    {
        if (index < 0 || index >= Capacity || index >= Entries.Count) return null;
        var entry = Entries[index];
        return entry && entry.Clip && entry.Clip.length > 0f ? entry : null;
    }
    private void OnValidate() { if (Entries.Count > Capacity) Debug.LogError($"Emote catalog holds at most {Capacity} entries.", this); }
}
```
All code uses `Get`. A `null` result means the slot is empty: it is not drawn, cannot be selected, and observers ignore that id.

### `SessionController.cs`
- Add `[SerializeField] private EmoteCatalog emoteCatalog;` below `tattooCatalog` (line 29).
- Add `public EmoteCatalog Emotes => emoteCatalog;` next to `Hats` and `Tattoos` (lines 56–57).

---

## 2. Input asset, bindings and hints

### `Assets/InputSystem_Actions.inputactions` (plain JSON, 4-space indent)
- **Player map, remove** the `ExitVehicle` action (id `53eab02e-…`) and both of its bindings (`465f9a7c-…` `<Keyboard>/f`, `9c622164-…` `<Gamepad>/rightStickPress`).
- **Change** the `SecondaryInteract` keyboard binding (id `bc213c5f-b3f8-40eb-9215-3759bfa99312`) from `<Keyboard>/r` to `<Keyboard>/f`.
- **Add** this action after `SecondaryInteract`:
  ```json
  { "name": "Emote", "type": "Button", "id": "5aef6057-7d02-4298-814a-5f47b4fbd533",
    "expectedControlType": "Button", "processors": "", "interactions": "", "initialStateCheck": false }
  ```
- **Add** these bindings after the last `SecondaryInteract` binding:
  - id `d0d51867-f5c7-44b1-a7d1-f90e9ebbfae0`: `<Keyboard>/r`, groups `Keyboard&Mouse`, action `Emote`
  - id `dce1d720-8376-4c2a-a3a5-5ba22ffe6b56`: `<Gamepad>/dpad/left`, groups `Gamepad`, action `Emote`

  Both use `"name": ""`, empty `interactions` and `processors`, and `isComposite` / `isPartOfComposite` set to `false`.

### `Player/InputBindings.cs` (constructor, lines 40–58)
- Rename the label: `Add("SecondaryInteract", "Secondary Interaction / Exit Vehicle");`.
- Add `Add("Emote", "Emote");` right after `Add("Inventory", "Inventory");`.
- Delete `Add("ExitVehicle", "Exit Vehicle");`.
- `Load` needs no change. The spec accepts that saved overrides for the removed action are dropped.

### `UI/HudController.cs`
- In `EnsureCartHints` and `EnsurePassengerHints`, change `map.FindAction("ExitVehicle")` to `map.FindAction("SecondaryInteract")`. Keep `Label = "Exit"`.

---

## 3. Avatar layer

### 3a. `Avatars/AvatarAnimationGraph.cs`

**`AvatarPose` enum**: change it to `{ Locomotion, Jump, Fall, Seated, Emote, Count }`.

**`AvatarAnimationState`**
- Size `Weights` and `from` with `(int)AvatarPose.Emote`, not `Count`. The emote is not a state-machine pose, so the existing pose loop never touches it.
- Add:
  ```csharp
  internal EmoteDefinition Emote { get; private set; }
  internal float EmoteTime { get; private set; }
  internal float EmoteWeight => Mathf.SmoothStep(0f, 1f, emoteBlend);
  private float emoteBlend;
  private bool emotePlaying;
  internal void PlayEmote(EmoteDefinition value) { Emote = value; EmoteTime = 0f; emotePlaying = true; }
  internal void StopEmote() => emotePlaying = false;
  internal void ClearEmote() { Emote = null; EmoteTime = emoteBlend = 0f; emotePlaying = false; }
  ```
- At the end of `Advance`, add:
  ```csharp
  if (Emote)
  {
      emoteBlend = Mathf.MoveTowards(emoteBlend, emotePlaying ? 1f : 0f, dt / EmoteDefinition.BlendDuration);
      float time = EmoteTime + dt * Emote.Speed, length = Emote.Clip.length;
      EmoteTime = Emote.Loop ? Mathf.Repeat(time, length) : Mathf.Min(time, length);
      if (!emotePlaying && emoteBlend <= 0f) ClearEmote();
  }
  ```
- `Snap` does not touch the emote. A replacement keeps the current `emoteBlend`, and the single playable jumps to the new clip's first frame, as the spec intends.

**`AvatarAnimationGraph`**
- Add fields `private AnimationClipPlayable emote;` and `private AnimationClip emoteClip;`.
- In the constructor, `states` is already created with `(int)AvatarPose.Count` inputs. Leave the Emote input unconnected.
- Add `BindEmote(AnimationClip clip)`. If the clip differs from `emoteClip`: disconnect and destroy the old playable, then connect `Create(clip)` to `states` input `(int)AvatarPose.Emote`, or leave it empty when `clip` is null. `Create` already sets playable IK on, foot IK off and speed 0.
- In `Evaluate`, replace the `states` weight loop (line 183) with:
  ```csharp
  var clip = state.Emote ? state.Emote.Clip : null;
  if (clip != emoteClip) BindEmote(clip);
  if (emote.IsValid()) emote.SetTime(state.EmoteTime);
  float body = 1f - state.EmoteWeight;
  for (int i = 0; i < state.Weights.Length; i++) states.SetInputWeight(i, state.Weights[i] * body);
  states.SetInputWeight((int)AvatarPose.Emote, state.EmoteWeight);
  ```
- `SampleSeated` loops over `Count`, so it already sets the Emote input to 0.

### 3b. Layer and IK weights

Each change below adds a weight parameter or setting.

- **`AvatarArmLayers.Set(in AvatarArmPose pose, float weight = 1f)`**: `Output.SetInputWeight(i + 1, Mathf.Clamp01(total) * weight)`.
- **`AvatarFingerLayers`**
  - Add `private float layerWeight = 1f;` and `internal void SetWeight(float weight)`, which stores the value and sets `Output` inputs 1 and 2 to `hand.Selected ? weight : 0f`.
  - In `Select`, use `layerWeight` wherever it currently uses `1f` for `Output.SetInputWeight`.
- **`AvatarArmIK.Solve(AvatarHandTargets targets, float dt, float weight = 1f)`**
  - Multiply `pose.Pitch` by `weight`.
  - Pass `weight` into the private `Solve` and multiply `positionWeight` and `rotationWeight` by it.
  - `LocalFirstPersonHands` keeps the defaults.
- **`AvatarHumanoidIK`**
  - `Probe`: `float contact = Mathf.Lerp(1f, stride, Mathf.Max(host.State.Motion, host.State.EmoteWeight));`
  - `ApplyHead`: compute `float weight = 1f - host.State.EmoteWeight`. Return with look-at weight 0 when `weight <= 0f`, the same as the existing disabled branch. Otherwise call `animator.SetLookAtWeight(weight, 0.15f, 1f, 0f, 0.5f)`.

### 3c. `Avatars/AvatarInstance.cs` `Evaluate` (lines 125–146)
```csharp
var targets = host.BodyTargets;
float free = 1f - host.State.EmoteWeight;
graph.Arms.Set(targets.Arms, free);
var leftHand = targets.Resolve(AvatarIKGoal.LeftHand); var rightHand = targets.Resolve(AvatarIKGoal.RightHand);
// Select calls unchanged
graph.Fingers.Advance(dt);
graph.Fingers.SetWeight(free);
...
using (AvatarPresentationSystem.IkMarker.Auto()) arms.Solve(targets, dt, free);
// instrumentation: targets.RoundTripMuscles instead of host.HandTargets.RoundTripMuscles
```
- **VRM eye look**
  - Add a field `private bool eyesActive;`.
  - Compute `bool eyes = host.HeadLookEnabled && host.State.EmoteWeight <= 0f;`.
  - When `eyes` is true, set `LookAtInput` as today.
  - When `eyes` is false, `eyesActive` is true and `host.HeadLookEnabled` is true, call `runtime.LookAt.SetYawPitchManually(0f, 0f)`.
  - Then set `eyesActive = eyes`.
- **Dormancy**
  - Add:
    ```csharp
    internal void SetDormant(bool value)
    {
        if (!Initialized) return;
        if (value) { ApplyFeatures(); gameObject.SetActive(false); }
        else { gameObject.SetActive(true); ApplyFeatures(); ResetMotion(); }
    }
    ```
  - In `ApplyFeatures`, change the springs line to `bool springs = !physical && host.AnimationEnabled && host.SpringsEnabled && !host.Dormant;`.
  - Deactivating the GameObject also hides the hat and the tattoo decals, which `SetVisible` would not.

### 3d. `Avatars/AvatarPresentation.cs`

**New members**
```csharp
private static readonly AvatarHandTargets noTargets = new();
internal bool IgnoresHandTargets { get; set; }
internal AvatarHandTargets BodyTargets => IgnoresHandTargets ? noTargets : HandTargets;
internal bool Dormant { get; private set; }
internal bool Idle => Dormant && !NeedsPreparation && !candidate;
private bool waking;
internal void SetDormant(bool value)
{
    if (Dormant == value) return;
    Dormant = value;
    if (value) State.ClearEmote(); else waking = true;
    if (active) active.SetDormant(value);
    if (candidate && candidate.Initialized) candidate.ApplyFeatures();
}
```

**`UpdateInput`**
- If `waking` is set, treat the call as a gap: set `facingInitialized = false` and `gap = true` before the existing logic.
- In the animation block, call `State.Snap(Input, BodyYaw, settings, registry.Animations)` instead of `Advance`, then clear `waking`. Only clear it once `settings` exists.
- `reset` is already true for a gap, so `active.ResetMotion()` runs.

**Other methods**
- **`Evaluate`**: guard the active path with `if (active && !Dormant)`.
- **`Commit`**: after `active.SetVisible(true);`, add `if (Dormant) active.SetDormant(true);`. The body is still committed and cosmetics still bind through `DidBind`.
- **`PrepareRagdoll`**: add `SetDormant(false);` as the first line, so the preloaded body becomes the ragdoll straight away. Keep the current emote here, so the ragdoll starts from the emote pose.
- **`FinishRagdoll`**: call `State.ClearEmote();` before the `State.Snap(...)` line.

### 3e. `Avatars/AvatarPresentationSystem.cs` `LateUpdate`
- First loop (line 57): `if (!host || host.Failed || host.Idle) continue;`
- `evaluationOrder` build (line 79): add `&& !host.Idle`.
- The commit loop iterates `evaluationOrder`, so idle hosts are skipped there too. A dormant body that has to rebuild (it `NeedsPreparation` or has a `candidate`) is not idle, so it prepares and commits as normal.

### 3f. `Avatars/Appearance/AvatarCosmeticPresentation.cs`
This is needed to hide first-person tattoo decals together with the first-person hands.

- Add `private bool visible = true;` and:
  ```csharp
  internal void SetVisible(bool value)
  {
      visible = value;
      if (hat) hat.SetActive(value);
      foreach (var projector in projectors) if (projector) projector.enabled = value;
  }
  ```
- In `UpdateTattoo`, set `projector.enabled = visible;` before the final `SetActive(true)`.
- In `Apply`, call `hat.SetActive(visible)` after the hat is created.

---

## 4. Motor disruption event (`Player/PlayerMotor*.cs`)

- **`PlayerMotor.cs`**: add `internal event System.Action Disturbed;`.
- **`PlayerMotor.cs` `ApplyImpacts`**: inside `if (Finite(combined...))` (around line 480), add `if (IsOwner && !PredictionManager.IsReconciling) Disturbed?.Invoke();`. This follows the same guard as the `carry.ImpactDrop` precedent above it.
- **`PlayerMotor.CartContacts.cs` `AfterContactPhysics`**: at the top of `if (target > 0f)` (line 313), add `if (IsOwner && !PredictionManager.IsReconciling) Disturbed?.Invoke();`. This covers cart hits.

---

## 5. `Player/PlayerEmote.cs` (new)

`public sealed class PlayerEmote : MonoBehaviour`. `PlayerAvatarPresentation.Awake` adds it and calls `Initialize(this)`, which leaves the component disabled. `Update` runs only while `Presenting`.

```csharp
internal const byte StopId = 0xFF;
internal EmoteDefinition Current { get; private set; }
internal bool Active => Current;
internal float ViewWeight { get; private set; }            // owner camera pull, 0..1
internal bool Presenting => Active || ViewWeight > 0f;     // observers: == Active
internal float FacingYaw { get; private set; }
internal event System.Action Changed;
internal bool CanStart => network.CanGameplayActions && motor.Grounded && !seating.Seated &&
    !seating.TransitionPending && !carry.IsCarried && !carry.IsCarrying;
```

**`Initialize(PlayerAvatarPresentation owner)`**
- Cache `owner`, `owner.Presentation`, and the components `PlayerInputReader`, `PlayerMotor`, `PlayerSeating`, `PlayerCarry`, `PlayerHealth` and `PlayerNetworkState`.
- Cache `catalog = SessionController.Instance.Emotes`.
- Subscribe `motor.Disturbed += Disturbed`, then set `enabled = false`.

**Methods**
- **`Play(byte index)`** (owner only): `var definition = catalog ? catalog.Get(index) : null; if (!owner.IsOwner || !definition || !CanStart) return; Begin(definition); owner.SendEmote(index);`
- **`Stop()`** (owner only): `if (owner.IsOwner) End(true);`
- **`Receive(byte id)`** (remote): if `id == StopId`, call `End(false)`. Otherwise, if `catalog.Get(id)` is valid, call `Begin(it)`. Invalid ids are ignored.
- **`ResetLocal()`**: used by stop-client and ownership change. Clears the state without sending anything: `Current = null`, `avatar.State.ClearEmote()`, `ViewWeight = 0`, `enabled = false`. Invoke `Changed` only if something was presenting.
- **`Begin(definition)`**:
  ```csharp
  if (!Current) FacingYaw = input.Yaw;
  Current = definition; elapsed = 0f; disruption = Disruption();
  avatar.State.PlayEmote(definition);
  enabled = true;
  Changed?.Invoke();
  ```
- **`End(bool send)`**:
  ```csharp
  if (!Current) return;
  Current = null;
  avatar.State.StopEmote();
  if (send) owner.SendEmote(StopId);
  if (health.IsDowned) ViewWeight = 0f;   // downed camera takes over, no pull-in
  enabled = Presenting;
  Changed?.Invoke();
  ```
- **`Update`**:
  ```csharp
  float dt = Mathf.Min(Time.deltaTime, 0.05f);          // same clamp as AvatarPresentationSystem
  if (Current)
  {
      elapsed += dt;
      int flags = Disruption();
      bool disrupted = (flags & ~disruption) != 0;
      disruption = flags;
      if (disrupted) End(owner.IsOwner);
      else if (!Current.Loop && elapsed * Current.Speed >= Current.Clip.length - EmoteDefinition.BlendDuration * Current.Speed) End(false);
  }
  if (owner.IsOwner)
  {
      bool presenting = Presenting;
      ViewWeight = health.IsDowned ? 0f : Mathf.MoveTowards(ViewWeight, Current ? 1f : 0f, dt / EmoteDefinition.BlendDuration);
      if (presenting != Presenting) Changed?.Invoke();
  }
  enabled = Presenting;
  ```
  - A one-shot starts its blend-out 0.2 s before the clip ends. `AvatarAnimationState` keeps advancing, so the clip reaches its last frame exactly as the weight reaches 0. No message is sent.
  - Disruption ends the emote when the state **changes into** a disrupted flag after the emote started. The owner sends stop; observers end locally.
- **`Disruption()`**: `(motor.Grounded ? 0 : 1) | (carry.IsCarried ? 2 : 0) | (seating.Seated ? 4 : 0) | (health.IsDowned ? 8 : 0)`.
- **`Disturbed()`**: `if (owner.IsOwner) Stop();`. It fires on world impacts and cart hits.
- **`OnDestroy`**: unsubscribe from `motor.Disturbed`.

`Changed` fires on every start, replacement and end, and when the owner's pull-in finishes (`Presenting` becomes false). The subscribers are:

| Subscriber | Reaction |
| --- | --- |
| `PlayerAvatarPresentation` | Wakes or sleeps the body; queues the reliable look sample |
| `PlayerHeldItemPresentation` | Hides or shows the held item |
| `PlayerHandPresentation` | Hides or shows the first-person hands |
| `HudController` | Shows or hides the crosshair |

---

## 6. `Player/PlayerAvatarPresentation.cs`

**Emote plumbing**
- Add `internal PlayerEmote Emote { get; private set; }`.
- In `Awake`, after the hands setup: `Emote = gameObject.AddComponent<PlayerEmote>(); Emote.Initialize(this); Emote.Changed += EmoteChanged;`. Unsubscribe in `OnDestroy`.
- Add the RPCs next to `ServerLook`, following its pattern:
  ```csharp
  internal void SendEmote(byte id) => ServerEmote(id);
  [ServerRpc] private void ServerEmote(byte id) { if (IsClientInitialized && !IsOwner) Emote.Receive(id); ObserversEmote(id); }
  [ObserversRpc(ExcludeOwner = true)] private void ObserversEmote(byte id) { if (!IsServerInitialized) Emote.Receive(id); }
  ```

**Body lifecycle**
- Add:
  ```csharp
  private void RefreshBody()
  {
      presentation.IgnoresHandTargets = IsOwner;
      presentation.SetDormant(IsOwner && health.IsAlive && !Emote.Presenting);
      presentation.SetVisual(IsClientInitialized);
  }
  private void EmoteChanged() { if (!Emote.Active) contextDirty = true; RefreshBody(); }
  ```
- **`OnStartClient`**:
  - Call `presentation.Configure(SessionController.Instance.Avatars, true, state.Snapshot.SpawnSlot / 8f)`, which removes the `!IsOwner || health.IsDowned` condition.
  - Call `RefreshBody()` immediately after `Configure`, before `BindStore()`, so the first owner commit is already dormant.
  - The fallback line is unchanged.
- **`OnOwnershipClient`**: call `Emote.ResetLocal()` first. Replace the `presentation.SetVisual(...)` line with `RefreshBody()`.
- **`OnStopClient`**: call `Emote.ResetLocal()` before `presentation.SetVisual(false)`.
- **`LifeChanged`**: replace `presentation.SetVisual(!IsOwner || health.IsDowned)` with `RefreshBody()`.

**Look samples**
- In `LateUpdate`, add `|| Emote.Active` to the early return.
- `EmoteChanged` sets `contextDirty` when the emote ends, which sends one reliable sample.

---

## 7. Owner-body side effects

- **`Player/PlayerRagdoll.cs` `Capture`** (line 50): `var hips = !avatar.Dormant && avatar.Binding != null ? avatar.Binding.GetBone(HumanBodyBones.Hips) : null;`. The dormant body's bones are stale.
- **`Player/PlayerHandPresentation.cs`**
  - **`HeavyBody`** (line 41): `float seated = remote ? avatar.State.Weights[(int)AvatarPose.Seated] : input.Seated ? 1f : 0f;`. The owner now always has a `Binding`, but its state is not advanced while dormant.
  - **Hiding**: add `private PlayerEmote emote; private bool hidden;` and:
    ```csharp
    private void EmoteChanged()
    {
        bool value = owner.IsOwner && emote.Presenting;
        if (hidden == value) return;
        hidden = value;
        if (active) active.SetVisible(!hidden);
        cosmetics?.SetVisible(!hidden);
    }
    ```
  - **`StartPresentation`**: `emote = owner.Emote; emote.Changed += EmoteChanged; EmoteChanged();`
  - **`StopPresentation`**: unsubscribe, then set `hidden = false`.
  - **`Evaluate`**, when a new rig is committed (lines 441–448): call `nextCosmetics.SetVisible(!hidden)` after `Apply`, and use `active.SetVisible(!hidden)` instead of `SetVisible(true)`.
  - The rig keeps evaluating while hidden, so release samples stay valid if Use is pressed during the pull-in.

---

## 8. Held items

- **`Player/HeldItemPresentationInput.cs`**: add `Emoting` to the `bool` field line.
- **`Player/PlayerHeldItemPresentation.cs`**
  - Cache `emote = playerAvatar.Emote` in `StartPresentation`, subscribe `emote.Changed += ContextChanged`, and unsubscribe in `StopPresentation`.
  - In `CaptureInput`, set `Emoting = emote && emote.Presenting`.
- **`Player/HeldItemPresentationState.cs` `CanShowHeldItem`** (line 110): add `&& !input.Emoting`.
- Result:
  - `WorldItem.ApplyHeldAttachment` hides the item. `ContextChanged` already calls `registry.RefreshHolder`.
  - `SubmitTargets` clears the item's hand IK targets.
  - The arm pose is still submitted but zeroed by `EmoteWeight` in `AvatarInstance`.
  - Equipped and inventory state don't change.
  - Observers get the item back when the emote ends; the owner gets it back when the pull-in finishes.

---

## 9. `Player/EmoteWheelSelection.cs` (new)

```csharp
internal sealed class EmoteWheelSelection
{
    internal const float DeadZone = 0.45f, StickDeadZone = 0.5f, PointerTravel = 160f;
    private readonly EmoteCatalog catalog;
    internal bool Open { get; private set; }
    internal bool Controller { get; private set; }
    internal Vector2 Pointer { get; private set; }      // y up, |Pointer| <= 1
    internal int Highlight { get; private set; } = -1;
    internal event System.Action Changed;
    internal EmoteWheelSelection(EmoteCatalog catalog) => this.catalog = catalog;
    internal void Begin(bool controller) { Open = true; Controller = controller; Pointer = default; Highlight = -1; Changed?.Invoke(); }
    internal void Close() { if (!Open) return; Open = false; Pointer = default; Highlight = -1; Changed?.Invoke(); }
    internal void Look(Vector2 value, bool controller) { ... }
    private int Slot(Vector2 direction) { ... }
}
```

**`Look`**
- A zero `value` is a no-op. For the stick, that is the recentre that keeps the highlight.
- Controller: when `value.magnitude > StickDeadZone` and `Slot(value) >= 0`, set the highlight to that slot. Otherwise the highlight doesn't change, including when the stick points at an empty slot. The pointer stays `default`.
- Mouse: `pointer = ClampMagnitude(Pointer + value / PointerTravel, 1)`. The highlight is `pointer.magnitude > DeadZone ? Slot(pointer) : -1`, where an empty slot gives -1.
- Invoke `Changed` only when the highlight, pointer or `Controller` changes.

**`Slot`**: `int slot = Mathf.RoundToInt(Mathf.Repeat(Mathf.Atan2(d.x, d.y) * Mathf.Rad2Deg, 360f) / 45f) % EmoteCatalog.Capacity; return catalog && catalog.Get(slot) ? slot : -1;`. Slot 0 is at the top and slots go clockwise.

---

## 10. `Player/PlayerInputReader.cs`

**Fields**
- **Remove** `exit` and `exitBlocked`, and every use of them.
- **Add** `emoteAction`, `emoteBlocked`, `InputAction[] emoteCancels` and `PlayerEmote emote`.
- **Add** these members:
  ```csharp
  internal EmoteWheelSelection EmoteWheel { get; private set; }
  internal bool EmoteWheelOpen => EmoteWheel != null && EmoteWheel.Open;
  private bool WheelAvailable => GameplayActive && !InputSuppressed && !equipment.IsCharging && emote.CanStart;
  ```

**Properties**
- `InteractPressed`: add `&& !EmoteWheelOpen`.
- `SecondaryInteractPressed`: add `&& !EmoteWheelOpen && !seating.Seated`. A seated press only exits.

**`OnStartClient`**
- Delete the `exit =` line and find `emoteAction = actions.FindAction("Emote")`.
- `emoteCancels` holds `use`, `directUse`, `drop`, `interact`, `secondaryInteract`, `Previous`, `Next` and `Hotbar1`…`Hotbar{PlayerInventory.HotbarSize}`.
- After `session = …`: `emote = GetComponent<PlayerAvatarPresentation>().Emote; EmoteWheel = new EmoteWheelSelection(session.Emotes);`. This must run before `SetGameplay(false)`.

**`ReadInput`** (lines 140–202), in this order:
1. **Block flags**: capture `bool blockSecondary = secondaryInteractBlocked;` before its existing release check, which replaces the `blockExit` capture. Add `bool blockEmote = emoteBlocked; if (emoteBlocked && !ButtonHeld(emoteAction)) emoteBlocked = false;`.
2. **No gameplay input** (`!SessionInputAvailable || InputSuppressed` branch): call `EmoteWheel.Close();` first.
3. **Movement**: after `movement = …`, add `if (emote.Active && movement.sqrMagnitude > 0f) emote.Stop();`. This runs even while the wheel is open.
4. **Wheel open**: read `delta` as today, then `if (EmoteWheel.Open) { UpdateWheel(delta); return; }`. This goes before the `jumpPending` line, so jump and look are frozen while the wheel is open.
5. **Normal path**: keep the existing jump and look code.
6. **Vehicle exit**: replace the exit block with `if (!blockSecondary && secondaryInteract.WasPressedThisFrame() && seating.Seated && !seating.TransitionPending) { suppressedInteractionFrame = Time.frameCount; seating.RequestExit(); return; }`.
7. **Open and cancel**: after `if (seating.TransitionPending || seating.PlacementPending) return;`:
   ```csharp
   if (!blockEmote && emoteAction.WasPressedThisFrame() && WheelAvailable) { EmoteWheel.Begin(presentation.IsController); return; }
   if (emote.Active && (jump.WasPressedThisFrame() || AnyPressed(emoteCancels))) emote.Stop();
   ```
   The emote ends here, then the existing lights, horn, drop, direct-use and use code runs as normal. Hotbar, Previous and Next `performed` callbacks in `InventoryInputHandler` run earlier in the same input update, so the selection changes in the same frame the emote ends.

**Helpers**
```csharp
private void UpdateWheel(Vector2 delta)
{
    if (!WheelAvailable) { CloseWheel(); return; }
    bool confirm = use.WasPressedThisFrame() || jump.WasPressedThisFrame() && jump.activeControl?.device is Gamepad;
    if (confirm && EmoteWheel.Highlight >= 0 || !ButtonHeld(emoteAction)) { Pick(); return; }
    EmoteWheel.Look(delta, look.activeControl?.device is Gamepad);
}
private void Pick()
{
    int slot = EmoteWheel.Highlight;
    CloseWheel();
    if (slot >= 0 && movement == Vector2.zero) emote.Play((byte)slot);
}
private void CloseWheel()
{
    EmoteWheel.Close();
    useBlocked = UseButtonHeld(); jumpBlocked = ButtonHeld(jump); emoteBlocked = ButtonHeld(emoteAction);
}
private static bool AnyPressed(InputAction[] actions) { foreach (var action in actions) if (action.WasPressedThisFrame()) return true; return false; }
```
Confirm uses the `Use` action (LMB and RT) or `Jump` from a gamepad (A). Space does not confirm.

**`Consume`**: in both `MoveInput` constructions, use `float facing = emote && emote.Active ? emote.FacingYaw : Yaw;`. The emote-start yaw holds while the inventory or panel is open too.

**`ClearContext`**: remove the `exitBlocked` line. Add `EmoteWheel?.Close(); emoteBlocked = ButtonHeld(emoteAction);`. This one hook closes the wheel without playing for the session panel (including Escape through `SessionOverlay.Pause` → `SetPanel` → `SetGameplay`), focus loss, overlay, inventory, seating requests and life changes.

---

## 11. Input gating elsewhere

- **`Player/InventoryInputHandler.cs` `Available`** (line 45): add `!input.EmoteWheelOpen &&`. This blocks Inventory, Hotbar, Previous, Next and scroll while the wheel is open.
- **`Player/PlayerInteraction.cs` `RefreshTarget`** (line 55): add `|| inputReader.EmoteWheelOpen` to the early return. No target is shown and no interaction runs while the wheel is open.

---

## 12. `Player/PlayerPresentation.cs` camera

- Add `private PlayerEmote emote;`. In `OnStartClient`, before `if (!IsOwner) return;`, set `emote = GetComponent<PlayerAvatarPresentation>().Emote;`.
- Pull the downed wall-collision code into a helper that both branches use:
  ```csharp
  private Vector3 Orbit(Vector3 target, Quaternion rotation, float maximum)
  {
      Vector3 direction = rotation * Vector3.back;
      float distance = Physics.SphereCast(target, 0.18f, direction, out var wall, maximum, cameraMask, QueryTriggerInteraction.Ignore)
          ? Mathf.Max(0f, wall.distance - 0.02f) : maximum;
      return target + direction * distance;
  }
  ```
- `UpdateCamera`:
  - The downed branch stays first and uses `Orbit(target, rotation, 3f)`.
  - Then:
    ```csharp
    var aim = AimPose;
    if (emote && emote.ViewWeight > 0f)
    {
        localCamera.transform.SetPositionAndRotation(Orbit(aim.position, aim.rotation, 3f * Mathf.SmoothStep(0f, 1f, emote.ViewWeight)), aim.rotation);
        return;
    }
    ```
  - The pivot is the first-person eye (`AimPose.position`, head height). The pull-out starts behind the player along the current look direction, and `Pitch`/`Yaw` orbit it.

---

## 13. HUD

### `UI/Hud.uxml`
Add after `revive-overlay`: `<ui:VisualElement name="emote-overlay" class="emote-overlay" picking-mode="Ignore" />`.

### `UI/Hud.uss`
```css
.emote-overlay { display: none; position: absolute; left: 0; top: 0; right: 0; bottom: 0; align-items: center; justify-content: center; }
.emote-wheel { width: 420px; height: 420px; }
.emote-wheel-slot { position: absolute; width: 110px; align-items: center; }
.emote-wheel-slot > Image { width: 44px; height: 44px; margin-bottom: 4px; }
.emote-wheel-slot > Label { font-size: 15px; color: var(--text-secondary); -unity-text-align: middle-center; white-space: normal; }
.emote-wheel-slot.highlighted > Label { color: var(--text-primary); -unity-font-style: bold; }
```

### `UI/EmoteWheel.cs` (new, same shape as `ReviveProgressWheel`)
- **Constructor `EmoteWheel(EmoteCatalog catalog)`**
  - Sets `pickingMode = Ignore` and adds the class `emote-wheel`.
  - For each slot `i < EmoteCatalog.Capacity` where `catalog.Get(i)` is valid, it builds a `VisualElement` with class `emote-wheel-slot` and `pickingMode` Ignore.
  - The element gets an optional `Image { sprite = Icon }`, then a `Label(DisplayName)`.
  - Place the element at the middle of the ring band:
    - `r = (1 + EmoteWheelSelection.DeadZone) * 0.25f`, as a fraction of the element size.
    - `left = Length.Percent(50 + Mathf.Sin(a) * r * 100)` and `top = Length.Percent(50 - Mathf.Cos(a) * r * 100)`, where `a = i * 45°` in radians.
    - `style.translate = new Translate(Length.Percent(-50), Length.Percent(-50))`.
  - Store the elements in an array and set `generateVisualContent += Draw`.
- **`Set(int highlight, Vector2 pointer, bool showPointer)`**: when any value changes, toggle the `highlighted` class on the old and new slot elements and call `MarkDirtyRepaint()`.
- **`Draw`**
  - `outer = min(w, h) * 0.5f - 2`, `inner = outer * EmoteWheelSelection.DeadZone`. The deadzone is the hole.
  - For each present slot, the painter angle of the centre is `c = -90 + 45 * i`, and the slice spans `c ± 21`:
    ```csharp
    BeginPath(); Arc(center, outer, c - 21, c + 21); Arc(center, inner, c + 21, c - 21, ArcDirection.CounterClockwise); ClosePath(); Fill();
    ```
  - Fill colours: rgba(12, 16, 24, 0.82) for normal slices and rgb(58, 80, 96) for the highlighted slice. Also stroke the highlighted slice in rgb(155, 218, 196) with `lineWidth = 3`.
  - When `showPointer` is set, fill a 5 px circle at `center + new Vector2(pointer.x, -pointer.y) * outer`.
- It repaints only from `Set`, so there is no work while closed.

### `UI/HudController.cs`
- **Fields**: `emoteOverlay`, `emoteWheel`, `EmoteWheelSelection wheelSelection` and `PlayerEmote emote`.
- **`OnEnable`**: `emoteOverlay = root.Q("emote-overlay"); emoteWheel = new EmoteWheel(session.Emotes); emoteOverlay.Add(emoteWheel);`.
- **`Bind`**
  - Unsubscribe the old `wheelSelection.Changed -= RefreshEmoteWheel` and `emote.Changed -= RefreshCrosshair`.
  - Assign `wheelSelection = inputReader ? inputReader.EmoteWheel : null` and `emote = inv ? inv.GetComponent<PlayerAvatarPresentation>().Emote : null`.
  - Subscribe both, then call `RefreshEmoteWheel()`.
- **New methods**:
  ```csharp
  private void RefreshEmoteWheel()
  {
      bool open = wheelSelection != null && wheelSelection.Open;
      emoteOverlay.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
      if (open) emoteWheel.Set(wheelSelection.Highlight, wheelSelection.Pointer, !wheelSelection.Controller);
      RefreshCrosshair();
  }
  private void RefreshCrosshair() => crosshair.style.display = health && health.IsDowned || inventoryOpen ||
      wheelSelection != null && wheelSelection.Open || emote && emote.Presenting ? DisplayStyle.None : DisplayStyle.Flex;
  ```
- **Crosshair writes**: replace the direct writes in `RefreshLife` (line 422), `OpenInventory` (line 542) and `CloseInventory` (line 558) with `RefreshCrosshair()`. In `CloseInventory`, call it after `inventoryOpen = false`.
- **`OnDisable`**: `emoteWheel?.RemoveFromHierarchy();`. `Bind(null, null)` already handles unsubscribing.

---

## User steps (assets)

1. Create `Assets/Game/Settings/Emotes/` and, in it, one `Two Birds/Emote Definition` per clip in `Assets/Art/Animations/Emotes`: Can Can, Chicken Dance, Dancing Maraschino Step, Dancing Twerk, Female Dance Pose, Female Dance Pose(1), Jazz Dancing, Wave Hip Hop Dance. Set `Clip`, `DisplayName`, `Loop`, `Speed` and an optional `Icon` on each.
2. Create a `Two Birds/Emote Catalog` in the same folder and list the definitions in wheel order. Slot 0 is at the top and slots go clockwise.
3. On the `SessionRoot` prefab, assign the catalog to `SessionController` → `Emote Catalog`.

The Player prefab needs no changes.

## Accepted behaviour

- Replacing an emote jumps to the new clip's first frame at the current blend weight, because there is a single rebound playable (spec).
- Head look and eye look fade with the 0.2 s emote blend instead of cutting off.
- A slow cart bump that doesn't launch the player, and isn't a world impact, doesn't end the emote. The owner still ends it if they leave the ground.
- Hotbar, Previous and Next selection changes land in the same frame the emote ends.

## Visual validation (user)

1. **Keyboard**
   - Hold R: the wheel shows every catalog name and icon, the crosshair hides, and the camera doesn't move while the mouse moves.
   - The pointer dot follows the mouse and highlights slices.
   - LMB plays and closes. Releasing R over a slice plays it. Releasing R in the centre plays nothing.
2. **Controller**
   - Hold D-pad Left, then use the right stick to highlight. The highlight stays when the stick recentres.
   - A or RT plays immediately. Releasing D-pad Left plays the highlight.
   - Pressing A with nothing highlighted keeps the wheel open, and A doesn't jump after the wheel closes.
3. **While the wheel is open**
   - Jump, LMB and RMB item use, Q, E, F, 1–8, scroll and Tab/Inventory do nothing. WASD still moves.
   - Escape closes the wheel and opens the session panel.
   - Alt-tab closes the wheel.
4. **Owner view**
   - The camera pulls out behind the player over about 0.2 s and orbits with look input. The body keeps its facing.
   - The full avatar, hat and tattoos are visible. The held item and first-person hands are hidden.
   - After the emote, the camera pulls in and first-person view faces where the camera looked. Hands, crosshair and held item return, and no stray tattoo decals appear.
5. **Loop and one-shot**
   - A loop emote runs until you move or jump. Looking and orbiting don't cancel it. Sprint alone and opening the inventory or panel don't cancel it either.
   - A one-shot plays once and blends back to idle without a pop.
6. **Disruption**: a cart hit, a potion or item shove (including a small sideways one), being picked up, seated or downed each end the emote at once, and the normal animation takes over.
7. **Observers (second client)**
   - The same emote starts and stops.
   - There is no head look, the feet stay planted on uneven ground, raised feet aren't pinned, and there is no root drift.
   - The fingers follow the clip and the held item is hidden.
   - A client that joins mid-emote sees the player in their normal pose.
   - The host observing a remote client's emote sees it too.
8. **Downed**: when the owner is downed, the downed camera takes over, and the ragdoll is the full avatar from the first frame, not the capsule. After revival, first person resumes and the body is hidden again.
9. **Keys and hints**
   - F disposes at the cauldron and exits the cart. R only opens the wheel.
   - The remap panel lists `Emote` and `Secondary Interaction / Exit Vehicle`, and no longer lists `Exit Vehicle`.
   - The cart hints show F or D-pad Right for Exit.
10. **Appearance**: changing appearance in the editor and then emoting shows the updated full body.
