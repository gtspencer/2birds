# Avatar Editor Polish

## Purpose

Keep tattoo editing controls scoped to the Tattoos tab and animate the preview avatar with the existing looping idle, while retaining a stable pose for tattoo placement.

## Tattoo section visibility

- Show the entire tattoo section only when the Tattoos tab is selected. This includes the equipped tattoo list, its heading, color controls, swatches, and placement button.
- Hide that section on the Avatars and Hats tabs and collapse its layout space.
- On the Tattoos tab, keep color and placement controls visible but disabled when no tattoo is selected.
- Keep **Remove All** available on every tab. Preserve its behavior of clearing both the hat and all tattoos.

## Preview animation

- Use the existing shared idle clip and animation playback system.
- Loop idle continuously while the avatar editor is open, including during tab navigation and color editing, except during tattoo placement.
- Preserve cursor head tracking while idle plays.
- Use the existing fixed editor pose during tattoo placement and disable cursor head tracking for that mode.
- Resume idle and cursor head tracking when placement ends.
- No new animation asset or Animator controller is required.

## Tattoo placement and tab transitions

- Apply the fixed editor pose before calculating placement against the avatar. Placement targeting must match the visible skin when adding a tattoo or repositioning an equipped tattoo.
- Leaving the Tattoos tab ends any active placement immediately and resumes idle.
- Preserve the selected tattoo when switching tabs.
- Returning to the Tattoos tab restores access to that selection without automatically entering placement. The player must explicitly enter placement again.

## Implementation scope

Use the existing avatar editor panel, preview, animation playback, and tattoo placement systems. Apply tab visibility through the existing UI Toolkit layout and coordinate preview pose changes with the existing placement surface refresh.

## Visual acceptance criteria

1. Switch between Avatars, Hats, and Tattoos. The full tattoo section appears only on Tattoos, and hidden controls leave no reserved space on the other tabs.
2. Open Tattoos with no tattoo selected. Color and placement controls remain visible and disabled. Selecting a tattoo enables the appropriate controls.
3. Confirm **Remove All** remains available on every tab and clears the hat and tattoos.
4. Observe the preview through several idle loops and while using editor controls. Idle continues smoothly outside placement, and the head follows the cursor.
5. Add a tattoo and reposition an equipped tattoo. Both operations use the fixed pose, stop cursor head tracking, and target the visible skin accurately.
6. End placement. Idle and cursor head tracking resume, and the tattoo remains correctly attached as the avatar moves.
7. Switch away from Tattoos during placement. Placement ends immediately and idle resumes. Return to Tattoos and confirm the tattoo remains selected without placement restarting.
