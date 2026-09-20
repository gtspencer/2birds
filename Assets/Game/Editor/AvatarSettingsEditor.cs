using UnityEditor;

namespace TwoBirds.Editor
{
    [CustomEditor(typeof(AvatarSettings))]
    public sealed class AvatarSettingsEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            DrawPropertiesExcluding(serializedObject, "m_Script", "Id", "Generated");
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("Id"), true);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("Generated"), true);
            }
            serializedObject.ApplyModifiedProperties();
        }
    }
}
