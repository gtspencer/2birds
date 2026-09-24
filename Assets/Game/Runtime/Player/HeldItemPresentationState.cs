using UnityEngine;

namespace TwoBirds
{
    internal sealed class HeldItemPresentationState : System.IDisposable
    {
        private enum RecoveryStage : byte { Follow, Pause, Return, Finished }
        private HeldItemPresentationInput input;
        private readonly HeldItemSettings defaults;
        private AvatarPresentation avatar;
        private AvatarSettings resolvedSettings, boundSettings;
        private AvatarBinding boundBinding;
        private HeldItemBodyFrame? preparedBody;
        internal struct ReleaseSample
        {
            internal uint Item;
            internal ulong Generation;
            internal Pose Palm, LeftPalm, ItemPose;
            internal byte Progress;
            internal bool Clear;
        }
        internal ReleaseSample committed;
        internal System.Action<Pose> CommitItem;
        private Pose correctedItem;
        private Transform target, fallback, followBone, leftTarget;
        private SlingshotPresentation slingshot;
        private bool pullNeedsCorrection;
        private bool SlingshotRecovery => action.State == ItemActionState.Recovering && actionDefinition is SlingshotDefinition;
        private ItemDefinition selectedDefinition, actionDefinition;
        private HeldItemPoseData selectedData, actionData;
        private ItemActionSnapshot action;
        private HeldItemPose pose, preparedPose;
        private HeldItemBodyFrame lastBody;
        private uint selectedId, preparedId;
        private Vector3 chargeStart, followStart, retainedPosition, returnStart;
        private Quaternion chargeRotation, retainedRotation, returnRotation;
        private Pose retainedItem, followItem, returnItem, returnLeft;
        private readonly TwoHandHoldPresentation twoHands = new();
        private float leftWeight, returnLeftWeight, returnFrameWeight;
        private bool returnHeavy;
        private double age, clock, blendStart, returnSegmentStart;
        private float weight, returnWeight, blendDuration, effectiveReach, returnReach;
        private RecoveryStage stage;
        private bool running = true, hasPose, blending, prepared, tracking, submitted, targetInstalled;
        private bool recoveryNeedsPose, releaseUnavailable;
        private bool chargeFromBlend;
        private int advancedFrame = -1;

        internal HeldItemPresentationState(AvatarPresentation avatar, Transform host, HeldItemSettings defaults)
        {
            this.avatar = avatar; this.defaults = defaults;
            target = new GameObject("RightItemHandTarget").transform; target.SetParent(host, false);
            leftTarget = new GameObject("LeftItemHandTarget").transform; leftTarget.SetParent(host, false);
            fallback = new GameObject("HeldItemFallback").transform; fallback.SetParent(host, false);
            Vector3 scale = host.lossyScale;
            fallback.localScale = new Vector3(1f / scale.x, 1f / scale.y, 1f / scale.z);
        }

        internal void SetInput(in HeldItemPresentationInput value, SlingshotPresentation visual)
        {
            input = value; resolvedSettings = avatar.Resolved?.Settings;
            if (slingshot != visual) { if (slingshot) slingshot.ResetPose(); slingshot = visual; }
            SelectionChanged(); ActionChanged();
            FindProjectile();
            if (!input.CanEquip || !input.CanCharge)
            {
                hasPose = blending = tracking = false; weight = leftWeight = 0f;
                twoHands.Reset(); ClearTarget();
            }
        }
        internal bool RecoveryFinished => action.State == ItemActionState.Recovering && actionDefinition && submitted &&
            age >= (actionDefinition is SlingshotDefinition sling ? sling.RecoverySeconds :
                Mathf.Max(0f, actionData.Settings.MaximumFollowDuration) + Mathf.Max(0f, actionData.Settings.EndPosePauseDuration) +
                Mathf.Max(0f, actionData.Settings.ReturnBlendDuration));
        internal void Advance() { CurrentAge(); advancedFrame = Time.frameCount; }
        internal HeldItemBodyFrame LastBody => lastBody;
        internal HeldItemPose Pose => pose;
        internal SlingshotPresentation Slingshot => slingshot;
        internal Transform RightTarget => target;
        internal Transform LeftTarget => leftTarget;
        internal GripReachReadout Readout { get; private set; }
        private bool clearanceAdjusted;
        internal bool Blending => action.State == ItemActionState.Recovering || blending || weight < 0.999f || leftWeight > 0f && leftWeight < 0.999f;
        internal void InvalidateBinding() { Bind(null); preparedBody = null; committed = default; }
        internal void StageRelease()
        {
            prepared = true; preparedId = committed.Item;
            var data = action.State == ItemActionState.Charging ? actionData : selectedData;
            preparedPose = data.Heavy ? new HeldItemPose(committed.ItemPose, committed.LeftPalm, committed.Palm, lastBody) :
                HeldItemPoseCalculation.FromItem(committed.ItemPose, lastBody, data);
        }
        internal bool CorrectItem(Pose item) { correctedItem = item; committed.Clear = false; return CorrectCommittedPose(); }
        public void Dispose()
        {
            running = false; ClearTarget();
            if (slingshot) slingshot.ResetPose();
            UnityEngine.Object.Destroy(target.gameObject); UnityEngine.Object.Destroy(leftTarget.gameObject);
            UnityEngine.Object.Destroy(fallback.gameObject);
        }

