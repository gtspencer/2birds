using System;
using FishNet.Component.Transforming.Beta;
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
        public AvatarHandContact LeftHandContact, RightHandContact;
        [SerializeField] private Transform gasPedal;
        [SerializeField] private Transform brakePedal;
        [Header("Lights and Horn")]
        [SerializeField] private GameObject[] headlights;
        [SerializeField] private Material litMaterial;
        [SerializeField] private MeshRenderer headlightMesh;
        private Material defaultMaterial;
        [SerializeField] private SfxSource hornSource;
        [SerializeField] private SoundEffect hornSfx;
        [SerializeField] private Vector3 gasPedalAngle = new(15f, 0f, 0f);
        [SerializeField] private Vector3 brakePedalAngle = new(15f, 0f, 0f);
        [SerializeField] private float pedalSpeed = 8f;
        [SerializeField] private PaintSlot[] paintSlots;
        private readonly Quaternion[] restRotations = new Quaternion[4];
        private readonly Vector3[] restPositions = new Vector3[4];
        private readonly float[] wheelScale = new float[4];
        private MaterialPropertyBlock[] blocks;
        private Quaternion steeringRest, gasPedalRest, brakePedalRest;
        private NetworkTickSmoother tickSmoother;
        internal Transform Graphics { get; private set; }
        private GolfCartNetwork network;
        private GolfCartSettings settings;
        private uint lastEpoch;
        private Vector3 previousPosition;
        private float spin, rearSpin;
        private float displaySteering, gasPedalT, brakePedalT, previousForwardSpeed;
        private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");

        private void Awake()
        {
            Graphics = transform.Find("Graphics");
            tickSmoother = Graphics.GetComponent<NetworkTickSmoother>();
            network = GetComponent<GolfCartNetwork>();
            settings = GetComponent<GolfCartController>().Settings;
            for (int i = 0; i < 4; i++)
            {
                restRotations[i] = wheels[i].localRotation;
                restPositions[i] = wheels[i].localPosition;
                wheelScale[i] = 1f / wheels[i].parent.lossyScale.y;
            }
            steeringRest = steeringWheel.localRotation;
            if (gasPedal != null) gasPedalRest = gasPedal.localRotation;
            if (brakePedal != null) brakePedalRest = brakePedal.localRotation;
            blocks = new MaterialPropertyBlock[paintSlots.Length];
            for (int i = 0; i < blocks.Length; i++) blocks[i] = new MaterialPropertyBlock();
            previousPosition = transform.position;

            defaultMaterial = headlightMesh.material;
            SetLights(false);
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

        internal void SetLights(bool on)
        {
            if (headlights == null) return;
            foreach (var light in headlights)
                if (light) light.SetActive(on);
            
            headlightMesh.material = on ? litMaterial : defaultMaterial;
        }

        internal void PlayHorn()
        {
            if (hornSource) hornSource.Play(hornSfx);
        }

        internal void ResetPose()
        {
            var smoother = tickSmoother.SmootherController;
            smoother?.StopSmoother();
            Graphics.SetPositionAndRotation(transform.position, transform.rotation);
            previousPosition = Graphics.position;
            smoother?.StartSmoother();
        }

        private void LateUpdate()
        {
            if (!network.IsServerInitialized && !network.IsClientInitialized) return;
            var frame = network.DisplayMotion;
            if (lastEpoch == frame.Epoch)
            {
                float turn = Vector3.Dot(frame.Position - previousPosition, frame.Rotation * Vector3.forward) / settings.WheelRadius * Mathf.Rad2Deg;
                spin = (spin + turn) % 360f;
                if (!frame.Handbrake) rearSpin = (rearSpin + turn) % 360f;
            }
            lastEpoch = frame.Epoch;
            previousPosition = frame.Position;
            float steer = frame.Steering / 127f * settings.SteeringAngle;
            for (int i = 0; i < 4; i++)
            {
                byte packed = i switch
                {
                    0 => frame.FrontLeft,
                    1 => frame.FrontRight,
                    2 => frame.RearLeft,
                    _ => frame.RearRight
                };
                wheels[i].localPosition = restPositions[i] + Vector3.up * (packed / 255f - 0.35f) * settings.SuspensionTravel * wheelScale[i];
                wheels[i].localRotation = Quaternion.Euler(0f, i < 2 ? steer : 0f, 0f) * restRotations[i] * Quaternion.Euler(i < 2 ? spin : rearSpin, 0f, 0f);
            }
            displaySteering = Mathf.MoveTowards(displaySteering, steer, Time.deltaTime * 180f);
            steeringWheel.localRotation = steeringRest * Quaternion.Euler(0f, displaySteering * 4f, 0f);

            if (gasPedal || brakePedal != null)
            {
                float forwardSpeed = Vector3.Dot(frame.Velocity, frame.Rotation * Vector3.forward);
                float gasTarget = 0f, brakeTarget = 0f;
                var local = PlayerSeating.Local;
                if (local && local.Cart == network && local.IsDriver && !local.TransitionPending)
                {
                    var move = local.Input.CartMove;
                    bool braking = move.y * forwardSpeed < 0f && Mathf.Abs(forwardSpeed) > settings.ReverseDeadband;
                    gasTarget = !braking && Mathf.Abs(move.y) > 0.1f ? 1f : 0f;
                    brakeTarget = braking || local.Input.Handbrake ? 1f : 0f;
                }
                else if (!frame.ParkingBrake)
                {
                    float accel = Time.deltaTime > 0.001f ? (forwardSpeed - previousForwardSpeed) / Time.deltaTime : 0f;
                    bool moving = Mathf.Abs(forwardSpeed) > 0.5f;
                    gasTarget = moving && accel * Mathf.Sign(forwardSpeed) > -2f ? 1f : 0f;
                    brakeTarget = frame.Handbrake || (moving && accel * Mathf.Sign(forwardSpeed) < -3f) ? 1f : 0f;
                }
                previousForwardSpeed = forwardSpeed;
                gasPedalT = Mathf.MoveTowards(gasPedalT, gasTarget, Time.deltaTime * pedalSpeed);
                brakePedalT = Mathf.MoveTowards(brakePedalT, brakeTarget, Time.deltaTime * pedalSpeed);
                if (gasPedal) gasPedal.localRotation = gasPedalRest * Quaternion.Euler(gasPedalAngle * gasPedalT);
                if (brakePedal) brakePedal.localRotation = brakePedalRest * Quaternion.Euler(brakePedalAngle * brakePedalT);
            }
        }
    }
}
