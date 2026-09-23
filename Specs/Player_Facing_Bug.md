# Player facing review

## 1. High: Suppressed input resets body facing while preserving camera yaw

**Locations:** `Assets/Game/Runtime/Player/PlayerInputReader.cs:186`, `Assets/Game/Runtime/Player/PlayerMotor.cs:408`, `Assets/Game/Runtime/Player/PlayerMotor.cs:528`, `Assets/Game/Runtime/Player/PlayerPresentation.cs:51`.

`PlayerInputReader.Consume()` returns `default` when gameplay input is disabled, suppressed, or inventory is open. That clears `MoveInput.Facing` to zero along with movement and buttons. The owner still submits this as a created input, and `ReplicateMove()` applies its zero-degree facing to the body. FishNet invokes locally created inputs with `ReplicateState.Created` even when their contents are default (`Assets/FishNet/Runtime/Object/NetworkBehaviour/NetworkBehaviour.Prediction.cs:591`). The missing-input guard therefore does not protect against this case.

The camera independently uses `PlayerInputReader.Yaw`, which remains unchanged. For example, if B looks toward world yaw 180 degrees and opens inventory, B's camera remains at 180 degrees while B's body turns toward zero degrees. A approaching from the camera's forward direction can appear on B's screen while seeing B's back. This also applies to menu/console/editor input blocking and focus or Steam-overlay suppression. It can be especially noticeable when switching between two client windows.

The look RPC cannot repair this divergence: `AvatarLookSample.Pack()` discards yaw when the player is not attached, and `CurrentPlacement.LookYaw` uses graphics yaw for on-foot players (`Assets/Game/Runtime/Player/PlayerAvatarPresentation.cs:17`, `:229`). Both remote body facing and remote look direction therefore depend on the incorrect body rotation.

**Recommended fix:** For an alive, on-foot owner whose controls are blocked, return neutral movement that preserves `Yaw`, such as `new MoveInput(Vector2.zero, Yaw, false)`. Keep movement, jump, and sprint disabled. Preserve the separate suspended/attached/downed behavior and the motor's handling of genuinely missing network input. This uses the existing facing field and requires no new messages, scene edits, components, or assets. Do not rotate the camera to match the erroneous zero-degree input.

## 2. Lower priority: Body-turn smoothing can temporarily exaggerate a fast turn

**Locations:** `Assets/Game/Runtime/Avatars/AvatarPresentation.cs:194`, `Assets/Game/Prefabs/Player.prefab:357`.

The camera follows input immediately, while remote graphics use tick smoothing and the avatar adds a second body-turn filter. A stationary avatar starts turning beyond a 45-degree difference, stops below 15 degrees, and turns at no more than 180 degrees per second. Its body can therefore remain approximately opposite the camera briefly after a rapid half-turn. Head look is also limited, so it cannot fully represent an instantaneous backwards glance.

**Why it matters:** This can resemble the reported symptom during a turn, but does not explain a sustained half-turn mismatch after normal input and presentation have settled.

**Recommended fix:** Address the neutral-input bug first. If brief turn lag remains unacceptable, increase stationary body-turn responsiveness or bound the maximum body-to-look difference. Treat that as a separate presentation choice; adding another yaw replication channel is unnecessary for the primary defect.

## Visual checks

With two clients, have B face roughly opposite world-forward, then open inventory or a menu while A watches B. B's avatar should retain its facing, and someone directly behind B should remain behind B's camera. Repeat while switching window focus and opening the Steam overlay. Close the UI and turn while standing and walking; distinguish a persistent mismatch from temporary body-turn smoothing. Repeat with host/client roles exchanged, and check facing after leaving a seat or being released from a carry.
