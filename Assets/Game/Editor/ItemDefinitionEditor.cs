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
            if (heavy) EditorGUILayout.HelpBox("Heavy positions place the collider reference center relative to the shoulder midpoint, in average arm lengths. Euler offsets rotate the object relative to the body before its prefab rotation. Both views use this frame. Author palm contacts with LeftHandGrip and RightHandGrip on the prefab.", MessageType.Info);
            var overrideSettings = serializedObject.FindProperty("OverrideHoldSettings");
            var property = serializedObject.GetIterator();
            for (bool enter = true; property.NextVisible(enter); enter = false)
            {
                if (heavy && property.name is "GripPosition" or "GripEuler") continue;
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
                    else using (new EditorGUI.DisabledScope(true))
                        EditorGUILayout.ObjectField("First Person Defaults", registry ? registry.HeldItemDefaults : null, typeof(HeldItemSettings), false);
                    continue;
                }
                if (property.name == "HandPose")
                {
                    if (overrideSettings.hasMultipleDifferentValues) continue;
                    if (overrideSettings.boolValue)
                        DrawPose(property, "Hold Settings", slingshot, heavy);
                    else
                        using (new EditorGUI.DisabledScope(true))
                            EditorGUILayout.ObjectField("System Defaults", registry ? registry.HeldItemDefaults : null,
                                typeof(HeldItemSettings), false);
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
