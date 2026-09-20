using System;
using UnityEditor;
using UnityEngine;

namespace TwoBirds.Editor
{
    [CustomPropertyDrawer(typeof(AvatarBonePathAttribute))]
    public sealed class AvatarBonePathDrawer : PropertyDrawer
    {
        private static Transform GetBone(SerializedProperty property, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(property.stringValue)) return null;
            var settings = (AvatarSettings)property.serializedObject.targetObject;
            if (!settings.Generated.Source) { error = "The source VRM is missing."; return null; }
            try { return AvatarSpringAuthoring.Resolve(settings.Generated.Source.transform, property.stringValue); }
            catch (InvalidOperationException exception) { error = exception.Message; return null; }
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            GetBone(property, out string error);
            return EditorGUIUtility.singleLineHeight + (error == null ? 0f : EditorGUIUtility.singleLineHeight * 3f + EditorGUIUtility.standardVerticalSpacing);
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);
            var bone = GetBone(property, out string error);
            var field = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);
            EditorGUI.BeginChangeCheck();
            var selected = (Transform)EditorGUI.ObjectField(field, new GUIContent(label.text, property.stringValue), bone, typeof(Transform), true);
            if (EditorGUI.EndChangeCheck())
            {
                try
                {
                    property.stringValue = selected ? AvatarSpringAuthoring.PathFor((AvatarSettings)property.serializedObject.targetObject, selected) : "";
                }
                catch (InvalidOperationException exception) { EditorUtility.DisplayDialog("Cannot assign spring bone", exception.Message, "OK"); }
            }
            if (error != null)
            {
                var help = new Rect(position.x, field.yMax + EditorGUIUtility.standardVerticalSpacing, position.width, EditorGUIUtility.singleLineHeight * 3f);
                EditorGUI.HelpBox(help, error, MessageType.Error);
            }
            EditorGUI.EndProperty();
        }
    }
}
