using UnityEngine;

namespace TwoBirds
{
    internal sealed class EmoteWheelSelection
    {
        internal const float DeadZone = 0.45f, StickDeadZone = 0.5f, PointerTravel = 160f;
        private readonly EmoteCatalog catalog;
        internal bool Open { get; private set; }
        internal bool Controller { get; private set; }
        internal Vector2 Pointer { get; private set; }
        internal int Highlight { get; private set; } = -1;
        internal event System.Action Changed;

        internal EmoteWheelSelection(EmoteCatalog catalog) => this.catalog = catalog;

        internal void Begin(bool controller)
        {
            Open = true; Controller = controller; Pointer = default; Highlight = -1;
            Changed?.Invoke();
        }

        internal void Close()
        {
            if (!Open) return;
            Open = false; Pointer = default; Highlight = -1;
            Changed?.Invoke();
        }

        internal void Look(Vector2 value, bool controller)
        {
            if (value == Vector2.zero) return;
            int highlight = Highlight;
            Vector2 pointer = default;
            if (controller)
            {
                int slot = value.magnitude > StickDeadZone ? Slot(value) : -1;
                if (slot >= 0) highlight = slot;
            }
            else
            {
                pointer = Vector2.ClampMagnitude(Pointer + value / PointerTravel, 1f);
                highlight = pointer.magnitude > DeadZone ? Slot(pointer) : -1;
            }
            if (highlight == Highlight && pointer == Pointer && controller == Controller) return;
            Highlight = highlight; Pointer = pointer; Controller = controller;
            Changed?.Invoke();
        }

        private int Slot(Vector2 direction)
        {
            int slot = Mathf.RoundToInt(Mathf.Repeat(Mathf.Atan2(direction.x, direction.y) * Mathf.Rad2Deg, 360f) / 45f) % EmoteCatalog.Capacity;
            return catalog && catalog.Get(slot) ? slot : -1;
        }
    }
}
