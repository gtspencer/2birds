# Player Health Review Implementation

All four findings are credible and fixed. No findings were rejected or deferred.

1. **Early revive completion — fixed.** `PlayerNetworkState.Health.cs` retains an otherwise valid completion request on the active claim. The existing server health tick completes it when the claim duration expires, after checking the rescue deadline and rescuer validity. Clearing the claim also clears pending completion; cancellation still uses the existing attempt and claim token.

2. **Downed avatar blocks preparation — fixed.** `AvatarPresentation.CanPrepare` excludes life-locked avatars with an active instance before `AvatarPresentationSystem` consumes its preparation slot. Outstanding preparation remains pending until unlocking. Downed players without an active avatar remain eligible to prepare their initial instance.

3. **Collision damage depends on shove settings — fixed.** `WorldItem` samples eligible contacts independently of shove configuration. Damage retains its item-speed, closing-speed, override, and contact-episode rules. `DontPushPlayer`, `ImpulseMultiplier`, and `MinimumImpactSpeed` gate only shove velocity, including the incoming shove supplied to a lethal-hit ragdoll.

4. **Overhead bars lack sprites — fixed.** Unity imported `HealthBar.png` as a single sprite and assigned it to both existing SpriteRenderers, `HealthBackground` and `HealthFill`, in `Player.prefab`. Unity saved the importer metadata and prefab references.

## Visual validation for the user

- On a remote client with latency, hold revive through completion; confirm it restores the target at 50% health without remaining stuck at 100%. Releasing Interact, leaving range, or taking damage must still cancel; the rescue timeout must still win when it expires.
- Queue an avatar change just before that player is downed. Confirm other players' avatars still load or change, and the pending change appears after revival or respawn.
- Hit a player above the damage threshold with shove disabled, zero impulse multiplier, and a shove threshold above the impact speed. Confirm health decreases without a shove. Also confirm ordinary shove behavior and damage rearming after separation.
- From another client, confirm overhead bars are visible at full and partial health, empty while downed, and half full after revival.
