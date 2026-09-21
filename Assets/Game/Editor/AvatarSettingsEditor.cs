using System;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace TwoBirds.Editor
{
    [CustomEditor(typeof(AvatarSettings))]
    public sealed class AvatarSettingsEditor : UnityEditor.Editor
    {
        private ReorderableList chains;

        private void OnEnable()
        {
            chains = new ReorderableList(serializedObject, serializedObject.FindProperty("AdditionalSprings"), true, true, true, true);
            chains.drawHeaderCallback = rect => EditorGUI.LabelField(rect, "Chains");
            chains.elementHeightCallback = index => EditorGUI.GetPropertyHeight(chains.serializedProperty.GetArrayElementAtIndex(index), true) + 4f;
            chains.drawElementCallback = (rect, index, active, focused) =>
            {
                rect.y += 2f;
                rect.height -= 4f;
                EditorGUI.PropertyField(rect, chains.serializedProperty.GetArrayElementAtIndex(index), new GUIContent($"Chain {index + 1}"), true);
            };
            chains.onAddCallback = list =>
            {
                int index = list.serializedProperty.arraySize++;
                var element = list.serializedProperty.GetArrayElementAtIndex(index);
                element.boxedValue = new AvatarSettings.SpringChain();
                element.isExpanded = true;
                list.index = index;
            };
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            DrawPropertiesExcluding(serializedObject, "m_Script", "Id", "Generated", "FirstPersonGenerated", "AdditionalSprings");
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Additional Spring Chains", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Drag Root and Tip bones from this avatar's hierarchy. Process to apply changes. Source springs are preserved. Collider offsets and radii use the source model's units.", MessageType.Info);
            using (new EditorGUI.DisabledScope(EditorApplication.isPlaying))
                chains.DoLayoutList();
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("Id"), true);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("Generated"), true);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("FirstPersonGenerated"), true);
            }
            serializedObject.ApplyModifiedProperties();
            var settings = (AvatarSettings)target;
            using (new EditorGUI.DisabledScope(EditorApplication.isPlaying || !settings.Generated.Source))
            {
                if (GUILayout.Button("Save and Process Avatar"))
                {
                    try
                    {
                        AssetDatabase.SaveAssetIfDirty(settings);
                        AvatarProcessor.Process(AssetDatabase.GUIDToAssetPath(settings.Generated.SourceGuid));
                    }
                    catch (Exception exception) { EditorUtility.DisplayDialog("Avatar processing failed", exception.Message, "OK"); }
                }
            }
        }
    }
}
