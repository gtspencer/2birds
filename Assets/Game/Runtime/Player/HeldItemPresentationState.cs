using UnityEngine;

namespace TwoBirds
{
    internal sealed class HeldItemPresentationState : System.IDisposable
    {
        private HeldItemPresentationInput input;
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
        private Transform target, fallback, leftTarget;
        private SlingshotPresentation slingshot;
        private bool SlingshotRecovery => action.State == ItemActionState.Recovering && actionDefinition is SlingshotDefinition;
        private ItemDefinition selectedDefinition, actionDefinition;
        private HoldClass selectedClass, actionClass;
        private HeldItemPoseData selectedData, actionData;
        private Pose selectedGrip, actionGrip, rightInItem, leftInItem;
        private ItemActionSnapshot action;
        private HeldItemBodyFrame lastBody;
        private uint selectedId, preparedId;
        private readonly HandHoldPresentation hands = new();
        private const int Slots = AvatarArmPose.Slots;
        private readonly HoldClass[] classes = new HoldClass[Slots];
        private readonly float[] weights = new float[Slots], startWeights = new float[Slots];
        private float charge, startCharge, releaseCharge, spread;
        private HoldClass chargeClass;
        private Pose preparedLeft, preparedRight, preparedItem;
        private double age, clock, blendStart;
        private float blendDuration;
        private bool running = true, hasBody, blending, prepared, tracking, submitted, targetInstalled;
        private bool releaseUnavailable;
        private int advancedFrame = -1;
        internal float HeavyFrameWeight => running && input.CanEquip ? ModeWeight(ItemHoldMode.TwoHand) : 0f;
        internal float ArmWeight => running && input.CanEquip ? weights[0] + weights[1] + weights[2] : 0f;
        private static float Ease(float progress) => LeanTween.easeOutSine(0f, 1f, Mathf.Clamp01(progress));
        private float ModeWeight(ItemHoldMode mode)
        {
            float total = 0f;
            for (int i = 0; i < Slots; i++) if (classes[i] && classes[i].Mode == mode) total += weights[i];
            return total;
        }
        private float ClassWeight(HoldClass value)
        {
            int slot = !value ? -1 : System.Array.IndexOf(classes, value);
            return slot >= 0 ? weights[slot] : 0f;
        }

