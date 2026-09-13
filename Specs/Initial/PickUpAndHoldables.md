### Goal
Implement the ability for players to pick things up off the ground.

### Details
There will be items on the ground in the game world.  Players should have the ability to look at an item, see a tooltip for pickup (support controller and mouse/keyboard), and pick the item up.  This pickup should persist across all clients.  There will be hundrends, if not thousands of these items, mainly rocks.  If an item is not currently being manipulated, we don't need to track its state over the network.  so rocks on the ground don't exchange data over the network until they are picked up, in which case we track that its picked up and owned by the player.  if its equipped, we still don't track position directly, instead we track on the player state object that they are holding that item, in which case remote clients can replicate.  we only track position over the network once one of these items is thrown.  after an object is on the ground and slowed to a near stop, we can freeze it and stop tracking the position over the network.  ensure objects have configurable linear damping and angular damping; we want them to travel far in the air, but stop relatively quickly after hitting the ground.

Held items should be visible to all players, including the local player.  Once we introduce avatars, we'll place the item in the avatar's hands, and likely do IK for arm position (both local and remote), but for now, we can just set a "slot" on the capsule for where the item is held.

Items can be used (mouse click or right trigger), or they can be dropped (q or controller equivalent.).  For rocks, using them is throwing them (players can hold interact to "charge" the throw.).  We'll introduce other items later that have different interactions on use, but don't worry about that for now.

Ensure this uses a similar networking stack/solution to the players.  Thrown items need relatively high replication synchronization, but we don't want to overwhelm the host with networking calls, and we want to make sure the thrower of the rock, if not the host, sees the most accurate representation of the trajectory of the rock.

### Success Criteria
- Player A can pick a rock up off the ground, and it goes into their inventory.
- Player B no longer sees the rock
- Player A can equip the rock, and it appears in front of them off to the right hand side of the screen as "held"
- Player B see player A holding the rock
- Player A can throw the rock, and player B sees the rock travel through the world on the same trajectory as player A.