        internal bool CanShowHeldItem => running && input.CanEquip && input.HasAction &&
            (input.Action.State != ItemActionState.Recovering ||
                input.ActionDefinition is SlingshotDefinition);
        internal bool ReadyForUse => running && input.CanEquip && input.CanCharge &&
            input.Action.State != ItemActionState.Recovering;
        internal Transform Attachment
        {
            get
            {
                if (selectedDefinition && selectedData.Heavy && resolvedSettings && CanShowHeldItem)
                {
                    var body = HeavyBody(resolvedSettings);
                    if (boundBinding != null) body = body.WithMeasurements(boundBinding.Measurements, boundBinding.Scale);
                    var sample = hasPose && pose.Heavy ? pose : HeldItemPoseCalculation.Hold(body, resolvedSettings, selectedData);
                    fallback.SetPositionAndRotation(sample.Item.position, sample.Item.rotation);
                }
                return fallback;
            }
        }
        internal float HeavyFrameWeight
        {
            get
            {
                if (!running || !input.CanEquip) return 0f;
                float destination = selectedDefinition && selectedData.Heavy ? 1f : 0f;
                if (action.State == ItemActionState.Recovering)
                {
                    if (actionData.Heavy) return twoHands.Frame(actionData.Settings, age, destination);
                    double start = actionData.Settings.MaximumFollowDuration + actionData.Settings.EndPosePauseDuration;
                    double end = start + actionData.Settings.ReturnBlendDuration;
                    if (age < start) return actionData.Heavy ? 1f : 0f;
                    start = System.Math.Max(start, returnSegmentStart);
                    return Mathf.Lerp(returnFrameWeight, destination, Mathf.SmoothStep(0f, 1f,
                        end <= start ? 1f : Mathf.Clamp01((float)((age - start) / (end - start)))));
                }
                if (destination > 0f || action.State == ItemActionState.Charging && actionData.Heavy) return 1f;
                return blending && returnHeavy ? 1f - Mathf.Clamp01((float)((Time.unscaledTimeAsDouble - blendStart) / blendDuration)) : 0f;
            }
        }

        internal HeldItemBodyFrame HeavyBody(AvatarSettings settings) => Body(settings, true);
        private HeldItemBodyFrame Body(AvatarSettings settings, bool heavy = false)
        {
            var placement = boundBinding != null ? avatar.Input : input.Placement;
            bool attached = placement.Seated || placement.Carried && !placement.ReleasePreview;
            Quaternion torso = boundBinding != null ? avatar.transform.rotation : attached ? placement.Facing.rotation :
                Quaternion.Euler(0f, placement.Facing.rotation.eulerAngles.y, 0f);
            float seated = boundBinding != null ? avatar.State.Weights[(int)AvatarPose.Seated] : placement.Seated ? 1f : 0f;
            return new HeldItemBodyFrame(settings, avatar.Registry, placement, torso, seated, heavy);
        }
        internal bool IsPendingRelease(uint id) => running && input.HasAction &&
            input.Action.State == ItemActionState.Recovering && input.Action.WorldId == id &&
            input.ActionDefinition is not SlingshotDefinition;
        internal bool MatchesPendingRelease(in ItemRecord record) => IsPendingRelease(record.Motion.Id) &&
            input.Action.Operation == record.Operation;
        internal HeldItemPoseData Grip(ItemDefinition definition) => definition == selectedDefinition ? selectedData : new HeldItemPoseData(definition, defaults, input.FirstPerson);

        private void Bind(AvatarBinding binding)
        {
            ClearLeftTarget();
            boundBinding = binding;
            boundSettings = binding?.Settings;
            followBone = binding?.GetBone(HumanBodyBones.RightHand);
            committed = default;
        }

        private void SelectionChanged()
        {
            if (!running) return;
            uint id = input.SelectedId;
            var definition = input.SelectedDefinition;
            if (id == selectedId && definition == selectedDefinition) return;
            float frameWeight = HeavyFrameWeight;
            ClearLeftTarget();
            selectedId = id;
            selectedDefinition = definition;
            if (definition) selectedData = new HeldItemPoseData(definition, defaults, input.FirstPerson);
            if (action.State == ItemActionState.Recovering)
            {
                if (stage == RecoveryStage.Return && hasPose)
                {
                    returnStart = lastBody.ToLocal(pose.FollowPosition);
                    returnRotation = Quaternion.Inverse(lastBody.Rotation) * pose.FollowRotation;
                    returnWeight = weight;
                    returnReach = effectiveReach;
                    CaptureTwoHands(lastBody);
                    returnSegmentStart = age;
                    returnFrameWeight = frameWeight;
                    if (actionData.Heavy) twoHands.Retarget(lastBody, age);
                }
            }
            else BeginBlend(definition ? selectedData.Settings.ReturnBlendDuration : blendDuration);

        }

        internal void RefreshContent()
        {
            if (selectedDefinition) selectedData = new HeldItemPoseData(selectedDefinition, defaults, input.FirstPerson);
            if (actionDefinition)
                actionData = new HeldItemPoseData(actionDefinition, defaults, input.FirstPerson);
            committed = default; prepared = false;
            if (hasPose && action.State == ItemActionState.Recovering)
            {
                returnStart = lastBody.ToLocal(pose.FollowPosition);
                returnRotation = Quaternion.Inverse(lastBody.Rotation) * pose.FollowRotation;
                returnWeight = weight; returnReach = effectiveReach;
                CaptureTwoHands(lastBody);
                if (stage == RecoveryStage.Return) { returnSegmentStart = age; twoHands.Retarget(lastBody, age); }
            }
        }

        private void FindProjectile()
        {
            if (SlingshotRecovery || releaseUnavailable || recoveryNeedsPose) return;
            tracking |= input.ProjectileAvailable;
            releaseUnavailable |= input.ProjectileUnavailable;
        }