        internal HeldItemPresentationState(AvatarPresentation avatar, Transform host)
        {
            this.avatar = avatar;
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
                blending = tracking = false; charge = 0f;
                hands.Reset(); ClearTargets();
            }
        }
        internal bool RecoveryFinished => action.State == ItemActionState.Recovering && actionDefinition && submitted &&
            age >= (actionDefinition is SlingshotDefinition sling ? sling.RecoverySeconds :
                Mathf.Max(0f, actionData.Class.MaximumFollowDuration) + Mathf.Max(0f, actionData.Class.EndPosePauseDuration) +
                Mathf.Max(0f, actionData.Class.ReturnBlendDuration));
        internal void Advance() { CurrentAge(); RefreshArms(); advancedFrame = Time.frameCount; }
        internal HeldItemBodyFrame LastBody => lastBody;
        internal SlingshotPresentation Slingshot => slingshot;
        internal Transform RightTarget => target;
        internal Transform LeftTarget => leftTarget;
        internal GripReachReadout Readout { get; private set; }
        internal Pose GripFrame { get; private set; }
        private bool clearanceAdjusted;
        internal bool Blending => blending || action.State == ItemActionState.Recovering;
        internal void InvalidateBinding() { Bind(null); preparedBody = null; committed = default; }
        internal void StageRelease()
        {
            prepared = true; preparedId = committed.Item;
            preparedLeft = committed.LeftPalm; preparedRight = committed.Palm; preparedItem = committed.ItemPose;
        }
        public void Dispose()
        {
            running = false; ClearTargets(); avatar.HandTargets.SetArms(default);
            Watch(ref selectedDefinition, null, actionDefinition); Watch(ref actionDefinition, null, null);
            Watch(ref selectedClass, null, actionClass); Watch(ref actionClass, null, null);
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
                if (selectedDefinition && CanShowHeldItem)
                {
                    if (boundBinding != null)
                    {
                        var item = ItemPose(boundBinding, selectedData, selectedGrip);
                        fallback.SetPositionAndRotation(item.position, item.rotation);
                    }
                    else if (TryBody(out var body, out _))
                    {
                        var item = HeldItemPoseCalculation.Compose(HeldItemPoseCalculation.FallbackFrame(body, selectedData), selectedGrip);
                        fallback.SetPositionAndRotation(item.position, item.rotation);
                    }
                }
                return fallback;
            }
        }

        private static Pose ItemPose(AvatarBinding binding, in HeldItemPoseData data, Pose grip)
        {
            if (!data.TwoHand) return HeldItemPoseCalculation.Compose(binding.Palm(true), grip);
            return binding.AnchoredItem ?? HeldItemPoseCalculation.Compose(
                HeldItemPoseCalculation.GripFrame(binding.Palm(true), binding.Palm(false), binding.Body.rotation), grip);
        }

        private HeldItemBodyFrame HeavyBody(AvatarSettings settings) => Body(settings, true);
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

        private void Bind(AvatarBinding binding)
        {
            boundBinding = binding;
            boundSettings = binding?.Settings;
            committed = default;
            RefreshGrips();
        }

        // One subscription per asset: other is the paired field, which keeps it when both hold the same asset.
        private void Watch(ref ItemDefinition current, ItemDefinition next, ItemDefinition other)
        {
            if (current == next) return;
            if (current && current != other) current.ContentChanged -= RefreshContent;
            current = next;
            if (current && current != other) current.ContentChanged += RefreshContent;
        }

        private void Watch(ref HoldClass current, HoldClass next, HoldClass other)
        {
            if (current == next) return;
            if (current && current != other) current.ContentChanged -= RefreshContent;
            current = next;
            if (current && current != other) current.ContentChanged += RefreshContent;
        }

        private void WatchClasses()
        {
            Watch(ref selectedClass, selectedDefinition ? selectedDefinition.HoldClass : null, actionClass);
            Watch(ref actionClass, actionDefinition ? actionDefinition.HoldClass : null, selectedClass);
        }

        private void RefreshGrips()
        {
            var id = boundBinding != null ? boundBinding.Id : avatar.Resolved != null ? avatar.Resolved.Id : default;
            if (selectedDefinition) selectedGrip = selectedDefinition.Grip(id, input.FirstPerson);
            if (actionDefinition) actionGrip = actionDefinition.Grip(id, input.FirstPerson);
        }

        private void SelectionChanged()
        {
            if (!running) return;
            uint id = input.SelectedId;
            var definition = input.SelectedDefinition;
            if (id == selectedId && definition == selectedDefinition) return;
            selectedId = id;
            Watch(ref selectedDefinition, definition, actionDefinition);
            WatchClasses();
            if (definition) selectedData = new HeldItemPoseData(definition);
            RefreshGrips();
            if (action.State == ItemActionState.Recovering && !SlingshotRecovery)
            { if (hands.Stage == HandHoldPresentation.RecoveryStage.Return) BeginReturn(); }
            else BeginBlend(definition ? selectedData.Class.ReturnBlendDuration : blendDuration);
        }

        internal void RefreshContent()
        {
            WatchClasses();
            if (selectedDefinition) selectedData = new HeldItemPoseData(selectedDefinition);
            if (actionDefinition) actionData = new HeldItemPoseData(actionDefinition);
            RefreshGrips();
            prepared = false;
            if (hands.Stage == HandHoldPresentation.RecoveryStage.Return) BeginReturn();
        }

        private void FindProjectile()
        {
            if (SlingshotRecovery || releaseUnavailable) return;
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
            float returnDuration = actionDefinition ? actionData.Class.ReturnBlendDuration : blendDuration;
            action = next;
            age = !input.FirstPerson && input.HasAction ? input.ActionAge : 0d;
            clock = Time.unscaledTimeAsDouble;
            tracking = false;
            releaseUnavailable = false;
            var previousDefinition = actionDefinition;
            Watch(ref actionDefinition, input.ActionDefinition, selectedDefinition);
            WatchClasses();
            if (actionDefinition && (action.State != ItemActionState.Recovering || previousDefinition != actionDefinition))
            {
                actionData = new HeldItemPoseData(actionDefinition);
                RefreshGrips();
            }
            if (action.State == ItemActionState.Charging) chargeClass = actionData.Class;
            else if (SlingshotRecovery)
            {
                submitted = !input.FirstPerson;
                prepared = blending = false;
                chargeClass = actionData.Class;
                releaseCharge = charge = Ease(action.ReleaseArcProgress / 255f);
                hands.Reset();
            }
            else if (action.State == ItemActionState.Recovering)
            {
                submitted = !input.FirstPerson;
                blending = false;
                chargeClass = actionData.Class;
                releaseCharge = charge = Ease(action.ReleaseArcProgress / 255f);
                StartFollow();
                int slot = Claim(actionData.Class);
                for (int i = 0; i < Slots; i++) startWeights[i] = i == slot ? 1f : 0f;
                startCharge = charge; prepared = false;
                FindProjectile();
            }
            else
            {
                prepared = false;
                if (previous.State == ItemActionState.Recovering) { blending = false; hands.Reset(); charge = 0f; }
                else BeginBlend(returnDuration);
            }
        }

        private int Claim(HoldClass value)
        {
            int slot = System.Array.IndexOf(classes, value);
            if (slot >= 0) return slot;
            slot = 0;
            for (int i = 1; i < Slots; i++) if (weights[i] < weights[slot]) slot = i;
            classes[slot] = value; weights[slot] = startWeights[slot] = 0f;
            return slot;
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
            ? Mathf.Clamp01((float)(elapsed / actionDefinition.ThrowChargeTime)) : actionData.Class.ChargePoseDuration <= 0f ? 1f :
            Mathf.Clamp01((float)(elapsed / actionData.Class.ChargePoseDuration));

        private bool TryBody(out HeldItemBodyFrame body, out AvatarSettings settings)
        {
            if (preparedBody.HasValue)
            {
                body = preparedBody.Value; settings = boundSettings ? boundSettings : resolvedSettings;
                return settings;
            }
            bool bound = boundBinding != null && boundSettings;
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

        internal void ReleaseSubmitted()
        {
            submitted = true;
        }

        private void BeginBlend(float duration)
        {
            blendDuration = Mathf.Max(0f, duration);
            System.Array.Copy(weights, startWeights, Slots); startCharge = charge;
            blendStart = Time.unscaledTimeAsDouble; blending = blendDuration > 0f;
        }

        private void BeginReturn()
        {
            System.Array.Copy(weights, startWeights, Slots); startCharge = charge;
            hands.Retarget(lastBody, age);
        }

        private void StartFollow()
        {
            hands.Reset();
            if (!TryBody(out var body, out _)) return;
            Pose left, right, item;
            if (prepared && preparedId == action.WorldId) { left = preparedLeft; right = preparedRight; item = preparedItem; }
            else if (committed.Item == action.WorldId) { left = committed.LeftPalm; right = committed.Palm; item = committed.ItemPose; }
            else if (boundBinding != null)
            { left = boundBinding.Palm(false); right = boundBinding.Palm(true); item = ItemPose(boundBinding, actionData, actionGrip); }
            else return;
            var inverse = HeldItemPoseCalculation.Inverse(item);
            rightInItem = HeldItemPoseCalculation.Compose(inverse, right);
            leftInItem = HeldItemPoseCalculation.Compose(inverse, left);
            lastBody = body;
            hands.BeginRelease(left, right, body, actionData.TwoHand);
        }

        private void RefreshArms()
        {
#if UNITY_INCLUDE_INSTRUMENTATION
            if (Authoring != null)
            {
                var held = selectedDefinition ? selectedData.Class : null;
                int authored = held ? Claim(held) : -1;
                for (int i = 0; i < Slots; i++) weights[i] = startWeights[i] = i == authored ? 1f : 0f;
                charge = startCharge = Authoring.Charged ? 1f : 0f; chargeClass = held;
                blending = false;
                return;
            }
#endif
            var selected = selectedDefinition && input.CanEquip ? selectedData.Class : null;
            int slot = selected ? Claim(selected) : -1;
            float t;
            if (action.State == ItemActionState.Recovering && !SlingshotRecovery && actionDefinition)
            {
                t = Mathf.SmoothStep(0f, 1f, hands.ReturnProgress(actionData.Class, age));
                charge = Mathf.Lerp(startCharge, 0f, t);
            }
            else
            {
                float blend = blending ? Mathf.Clamp01((float)((Time.unscaledTimeAsDouble - blendStart) / blendDuration)) : 1f;
                if (blend >= 1f) blending = false;
                t = Mathf.SmoothStep(0f, 1f, blend);
                if (action.State == ItemActionState.Charging && actionDefinition) charge = Ease(ChargeProgress(age));
                else if (SlingshotRecovery) charge = Mathf.Lerp(releaseCharge, 0f, Mathf.SmoothStep(0f, 1f,
                    Mathf.Clamp01((float)(age / Mathf.Max(0.01f, ((SlingshotDefinition)actionDefinition).RecoverySeconds)))));
                else charge = Mathf.Lerp(startCharge, 0f, t);
            }
            for (int i = 0; i < Slots; i++) weights[i] = Mathf.Lerp(startWeights[i], i == slot ? 1f : 0f, t);
        }

        internal void PrepareHands(AvatarBinding binding, HeldItemBodyFrame? body)
        {
            if (!running) return;
            if (advancedFrame != Time.frameCount) Advance();
            committed = default;
            if (boundBinding != binding) Bind(binding);
            preparedBody = body;
            hasBody = TryBody(out lastBody, out _);
            if (!hasBody) { ClearTargets(); return; }
            if (!input.CanEquip || !input.HasAction) { avatar.HandTargets.SetArms(default); ClearTargets(); return; }
            SubmitTargets();
        }

        private void SubmitTargets()
        {
            var targets = avatar.HandTargets;
            bool authoring = false;
#if UNITY_INCLUDE_INSTRUMENTATION
            authoring = Authoring != null;
            targets.Swivel[0] = authoring ? Authoring.LeftSwivel : 0f;
            targets.Swivel[1] = authoring ? Authoring.RightSwivel : 0f;
            targets.RoundTripMuscles = false;
#endif
            var placement = boundBinding != null ? avatar.Input : input.Placement;
            targets.SetArms(new AvatarArmPose
            {
                A = classes[0], B = classes[1], C = classes[2], WeightA = weights[0], WeightB = weights[1], WeightC = weights[2],
                ChargeClass = chargeClass, Charge = charge,
                Pitch = input.FirstPerson || authoring ? 0f : Mathf.Clamp(placement.LookPitch, -40f, 50f) * charge
            });
            if (action.State == ItemActionState.Recovering && !SlingshotRecovery) { targets.ClearAnchor(); SubmitFollow(); return; }
            if (!selectedDefinition || !CanShowHeldItem) { ClearTargets(); return; }
            bool charging = action.State == ItemActionState.Charging;
            var data = charging ? actionData : selectedData;
            var clips = avatar.Registry.Animations;
            var fingers = data.Fingers ? data.Fingers : clips.GripFingers;
            float weight = ClassWeight(data.Class);
            spread = data.TwoHand ? data.Class.Spread(input.FirstPerson, data.Class == chargeClass ? charge : 0f) : 0f;
#if UNITY_INCLUDE_INSTRUMENTATION
            if (authoring && (Authoring.Right.HasValue || Authoring.Left.HasValue)) { SubmitAuthoring(data, fingers); return; }
#endif
            if (data.TwoHand && weight > 0f)
            {
                targets.SetAnchor(spread, charging ? actionGrip : selectedGrip);
                float reach = spread > 0f ? weight : 0f;
                targets.Set(AvatarIKGoal.RightHand, AvatarHandSource.Item, target, reach, reach, AvatarArmIK.MaximumReach, fingers);
                targets.Set(AvatarIKGoal.LeftHand, AvatarHandSource.Item, leftTarget, reach, reach, AvatarArmIK.MaximumReach, fingers);
            }
            else
            {
                targets.ClearAnchor();
                targets.Set(AvatarIKGoal.RightHand, AvatarHandSource.Item, target, 0f, 0f, fingers: fingers);
                if (data.HoldMode == ItemHoldMode.Slingshot)
                    targets.Set(AvatarIKGoal.LeftHand, AvatarHandSource.Item, leftTarget, 0f, 0f, fingers: clips.GripFingers);
                else targets.Clear(AvatarIKGoal.LeftHand, AvatarHandSource.Item);
            }
            targetInstalled = true;
        }

        private void SubmitFollow()
        {
            if (!hands.Releasing) { ClearTargets(); return; }
            bool available = tracking && input.ProjectileAvailable;
            hands.Sample(HeldItemPoseCalculation.Compose(input.Projectile, leftInItem),
                HeldItemPoseCalculation.Compose(input.Projectile, rightInItem),
                available, releaseUnavailable || tracking && !available, lastBody, actionData.Class, input.EnvironmentMask, age, null, null);
            if (!hands.Following) { tracking = false; releaseUnavailable = true; }
            HandHoldPresentation.Submit(avatar.HandTargets, AvatarHandSource.Item, leftTarget, target, hands.Left, hands.Right,
                hands.LeftWeight, hands.RightWeight, HandHoldPresentation.Reach(actionData.Class), avatar.Registry.Animations.OpenFingers,
                !(hands.Following && available));
            targetInstalled = true;
        }

        internal Vector3 CommitHands(AvatarBinding binding)
        {
            if (!running || binding == null && !hasBody) return Vector3.zero;
            bool charging = action.State == ItemActionState.Charging;
            var data = charging ? actionData : selectedData;
            var grip = charging ? actionGrip : selectedGrip;
            bool showing = selectedId != 0 && CanShowHeldItem;
            Pose palm, left, item, frame;
            if (binding != null)
            {
                palm = binding.Palm(true); left = binding.Palm(false);
                item = ItemPose(binding, data, grip);
                frame = data.TwoHand ? HeldItemPoseCalculation.Compose(item, HeldItemPoseCalculation.Inverse(grip)) : palm;
            }
            else
            {
                frame = HeldItemPoseCalculation.FallbackFrame(lastBody, data);
                item = HeldItemPoseCalculation.Compose(frame, grip);
                Vector3 half = frame.rotation * Vector3.right * (spread * 0.5f);
                palm = new Pose(frame.position + half, frame.rotation); left = new Pose(frame.position - half, frame.rotation);
            }
            Pose requestedRight = palm, requestedLeft = left;
            if (data.TwoHand && spread > 0f)
            {
                Vector3 half = frame.rotation * Vector3.right * (spread * 0.5f);
                requestedRight = new Pose(frame.position + half, palm.rotation);
                requestedLeft = new Pose(frame.position - half, left.rotation);
            }
            bool resolve = showing && input.FirstPerson;
#if UNITY_INCLUDE_INSTRUMENTATION
            resolve &= Authoring == null;
            if (Authoring != null && binding != null)
            {
                if (Authoring.Right.HasValue) requestedRight = AvatarHandTargets.Rebase(Authoring.Right.Value, Pose.identity, binding.Body);
                if (Authoring.Left.HasValue) requestedLeft = AvatarHandTargets.Rebase(Authoring.Left.Value, Pose.identity, binding.Body);
            }
#endif
            bool rightUnreachable = !Blending && data.TwoHand && !InHandReach(requestedRight, true);
            bool leftUnreachable = !Blending && data.TwoHand && !InHandReach(requestedLeft, false);
            Vector3 correction = Vector3.zero;
            bool clear = true;
            if (resolve)
            {
                clear = ItemReleaseClearance.TryResolve(item, input.Aim.position,
                    data.TwoHand ? data.Sphere : new ItemReleaseSphere(Vector3.zero, data.ReleaseRadius),
                    lastBody.Rotation, input.EnvironmentMask, out var allowed);
                if (clear) correction = allowed.position - item.position;
                if (correction.sqrMagnitude < 0.000001f) correction = Vector3.zero;
                palm.position += correction; left.position += correction; item.position += correction;
                requestedRight.position += correction; requestedLeft.position += correction;
            }
            clearanceAdjusted = correction != Vector3.zero;
            GripFrame = new Pose(frame.position + correction, frame.rotation);
            fallback.SetPositionAndRotation(item.position, item.rotation);
            if (showing)
            {
                CommitItem?.Invoke(item);
                if (slingshot)
                {
                    var state = selectedId == action.WorldId ? action.State : ItemActionState.Idle;
                    float recovery = actionDefinition is SlingshotDefinition sling ? sling.RecoverySeconds : 0.5f;
                    float attach = data.Class && data.Class.View(input.FirstPerson).Hold ? ClassWeight(data.Class) : 0f;
                    slingshot.Evaluate(item, state, age, charge, attach, recovery, binding != null ? left : (Pose?)null, data.PouchOffset);
                }
            }
            Readout = new GripReachReadout
            {
                RequestedRight = requestedRight, RequestedLeft = requestedLeft,
                EvaluatedRight = palm, EvaluatedLeft = left, ActiveBlend = Blending, ClearanceAdjusted = clearanceAdjusted,
                RightUnreachable = rightUnreachable, LeftUnreachable = leftUnreachable,
                HasLeft = data.HoldMode != ItemHoldMode.OneHand
            };
            committed = new ReleaseSample { Item = selectedId, Generation = binding?.Generation ?? 0,
                Palm = palm, LeftPalm = left, ItemPose = item, Clear = clear,
                Progress = (byte)Mathf.RoundToInt((action.State == ItemActionState.Charging ? ChargeProgress(age) : 0f) * 255f) };
            return correction;
        }

        internal void Shift(Vector3 correction)
        {
            committed.Palm.position += correction; committed.LeftPalm.position += correction;
            committed.ItemPose.position += correction;
            fallback.position += correction;
            CommitItem?.Invoke(committed.ItemPose);
            if (slingshot) slingshot.Shift(correction);
        }

        private bool InHandReach(Pose palm, bool right)
        {
            var data = lastBody.Measurements;
            var rotation = palm.rotation * Quaternion.Inverse(right ? data.RightWristToPalmRotation : data.LeftWristToPalmRotation);
            var wrist = palm.position - rotation * ((right ? data.RightWristToPalmPosition : data.LeftWristToPalmPosition) * lastBody.Scale);
            return Vector3.Distance(wrist, right ? lastBody.Shoulder : lastBody.LeftShoulder) <=
                (right ? lastBody.ArmLength : lastBody.LeftArmLength) * AvatarArmIK.MaximumReach + 0.001f;
        }

        private void ClearTargets()
        {
#if UNITY_INCLUDE_INSTRUMENTATION
            avatar.HandTargets.Swivel[0] = avatar.HandTargets.Swivel[1] = 0f;
            avatar.HandTargets.RoundTripMuscles = false;
#endif
            avatar.HandTargets.ClearAnchor();
            avatar.HandTargets.Clear(AvatarIKGoal.LeftHand, AvatarHandSource.Item);
            if (!targetInstalled) return;
            HandHoldPresentation.Clear(avatar.HandTargets, AvatarHandSource.Item);
            targetInstalled = false;
        }

#if UNITY_INCLUDE_INSTRUMENTATION
        internal sealed class AuthoringPose
        {
            internal bool Charged;
            internal Pose? Right, Left;
            internal float RightSwivel, LeftSwivel;
        }
        internal AuthoringPose Authoring;

        private void SubmitAuthoring(in HeldItemPoseData data, AnimationClip fingers)
        {
            var targets = avatar.HandTargets;
            targets.ClearAnchor();
            targets.RoundTripMuscles = true;
            Override(AvatarIKGoal.RightHand, target, Authoring.Right, fingers);
            if (data.HoldMode == ItemHoldMode.OneHand) targets.Clear(AvatarIKGoal.LeftHand, AvatarHandSource.Item);
            else Override(AvatarIKGoal.LeftHand, leftTarget, Authoring.Left,
                data.HoldMode == ItemHoldMode.Slingshot ? avatar.Registry.Animations.GripFingers : fingers);
            targetInstalled = true;
        }

        private void Override(AvatarIKGoal hand, Transform palm, Pose? local, AnimationClip fingers)
        {
            var targets = avatar.HandTargets;
            if (!local.HasValue) { targets.Set(hand, AvatarHandSource.Item, palm, 0f, 0f, fingers: fingers); return; }
            var world = AvatarHandTargets.Rebase(local.Value, Pose.identity, targets.Body);
            palm.SetPositionAndRotation(world.position, world.rotation);
            targets.Set(hand, AvatarHandSource.Item, palm, 1f, 1f, AvatarArmIK.MaximumReach, fingers, bodyRelative: true);
        }
#endif
    }
}
