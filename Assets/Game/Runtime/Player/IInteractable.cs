using UnityEngine;

namespace TwoBirds
{
    public interface IInteractable
    {
        string ActionText { get; }
        string TooltipTextOverride { get; }
        bool HideTooltipText { get; }
        string InputActionPath { get; }
        bool CanInteract { get; }
        Transform TooltipAnchor => null;
        void Interact();
        string SecondaryActionText => null;
        string SecondaryInputActionPath => "Player/SecondaryInteract";
        bool CanSecondaryInteract => false;
        void SecondaryInteract() { }
    }
}