        private void ActionChanged()
        {
            if (!running) return;
            var next = input.Action;
            if (input.HasAction && next.State == action.State && next.ControlRevision == action.ControlRevision &&
                next.TransitionSequence == action.TransitionSequence) { return; }
            var previous = action;
            float returnDuration = actionDefinition ? actionData.Settings.ReturnBlendDuration : blendDuration;
            action = next;
            age = !input.FirstPerson && input.HasAction ? input.ActionAge : 0d;
            clock = Time.unscaledTimeAsDouble;
            tracking = false;
            releaseUnavailable = false;
            var previousDefinition = actionDefinition;
            actionDefinition = input.ActionDefinition;
            if (actionDefinition && (action.State != ItemActionState.Recovering || previousDefinition != actionDefinition))
                actionData = new HeldItemPoseData(actionDefinition, defaults, input.FirstPerson);
            if (action.State == ItemActionState.Charging)
            {
                chargeFromBlend = blending && hasPose;
                chargeStart = actionData.HoldPosition(boundSettings ? boundSettings : resolvedSettings);
                chargeRotation = actionData.HoldRotation;
                if (TryBody(out var body, out var settings))
                {
                    if (hasPose)
                    {
                        chargeStart = actionData.Heavy ? lastBody.CenterToLocal(pose.Item.position + pose.Item.rotation * actionData.Sphere.Center) : lastBody.ToLocal(pose.FollowPosition);
                        chargeRotation = Quaternion.Inverse(lastBody.Rotation) * (actionData.Heavy
                            ? pose.Item.rotation * Quaternion.Inverse(actionData.PrefabRotation) : pose.FollowRotation);
                    }
                    pose = ChargePose(body, settings, actionData, ChargeProgress(age), chargeFromBlend, out effectiveReach);
                    lastBody = body;
                    hasPose = true;
                }
                blending = false;
                weight = 1f;
            }
            else if (SlingshotRecovery)
            {
                submitted = !input.FirstPerson;
                prepared = blending = false;
                recoveryNeedsPose = !hasPose || previous.State != ItemActionState.Charging;
                if ((!hasPose || previous.State != ItemActionState.Charging) && TryBody(out var body, out var settings))
                {
                    pose = ChargePose(body, settings, actionData, action.ReleaseArcProgress / 255f, false, out effectiveReach);
                    lastBody = body; hasPose = true;
                    recoveryNeedsPose = false;
                }
                if (hasPose)
                {
                    returnStart = lastBody.ToLocal(pose.FollowPosition);
                    returnRotation = Quaternion.Inverse(lastBody.Rotation) * pose.FollowRotation;
                }
                returnWeight = 1f;
                returnReach = effectiveReach;
                stage = RecoveryStage.Return;
            }
            else if (action.State == ItemActionState.Recovering)
            {
                stage = RecoveryStage.Follow;
                returnSegmentStart = 0d;
                returnFrameWeight = actionData.Heavy ? 1f : 0f;
                blending = false;
                submitted = !input.FirstPerson;
                recoveryNeedsPose = true;
                if (previous.State != ItemActionState.Charging)
                { chargeStart = actionData.HoldPosition(boundSettings ? boundSettings : resolvedSettings); chargeRotation = actionData.HoldRotation; }
                if (TryBody(out var body, out var settings))
                {
                    if (prepared && preparedId == action.WorldId)
                    {
                        pose = preparedPose;
                    }
                    else if (committed.Item == action.WorldId && hasPose)
                    {
                        pose = actionData.Heavy ? new HeldItemPose(committed.ItemPose, committed.LeftPalm, committed.Palm, body) :
                            new HeldItemPose(committed.Palm.position, committed.Palm.rotation *
                            Quaternion.Inverse(body.Measurements.RightWristToPalmRotation), body.Measurements, body.Scale, actionData);
                    }
                    else
                    {
                        pose = HeldItemPoseCalculation.Charge(body, settings, actionData, chargeStart, chargeRotation,
                            action.ReleaseArcProgress / 255f);
                    }
                    lastBody = body;
                    retainedRotation = Quaternion.Inverse(body.Rotation) * pose.FollowRotation;
                    followStart = retainedPosition = body.ToLocal(pose.FollowPosition);
                    followItem = retainedItem = HeavyItemPoseCalculation.ToLocal(pose.Item, body);
                    if (actionData.Heavy) twoHands.BeginRelease(pose.LeftPalm, new Pose(pose.FollowPosition, pose.FollowRotation), body);
                    leftWeight = actionData.Heavy ? 1f : 0f;
                    hasPose = true;
                    recoveryNeedsPose = false;
                }
                prepared = false;
                weight = 1f;
                FindProjectile();
            }
            else
            {
                prepared = false;
                if (previous.State == ItemActionState.Recovering)
                {
                    blending = false;
                    hasPose = false;
                    returnHeavy = false;
                    leftWeight = 0f;
                }
                else BeginBlend(returnDuration);
            }

        }

        private double CurrentAge()
        {
            double now = Time.unscaledTimeAsDouble;
            age += System.Math.Max(0d, now - clock);
            if (action.State == ItemActionState.Charging && actionDefinition is SlingshotDefinition)
                age = input.ActionAge;
            else if (!input.FirstPerson) age = System.Math.Max(age, input.ActionAge);
            clock = now;
            return age;
        }

