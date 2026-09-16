using FishNet.Object;

namespace TwoBirds
{
    public sealed class PickupRegistry : NetworkBehaviour
    {
        public override void OnStartServer()
        {
            WorldItemRegistry.Instance.BeginWorld(gameObject.scene);
            if (BirdRegistry.Instance) BirdRegistry.Instance.BeginWorld(gameObject.scene);
            else SessionController.Instance.Leave("Add BirdRegistry and its settings/catalog references to SessionRoot.");
        }
        public override void OnStartClient()
        {
            WorldItemRegistry.Instance.JoinWorld(gameObject.scene);
            if (BirdRegistry.Instance) BirdRegistry.Instance.JoinWorld(gameObject.scene);
            else SessionController.Instance.Leave("Add BirdRegistry and its settings/catalog references to SessionRoot.");
        }
        public override void OnStopNetwork()
        {
            WorldItemRegistry.Instance?.EndWorld();
            BirdRegistry.Instance?.EndWorld();
        }
    }
}
