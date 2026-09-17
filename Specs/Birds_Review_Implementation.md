# Birds review qualifications

The numbered findings identify actionable issues. These recommendations and acceptance expectations need qualification:

| Review item | Qualification |
| --- | --- |
| 1: Continuous collision detection | Continuous Speculative is not required. The existing hit bodies can use dynamic sweep CCD, with zero inverse mass and inertia scaling on the bird side of rock contacts. This preserves route-driven movement without introducing speculative hit decisions. Unity documents [CCD pairing](https://docs.unity3d.com/6000.5/Documentation/ScriptReference/Rigidbody-collisionDetectionMode.html) and [contact mass scaling](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/ModifiableMassProperties.html). The original Discrete-mode finding remains valid. |
| 8: Shared timed phases | Additional phase-transition broadcasts are unnecessary. Route start/end times already determine promotion, arrival, and recovery; clients can derive these transitions while only the host arbitrates claims. |
| 10: Scare deadlines | A bounded planner cannot guarantee immediate route searches for an arbitrary simultaneous burst. All scares, including zero-delay scares, may wait for a planning slot, as explicitly requested. Original deadlines order pending escapes; they are not promises of an exact departure time under overload or when no suitable route exists. |
| 13: Corpse lifetime acceptance | Full lifetime for every nearby corpse cannot be guaranteed together with a hard body cap. Distant deaths should not evict nearby bodies. When relevant deaths alone exceed the cap, distance determines retention and a farther body may end early. |
| 15: Phase-related digest fields | Retaining `Next` and `Effective` solely to repair timed phases is unnecessary when both peers derive phases from the shared routes. Record revision remains the digest repair key; hit-result `Contact` remains necessary for competing predictions. |
