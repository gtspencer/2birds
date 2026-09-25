#if UNITY_INCLUDE_INSTRUMENTATION
using System.Collections.Generic;
using UnityEngine;

namespace TwoBirds
{
    public sealed class GripObserverPreview : MonoBehaviour
    {
        private AvatarPresentation presentation;
        private PlayerAvatarPresentation owner;
        private PlayerHeldItemPresentation held;
        private ItemDefinition definition;
        private GameObject visual;
        private SlingshotPresentation slingshot;
        private AvatarCosmeticPresentation cosmetics;
        private float lateral;
        private bool followsOwner;
        private readonly HashSet<ulong> isolatedBindings = new();
        internal HeldItemPresentationState State { get; private set; }
        public AvatarPresentation Presentation => presentation;
        internal string DisplayName => presentation.Resolved?.Settings.DisplayName;
        internal void Initialize(PlayerAvatarPresentation owner, AvatarRegistry avatars, float lateral, AvatarId avatar = default)
        {
            this.owner = owner; this.lateral = lateral;
            held = owner.GetComponent<PlayerHeldItemPresentation>();
            presentation = GetComponent<AvatarPresentation>();
            presentation.Configure(avatars, true);
            presentation.InputSource = () => Shifted(owner.CurrentPlacement);
            State = new HeldItemPresentationState(presentation, transform);
            State.CommitItem = pose => { if (visual) visual.transform.SetPositionAndRotation(pose.position, pose.rotation); };
            presentation.PreparingHands += Prepare;
            presentation.HandsEvaluated += Commit;
            presentation.DidBind += Bound;
            presentation.WillUnbind += Unbound;
            followsOwner = !avatar.IsValid;
            if (!followsOwner) { presentation.RequestAvatar(avatar); return; }
            owner.Presentation.IdentityResolved += SelectedAvatar;
            if (owner.Presentation.Resolved != null) SelectedAvatar(owner.Presentation.Resolved);
        }
        private Vector3 Offset(in AvatarPresentationInput placement) =>
            Quaternion.Euler(0f, placement.Facing.rotation.eulerAngles.y, 0f) * Vector3.right * lateral;
        private AvatarPresentationInput Shifted(AvatarPresentationInput placement)
        {
            Vector3 offset = Offset(placement);
            placement.SolePosition += offset; placement.Facing.position += offset;
            return placement;
        }
        private void SelectedAvatar(AvatarRegistry.Entry entry) => presentation.RequestAvatar(entry.Id);
        private void Bound(AvatarBinding binding)
        {
            var session = SessionController.Instance;
            cosmetics = new AvatarCosmeticPresentation(binding, session.Hats, session.Tattoos);
            cosmetics.Apply(owner.Appearance);
            AvatarCosmeticPresentation.SetLayer(binding.Animator.gameObject, LayerMask.NameToLayer(AvatarEditorPreview.LayerName));
        }
        private void Unbound(AvatarBinding binding) { cosmetics?.Dispose(); cosmetics = null; State.InvalidateBinding(); }
        private void Prepare(AvatarBinding binding, float dt)
        {
            if (isolatedBindings.Add(binding.Generation))
            {
                AvatarCosmeticPresentation.SetLayer(binding.Animator.gameObject, LayerMask.NameToLayer(AvatarEditorPreview.LayerName));
                foreach (var collider in binding.Animator.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
                foreach (var body in binding.Animator.GetComponentsInChildren<Rigidbody>(true)) { body.isKinematic = true; body.detectCollisions = false; }
            }
            var input = held.CaptureInput(false);
            Vector3 offset = Offset(input.Placement);
            input.Placement = Shifted(input.Placement); input.Aim.position += offset; input.Projectile.position += offset;
            if (definition != input.SelectedDefinition)
            {
                if (visual) Destroy(visual);
                definition = input.SelectedDefinition;
                visual = definition ? ItemPresentationVisual.Create(definition, transform, LayerMask.NameToLayer(AvatarEditorPreview.LayerName)) : null;
                slingshot = visual ? visual.GetComponent<SlingshotPresentation>() : null;
            }
            State.SetInput(input, slingshot);
            var frame = new HeldItemBodyFrame(binding.GetBone(HumanBodyBones.RightUpperArm).position,
                binding.Animator.transform.rotation, binding.Measurements, binding.Scale,
                leftShoulder: binding.GetBone(HumanBodyBones.LeftUpperArm).position);
            presentation.HandTargets.SetBody(binding.Body);
            if (presentation.Binding == null || presentation.Binding == binding) State.PrepareHands(binding, frame);
            if (visual) visual.SetActive(State.CanShowHeldItem);
        }
        private void Commit(AvatarBinding binding) => State.CommitHands(binding);
        private void OnDestroy()
        {
            if (owner && followsOwner) owner.Presentation.IdentityResolved -= SelectedAvatar;
            if (presentation)
            {
                presentation.PreparingHands -= Prepare; presentation.HandsEvaluated -= Commit;
                presentation.DidBind -= Bound; presentation.WillUnbind -= Unbound;
                presentation.InputSource = null; presentation.SetVisual(false);
            }
            cosmetics?.Dispose(); State?.Dispose(); State = null;
            if (visual) Destroy(visual);
        }
    }
}
#endif
