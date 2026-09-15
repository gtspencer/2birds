using UnityEngine;

namespace TwoBirds
{
    public interface IInteractable
    {
        string ActionText { get; }
        string InputActionPath { get; }
        bool CanInteract { get; }
        Transform TooltipAnchor => null;
        void Interact();
    }
}
