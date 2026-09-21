# Review item not adopted

**Smaller inconsistencies, item 2: avoid repeated target installations.**

`AvatarHandTargets.Set()` only copies a small value-type target into a fixed array; `Clear()` assigns its default value. Neither operation installs bindings, allocates objects, raises events, or resets interpolation. Repeating an unchanged assignment has no visible effect, while blend weights legitimately change during evaluation. Caching the same target state in each caller would add synchronization and invalidation logic for negligible benefit. Leave these assignments as they are.
