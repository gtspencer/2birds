using UnityEditor;
using UnityEngine;

namespace TwoBirds.Editor
{
    [CustomEditor(typeof(ItemDefinition), true), CanEditMultipleObjects]
    public sealed class ItemDefinitionEditor : UnityEditor.Editor
    {
        private ItemRegistry registry;

        private void OnEnable() => registry = AssetDatabase.LoadAssetAtPath<ItemRegistry>("Assets/Game/ScriptableObjects/ItemRegistry.asset");

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            bool slingshot = System.Array.TrueForAll(targets, value => value is SlingshotDefinition);
            bool heavy = System.Array.TrueForAll(targets, value => ((ItemDefinition)value).HoldMode == ItemHoldMode.Heavy);
            if (heavy) EditorGUILayout.HelpBox("Heavy positions place the collider reference center relative to the shoulder midpoint, in average arm lengths. Euler offsets rotate the object relative to the body before its prefab rotation. Both views use this frame. Author both palm contacts in item-root space.", MessageType.Info);
            var overrideSettings = serializedObject.FindProperty("OverrideHoldSettings");
            var property = serializedObject.GetIterator();
            for (bool enter = true; property.NextVisible(enter); enter = false)
            {
                if (!heavy && property.name == "LeftPalmContact") continue;
                if (property.name is "OverrideHoldSettings" or "OverrideFirstPersonPose" or "OverrideRemoteChargePose" or "OverrideFirstPersonChargePose")
                {
                    EditorGUI.showMixedValue = property.hasMultipleDifferentValues;
                    EditorGUI.BeginChangeCheck();
                    bool enabled = EditorGUILayout.Toggle(property.displayName, property.boolValue);
                    if (EditorGUI.EndChangeCheck())
                    {
                        serializedObject.ApplyModifiedProperties();
                        Undo.RecordObjects(targets, "Change grip override");
                        foreach (ItemDefinition item in targets)
                        {
                            registry.HeldItemDefaults.SetOverride(item, property.name, enabled);
                            EditorUtility.SetDirty(item);
                        }
                        serializedObject.Update();
                    }
                    EditorGUI.showMixedValue = false;
                    continue;
                }
                if (property.name is "RemoteChargePose" or "FirstPersonChargePose")
                {
                    if (serializedObject.FindProperty("Override" + property.name).boolValue)
                        EditorGUILayout.PropertyField(property, true);
                    else DrawInherited(property.name, slingshot, heavy);
                    continue;
                }
                if (property.name == "CollisionDamage")
                {
                    var damageOverride = serializedObject.FindProperty("OverrideCollisionDamage");
                    if (damageOverride.boolValue || damageOverride.hasMultipleDifferentValues)
                        EditorGUILayout.PropertyField(property);
                    continue;
                }
                if (property.name == "FirstPersonPose")
                {
                    var localOverride = serializedObject.FindProperty("OverrideFirstPersonPose");
                    if (localOverride.boolValue || localOverride.hasMultipleDifferentValues)
                        DrawPose(property, "First Person Spatial Pose", slingshot, heavy);
                    else DrawInherited(property.name, slingshot, heavy);
                    continue;
                }
                if (property.name == "HandPose")
                {
                    if (overrideSettings.hasMultipleDifferentValues) continue;
                    if (overrideSettings.boolValue)
                        DrawPose(property, "Hold Settings", slingshot, heavy);
                    else DrawInherited(property.name, slingshot, heavy);
                    continue;
                }
                using (new EditorGUI.DisabledScope(property.name == "m_Script"))
                    EditorGUILayout.PropertyField(property, property.name == "OverrideHoldSettings"
                        ? new GUIContent(heavy ? "Override Heavy Pose / Timing" : "Override Third Person / Timing") : new GUIContent(property.displayName), true);
                if (property.name == "OverrideHoldSettings" && overrideSettings.boolValue)
                    serializedObject.FindProperty("HandPose").isExpanded = true;
            }
            serializedObject.ApplyModifiedProperties();
        }

        private void DrawInherited(string field, bool slingshot, bool heavy)
        {
            if (!registry || !registry.HeldItemDefaults) return;
            foreach (ItemDefinition item in targets)
            {
                var defaults = registry.HeldItemDefaults;
                var display = Instantiate(item);
                display.hideFlags = HideFlags.HideAndDontSave;
                display.HandPose = defaults.ResolveHold(item);
                display.FirstPersonPose = defaults.ResolveSpatial(item, true);
                if (display is SlingshotDefinition sling && item is SlingshotDefinition source)
                {
                    sling.RemoteChargePose = defaults.ResolveCharge(source, false);
                    sling.FirstPersonChargePose = defaults.ResolveCharge(source, true);
                }
                using (new EditorGUI.DisabledScope(true))
                {
                    string group = field == "HandPose" ? heavy ? "HeavyHoldSettings" : "HoldSettings" :
                        field == "FirstPersonPose" ? heavy ? "resolved heavy hold" : field : "SlingshotChargePose";
                    bool itemSource = field == "FirstPersonPose" && item.HoldMode == ItemHoldMode.Heavy && item.OverrideHoldSettings;
                    EditorGUILayout.ObjectField($"{item.name}: {(itemSource ? "HandPose spatial fields" : group)} (inherited)",
                        itemSource ? (UnityEngine.Object)item : defaults, typeof(ScriptableObject), false);
                    using var values = new SerializedObject(display);
                    var property = values.FindProperty(field);
                    property.isExpanded = true;
                    DrawPose(property, field, slingshot, heavy);
                }
                DestroyImmediate(display);
            }
        }

        private static void DrawPose(SerializedProperty property, string label, bool slingshot, bool heavy)
        {
            if (!slingshot && !heavy) { EditorGUILayout.PropertyField(property, new GUIContent(label), true); return; }
            property.isExpanded = EditorGUILayout.Foldout(property.isExpanded, label, true);
            if (!property.isExpanded) return;
            EditorGUI.indentLevel++;
            var child = property.Copy();
            var end = property.GetEndProperty();
            for (bool enter = true; child.NextVisible(enter) && !SerializedProperty.EqualContents(child, end); enter = false)
            {
                if (slingshot && child.name is "ChargeControlPosition" or "ChargedPosition" or "ChargedWristEuler" or "ChargePoseDuration") continue;
                string title = heavy ? child.name switch
                {
                    "HoldPosition" => "Hold Center", "ChargeControlPosition" => "Charge Control Center",
                    "ChargedPosition" => "Charged Center", "HoldWristEuler" => "Hold Object Euler",
                    "ChargedWristEuler" => "Charged Object Euler", _ => child.displayName
                } : child.displayName;
                EditorGUILayout.PropertyField(child, new GUIContent(title), true);
            }
            EditorGUI.indentLevel--;
        }
    }
}
