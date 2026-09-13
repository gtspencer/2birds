### Goal
Implement a hud, including a hotbar and health indicator, and an inventory system.  The inventory system should be openable/closeable, support drag and drops within to reorder, and support dragging an item out of the inventory to drop it.  Items in the inventory and hot bar can stackable, or they can be unique.  Create a test object if you need for testing, but delete it and clean it up after.  we'll implement pick ups and holdables after this (don't do this as part of this plan)

### Details
The hot bar should be the first slot of the inventory.  when the inventory is up, the hot bar row is numbered 1 to 8.  there should be 8 rows of 3.  Use Unity's UI Toolkit.  Create reusable components where it makes sense.

### Success Criteria
- a hot bar and health are present on the screen while playing
- items can be added to the inventory from the game world by interacting with an item.  if a hot bar slot is open, the item goes to that hotbar slot first
- items can be dragged and reordered in the inventory while open
- items can be dragged out of the inventory window, and dropped from the inventory back into the world.