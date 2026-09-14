namespace TwoBirds
{
    public interface IInteractable
    {
        string ActionText { get; }
        string InputActionPath { get; }
        bool CanInteract { get; }
        void Interact();
    }
}
