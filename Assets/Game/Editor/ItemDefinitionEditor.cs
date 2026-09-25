using UnityEditor;

namespace TwoBirds.Editor
{
    [CustomEditor(typeof(ItemDefinition), true), CanEditMultipleObjects]
    public sealed class ItemDefinitionEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var property = serializedObject.GetIterator();
            for (bool enter = true; property.NextVisible(enter); enter = false)
            {
                if (property.name is "DestroyOnSplat" or "SplatDefinition")
                {
                    var enabled = serializedObject.FindProperty("SpawnSplat");
                    if (enabled.boolValue || enabled.hasMultipleDifferentValues)
                        EditorGUILayout.PropertyField(property);
                    continue;
                }
                if (property.name == "CollisionDamage")
                {
                    var damageOverride = serializedObject.FindProperty("OverrideCollisionDamage");
                    if (damageOverride.boolValue || damageOverride.hasMultipleDifferentValues)
                        EditorGUILayout.PropertyField(property);
                    continue;
                }
                using (new EditorGUI.DisabledScope(property.name == "m_Script"))
                    EditorGUILayout.PropertyField(property, true);
            }
            serializedObject.ApplyModifiedProperties();
            foreach (ItemDefinition definition in targets)
                if (definition.SpawnSplat && !definition.CanSpawnSplat)
                {
                    EditorGUILayout.HelpBox("Splat requires a Splat Definition with a decal material. Splat and Destroy On Splat are inactive until configured.", MessageType.Warning);
                    break;
                }
        }
    }
}
