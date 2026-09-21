using System;
using UnityEngine;

namespace TwoBirds
{
    [DefaultExecutionOrder(210)]
    public sealed class PlayerHandPresentation : MonoBehaviour
    {
        private PlayerAvatarPresentation owner;
        private PlayerHeldItemPresentation held;
        private PlayerPresentation player;
        private Transform viewCamera;
        private Pose CameraPose => viewCamera ? new Pose(viewCamera.position, viewCamera.rotation) : player.AimPose;
        private PlayerMotor motor;
        private PlayerSeating seating;
        private PlayerCarry carry;
        private AvatarPresentation avatar;
        private LocalFirstPersonHands active;
        private AvatarRegistry.Entry pending;
        private AvatarHandContact leftContact, rightContact;
        private readonly Transform[] freeTargets = new Transform[2];
        private readonly float[] contactWeights = new float[2];
        private readonly float[] contactBlends = new float[2];
        private readonly float[] contactReaches = new float[2];
        private readonly AnimationClip[] contactFingers = new AnimationClip[2];
        private readonly Transform[] contactTargets = new Transform[2];
        private AvatarPresentationInput movement;
        private Pose contactFrame = new(Vector3.zero, Quaternion.identity);
        private float placementWeight;
        private readonly Vector3[] freePositions = new Vector3[2];
        private readonly Quaternion[] freeRotations = new Quaternion[2];
        private readonly bool[] freeInitialized = new bool[2];
        private readonly float[] freeWeights = new float[2];
        private ulong generation;
        private float phase, falling, descent, landing, landingAge;
        private bool running, seeded, placed;
        private int advancedFrame = -1;
        internal AvatarBinding LocalBinding => active ? active.Binding : null;

        internal void Initialize(PlayerAvatarPresentation owner)
        {
            this.owner = owner; avatar = owner.Presentation;
            held = GetComponent<PlayerHeldItemPresentation>(); player = GetComponent<PlayerPresentation>();
            motor = GetComponent<PlayerMotor>(); seating = GetComponent<PlayerSeating>(); carry = GetComponent<PlayerCarry>();
        }

        internal void StartPresentation()
        {
            if (running || !isActiveAndEnabled || !owner || !owner.IsClientInitialized) return;
            running = true;
            avatar.IdentityResolved += IdentityResolved;
            avatar.PreparingHands += PrepareRemote;
            avatar.HandsEvaluated += CommitRemote;
            seating.PresentationContextChanged += ContextChanged;
            carry.PresentationContextChanged += ContextChanged;
            motor.Simulated += Simulated;
            for (int i = 0; i < 2; i++)
            {
                freeTargets[i] = new GameObject(i == 0 ? "LeftFreePalm" : "RightFreePalm").transform;
                freeTargets[i].SetParent(transform, false);
                contactTargets[i] = new GameObject(i == 0 ? "LeftContactPalm" : "RightContactPalm").transform;
                contactTargets[i].SetParent(transform, false);
            }
            IdentityResolved(avatar.Resolved);
            ContextChanged();
        }

        private void IdentityResolved(AvatarRegistry.Entry entry) { pending = entry; CacheContacts(); }
        private void ContextChanged()
        {
            seeded = false; falling = descent = landing = 0f;
            bool driver = seating.IsDriver && !seating.TransitionPending && !seating.AwaitingReference && !seating.PlacementPending;
            var cart = driver ? seating.Cart : null;
            leftContact = rightContact = null;
            if (cart)
            {
                BindContact(cart.Presentation.LeftHandContact);
                BindContact(cart.Presentation.RightHandContact);
            }
            CacheContacts();
            movement = owner.CurrentPlacement;
        }

        private void BindContact(AvatarHandContact contact)
        {
            if (!contact) return;
            if (contact.Hand == AvatarIKGoal.LeftHand) leftContact = contact;
            else if (contact.Hand == AvatarIKGoal.RightHand) rightContact = contact;
        }

