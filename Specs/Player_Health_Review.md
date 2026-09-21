1. **[P2] An early revive completion can leave the rescue stuck at 100%.**

   **Location:** [PlayerRevival.cs](Assets/Game/Runtime/Player/PlayerRevival.cs), lines 107–109; [PlayerNetworkState.Health.cs](Assets/Game/Runtime/Player/PlayerNetworkState.Health.cs), lines 360–369.

   **Problem:** The rescuer sets `completionSent` permanently before sending completion. The server silently discards that request when its elapsed time is still below the claim duration. The client's countdown uses FishNet's estimated server tick, which can advance during timing correction; it is not guaranteed that a locally completed countdown means the server deadline has passed when the request arrives. There is no rejection response, retry, or server-side completion scheduled for later.

   **Why it matters:** A valid rescue can reach a full progress wheel without reviving the player. Continuing to hold interact never resends completion, and the claim blocks other rescuers until cancellation or the downed player's timeout.

   **Recommended fix:** Give completion an acknowledgement and retry path, or retain an otherwise valid early completion until the server deadline. Keep cancellation tied to the existing attempt and claim token.

2. **[P2] A downed avatar can prevent other avatars from being prepared.**

   **Location:** [AvatarPresentation.cs](Assets/Game/Runtime/Avatars/AvatarPresentation.cs), lines 229–232; [AvatarPresentationSystem.cs](Assets/Game/Runtime/Avatars/AvatarPresentationSystem.cs), lines 60–64.

   **Problem:** If an avatar change sets `NeedsPreparation` and the player becomes downed before preparation runs, `Prepare` returns for `lifeLocked && active` without clearing that flag. The shared presentation system marks its single preparation slot as used before calling `Prepare`, so the same locked avatar consumes that slot every frame.

   **Why it matters:** Avatars later in the preparation order cannot load or change until the downed player revives or respawns. This also delays full ragdoll creation for other players waiting on an avatar instance.

   **Recommended fix:** Defer outstanding preparation when entering the downed state and restore it when unlocking, or exclude locked instances from preparation eligibility before consuming the shared slot.

3. **[P2] Collision damage incorrectly depends on shove settings.**

   **Location:** [WorldItem.cs](Assets/Game/Runtime/Items/WorldItem.cs), lines 972–978; prerequisite gates at lines 788–791 and 969–970.

   **Problem:** The new damage calculation is reachable only through `ContactEligible`, which requires `DontPushPlayer == false` and `ImpulseMultiplier > 0`. It also sits after the `MinimumImpactSpeed` return. Those existing settings describe shove behavior, but they now suppress health damage too, even with a positive `CollisionDamage` override and a hit above `ItemDamageSpeed`.

   **Why it matters:** An item cannot deal collision damage without also enabling shove. Raising the shove threshold above the damage threshold silently changes when damage occurs. All current potion definitions have `DontPushPlayer` enabled, so their collision-damage overrides would also be ineffective.

   **Recommended fix:** Sample eligible item contacts independently of shove configuration. Evaluate damage using its damage settings, then separately gate the queued shove using `DontPushPlayer`, `ImpulseMultiplier`, and `MinimumImpactSpeed`.

4. **[P2] The overhead health bars have no renderable sprites.**

   **Location:** [Player.prefab](Assets/Game/Prefabs/Player.prefab), lines 996 and 1088; [HealthBar.png.meta](Assets/Game/UI/HealthBar.png.meta), lines 46 and 111.

   **Problem:** Both `HealthBackground` and `HealthFill` serialize `m_Sprite: {fileID: 0}`. `PlayerNameLabel` only adjusts the fill transform; nothing assigns either sprite at runtime. The new texture is also configured as Multiple with an empty sprite list.

   **Why it matters:** Other players' overhead health bars remain invisible at every health value.

   **Recommended fix:** In Unity, import the texture as a single sprite or define a sprite slice, then assign the sprite to both renderers in the player prefab. Let Unity save the importer metadata and prefab references.
