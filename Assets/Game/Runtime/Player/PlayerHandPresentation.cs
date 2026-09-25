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
        private readonly HandHoldPresentation carryHands = new();
        private readonly Transform[] carryTargets = new Transform[2];
        private HeldItemBodyFrame carryBody;
        private Pose carryLeft, carryRight;
        private bool outstretched, carryDisplayed, releaseCaptured;
        private uint releaseRevision, releaseRequest;
        private int releasedPartner = -1;
        private double carryReleaseStart;
        private HoldSlot CarrySettings => items.CarryHold;
        private bool CarryHolding => carry.IsCarrying && gripPartner && (outstretched || gripBinding != null && gripBinding.HasCarryGrips);
        private float CarryFrameWeight => carryHands.Releasing ? carryHands.Frame(CarrySettings,
            Time.unscaledTimeAsDouble - carryReleaseStart, 0f) : CarryHolding ? 1f : 0f;
        private HeldItemBodyFrame CarryBodyFrame(AvatarSettings settings)
        {
            bool remote = !owner.IsOwner && avatar.Binding != null;
            var input = remote ? avatar.Input : owner.CurrentPlacement;
            bool attached = input.Seated || input.Carried && !input.ReleasePreview;
            Quaternion torso = remote ? avatar.transform.rotation : attached ? input.Facing.rotation :
                Quaternion.Euler(0f, input.Facing.rotation.eulerAngles.y, 0f);
            float seated = remote ? avatar.State.Weights[(int)AvatarPose.Seated] : input.Seated ? 1f : 0f;
            return new HeldItemBodyFrame(settings, avatar.Registry, input, torso, seated, true);
        }
        private AvatarPresentation avatar;
        private LocalFirstPersonHands active;
        private AvatarCosmeticPresentation cosmetics;
        internal void AppearanceChanged() => cosmetics?.Apply(owner.Appearance);
        private PlayerEmote emote;
        private bool hidden;
        private AvatarRegistry.Entry pending;
        private AvatarHandContact leftContact, rightContact;
        private readonly Transform[] freeTargets = new Transform[2];
        private HandContactPresentation contacts;
        private AvatarPresentationInput movement;
        private Pose contactFrame = new(Vector3.zero, Quaternion.identity);
        private float placementWeight;
        private readonly Vector3[] freePositions = new Vector3[2];
        private readonly Quaternion[] freeRotations = new Quaternion[2];
        private readonly bool[] freeInitialized = new bool[2];
        private readonly float[] freeWeights = new float[2];
        private Vector3 appliedBob;
        private ulong generation;
        private float phase, falling, descent, landing, landingAge, freeFall;
        private bool running, seeded, placed;
        private int advancedFrame = -1;
        internal event Action<AvatarBinding> LocalBound;
        internal AvatarBinding LocalBinding => active ? active.Binding : null;
        internal AvatarHandContact LeftContact => leftContact;
        internal AvatarHandContact RightContact => rightContact;

        public void BeginInteraction(AvatarHandContact left, AvatarHandContact right) { leftContact = left; rightContact = right; }
        public void EndInteraction() { leftContact = rightContact = null; }

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
            avatar.Registry.FirstPerson.ContentChanged += FirstPersonChanged;
            avatar.PreparingHands += PrepareRemote;
            avatar.HandsEvaluated += CommitRemote;
            avatar.PosingHands += PoseRemote;
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
            }
            contacts = new HandContactPresentation(transform);
            IdentityResolved(avatar.Resolved);
            ContextChanged();
            emote = owner.Emote;
            emote.Changed += EmoteChanged;
            EmoteChanged();
        }

        private void EmoteChanged()
        {
            bool value = owner.IsOwner && emote.Presenting;
            if (hidden == value) return;
            hidden = value;
            if (active) active.SetVisible(!hidden);
            cosmetics?.SetVisible(!hidden);
        }

        private void IdentityResolved(AvatarRegistry.Entry entry)
        {
            pending = entry;
            if (active && entry != null && active.Binding.Id == entry.Id) active.RefreshMeasurements();
            held.State?.InvalidateBinding();
        }
        private void FirstPersonChanged() { pending = avatar.Resolved; seeded = false; held.State?.RefreshContent(); }
        private void LocalCameraChanged(Camera camera) => viewCamera = owner.IsOwner && camera ? camera.transform : null;
        private void ContextChanged()
        {
            seeded = false; falling = descent = landing = 0f;
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
                HandHoldPresentation.Clear(avatar.HandTargets, AvatarHandSource.Carry);
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
            HandHoldPresentation.Clear(avatar.HandTargets, AvatarHandSource.Carry);
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
                HandHoldPresentation.Clear(avatar.HandTargets, AvatarHandSource.Carry);
                return;
            }
            bool staged = binding != null && (owner.IsOwner ? active && binding != active.Binding :
                avatar.Binding != null && binding != avatar.Binding);
            if (staged && carryHands.Releasing)
            {
                HandHoldPresentation.Submit(avatar.HandTargets, AvatarHandSource.Carry, carryTargets[0], carryTargets[1],
                    body.CenterToWorld(carryBody.CenterToLocal(carryHands.Left)),
                    body.CenterToWorld(carryBody.CenterToLocal(carryHands.Right)),
                    carryHands.LeftWeight, carryHands.RightWeight, HandHoldPresentation.Reach(CarrySettings), avatar.Registry.Animations.OpenFingers,
                    bodyRelative: false);
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
                if (carryHands.Stage == HandHoldPresentation.RecoveryStage.Finished) { ClearCarry(); return; }
            }
            else carryHands.Hold(left, right);
            HandHoldPresentation.Submit(avatar.HandTargets, AvatarHandSource.Carry, carryTargets[0], carryTargets[1],
                carryHands.Left, carryHands.Right, carryHands.LeftWeight, carryHands.RightWeight,
                HandHoldPresentation.Reach(CarrySettings), carryHands.Releasing ? avatar.Registry.Animations.OpenFingers : avatar.Registry.Animations.GripFingers,
                bodyRelative: false);
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
                ((right ? body.ArmLength : body.LeftArmLength) * HandHoldPresentation.Reach(CarrySettings));
            return new Pose(position + wrist * ((right ? data.RightWristToPalmPosition : data.LeftWristToPalmPosition) * body.Scale), palm);
        }

        private void CommitCarry(AvatarBinding binding)
        {
            if (!carryDisplayed || binding == null) return;
            carryLeft = binding.Palm(false); carryRight = binding.Palm(true);
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

        private void Contacts(float dt, AvatarBinding binding) =>
            contacts.Submit(avatar.HandTargets, leftContact, rightContact, binding?.Id ?? avatar.Resolved?.Id ?? default,
                owner.IsOwner, avatar.Registry.Animations.GripFingers, dt);

        private void PrepareRemote(AvatarBinding binding, float dt)
        {
            if (owner.IsOwner) return;
            Contacts(dt, binding);
            var body = new HeldItemBodyFrame(binding.GetBone(HumanBodyBones.RightUpperArm).position,
                binding.Animator.transform.rotation, binding.Measurements, binding.Scale,
                leftShoulder: binding.GetBone(HumanBodyBones.LeftUpperArm).position);
            avatar.HandTargets.SetBody(binding.Body);
            if (avatar.Binding == null || binding == avatar.Binding) held.PrepareHands(binding, body);
            PrepareCarry(binding, body);
        }
        private void PoseRemote(AvatarBinding binding)
        {
            if (owner.IsOwner) return;
            if (avatar.Binding == null || binding == avatar.Binding) held.PoseHands(binding);
        }
        private void CommitRemote(AvatarBinding binding)
        {
            if (owner.IsOwner) return;
            held.CommitHands(binding);
            CommitCarry(binding);
        }

        internal bool TryBody(out HeldItemBodyFrame body)
        {
            if (active) { body = active.BodyFrame; return true; }
            var settings = avatar.Resolved?.Settings;
            if (!settings) { body = default; return false; }
            var data = AvatarPalmCalibration.Measurements(settings, settings.FirstPersonGenerated.FormatVersion == AvatarSettings.CurrentFormatVersion);
            Pose aim = CameraPose;
            Vector3 shoulder = aim.position + aim.rotation * (avatar.Registry.FirstPerson.ShoulderOffset +
                settings.FirstPersonPlacementOffset + Vector3.right * ((data.RightShoulder.x - data.LeftShoulder.x) * settings.Scale * 0.5f));
            body = new HeldItemBodyFrame(shoulder, aim.rotation, data, settings.Scale);
            float carried = CarryFrameWeight;
            if (carried > 0f)
            {
                var frame = CarryBodyFrame(settings).WithMeasurements(data, settings.Scale);
                body = new HeldItemBodyFrame(Vector3.Lerp(body.Shoulder, frame.Shoulder, carried),
                    Quaternion.Slerp(body.Rotation, frame.Rotation, carried), data, settings.Scale);
            }
            return true;
        }

        private Pose FirstPersonFrame()
        {
            if (active) return new Pose(active.transform.position, active.transform.rotation);
            var camera = CameraPose;
            var settings = avatar.Resolved?.Settings;
            if (!settings) return camera;
            var data = settings.FirstPersonGenerated;
            Vector3 offset = avatar.Registry.FirstPerson.ShoulderOffset + settings.FirstPersonPlacementOffset -
                (data.LeftShoulder + data.RightShoulder) * (0.5f * settings.Scale);
            return new Pose(camera.position + camera.rotation * offset, camera.rotation);
        }

        private void LateUpdate()
        {
            if (!running || !owner.IsOwner) return;
            float dt = advancedFrame == Time.frameCount ? 0f : Mathf.Min(Time.deltaTime, 0.05f);
            advancedFrame = Time.frameCount;
            Evaluate(dt);
        }
        internal void SampleImmediately() { if (running && owner.IsOwner) Evaluate(0f); }

        internal void TranslateRig(Vector3 offset) { if (active && offset != Vector3.zero) active.transform.position += offset; }

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
                        AdvanceFreeHands(candidate, 0f); Place(candidate, 0f);
                        avatar.HandTargets.SetBody(candidate.Binding.Body);
                        SubmitFreeHands(candidate); Contacts(0f, candidate.Binding);
                        held.PrepareHands(candidate.Binding, candidate.BodyFrame);
                        held.PoseHands(candidate.Binding, new Pose(candidate.transform.position, candidate.transform.rotation));
                        PrepareCarry(candidate.Binding, candidate.BodyFrame);
                        candidate.Evaluate(0f, avatar.Registry.Animations);
                        var nextCosmetics = new AvatarCosmeticPresentation(candidate.Binding,
                            SessionController.Instance.Hats, SessionController.Instance.Tattoos, true);
                        nextCosmetics.Apply(owner.Appearance);
                        nextCosmetics.SetVisible(!hidden);
                        var previous = active; active = candidate;
                        cosmetics?.Dispose(); cosmetics = nextCosmetics;
                        TranslateRig(held.CommitHands(active.Binding));
                        if (previous) { previous.SetVisible(false); Destroy(previous.gameObject); }
                        active.SetVisible(!hidden);
                        LocalBound?.Invoke(active.Binding);
                    }
                    catch (Exception exception)
                    {
                        if (candidate && candidate != active) Destroy(candidate.gameObject);
                        Debug.LogError($"Local hands for {entry.Id}: {exception.Message}", this);
                    }
                }
            }
            Contacts(dt, active ? active.Binding : null);
            if (active) { AdvanceFreeHands(active, dt); Place(active, dt); SubmitFreeHands(active); avatar.HandTargets.SetBody(active.Binding.Body); }
            held.PrepareHands(active ? active.Binding : null, TryBody(out var body) ? body : null);
            held.PoseHands(active ? active.Binding : null, FirstPersonFrame());
            if (TryBody(out var carryFrame)) PrepareCarry(active ? active.Binding : null, carryFrame);
            if (active) active.Evaluate(dt, avatar.Registry.Animations);
            TranslateRig(held.CommitHands(active ? active.Binding : null));
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
            float carried = CarryFrameWeight;
            if (carried > 0f)
            {
                var body = CarryBodyFrame(rig.Binding.Settings);
                frame.position = Vector3.Lerp(frame.position, body.Center, carried);
                frame.rotation = Quaternion.Slerp(frame.rotation, body.Rotation, carried);
            }
            placed = true;
            appliedBob = HoldBob(rig);
            rig.Place(frame, appliedBob, carried);
        }

        private Vector3 HoldBob(LocalFirstPersonHands rig)
        {
            float weight = held.HoldWeight;
            if (weight <= 0f || !freeInitialized[1]) return Vector3.zero;
            var data = rig.Binding.Measurements;
            Vector3 rest = Vector3.ClampMagnitude(avatar.Registry.FirstPerson.Right.RestPosition + rig.Binding.Settings.FirstPersonReachOffset, 0.85f);
            return (freePositions[1] - rest) * ((data.RightArm.x + data.RightArm.y) * rig.Binding.Scale * weight);
        }

        private void AdvanceFreeHands(LocalFirstPersonHands rig, float dt)
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
            freeFall = fall;
            for (int i = 0; i < 2; i++)
            {
                bool right = i == 1;
                var hand = right ? tuning.Right : tuning.Left;
                Vector3 local = Vector3.Lerp(Vector3.Lerp(hand.RestPosition, hand.RisePosition, rise), hand.FallPosition, fall);
                float bounce = Mathf.Sin(phase + i * Mathf.PI) * tuning.Bounce * Mathf.Clamp01(speed / Mathf.Max(0.01f, movement.WalkSpeed));
                local += new Vector3(Mathf.Sin(phase * 1.3f + i) * fall * tuning.FallStrength, bounce - dip * tuning.LandingStrength,
                    Mathf.Cos(phase * 1.1f + i) * fall * tuning.FallStrength);
                local += rig.Binding.Settings.FirstPersonReachOffset;
                Vector3 euler = Vector3.Lerp(Vector3.Lerp(hand.RestEuler, hand.RiseEuler, rise), hand.FallEuler, fall);
                var occupied = avatar.HandTargets.Resolve(right ? AvatarIKGoal.RightHand : AvatarIKGoal.LeftHand);
                bool owned = occupied.Transform && occupied.Transform != freeTargets[i];
                freeWeights[i] = !freeInitialized[i] ? 1f : occupied.Source == AvatarHandSource.Item ? 1f :
                    owned ? 0f : Mathf.MoveTowards(freeWeights[i], 1f, dt / tuning.BlendTime);
                float blend = freeInitialized[i] ? AvatarPresentation.Smooth(dt, tuning.BlendTime) : 1f;
                freePositions[i] = Vector3.Lerp(freePositions[i], Vector3.ClampMagnitude(local, 0.85f), blend);
                freeRotations[i] = Quaternion.Slerp(freeRotations[i], Quaternion.Euler(euler), blend);
                freeInitialized[i] = true;
            }
        }

        private void SubmitFreeHands(LocalFirstPersonHands rig)
        {
            var data = rig.Binding.Measurements;
            for (int i = 0; i < 2; i++)
            {
                bool right = i == 1;
                float length = (right ? data.RightArm.x + data.RightArm.y : data.LeftArm.x + data.LeftArm.y) * rig.Binding.Scale;
                var shoulder = rig.Binding.GetBone(right ? HumanBodyBones.RightUpperArm : HumanBodyBones.LeftUpperArm);
                freeTargets[i].SetPositionAndRotation(shoulder.position - rig.transform.rotation * appliedBob +
                    rig.transform.rotation * (freePositions[i] * length), rig.transform.rotation * freeRotations[i]);
                avatar.HandTargets.Set(right ? AvatarIKGoal.RightHand : AvatarIKGoal.LeftHand, AvatarHandSource.Free, freeTargets[i],
                    freeWeights[i], freeWeights[i], fingers: avatar.Registry.Animations.RelaxedFingers, openWeight: freeFall);
            }
        }

        internal void StopPresentation()
        {
            if (!running) return;
            running = false;
            PlayerPresentation.LocalCameraChanged -= LocalCameraChanged;
            emote.Changed -= EmoteChanged;
            hidden = false;
            cosmetics?.Dispose(); cosmetics = null;
            avatar.Registry.FirstPerson.ContentChanged -= FirstPersonChanged;
            avatar.IdentityResolved -= IdentityResolved; avatar.PreparingHands -= PrepareRemote; avatar.HandsEvaluated -= CommitRemote;
            avatar.PosingHands -= PoseRemote;
            seating.PresentationContextChanged -= ContextChanged; carry.PresentationContextChanged -= ContextChanged; motor.Simulated -= Simulated;
            carry.PreparingRelease -= CaptureCarryRelease;
            carry.ReleaseStarted -= StartCarryRelease;
            carry.ReleaseRejected -= RejectCarryRelease;
            inventory.InventoryChanged -= CarrySelectionChanged;
            if (items) items.PresentationChanged -= CarryItemChanged;
            ClearCarry();
            foreach (var target in carryTargets) if (target) Destroy(target.gameObject);
            foreach (var hand in new[] { AvatarIKGoal.LeftHand, AvatarIKGoal.RightHand }) avatar.HandTargets.Clear(hand, AvatarHandSource.Free);
            foreach (var target in freeTargets) if (target) Destroy(target.gameObject);
            contacts.Clear(avatar.HandTargets); contacts.Dispose(); contacts = null;
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