        private void CacheContacts()
        {
            for (int i = 0; i < 2; i++)
            {
                var contact = i == 0 ? leftContact : rightContact;
                if (!contact) continue;
                contactBlends[i] = contact.BlendTime; contactReaches[i] = contact.MaximumReach;
                contactFingers[i] = contact.Fingers ? contact.Fingers : avatar.Registry.Animations.GripFingers;
            }
        }

        private void Simulated(uint tick, Vector3 position)
        {
            var next = owner.CurrentPlacement;
            bool constrained = next.Seated || next.Carried && !next.ReleasePreview || next.Pending;
            if (constrained || next.ResetRevision != movement.ResetRevision || next.ControlRevision != movement.ControlRevision)
            { seeded = false; falling = descent = landing = 0f; }
            else
            {
                if (seeded && next.Grounded && !movement.Grounded)
                { landing = Mathf.Clamp01(descent / 10f); landingAge = 0f; }
                if (!next.Grounded) descent = Mathf.Max(descent, -next.WorldVelocity.y);
                else descent = 0f;
                seeded = true;
            }
            movement = next;
        }

        private void Contacts(float dt)
        {
            Set(leftContact, 0, AvatarIKGoal.LeftHand);
            Set(rightContact, 1, AvatarIKGoal.RightHand);
            void Set(AvatarHandContact contact, int index, AvatarIKGoal hand)
            {
                if (contact)
                {
                    contactTargets[index].SetPositionAndRotation(contact.transform.position, contact.transform.rotation);
                }
                contactWeights[index] = contactBlends[index] <= 0f ? contact ? 1f : 0f :
                    Mathf.MoveTowards(contactWeights[index], contact ? 1f : 0f, dt / contactBlends[index]);
                if (contactWeights[index] <= 0f && !contact)
                { avatar.HandTargets.Clear(hand, AvatarHandSource.Contact); return; }
                avatar.HandTargets.Set(hand, AvatarHandSource.Contact, contactTargets[index], contactWeights[index], contactWeights[index],
                    contactReaches[index], contactFingers[index]);
            }
        }

        private void PrepareRemote(AvatarBinding binding, float dt)
        {
            if (owner.IsOwner) return;
            Contacts(dt);
            var body = new HeldItemBodyFrame(binding.GetBone(HumanBodyBones.RightUpperArm).position,
                binding.Animator.transform.rotation, binding.Measurements, binding.Scale);
            if (avatar.Binding != null && binding != avatar.Binding) held.PrepareCandidate(binding, body);
            else held.PrepareHands(binding, body);
        }
        private void CommitRemote(AvatarBinding binding) { if (!owner.IsOwner) held.CommitHands(binding); }

        internal bool TryBody(out HeldItemBodyFrame body)
        {
            if (active) { body = active.BodyFrame; return true; }
            var settings = avatar.Resolved?.Settings;
            if (!settings) { body = default; return false; }
            var data = settings.FirstPersonGenerated.FormatVersion == AvatarSettings.CurrentFormatVersion ? settings.FirstPersonGenerated : settings.Generated;
            Pose aim = CameraPose;
            Vector3 shoulder = aim.position + aim.rotation * (avatar.Registry.FirstPerson.ShoulderOffset +
                settings.FirstPersonPlacementOffset + Vector3.right * ((data.RightShoulder.x - data.LeftShoulder.x) * settings.Scale * 0.5f));
            body = new HeldItemBodyFrame(shoulder, aim.rotation, data, settings.Scale);
            return true;
        }

        private void LateUpdate()
        {
            if (!running || !owner.IsOwner) return;
            if (!viewCamera && player.ViewCamera) viewCamera = player.ViewCamera.transform;
            float dt = advancedFrame == Time.frameCount ? 0f : Mathf.Min(Time.deltaTime, 0.05f);
            advancedFrame = Time.frameCount;
            Evaluate(dt);
        }
        internal void SampleImmediately() { if (running && owner.IsOwner) Evaluate(0f); }

