ArgumentOutOfRangeException: Specified argument was out of the range of valid values.
Parameter name: Cannot get contact at index 0. There are 0 contact(s).
UnityEngine.Collision.GetContact (System.Int32 index) (at <09b7e658d29c429e9c7c5e1754a25291>:0)
TwoBirds.PebbleProjectile.Contact (UnityEngine.Collision collision) (at Assets/Game/Runtime/Projectiles/PebbleProjectile.cs:224)
TwoBirds.PebbleProjectile.OnCollisionEnter (UnityEngine.Collision collision) (at Assets/Game/Runtime/Projectiles/PebbleProjectile.cs:215)
UnityEngine.Physics:Simulate(Single)
FishNet.Managing.Timing.TimeManager:SimulatePhysics(Single) (at Assets/FishNet/Runtime/Managing/Timing/TimeManager.cs:1089)
FishNet.Managing.Timing.TimeManager:IncreaseTick() (at Assets/FishNet/Runtime/Managing/Timing/TimeManager.cs:747)
FishNet.Managing.Timing.TimeManager:<TickUpdate>g__MethodLogic|113_0() (at Assets/FishNet/Runtime/Managing/Timing/TimeManager.cs:393)
FishNet.Managing.Timing.TimeManager:TickUpdate() (at Assets/FishNet/Runtime/Managing/Timing/TimeManager.cs:381)
FishNet.Transporting.NetworkReaderLoop:Update() (at Assets/FishNet/Runtime/Transporting/NetworkReaderLoop.cs:29)