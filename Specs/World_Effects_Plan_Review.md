Accuracy and Internal Consistency

1. Host player double-detection path

The plan says: "Stop the existing server collision callback from independently creating impacts for client-owned players." But on the host, the rock is a dynamic rigidbody and the player hitbox is kinematic — OnCollisionEnter fires on the rock (WorldItem.cs:395). The new swept detection also runs on the host (the host is the "affected player's owner"). Unless OnCollisionEnter is fully disabled for all player hits (not just client-owned ones), the host player gets double impacts — one from physics collision, one from the sweep.

The later sentence "The server continues to simulate the rock's physical collision and bounce" reinforces that the physics collision still happens. The bounce is fine — the issue is only HitPlayer being called from it.

2. Release grace doesn't cover swept detection

The plan says to "Honor the thrower's existing collision grace," but the current grace mechanism (Physics.IgnoreCollision at WorldItem.cs:325) only suppresses physics-engine collisions between specific collider pairs. The new swept detection is manual math — it won't consult Physics.IgnoreCollision at all. The swept path needs its own independent grace check (thrower identity + time), which the plan doesn't call out explicitly. Without this, a self-thrown rock at close range could register a swept contact during the grace window even though physics would ignore it.

3. lastHitId as a high-water mark breaks under independent ID spaces

The plan's rule 7 says owner-reported and server-originated impacts need "distinct identities." But PlayerMotor.cs:119 uses hit.Id <= lastHitId as a high-water mark — processing any hit permanently suppresses all lower IDs regardless of source. If owner and server hits use separate ID sequences, a server impulse with a low ID would be suppressed by a higher owner-reported ID, or vice versa. The plan identifies the problem (rule 7) but doesn't specify the resolution — and the current pruning logic (RemoveAppliedHits at line 184) has the same issue.

4. Recovery duration missing from event data

The plan says impacts carry "recovery ticks" and that cars can request longer recovery. But the current ItemHit struct (WorldItemMessages.cs:46) has no recovery field — knockbackTicks is hardcoded to 12 in the motor (PlayerMotor.cs:122). The plan acknowledges the event "needs... recovery ticks" in the event timing section, but the expected-files table says WorldItemMessages.cs is for removing item-specific hit data, not for adding recovery duration. The recovery field needs to land somewhere in the motor's impact struct, and knockbackTicks = Math.Max(knockbackTicks, hit.RecoveryTicks) replaces the current unconditional = 12.

Potential Cross-Client Inconsistency

5. Observer sees impact without visual contact

The affected client detects the swept contact against its locally interpolated rock position. An observer interpolates the same rock with its own timing — network jitter, different interpolation delay, different frame rate. The observer receives the server's confirmation and sees the player get knocked, but the rock's visual position on their screen might not overlap the player at that moment. The plan acknowledges "avoid promising identical contact frames on every client," which is honest, but at high latency the mismatch between visible rock position and visible knockback could look like a bug. Worth noting for tuning: if it's bad, the observer could flash a small contact effect at the confirmed position rather than relying on rock-player visual overlap.

6. Swept detection timing varies by client role

On the affected client, detection runs against the interpolated rock each frame/tick. On the host (if the host is the affected player), detection runs against the physics-simulated rock each tick. These are different motion representations with different timing characteristics. The rock's physics position on the host is ahead of its interpolated position on clients by roughly the interpolation delay. This means the host detects contact earlier relative to the visual presentation than a client does. The plan doesn't distinguish these two timing profiles.

Bugs That Could Surface During Later Development

7. Recovery shortening when mixing impact sources

The plan says "retain the longest remaining recovery period." With rock recovery at ~12 ticks and a future car at potentially 30+, a rock hit arriving during car recovery would overwrite knockbackTicks to 12 if the code uses assignment instead of max. The plan states the correct rule but the implementation order puts the car hookup last (step 5) — by then the motor code may have been written with a single recovery duration in mind and the max() logic might be missing.

8. Swept detection on pooled/reinitialized items

The plan says to reset contact samples on pooling. But WorldItem.ReturnToPool() (WorldItem.cs:383) calls ResetPresentation() which clears history and samples. If the swept detection state lives on WorldItem, the pool path is covered. But if a pooled item is rented and Initialize() is called, the plan should ensure swept state is also reset there — Initialize() calls ResetPresentation() so this works, but only if swept detection state is cleared in ResetPresentation() or Initialize(). Worth being explicit about.

9. External mode interaction with impacts

Currently, pendingImpulse is applied outside the Mode != External check (PlayerMotor.cs:149), meaning impulses apply even in External mode. The plan says "An impact is a temporary change in velocity, not a transfer of control" — but it doesn't say whether impacts should be suppressed during External mode. If a player is later externally controlled by a vehicle, should a rock hit still knock them? If so, the velocity change fights the external controller. If not, the swept detection should skip players in External mode.

10. MvpValidation.cs impulse magnitude

MvpValidation.cs:119 calls QueueImpulse(new Vector3(800f, 320f, 0f)). The plan changes the shared path from ForceMode.Impulse to ForceMode.VelocityChange with a mass conversion in QueueImpulse. This preserves semantics for a mass-1 player, but the value (800 m/s velocity change) is enormous regardless. Not a plan bug, but worth flagging as a test artifact that will look different if player mass ever changes from 1.

---

Summary: The biggest structural risk is items 1 and 2 — the host getting double impacts and the release grace not applying to manual sweeps. These are both silent bugs that would only appear in host-as-victim scenarios and self-throw-at-close-range, respectively. Item 3 (ID space collision) is the hardest design gap and will block step 2 unless the approach is chosen upfront.