        private float ChargeProgress(double elapsed) => actionDefinition is SlingshotDefinition
            ? Mathf.Clamp01((float)(elapsed / actionDefinition.ThrowChargeTime)) : actionData.Settings.ChargePoseDuration <= 0f ? 1f :
            Mathf.Clamp01((float)(elapsed / actionData.Settings.ChargePoseDuration));

        private HeldItemPose ChargePose(in HeldItemBodyFrame body, AvatarSettings settings, in HeldItemPoseData data,
            float progress, bool fromBlend, out float reach)
        {
            reach = data.Reach;
            if (actionDefinition is not SlingshotDefinition || !slingshot)
                return HeldItemPoseCalculation.Charge(body, settings, data, chargeStart, chargeRotation, progress);
            var hold = HeldItemPoseCalculation.Hold(body, settings, data);
            var start = fromBlend ? new HeldItemPose(body.ToWorld(chargeStart), body.Rotation * chargeRotation *
                Quaternion.Inverse(body.Measurements.RightWristToPalmRotation), body.Measurements, body.Scale, data) : hold;
            var placement = boundBinding != null ? avatar.Input : input.Placement;
            return HeldItemPoseCalculation.SlingshotCharge(body, data, slingshot, hold, start, progress, input.FirstPerson,
                input.Camera,
                Quaternion.Euler(placement.LookPitch, placement.LookYaw, 0f), out reach);
        }

        private bool TryBody(out HeldItemBodyFrame body, out AvatarSettings settings)
        {
            if (preparedBody.HasValue)
            {
                body = preparedBody.Value; settings = boundSettings ? boundSettings : resolvedSettings;
                return settings;
            }
            bool bound = followBone && boundSettings;
            settings = bound ? boundSettings : resolvedSettings;
            if (!settings) { body = default; return false; }
            if (HeavyFrameWeight > 0f) { body = HeavyBody(settings); return true; }
            body = Body(settings);
            return true;
        }

        internal bool TryPebbleDeparture(uint weapon, uint shot, uint launched, out Vector3 center)
        {
            center = default;
            if (selectedId != weapon || !slingshot || !slingshot.HasLoadedPose || action.WorldId != weapon) return false;
            if (action.State == ItemActionState.Charging ? (int)(action.StartedTick - launched) > 0 : action.Operation != shot) return false;
            center = slingshot.DepartureCenter;
            return true;
        }

        private void PrepareLeft(Pose frame, in HeldItemBodyFrame body, HeldItemPose? candidate = null)
        {
            var sample = candidate ?? pose;
            if (sample.Heavy && leftWeight > 0f)
            {
                leftTarget.SetPositionAndRotation(sample.LeftPalm.position, sample.LeftPalm.rotation);
                var clips = avatar.Registry.Animations;
                var definition = action.State == ItemActionState.Idle ? selectedDefinition : actionDefinition;
                var fingers = action.State == ItemActionState.Recovering ? clips.OpenFingers :
                    definition && definition.GripFingers ? definition.GripFingers : clips.GripFingers;
                avatar.HandTargets.Set(AvatarIKGoal.LeftHand, AvatarHandSource.Item, leftTarget,
                    leftWeight, leftWeight, HeavyItemPoseCalculation.MaximumReach, fingers, bodyRelative: true);
                return;
            }
            if (!slingshot || !CanShowHeldItem) { ClearLeftTarget(); return; }
            var state = selectedId == action.WorldId ? action.State : ItemActionState.Idle;
            float draw = state == ItemActionState.Charging
                ? ChargeProgress(age)
                : state == ItemActionState.Recovering ? action.ReleaseArcProgress / 255f : 0f;
            var data = state == ItemActionState.Idle ? selectedData : actionData;
            Pose palm = slingshot.Evaluate(frame, state, draw, age, body, data.SlingshotCharge.PullingHandDrawOffset, data.PullingContact, data.PrefabScale);
            leftTarget.SetPositionAndRotation(palm.position, palm.rotation);
            if (slingshot.LeftWeight <= 0f) { ClearLeftTarget(); return; }
            avatar.HandTargets.Set(AvatarIKGoal.LeftHand, AvatarHandSource.Item, leftTarget,
                slingshot.LeftWeight, slingshot.LeftWeight, 0.85f, avatar.Registry.Animations.GripFingers, bodyRelative: true);
        }

        private void ClearLeftTarget() => avatar.HandTargets.Clear(AvatarIKGoal.LeftHand, AvatarHandSource.Item);

        internal void ReleaseSubmitted()
        {
            submitted = true;
        }


        private void BeginBlend(float duration)
        {
            blendDuration = Mathf.Max(0f, duration);
            if (!hasPose && followBone && TryBody(out var body, out var settings))
            {
                pose = selectedData.Heavy ? HeldItemPoseCalculation.Hold(body, settings, selectedData) :
                    new HeldItemPose(fallback.position, followBone.rotation, body.Measurements, body.Scale, selectedData);
                lastBody = body;
                weight = 0f;
                hasPose = true;
            }
            blending = hasPose && blendDuration > 0f;
            blendStart = Time.unscaledTimeAsDouble;
            if (!hasPose) return;
            returnStart = lastBody.ToLocal(pose.FollowPosition);
            returnRotation = Quaternion.Inverse(lastBody.Rotation) * pose.FollowRotation;
            returnWeight = weight;
            returnReach = effectiveReach;
            CaptureTwoHands(lastBody);
        }

        private void CaptureTwoHands(in HeldItemBodyFrame body)
        {
            returnHeavy = pose.Heavy;
            returnItem = HeavyItemPoseCalculation.ToLocal(pose.Item, body);
            returnLeft = HeavyItemPoseCalculation.ToLocal(pose.LeftPalm, body);
            returnLeftWeight = leftWeight;
        }

