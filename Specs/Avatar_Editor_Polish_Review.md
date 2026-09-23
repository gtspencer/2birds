1. **[P2] Preserve tattoo framing when switching preview poses**

   **Location:** [AvatarEditorPreview.cs:76](/Users/spencer/source/repos/2birds/Assets/Game/Runtime/Avatars/Appearance/AvatarEditorPreview.cs:76), `SetPlacement` (lines 76–84).

   **Problem:** Entering placement now switches the animated avatar into its fixed editing pose, and leaving placement immediately restores the animated pose. Neither transition updates the camera target. `AvatarEditorController.SelectTattoo` and the panel's zoom handlers center the camera on the tattoo before this pose change, so the target remains at its previous world position while the tattoo moves with its bone.

   **Why it matters:** Select an arm or hand tattoo, zoom in closely, then choose **Edit placement**. The limb and its gizmo can move outside the viewport when the editing pose is applied. Returning to the animated preview can similarly lose the selected tattoo's framing.

   **Recommended fix:** Preserve the selected tattoo's screen position across the pose transition by shifting the camera target by its world-position delta, or refocus the selected tattoo after evaluating the new pose. Keep the current orbit and zoom.
