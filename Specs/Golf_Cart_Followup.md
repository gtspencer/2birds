# Golf Cart Follow-up

Recommendations for commit `0570d1a` (`initial cart`) and the parking-brake/settings working changes discussed in [Golf_Cart_Review.md](Golf_Cart_Review.md).

## Recommended changes

1. Prevent repeated ejection handoffs for an already-empty overturned cart.
2. Bound the two wheel-rotation accumulators.
3. Correct tooltips that describe behavior or units the implementation does not have.
4. Document both parts of the FishNet modification in the existing game README.

The first issue is an omission from the review. The review's proposed recovery fallback and additional tuning field are not required fixes under the existing specification.

## Assessment of the review

### 1. FishNet's `Generated` directory: real maintenance concern, incorrect cause

Normal FishNet code generation does not overwrite these C# source files. The installed [FishNetILPP.cs](Assets/FishNet/CodeGenerating/ILCore/FishNetILPP.cs), particularly `Process` at lines 51 and 117, processes compiled assemblies and writes assembly/symbol data to memory streams. The directory name alone is not evidence that recompilation deletes `MotionFrame` or `ParkingBrake`.

Replacing or upgrading the imported FishNet source can overwrite the modifications. Documenting that dependency is warranted, but it must cover both `NetworkTransform.Motion.cs` and the edits to `NetworkTransform.cs`, not just the added field. The [plan](Golf_Cart_Plan.md) already explains why the sealed class needs an extension; the missing piece is an accurate, easy-to-find maintenance note. See change 4 below.

### 2. Unconditional event unsubscription: no demonstrated defect

The subscription in `GolfCartNetwork.OnStartServer` and removal in `OnStopNetwork` are safe in the current lifecycle. Removing an absent delegate is a no-op. Saying this might mask some future subscription problem does not establish a current bug.

Moving the removal to `OnStopServer` could make the pairing more obvious, but it is optional style cleanup. Do not add subscription flags or peer-specific branches solely for this finding.

### 3. Unbounded wheel spin: warranted

`GolfCartPresentation.LateUpdate` adds traveled rotation to `spin` and `rearSpin` indefinitely. Large accumulated float values lose resolution even though the displayed orientation only needs one revolution. Bound both values; see change 2.

### 4. Failed recovery: prescribed behavior, not a missing fallback

The specification explicitly says that unsuccessful placement must preserve the cart and keep recovery available. The plan specifies a bounded 4 m search on a 0.5 m grid. `GolfCartNetwork.Request` only clears `Recovery` after `TryRecovery` succeeds; completing the player's request permits another attempt. The recovery tooltip remains available.

An unchanged, fully blocked environment can continue to defeat that search. That is a limitation of the agreed placement policy, not evidence that the request path becomes permanently disabled. The review also conflates this with an off-map cart: the plan's no-respawn decision concerns a separate level-containment problem.

Do not automatically respawn the cart, bypass clearance checks, or enlarge the configured search as a bug fix. A player-facing failure message is a reasonable optional UX addition if desired. A console warning would not itself help a player recover the cart.

### 5. Collision enter/stay double counting: unsupported as stated

The review supplies no basis for its assertion that Unity 6 ordinarily reports the same initial contact through both callbacks in one physics step. Unity documents enter for newly established contact and stay for continuing contact. A compound cart can receive enter for one collider pair and stay for another during the same step; those are distinct contacts, which the plan explicitly asks the controller to aggregate. See Unity's [collision-event documentation](https://docs.unity3d.com/6000.0/Documentation/Manual/collider-interactions-oncollision.html).

`collisionSeverity` resets at the start of each normal physics step, and accumulation rejects prediction replay. Summing impulse magnitudes is a collision-severity metric, not necessarily the cart's exact net velocity change; opposing impulses need not cancel in this metric. Retain both callbacks and the aggregation. Do not add deduplication or change the crash threshold on the review's assumption alone.

### 6. The one-second recovery-clear threshold: optional naming, not a required setting

The expression is in `GolfCartController.AfterPhysics` at line 169, not `GolfCartNetwork`. The claim that every other timing value lives in settings is also inaccurate: the handoff deadline is fixed at two seconds in `GolfCartNetwork.BeginHandoff`.

The specification requires sustained clearing behavior but does not require this duration to be designer-configurable. A private `RecoveryClearSeconds = 1f` constant is sufficient if the literal needs a name. Adding a serialized setting solely for consistency would conflict with the repository's instruction to avoid unrequested configurability.

### 7. Static registries: domain-reload premise is reversed