        internal void PrepareHands(AvatarBinding binding, HeldItemBodyFrame? body)
        {
            if (!running) return;
            if (advancedFrame != Time.frameCount) { advancedFrame = Time.frameCount; CurrentAge(); }
            committed = default;
            if (boundBinding != binding) Bind(binding);
            preparedBody = body;
            RefreshPose();
        }

        internal void PrepareCandidate(AvatarBinding binding, in HeldItemBodyFrame body)
        {
            if (!running || !hasPose || weight <= 0f) return;
            var data = action.State == ItemActionState.Idle || SlingshotRecovery ? selectedData : actionData;
            float reach = effectiveReach > 0f ? effectiveReach : data.Reach;
            HeldItemPose sample;
            if (data.Heavy && pose.Heavy)
                sample = HeldItemPoseCalculation.FromItem(pose.Item, body, data);
            else if (action.State == ItemActionState.Charging)
                sample = ChargePose(body, binding.Settings, data, ChargeProgress(age), chargeFromBlend, out reach);
            else if (pose.Heavy)
                sample = new HeldItemPose(pose.Item, pose.LeftPalm, new Pose(pose.FollowPosition, pose.FollowRotation), body);
            else
            {
                sample = new HeldItemPose(body.ToWorld(lastBody.ToLocal(pose.FollowPosition)),
                    body.Rotation * Quaternion.Inverse(lastBody.Rotation) * pose.FollowRotation *
                    Quaternion.Inverse(body.Measurements.RightWristToPalmRotation), body.Measurements, body.Scale, data);
                if (selectedDefinition is SlingshotDefinition || reach > data.Reach)
                    reach = HeldItemPoseCalculation.SlingshotReach(sample, body, data);
                sample = HeldItemPoseCalculation.Resolve(sample.FollowPosition, sample.WristRotation, body, binding.Settings, data, out _,
                    soften: false, effectiveReach: reach);
            }
            target.SetPositionAndRotation(sample.FollowPosition, sample.FollowRotation);
            PrepareLeft(sample.Item, body, sample);
            SetTarget(reach);
        }

        internal bool CommitHands(AvatarBinding binding)
        {
            if (!running || !hasPose) return true;
            Pose palm = binding != null ? binding.Palm(true) : new(pose.FollowPosition, pose.FollowRotation);
            var grip = action.State == ItemActionState.Charging ? actionData : selectedData;
            Pose itemPose = !grip.Heavy ? HeldItemPoseCalculation.ItemFromPalm(palm, grip.RightContact, grip.PrefabScale) :
                binding != null ? AvatarHandTargets.Rebase(pose.Item, new Pose(lastBody.Center, lastBody.Rotation), binding.Body) : pose.Item;
            fallback.SetPositionAndRotation(grip.Heavy ? itemPose.position : palm.position, grip.Heavy ? itemPose.rotation : palm.rotation);
            bool clear = true;
            if (selectedId != 0 && CanShowHeldItem)
            {
                CommitItem?.Invoke(itemPose);
                Vector3 previousLeft = leftTarget.position;
                PrepareLeft(itemPose, lastBody);
                pullNeedsCorrection = slingshot && slingshot.LeftWeight > 0f &&
                    (leftTarget.position - previousLeft).sqrMagnitude > 0.000001f;
                if (slingshot && binding != null) slingshot.CommitPalm(binding.Palm(false));
                if (input.FirstPerson)
                {
                    bool accessible = ResolveClearance(itemPose, lastBody, grip, out correctedItem);
                    clear = accessible && (correctedItem.position - itemPose.position).sqrMagnitude < 0.000001f;
                    if (!accessible) correctedItem = itemPose;
                }
            }
            Pose evaluatedLeft = binding != null ? binding.Palm(false) : pose.LeftPalm;
            bool impossible = pose.Heavy && !HeavyItemPoseCalculation.InReach(pose, lastBody, HeavyItemPoseCalculation.MaximumReach);
            bool pulling = slingshot && CanShowHeldItem && (slingshot.Loaded || slingshot.LeftWeight > 0f);
            Pose requestedLeft = slingshot ? new Pose(leftTarget.position, leftTarget.rotation) : pose.RequestedLeft;
            Readout = new GripReachReadout
            {
                RequestedRight = pose.RequestedRight, RequestedLeft = requestedLeft,
                EvaluatedRight = palm, EvaluatedLeft = evaluatedLeft, ActiveBlend = Blending,
                ReachLimited = pose.ReachLimited, ClearanceAdjusted = clearanceAdjusted,
                RightUnreachable = !Blending && (impossible || slingshot && CanShowHeldItem) &&
                    !InHandReach(pose.RequestedRight, true, pose.Heavy ? HeavyItemPoseCalculation.MaximumReach : effectiveReach),
                LeftUnreachable = !Blending && (impossible || pulling) &&
                    !InHandReach(requestedLeft, false, pulling ? 0.85f : pose.Heavy ? HeavyItemPoseCalculation.MaximumReach : effectiveReach),
                HasLeft = pose.Heavy || pulling
            };
            committed = new ReleaseSample { Item = selectedId, Generation = binding?.Generation ?? 0,
                Palm = palm, LeftPalm = binding != null ? binding.Palm(false) : pose.LeftPalm,
                ItemPose = itemPose, Clear = clear,
                Progress = (byte)Mathf.RoundToInt((action.State == ItemActionState.Charging ? ChargeProgress(age) : 0f) * 255f) };
            return clear && !pullNeedsCorrection;
        }

