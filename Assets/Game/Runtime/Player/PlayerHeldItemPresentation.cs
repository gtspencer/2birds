using UnityEngine;

namespace TwoBirds
{
    [DefaultExecutionOrder(175), DisallowMultipleComponent]
    public sealed class PlayerHeldItemPresentation : MonoBehaviour
    {
        private enum RecoveryStage : byte { Follow, Pause, Return, Finished }
        private PlayerEquipment equipment;
        private PlayerInventory inventory;
        private PlayerNetworkState networkState;
        private PlayerAvatarPresentation playerAvatar;
        private PlayerPresentation player;
        private PlayerSeating seating;
        private PlayerCarry carry;
        private WorldItemRegistry registry;
        private AvatarPresentation avatar;
        private AvatarSettings resolvedSettings, boundSettings;
        private AvatarBinding boundBinding;
        private HeldItemBodyFrame? preparedBody;
        private struct ReleaseSample
        {
            internal uint Item;
            internal ulong Generation;
            internal Pose Palm, ItemPose;
            internal byte Progress;
            internal bool Clear;
        }
        private ReleaseSample committed;
        private Pose correctedItem;
        private Transform target, fallback, followBone;
        private ItemDefinition selectedDefinition, actionDefinition, subscribedSelected, subscribedAction;
        private HeldItemPoseData selectedData, actionData;
        private ItemActionSnapshot action;
        private HeldItemPose pose, preparedPose;
        private HeldItemBodyFrame lastBody;
        private WorldItem projectile;
        private uint selectedId, preparedId;
        private Vector3 chargeStart, retainedPosition, returnStart;
        private Quaternion chargeRotation, retainedRotation, returnRotation;
        private double age, clock, blendStart, returnSegmentStart;
        private float weight, returnWeight, blendDuration;
        private RecoveryStage stage;
        private bool running, hasPose, blending, prepared, tracking, submitted, targetInstalled;
        private bool recoveryNeedsPose, releaseUnavailable;
        private int advancedFrame = -1;

        internal bool CanShowHeldItem => running && inventory.CanEquip && networkState.HasActionSnapshot &&
            networkState.ItemAction.State != ItemActionState.Recovering;
        internal bool ReadyForUse => running && inventory.CanEquip && networkState.CanCharge &&
            networkState.ItemAction.State != ItemActionState.Recovering;
        internal Transform Attachment => fallback;
        internal HeldItemPoseData Grip(ItemDefinition definition) => definition == selectedDefinition ? selectedData : registry.GetHeldPose(definition, inventory.IsOwner);

        private void Awake()
        {
            equipment = GetComponent<PlayerEquipment>();
            inventory = GetComponent<PlayerInventory>();
            networkState = GetComponent<PlayerNetworkState>();
            playerAvatar = GetComponent<PlayerAvatarPresentation>();
            player = GetComponent<PlayerPresentation>();
            seating = GetComponent<PlayerSeating>();
            carry = GetComponent<PlayerCarry>();
            avatar = playerAvatar.Presentation;
        }

        private void OnEnable()
        {
            if (equipment && equipment.IsClientInitialized) StartPresentation();
        }

        internal void StartPresentation()
        {
            if (running || !isActiveAndEnabled) return;
            registry = WorldItemRegistry.Instance;
            if (!registry) return;
            running = true;
            target = new GameObject("RightItemHandTarget").transform;
            target.SetParent(transform, false);
            fallback = new GameObject("HeldItemFallback").transform;
            fallback.SetParent(transform, false);
            Vector3 scale = transform.lossyScale;
            fallback.localScale = new Vector3(1f / scale.x, 1f / scale.y, 1f / scale.z);
            inventory.InventoryChanged += SelectionChanged;
            inventory.ControlPermissionsChanged += ContextChanged;
            networkState.ActionChanged += ActionChanged;
            seating.PresentationContextChanged += ContextChanged;
            carry.PresentationContextChanged += ContextChanged;
            registry.PresentationChanged += LifecycleChanged;
            registry.HeldDefaults.ContentChanged += PoseContentChanged;
            avatar.IdentityResolved += IdentityResolved;
            avatar.WillUnbind += WillUnbind;
            avatar.DidBind += DidBind;
            resolvedSettings = avatar.Resolved?.Settings;
            if (avatar.Binding != null) Bind(avatar.Binding);
            SelectionChanged();
            ActionChanged();
        }

