using FishNet.Object;

namespace TwoBirds
{
    public sealed class PickupRegistry : NetworkBehaviour
    {
        public override void OnStartServer() => WorldItemRegistry.Instance.BeginWorld(gameObject.scene);
        public override void OnStartClient() => WorldItemRegistry.Instance.JoinWorld(gameObject.scene);
        public override void OnStopNetwork() => WorldItemRegistry.Instance?.EndWorld();
    }
}