        private bool InHandReach(Pose palm, bool right, float reach)
        {
            var data = lastBody.Measurements;
            var rotation = palm.rotation * Quaternion.Inverse(right ? data.RightWristToPalmRotation : data.LeftWristToPalmRotation);
            var wrist = palm.position - rotation * ((right ? data.RightWristToPalmPosition : data.LeftWristToPalmPosition) * lastBody.Scale);
            return Vector3.Distance(wrist, right ? lastBody.Shoulder : lastBody.LeftShoulder) <=
                (right ? lastBody.ArmLength : lastBody.LeftArmLength) * reach + 0.001f;
        }

        internal bool CorrectCommittedPose()
        {
            if (committed.Clear || (correctedItem.position - committed.ItemPose.position).sqrMagnitude < 0.000001f)
            { bool retry = pullNeedsCorrection; pullNeedsCorrection = false; return retry; }
            var data = action.State == ItemActionState.Charging ? actionData : selectedData;
            ApplyClearancePose(correctedItem, lastBody, data);
            target.SetPositionAndRotation(pose.FollowPosition, pose.FollowRotation);
            PrepareLeft(pose.Item, lastBody);
            return true;
        }

        private bool ResolveClearance(Pose desired, in HeldItemBodyFrame body, in HeldItemPoseData data, out Pose allowed)
        {
            if (data.Heavy)
                return ItemReleaseClearance.TryResolve(desired, input.Aim.position, data.Sphere, body.Rotation,
                    input.EnvironmentMask, HeavyItemPoseCalculation.Reach(desired, body, data), out allowed);
            var sample = HeldItemPoseCalculation.FromItem(desired, body, data);
            return ItemReleaseClearance.TryResolve(desired, input.Aim.position, data.ReleaseRadius, body.Rotation,
                input.EnvironmentMask, body.Shoulder + desired.position - sample.WristPosition,
                body.ArmLength * (effectiveReach > 0f ? effectiveReach : data.Reach), out allowed);
        }

        private void ApplyClearancePose(Pose allowed, in HeldItemBodyFrame body, in HeldItemPoseData data)
        {
            clearanceAdjusted |= (allowed.position - pose.Item.position).sqrMagnitude > 0.000001f;
            var corrected = HeldItemPoseCalculation.FromItem(allowed, body, data);
            var evaluated = !data.Heavy && pose.Heavy && leftWeight > 0f
                ? new HeldItemPose(corrected.Item, pose.LeftPalm, new Pose(corrected.FollowPosition, corrected.FollowRotation), body)
                : corrected;
            pose = new HeldItemPose(evaluated, pose);
        }

        private void RefreshPose()
        {
            clearanceAdjusted = false;
            if (!running) return;
            if (!TryBody(out var body, out var settings)) { hasPose = false; ClearTarget(); return; }
            if (!input.CanEquip || !input.HasAction)
            { weight = 0f; ClearTarget(); return; }
            effectiveReach = (action.State == ItemActionState.Idle || SlingshotRecovery) && selectedDefinition
                ? selectedData.Reach : actionData.Reach;
            if (SlingshotRecovery)
            {
                if (recoveryNeedsPose)
                {
                    pose = ChargePose(body, settings, actionData, action.ReleaseArcProgress / 255f, false, out effectiveReach);
                    returnStart = body.ToLocal(pose.FollowPosition);
                    returnRotation = Quaternion.Inverse(body.Rotation) * pose.FollowRotation;
                    returnReach = effectiveReach;
                    recoveryNeedsPose = false;
                }
                if (selectedId == action.WorldId) Blend(body, settings, Mathf.Clamp01((float)age / 0.2f), selectedData);
                else if (selectedDefinition) { pose = HeldItemPoseCalculation.Hold(body, settings, selectedData); weight = 1f; hasPose = true; }
                else weight = 0f;
            }
            else if (action.State == ItemActionState.Recovering && actionDefinition)
            {
                if (recoveryNeedsPose)
                {
                    pose = HeldItemPoseCalculation.Charge(body, settings, actionData, chargeStart, chargeRotation,
                        action.ReleaseArcProgress / 255f);
                    retainedRotation = Quaternion.Inverse(body.Rotation) * pose.FollowRotation;
                    followStart = retainedPosition = body.ToLocal(pose.FollowPosition);
                    followItem = retainedItem = HeavyItemPoseCalculation.ToLocal(pose.Item, body);
                    if (actionData.Heavy) twoHands.BeginRelease(pose.LeftPalm, new Pose(pose.FollowPosition, pose.FollowRotation), body);
                    leftWeight = actionData.Heavy ? 1f : 0f;
                    recoveryNeedsPose = false;
                    FindProjectile();
                }
                Recover(body, settings, age);
            }
            else if (action.State == ItemActionState.Charging && actionDefinition)
            {
                pose = ChargePose(body, settings, actionData, ChargeProgress(age), chargeFromBlend, out effectiveReach);
                weight = 1f;
                leftWeight = actionData.Heavy ? 1f : 0f;
                hasPose = true;
            }
            else if (selectedDefinition || blending)
            {
                float t = blending ? Mathf.Clamp01((float)((Time.unscaledTimeAsDouble - blendStart) / blendDuration)) : 1f;
                Blend(body, settings, t, selectedDefinition ? selectedData : actionData);
                if (t >= 1f) blending = false;
            }
            else weight = 0f;
            lastBody = body;
            if (hasPose)
            {
                var data = action.State == ItemActionState.Charging ? actionData : selectedData;
                if (input.FirstPerson && CanShowHeldItem && selectedId != 0 && ResolveClearance(pose.Item, body, data, out var allowed))
                {
                    ApplyClearancePose(allowed, body, data);
                }
                target.SetPositionAndRotation(pose.FollowPosition, pose.FollowRotation);
                if (!followBone) fallback.SetPositionAndRotation(data.Heavy ? pose.Item.position : pose.FollowPosition,
                    data.Heavy ? pose.Item.rotation : pose.FollowRotation);
            }
            PrepareLeft(pose.Item, body);
            SetTarget(effectiveReach);
        }

