I want a sling shot weapon in this game.  This will be the first weapon type.

The sling shot should shoot pebbles (smaller versions of rocks in the game; i will provide the 3d model).   There should be unlimited ammo for this.

The sling shot is shaped like a regular slingshot (Y shaped).  I also want a dynamic ""stretchy"" band connecting the two arms (where the player shoots from).  Explore the best way to do this, but I'm thinking a line renderer with a few points, and we simulate the stretching motion when the player pulls back, then the release, and then some sway/jitter after the tension is released.  use the existing hand IK system.

the player holds this in their hand (networked for all players).  When not charging, the sling shot is idle.  when charging, the other hand comes up and pulls the center back (for all players, remote and local), and a pebble automatically appears in the slingshot pouch.

the launch force and velocity should be greater than throwing a rock, but ensure its configurable.  pebbles are still affected by gravity.  we'll place the slingshot on the ground in the starting scene.  players can pick it up, equip it, store it in their inventory, and drop it.  it CANNOT go in the cauldron.

Pebbles should hit their target, spawn a small dirt particle effect, and disappear (no picking up pebbles; they are a momentary projectile).  pebbles can do damage to other players, but it cannot give them an impulse and send them flying (same with other physics based things like the golfcart, but no damage on the golfcart).

Sling shots will have a configurable cooldown between shots (like throwing rocks), largely to let the animation play out, but also to reduce spamming.

Ensure proper networking.  Ideally, we don't network the entire pebble and can just simulate it based on initial conditions, especially considering there's no bounces.  explore and let me know the best approach for networking (we want to keep network messages to a minimum).