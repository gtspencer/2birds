using FishNet.Object;
using UnityEngine;

namespace TwoBirds
{
    public sealed class Cauldron : NetworkBehaviour, IInteractable
    {
        public CraftingRecipeBook Recipes;
        public Transform IntakeAnchor, CurveAnchor, OutputAnchor, BlastAnchor;
        [Min(0.01f)] public float InsertionSeconds = 0.6f, BrewSeconds = 1.5f, RiseSeconds = 0.5f, ResultSeconds = 0.5f;
        [Min(0f)] public float BlastRadius = 3f, BlastDamage = 25f, BlastOutward = 6f, BlastUpward = 3f;
        public CauldronRecord State { get; private set; }
        public CauldronPresentation Presentation { get; private set; }
        private WorldItemRegistry registry;
        public bool Accepting => (State.Phase is CauldronPhase.Empty or CauldronPhase.Occupied) && (State.Ingredients?.Count ?? 0) < 3;
        public bool Settled => State.Phase == CauldronPhase.Occupied && registry.ServerTick >= State.Deadline;
        private PlayerInventory User => registry ? registry.LocalInventory : null;
        public string ActionText => User && !User.GetEquipped().IsEmpty ? "Insert item" : "Brew";
        public string TooltipTextOverride => null;
        public bool HideTooltipText => false;
        public string InputActionPath => "Player/Interact";
        public string SecondaryActionText => "Dispose";
        public string SecondaryInputActionPath => "Player/SecondaryInteract";
        public bool CanInteract => User && User.CanCraft && (!User.GetEquipped().IsEmpty ? Accepting : Settled);
        public bool CanSecondaryInteract => User && User.CanCraft && User.GetEquipped().IsEmpty && Settled;
        private void Awake() => Presentation = GetComponent<CauldronPresentation>();
        public override void OnStartNetwork()
        {
            registry = WorldItemRegistry.Instance;
            registry.RegisterCauldron(this);
        }
        public override void OnStopNetwork() { if (registry) registry.UnregisterCauldron(this); }
        public void Interact() => User.CauldronAction(this, User.GetEquipped().IsEmpty ? InventoryOperation.Brew : InventoryOperation.Insert);
        public void SecondaryInteract() => User.CauldronAction(this, InventoryOperation.Dispose);
        internal void Apply(CauldronRecord state, bool snapshot)
        {
            State = state;
            Presentation.Apply(state, snapshot);
        }
    }
}
