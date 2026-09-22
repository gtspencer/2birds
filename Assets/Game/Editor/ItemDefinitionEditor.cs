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
            var overrideSettings = serializedObject.FindProperty("OverrideHoldSettings");
            var property = serializedObject.GetIterator();
            for (bool enter = true; property.NextVisible(enter); enter = false)
            {
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
                        DrawPose(property, "First Person Spatial Pose", slingshot);
                    else using (new EditorGUI.DisabledScope(true))
                        EditorGUILayout.ObjectField("First Person Defaults", registry ? registry.HeldItemDefaults : null, typeof(HeldItemSettings), false);
                    continue;
                }
                if (property.name == "HandPose")
                {
                    if (overrideSettings.hasMultipleDifferentValues) continue;
                    if (overrideSettings.boolValue)
                        DrawPose(property, "Hold Settings", slingshot);
                    else
                        using (new EditorGUI.DisabledScope(true))
                            EditorGUILayout.ObjectField("System Defaults", registry ? registry.HeldItemDefaults : null,
                                typeof(HeldItemSettings), false);
                    continue;
                }
                using (new EditorGUI.DisabledScope(property.name == "m_Script"))
                    EditorGUILayout.PropertyField(property, property.name == "OverrideHoldSettings"
                        ? new GUIContent("Override Third Person / Timing") : new GUIContent(property.displayName), true);
                if (property.name == "OverrideHoldSettings" && overrideSettings.boolValue)
                    serializedObject.FindProperty("HandPose").isExpanded = true;
            }
            serializedObject.ApplyModifiedProperties();
        }

        private static void DrawPose(SerializedProperty property, string label, bool slingshot)
        {
            if (!slingshot) { EditorGUILayout.PropertyField(property, new GUIContent(label), true); return; }
            property.isExpanded = EditorGUILayout.Foldout(property.isExpanded, label, true);
            if (!property.isExpanded) return;
            EditorGUI.indentLevel++;
            var child = property.Copy();
            var end = property.GetEndProperty();
            for (bool enter = true; child.NextVisible(enter) && !SerializedProperty.EqualContents(child, end); enter = false)
            {
                if (child.name is "ChargeControlPosition" or "ChargedPosition" or "ChargedWristEuler" or "ChargePoseDuration") continue;
                EditorGUILayout.PropertyField(child, true);
            }
            EditorGUI.indentLevel--;
        }
    }
}
