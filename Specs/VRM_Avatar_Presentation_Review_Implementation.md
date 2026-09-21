# VRM avatar presentation implementation decisions

This document explains the flagged recommendations in [the presentation review](VRM_Avatar_Presentation_Review.md). Deferral accepts the remaining risk until the stated evidence or design decision supports a targeted change. It does not establish that the existing behavior is sufficient.

1. **Catalog loading and separation of processing metadata**

   The direct-reference concern is valid: the registry references source models, processed prefabs, and settings. The review does not establish how much memory those references cost for the intended catalog. Shared dependencies also do not imply separate texture allocations for every reference.

   Explicit loading would add asset-handle ownership, load failures, cancellation when selections change, and rules for releasing dependencies after replacement. Separating processing metadata is a smaller option, but source references currently participate in registry/settings association checks. Removing them requires preserving that association through another representation. Neither change follows automatically from having two example avatars.

   **Reason for deferral:** the loading architecture would add complexity without an established catalog memory problem. The review itself makes explicit loading conditional on catalog size.

   **Revisit when:** a representative catalog shows unacceptable startup time or resident memory from unselected avatars. First identify which references retain those dependencies; remove processing-only references where that is sufficient before introducing a loading system. The eager-reference risk remains until then.

2. **Asynchronous preparation and pooling**

   One candidate per frame limits the number of preparations, not their duration. Instantiation and initialization can still cause a visible hitch, and work for different candidates can overlap within one frame.

   Making preparation asynchronous does not inherently move Unity object creation or animation work off the main thread. Spreading work across frames requires explicit partial-initialization, cancellation, and cleanup rules. Pooling additionally retains model resources and requires resetting animation, IK, spring state, bindings, and visibility correctly on reuse.

   **Reason for deferral:** these mechanisms should address a measured expensive stage. Adding them before identifying that stage risks increasing memory and lifecycle complexity without reducing the worst frame. This follows the review's recommendation to measure before selecting either approach.

   **Revisit when:** representative replacements exceed the frame budget. Capture the preparation stages and worst simultaneous-swap frames, then choose the smallest change that addresses the dominant cost. Synchronous preparation remains a possible source of hitches.

3. **Proportion-aware name-label clearance**

   The review gives a concrete reason to question the current head-position-plus-height formula: Gerbil's rest bounds extend above the calculated anchor. That supports investigating overlap, but does not establish the desired anchor during animation, seated poses, carrying, or different camera angles.

   Automatically using whole-avatar bounds can make a label respond to raised hands, accessories, or other geometry unrelated to the head. Evaluating bounds every frame would also add recurring work. A generated head-clearance measurement or an authored offset could be simpler, but each defines a different visual rule.

   **Reason for deferral:** changing placement requires a visual acceptance decision. Compliance with the original formula does not resolve the readability concern.

   **Revisit when:** visual inspection identifies the affected avatars and poses. Prefer a fixed per-avatar clearance if it solves those cases; use dynamic placement only if motion requires it. Head or ear overlap remains possible with the current formula.

4. **Removing the spring-service discovery search**

   In [AvatarSpringBatch](Assets/Game/Runtime/Avatars/NativeSprings/AvatarSpringBatch.cs), the search before `FastSpringBoneService.Instance` establishes whether a service already exists. That result determines whether release destroys the service or restores its previous update policy. The installed singleton accessor can find or create a service, but does not report which happened.

   **Reason for flagging:** describing the search as redundant overlooks its ownership role. Simply deleting it removes information needed by the current cleanup policy. Assuming ownership could destroy a service belonging to another consumer; assuming no ownership changes cleanup for a service created by avatar presentation. The search occurs on initial retention rather than every presentation frame.

   **Revisit when:** service ownership can be established explicitly, or an equivalent discovery API can preserve that distinction. The obsolete-API concern is separate and remains valid; retaining ownership detection does not justify retaining an obsolete API indefinitely.

5. **Representative spring content and performance acceptance**

   The review reports empty spring lists in both supplied processed avatars. Those models cannot demonstrate spring motion or representative spring simulation costs. Small geometry examples also cannot establish performance for seven remote avatars with more demanding content.

   **Reason for deferral:** this is a content and measurement requirement. Inventing a spring rig would introduce asset and artistic choices without establishing that it represents intended gameplay content. Performance claims based on these examples would rest on the wrong assumption that their workload is representative.

   **Revisit when:** an intended avatar with real spring chains is available. Observe swaps, teleports, feature toggles, and simultaneous despawns, then profile an eight-player session with seven nearby remote avatars. Treat the review's 16.67 ms total frame target and provisional 4 ms combined avatar CPU budget as acceptance targets. Include worst replacement frames, CPU/GPU time, allocations, and managed/native memory over repeated swaps.

Visual acceptance should include Gerbil's head and ears at different heights and camera angles, label placement while seated and carried, and spring motion during replacement and simultaneous despawn. Those observations determine whether the deferred visual and preparation changes are needed.
