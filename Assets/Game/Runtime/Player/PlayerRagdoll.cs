using System.Collections.Generic;
using FishNet.Component.Prediction;
using UnityEngine;

namespace TwoBirds
{
    public struct PlayerRagdollSeed
    {
        public Vector3 Position, PosePosition, Velocity;
        public Quaternion Rotation;
        public bool Seated, Carried;
    }

    public sealed class PlayerRagdoll : MonoBehaviour
    {
        private static readonly Dictionary<Collider, PlayerRevival> targets = new();
        [SerializeField] private Rigidbody proxy;
        [SerializeField] private Collider proxyCollider;
        private readonly RigidbodyPauser pauser = new();
        private PlayerNetworkState network;
        private PlayerAvatarPresentation playerAvatar;
        private AvatarPresentation avatar;
        private AvatarRagdoll physical;
        private PlayerRevival revival;
        private PlayerRagdollSeed seed;
        private Vector3 remoteFrom, remoteTo;
        private float sampleTime, quietSince;
        private bool active;
        private ulong bindingGeneration;
        public Vector3 RootPosition => !active ? transform.position : network.IsOwner ?
            physical ? physical.Pelvis.position : proxy.position :
            Vector3.Lerp(remoteFrom, remoteTo, Mathf.Clamp01((Time.unscaledTime - sampleTime) / 0.1f));
        internal bool Settled => physical ? physical.Settled : proxy.IsSleeping() ||
            proxy.linearVelocity.sqrMagnitude < 0.0025f && proxy.angularVelocity.sqrMagnitude < 0.01f;
        internal Vector3 Velocity => physical ? physical.Pelvis.linearVelocity : proxy.linearVelocity;

        private void Awake()
        {
            network = GetComponent<PlayerNetworkState>();
            playerAvatar = GetComponent<PlayerAvatarPresentation>();
            avatar = playerAvatar.Presentation;
            revival = GetComponent<PlayerRevival>();
            avatar.DidBind += Bind;
            avatar.WillUnbind += Unbind;
        }
        internal static bool TryTarget(Collider collider, out PlayerRevival target) => targets.TryGetValue(collider, out target);
        internal PlayerRagdollSeed Capture(Vector3 velocity)
        {
            var input = playerAvatar.CurrentPlacement;
            var hips = avatar.Binding?.GetBone(HumanBodyBones.Hips);
            Vector3 root = hips ? hips.position : input.Facing.position;
            if (!hips && input.Seated && avatar.Resolved != null)
                root += input.Facing.rotation * AvatarDriverPose.SeatedOffset(avatar.Resolved.Settings, avatar.Registry, input.Driver);
            return new PlayerRagdollSeed { Position = root, PosePosition = input.Facing.position,
                Rotation = input.Facing.rotation, Velocity = velocity, Seated = input.Seated, Carried = input.Carried };
        }

        internal void Begin(PlayerRagdollSeed value)
        {
            if (active) return;
            seed = value;
            active = true;
            remoteFrom = remoteTo = value.Position;
            sampleTime = quietSince = Time.unscaledTime;
            if (!network.IsClientInitialized) return;
            proxy.transform.SetParent(null, true);
            proxy.gameObject.SetActive(true);
            proxy.position = value.Position;
            proxy.rotation = value.Rotation;
            proxyCollider.enabled = true;
            proxy.isKinematic = !network.IsOwner;
            if (network.IsOwner) proxy.linearVelocity = value.Velocity;
            targets[proxyCollider] = revival;
            pauser.UpdateRigidbodies(new[] { proxy });
            var input = playerAvatar.CurrentPlacement;
            input.Facing = new Pose(value.PosePosition, value.Rotation);
            input.SolePosition += value.PosePosition - playerAvatar.CurrentPlacement.Facing.position;
            input.Seated = value.Seated;
            input.Carried = value.Carried;
            avatar.PrepareRagdoll(input);
            if (avatar.Binding != null) Bind(avatar.Binding);
        }

