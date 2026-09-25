using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;

namespace TwoBirds.Editor
{
    [InitializeOnLoad]
    public static class GripAuthoringBuildPolicy
    {
        public const string ScenePath = "Assets/Scenes/AvatarPresentationDemo.unity";
        static GripAuthoringBuildPolicy() => BuildPlayerWindow.RegisterBuildPlayerHandler(options =>
            BuildPlayerWindow.DefaultBuildMethods.BuildPlayer(Apply(options)));

        public static BuildPlayerOptions Apply(BuildPlayerOptions options)
        {
            options.scenes = (options.scenes ?? Array.Empty<string>()).Where(scene => scene != ScenePath).ToArray();
            return options;
        }
    }

    public sealed class GripAuthoringBuildGuard : BuildPlayerProcessor
    {
        private const string ItemRegistryPath = "Assets/Game/ScriptableObjects/ItemRegistry.asset";
        public override int callbackOrder => 0;
        public override void PrepareForBuild(BuildPlayerContext context)
        {
            if ((context.BuildPlayerOptions.scenes ?? Array.Empty<string>()).Contains(GripAuthoringBuildPolicy.ScenePath))
                throw new BuildFailedException("Apply GripAuthoringBuildPolicy.Apply to BuildPlayerOptions before building: the grip authoring scene is editor-only.");
            var items = AssetDatabase.LoadAssetAtPath<ItemRegistry>(ItemRegistryPath);
            var missing = items ? items.Items.Where(item => item && !item.HoldSlot).Select(item => item.name).ToArray() : Array.Empty<string>();
            if (missing.Length > 0) throw new BuildFailedException("Items without a Hold Slot: " + string.Join(", ", missing));
            if (!items) return;
            var slots = items.Items.Where(item => item && item.HoldSlot).Select(item => item.HoldSlot).Append(items.CarryHold)
                .Where(slot => slot).Distinct();
            var undefined = new List<string>();
            foreach (var slot in slots)
                foreach (GripTarget target in Enum.GetValues(typeof(GripTarget)))
                    foreach (bool firstPerson in new[] { false, true })
                        if (GripPoses.Required(slot.Mode, target) && !slot.Defaults.TryGet(target, firstPerson, out _))
                            undefined.Add($"{slot.name}: {target} ({(firstPerson ? "FP" : "TP")})");
            if (undefined.Count > 0) throw new BuildFailedException("Hold Slot defaults missing: " + string.Join(", ", undefined));
        }
    }
}