        private void Recover(in HeldItemBodyFrame body, AvatarSettings settings, double elapsed)
        {
            if (actionData.Heavy) { RecoverHeavy(body, settings, elapsed); return; }
            double followEnd = Mathf.Max(0f, actionData.Settings.MaximumFollowDuration);
            double pauseEnd = followEnd + Mathf.Max(0f, actionData.Settings.EndPosePauseDuration);
            double returnEnd = pauseEnd + Mathf.Max(0f, actionData.Settings.ReturnBlendDuration);
            Quaternion palmToWrist = Quaternion.Inverse(body.Measurements.RightWristToPalmRotation);
            if (stage == RecoveryStage.Follow)
            {
                bool unavailable = releaseUnavailable || tracking && !input.ProjectileAvailable;
                bool beyond = false;
                pose = HeldItemPoseCalculation.Resolve(body.ToWorld(retainedPosition), body.Rotation * retainedRotation * palmToWrist,
                    body, settings, actionData, out _, soften: false);
                if (tracking && !unavailable && elapsed < followEnd)
                {
                    var contact = HeldItemPoseCalculation.PalmFromItem(new Pose(input.Projectile.position, input.Projectile.rotation),
                        actionData.RightContact, actionData.PrefabScale);
                    Quaternion palm = contact.rotation;
                    Vector3 follow = contact.position;
                    var candidate = HeldItemPoseCalculation.Resolve(Vector3.Lerp(body.ToWorld(followStart), follow,
                        LeanTween.easeOutSine(0f, 1f, Mathf.Clamp01((float)elapsed / 0.08f))),
                        palm * palmToWrist, body, settings, actionData, out beyond, soften: false);
                    if (Physics.Linecast(body.Shoulder, candidate.FollowPosition, input.EnvironmentMask, QueryTriggerInteraction.Ignore)) beyond = true;
                    else
                    {
                        pose = candidate;
                        retainedPosition = body.ToLocal(pose.FollowPosition);
                        retainedRotation = Quaternion.Inverse(body.Rotation) * pose.FollowRotation;
                    }
                }
                if (unavailable || beyond || elapsed >= followEnd)
                {
                    tracking = false; releaseUnavailable = true;
                }
                if (elapsed >= followEnd) stage = RecoveryStage.Pause;
            }
            if (stage == RecoveryStage.Pause)
            {
                pose = HeldItemPoseCalculation.Resolve(body.ToWorld(retainedPosition), body.Rotation * retainedRotation * palmToWrist,
                    body, settings, actionData, out _, soften: false);
                if (elapsed >= pauseEnd)
                {
                    stage = RecoveryStage.Return;
                    returnStart = body.ToLocal(pose.FollowPosition);
                    returnRotation = Quaternion.Inverse(body.Rotation) * pose.FollowRotation;
                    returnWeight = 1f; returnSegmentStart = pauseEnd;
                    returnReach = actionData.Reach;
                    CaptureTwoHands(body);
                }
            }
            if (stage == RecoveryStage.Return || stage == RecoveryStage.Finished)
            {
                float t = returnEnd <= returnSegmentStart ? 1f : Mathf.Clamp01((float)((elapsed - returnSegmentStart) / (returnEnd - returnSegmentStart)));
                Blend(body, settings, t, selectedDefinition ? selectedData : actionData);
                if (elapsed >= returnEnd) stage = RecoveryStage.Finished;
            }
            hasPose = true;
        }

        private void RecoverHeavy(in HeldItemBodyFrame body, AvatarSettings settings, double elapsed)
        {
            bool holding = selectedDefinition && input.CanEquip;
            var data = holding ? selectedData : actionData;
            var destination = HeldItemPoseCalculation.Hold(body, settings, data);
            bool available = tracking && input.ProjectileAvailable;
            var live = available ? HeldItemPoseCalculation.FromItem(new Pose(input.Projectile.position,
                input.Projectile.rotation), body, actionData) : pose;
            twoHands.Sample(live.LeftPalm, new Pose(live.FollowPosition, live.FollowRotation), available,
                releaseUnavailable || tracking && !available, body, actionData.Settings, input.EnvironmentMask, elapsed,
                holding && !data.Heavy ? twoHands.Left : destination.LeftPalm,
                new Pose(destination.FollowPosition, destination.FollowRotation), holding && data.Heavy ? 1f : 0f,
                holding ? 1f : 0f, holding && data.Heavy ? 1f : 0f);
            if (available && twoHands.Following)
            {
                var root = HeavyItemPoseCalculation.Blend(HeavyItemPoseCalculation.ToWorld(followItem, body), live.Item,
                    LeanTween.easeOutSine(0f, 1f, Mathf.Clamp01((float)elapsed / 0.08f)));
                retainedItem = HeavyItemPoseCalculation.ToLocal(root, body);
            }
            if (!twoHands.Following) { tracking = false; releaseUnavailable = true; }
            stage = (RecoveryStage)twoHands.Stage;
            float blend = Mathf.SmoothStep(0f, 1f, twoHands.ReturnProgress(actionData.Settings, elapsed));
            var item = HeavyItemPoseCalculation.Blend(HeavyItemPoseCalculation.ToWorld(retainedItem, body), destination.Item, blend);
            pose = new HeldItemPose(item, twoHands.Left, twoHands.Right, body);
            weight = twoHands.RightWeight; leftWeight = twoHands.LeftWeight;
            effectiveReach = Mathf.Lerp(actionData.Reach, data.Reach, blend);
            hasPose = true;
        }

