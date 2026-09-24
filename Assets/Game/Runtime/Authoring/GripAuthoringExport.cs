#if UNITY_INCLUDE_INSTRUMENTATION
using System;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace TwoBirds
{
    public static class GripAuthoringExport
    {
        public static string Folder => Path.Combine(Application.persistentDataPath, "GripAuthoring");
        public static string Save(GripAuthoringDrafts drafts, GripAuthoringDraft record)
        {
            var output = new JObject
            {
                ["Context"] = record.Context.ToString(), ["DisplayName"] = record.DisplayName,
                ["SourceSettings"] = record.Source.name, ["Authored"] = Fields(record.Values),
                ["UnitsAndFrames"] = new JObject
                {
                    ["RightPalmContact"] = "Item-root metres before prefab scale; Euler degrees",
                    ["LeftPalmContact"] = "Item-root metres before prefab scale; Euler degrees",
                    ["PullingPalmContact"] = "Moving pouch origin, item-root axes; metres before prefab scale; Euler degrees",
                    ["PalmCorrection"] = "Generated palm local axes; metres before avatar scale; Euler degrees",
                    ["HandPose"] = "Shoulder-relative arm lengths; heavy: shoulder midpoint and average arm lengths; Euler degrees; timing seconds",
                    ["FirstPersonPose"] = "Camera-oriented shoulder frame; arm lengths; Euler degrees",
                    ["ChargePose"] = "Camera/aim offset metres; palm Euler degrees; draw offset item-root metres before scale",
                    ["AvatarPlacement"] = "VisualHeight metres; StandingOffset body metres; first-person placement metres; reach/hold offsets arm lengths; YawOffset degrees",
                    ["FirstPersonHands"] = "ShoulderOffset metres; free-hand positions arm lengths; Euler degrees; transition durations seconds"
                }
            };
            if (record.Context == GripAuthoringContext.Item)
            {
                output["ItemId"] = record.ItemId;
                var item = (ItemDefinition)record.Runtime;
                output["Effective"] = new JObject
                {
                    ["HandPose"] = Fields(drafts.Held.ResolveHold(item)),
                    ["FirstPersonPose"] = Fields(drafts.Held.ResolveSpatial(item, true)),
                    ["GripFingers"] = item.GripFingers ? item.GripFingers.name : drafts.Avatars.Animations.GripFingers.name,
                    ["GripFingersSource"] = item.GripFingers ? item.name : drafts.Avatars.Animations.name
                };
                var authored = (JObject)output["Authored"];
                if (item.HoldMode != ItemHoldMode.Heavy) authored.Remove("LeftPalmContact");
                if (item is not SlingshotDefinition)
                    foreach (string field in new[] { "PullingPalmContact", "OverrideRemoteChargePose", "OverrideFirstPersonChargePose",
                        "RemoteChargePose", "FirstPersonChargePose", "RecoverySeconds" }) authored.Remove(field);
            }
            if (record.Context == GripAuthoringContext.AvatarCalibration) output["AvatarId"] = record.AvatarId.ToString();
            Directory.CreateDirectory(Folder);
            string path = Path.Combine(Folder, record.Key + ".json");
            File.WriteAllText(path, output.ToString(Formatting.Indented));
            drafts.Saved(record);
            return path;
        }
        private static JToken Fields(object value)
        {
            if (value == null) return JValue.CreateNull();
            if (value is UnityEngine.Object asset) return asset ? new JValue(asset.name) : JValue.CreateNull();
            var type = value.GetType();
            if (type.IsPrimitive || value is string || type.IsEnum) return JToken.FromObject(value);
            var fields = new JObject();
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public)) fields[field.Name] = Fields(field.GetValue(value));
            return fields;
        }
        public static void OpenFolder() { Directory.CreateDirectory(Folder); Application.OpenURL(new Uri(Folder + Path.DirectorySeparatorChar).AbsoluteUri); }
    }
}
#endif
