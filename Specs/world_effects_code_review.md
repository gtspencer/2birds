## Architecture Assessment

The approach is sound for what it's solving. The core decision — affected player's owner is the sole reporter, server confirms, all clients replay from confirmed state — is the right call for FishNet prediction. It gives immediate feedback on the victim's client and keeps the message count low.

The `WorldImpact` generalization (velocity change + recovery ticks through a shared motor API) is clean and will extend to cars without rework. Batching contacts per movement tick with a 40 m/s cap is a practical safeguard.

### Is this the best way?

For the constraints (FishNet prediction, kinematic remote bodies, responsive local feedback), yes. The main alternative — relying on Unity's `OnTriggerEnter` — doesn't fire for kinematic-kinematic pairs (remote rocks vs. remote player hitboxes), which is exactly the common case. The geometric sweep is necessary.

The analytical sweep math in `PlayerItemHitbox.SweepSphere` (cylinder body + two hemisphere caps) is correct and fast. Caching the capsule geometry at Awake avoids per-frame allocations.

### Reliability concerns

**1. The two detection paths diverge in what they measure.**
- Host victim: `OnCollisionEnter` uses `incomingVelocity` captured in `BeforePhysics` and the physics engine's contact normal (`-contact.normal`).
- Remote victim: geometric sweep uses snapshot-interpolated or physics-segment velocity and a geometrically derived `intoPlayer` direction.

These will produce different shove magnitudes and directions for the same physical scenario. A rock grazing at a steep angle will get a different normal from Unity's contact solver vs. the capsule sweep. This is inherent to the dual-path design and probably acceptable, but it means host-vs-client feel will never be identical for marginal contacts.

**2. Contact state machine has many reset entry points.**
The combination of `hasContactPose`, `touchingPlayer`, `rebaseContactPose`, `previousSphere`, `previousPlayer`, `previousCorrectionOffset`, `previousPlayerCorrection`, `contactPlayer`, `contactGeneration`, `contactReset`, `releasePlayer`, `releaseOperation` is a lot of interrelated state. There are three levels of reset (`ResetIncomingMotion`, `ResetContactSamples`, `ResetContactState`) called from:
- `ResetPresentation` (pool/initialize)
- `PredictPickup`
- `SetRecord` (on state/release change)
- `PresentHeld`
- `SamplePlayerContact` (generation/player change)

Missing any one of these on a lifecycle transition means either a phantom sweep or a suppressed real contact. The existing review's finding about correction blackouts was one such case; the `rebaseContactPose` flag addresses it, but the overall fragility remains. This is the area most likely to produce hard-to-reproduce bugs.

**3. The `rejectReplay` mechanism looks correct but is subtle.** It pauses the rigidbody during stale-generation replays and returns early from `ReplicateMove`. The `AfterReconcile` callback clears it after the cycle. This handles the review's finding #1. One edge case: if `OnStopNetwork` fires mid-reconcile, it calls `AfterReplay` (unpausing) then clears `rejectReplay`, which should be safe.

**4. Impact generation synchronization.** `ObserversGeneration` (reliable RPC) can arrive before the reconcile state carrying the same generation number. `ReceiveGeneration` calls `BeginGeneration` which clears all impacts. If a reconcile then arrives with the old generation, `rejectReplay = true` correctly skips that stale state. This looks handled.

### Visual consistency

The main inconsistency source is **remote rock snapshot gaps**. The plan and review both acknowledge this: a rock that hits and bounces between 20 Hz snapshots may never register on the victim's client. The sweep can only detect what the presented motion shows.

For smooth, well-aimed throws this is fine. For ricochets and grazes at shallow angles, the remote client may miss contacts that the host's physics detects. The plan explicitly accepts this tradeoff and proposes compact contact/trajectory samples as a future extension if needed.

Within a single client, visual consistency is good — the sweep follows the same presented motion the player sees, including correction rebasing. The presentation-correction tracking (`PresentationCorrected` event, `previousCorrectionOffset`) ensures that a reconciliation shift doesn't register as a swept flight path.

### What I'd change

**Default `ImpulseMultiplier` to 0.** The review's finding #4 is valid — every new item type automatically gets knockback with the current default of 1. Worse, host detection works with any collider shape but remote detection requires a `SphereCollider`. Defaulting to 0 makes impact opt-in, which is safer for a feature that requires specific collider setup.

Beyond that, the existing review findings are solid. Findings #1 and #3 appear addressed by the current code. Finding #2 (frame-rate dependent velocity) also appears fixed — predicted rocks use physics-step velocity from `AfterPhysics`, and remote rocks use tick-based interpolation. Finding #4 (default multiplier) remains.

### Bottom line

This is a well-engineered solution to a genuinely hard problem (collision detection between predicted/interpolated objects on different network timelines). The complexity is high but mostly necessary. The contact state machine is the riskiest area for subtle bugs. The dual detection path (physics callback vs. geometric sweep) is an unavoidable consequence of kinematic remote bodies, and the resulting host-vs-client feel difference for edge-case contacts is an acceptable tradeoff.