        private void Blend(in HeldItemBodyFrame body, AvatarSettings settings, float t, in HeldItemPoseData data)
        {
            bool holding = selectedDefinition && input.CanEquip;
            var destination = HeldItemPoseCalculation.Hold(body, settings, data);
            if (data.Heavy || returnHeavy)
            {
                float heavyBlend = Mathf.SmoothStep(0f, 1f, t);
                effectiveReach = data.Reach;
                if (data.Heavy)
                {
                    var start = HeavyItemPoseCalculation.ToWorld(returnItem, body);
                    Pose frame = t >= 1f || !hasPose ? destination.Item : new Pose(Vector3.Lerp(start.position, destination.Item.position, heavyBlend),
                        Quaternion.Slerp(start.rotation, destination.Item.rotation, heavyBlend));
                    var evaluated = HeldItemPoseCalculation.FromItem(frame, body, data);
                    pose = new HeldItemPose(frame, evaluated.LeftPalm, new Pose(evaluated.FollowPosition, evaluated.FollowRotation), body,
                        requestedRight: destination.RequestedRight, requestedLeft: destination.RequestedLeft,
                        unreachable: destination.Unreachable);
                }
                else
                {
                    var right = HeldItemPoseCalculation.Resolve(Vector3.Lerp(body.ToWorld(returnStart), destination.FollowPosition, heavyBlend),
                        Quaternion.Slerp(body.Rotation * returnRotation, destination.FollowRotation, heavyBlend) *
                        Quaternion.Inverse(body.Measurements.RightWristToPalmRotation), body, settings, data, out _, soften: false);
                    pose = new HeldItemPose(right.Item, HeavyItemPoseCalculation.ToWorld(returnLeft, body),
                        new Pose(right.FollowPosition, right.FollowRotation), body);
                }
                weight = Mathf.Lerp(returnWeight, holding ? 1f : 0f, heavyBlend);
                leftWeight = Mathf.Lerp(returnLeftWeight, holding && data.Heavy ? 1f : 0f, heavyBlend);
                if (t >= 1f)
                {
                    returnHeavy = holding && data.Heavy;
                    if (!data.Heavy) pose = destination;
                }
                hasPose = true;
                return;
            }
            leftWeight = 0f;
            bool extended = selectedDefinition is SlingshotDefinition || returnReach > data.Reach;
            float movement = extended ? Mathf.SmoothStep(0f, 1f, t) : LeanTween.easeOutBack(0f, 1f, t, 0.5f);
            effectiveReach = extended ? Mathf.Lerp(Mathf.Max(data.Reach, returnReach), data.Reach, movement) : data.Reach;
            if (extended && t < 1f)
            {
                var sample = new HeldItemPose(Vector3.Lerp(body.ToWorld(returnStart), destination.FollowPosition, movement),
                    Quaternion.Slerp(body.Rotation * returnRotation, destination.FollowRotation, movement) *
                    Quaternion.Inverse(body.Measurements.RightWristToPalmRotation), body.Measurements, body.Scale, data);
                effectiveReach = Mathf.Max(effectiveReach, HeldItemPoseCalculation.SlingshotReach(sample, body, data));
            }
            pose = t >= 1f ? destination : HeldItemPoseCalculation.Resolve(Vector3.LerpUnclamped(body.ToWorld(returnStart), destination.FollowPosition, movement),
                Quaternion.SlerpUnclamped(body.Rotation * returnRotation, destination.FollowRotation, movement) *
                Quaternion.Inverse(body.Measurements.RightWristToPalmRotation), body, settings, data, out _,
                soften: false, effectiveReach: effectiveReach);
            weight = Mathf.Lerp(returnWeight, holding ? 1f : 0f, LeanTween.easeInOutSine(0f, 1f, t));
            hasPose = true;
        }

        private void SetTarget(float reach)
        {
            if (weight <= 0f) { ClearTarget(); return; }
            var clips = avatar.Registry.Animations;
            AnimationClip fingers = action.State == ItemActionState.Recovering && !SlingshotRecovery ? clips.OpenFingers :
                selectedDefinition && selectedDefinition.GripFingers ? selectedDefinition.GripFingers : clips.GripFingers;
            if (pose.Heavy)
                TwoHandHoldPresentation.Submit(avatar.HandTargets, AvatarHandSource.Item, leftTarget, target,
                    new Pose(leftTarget.position, leftTarget.rotation), new Pose(target.position, target.rotation),
                    leftWeight, weight, HeavyItemPoseCalculation.MaximumReach, fingers);
            else avatar.HandTargets.Set(AvatarIKGoal.RightHand, AvatarHandSource.Item, target, weight, weight, reach, fingers, bodyRelative: true);
            targetInstalled = true;
        }
        private void ClearTarget()
        {
            ClearLeftTarget();
            if (!targetInstalled) return;
            TwoHandHoldPresentation.Clear(avatar.HandTargets, AvatarHandSource.Item);
            targetInstalled = false;
        }
    }
}
