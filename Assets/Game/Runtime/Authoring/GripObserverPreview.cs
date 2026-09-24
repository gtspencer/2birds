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
        private GripAuthoringDrafts drafts;
        private ItemDefinition definition;
        private GameObject visual;
        private SlingshotPresentation slingshot;
        private AvatarCosmeticPresentation cosmetics;
        private readonly HashSet<ulong> isolatedBindings = new();
        internal HeldItemPresentationState State { get; private set; }
        public AvatarPresentation Presentation => presentation;
        public Transform ItemRoot => visual ? visual.transform : null;
        internal void Initialize(PlayerAvatarPresentation owner, GripAuthoringDrafts drafts)
        {
            this.owner = owner; this.drafts = drafts;
            held = owner.GetComponent<PlayerHeldItemPresentation>();
            presentation = GetComponent<AvatarPresentation>();
            presentation.Configure(drafts.Avatars, true);
            presentation.InputSource = () => owner.CurrentPlacement;
            State = new HeldItemPresentationState(presentation, transform, drafts.Held);
            State.CommitItem = pose => { if (visual) visual.transform.SetPositionAndRotation(pose.position, pose.rotation); };
            presentation.PreparingHands += Prepare;
            presentation.HandsEvaluated += Commit;
            presentation.DidBind += Bound;
            presentation.WillUnbind += Unbound;
            owner.Presentation.IdentityResolved += SelectedAvatar;
            drafts.ContentChanged += ContentChanged;
            if (owner.Presentation.Resolved != null) SelectedAvatar(owner.Presentation.Resolved);
        }
        private void SelectedAvatar(AvatarRegistry.Entry entry) => presentation.RequestAvatar(entry.Id);
        private void ContentChanged(GripAuthoringDraft record) => State.RefreshContent();
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
            if (State.HeavyFrameWeight > 0f) frame = frame.WithReference(State.HeavyBody(binding.Settings));
            if (presentation.Binding != null && presentation.Binding != binding) State.PrepareCandidate(binding, frame);
            else State.PrepareHands(binding, frame);
            if (visual) visual.SetActive(State.CanShowHeldItem);
        }
        private void Commit(AvatarBinding binding) => State.CommitHands(binding);
        private void OnDestroy()
        {
            if (owner) owner.Presentation.IdentityResolved -= SelectedAvatar;
            if (drafts != null) drafts.ContentChanged -= ContentChanged;
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
