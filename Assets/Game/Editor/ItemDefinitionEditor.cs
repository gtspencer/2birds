using UnityEditor;

namespace TwoBirds.Editor
{
    [CustomEditor(typeof(ItemDefinition), true), CanEditMultipleObjects]
    public sealed class ItemDefinitionEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            bool twoHand = System.Array.TrueForAll(targets, value => ((ItemDefinition)value).HoldMode == ItemHoldMode.TwoHand);
            if (twoHand) EditorGUILayout.HelpBox("TwoHand items sit between the posed palms: the midpoint of the palm contacts sits at the palms' midpoint. Author both palm contacts in item-root space.", MessageType.Info);
            var property = serializedObject.GetIterator();
            for (bool enter = true; property.NextVisible(enter); enter = false)
            {
                if (!twoHand && property.name == "LeftPalmContact") continue;
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
        }
    }
}
