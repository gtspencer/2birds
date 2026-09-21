using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TwoBirds
{
    public sealed class InputBindings
    {
        public const string KeyboardMouse = "Keyboard&Mouse", Controller = "Gamepad";
        private const string Preference = "InputBindingOverrides";
        private readonly InputActionAsset asset;
        private readonly List<Entry> entries = new();
        public IReadOnlyList<Entry> Entries => entries;
        public event Action Changed;

        public sealed class Entry
        {
            public InputAction Action { get; }
            public Guid Id { get; }
            public string Name { get; }
            public string Group { get; }
            public int Index
            {
                get
                {
                    for (int i = 0; i < Action.bindings.Count; i++)
                        if (Action.bindings[i].id == Id) return i;
                    return -1;
                }
            }

            internal Entry(InputAction action, InputBinding binding, string name, string group)
            { Action = action; Id = binding.id; Name = name; Group = group; }
        }

        public InputBindings(InputActionAsset asset)
        {
            this.asset = asset;
            Add("Move", "Move");
            Add("Sprint", "Sprint");
            Add("Interact", "Interact");
            Add("Use", "Use");
            Add("DirectUse", "Use Potion");
            Add("SecondaryInteract", "Secondary Interaction");
            Add("Drop", "Drop");
            Add("Inventory", "Inventory");
            Add("Jump", "Jump / Handbrake");
            Add("ExitVehicle", "Exit Vehicle");
            Add("Lights", "Lights");
            Add("Horn", "Horn");
            Add("Previous", "Previous Item");
            Add("Next", "Next Item");
            for (int i = 1; i <= PlayerInventory.HotbarSize; i++) Add($"Hotbar{i}", $"Hotbar Slot {i}");
        }

        private void Add(string actionName, string name)
        {
            var action = asset.FindAction("Player/" + actionName, true);
            var movementParts = new HashSet<string>();
            foreach (var binding in action.bindings)
            {
                if (binding.isComposite) continue;
                foreach (string group in new[] { KeyboardMouse, Controller })
                {
                    if (!InGroup(binding, group)) continue;
                    string label = name;
                    if (actionName == "Move")
                    {
                        if (!binding.isPartOfComposite || group != KeyboardMouse) continue;
                        label = binding.name switch { "up" => "Forward", "down" => "Backward", "left" => "Left", "right" => "Right", _ => binding.name };
                        label += movementParts.Add(binding.name) ? " (Primary)" : " (Alternate)";
                    }
                    entries.Add(new Entry(action, binding, label, group));
                }
            }
        }

        internal static bool InGroup(InputBinding binding, string group) =>
            Array.Exists((binding.groups ?? "").Split(';'), value => value == group);

        public static void Load(InputActionAsset asset)
        {
            asset.RemoveAllBindingOverrides();
            if (!PlayerPrefs.HasKey(Preference)) return;
            try { asset.LoadBindingOverridesFromJson(PlayerPrefs.GetString(Preference)); }
            catch (Exception)
            {
                asset.RemoveAllBindingOverrides();
                PlayerPrefs.DeleteKey(Preference);
                PlayerPrefs.Save();
            }
        }

        public void Apply(Entry entry, string path)
        {
            entry.Action.ApplyBindingOverride(entry.Index, path);
            Save();
        }

        public void Reset(Entry entry)
        {
            entry.Action.RemoveBindingOverride(entry.Index);
            Save();
        }

        public void Reset(string group)
        {
            foreach (var entry in entries)
                if (entry.Group == group) entry.Action.RemoveBindingOverride(entry.Index);
            Save();
        }

        private void Save()
        {
            PlayerPrefs.SetString(Preference, asset.SaveBindingOverridesAsJson());
            PlayerPrefs.Save();
            Changed?.Invoke();
        }

        public List<string> Conflicts(Entry target, string path, InputControl control)
        {
            var result = new List<string>();
            foreach (var entry in entries)
            {
                if (entry == target || entry.Group != target.Group) continue;
                string other = entry.Action.bindings[entry.Index].effectivePath;
                if (string.IsNullOrEmpty(other)) continue;
                if (string.Equals(other, path, StringComparison.OrdinalIgnoreCase) ||
                    control != null && InputControlPath.Matches(other, control)) result.Add(entry.Name);
            }
            return result;
        }
    }
}