A domain reload resets static state. Persistence between Play sessions is the concern when domain reload is disabled. [EditorSettings.asset](ProjectSettings/EditorSettings.asset) has `m_EnterPlayModeOptions: 0`, so neither domain nor scene reload is disabled. Unity describes this distinction in its [domain-reloading documentation](https://docs.unity3d.com/6000.0/Documentation/Manual/domain-reloading.html).

Normal teardown removes registered carts and players, clears their unresolved entries, and clears `PlayerSeating.Local`. An exception interrupting teardown is a hypothetical failure, not a reason to replace these registries. Explicit static resets could become relevant if the project later adopts disabled domain reload; they are not justified by this review.

### 8. Parking-brake change detection: the separate branch is correct

`parkingChanged` already bypasses the three-tick throttle and selects reliable delivery in `GolfCartNetwork.AfterPhysics` at lines 234-242. Adding `ParkingBrake` to `changed` would not fix anything in that branch. The variable and condition are sufficiently explicit that another comment is optional.

`InstallBaseline` does not initialize `lastSent`, which can cause a redundant initial send. However, seat application clears driving input, and the configured FishNet tick loop receives state before simulating physics. A stale comparison value alone does not establish a missed brake release under these paths. Seeding `lastSent` from the baseline is an optional consistency improvement, not a required correction to this finding.

### 9. Removing the persistent input preload: supported by the actual configuration

The removed GUID is `Assets/InputSystem_Actions.inputactions`. It remains assigned under `com.unity.input.settings.actions` in [EditorBuildSettings.asset](ProjectSettings/EditorBuildSettings.asset), line 15.

The installed Input System 1.20 package's `ProjectWideActionsBuildProvider.OnPreprocessBuild` reads that assignment and calls `BuildProviderHelpers.PreProcessSinglePreloadedAsset`; its post-build callback removes the entry it added. These implementations are under `Library/PackageCache/com.unity.inputsystem@7a4e1a2a8194/InputSystem/Editor/`. Unity also explicitly documents that the asset assigned to [InputSystem.actions](https://docs.unity3d.com/Packages/com.unity.inputsystem@1.20/api/UnityEngine.InputSystem.InputSystem.html#UnityEngine_InputSystem_InputSystem_actions) is included in player builds as a preloaded asset.

The review's final conclusion is appropriate for this project, but generic Input System initialization is not the reason. The configured project-wide action asset and its build provider are the reason. Retain the removal and preserve that assignment; no manual preload restoration is warranted.

### 10. Damping only changes with the motion baseline: intentional

`ResetMotion` receives the new simulator's driven/unmanned state through `InstallBaseline`. Driver entry, driver exit, driver-seat switches, ejection, recovery, and driver disconnect all use that path when changing driving authority. Passenger-only changes correctly leave damping alone.

An imagined debug command bypassing the protocol is not a current code path. Keep the transition-based assignment; repeatedly setting damping in the physics loop would add work without addressing an existing defect. Live adjustment of these settings during an unchanged driving session would be a separate requirement.

### Architecture and parking-brake claims need narrower wording

- Cross-client consistency comes from one active simulator and replicated motion. Choosing raycast suspension does not establish deterministic physics, and the stated design does not require separate peers to simulate matching trajectories.
- The epoch and stop/commit/start protocol fits the chosen requirements. The review does not establish that no simpler protocol could ever work. No networking redesign follows from that claim.
- The parking logic deliberately zeros velocity and suspends suspension/tire forces while holding. It is a reasonable gameplay policy, but calling it physically correct overstates what it models. Four support raycasts and the controller's other tick logic still run while parked; sleeping does not eliminate all cart work. Unity describes [Rigidbody.Sleep](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Rigidbody.Sleep.html) as forcing sleep until wake-up.

## Concrete fixes

### 1. Stop redundant incidents from restarting an empty flipped cart

**Files:** [GolfCartController.cs](Assets/Game/Runtime/Vehicles/GolfCartController.cs), `ResetMotion` and `AfterPhysics`; [GolfCartNetwork.cs](Assets/Game/Runtime/Vehicles/GolfCartNetwork.cs), `ReportIncident`, `Commit`, and `InstallBaseline`.

An overturned cart can enter this repeating path:

1. A crash, rollover, or settled-flip incident ejects riders and installs a new motion baseline.
2. `ResetMotion` clears both `rolloverTime` and `rolloverReported`.
3. The empty cart remains overturned. After `RolloverSeconds`, `eject` becomes true again, even if `Recovery` is already `Flipped`.
4. `ReportIncident` has no occupancy/no-change guard. It starts another handoff, and `Commit` creates another epoch even though there are no riders to eject and no recovery change.
5. Installing that baseline clears the latch again.

This creates repeated reliable state changes and physics/interpolation resets. It also advances the state revision unnecessarily, allowing recovery requests carrying the previous revision to be refused. With the default settings, the repeated rollover trigger is approximately every 0.6 seconds after each reset.

**Minimal fix:** in `ReportIncident`, before setting `incidentPending`, ignore a report when the cart has no occupants and the proposed recovery is either `None` or the already-active recovery. Continue processing genuine recovery changes on empty carts, especially entering `Flipped` or promoting `Stuck` to `Flipped`. Keep occupied-cart crash and rollover ejections intact.

This avoids changing the handoff protocol or introducing another persistent incident registry.

### 2. Bound wheel rotation without changing animation behavior

**File:** [GolfCartPresentation.cs](Assets/Game/Runtime/Vehicles/GolfCartPresentation.cs), lines 63-65.

Replace the accumulations inside the existing epoch/parking guard with:

```csharp
spin = (spin + turn) % 360f;
if (!frame.Handbrake) rearSpin = (rearSpin + turn) % 360f;
```

Negative remainders are valid rotation angles and preserve reverse motion. Keep the existing handbrake, parking-brake, and epoch conditions; resetting spin on handoff would unnecessarily change wheel orientation.

### 3. Make settings tooltips match the implemented model

**Files:** [GolfCartSettings.cs](Assets/Game/Runtime/Vehicles/GolfCartSettings.cs), with behavior defined in `GolfCartController`, `GolfCartPresentation`, and `PlayerSeating.SampleCartContacts`.

The review's blanket approval of the tooltips is unfounded. Correct these descriptions rather than adding physics behavior to make the descriptions true:

| Setting | Required correction |
| --- | --- |
| `SuspensionTravel` | Describe available suspension travel and ray reach. Increasing travel does not lower `Spring` stiffness or automatically make the suspension softer. |
| `DriveForce` | Describe requested drive force, limited by the tire-force budget. There is no simulated wheel angular velocity or wheelspin model; wheel visuals follow traveled distance. |
| `BrakeForce`, `HandbrakeForce` | Describe braking force limits and grip interaction. Larger values do not simulate wheels locking harder. Rear-wheel visual locking is controlled by the handbrake flag. |
| `GripRecoverySeconds` | The rate is `1 / GripRecoverySeconds` per second. Recovering from `HandbrakeGrip` to 1 takes `(1 - HandbrakeGrip) * GripRecoverySeconds`; the defaults give 0.28 s, not 0.35 s. Describe it as the recovery time from zero grip to full grip. |
| `MinimumHitSpeed` | The threshold is relative closing speed along the contact normal, including cart angular motion and the pedestrian's velocity. It is not simply cart speed. |
| `CollisionMultiplier` | It scales the closing-speed-based velocity impulse, including the added lift contribution, not the cart's raw velocity or a force. |
| `HitLift` | It is a dimensionless lift multiplier. The extra vertical contribution is `HitLift * closing * CollisionMultiplier`, not a fixed number of m/s. Remove the blanket `(m/s)` from the pedestrian-impact section header and keep units on the relevant individual descriptions. |
| `RolloverAngle` | 100 degrees from upright is 10 degrees beyond sideways. Fully upside-down is 180 degrees. Remove the claim that 100 is nearly upside-down. |
| `RolloverSeconds` | This delays the rollover-triggered ejection path. A crash or settled-flip recovery can eject riders earlier; the default settled-flip delay is 0.5 s. |
| `LandingGraceSeconds` | Stuck detection resets during the grace period; it does not pause and preserve accumulated stuck time. The period also starts when motion is reset. |
| `RecoveryHeightChange` | Increasing this expands ray reach and the permitted root-height change, but `TryRecovery` separately rejects ground above `Body.position.y + 0.15f`. Do not promise that increasing it alone allows recovery onto much higher terrain. |

These are documentation corrections. In particular, changing `HitLift` to an absolute upward velocity or adding wheelspin would alter gameplay and require separate tuning decisions.

### 4. Document the vendor patch where maintainers will find it

**File:** [Assets/Game/README.md](Assets/Game/README.md).

The README currently states that imported FishNet code is unchanged. Replace that inaccurate statement with a short maintenance section covering:

- FishNet 4.7.3 includes an opt-in cart motion extension in `NetworkTransform.Motion.cs` and integration hooks in `NetworkTransform.cs`.
- Preserve the partial declaration, `GoalData.Motion`, epoch guards, interpolation-goal hook, and reset hook when upgrading. Retaining the additional file alone is insufficient.
- The extension owns the motion-frame payload, including parking state, coherent velocity samples, and epoch handling. Default transforms use the existing path when epoch motion is disabled.
- Reapply/adapt both source modifications when replacing FishNet. Normal IL post-processing does not regenerate these source files.

Keep the partial class in the FishNet runtime assembly. Simply moving it into ordinary game scripts would split the declarations across assemblies and would not remove the dependency on private FishNet internals. No new Unity asset or migration tool is needed for this documentation change.

## Visual validation for the user

After implementing the fixes, use a host and guest to check:

- Drive forward and backward for an extended period; wheel motion remains smooth, front wheels steer, and rear wheels stop spinning during handbraking.
- Let an unmanned cart park, then have the other client enter and immediately accelerate. Both views release the parked wheel presentation and resume motion consistently. Repeat after switching drivers and after a driver disconnect.
- Overturn the occupied cart and leave it empty for several seconds. Riders eject once, the cart does not repeatedly hitch, and `Flip cart` remains usable on both clients.
- Attempt recovery where no candidate fits. The cart stays in place and the recovery action remains available; after clearing an obstruction, another attempt can succeed.
- Check ordinary bumps versus major crashes, and confirm pedestrian impacts still launch once per contact.
- In a normally built player, confirm menu navigation, walking, cart controls, and exit input work with the project-wide actions assignment.
