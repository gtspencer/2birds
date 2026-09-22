# Boulder Review

## 1. [P1] Resting boulders have different effective mass across clients

**Location:** `Assets/Game/Runtime/Items/WorldItem.cs:345–351`; `Assets/Game/ScriptableObjects/Items/Boulder.asset:65–66`.

**Problem:** The boulder enables both `CollideWhileSleeping` and `SimulateOnReleasingClient`. Its Rigidbody remains dynamic only on the releasing client; `WorldItem.ApplyRecord` makes it kinematic elsewhere (line 281). Keeping its GolfCart collisions enabled therefore makes the same resting boulder a movable 10 kg body on one peer and an immovable collider on the others. For example, after a non-host player drops a boulder, a cart hitting it encounters a kinematic obstacle on the server while the releasing client simulates a dynamic collision. `OfflineRigidbody` only pauses bodies during reconciliation; it does not provide collision prediction or ownership transfer.

**Why it matters:** Cart prediction and server simulation resolve materially different collisions. This can produce abrupt stops, corrections, and incorrect crash/ejection decisions: `GolfCartController.AccumulateCollision` feeds the physical collision impulse directly into crash severity.

**Recommended fix:** Support locally predicted boulder motion during cart contact, with a contact/ownership handoff and synchronized motion or impulses. Ensure the cart's relevant simulation peers resolve the interaction with comparable effective mass before enabling persistent resting collisions.

## 2. [P2] Heavy-item poses discard the passenger's pitch and roll

**Location:** `Assets/Game/Runtime/Player/PlayerHeldItemPresentation.cs:95–101`; `Assets/Game/Runtime/Player/PlayerHandPresentation.cs:148–151`.

**Problem:** `HeavyBody` always reconstructs the torso rotation as yaw only. Passengers can equip items, and their avatar inherits the seat's full rotation (`AvatarPresentation.UpdateInput` and `AvatarInstance.Place`). On an inclined or rolling cart, the new heavy-item path replaces the binding's actual shoulder frame with upright generated shoulders. The boulder and its hand targets consequently stay upright while the visible passenger tilts.

**Why it matters:** The two-handed grip can separate from the boulder or be clamped short by hand IK, and the computed release reach no longer describes the passenger's actual arms. Ordinary items retain the binding's full rotation in this path.

**Recommended fix:** Preserve the attached body's full rotation when seated, consistent with the existing attached-avatar placement. Use yaw-only orientation for standing poses, and retain the real binding's shoulder positions for reach constraints while keeping the shared heavy-item reference center.

## 3. [P2] Clearance processing removes the left-hand transition when switching items

**Location:** `Assets/Game/Runtime/Player/PlayerHeldItemPresentation.cs:817–821, 937–946`.

**Problem:** Switching from a boulder to an ordinary item enters the `returnHeavy` branch of `Blend`, which retains `returnLeft` and fades `leftWeight`. On the owning client, `RefreshPose` then calls `ResolveClearance` and unconditionally replaces that pose with `HeldItemPoseCalculation.FromItem(allowed, body, data)` whenever clearance succeeds—even when no correction was needed. Because the newly selected item is ordinary, this replacement sets `Heavy = false` and discards `LeftPalm`. `PrepareLeft` immediately clears the left-hand item target despite its remaining blend weight.

**Why it matters:** A normal boulder-to-rock switch drops the left hand out of the intended return animation. The observer path does not perform this owner-only clearance replacement, so the transition also differs between clients.

**Recommended fix:** Preserve the outgoing left-palm pose and transition flag when applying clearance to an ordinary destination item. Update only the destination item's pose and right-hand target; keep the left-hand target until its return weight reaches zero.
