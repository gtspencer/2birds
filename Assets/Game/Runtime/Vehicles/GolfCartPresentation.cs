using System;
using UnityEngine;

namespace TwoBirds
{
    [DefaultExecutionOrder(0)]
    public sealed class GolfCartPresentation : MonoBehaviour
    {
        [Serializable]
        private struct PaintSlot
        {
            public Renderer Renderer;
            public int MaterialIndex;
        }
        [SerializeField] private Transform[] wheels;
        [SerializeField] private Transform steeringWheel;
        [SerializeField] private PaintSlot[] paintSlots;
        private readonly Quaternion[] restRotations = new Quaternion[4];
        private readonly Vector3[] restPositions = new Vector3[4];
        private readonly float[] wheelScale = new float[4];
        private MaterialPropertyBlock[] blocks;
        private Quaternion steeringRest;
        private GolfCartNetwork network;
        private GolfCartSettings settings;
        private uint lastEpoch;
        private Vector3 previousPosition;
        private float spin, rearSpin;
        private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");

        private void Awake()
        {
            network = GetComponent<GolfCartNetwork>();
            settings = GetComponent<GolfCartController>().Settings;
            for (int i = 0; i < 4; i++)
            {
                restRotations[i] = wheels[i].localRotation;
                restPositions[i] = wheels[i].localPosition;
                wheelScale[i] = 1f / wheels[i].parent.lossyScale.y;
            }
            steeringRest = steeringWheel.localRotation;
            blocks = new MaterialPropertyBlock[paintSlots.Length];
            for (int i = 0; i < blocks.Length; i++) blocks[i] = new MaterialPropertyBlock();
            previousPosition = transform.position;
        }

        internal void SetColor(Color color)
        {
            for (int i = 0; i < paintSlots.Length; i++)
            {
                var slot = paintSlots[i];
                slot.Renderer.GetPropertyBlock(blocks[i], slot.MaterialIndex);
                blocks[i].SetColor(BaseColor, color);
                slot.Renderer.SetPropertyBlock(blocks[i], slot.MaterialIndex);
            }
        }

        private void LateUpdate()
        {
            if (!network.IsServerInitialized && !network.IsClientInitialized) return;
            var frame = network.DisplayMotion;
            if (lastEpoch == frame.Epoch)
            {
                float turn = Vector3.Dot(frame.Position - previousPosition, frame.Rotation * Vector3.forward) / settings.WheelRadius * Mathf.Rad2Deg;
                spin += turn;
                if (!frame.Handbrake) rearSpin += turn;
            }
            lastEpoch = frame.Epoch;
            previousPosition = frame.Position;
            float steer = frame.Steering / 127f * settings.SteeringAngle;
            for (int i = 0; i < 4; i++)
            {
                byte packed = i == 0 ? frame.FrontLeft : i == 1 ? frame.FrontRight : i == 2 ? frame.RearLeft : frame.RearRight;
                wheels[i].localPosition = restPositions[i] + Vector3.up * (packed / 255f - 0.35f) * settings.SuspensionTravel * wheelScale[i];
                wheels[i].localRotation = Quaternion.Euler(0f, i < 2 ? steer : 0f, 0f) * restRotations[i] * Quaternion.Euler(i < 2 ? spin : rearSpin, 0f, 0f);
            }
            steeringWheel.localRotation = steeringRest * Quaternion.Euler(0f, -steer * 4f, 0f);
        }
    }
}
