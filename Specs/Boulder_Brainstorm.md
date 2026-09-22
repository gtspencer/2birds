## Boulder Brainstorm

I want a boulder item in this game.  it should essentially be a large rock.  the model is `Assets/Game/Prefabs/Items/Boulder.prefab` (already has a collider and rigidbody).  players can pick it up (author with 2 handpoints on either side of the model, held around the stomach, so in first person view just the top is visible) and keep it in their inventory.  they cannot throw it very far (reuse the existing charge throw system).  boulders can cause impulse and damage to players, but only if going a certain speed, likely only achieved by a runaway boulder down a hill.

this is our first two handed held item.  explore how the hand IK holding system works.  also, this boulder introduces a new held point around the stomach (we can call it a heavy item hold).  extend the hold system so we have multiple hold slots; the default being the hand, but allow for "stomach area" holds too.  only 1 thing can be held at a time (like current implementation); so a player cannot hold both a rock in the hand, and a boulder in the heavy hold slot.

if an item in the existing item settings object is marked as heavy hold, then we automatically find 2 points on either side of it to use as the attach points.  when charging for a throw of a heavy item, bring the item (and thus the hold points), above the head of the avatar and release the throw from up there.

boulders can also be created in the cauldron by combining 3 small rocks as its recipe.  boulders can also be placed in the cauldron, but we don't need a recipe that contains them right now.

this feature largely uses existing systems (equip/throw/hold) with some tweaks to the visuals. 