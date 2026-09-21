using UnityEditor;
using UnityEngine;

namespace TwoBirds.Editor
{
    [CustomEditor(typeof(ItemDefinition)), CanEditMultipleObjects]
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
                    EditorGUILayout.PropertyField(property, true);
                if (property.name == "OverrideHoldSettings" && overrideSettings.boolValue)
                    serializedObject.FindProperty("HandPose").isExpanded = true;
            }
            serializedObject.ApplyModifiedProperties();
        }
    }
}
