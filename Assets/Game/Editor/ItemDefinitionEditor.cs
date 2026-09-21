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
                        EditorGUILayout.PropertyField(property, new GUIContent("First Person Spatial Pose"), true);
                    else using (new EditorGUI.DisabledScope(true))
                        EditorGUILayout.ObjectField("First Person Defaults", registry ? registry.HeldItemDefaults : null, typeof(HeldItemSettings), false);
                    continue;
                }
                if (property.name == "HandPose")
                {
                    if (overrideSettings.hasMultipleDifferentValues) continue;
                    if (overrideSettings.boolValue)
                        EditorGUILayout.PropertyField(property, new GUIContent("Hold Settings"), true);
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
    }
}
