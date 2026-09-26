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
        private readonly Transform target, leftTarget, rightHint, leftHint;
        private readonly GripSlotRig rig;
        private GripSlotRig.Slot selectedSlot;
        private SlingshotPresentation slingshot;
        private bool SlingshotRecovery => action.State == ItemActionState.Recovering && actionDefinition is SlingshotDefinition;
        private bool Following => action.State == ItemActionState.Recovering && !SlingshotRecovery;
        private ItemDefinition selectedDefinition, actionDefinition;
        private HoldSlot selectedHold, actionHold;
        private HeldItemPoseData selectedData, actionData;
        private Pose rightInItem, leftInItem;
        private ItemActionSnapshot action;
        private HeldItemBodyFrame lastBody;
        private uint selectedId, preparedId, chargeItem;
        private readonly HandHoldPresentation hands = new();
        private float charge, startCharge, releaseCharge, releaseDraw, grab, startGrab, draw, startDraw, cancelDuration;
        private float holdWeight, leftWeight, startRightWeight, startLeftWeight, switchBlend = 1f;
        private float rightHintWeight, leftHintWeight, sourceRightHintWeight, sourceLeftHintWeight, followRightHintWeight, followLeftHintWeight;
        private Pose sourceRight, sourceLeft, releasedLeft;
        private Vector3 sourceRightHint, sourceLeftHint, followRightHint, followLeftHint;
        private Pose preparedLeft, preparedRight;
        private double age, clock, blendStart;
        private float blendDuration;
        private bool running = true, hasBody, blending, canceling, prepared, tracking, submitted, targetInstalled, shown, holdRelease;
        private bool releaseUnavailable;
        private int advancedFrame = -1;
        internal float HoldWeight => running && input.CanEquip ? holdWeight : 0f;
        internal Transform Attachment => selectedSlot.ItemAnchor;
        private static float Ease(float progress) => LeanTween.easeOutSine(0f, 1f, Mathf.Clamp01(progress));
        private static float ReturnDuration(in HeldItemPoseData data) => data.Slot ? data.Slot.ReturnBlendDuration : 0f;

        internal HeldItemPresentationState(AvatarPresentation avatar, Transform host)
        {
            this.avatar = avatar;
            target = Create("RightItemHandTarget"); leftTarget = Create("LeftItemHandTarget");
            rightHint = Create("RightItemElbowHint"); leftHint = Create("LeftItemElbowHint");
            rig = new GripSlotRig(host);
            selectedSlot = rig.Hand;
            Transform Create(string name)
            {
                var created = new GameObject(name).transform;
                created.SetParent(host, false);
                return created;
            }
        }

        internal void SetInput(in HeldItemPresentationInput value, SlingshotPresentation visual)
        {
            input = value; resolvedSettings = avatar.Resolved?.Settings;
            if (slingshot != visual) { if (slingshot) slingshot.ResetPose(); slingshot = visual; }
            SelectionChanged(); ActionChanged();
            FindProjectile();
            if (!input.CanEquip || !input.CanCharge)
            {
                blending = tracking = canceling = false; charge = grab = draw = 0f; chargeItem = 0;
                hands.Reset(); ClearTargets();
            }
        }
        internal bool RecoveryFinished => action.State == ItemActionState.Recovering && actionDefinition && submitted &&
            age >= (actionDefinition is SlingshotDefinition sling ? sling.RecoverySeconds :
                Mathf.Max(0f, actionData.Slot.MaximumFollowDuration) + Mathf.Max(0f, actionData.Slot.EndPosePauseDuration) +
                Mathf.Max(0f, actionData.Slot.ReturnBlendDuration));
        internal void Advance() { CurrentAge(); RefreshCharge(); RefreshWeights(); advancedFrame = Time.frameCount; }
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
            running = false; ClearTargets();
            Watch(ref selectedDefinition, null, actionDefinition); Watch(ref actionDefinition, null, null);
            Watch(ref selectedHold, null, actionHold); Watch(ref actionHold, null, null);
            if (slingshot) slingshot.ResetPose();
            UnityEngine.Object.Destroy(target.gameObject); UnityEngine.Object.Destroy(leftTarget.gameObject);
            UnityEngine.Object.Destroy(rightHint.gameObject); UnityEngine.Object.Destroy(leftHint.gameObject);
            rig.Dispose();
        }

        internal bool CanShowHeldItem => running && input.CanEquip && input.HasAction && !input.Emoting &&
            (input.Action.State != ItemActionState.Recovering ||
                input.ActionDefinition is SlingshotDefinition);
        internal bool ReadyForUse => running && input.CanEquip && input.CanCharge &&
            input.Action.State != ItemActionState.Recovering;
        private bool ReturnsToItem => selectedDefinition && selectedId != action.WorldId && running && input.CanEquip && !input.Emoting;
        private bool ChargeActive => Frozen || selectedId != 0 && selectedId == chargeItem;

        private HeldItemBodyFrame Body(AvatarSettings settings)
        {
            var placement = boundBinding != null ? avatar.Input : input.Placement;
            bool attached = placement.Seated || placement.Carried && !placement.ReleasePreview;
            Quaternion torso = boundBinding != null ? avatar.transform.rotation : attached ? placement.Facing.rotation :
                Quaternion.Euler(0f, placement.Facing.rotation.eulerAngles.y, 0f);
            float seated = boundBinding != null ? avatar.State.Weights[(int)AvatarPose.Seated] : placement.Seated ? 1f : 0f;
            return new HeldItemBodyFrame(settings, avatar.Registry, placement, torso, seated);
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
            ApplySlot();
        }

        // One subscription per asset: other is the paired field, which keeps it when both hold the same asset.
        private void Watch(ref ItemDefinition current, ItemDefinition next, ItemDefinition other)
        {
            if (current == next) return;
            if (current && current != other) current.ContentChanged -= RefreshContent;
            current = next;
            if (current && current != other) current.ContentChanged += RefreshContent;
        }

        private void Watch(ref HoldSlot current, HoldSlot next, HoldSlot other)
        {
            if (current == next) return;
            if (current && current != other) current.ContentChanged -= RefreshContent;
            current = next;
            if (current && current != other) current.ContentChanged += RefreshContent;
        }

        private void WatchSlots()
        {
            Watch(ref selectedHold, selectedDefinition ? selectedDefinition.HoldSlot : null, actionHold);
            Watch(ref actionHold, actionDefinition ? actionDefinition.HoldSlot : null, selectedHold);
        }

        private AvatarId AvatarKey => boundBinding != null ? boundBinding.Id : avatar.Resolved != null ? avatar.Resolved.Id : default;
        private float Scale => GripPoses.Scale(boundSettings ? boundSettings : resolvedSettings);

        private void ApplySlot(bool force = false)
        {
            resolvedSettings = avatar.Resolved?.Settings;
            if (!selectedDefinition) return;
            selectedSlot = rig.For(selectedData.Mode);
            rig.Apply(selectedSlot, selectedData.Slot, selectedDefinition, AvatarKey, input.FirstPerson, Scale, force);
        }

        private void SelectionChanged()
        {
            if (!running) return;
            uint id = input.SelectedId;
            var definition = input.SelectedDefinition;
            if (id == selectedId && definition == selectedDefinition) return;
            float previousDuration = selectedDefinition ? ReturnDuration(selectedData) : blendDuration;
            selectedId = id;
            Watch(ref selectedDefinition, definition, actionDefinition);
            WatchSlots();
            if (definition) selectedData = new HeldItemPoseData(definition);
            if (Following)
            {
                ApplySlot();
                if (hands.Stage == HandHoldPresentation.RecoveryStage.Return) BeginReturn();
                return;
            }
            BeginBlend(definition ? ReturnDuration(selectedData) : previousDuration);
            ApplySlot();
        }

        internal void RefreshContent()
        {
            WatchSlots();
            if (selectedDefinition) selectedData = new HeldItemPoseData(selectedDefinition);
            if (actionDefinition) actionData = new HeldItemPoseData(actionDefinition);
            ApplySlot();
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
            var previousData = actionData;
            action = next;
            age = !input.FirstPerson && input.HasAction ? input.ActionAge : 0d;
            clock = Time.unscaledTimeAsDouble;
            tracking = false;
            releaseUnavailable = false;
            canceling = false;
            var previousDefinition = actionDefinition;
            Watch(ref actionDefinition, input.ActionDefinition, selectedDefinition);
            WatchSlots();
            if (actionDefinition && (action.State != ItemActionState.Recovering || previousDefinition != actionDefinition))
                actionData = new HeldItemPoseData(actionDefinition);
            if (action.State == ItemActionState.Charging) chargeItem = action.WorldId;
            else if (SlingshotRecovery)
            {
                submitted = !input.FirstPerson;
                prepared = blending = false;
                chargeItem = action.WorldId;
                releaseCharge = charge = Ease(action.ReleaseArcProgress / 255f);
                releaseDraw = draw;
                // The draw hand stays where it let go while the pouch snaps back.
                holdRelease = previous.State == ItemActionState.Charging && previous.WorldId == action.WorldId;
                if (holdRelease) releasedLeft = Local(rig.Frame, leftTarget);
                hands.Reset();
            }
            else if (action.State == ItemActionState.Recovering)
            {
                submitted = !input.FirstPerson;
                blending = false;
                chargeItem = 0;
                startRightWeight = holdWeight; startLeftWeight = leftWeight;
                StartFollow();
                prepared = false;
                FindProjectile();
            }
            else
            {
                prepared = false;
                if (previous.State == ItemActionState.Recovering)
                { blending = false; hands.Reset(); charge = grab = draw = 0f; chargeItem = 0; }
                else if (previous.State == ItemActionState.Charging)
                {
                    canceling = true; chargeItem = previous.WorldId;
                    startCharge = charge; startGrab = grab; startDraw = draw;
                    cancelDuration = previousData.Mode == HoldSlotMode.Slingshot ? previousData.RecoverySeconds : previousData.ChargeDuration;
                }
                else chargeItem = 0;
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

        private float ChargeProgress(double elapsed) =>
            actionData.ChargeDuration <= 0f ? 1f : Mathf.Clamp01((float)(elapsed / actionData.ChargeDuration));

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
            var frame = rig.Frame;
            sourceRight = Local(frame, target); sourceLeft = Local(frame, leftTarget);
            sourceRightHint = frame.InverseTransformPoint(rightHint.position);
            sourceLeftHint = frame.InverseTransformPoint(leftHint.position);
            sourceRightHintWeight = rightHintWeight; sourceLeftHintWeight = leftHintWeight;
            startRightWeight = holdWeight; startLeftWeight = leftWeight;
            blendStart = Time.unscaledTimeAsDouble; blending = blendDuration > 0f;
        }

        private void BeginReturn()
        {
            startRightWeight = holdWeight; startLeftWeight = leftWeight;
            hands.Retarget(lastBody, age);
            CaptureFollowHints();
        }

        private void CaptureFollowHints()
        {
            followRightHint = rig.Frame.InverseTransformPoint(rightHint.position);
            followLeftHint = rig.Frame.InverseTransformPoint(leftHint.position);
            followRightHintWeight = rightHintWeight; followLeftHintWeight = leftHintWeight;
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
            GripPoses.TryResolve(actionData.Slot, actionDefinition, AvatarKey, GripTarget.RightHand, input.FirstPerson, out rightInItem, out _);
            GripPoses.TryResolve(actionData.Slot, actionDefinition, AvatarKey, GripTarget.LeftHand, input.FirstPerson, out leftInItem, out _);
            CaptureFollowHints();
            lastBody = body;
            hands.BeginRelease(left, right, body, actionData.Heavy);
        }

        private void RefreshCharge()
        {
#if UNITY_INCLUDE_INSTRUMENTATION
            if (Frozen)
            {
                charge = grab = draw = AuthoringPhase == GripAuthoringPhase.Charged ? 1f : 0f;
                blending = canceling = false;
                return;
            }
#endif
            if (action.State == ItemActionState.Charging && actionDefinition)
            {
                charge = actionData.ChargeDuration <= 0f ? 1f : Ease((float)(age / actionData.ChargeDuration));
                if (actionData.Mode != HoldSlotMode.Slingshot) return;
                float grabSeconds = actionData.GrabSeconds, span = actionDefinition.ThrowChargeTime - grabSeconds;
                grab = grabSeconds <= 0f ? 1f : Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((float)(age / grabSeconds)));
                draw = span <= 0f ? 1f : Ease((float)((age - grabSeconds) / span));
            }
            else if (canceling)
            {
                float t = cancelDuration <= 0f ? 1f : Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((float)(age / cancelDuration)));
                charge = Mathf.Lerp(startCharge, 0f, t); grab = Mathf.Lerp(startGrab, 0f, t); draw = Mathf.Lerp(startDraw, 0f, t);
                if (t >= 1f) { canceling = false; chargeItem = 0; }
            }
            else if (SlingshotRecovery)
            {
                float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((float)(age / Mathf.Max(0.01f, actionData.RecoverySeconds))));
                charge = Mathf.Lerp(releaseCharge, 0f, t); grab = 1f - t; draw = 0f;
            }
            else charge = grab = draw = 0f;
        }

        private void RefreshWeights()
        {
            if (Following)
            {
                float r = actionData.Slot ? Mathf.SmoothStep(0f, 1f, hands.ReturnProgress(actionData.Slot, age)) : 1f;
                bool returning = ReturnsToItem;
                holdWeight = Mathf.Lerp(startRightWeight, returning ? 1f : 0f, r);
                leftWeight = Mathf.Lerp(startLeftWeight, returning && selectedSlot.Mode == HoldSlotMode.Heavy ? 1f : 0f, r);
                shown = false;
                return;
            }
            bool show = selectedDefinition && CanShowHeldItem;
            if (show != shown) { shown = show; BeginBlend(ReturnDuration(selectedData)); }
            float progress = blending && blendDuration > 0f ? Mathf.Clamp01((float)((Time.unscaledTimeAsDouble - blendStart) / blendDuration)) : 1f;
            if (progress >= 1f) blending = false;
            switchBlend = Mathf.SmoothStep(0f, 1f, progress);
            float leftGoal = !show ? 0f : selectedData.Mode == HoldSlotMode.Heavy ? 1f :
                selectedData.Mode == HoldSlotMode.Slingshot && ChargeActive ? grab : 0f;
            holdWeight = Mathf.Lerp(startRightWeight, show ? 1f : 0f, switchBlend);
            leftWeight = Mathf.Lerp(startLeftWeight, leftGoal, switchBlend);
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
            if (!input.CanEquip || !input.HasAction) { ClearTargets(); return; }
            SubmitTargets();
        }

        private void SubmitTargets()
        {
            if (Following) { SubmitFollow(); return; }
            if (holdWeight <= 0f && leftWeight <= 0f) { ClearTargets(); return; }
            var targets = avatar.HandTargets;
            var clips = avatar.Registry.Animations;
            var fingers = selectedData.Fingers ? selectedData.Fingers : clips.GripFingers;
            targets.Set(AvatarIKGoal.RightHand, AvatarHandSource.Item, target, holdWeight, holdWeight, AvatarArmIK.MaximumReach, fingers,
                hint: rightHint, hintWeight: rightHintWeight);
            if (leftWeight > 0f)
                targets.Set(AvatarIKGoal.LeftHand, AvatarHandSource.Item, leftTarget, leftWeight, leftWeight, AvatarArmIK.MaximumReach,
                    selectedData.Mode != HoldSlotMode.Slingshot ? fingers : SlingshotRecovery ? clips.OpenFingers : clips.GripFingers,
                    hint: leftHint, hintWeight: leftHintWeight);
            else targets.Clear(AvatarIKGoal.LeftHand, AvatarHandSource.Item);
            targetInstalled = true;
        }

        private void SubmitFollow()
        {
            if (!hands.Releasing) { ClearTargets(); return; }
            bool available = tracking && input.ProjectileAvailable;
            Pose? destinationRight = null, destinationLeft = null;
            float destinationRightWeight = 0f, destinationLeftWeight = 0f;
            if (ReturnsToItem)
            {
                destinationRight = World(rig.Target(selectedSlot, GripTarget.RightHand)); destinationRightWeight = 1f;
                if (selectedSlot.Mode == HoldSlotMode.Heavy)
                { destinationLeft = World(rig.Target(selectedSlot, GripTarget.LeftHand)); destinationLeftWeight = 1f; }
            }
            hands.Sample(HeldItemPoseCalculation.Compose(input.Projectile, leftInItem),
                HeldItemPoseCalculation.Compose(input.Projectile, rightInItem),
                available, releaseUnavailable || tracking && !available, lastBody, actionData.Slot, input.EnvironmentMask, age,
                destinationLeft, destinationRight, destinationLeftWeight, destinationRightWeight);
            if (!hands.Following) { tracking = false; releaseUnavailable = true; }
            HandHoldPresentation.Submit(avatar.HandTargets, AvatarHandSource.Item, leftTarget, target, hands.Left, hands.Right,
                hands.LeftWeight, hands.RightWeight, HandHoldPresentation.Reach(actionData.Slot), avatar.Registry.Animations.OpenFingers,
                !(hands.Following && available), leftHint, rightHint, leftHintWeight, rightHintWeight);
            targetInstalled = true;
        }

        internal void PoseHands(AvatarBinding binding, Pose? frame = null)
        {
            if (!running || binding == null && !hasBody && !frame.HasValue) return;
            if (advancedFrame != Time.frameCount) Advance();
            rig.Place(frame ?? (binding != null
                ? new Pose(binding.GetBone(HumanBodyBones.Hips).position, binding.Animator.transform.rotation)
                : new Pose(lastBody.Hips, lastBody.Rotation)));
            var slot = selectedSlot;
            bool active = ChargeActive;
            float anchorCharge = active ? charge : 0f, pitch = 0f;
            Vector3 pivot = default;
            if (!frame.HasValue && !Frozen && anchorCharge > 0f)
            {
                var placement = boundBinding != null ? avatar.Input : input.Placement;
                pitch = Mathf.Clamp(placement.LookPitch, -40f, 50f);
                pivot = slot.Mode == HoldSlotMode.Heavy
                    ? binding != null ? binding.Body.position : lastBody.Center
                    : binding != null ? binding.GetBone(HumanBodyBones.RightUpperArm).position : lastBody.Shoulder;
            }
            rig.Anchor(slot, anchorCharge, active ? draw : 0f, slingshot ? slingshot.RestOffset : Vector3.zero, pitch, pivot);
#if UNITY_INCLUDE_INSTRUMENTATION
            if (rig.Locked)
            {
                rig.MirrorFollow(slot);
                if (binding != null) FollowAutomaticHints(binding);
            }
#endif
            if (Following) { PoseFollowHints(); return; }
            var frameTransform = rig.Frame;
            bool leftUsed = slot.Mode != HoldSlotMode.Hand;
            var right = World(rig.Target(slot, GripTarget.RightHand));
            var left = holdRelease && active && SlingshotRecovery ? ToWorld(frameTransform, releasedLeft) : World(rig.Target(slot, GripTarget.LeftHand));
            Vector3 leftPole = default;
            float leftPoleWeight = 0f;
            bool rightHinted = rig.Hint(slot, true, anchorCharge, pitch, pivot, out var rightPole, out float rightPoleWeight);
            bool leftHinted = leftUsed && rig.Hint(slot, false, anchorCharge, pitch, pivot, out leftPole, out leftPoleWeight);
            if (blending)
            {
                float t = switchBlend;
                right = Blend(ToWorld(frameTransform, sourceRight), right, startRightWeight > 0f ? t : 1f);
                left = leftUsed ? Blend(ToWorld(frameTransform, sourceLeft), left, startLeftWeight > 0f ? t : 1f) : ToWorld(frameTransform, sourceLeft);
                rightPole = BlendHint(frameTransform.TransformPoint(sourceRightHint), sourceRightHintWeight, rightPole, rightHinted, t);
                leftPole = BlendHint(frameTransform.TransformPoint(sourceLeftHint), sourceLeftHintWeight, leftPole, leftHinted, t);
                rightPoleWeight = Mathf.Lerp(sourceRightHintWeight, rightPoleWeight, t);
                leftPoleWeight = Mathf.Lerp(sourceLeftHintWeight, leftPoleWeight, t);
                rightHinted = leftHinted = true;
            }
            target.SetPositionAndRotation(right.position, right.rotation);
            leftTarget.SetPositionAndRotation(left.position, left.rotation);
            if (rightHinted) rightHint.position = rightPole;
            if (leftHinted) leftHint.position = leftPole;
            rightHintWeight = rightPoleWeight; leftHintWeight = leftPoleWeight;
        }

        private static Vector3 BlendHint(Vector3 source, float sourceWeight, Vector3 destination, bool defined, float t) =>
            !defined ? source : sourceWeight > 0f ? Vector3.Lerp(source, destination, t) : destination;

        private void PoseFollowHints()
        {
            float r = actionData.Slot ? Mathf.SmoothStep(0f, 1f, hands.ReturnProgress(actionData.Slot, age)) : 1f;
            Vector3 rightStart = rig.Frame.TransformPoint(followRightHint), leftStart = rig.Frame.TransformPoint(followLeftHint);
            bool destination = ReturnsToItem;
            float rightGoal = 0f, leftGoal = 0f;
            Vector3 rightPole = rightStart, leftPole = leftStart;
            if (destination && rig.Hint(selectedSlot, true, 0f, 0f, default, out var right, out float rightDestination))
            { rightPole = Vector3.Lerp(rightStart, right, r); rightGoal = rightDestination; }
            if (destination && selectedSlot.Mode == HoldSlotMode.Heavy &&
                rig.Hint(selectedSlot, false, 0f, 0f, default, out var left, out float leftDestination))
            { leftPole = Vector3.Lerp(leftStart, left, r); leftGoal = leftDestination; }
            rightHint.position = rightPole; leftHint.position = leftPole;
            rightHintWeight = Mathf.Lerp(followRightHintWeight, rightGoal, r);
            leftHintWeight = Mathf.Lerp(followLeftHintWeight, leftGoal, r);
        }

        internal Vector3 CommitHands(AvatarBinding binding)
        {
            if (!running || binding == null && !hasBody) return Vector3.zero;
            var data = selectedData;
            bool showing = selectedId != 0 && CanShowHeldItem;
            Pose item = World(selectedSlot.ItemAnchor);
            Pose requestedRight = World(target), requestedLeft = World(leftTarget);
            Pose palm = binding != null ? binding.Palm(true) : requestedRight;
            Pose left = binding != null ? binding.Palm(false) : requestedLeft;
            bool resolve = showing && input.FirstPerson && !Frozen;
            bool hasLeft = leftWeight > 0f;
            bool rightUnreachable = !Blending && showing && !InHandReach(requestedRight, true);
            bool leftUnreachable = !Blending && showing && hasLeft && !InHandReach(requestedLeft, false);
            Vector3 correction = Vector3.zero;
            bool clear = true;
            if (resolve)
            {
                clear = ItemReleaseClearance.TryResolve(item, input.Aim.position,
                    data.Heavy ? data.Sphere : new ItemReleaseSphere(Vector3.zero, data.ReleaseRadius),
                    lastBody.Rotation, input.EnvironmentMask, out var allowed);
                if (clear) correction = allowed.position - item.position;
                if (correction.sqrMagnitude < 0.000001f) correction = Vector3.zero;
                if (correction != Vector3.zero)
                {
                    rig.Frame.position += correction;
                    palm.position += correction; left.position += correction; item.position += correction;
                    requestedRight.position += correction; requestedLeft.position += correction;
                }
            }
            clearanceAdjusted = correction != Vector3.zero;
            if (showing)
            {
                CommitItem?.Invoke(item);
                if (slingshot)
                {
                    var state = selectedId == action.WorldId ? action.State : ItemActionState.Idle;
                    float recovery = actionDefinition is SlingshotDefinition sling ? sling.RecoverySeconds : 0.5f;
                    float pull = state == ItemActionState.Recovering ? releaseDraw : ChargeActive ? draw : 0f;
                    slingshot.Evaluate(item, state, age, pull, recovery, rig.For(HoldSlotMode.Slingshot).Pouch.position);
                }
            }
            Readout = new GripReachReadout
            {
                RequestedRight = requestedRight, RequestedLeft = requestedLeft,
                EvaluatedRight = palm, EvaluatedLeft = left, ActiveBlend = Blending, ClearanceAdjusted = clearanceAdjusted,
                RightUnreachable = rightUnreachable, LeftUnreachable = leftUnreachable, HasLeft = hasLeft
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
            rig.Frame.position += correction;
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
            avatar.HandTargets.Clear(AvatarIKGoal.LeftHand, AvatarHandSource.Item);
            if (!targetInstalled) return;
            HandHoldPresentation.Clear(avatar.HandTargets, AvatarHandSource.Item);
            targetInstalled = false;
        }

        private static Pose World(Transform transform) => new(transform.position, transform.rotation);
        private static Pose Local(Transform frame, Transform transform) =>
            new(frame.InverseTransformPoint(transform.position), Quaternion.Inverse(frame.rotation) * transform.rotation);
        private static Pose ToWorld(Transform frame, Pose local) => new(frame.TransformPoint(local.position), frame.rotation * local.rotation);
        private static Pose Blend(Pose from, Pose to, float t) =>
            new(Vector3.Lerp(from.position, to.position, t), Quaternion.Slerp(from.rotation, to.rotation, t));

#if UNITY_INCLUDE_INSTRUMENTATION
        internal GripAuthoringPhase AuthoringPhase;
        private bool Frozen => AuthoringPhase != GripAuthoringPhase.Live;
        internal bool AuthoringLocked { set => rig.Locked = value; }
        internal bool AuthoringMirror { set => rig.Mirror = value; }
        internal void ResetAuthored(GripTarget target) { if (selectedDefinition) rig.ResetAuthored(selectedSlot, target); }
        internal bool TryAuthored(GripTarget authored, out Transform transform, out Pose stored, out GripLayer layer, out bool dirty)
        {
            transform = null; stored = default; layer = GripLayer.None; dirty = false;
            return selectedDefinition && rig.TryAuthored(selectedSlot, authored, Scale, out transform, out stored, out layer, out dirty);
        }
        internal void ReapplyAuthored() => ApplySlot(true);

        private void FollowAutomaticHints(AvatarBinding binding)
        {
            var body = binding.Body.rotation;
            for (int i = 0; i < 2; i++)
            {
                bool right = i == 1;
                var upper = binding.GetBone(right ? HumanBodyBones.RightUpperArm : HumanBodyBones.LeftUpperArm);
                var lower = binding.GetBone(right ? HumanBodyBones.RightLowerArm : HumanBodyBones.LeftLowerArm);
                var hand = right ? target : leftTarget;
                Vector3 automatic = AvatarArmIK.AutoHint(upper.position, hand.position, body, right, Vector3.Distance(upper.position, lower.position));
                rig.FollowAutomaticHint(selectedSlot, right ? GripTarget.RightElbowHold : GripTarget.LeftElbowHold, automatic);
                rig.FollowAutomaticHint(selectedSlot, right ? GripTarget.RightElbowCharge : GripTarget.LeftElbowCharge, automatic);
            }
        }
#else
        private const bool Frozen = false;
#endif
    }
}
