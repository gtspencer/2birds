using System.Collections.Generic;
using FishNet.Object;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TwoBirds
{
    internal sealed class SplatAttachment
    {
        private static readonly HumanBodyBones[] Bones =
        {
            HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest,
            HumanBodyBones.Neck, HumanBodyBones.Head, HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm,
            HumanBodyBones.LeftLowerArm, HumanBodyBones.RightLowerArm, HumanBodyBones.LeftHand, HumanBodyBones.RightHand,
            HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg, HumanBodyBones.LeftLowerLeg,
            HumanBodyBones.RightLowerLeg, HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot
        };
        private readonly Dictionary<Transform, string> scenePaths = new();
        private readonly Dictionary<string, Transform> sceneTargets = new();
        private readonly WorldItemRegistry registry;

        internal SplatAttachment(WorldItemRegistry registry, Scene scene)
        {
            this.registry = registry;
            foreach (var root in scene.GetRootGameObjects()) Cache(root.transform, root.transform.GetSiblingIndex().ToString());
        }

        private void Cache(Transform node, string path)
        {
            if (node.GetComponent<WorldItem>() || node.GetComponent<PlayerInventory>() || node.GetComponent<GolfCartNetwork>() ||
                node.GetComponent<BirdRegistry>() || node.GetComponent<BirdView>() || node.GetComponent<NetworkObject>()) return;
            scenePaths[node] = path; sceneTargets[path] = node;
            for (int i = 0; i < node.childCount; i++) Cache(node.GetChild(i), path + "/" + i);
        }

        internal void Capture(uint source, Collider collider, Vector3 point, Vector3 normal, bool presented,
            out SplatTarget target, out Vector3 localPoint, out Quaternion localRotation)
        {
            target = default; localPoint = default; localRotation = Quaternion.identity;
            if (!collider) return;
            Transform frame = null;
            int playerId = collider.TryGetComponent<PlayerItemHitbox>(out var hitbox) ? hitbox.Motor.ObjectId :
                collider.TryGetComponent<PlayerEffectReceiver>(out var receiver) ? receiver.Effects.ObjectId : -1;
            var parentPlayer = collider.GetComponentInParent<PlayerInventory>();
            var cart = collider.GetComponentInParent<GolfCartNetwork>();
            var item = collider.GetComponentInParent<WorldItem>();
            var networkObject = collider.GetComponentInParent<NetworkObject>();
            if (playerId < 0 && parentPlayer) playerId = parentPlayer.ObjectId;
            if (playerId >= 0 && registry.TryGetPlayer(playerId, out var player))
            {
                var avatar = player.GetComponent<PlayerAvatarPresentation>();
                var binding = avatar.Presentation.Binding;
                if (binding == null) return;
                if (!presented)
                {
                    var physical = player.Effects.Motor.Body;
                    var placement = avatar.CurrentPlacement.Facing;
                    var rotation = placement.rotation * Quaternion.Inverse(physical.rotation);
                    point = placement.position + rotation * (point - physical.position);
                    normal = rotation * normal;
                }
                float closest = float.PositiveInfinity;
                foreach (var bone in Bones)
                {
                    var candidate = binding.GetBone(bone);
                    if (!candidate || (candidate.position - point).sqrMagnitude >= closest) continue;
                    frame = candidate; closest = (candidate.position - point).sqrMagnitude; target.Bone = bone;
                }
                target.Kind = SplatTargetKind.Player; target.Id = (uint)playerId;
                target.Lifetime = player.Effects.Lifetime; target.Reset = player.Effects.Reset;
            }
            else if (cart)
            {
                target = new SplatTarget { Kind = SplatTargetKind.Cart, Id = (uint)cart.ObjectId, Lifetime = cart.EffectLifetime };
                var graphics = cart.Presentation.Graphics;
                if (collider.transform.IsChildOf(graphics))
                {
                    target.Path = ChildPath(graphics, collider.transform);
                    frame = collider.transform;
                }
                else
                {
                    frame = graphics;
                    var physical = cart.Controller.Body;
                    point = graphics.TransformPoint(Quaternion.Inverse(physical.rotation) * (point - physical.position));
                    normal = graphics.rotation * Quaternion.Inverse(physical.rotation) * normal;
                }
            }
            else if (item)
            {
                if (item.Record.Motion.Id == source) return;
                target = new SplatTarget { Kind = SplatTargetKind.Item, Id = item.Record.Motion.Id };
                frame = item.SplatTransform;
                point = frame.TransformPoint(item.transform.InverseTransformPoint(point));
                normal = frame.rotation * Quaternion.Inverse(item.transform.rotation) * normal;
            }
            else if (BirdRegistry.Instance && BirdRegistry.Instance.TryGetSplatBird(collider, out uint life, out var view))
            {
                target = new SplatTarget { Kind = SplatTargetKind.Bird, Id = life };
                if (!view) return;
                frame = view.transform;
                point += frame.position - collider.transform.position;
            }
            else if (networkObject)
            {
                target = new SplatTarget { Kind = SplatTargetKind.Network, Id = (uint)networkObject.ObjectId,
                    Path = ChildPath(networkObject.transform, collider.transform) };
                frame = collider.transform;
            }
            else if (scenePaths.TryGetValue(collider.transform, out var path))
            {
                target = new SplatTarget { Kind = SplatTargetKind.Scene, Path = path };
                frame = collider.transform;
            }
            if (!frame) { target.Kind = SplatTargetKind.Missing; return; }
            Vector3 inward = normal.sqrMagnitude > 0f ? -normal.normalized : Vector3.forward;
            Vector3 tangent = Mathf.Abs(Vector3.Dot(inward, Vector3.up)) > 0.99f ? Vector3.forward : Vector3.up;
            localPoint = frame.InverseTransformPoint(point);
            localRotation = Quaternion.Inverse(frame.rotation) * Quaternion.LookRotation(inward, tangent);
        }

        internal bool Resolve(SplatTarget target, out Transform frame, out SplatTargetLifetime owner)
        {
            frame = null; owner = null;
            Transform root = null;
            switch (target.Kind)
            {
                case SplatTargetKind.Player:
                    if (!registry.TryGetPlayer((int)target.Id, out var player) || !player ||
                        player.Effects.Lifetime != target.Lifetime || player.Effects.Reset != target.Reset) return false;
                    var avatar = player.GetComponent<PlayerAvatarPresentation>().Presentation;
                    frame = avatar.Binding?.GetBone(target.Bone);
                    if (avatar.Binding != null && !frame) return false;
                    owner = SplatTargetLifetime.Get(player.transform); owner.BindPlayer(player);
                    return player.gameObject.activeInHierarchy;
                case SplatTargetKind.Cart:
                    if (!registry.TryGetSplatNetworkObject((int)target.Id, out var cartObject) ||
                        !cartObject.TryGetComponent<GolfCartNetwork>(out var cart) || cart.EffectLifetime != target.Lifetime) return false;
                    root = cart.transform; frame = Child(cart.Presentation.Graphics, target.Path);
                    break;
                case SplatTargetKind.Item:
                    if (!registry.TryGetItem(target.Id, out var item) || !item) return false;
                    root = item.transform; frame = item.SplatTransform;
                    break;
                case SplatTargetKind.Bird:
                    if (!BirdRegistry.Instance || !BirdRegistry.Instance.TryGetSplatView(target.Id, out var view)) return false;
                    root = frame = view.transform;
                    break;
                case SplatTargetKind.Network:
                    if (!registry.TryGetSplatNetworkObject((int)target.Id, out var networkObject)) return false;
                    root = networkObject.transform; frame = Child(root, target.Path);
                    break;
                case SplatTargetKind.Scene:
                    sceneTargets.TryGetValue(target.Path, out root); frame = root;
                    break;
            }
            if (!root || !frame || !frame.gameObject.activeInHierarchy) return false;
            owner = SplatTargetLifetime.Get(root);
            return true;
        }

        private static string ChildPath(Transform root, Transform child)
        {
            var path = new List<int>();
            while (child != root) { path.Add(child.GetSiblingIndex()); child = child.parent; }
            path.Reverse();
            return string.Join("/", path);
        }

        private static Transform Child(Transform root, string path)
        {
            if (string.IsNullOrEmpty(path)) return root;
            foreach (var part in path.Split('/'))
            {
                if (!int.TryParse(part, out int index) || index < 0 || index >= root.childCount) return null;
                root = root.GetChild(index);
            }
            return root;
        }
    }
}
