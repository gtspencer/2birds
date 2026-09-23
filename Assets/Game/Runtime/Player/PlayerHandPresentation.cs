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
        internal Pose CameraPose => viewCamera ? new Pose(viewCamera.position, viewCamera.rotation) : player.AimPose;
        private PlayerMotor motor;
        private PlayerSeating seating;
        private PlayerCarry carry;
        private PlayerInventory inventory;
        private WorldItemRegistry items;
        private PlayerCarry gripPartner;
        private AvatarPresentation gripSource;
        private AvatarBinding gripBinding;
        private readonly TwoHandHoldPresentation carryHands = new();
        private readonly Transform[] carryTargets = new Transform[2];
        private HeldItemBodyFrame carryBody;
        private Pose carryLeft, carryRight;
        private bool outstretched, carryDisplayed, releaseCaptured;
        private uint releaseRevision, releaseRequest;
        private int releasedPartner = -1;
        private double carryReleaseStart;
        private HeldItemPoseSettings CarrySettings => items.HeldDefaults.HeavyHoldSettings;
        private bool CarryHolding => carry.IsCarrying && gripPartner && (outstretched || gripBinding != null && gripBinding.HasCarryGrips);
        private float HeavyFrameWeight => carryHands.Releasing ? carryHands.Frame(CarrySettings,
            Time.unscaledTimeAsDouble - carryReleaseStart, 0f) : CarryHolding ? 1f : held.HeavyFrameWeight;
        private HeldItemBodyFrame HeavyBody(AvatarSettings settings) => TwoHandHoldPresentation.Body(owner, settings);
        private AvatarPresentation avatar;
        private LocalFirstPersonHands active;
        private AvatarCosmeticPresentation cosmetics;
        internal void AppearanceChanged() => cosmetics?.Apply(owner.Appearance);
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
            inventory = GetComponent<PlayerInventory>();
            motor = GetComponent<PlayerMotor>(); seating = GetComponent<PlayerSeating>(); carry = GetComponent<PlayerCarry>();
        }

        internal void StartPresentation()
        {
            if (running || !isActiveAndEnabled || !owner || !owner.IsClientInitialized) return;
            items = WorldItemRegistry.Instance;
            running = true;
            LocalCameraChanged(player.ViewCamera);
            PlayerPresentation.LocalCameraChanged += LocalCameraChanged;
            avatar.IdentityResolved += IdentityResolved;
            avatar.PreparingHands += PrepareRemote;
            avatar.HandsEvaluated += CommitRemote;
            seating.PresentationContextChanged += ContextChanged;
            carry.PresentationContextChanged += ContextChanged;
            carry.PreparingRelease += CaptureCarryRelease;
            carry.ReleaseStarted += StartCarryRelease;
            carry.ReleaseRejected += RejectCarryRelease;
            inventory.InventoryChanged += CarrySelectionChanged;
            items.PresentationChanged += CarryItemChanged;
            motor.Simulated += Simulated;
            for (int i = 0; i < 2; i++)
            {
                carryTargets[i] = new GameObject(i == 0 ? "LeftCarryPalm" : "RightCarryPalm").transform;
                carryTargets[i].SetParent(transform, false);
                freeTargets[i] = new GameObject(i == 0 ? "LeftFreePalm" : "RightFreePalm").transform;
                freeTargets[i].SetParent(transform, false);
                contactTargets[i] = new GameObject(i == 0 ? "LeftContactPalm" : "RightContactPalm").transform;
                contactTargets[i].SetParent(transform, false);
            }
            IdentityResolved(avatar.Resolved);
            ContextChanged();
        }

        private void IdentityResolved(AvatarRegistry.Entry entry) { pending = entry; CacheContacts(); }
        private void LocalCameraChanged(Camera camera) => viewCamera = owner.IsOwner && camera ? camera.transform : null;
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
            RefreshCarryContext();
        }

        private void RefreshCarryContext()
        {
            if (releaseCaptured) return;
            if (seating.Seated || seating.TransitionPending || seating.AwaitingReference || seating.PlacementPending || carry.IsCarried)
            { ClearCarry(); return; }
            if (carry.IsCarrying && carry.Partner)
            {
                if (carryHands.Releasing && gripPartner == carry.Partner && motor.ControlRevision + 1 == releaseRevision) return;
                if (carryHands.Releasing || gripPartner != carry.Partner || outstretched != carry.Partner.IsOwner)
                {
                    ClearCarry();
                    BindCarryPartner(carry.Partner);
                }
            }
            else if (!carryHands.Releasing) ClearCarry();
            else if (motor.ControlRevision != releaseRevision) ClearCarry();
        }

        private void BindCarryPartner(PlayerCarry partner)
        {
            DetachGripSource();
            gripPartner = partner;
            outstretched = partner && partner.IsOwner;
            if (!partner || outstretched) return;
            gripSource = partner.GetComponent<PlayerAvatarPresentation>().Presentation;
            gripSource.WillUnbind += GripWillUnbind;
            gripSource.DidBind += GripDidBind;
            GripDidBind(gripSource.Binding);
        }

        private void GripWillUnbind(AvatarBinding binding)
        {
            gripBinding = null;
            avatar.HandDependency = null;
            if (!carryHands.Releasing)
            {
                carryDisplayed = false;
                carryHands.Reset();
                TwoHandHoldPresentation.Clear(avatar.HandTargets, AvatarHandSource.Carry);
            }
        }

        private void GripDidBind(AvatarBinding binding)
        {
            if (carryHands.Releasing) return;
            gripBinding = binding;
            avatar.HandDependency = binding != null && binding.HasCarryGrips ? gripSource : null;
        }

        private void DetachGripSource()
        {
            if (gripSource)
            {
                gripSource.WillUnbind -= GripWillUnbind;
                gripSource.DidBind -= GripDidBind;
            }
            gripSource = null; gripBinding = null;
            avatar.HandDependency = null;
        }

        private void ClearCarry()
        {
            carryHands.Reset();
            DetachGripSource();
            gripPartner = null;
            carryDisplayed = outstretched = releaseCaptured = false;
            releaseRequest = releaseRevision = 0; releasedPartner = -1;
            TwoHandHoldPresentation.Clear(avatar.HandTargets, AvatarHandSource.Carry);
        }

        private void CarryItemChanged(uint id, int previousHolder, int holder)
        {
            if (previousHolder == inventory.ObjectId || holder == inventory.ObjectId) CarrySelectionChanged();
        }

        internal void ItemSelected()
        {
            if (carryHands.Releasing && !releaseCaptured && !carry.IsCarrying) ClearCarry();
        }

        private void CarrySelectionChanged()
        {
            if (!carryHands.Releasing || releaseCaptured || carry.IsCarrying) return;
            bool selected = owner.IsOwner ? inventory.SelectedSlot >= 0 : items.EquippedPresentation(inventory.ObjectId);
            if (selected) ClearCarry();
        }

        private void CaptureCarryRelease(CarryPresentationRelease release)
        {
            if (release.Release.Intent != ItemReleaseIntent.Throw) { ClearCarry(); return; }
            if (carryHands.Releasing && release.Revision == releaseRevision && release.Partner && release.Partner.ObjectId == releasedPartner)
            { releaseCaptured = true; return; }
            if (!carryDisplayed || !release.Partner || gripPartner != release.Partner) return;
            releaseCaptured = true;
            releaseRevision = release.Revision; releaseRequest = release.Request; releasedPartner = release.Partner.ObjectId;
            carryReleaseStart = Time.unscaledTimeAsDouble;
            carryHands.BeginRelease(carryLeft, carryRight, carryBody);
        }

        private void StartCarryRelease(CarryPresentationRelease release)
        {
            releaseCaptured = false;
            if (release.Release.Intent != ItemReleaseIntent.Throw) { ClearCarry(); return; }
            if (carryHands.Releasing && outstretched) DetachGripSource();
        }

        private void RejectCarryRelease(uint request)
        {
            if (carryHands.Releasing && releaseRequest != request) return;
            ClearCarry();
            RefreshCarryContext();
        }

        private void PrepareCarry(AvatarBinding binding, in HeldItemBodyFrame body)
        {
            if (!carryHands.Releasing && !CarryHolding)
            {
                carryDisplayed = false;
                TwoHandHoldPresentation.Clear(avatar.HandTargets, AvatarHandSource.Carry);
                return;
            }
            bool staged = binding != null && (owner.IsOwner ? active && binding != active.Binding :
                avatar.Binding != null && binding != avatar.Binding);
            if (staged && carryHands.Releasing)
            {
                TwoHandHoldPresentation.Submit(avatar.HandTargets, AvatarHandSource.Carry, carryTargets[0], carryTargets[1],
                    HeavyItemPoseCalculation.ToWorld(HeavyItemPoseCalculation.ToLocal(carryHands.Left, carryBody), body),
                    HeavyItemPoseCalculation.ToWorld(HeavyItemPoseCalculation.ToLocal(carryHands.Right, carryBody), body),
                    carryHands.LeftWeight, carryHands.RightWeight, TwoHandHoldPresentation.Reach(CarrySettings), avatar.Registry.Animations.OpenFingers);
                return;
            }
            Pose left = default, right = default;
            bool available = gripBinding != null && gripBinding.HasCarryGrips && gripPartner &&
                (!carryHands.Releasing || gripPartner.Role == CarryRole.Free ||
                    gripPartner.IsCarried && gripPartner.Partner == carry && motor.ControlRevision + 1 == releaseRevision);
            if (available)
            {
                left = new Pose(gripBinding.LeftCarryGrip.position, gripBinding.LeftCarryGrip.rotation);
                right = new Pose(gripBinding.RightCarryGrip.position, gripBinding.RightCarryGrip.rotation);
            }
            else if (outstretched)
            {
                left = OutstretchedPalm(body, false); right = OutstretchedPalm(body, true);
            }
            if (carryHands.Releasing)
            {
                Pose destinationLeft = owner.IsOwner && freeTargets[0] ? new Pose(freeTargets[0].position, freeTargets[0].rotation) : carryHands.Left;
                Pose destinationRight = owner.IsOwner && freeTargets[1] ? new Pose(freeTargets[1].position, freeTargets[1].rotation) : carryHands.Right;
                carryHands.Sample(left, right, available && !outstretched, !available || outstretched, body,
                    CarrySettings, items.EnvironmentMask, Time.unscaledTimeAsDouble - carryReleaseStart, destinationLeft, destinationRight);
                if (!carryHands.Following && (gripSource || gripBinding != null)) DetachGripSource();
                if (carryHands.Stage == TwoHandHoldPresentation.RecoveryStage.Finished) { ClearCarry(); return; }
            }
            else carryHands.Hold(left, right);
            TwoHandHoldPresentation.Submit(avatar.HandTargets, AvatarHandSource.Carry, carryTargets[0], carryTargets[1],
                carryHands.Left, carryHands.Right, carryHands.LeftWeight, carryHands.RightWeight,
                TwoHandHoldPresentation.Reach(CarrySettings), carryHands.Releasing ? avatar.Registry.Animations.OpenFingers : avatar.Registry.Animations.GripFingers);
            if (binding == null || binding == avatar.Binding || owner.IsOwner && (!active || binding == active.Binding))
            {
                carryBody = body; carryLeft = carryHands.Left; carryRight = carryHands.Right;
                carryDisplayed = true;
            }
        }

        private Pose OutstretchedPalm(in HeldItemBodyFrame body, bool right)
        {
            var data = body.Measurements;
            Quaternion palm = body.Rotation * Quaternion.Euler(0f, 0f, right ? 90f : -90f);
            Quaternion wrist = palm * Quaternion.Inverse(right ? data.RightWristToPalmRotation : data.LeftWristToPalmRotation);
            Vector3 position = (right ? body.Shoulder : body.LeftShoulder) + body.Rotation * Vector3.forward *
                ((right ? body.ArmLength : body.LeftArmLength) * TwoHandHoldPresentation.Reach(CarrySettings));
            return new Pose(position + wrist * ((right ? data.RightWristToPalmPosition : data.LeftWristToPalmPosition) * body.Scale), palm);
        }

        private void CommitCarry(AvatarBinding binding)
        {
            if (!carryDisplayed || binding == null) return;
            carryLeft = binding.Palm(false); carryRight = binding.Palm(true);
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

        private void Contacts(float dt, AvatarSettings settings)
        {
            Vector3 offset = (leftContact || rightContact) && seating.Cart
                ? seating.Cart.GetSeat(0).VisualRider.rotation * AvatarDriverPose.HandOffset(settings, avatar.Registry) : Vector3.zero;
            Set(leftContact, 0, AvatarIKGoal.LeftHand);
            Set(rightContact, 1, AvatarIKGoal.RightHand);
            void Set(AvatarHandContact contact, int index, AvatarIKGoal hand)
            {
                if (contact)
                {
                    contactTargets[index].SetPositionAndRotation(contact.transform.position + offset, contact.transform.rotation);
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
            Contacts(dt, binding.Settings);
            var body = new HeldItemBodyFrame(binding.GetBone(HumanBodyBones.RightUpperArm).position,
                binding.Animator.transform.rotation, binding.Measurements, binding.Scale,
                leftShoulder: binding.GetBone(HumanBodyBones.LeftUpperArm).position);
            if (HeavyFrameWeight > 0f) body = body.WithReference(HeavyBody(binding.Settings));
            if (avatar.Binding != null && binding != avatar.Binding) held.PrepareCandidate(binding, body);
            else held.PrepareHands(binding, body);
            PrepareCarry(binding, body);
        }
        private void CommitRemote(AvatarBinding binding)
        {
            if (owner.IsOwner) return;
            held.CommitHands(binding);
            CommitCarry(binding);
        }

        internal bool TryBody(out HeldItemBodyFrame body)
        {
            if (active) { body = BodyFor(active); return true; }
            var settings = avatar.Resolved?.Settings;
            if (!settings) { body = default; return false; }
            var data = settings.FirstPersonGenerated.FormatVersion == AvatarSettings.CurrentFormatVersion ? settings.FirstPersonGenerated : settings.Generated;
            Pose aim = CameraPose;
            Vector3 shoulder = aim.position + aim.rotation * (avatar.Registry.FirstPerson.ShoulderOffset +
                settings.FirstPersonPlacementOffset + Vector3.right * ((data.RightShoulder.x - data.LeftShoulder.x) * settings.Scale * 0.5f));
            body = new HeldItemBodyFrame(shoulder, aim.rotation, data, settings.Scale);
            float heavy = HeavyFrameWeight;
            if (heavy > 0f)
            {
                var frame = HeavyBody(settings).WithMeasurements(data, settings.Scale);
                body = new HeldItemBodyFrame(Vector3.Lerp(body.Shoulder, frame.Shoulder, heavy),
                    Quaternion.Slerp(body.Rotation, frame.Rotation, heavy), data, settings.Scale);
            }
            return true;
        }

        private HeldItemBodyFrame BodyFor(LocalFirstPersonHands rig) => HeavyFrameWeight >= 1f
            ? rig.BodyFrame.WithReference(HeavyBody(rig.Binding.Settings)) : rig.BodyFrame;

        private void LateUpdate()
        {
            if (!running || !owner.IsOwner) return;
            float dt = advancedFrame == Time.frameCount ? 0f : Mathf.Min(Time.deltaTime, 0.05f);
            advancedFrame = Time.frameCount;
            Evaluate(dt);
        }
        internal void SampleImmediately() { if (running && owner.IsOwner) Evaluate(0f); }

        internal void CommitCorrection()
        {
            if (active) active.Evaluate(0f, avatar.Registry.Animations);
            held.CommitHands(active ? active.Binding : null);
        }

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
                        FreeHands(candidate, 0f); Contacts(0f, candidate.Binding.Settings);
                        held.PrepareHands(candidate.Binding, BodyFor(candidate));
                        PrepareCarry(candidate.Binding, BodyFor(candidate));
                        candidate.Evaluate(0f, avatar.Registry.Animations);
                        var nextCosmetics = new AvatarCosmeticPresentation(candidate.Binding,
                            SessionController.Instance.Hats, SessionController.Instance.Tattoos, true);
                        nextCosmetics.Apply(owner.Appearance);
                        var previous = active; active = candidate;
                        cosmetics?.Dispose(); cosmetics = nextCosmetics;
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
            Contacts(dt, active ? active.Binding.Settings : avatar.Resolved?.Settings);
            if (active) { Place(active, dt); FreeHands(active, dt); }
            held.PrepareHands(active ? active.Binding : null, TryBody(out var body) ? body : null);
            if (TryBody(out var carryFrame)) PrepareCarry(active ? active.Binding : null, carryFrame);
            if (active) active.Evaluate(dt, avatar.Registry.Animations);
            if (!held.CommitHands(active ? active.Binding : null) && held.CorrectCommittedPose())
            {
                if (active) active.Evaluate(0f, avatar.Registry.Animations);
                held.CommitHands(active ? active.Binding : null);
            }
            CommitCarry(active ? active.Binding : null);
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
                AvatarDriverPose.SeatedOffset(rig.Binding.Settings, avatar.Registry, true);
            Vector3 cameraPosition = camera.position + camera.rotation * tuning.ShoulderOffset;
            Pose frame = new(Vector3.Lerp(cameraPosition, contactFrame.position + contactFrame.rotation * bodyOffset, placementWeight),
                Quaternion.Slerp(camera.rotation, contactFrame.rotation, placementWeight));
            float heavy = HeavyFrameWeight;
            if (heavy > 0f)
            {
                var body = HeavyBody(rig.Binding.Settings);
                frame.position = Vector3.Lerp(frame.position, body.Center, heavy);
                frame.rotation = Quaternion.Slerp(frame.rotation, body.Rotation, heavy);
            }
            placed = true;
            rig.Place(frame, Vector3.zero, heavy);
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
            PlayerPresentation.LocalCameraChanged -= LocalCameraChanged;
            cosmetics?.Dispose(); cosmetics = null;
            avatar.IdentityResolved -= IdentityResolved; avatar.PreparingHands -= PrepareRemote; avatar.HandsEvaluated -= CommitRemote;
            seating.PresentationContextChanged -= ContextChanged; carry.PresentationContextChanged -= ContextChanged; motor.Simulated -= Simulated;
            carry.PreparingRelease -= CaptureCarryRelease;
            carry.ReleaseStarted -= StartCarryRelease;
            carry.ReleaseRejected -= RejectCarryRelease;
            inventory.InventoryChanged -= CarrySelectionChanged;
            if (items) items.PresentationChanged -= CarryItemChanged;
            ClearCarry();
            foreach (var target in carryTargets) if (target) Destroy(target.gameObject);
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