        internal void StopPresentation()
        {
            if (!running) return;
            running = false;
            inventory.InventoryChanged -= SelectionChanged;
            inventory.ControlPermissionsChanged -= ContextChanged;
            networkState.ActionChanged -= ActionChanged;
            networkState.ResetItemAction();
            seating.PresentationContextChanged -= ContextChanged;
            carry.PresentationContextChanged -= ContextChanged;
            if (registry)
            {
                registry.PresentationChanged -= LifecycleChanged;
                registry.HeldDefaults.ContentChanged -= PoseContentChanged;
                registry.DetachHolder(inventory);
            }
            avatar.IdentityResolved -= IdentityResolved;
            avatar.WillUnbind -= WillUnbind;
            avatar.DidBind -= DidBind;
            ClearTarget();
            if (target) Destroy(target.gameObject);
            if (fallback) Destroy(fallback.gameObject);
            target = fallback = followBone = null;
            resolvedSettings = boundSettings = null;
            boundBinding = null; preparedBody = null; committed = default;
            selectedDefinition = actionDefinition = null;
            SubscribeContent();
            projectile = null;
            prepared = hasPose = blending = tracking = submitted = recoveryNeedsPose = releaseUnavailable = false;
            selectedId = preparedId = 0;
            action = default;
            age = clock = 0;
            weight = 0f;
            advancedFrame = -1;
        }

        private void OnDisable() => StopPresentation();
        private void OnDestroy() => StopPresentation();

        private void IdentityResolved(AvatarRegistry.Entry entry)
        {
            resolvedSettings = entry.Settings;
            committed = default;
        }

        private void Bind(AvatarBinding binding)
        {
            boundBinding = binding;
            boundSettings = binding?.Settings;
            followBone = binding?.GetBone(HumanBodyBones.RightHand);
            committed = default;
        }

        private void WillUnbind(AvatarBinding binding)
        {
            boundBinding = null;
            committed = default;
            followBone = null;
            boundSettings = null;
            preparedBody = null;
            ClearTarget();
            registry.RefreshHolder(inventory);
        }

        private void DidBind(AvatarBinding binding)
        {
            Bind(binding);
            committed = default;
            registry.RefreshHolder(inventory);
        }

        private void ContextChanged()
        {
            if (!running) return;
            if (!inventory.CanEquip || !networkState.CanCharge)
            {
                equipment.CancelUse();
                networkState.ResetItemAction();
                projectile = null;
                prepared = tracking = blending = hasPose = false;
                weight = 0f;
                ClearTarget();
            }
            SelectionChanged();
            registry.RefreshHolder(inventory);
        }

        private void SelectionChanged()
        {
            if (!running) return;
            uint id = 0;
            ItemDefinition definition = null;
            if (inventory.IsOwner)
            {
                var stack = inventory.GetEquipped();
                if (!stack.IsEmpty) { id = stack.WorldIds[0]; definition = registry.GetDefinition(stack.ItemId); }
            }
            else
            {
                var item = registry.EquippedPresentation(inventory.ObjectId);
                if (item) { id = item.Record.Motion.Id; definition = item.Definition; }
            }
            if (id == selectedId && definition == selectedDefinition) return;
            selectedId = id;
            selectedDefinition = definition;
            SubscribeContent();
            if (definition) selectedData = registry.GetHeldPose(definition, inventory.IsOwner);
            if (action.State == ItemActionState.Recovering)
            {
                if (stage == RecoveryStage.Return && hasPose)
                {
                    returnStart = lastBody.ToLocal(pose.FollowPosition);
                    returnRotation = Quaternion.Inverse(lastBody.Rotation) * pose.WristRotation;
                    returnWeight = weight;
                    returnSegmentStart = age;
                }
            }
            else BeginBlend(definition ? selectedData.Settings.ReturnBlendDuration : blendDuration);
            registry.RefreshHolder(inventory);
        }

