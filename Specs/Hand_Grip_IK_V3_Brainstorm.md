I want to update my hand grip ik system for items.  Right now, it relies on poses, but I want it to rely on more explicit IK.  We can keep poses for the hand (i.e. the shapes of the fingers), but hand position, and thus lower arm and upper arm positions and shoulder rotations, should be entirely ik driven.

There are 2 held item types and each has a slot on the avatar: regular items, and heavy items.  regular items go in the players hand slot, and heavy items go in a slot around the stomach/hips of the avatar.  but when moving, both slots should follow the hip movement of the avatar (so that likely means the hip is the parent of both).  the items should be parented under these slots when 'equipped'.

each item slot (both hand and heavy), should have a persistent sub item(s): hand target(s).  for a regular item, we should have a hand target for the right hand.  for the heavy objects, we have hand targets for both hands.  with this approach, an designer can, in the editor, move these hand targets relative to the object, and thus move the hands.  when they do this, we want to be able to apply this position and rotation offset to the item for this avatar, so any time that particular avatar picks up that particular item, it does so with that hand offset.  we'll have sensible defaults that all avatars and items initially share (and a way to make a particular position/rotation the default).

Similarly to the hand target offsets, we want item offsets, which save for that particular avatar (again with sensible defaults, and the same way to configure those defaults).

I want to be able to do this for both first person, and third person, and to have different per item per avatar position and rotation offsets.

Then we have using items.  both regular items, and heavy items, can be charged, then thrown.  I want a charge pose, with an offset editor identical to the above.  When charging, we lerp the item slot (and thus the hand targets and item) to the charge position.  this charge position should also be an empty gameobject that is configurable in editor, with position and rotation offsets savable (notice a pattern?).  the time to lerp should also be configurable per item.

After a throw, we want the hand target to follow the thrown item for a while, before returning to either the default pose (no ik), or if we have another item in our hand, back to the held position for that item.

We also have a slingshot.  This is a custom item that has custom authored targets.  the standard hold is similar to regular items, but when charged, the charge position of the right hand is different, but i also want to engage the left hand to "pull" the stretchy sling back.  I'm not sure the best way to do this conceptually, so use the above to define a system that is similarly easy to adjust and tweak (for both first and third person).

Lastly, we have world/world item interactions.  Right now, we just have the golf cart, but we'll add more later.  For now we can focus on the golf cart.  When in the golf cart, I want the golf cart steering wheel to have 2 hand targets (right and left), that I can adjust in scene to change to change the positions of where the hand is following.  similarly, I want to be able to tweak in first and third person.

Adding world interactions or world item interactions should be simple: author a new hand target, and the state/interaction script of the avatar tells the avatar's hand(s) to follow that point for the length of the interaction.

Additional context:
- a GripAuthoringScene with tooling already exists.  But needs some massive overhauls).
- we can throw away ANYTHING and EVERYTHING we need to accommodate for the above ask
- the main design decisions surround ease of tweaking; the editor should be able to click on an object, drag/rotate the gizmo, click a button, and move on to the next item or avatar for tweaking.