        private void Evaluate(float dt)
        {
            if (pending != null)
            {
                var entry = pending; pending = null;
                if (entry.FirstPersonPrefab)
                {
                    LocalFirstPersonHands candidate = null;
                    try
                    {
                        AvatarContentValidation.Validate(entry.FirstPersonPrefab, entry.Settings, true);
                        candidate = Instantiate(entry.FirstPersonPrefab).GetComponent<LocalFirstPersonHands>();
                        candidate.Initialize(entry, avatar.Registry.Animations, avatar.HandTargets, ++generation);
                        Place(candidate, 0f);
                        FreeHands(candidate, 0f); Contacts(0f);
                        held.PrepareHands(candidate.Binding, candidate.BodyFrame);
                        candidate.Evaluate(0f, avatar.Registry.Animations);
                        var previous = active; active = candidate;
                        held.CommitHands(active.Binding);
                        if (previous) { previous.SetVisible(false); Destroy(previous.gameObject); }
                        active.SetVisible(true);
                    }
                    catch (Exception exception)
                    {
                        if (candidate && candidate != active) Destroy(candidate.gameObject);
                        Debug.LogError($"Local hands for {entry.Id}: {exception.Message}", this);
                    }
                }
            }
            Contacts(dt);
            if (active) { Place(active, dt); FreeHands(active, dt); }
            held.PrepareHands(active ? active.Binding : null, TryBody(out var body) ? body : null);
            if (active) active.Evaluate(dt, avatar.Registry.Animations);
            if (!held.CommitHands(active ? active.Binding : null) && held.CorrectCommittedPose())
            {
                if (active) active.Evaluate(0f, avatar.Registry.Animations);
                held.CommitHands(active ? active.Binding : null);
            }
        }

        private void Place(LocalFirstPersonHands rig, float dt)
        {
            var tuning = avatar.Registry.FirstPerson;
            bool contact = leftContact || rightContact;
            if (contact && seating.Cart) contactFrame = seating.Cart.GetSeat(0).VisualRider;
            placementWeight = placed ? Mathf.MoveTowards(placementWeight, contact ? 1f : 0f,
                dt / Mathf.Max(0.01f, tuning.PlacementBlendTime)) : contact ? 1f : 0f;
            var camera = CameraPose;
            var data = rig.Binding.Measurements;
            Vector3 bodyOffset = ((data.LeftShoulder + data.RightShoulder) * 0.5f - data.Hips) * rig.Binding.Scale +
                rig.Binding.Settings.SeatedPelvisOffset;
            Vector3 cameraPosition = camera.position + camera.rotation * tuning.ShoulderOffset;
            Pose frame = new(Vector3.Lerp(cameraPosition, contactFrame.position + contactFrame.rotation * bodyOffset, placementWeight),
                Quaternion.Slerp(camera.rotation, contactFrame.rotation, placementWeight));
            placed = true;
            rig.Place(frame, Vector3.zero);
        }