        private void SubscribeContent()
        {
            if (subscribedSelected) subscribedSelected.ContentChanged -= PoseContentChanged;
            if (subscribedAction) subscribedAction.ContentChanged -= PoseContentChanged;
            subscribedSelected = selectedDefinition;
            subscribedAction = actionDefinition != selectedDefinition ? actionDefinition : null;
            if (subscribedSelected) subscribedSelected.ContentChanged += PoseContentChanged;
            if (subscribedAction) subscribedAction.ContentChanged += PoseContentChanged;
        }

        private void PoseContentChanged()
        {
            if (selectedDefinition) selectedData = registry.GetHeldPose(selectedDefinition, inventory.IsOwner);
            if (actionDefinition && action.State != ItemActionState.Recovering)
                actionData = registry.GetHeldPose(actionDefinition, inventory.IsOwner);
            committed = default;
        }

        private void LifecycleChanged(uint id, int previousHolder, int holder)
        {
            if (previousHolder == inventory.ObjectId || holder == inventory.ObjectId || id == selectedId) SelectionChanged();
            if (action.State == ItemActionState.Recovering && stage == RecoveryStage.Follow && id == action.WorldId)
                FindProjectile();
        }

        private void FindProjectile()
        {
            if (tracking || releaseUnavailable || recoveryNeedsPose) return;
            if (registry.TryGetItem(action.WorldId, out var item) && item)
            {
                if (Matches(item))
                {
                    projectile = item;
                    tracking = true;
                    return;
                }
                if (MatchesRelease(item.Record)) releaseUnavailable = true;
            }
            if (registry.TryGetRecord(action.WorldId, out var record))
                releaseUnavailable |= record.State == WorldItemState.Removed ||
                    record.State == WorldItemState.Held && record.Holder != inventory.ObjectId &&
                        (int)(record.Motion.Tick - action.StartedTick) >= 0 ||
                    record.State == WorldItemState.World && record.Releaser >= 0 && !MatchesRelease(record) &&
                        (int)(record.LaunchTick - action.StartedTick) > 0;
        }

        private bool MatchesRelease(in ItemRecord record) => record.Motion.Id == action.WorldId &&
            record.Releaser == inventory.ObjectId && record.Operation == action.Operation;
        private bool Matches(WorldItem item) => item && item.ReleaseAvailable && MatchesRelease(item.Record);

