We are implementing an emote wheel.
Pressing and holding 'R' on the keyboard should open the emote wheel

The emote wheel is a UI radial menu with 8 items.  Dragging mouse over an item in the wheel and clicking plays the animation.  Also, if an item is selected (i.e. hovered) when the user closes the radial menu (by lifting the 'R' key), then that emote animation is played, even if the player didn't actually click.

Choose sensible defaults for controller controls as well.

The animations are `C:\Users\spenc\source\repos\2birds\Assets\Art\Animations\Emotes`.

Players are affected by all world interactions while emoting (getting hit by impulse, getting hit by cart, any damage, etc).  If they are tossed by an impulse (or picked up), they exit the emoting state and execute their normal animated state machine.

Emotes should be networked, but ideally we only need 1 byte to send an rpc of the emote id.  We want to keep the existing feet IK during emotes.  All emotes will be stationary (so no root motion, but ensure we don't apply any, just like the current animations).  We can turn off remote head rotation when a player emotes, but we still want to rotate their body when they turn.  Use the existing rotating logic; it's okay that they will slide.

Some emotes are looping, and some are one shot.  For looping animations, the player breaks out of the animation once they start moving (looking around does not cancel the animation)

Create a scriptable object that defines an emote.  it will hold reference to the emote, and include settings for a speed override, a checkbox if it loops, a display name, etc.