        private void Bind(AvatarBinding binding)
        {
            if (!active || binding.Generation < bindingGeneration) return;
            bindingGeneration = binding.Generation;
            var body = binding.Animator.GetComponent<AvatarRagdoll>();
            if (!body || physical == body) return;
            Vector3 position = RootPosition;
            Vector3 velocity = proxy.linearVelocity;
            avatar.SetPhysical(true);
            physical = body;
            physical.Activate(position, velocity, network.IsOwner);
            foreach (var collider in physical.Colliders) targets[collider] = revival;
            proxyCollider.enabled = false;
            proxy.isKinematic = true;
            proxy.gameObject.SetActive(false);
            pauser.UpdateRigidbodies(physical.Bodies);
        }

        private void Unbind(AvatarBinding binding)
        {
            if (!physical || binding.Generation != bindingGeneration) return;
            Vector3 position = RootPosition, velocity = Velocity;
            foreach (var collider in physical.Colliders) targets.Remove(collider);
            physical.Deactivate();
            physical = null;
            if (!active) return;
            proxy.gameObject.SetActive(true);
            proxy.position = position;
            proxyCollider.enabled = true;
            proxy.isKinematic = !network.IsOwner;
            if (network.IsOwner) proxy.linearVelocity = velocity;
            pauser.UpdateRigidbodies(new[] { proxy });
        }

        internal void End()
        {
            if (!active) return;
            pauser.Unpause();
            active = false;
            if (physical)
            {
                foreach (var collider in physical.Colliders) targets.Remove(collider);
                physical.Deactivate();
                physical = null;
            }
            targets.Remove(proxyCollider);
            if (!proxy.isKinematic) { proxy.linearVelocity = Vector3.zero; proxy.angularVelocity = Vector3.zero; }
            proxy.isKinematic = true;
            proxyCollider.enabled = false;
            proxy.gameObject.SetActive(false);
            proxy.transform.SetParent(transform, true);
            avatar.SetPhysical(false);
        }

        internal void ReceiveRoot(Vector3 position, bool settled)
        {
            if (network.IsOwner) return;
            remoteFrom = RootPosition;
            remoteTo = position;
            sampleTime = Time.unscaledTime;
        }
        private void BeforePhysics(float delta)
        {
            if (!active || !network.IsClientInitialized || network.PredictionManager.IsReconciling) return;
            if (!network.IsOwner)
            {
                var root = physical ? physical.Pelvis : proxy;
                root.MovePosition(RootPosition);
                return;
            }
            if (!Settled) quietSince = Time.unscaledTime;
            network.PublishRoot(RootPosition, Settled && Time.unscaledTime - quietSince >= 0.3f);
        }
        private void BeforeReplay(uint client, uint server) { if (active) pauser.Pause(); }
        private void AfterReplay(uint client, uint server) => pauser.Unpause();
        internal void OwnershipChanged()
        {
            if (!active) return;
            if (physical) physical.Pelvis.isKinematic = !network.IsOwner;
            else proxy.isKinematic = !network.IsOwner;
        }
        internal void StartNetwork()
        {
            network.TimeManager.OnPrePhysicsSimulation += BeforePhysics;
            network.PredictionManager.OnPreReconcile += BeforeReplay;
            network.PredictionManager.OnPostReconcile += AfterReplay;
        }
        internal void StopNetwork()
        {
            network.TimeManager.OnPrePhysicsSimulation -= BeforePhysics;
            network.PredictionManager.OnPreReconcile -= BeforeReplay;
            network.PredictionManager.OnPostReconcile -= AfterReplay;
            End();
        }
        private void OnDestroy()
        {
            if (avatar) { avatar.DidBind -= Bind; avatar.WillUnbind -= Unbind; }
            if (proxy) Destroy(proxy.gameObject);
        }
    }
}