        private void ActionChanged()
        {
            if (!running) return;
            var next = networkState.ItemAction;
            if (networkState.HasActionSnapshot && next.State == action.State && next.ControlRevision == action.ControlRevision &&
                next.TransitionSequence == action.TransitionSequence) { registry.RefreshHolder(inventory); return; }
            var previous = action;
            float returnDuration = actionDefinition ? actionData.Settings.ReturnBlendDuration : blendDuration;
            action = next;
            age = !inventory.IsOwner && networkState.HasActionSnapshot ? networkState.ActionAge(action) : 0d;
            clock = Time.unscaledTimeAsDouble;
            projectile = null;
            tracking = false;
            releaseUnavailable = false;
            actionDefinition = action.DefinitionId == 0 ? null : registry.GetDefinition(action.DefinitionId);
            SubscribeContent();
            if (actionDefinition) actionData = registry.GetHeldPose(actionDefinition, inventory.IsOwner);
            if (action.State == ItemActionState.Charging)
            {
                chargeStart = actionData.HoldPosition(boundSettings ? boundSettings : resolvedSettings);
                chargeRotation = actionData.HoldRotation;
                if (TryBody(out var body, out var settings))
                {
                    if (hasPose)
                    {
                        chargeStart = lastBody.ToLocal(pose.FollowPosition);
                        chargeRotation = Quaternion.Inverse(lastBody.Rotation) * pose.FollowRotation;
                    }
                    pose = HeldItemPoseCalculation.Charge(body, settings, actionData, chargeStart, chargeRotation, ChargeProgress(age));
                    lastBody = body;
                    hasPose = true;
                }
                blending = false;
                weight = 1f;
            }
            else if (action.State == ItemActionState.Recovering)
            {
                stage = RecoveryStage.Follow;
                blending = false;
                submitted = !inventory.IsOwner;
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
                        pose = new HeldItemPose(committed.Palm.position, committed.Palm.rotation *
                            Quaternion.Inverse(body.Measurements.RightWristToPalmRotation), body.Measurements, body.Scale, actionData);
                    }
                    else
                    {
                        pose = HeldItemPoseCalculation.Charge(body, settings, actionData, chargeStart, chargeRotation,
                            action.ReleaseArcProgress / 255f);
                    }
                    lastBody = body;
                    retainedRotation = Quaternion.Inverse(body.Rotation) * pose.WristRotation;
                    retainedPosition = body.ToLocal(pose.FollowPosition);
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
                }
                else BeginBlend(returnDuration);
            }
            registry.RefreshHolder(inventory);
        }

        private double CurrentAge()
        {
            double now = Time.unscaledTimeAsDouble;
            age += System.Math.Max(0d, now - clock);
            if (!inventory.IsOwner) age = System.Math.Max(age, networkState.ActionAge(action));
            clock = now;
            return age;
        }

        private float ChargeProgress(double elapsed) => actionData.Settings.ChargePoseDuration <= 0f ? 1f :
            Mathf.Clamp01((float)(elapsed / actionData.Settings.ChargePoseDuration));

        private bool TryBody(out HeldItemBodyFrame body, out AvatarSettings settings)
        {
            if (preparedBody.HasValue)
            {
                body = preparedBody.Value; settings = boundSettings ? boundSettings : resolvedSettings;
                return settings;
            }
            if (inventory.IsOwner && playerAvatar.Hands && playerAvatar.Hands.TryBody(out body))
            { settings = resolvedSettings; return settings; }
            bool bound = followBone && boundSettings;
            settings = bound ? boundSettings : resolvedSettings;
            if (!settings) { body = default; return false; }
            var input = bound ? avatar.Input : playerAvatar.CurrentPlacement;
            bool attached = input.Seated || input.Carried && !input.ReleasePreview;
            Quaternion torso = bound ? avatar.transform.rotation : attached ? input.Facing.rotation :
                Quaternion.Euler(0f, input.Facing.rotation.eulerAngles.y, 0f);
            float seated = bound ? avatar.State.Weights[(int)AvatarPose.Seated] : input.Seated ? 1f : 0f;
            body = new HeldItemBodyFrame(settings, input, torso, seated);
            return true;
        }

        internal bool TryPrepareRelease(WorldItem item, out Pose release, out byte progress)
        {
            release = default;
            progress = 0;
            if (!ReadyForUse || !item) return false;
            CurrentAge();
            CompleteAtDeadline();
            playerAvatar.Hands.SampleImmediately();
            if (committed.Item != item.Record.Motion.Id || !committed.Clear ||
                committed.Generation != (playerAvatar.Hands.LocalBinding?.Generation ?? 0))
            {
                networkState.CancelItemCharge();
                return false;
            }
            release = committed.ItemPose;
            prepared = true;
            preparedId = item.Record.Motion.Id;
            preparedPose = new HeldItemPose(committed.Palm.position,
                committed.Palm.rotation * Quaternion.Inverse(lastBody.Measurements.RightWristToPalmRotation),
                lastBody.Measurements, lastBody.Scale, action.State == ItemActionState.Charging ? actionData : selectedData);
            progress = committed.Progress;
            return true;
        }

        internal void ReleaseSubmitted()
        {
            submitted = true;
        }
        internal void RejectRelease(uint id, uint operation) => networkState.CompleteRecovery(id, operation);

        private void BeginBlend(float duration)
        {
            blendDuration = Mathf.Max(0f, duration);
            if (!hasPose && followBone && TryBody(out var body, out var settings))
            {
                pose = new HeldItemPose(fallback.position, followBone.rotation, body.Measurements, body.Scale, selectedData);
                lastBody = body;
                weight = 0f;
                hasPose = true;
            }
            blending = hasPose && blendDuration > 0f;
            blendStart = Time.unscaledTimeAsDouble;
            if (!hasPose) return;
            returnStart = lastBody.ToLocal(pose.FollowPosition);
            returnRotation = Quaternion.Inverse(lastBody.Rotation) * pose.WristRotation;
            returnWeight = weight;
        }

        private void LateUpdate()
        {
            if (!running) return;
            if (advancedFrame != Time.frameCount)
            { advancedFrame = Time.frameCount; CurrentAge(); CompleteAtDeadline(); }
            if (!inventory.IsOwner && !avatar.EvaluatesTargets)
            {
                PrepareHands(null, null);
                CommitHands(null);
            }
        }

        internal void PrepareHands(AvatarBinding binding, HeldItemBodyFrame? body)
        {
            if (!running) return;
            committed = default;
            if (boundBinding != binding) Bind(binding);
            preparedBody = body;
            RefreshPose();
        }

        internal void PrepareCandidate(AvatarBinding binding, in HeldItemBodyFrame body)
        {
            if (!running || !hasPose || weight <= 0f) return;
            var data = action.State == ItemActionState.Idle ? selectedData : actionData;
            var sample = action.State == ItemActionState.Charging
                ? HeldItemPoseCalculation.Charge(body, binding.Settings, data, chargeStart, chargeRotation, ChargeProgress(age))
                : HeldItemPoseCalculation.Resolve(body.ToWorld(lastBody.ToLocal(pose.FollowPosition)),
                    body.Rotation * Quaternion.Inverse(lastBody.Rotation) * pose.WristRotation, body, binding.Settings, data, out _);
            target.SetPositionAndRotation(sample.FollowPosition, sample.FollowRotation);
        }

        private void CompleteAtDeadline()
        {
            if (action.State != ItemActionState.Recovering || !actionDefinition || !inventory.IsOwner || !submitted) return;
            double end = Mathf.Max(0f, actionData.Settings.MaximumFollowDuration) +
                Mathf.Max(0f, actionData.Settings.EndPosePauseDuration) + Mathf.Max(0f, actionData.Settings.ReturnBlendDuration);
            if (age < end) return;
            submitted = false;
            networkState.CompleteRecovery(action.WorldId, action.Operation);
        }

        internal bool CommitHands(AvatarBinding binding)
        {
            if (!running || !hasPose) return true;
            Pose palm = new(pose.FollowPosition, pose.FollowRotation);
            if (binding != null)
            {
                var wrist = binding.GetBone(HumanBodyBones.RightHand);
                var data = binding.Measurements;
                palm = new Pose(wrist.position + wrist.rotation * (data.RightWristToPalmPosition * binding.Scale),
                    wrist.rotation * data.RightWristToPalmRotation);
            }
            fallback.SetPositionAndRotation(palm.position, palm.rotation);
            var grip = action.State == ItemActionState.Charging ? actionData : selectedData;
            Pose itemPose = new(palm.position + palm.rotation * grip.GripPosition, palm.rotation * grip.GripRotation);
            bool clear = true;
            if (selectedId != 0 && registry.TryGetItem(selectedId, out var item) && item && CanShowHeldItem)
            {
                item.CommitHeldPose(itemPose, inventory.ObjectId);
                if (inventory.IsOwner)
                {
                    bool accessible = ItemReleaseClearance.TryResolve(itemPose, player.AimPose.position, item.ReleaseRadius,
                        lastBody.Rotation, registry.EnvironmentMask, out correctedItem);
                    clear = accessible && (correctedItem.position - itemPose.position).sqrMagnitude < 0.000001f;
                    if (!accessible) correctedItem = itemPose;
                }
            }
            committed = new ReleaseSample { Item = selectedId, Generation = binding?.Generation ?? 0,
                Palm = palm, ItemPose = itemPose, Clear = clear,
                Progress = (byte)Mathf.RoundToInt((action.State == ItemActionState.Charging ? ChargeProgress(age) : 0f) * 255f) };
            return clear;
        }

        internal bool CorrectCommittedPose()
        {
            if (committed.Clear || (correctedItem.position - committed.ItemPose.position).sqrMagnitude < 0.000001f) return false;
            var data = action.State == ItemActionState.Charging ? actionData : selectedData;
            Quaternion palm = correctedItem.rotation * Quaternion.Inverse(data.GripRotation);
            Quaternion wrist = palm * Quaternion.Inverse(lastBody.Measurements.RightWristToPalmRotation);
            pose = HeldItemPoseCalculation.Resolve(correctedItem.position - palm * data.GripPosition, wrist,
                lastBody, boundSettings ? boundSettings : resolvedSettings, data, out _);
            target.SetPositionAndRotation(pose.FollowPosition, pose.FollowRotation);
            return true;
        }

        private void RefreshPose()
        {
            if (!running) return;
            if (!TryBody(out var body, out var settings)) { hasPose = false; ClearTarget(); return; }
            if (!inventory.CanEquip || !networkState.HasActionSnapshot)
            { weight = 0f; ClearTarget(); return; }
            if (action.State == ItemActionState.Recovering && actionDefinition)
            {
                if (recoveryNeedsPose)
                {
                    pose = HeldItemPoseCalculation.Charge(body, settings, actionData, chargeStart, chargeRotation,
                        action.ReleaseArcProgress / 255f);
                    retainedRotation = Quaternion.Inverse(body.Rotation) * pose.WristRotation;
                    retainedPosition = body.ToLocal(pose.FollowPosition);
                    recoveryNeedsPose = false;
                    FindProjectile();
                }
                Recover(body, settings, age);
            }
            else if (action.State == ItemActionState.Charging && actionDefinition)
            {
                pose = HeldItemPoseCalculation.Charge(body, settings, actionData, chargeStart, chargeRotation,
                    ChargeProgress(age));
                weight = 1f;
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
                if (inventory.IsOwner && CanShowHeldItem && selectedId != 0 && registry.TryGetItem(selectedId, out var item) && item &&
                    ItemReleaseClearance.TryResolve(pose.Item, player.AimPose.position, item.ReleaseRadius, body.Rotation,
                        registry.EnvironmentMask, out var allowed))
                {
                    var data = action.State == ItemActionState.Charging ? actionData : selectedData;
                    Quaternion palm = allowed.rotation * Quaternion.Inverse(data.GripRotation);
                    pose = HeldItemPoseCalculation.Resolve(allowed.position - palm * data.GripPosition,
                        palm * Quaternion.Inverse(body.Measurements.RightWristToPalmRotation), body, settings, data, out _);
                }
                target.SetPositionAndRotation(pose.FollowPosition, pose.FollowRotation);
                if (!followBone) fallback.SetPositionAndRotation(pose.FollowPosition, pose.FollowRotation);
            }
            SetTarget(action.State == ItemActionState.Idle && selectedDefinition ? selectedData.Reach : actionData.Reach);
        }

        private void Recover(in HeldItemBodyFrame body, AvatarSettings settings, double elapsed)
        {
            double followEnd = Mathf.Max(0f, actionData.Settings.MaximumFollowDuration);
            double pauseEnd = followEnd + Mathf.Max(0f, actionData.Settings.EndPosePauseDuration);
            double returnEnd = pauseEnd + Mathf.Max(0f, actionData.Settings.ReturnBlendDuration);
            if (stage == RecoveryStage.Follow)
            {
                bool unavailable = releaseUnavailable || tracking && !Matches(projectile);
                bool beyond = false;
                if (tracking && !unavailable && elapsed < followEnd)
                {
                    Quaternion palm = projectile.PresentedRotation * Quaternion.Inverse(actionData.GripRotation);
                    Vector3 follow = projectile.PresentedRootPosition - palm * actionData.GripPosition;
                    pose = HeldItemPoseCalculation.Resolve(Vector3.Lerp(body.ToWorld(retainedPosition), follow,
                        LeanTween.easeOutSine(0f, 1f, Mathf.Clamp01((float)elapsed / 0.08f))),
                        palm * Quaternion.Inverse(body.Measurements.RightWristToPalmRotation), body, settings, actionData, out beyond);
                    if (Physics.Linecast(body.Shoulder, pose.FollowPosition, registry.EnvironmentMask, QueryTriggerInteraction.Ignore)) beyond = true;
                }
                else pose = HeldItemPoseCalculation.Resolve(body.ToWorld(retainedPosition), body.Rotation * retainedRotation,
                    body, settings, actionData, out _);
                if (unavailable || beyond || elapsed >= followEnd)
                {
                    retainedPosition = body.ToLocal(pose.FollowPosition);
                    retainedRotation = Quaternion.Inverse(body.Rotation) * pose.WristRotation;
                    projectile = null; tracking = false; releaseUnavailable = true;
                }
                if (elapsed >= followEnd) stage = RecoveryStage.Pause;
            }
            if (stage == RecoveryStage.Pause)
            {
                pose = HeldItemPoseCalculation.Resolve(body.ToWorld(retainedPosition), body.Rotation * retainedRotation, body, settings, actionData, out _);
                if (elapsed >= pauseEnd)
                {
                    stage = RecoveryStage.Return;
                    returnStart = body.ToLocal(pose.FollowPosition);
                    returnRotation = Quaternion.Inverse(body.Rotation) * pose.WristRotation;
                    returnWeight = 1f; returnSegmentStart = pauseEnd;
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

        private void Blend(in HeldItemBodyFrame body, AvatarSettings settings, float t, in HeldItemPoseData data)
        {
            bool holding = selectedDefinition && inventory.CanEquip;
            var destination = HeldItemPoseCalculation.Hold(body, settings, data);
            float movement = LeanTween.easeOutBack(0f, 1f, t, 0.5f);
            pose = t >= 1f ? destination : HeldItemPoseCalculation.Resolve(Vector3.LerpUnclamped(body.ToWorld(returnStart), destination.FollowPosition, movement),
                Quaternion.SlerpUnclamped(body.Rotation * returnRotation, destination.WristRotation, movement), body, settings, data, out _);
            weight = Mathf.Lerp(returnWeight, holding ? 1f : 0f, LeanTween.easeInOutSine(0f, 1f, t));
            hasPose = true;
        }

        private void SetTarget(float reach)
        {
            if (weight <= 0f) { ClearTarget(); return; }
            var clips = avatar.Registry.Animations;
            AnimationClip fingers = action.State == ItemActionState.Recovering ? clips.OpenFingers :
                selectedDefinition && selectedDefinition.GripFingers ? selectedDefinition.GripFingers : clips.GripFingers;
            avatar.HandTargets.Set(AvatarIKGoal.RightHand, AvatarHandSource.Item, target, weight, weight, reach, fingers);
            targetInstalled = true;
        }
        private void ClearTarget()
        {
            if (!targetInstalled) return;
            avatar.ClearHandTarget(AvatarIKGoal.RightHand);
            targetInstalled = false;
        }
    }
}
