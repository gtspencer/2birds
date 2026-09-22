### These are general notes and fixes for the slingshot

- pebbles don't collide with `Environment` layer, only `Ground` (maybe `Default` too but unsure).  Point out where this setting is, as well as fixing it.
- Error on contacts:
```
Physics.ClosestPoint can only be used with a BoxCollider, SphereCollider, CapsuleCollider and a convex MeshCollider.
UnityEngine.Collider:ClosestPoint (UnityEngine.Vector3)
TwoBirds.PebbleProjectile:Overlaps (UnityEngine.Collider,single) (at Assets/Game/Runtime/Projectiles/PebbleProjectile.cs:161)
TwoBirds.PebbleProjectile:BeforePhysics () (at Assets/Game/Runtime/Projectiles/PebbleProjectile.cs:150)
TwoBirds.PebbleRegistry:BeforePhysics () (at Assets/Game/Runtime/Projectiles/PebbleRegistry.cs:230)
TwoBirds.WorldItemRegistry:BeforePhysics (single) (at Assets/Game/Runtime/Items/WorldItemRegistry.cs:578)
FishNet.Managing.Timing.TimeManager:InvokeOnPhysicsSimulation (bool,single) (at Assets/FishNet/Runtime/Managing/Timing/TimeManager.cs:1077)
FishNet.Managing.Timing.TimeManager:IncreaseTick () (at Assets/FishNet/Runtime/Managing/Timing/TimeManager.cs:746)
FishNet.Managing.Timing.TimeManager:<TickUpdate>g__MethodLogic|113_0 () (at Assets/FishNet/Runtime/Managing/Timing/TimeManager.cs:393)
FishNet.Managing.Timing.TimeManager:TickUpdate () (at Assets/FishNet/Runtime/Managing/Timing/TimeManager.cs:381)
FishNet.Transporting.NetworkReaderLoop:Update () (at Assets/FishNet/Runtime/Transporting/NetworkReaderLoop.cs:29)
```
- Currently, for both local and remote avatars, the holding hand is palm up, so the slingshot intersects the center of the palm.  Rotate the wrist 90 degrees, so the palm is facing left (for the local user, and do the equivalent for the remote clients)
- when charging, the sling shot moves up and towards the center.  don't move it up, just move it towards the center.  and move it to the center even more (it should be directly in the center of the avatar -- on remote avatars, the arms should be almost entirely stretched out in front of the body; the current distance from the avatar is fine for the local player)
- add a few points to the line renderer, and make the slingshot band wiggle a bit more after shooting, and relax back into a smoother, more relaxed position (currently, it is jagged in the 'relaxed' position).
- in a build, the remote client gets this error when a slingshot pebble is released and/or hits something:
```
Rotation quaternions must be unit length.
UnityEngine.Debug:ExtractStackTraceNoAlloc (byte*,int,string)
UnityEngine.StackTraceUtility:ExtractStackTrace () (at C:/build/output/unity/unity/Runtime/Export/Scripting/StackTrace.cs:35)
UnityEngine.Rigidbody:set_rotation (UnityEngine.Quaternion)
TwoBirds.RigidbodyMotionState:Apply (UnityEngine.Rigidbody,TwoBirds.ItemMotion,UnityEngine.Vector3) (at C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Items/RigidbodyMotionState.cs:43)
TwoBirds.PebbleProjectile:Present (TwoBirds.PlayerItemHitbox) (at C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Projectiles/PebbleProjectile.cs:305)
TwoBirds.PebbleRegistry:Present (TwoBirds.PlayerItemHitbox) (at C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Projectiles/PebbleRegistry.cs:274)
TwoBirds.WorldItemRegistry:LateUpdate () (at C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Items/WorldItemRegistry.cs:604)

[C:/Users/spenc/source/repos/2birds/Assets/Game/Runtime/Projectiles/PebbleProjectile.cs line 305]
```
*note - we DONT want to network the rotation of pebbles, we can infer that locally.
- Ensure the slingshot script (SlingshotPresentation.cs) doesn't rely on the mesh renderer/mesh names, as I will be replacing the temporary model we have.  Flag this if we do, and propose an update so its 3d model agnostic (and just relies on transforms in the child, though it seems that's what it already does; just verify)