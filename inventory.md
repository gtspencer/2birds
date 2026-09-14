Implement a hud, including a hotbar and health indicator, and an inventory system.  The inventory system should be openable/closeable, support drag and drops within to reorder, and support dragging an item out of the inventory to drop it.  Items in the inventory and hot bar can stackable, or they can be unique.  The first item will be a rock (Assets/Art/Rock/Rock_Basalt.fbx).  Just name it "Rock", not basalt.

There will be hundreds, if not thousands of rocks to be picked up on the map.  But just implmement the logic, and I will do the placing and testing.

The general solution should include a baking step in the editor, so expose an editor tool for this.  We'll include other things in the bake later.  But ideally, we give each rock a unique id at bake, and only network it once the rock is picked up (i.e. send a message to the host saying "i picked up this rock")

This will place the rock in the inventory (defaulting to the first available hotbar slot of one is open).

The inventory should have 24 slots, 3 rows of 8.  the first row is the hotbar slot.  hot bar slots can be selected/deselected via number keys on keyboard, or scrolled through via right/left bumper on controller.  If an item is selected in the hotbar, that item appears in the hands of the avatar.  right now, we just have a capsule as the avatar; we'll add a body later, so just create an empty 'slot' for it on the avatar.  The item should appear for remote clients, and it should appear as 'equipped' for the local avatar in the bottom right corner of the screen.

Ensure the pickup system is extensible, we'll have other items to be picked up/thrown/equipped later.  Also ensure we have a drop command (which drops the held item), and a "use" command (mouse click).  But don't wire any of that up yet.