# Grip Authoring Review Implementation

All five findings are credible and addressed. No findings were rejected or deferred.

| Finding | Resolution |
| --- | --- |
| 1. Authoring cursor state | `PlayerInputReader` refreshes cursor presentation using both session input availability and authoring focus. Focus, inventory, and gameplay changes use this common path; `GripAuthoringScene.SetEditing()` only changes focus. Session entry and pause closure therefore preserve editing mode. |
| 2. Unreachable slingshot contacts | The left-hand readout remains present while charging, including when reach failure sets its IK weight to zero. Requested contacts are checked independently for each hand, using the pulling hand's 0.85 reach fraction. The observer holding-hand clamp also retains its original requested palm and reach-limited flag. |
| 3. Heavy-item requested pose | Heavy placement records whether projection moved the root. The hold/blend path preserves the destination's unprojected requested palms and reach flags while retaining the projected pose for presentation, including at a settled hold. |
| 4. First-person draw handle | The draw-offset handle uses the selected presentation state's slingshot draw center and root rotation, matching the first-person or observer values being edited. |
| 5. Instrumentation boundary | Runtime authoring code, integration call sites, and editor window/persistence references use `UNITY_INCLUDE_INSTRUMENTATION`. The build policy and guard share one inclusion rule covering managed code variants, Unity's development/debugging flags, and explicit instrumentation defines. |

Unity 6.6's Instrumented, Checked, and Debug managed variants include instrumentation; Development Build also currently defines the instrumentation symbol. Scene inclusion accounts for both sources. See [Unity's managed code variant documentation](https://docs.unity.com/en-us/engine/6000.6/manual/scripting/debugging-and-diagnostics/managed-code-variants).

## Visual checks

- Open grip authoring and close the pause panel: the editing cursor should remain visible and unlocked. F2 should switch between editing and gameplay.
- Charge a slingshot and move its pulling contact or draw offset beyond reach: the left-hand error should remain visible and report an unreachable contact in both views. Restore the values and check that contact returns.
- Move a heavy item's hold position far beyond arm reach: the item should remain projected into reach while the readout shows reach limiting and nonzero requested-contact error after blending settles.
- Give first-person and observer charge poses different offsets and rotations. Drag the first-person draw handle and confirm it follows the first-person slingshot's position and axes.
- Check authoring availability in an instrumented build and its absence in a Release-variant build without development/debugging flags or explicit instrumentation defines.
