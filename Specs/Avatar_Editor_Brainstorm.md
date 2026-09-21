I want an avatar editor.  It should be 1 screen in 3 places -- a new button on the main menu with an Avatar Editor, a button in the lobby, at a skin changing station in the game world.  the skin changing station is just an object the player can walk up to and interact (press E on keyboard), and the full screen menu appears.

The avatar editor should be a full screen menu.  In the editor, I want to display a 3d version of the current player avatar on the right side.  dragging it with mouse, or using the right stick on controller, will rotate it.  zooming with mouse or right/left trigger on controller will zoom in/out (ensure these controls live on screen)

On the left side, I want to be able to do 3 things: change avatar (from an avatar library -- some start out locked and have an unlock condition.  the two we currently have in game should start as unlocked), add tattoo (use decals for this, default place the decal on the center of the avatar, and add a gizmo to let the player drag it around the body, including letting them rotate, we'll have a prebuilt library of allowed tattoos), and add hat (we will also have a hat library, where some are unlocked and others are locked with a lock condition, similar to avatars.  the hat can be placed on the head, and the position cannot be modified.  we already know where the head is in the presentation model, so we'll just place the hat there; ensure each avatar has an optional hat position offset in its settings, and each hat has a hat position offset in its settings in case some hats need a different default position than others.)

When changing an avatar, try to keep the equipped hat and tattoos.  if an avatar is drastically different size, try to place the tattoo in the best place.  if you can't , we can just silently delete it.

Have a 'remove all' button that removes all hats and tattoos.

Save the avatar choice, hat choice, and tattoos choices to the player prefs.

Network these.  Avatars are already networked, but also send the hat choice (just the hat id, not the actual hat position), and tattoo choices (tattoo id + position) once on game load, and also on demand only when something changes.