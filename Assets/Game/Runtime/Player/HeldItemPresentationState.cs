using UnityEngine;

namespace TwoBirds
{
    internal sealed class HeldItemPresentationState : System.IDisposable
    {
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
        private Transform target, fallback, leftTarget;
        private SlingshotPresentation slingshot;
        private bool SlingshotRecovery => action.State == ItemActionState.Recovering && actionDefinition is SlingshotDefinition;
        private ItemDefinition selectedDefinition, actionDefinition;
        private HeldItemPoseData selectedData, actionData;
        private ItemActionSnapshot action;
        private HeldItemBodyFrame lastBody;
        private uint selectedId, preparedId;
        private readonly HandHoldPresentation hands = new();
        private readonly float[] weights = new float[3], startWeights = new float[3];
        private float charge, startCharge, releaseCharge;
        private ItemHoldMode chargeMode;
        private Pose preparedLeft, preparedRight;
        private double age, clock, blendStart;
        private float blendDuration;
        private bool running = true, hasBody, blending, prepared, tracking, submitted, targetInstalled;
        private bool releaseUnavailable;
        private int advancedFrame = -1;
        internal float HeavyFrameWeight => running && input.CanEquip ? weights[(int)ItemHoldMode.TwoHand] : 0f;
        internal float ArmWeight => running && input.CanEquip ? weights[0] + weights[1] + weights[2] : 0f;
        private static float Ease(float progress) => LeanTween.easeOutSine(0f, 1f, Mathf.Clamp01(progress));

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
                blending = tracking = false; charge = 0f;
                hands.Reset(); ClearTargets();
            }
        }
        internal bool RecoveryFinished => action.State == ItemActionState.Recovering && actionDefinition && submitted &&
            age >= (actionDefinition is SlingshotDefinition sling ? sling.RecoverySeconds :
                Mathf.Max(0f, actionData.Poses.MaximumFollowDuration) + Mathf.Max(0f, actionData.Poses.EndPosePauseDuration) +
                Mathf.Max(0f, actionData.Poses.ReturnBlendDuration));
        internal void Advance() { CurrentAge(); RefreshArms(); advancedFrame = Time.frameCount; }
        internal HeldItemBodyFrame LastBody => lastBody;
        internal SlingshotPresentation Slingshot => slingshot;
        internal Transform RightTarget => target;
        internal Transform LeftTarget => leftTarget;
        internal GripReachReadout Readout { get; private set; }
        private bool clearanceAdjusted;
        internal bool Blending => blending || action.State == ItemActionState.Recovering;
        internal void InvalidateBinding() { Bind(null); preparedBody = null; committed = default; }
        internal void StageRelease()
        {
            prepared = true; preparedId = committed.Item;
            preparedLeft = committed.LeftPalm; preparedRight = committed.Palm;
        }
        public void Dispose()
        {
            running = false; ClearTargets(); avatar.HandTargets.SetArms(default);
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
                if (selectedDefinition && selectedData.TwoHand && CanShowHeldItem)
                {
                    if (boundBinding != null)
                    {
                        var item = boundBinding.AnchoredItem ?? HeldItemPoseCalculation.TwoHandAnchor(boundBinding.Palm(true), boundBinding.Palm(false),
                            selectedData.RightContact, selectedData.LeftContact, selectedData.PrefabScale);
                        fallback.SetPositionAndRotation(item.position, item.rotation);
                    }
                    else if (TryBody(out var body, out _))
                    {
                        var item = HeldItemPoseCalculation.Fallback(body, selectedData);
                        fallback.SetPositionAndRotation(item.position, item.rotation);
                    }
                }
                return fallback;
            }
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
        internal HeldItemPoseData Grip(ItemDefinition definition) => definition == selectedDefinition ? selectedData : new HeldItemPoseData(definition, defaults);

        private void Bind(AvatarBinding binding)
        {
            boundBinding = binding;
            boundSettings = binding?.Settings;
            committed = default;
#if UNITY_INCLUDE_INSTRUMENTATION
            if (Edit != null) Edit.Seeded = false;
#endif
        }

        private void SelectionChanged()
        {
            if (!running) return;
            uint id = input.SelectedId;
            var definition = input.SelectedDefinition;
            if (id == selectedId && definition == selectedDefinition) return;
            selectedId = id;
            selectedDefinition = definition;
            if (definition) selectedData = new HeldItemPoseData(definition, defaults);
            if (action.State == ItemActionState.Recovering && !SlingshotRecovery)
            { if (hands.Stage == HandHoldPresentation.RecoveryStage.Return) BeginReturn(); }
            else BeginBlend(definition ? selectedData.Poses.ReturnBlendDuration : blendDuration);
        }

        internal void RefreshContent()
        {
            if (selectedDefinition) selectedData = new HeldItemPoseData(selectedDefinition, defaults);
            if (actionDefinition) actionData = new HeldItemPoseData(actionDefinition, defaults);
            committed = default; prepared = false;
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
            float returnDuration = actionDefinition ? actionData.Poses.ReturnBlendDuration : blendDuration;
            action = next;
            age = !input.FirstPerson && input.HasAction ? input.ActionAge : 0d;
            clock = Time.unscaledTimeAsDouble;
            tracking = false;
            releaseUnavailable = false;
            var previousDefinition = actionDefinition;
            actionDefinition = input.ActionDefinition;
            if (actionDefinition && (action.State != ItemActionState.Recovering || previousDefinition != actionDefinition))
                actionData = new HeldItemPoseData(actionDefinition, defaults);
            if (action.State == ItemActionState.Charging) chargeMode = actionData.HoldMode;
            else if (SlingshotRecovery)
            {
                submitted = !input.FirstPerson;
                prepared = blending = false;
                chargeMode = ItemHoldMode.Slingshot;
                releaseCharge = charge = Ease(action.ReleaseArcProgress / 255f);
                hands.Reset();
            }
            else if (action.State == ItemActionState.Recovering)
            {
                submitted = !input.FirstPerson;
                blending = false;
                chargeMode = actionData.HoldMode;
                releaseCharge = charge = Ease(action.ReleaseArcProgress / 255f);
                StartFollow();
                for (int i = 0; i < 3; i++) startWeights[i] = i == (int)chargeMode ? 1f : 0f;
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
            ? Mathf.Clamp01((float)(elapsed / actionDefinition.ThrowChargeTime)) : actionData.Poses.ChargePoseDuration <= 0f ? 1f :
            Mathf.Clamp01((float)(elapsed / actionData.Poses.ChargePoseDuration));

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
            System.Array.Copy(weights, startWeights, 3); startCharge = charge;
            blendStart = Time.unscaledTimeAsDouble; blending = blendDuration > 0f;
        }

        private void BeginReturn()
        {
            System.Array.Copy(weights, startWeights, 3); startCharge = charge;
            hands.Retarget(lastBody, age);
        }

        private void StartFollow()
        {
            hands.Reset();
            if (!TryBody(out var body, out _)) return;
            Pose left, right;
            if (prepared && preparedId == action.WorldId) { left = preparedLeft; right = preparedRight; }
            else if (committed.Item == action.WorldId) { left = committed.LeftPalm; right = committed.Palm; }
            else if (boundBinding != null) { left = boundBinding.Palm(false); right = boundBinding.Palm(true); }
            else return;
            lastBody = body;
            hands.BeginRelease(left, right, body, actionData.TwoHand);
        }

        private void RefreshArms()
        {
#if UNITY_INCLUDE_INSTRUMENTATION
            if (Edit != null)
            {
                for (int i = 0; i < 3; i++) weights[i] = i == (int)Edit.Mode ? 1f : 0f;
                charge = Edit.Charged ? 1f : 0f; chargeMode = Edit.Mode;
                return;
            }
#endif
            int selected = selectedDefinition && input.CanEquip ? (int)selectedData.HoldMode : -1;
            float t;
            if (action.State == ItemActionState.Recovering && !SlingshotRecovery && actionDefinition)
            {
                t = Mathf.SmoothStep(0f, 1f, hands.ReturnProgress(actionData.Poses, age));
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
            for (int i = 0; i < 3; i++) weights[i] = Mathf.Lerp(startWeights[i], i == selected ? 1f : 0f, t);
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
            bool editing = false;
#if UNITY_INCLUDE_INSTRUMENTATION
            editing = Edit != null;
            targets.Swivel[0] = editing ? Edit.LeftSwivel : 0f;
            targets.Swivel[1] = editing ? Edit.RightSwivel : 0f;
#endif
            var placement = boundBinding != null ? avatar.Input : input.Placement;
            targets.SetArms(new AvatarArmPose
            {
                Settings = defaults, OneHand = weights[0], TwoHand = weights[1], Slingshot = weights[2],
                Charge = charge, ChargeMode = chargeMode,
                Pitch = input.FirstPerson || editing ? 0f : Mathf.Clamp(placement.LookPitch, -40f, 50f) * charge
            });
            if (action.State == ItemActionState.Recovering && !SlingshotRecovery) { targets.ClearAnchor(); SubmitFollow(); return; }
#if UNITY_INCLUDE_INSTRUMENTATION
            if (editing) { SubmitEdit(); return; }
#endif
            if (!selectedDefinition || !CanShowHeldItem) { ClearTargets(); return; }
            var data = action.State == ItemActionState.Charging ? actionData : selectedData;
            var clips = avatar.Registry.Animations;
            var fingers = data.Fingers ? data.Fingers : clips.GripFingers;
            float weight = weights[(int)data.HoldMode];
            if (data.TwoHand && weight > 0f)
            {
                targets.SetAnchor(data.RightContact, data.LeftContact, data.PrefabScale);
                targets.Set(AvatarIKGoal.RightHand, AvatarHandSource.Item, target, weight, weight, AvatarArmIK.MaximumReach, fingers);
                targets.Set(AvatarIKGoal.LeftHand, AvatarHandSource.Item, leftTarget, weight, weight, AvatarArmIK.MaximumReach, fingers);
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
            hands.Sample(HeldItemPoseCalculation.PalmFromItem(input.Projectile, actionData.LeftContact, actionData.PrefabScale),
                HeldItemPoseCalculation.PalmFromItem(input.Projectile, actionData.RightContact, actionData.PrefabScale),
                available, releaseUnavailable || tracking && !available, lastBody, actionData.Poses, input.EnvironmentMask, age, null, null);
            if (!hands.Following) { tracking = false; releaseUnavailable = true; }
            HandHoldPresentation.Submit(avatar.HandTargets, AvatarHandSource.Item, leftTarget, target, hands.Left, hands.Right,
                hands.LeftWeight, hands.RightWeight, HandHoldPresentation.Reach(actionData.Poses), avatar.Registry.Animations.OpenFingers,
                !(hands.Following && available));
            targetInstalled = true;
        }

        internal Vector3 CommitHands(AvatarBinding binding)
        {
            if (!running || binding == null && !hasBody) return Vector3.zero;
            var data = action.State == ItemActionState.Charging ? actionData : selectedData;
            bool showing = selectedId != 0 && CanShowHeldItem;
            Pose palm, left, item;
            if (binding != null)
            {
                palm = binding.Palm(true); left = binding.Palm(false);
                item = !data.TwoHand ? HeldItemPoseCalculation.ItemFromPalm(palm, data.RightContact, data.PrefabScale) :
                    binding.AnchoredItem ?? HeldItemPoseCalculation.TwoHandAnchor(palm, left, data.RightContact, data.LeftContact, data.PrefabScale);
            }
            else
            {
                item = HeldItemPoseCalculation.Fallback(lastBody, data);
                palm = HeldItemPoseCalculation.PalmFromItem(item, data.RightContact, data.PrefabScale);
                left = HeldItemPoseCalculation.PalmFromItem(item, data.LeftContact, data.PrefabScale);
            }
            Pose requestedRight = data.TwoHand ? HeldItemPoseCalculation.PalmFromItem(item, data.RightContact, data.PrefabScale) : palm;
            Pose requestedLeft = data.TwoHand ? HeldItemPoseCalculation.PalmFromItem(item, data.LeftContact, data.PrefabScale) : left;
            bool rightUnreachable = !Blending && data.TwoHand && !InHandReach(requestedRight, true);
            bool leftUnreachable = !Blending && data.TwoHand && !InHandReach(requestedLeft, false);
            Vector3 correction = Vector3.zero;
            bool clear = true, resolve = showing && input.FirstPerson;
#if UNITY_INCLUDE_INSTRUMENTATION
            resolve &= Edit == null;
#endif
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
            fallback.SetPositionAndRotation(data.TwoHand ? item.position : palm.position, data.TwoHand ? item.rotation : palm.rotation);
            if (showing)
            {
                CommitItem?.Invoke(item);
                if (slingshot)
                {
                    var state = selectedId == action.WorldId ? action.State : ItemActionState.Idle;
                    float recovery = actionDefinition is SlingshotDefinition sling ? sling.RecoverySeconds : 0.5f;
                    float attach = defaults.Slingshot.Hold(input.FirstPerson) ? weights[(int)ItemHoldMode.Slingshot] : 0f;
                    slingshot.Evaluate(item, state, age, charge, attach, recovery, binding != null ? left : (Pose?)null,
                        data.PullingContact, data.PrefabScale);
                }
            }
#if UNITY_INCLUDE_INSTRUMENTATION
            if (Edit != null && binding != null && !Edit.Seeded)
            {
                Edit.Right = AvatarHandTargets.Rebase(binding.Palm(true), binding.Body, Pose.identity);
                Edit.Left = AvatarHandTargets.Rebase(binding.Palm(false), binding.Body, Pose.identity);
                Edit.Seeded = true;
            }
#endif
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
#endif
            avatar.HandTargets.ClearAnchor();
            avatar.HandTargets.Clear(AvatarIKGoal.LeftHand, AvatarHandSource.Item);
            if (!targetInstalled) return;
            HandHoldPresentation.Clear(avatar.HandTargets, AvatarHandSource.Item);
            targetInstalled = false;
        }

#if UNITY_INCLUDE_INSTRUMENTATION
        internal sealed class PoseEdit
        {
            internal ItemHoldMode Mode;
            internal bool Charged, Seeded;
            internal Pose Right, Left;
            internal float RightSwivel, LeftSwivel;
        }
        internal PoseEdit Edit;

        private void SubmitEdit()
        {
            var targets = avatar.HandTargets;
            targets.ClearAnchor();
            var fingers = selectedDefinition && selectedDefinition.GripFingers ? selectedDefinition.GripFingers : avatar.Registry.Animations.GripFingers;
            float weight = Edit.Seeded ? 1f : 0f;
            var right = AvatarHandTargets.Rebase(Edit.Right, Pose.identity, targets.Body);
            target.SetPositionAndRotation(right.position, right.rotation);
            targets.Set(AvatarIKGoal.RightHand, AvatarHandSource.Item, target, weight, weight, AvatarArmIK.MaximumReach, fingers, bodyRelative: true);
            if (Edit.Mode == ItemHoldMode.OneHand) targets.Clear(AvatarIKGoal.LeftHand, AvatarHandSource.Item);
            else
            {
                var left = AvatarHandTargets.Rebase(Edit.Left, Pose.identity, targets.Body);
                leftTarget.SetPositionAndRotation(left.position, left.rotation);
                targets.Set(AvatarIKGoal.LeftHand, AvatarHandSource.Item, leftTarget, weight, weight, AvatarArmIK.MaximumReach, fingers, bodyRelative: true);
            }
            targetInstalled = true;
        }
#endif
    }
}
