We are implementing a "splat" system.

This will be exposed as a setting on items.  we should have a checkbox called "Spawn Splat".  If true, additional settings are exposed: "destroy on splat" (bool), "splat texture" (either a texture or material)

We will use the Unity Decal Renderer system for this.  When an object hits something (other players, ground, environment, golfcart, etc.), we spawn a unity decal there with the splat texture defined in the item settings.  if destroy on splat is true, the object gets destroyed when something is hit.  if the item is not destroyed, we splat once, not on subsequent bounces.