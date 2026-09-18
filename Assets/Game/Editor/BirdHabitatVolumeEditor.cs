using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace TwoBirds.Editor
{
    [CustomEditor(typeof(BirdHabitatVolume))]
    public sealed class BirdHabitatVolumeEditor : UnityEditor.Editor
    {
        [MenuItem("GameObject/Two Birds/Habitat/Generic", false, 10)]
        private static void CreateHabitatVolume()
        {
            var go = new GameObject("Bird Habitat Volume");
            go.AddComponent<BirdHabitatVolume>();
            GameObjectUtility.SetParentAndAlign(go, Selection.activeGameObject);
            Undo.RegisterCreatedObjectUndo(go, "Create Bird Habitat Volume");
            Selection.activeGameObject = go;
        }

        [MenuItem("GameObject/Two Birds/Habitat/Water", false, 10)]
        private static void CreateWaterHabitatVolume()
        {
            var go = new GameObject("Water Habitat Volume");
            var habitat = go.AddComponent<BirdHabitatVolume>();
            habitat.Kind = BirdHabitatKind.Water;
            GameObjectUtility.SetParentAndAlign(go, Selection.activeGameObject);
            Undo.RegisterCreatedObjectUndo(go, "Create Water Habitat Volume");
            Selection.activeGameObject = go;
        }

        [MenuItem("GameObject/Two Birds/Habitat/Air", false, 10)]
        private static void CreateAirHabitatVolume()
        {
            var go = new GameObject("Air Habitat Volume");
            var habitat = go.AddComponent<BirdHabitatVolume>();
            habitat.Kind = BirdHabitatKind.Air;
            GameObjectUtility.SetParentAndAlign(go, Selection.activeGameObject);
            Undo.RegisterCreatedObjectUndo(go, "Create Air Habitat Volume");
            Selection.activeGameObject = go;
        }

        private readonly BoxBoundsHandle _boxHandle = new();
        private readonly SphereBoundsHandle _sphereHandle = new();

        private void OnSceneGUI()
        {
            var volume = (BirdHabitatVolume)target;
            var color = volume.Kind == BirdHabitatKind.Water ? new Color(0f, 1f, 1f, 0.6f)
                : volume.Kind == BirdHabitatKind.Air ? new Color(1f, 1f, 1f, 0.6f)
                : new Color(0f, 1f, 0f, 0.6f);
            if (volume.Shape == ZoneShape.Sphere)
            {
                _sphereHandle.center = volume.transform.position;
                _sphereHandle.radius = volume.Radius;
                _sphereHandle.handleColor = color;
                _sphereHandle.wireframeColor = color;
                EditorGUI.BeginChangeCheck();
                _sphereHandle.DrawHandle();
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(volume, "Edit Volume Radius");
                    Undo.RecordObject(volume.transform, "Move Volume");
                    volume.transform.position = _sphereHandle.center;
                    volume.Radius = _sphereHandle.radius;
                }
            }
            else
            {
                var matrix = Handles.matrix;
                Handles.matrix = volume.transform.localToWorldMatrix;
                _boxHandle.center = Vector3.zero;
                _boxHandle.size = volume.Size;
                _boxHandle.handleColor = color;
                _boxHandle.wireframeColor = color;
                EditorGUI.BeginChangeCheck();
                _boxHandle.DrawHandle();
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(volume, "Edit Volume Size");
                    Undo.RecordObject(volume.transform, "Move Volume");
                    volume.transform.position = volume.transform.TransformPoint(_boxHandle.center);
                    volume.Size = _boxHandle.size;
                }
                Handles.matrix = matrix;
            }
        }
    }
}
