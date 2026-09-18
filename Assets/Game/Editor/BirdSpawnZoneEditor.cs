using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace TwoBirds.Editor
{
    [CustomEditor(typeof(BirdSpawnZone))]
    public sealed class BirdSpawnZoneEditor : UnityEditor.Editor
    {
        [MenuItem("GameObject/Two Birds/Bird Spawn Zone", false, 10)]
        private static void CreateBirdSpawnZone()
        {
            var go = new GameObject("Bird Spawn Zone");
            go.AddComponent<BirdSpawnZone>();
            GameObjectUtility.SetParentAndAlign(go, Selection.activeGameObject);
            Undo.RegisterCreatedObjectUndo(go, "Create Bird Spawn Zone");
            Selection.activeGameObject = go;
        }

        private readonly BoxBoundsHandle _boxHandle = new();
        private readonly SphereBoundsHandle _sphereHandle = new();

        private void OnSceneGUI()
        {
            var zone = (BirdSpawnZone)target;
            var color = new Color(1f, 0.92f, 0.016f, 0.6f);
            if (zone.Shape == ZoneShape.Sphere)
            {
                _sphereHandle.center = zone.transform.position;
                _sphereHandle.radius = zone.Radius;
                _sphereHandle.handleColor = color;
                _sphereHandle.wireframeColor = color;
                EditorGUI.BeginChangeCheck();
                _sphereHandle.DrawHandle();
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(zone, "Edit Zone Radius");
                    Undo.RecordObject(zone.transform, "Move Zone");
                    zone.transform.position = _sphereHandle.center;
                    zone.Radius = _sphereHandle.radius;
                }
            }
            else
            {
                var matrix = Handles.matrix;
                Handles.matrix = zone.transform.localToWorldMatrix;
                _boxHandle.center = Vector3.zero;
                _boxHandle.size = zone.Size;
                _boxHandle.handleColor = color;
                _boxHandle.wireframeColor = color;
                EditorGUI.BeginChangeCheck();
                _boxHandle.DrawHandle();
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(zone, "Edit Zone Size");
                    Undo.RecordObject(zone.transform, "Move Zone");
                    zone.transform.position = zone.transform.TransformPoint(_boxHandle.center);
                    zone.Size = _boxHandle.size;
                }
                Handles.matrix = matrix;
            }
        }
    }
}