        private void FreeHands(LocalFirstPersonHands rig, float dt)
        {
            var tuning = avatar.Registry.FirstPerson;
            bool constrained = movement.Seated || movement.Carried && !movement.ReleasePreview || movement.Pending || !seeded;
            float speed = constrained ? 0f : new Vector2(movement.WorldVelocity.x, movement.WorldVelocity.z).magnitude;
            phase += dt * Mathf.Lerp(5f, 10f, Mathf.Clamp01(speed / Mathf.Max(0.01f, movement.SprintSpeed)));
            bool down = !constrained && !movement.Grounded && movement.WorldVelocity.y < 0f;
            falling = down ? falling + dt : 0f;
            float fall = down ? Mathf.Clamp01(0.12f + -movement.WorldVelocity.y / 14f + falling * 0.15f) : 0f;
            float rise = !constrained && !movement.Grounded && !down ? 1f : 0f;
            landingAge += dt;
            float dip = landingAge < tuning.LandingDuration ? Mathf.Sin(Mathf.PI * landingAge / tuning.LandingDuration) * landing : 0f;
            for (int i = 0; i < 2; i++)
            {
                bool right = i == 1;
                var hand = right ? tuning.Right : tuning.Left;
                var data = rig.Binding.Measurements;
                float length = (right ? data.RightArm.x + data.RightArm.y : data.LeftArm.x + data.LeftArm.y) * rig.Binding.Scale;
                Vector3 local = Vector3.Lerp(Vector3.Lerp(hand.RestPosition, hand.RisePosition, rise), hand.FallPosition, fall);
                float bounce = Mathf.Sin(phase + i * Mathf.PI) * tuning.Bounce * Mathf.Clamp01(speed / Mathf.Max(0.01f, movement.WalkSpeed));
                local += new Vector3(Mathf.Sin(phase * 1.3f + i) * fall * tuning.FallStrength, bounce - dip * tuning.LandingStrength,
                    Mathf.Cos(phase * 1.1f + i) * fall * tuning.FallStrength);
                local += rig.Binding.Settings.FirstPersonReachOffset;
                var shoulder = rig.Binding.GetBone(right ? HumanBodyBones.RightUpperArm : HumanBodyBones.LeftUpperArm);
                Vector3 euler = Vector3.Lerp(Vector3.Lerp(hand.RestEuler, hand.RiseEuler, rise), hand.FallEuler, fall);
                var goal = right ? AvatarIKGoal.RightHand : AvatarIKGoal.LeftHand;
                var occupied = avatar.HandTargets.Resolve(goal);
                bool owned = occupied.Transform && occupied.Transform != freeTargets[i];
                freeWeights[i] = !freeInitialized[i] ? 1f : owned ? 0f : Mathf.MoveTowards(freeWeights[i], 1f, dt / tuning.BlendTime);
                float blend = freeInitialized[i] ? AvatarPresentation.Smooth(dt, tuning.BlendTime) : 1f;
                freePositions[i] = Vector3.Lerp(freePositions[i], Vector3.ClampMagnitude(local, 0.85f), blend);
                freeRotations[i] = Quaternion.Slerp(freeRotations[i], Quaternion.Euler(euler), blend);
                freeInitialized[i] = true;
                freeTargets[i].SetPositionAndRotation(shoulder.position + rig.transform.rotation * (freePositions[i] * length),
                    rig.transform.rotation * freeRotations[i]);
                avatar.HandTargets.Set(goal, AvatarHandSource.Free, freeTargets[i], freeWeights[i], freeWeights[i],
                    fingers: avatar.Registry.Animations.RelaxedFingers, openWeight: fall);
            }
        }

        internal void StopPresentation()
        {
            if (!running) return;
            running = false;
            avatar.IdentityResolved -= IdentityResolved; avatar.PreparingHands -= PrepareRemote; avatar.HandsEvaluated -= CommitRemote;
            seating.PresentationContextChanged -= ContextChanged; carry.PresentationContextChanged -= ContextChanged; motor.Simulated -= Simulated;
            foreach (var hand in new[] { AvatarIKGoal.LeftHand, AvatarIKGoal.RightHand })
            { avatar.HandTargets.Clear(hand, AvatarHandSource.Contact); avatar.HandTargets.Clear(hand, AvatarHandSource.Free); }
            foreach (var target in freeTargets) if (target) Destroy(target.gameObject);
            foreach (var target in contactTargets) if (target) Destroy(target.gameObject);
            Array.Clear(contactWeights, 0, contactWeights.Length);
            viewCamera = null;
            Array.Clear(freeInitialized, 0, freeInitialized.Length);
            if (active) Destroy(active.gameObject);
            active = null; pending = null; seeded = placed = false;
        }
        private void OnEnable() => StartPresentation();
        private void OnDisable() => StopPresentation();
        private void OnDestroy() => StopPresentation();
    }
}
