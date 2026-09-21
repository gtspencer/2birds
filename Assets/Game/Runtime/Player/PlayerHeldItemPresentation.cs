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
        private Transform target, fallback, followBone;
        private ItemDefinition selectedDefinition, actionDefinition;
        private HeldItemPoseData selectedData, actionData;
        private ItemActionSnapshot action;
        private HeldItemPose pose, preparedPose;
        private HeldItemBodyFrame lastBody;
        private WorldItem projectile;
        private uint selectedId, preparedId;
        private Vector3 chargeStart, retainedPosition, followOffset, preparedOffset, returnStart;
        private Quaternion chargeRotation, retainedRotation, returnRotation;
        private double age, clock, stageStart, blendStart, returnSegmentStart;
        private float weight, returnWeight, blendDuration, installedWeight = -1f, installedReach;
        private RecoveryStage stage;
        private bool running, hasPose, blending, prepared, tracking, submitted, targetInstalled;
        private bool recoveryNeedsPose, releaseUnavailable;
        private int advancedFrame = -1;

        internal bool CanShowHeldItem => running && inventory.CanEquip && networkState.HasActionSnapshot &&
            networkState.ItemAction.State != ItemActionState.Recovering;
        internal bool ReadyForUse => running && inventory.CanEquip && networkState.CanCharge &&
            networkState.ItemAction.State != ItemActionState.Recovering;
        internal Transform Attachment => followBone ? followBone : fallback;
        internal HeldItemPoseData Grip(ItemDefinition definition) => definition == selectedDefinition ? selectedData : new HeldItemPoseData(definition);

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
            avatar.IdentityResolved += IdentityResolved;
            avatar.WillUnbind += WillUnbind;
            avatar.DidBind += DidBind;
            avatar.BeforeEvaluation += BeforeEvaluation;
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
                registry.DetachHolder(inventory);
            }
            avatar.IdentityResolved -= IdentityResolved;
            avatar.WillUnbind -= WillUnbind;
            avatar.DidBind -= DidBind;
            avatar.BeforeEvaluation -= BeforeEvaluation;
            ClearTarget();
            if (target) Destroy(target.gameObject);
            if (fallback) Destroy(fallback.gameObject);
            target = fallback = followBone = null;
            resolvedSettings = boundSettings = null;
            selectedDefinition = actionDefinition = null;
            projectile = null;
            prepared = hasPose = blending = tracking = submitted = recoveryNeedsPose = releaseUnavailable = false;
            selectedId = preparedId = 0;
            action = default;
            age = clock = stageStart = 0;
            weight = 0f;
            advancedFrame = -1;
        }

        private void OnDisable() => StopPresentation();
        private void OnDestroy() => StopPresentation();

        private void IdentityResolved(AvatarRegistry.Entry entry)
        {
            resolvedSettings = entry.Settings;
            RefreshPose(false);
        }

        private void Bind(AvatarBinding binding)
        {
            boundSettings = binding.Settings;
            followBone = binding.GetBone(boundSettings.RightHandFollowBone);
        }

        private void WillUnbind(AvatarBinding binding)
        {
            followBone = null;
            boundSettings = null;
            ClearTarget();
            registry.RefreshHolder(inventory);
        }

        private void DidBind(AvatarBinding binding)
        {
            Bind(binding);
            RefreshPose(false);
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
            if (definition) selectedData = new HeldItemPoseData(definition);
            if (action.State == ItemActionState.Recovering)
            {
                if (stage == RecoveryStage.Return && hasPose)
                {
                    returnStart = lastBody.ToLocal(pose.FollowPosition);
                    returnRotation = Quaternion.Inverse(lastBody.Rotation) * pose.WristRotation;
                    returnWeight = weight;
                    returnSegmentStart = CurrentAge();
                }
            }
            else BeginBlend(definition ? selectedData.Settings.ReturnBlendDuration : blendDuration);
            registry.RefreshHolder(inventory);
        }

        private void LifecycleChanged(uint id, int previousHolder, int holder)
        {
            if (previousHolder == inventory.ObjectId || holder == inventory.ObjectId || id == selectedId) SelectionChanged();
            if (action.State == ItemActionState.Recovering && stage == RecoveryStage.Follow && id == action.WorldId)
                FindProjectile();
        }

        private void FindProjectile()
        {
            if (tracking) return;
            if (registry.TryGetItem(action.WorldId, out var item) && Matches(item))
            {
                projectile = item;
                tracking = true;
            }
            else if (registry.TryGetRecord(action.WorldId, out var record) && record.State == WorldItemState.Removed)
                releaseUnavailable = true;
        }

        private bool Matches(WorldItem item) => item && item.ReleaseAvailable && item.Record.Motion.Id == action.WorldId &&
            item.Record.Releaser == inventory.ObjectId && item.Record.Operation == action.Operation;

        private void ActionChanged()
        {
            if (!running) return;
            var next = networkState.ItemAction;
            if (networkState.HasActionSnapshot && next.State == action.State && next.ControlRevision == action.ControlRevision &&
                next.TransitionSequence == action.TransitionSequence) { registry.RefreshHolder(inventory); return; }
            var previous = action;
            action = next;
            age = !inventory.IsOwner && networkState.HasActionSnapshot ? networkState.ActionAge(action) : 0d;
            clock = Time.unscaledTimeAsDouble;
            projectile = null;
            tracking = false;
            releaseUnavailable = false;
            actionDefinition = action.DefinitionId == 0 ? null : registry.GetDefinition(action.DefinitionId);
            if (actionDefinition) actionData = new HeldItemPoseData(actionDefinition);
            if (action.State == ItemActionState.Charging)
            {
                chargeStart = actionData.Settings.HoldPosition;
                chargeRotation = actionData.HoldRotation;
                if (TryBody(false, out var body, out var settings))
                {
                    if (hasPose)
                    {
                        chargeStart = lastBody.ToLocal(pose.FollowPosition);
                        chargeRotation = Quaternion.Inverse(lastBody.Rotation) * pose.WristRotation;
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
                stageStart = 0d;
                blending = false;
                submitted = !inventory.IsOwner;
                recoveryNeedsPose = true;
                if (previous.State != ItemActionState.Charging)
                { chargeStart = actionData.Settings.HoldPosition; chargeRotation = actionData.HoldRotation; }
                if (TryBody(false, out var body, out var settings))
                {
                    if (prepared && preparedId == action.WorldId)
                    {
                        pose = preparedPose;
                        followOffset = preparedOffset;
                    }
                    else
                    {
                        pose = HeldItemPoseCalculation.Charge(body, settings, actionData, chargeStart, chargeRotation,
                            action.ReleaseArcProgress / 255f);
                        followOffset = pose.FollowPosition - pose.Item.position;
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
                    RefreshPose();
                }
                else BeginBlend(actionDefinition ? actionData.Settings.ReturnBlendDuration : blendDuration);
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

        private bool TryBody(bool gameplay, out HeldItemBodyFrame body, out AvatarSettings settings)
        {
            bool bound = !gameplay && avatar.EvaluatesTargets && followBone && boundSettings;
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
            if (!ReadyForUse || !item || !TryBody(true, out var body, out var settings)) return false;
            var data = new HeldItemPoseData(item.Definition);
            float arc = action.State == ItemActionState.Charging ? ChargeProgress(CurrentAge()) : 0f;
            var current = action.State == ItemActionState.Charging
                ? HeldItemPoseCalculation.Charge(body, settings, data, chargeStart, chargeRotation, arc)
                : HeldItemPoseCalculation.Hold(body, settings, data);
            if (!ItemReleaseClearance.TryResolve(current.Item, player.AimPose.position, item.DropDiameter * 0.5f,
                body.Rotation, registry.EnvironmentMask, out release))
            {
                networkState.CancelItemCharge();
                return false;
            }
            prepared = true;
            preparedId = item.Record.Motion.Id;
            preparedPose = current;
            preparedOffset = current.FollowPosition - release.position;
            progress = (byte)Mathf.RoundToInt(arc * 255f);
            return true;
        }

        internal void ReleaseSubmitted()
        {
            submitted = true;
            RefreshPose();
        }
        internal void RejectRelease(uint id, uint operation) => networkState.CompleteRecovery(id, operation);

        private void BeginBlend(float duration)
        {
            blendDuration = Mathf.Max(0f, duration);
            blending = hasPose && blendDuration > 0f;
            blendStart = Time.unscaledTimeAsDouble;
            if (!hasPose) return;
            returnStart = lastBody.ToLocal(pose.FollowPosition);
            returnRotation = Quaternion.Inverse(lastBody.Rotation) * pose.WristRotation;
            returnWeight = weight;
        }

        private void LateUpdate()
        {
            if (running && !avatar.EvaluatesTargets) Advance();
        }
        private void BeforeEvaluation()
        {
            if (running && avatar.EvaluatesTargets) Advance();
        }
        private void Advance()
        {
            if (advancedFrame == Time.frameCount) return;
            advancedFrame = Time.frameCount;
            RefreshPose();
        }

        private void RefreshPose(bool advanceAction = true)
        {
            if (!running || !TryBody(false, out var body, out var settings)) return;
            if (!inventory.CanEquip || !networkState.HasActionSnapshot)
            { weight = 0f; ClearTarget(); return; }
            if (action.State == ItemActionState.Recovering && actionDefinition)
            {
                if (recoveryNeedsPose)
                {
                    pose = HeldItemPoseCalculation.Charge(body, settings, actionData, chargeStart, chargeRotation,
                        action.ReleaseArcProgress / 255f);
                    followOffset = pose.FollowPosition - pose.Item.position;
                    retainedRotation = Quaternion.Inverse(body.Rotation) * pose.WristRotation;
                    retainedPosition = body.ToLocal(pose.FollowPosition);
                    recoveryNeedsPose = false;
                }
                if (advanceAction) Recover(body, settings, CurrentAge());
                else if (hasPose)
                    pose = HeldItemPoseCalculation.Resolve(body.ToWorld(lastBody.ToLocal(pose.FollowPosition)),
                        body.Rotation * (Quaternion.Inverse(lastBody.Rotation) * pose.WristRotation), body, settings, actionData, out _);
            }
            else if (action.State == ItemActionState.Charging && actionDefinition)
            {
                pose = HeldItemPoseCalculation.Charge(body, settings, actionData, chargeStart, chargeRotation,
                    ChargeProgress(advanceAction ? CurrentAge() : age));
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
                target.SetPositionAndRotation(pose.FollowPosition, pose.WristRotation);
                fallback.SetPositionAndRotation(pose.FollowPosition, pose.FollowRotation);
            }
            SetTarget(action.State == ItemActionState.Idle && selectedDefinition ? selectedData.Reach : actionData.Reach);
        }

        private void Recover(in HeldItemBodyFrame body, AvatarSettings settings, double elapsed)
        {
            if (stage == RecoveryStage.Follow)
            {
                float maximum = Mathf.Max(0f, actionData.Settings.MaximumFollowDuration);
                bool expired = elapsed >= maximum;
                bool unavailable = releaseUnavailable || tracking && !Matches(projectile);
                bool beyond = false;
                if (tracking && !unavailable)
                    pose = HeldItemPoseCalculation.Resolve(projectile.PresentedOrigin + followOffset,
                        body.Rotation * retainedRotation, body, settings, actionData, out beyond);
                else if (tracking)
                    pose = HeldItemPoseCalculation.Resolve(pose.FollowPosition, body.Rotation * retainedRotation,
                        body, settings, actionData, out _);
                else pose = HeldItemPoseCalculation.Resolve(body.ToWorld(retainedPosition), body.Rotation * retainedRotation,
                    body, settings, actionData, out _);
                if (expired || unavailable || beyond)
                {
                    stage = RecoveryStage.Pause;
                    stageStart = expired ? maximum : elapsed;
                    retainedPosition = body.ToLocal(pose.FollowPosition);
                    retainedRotation = Quaternion.Inverse(body.Rotation) * pose.WristRotation;
                    projectile = null;
                    tracking = false;
                }
            }
            if (stage == RecoveryStage.Pause)
            {
                pose = HeldItemPoseCalculation.Resolve(body.ToWorld(retainedPosition), body.Rotation * retainedRotation,
                    body, settings, actionData, out _);
                float pause = Mathf.Max(0f, actionData.Settings.EndPosePauseDuration);
                if (elapsed >= stageStart + pause)
                {
                    stageStart += pause;
                    stage = RecoveryStage.Return;
                    returnStart = body.ToLocal(pose.FollowPosition);
                    returnRotation = Quaternion.Inverse(body.Rotation) * pose.WristRotation;
                    returnWeight = 1f;
                    returnSegmentStart = stageStart;
                }
            }
            if (stage == RecoveryStage.Return)
            {
                double end = stageStart + Mathf.Max(0f, actionData.Settings.ReturnBlendDuration);
                float t = end <= returnSegmentStart ? 1f : Mathf.Clamp01((float)((elapsed - returnSegmentStart) / (end - returnSegmentStart)));
                Blend(body, settings, t, selectedDefinition ? selectedData : actionData);
                if (t >= 1f) stage = RecoveryStage.Finished;
            }
            if (stage == RecoveryStage.Finished)
            {
                Blend(body, settings, 1f, selectedDefinition ? selectedData : actionData);
                if (inventory.IsOwner && submitted) networkState.CompleteRecovery(action.WorldId, action.Operation);
            }
            hasPose = true;
        }

        private void Blend(in HeldItemBodyFrame body, AvatarSettings settings, float t, in HeldItemPoseData data)
        {
            bool holding = selectedDefinition && inventory.CanEquip;
            var destination = holding ? HeldItemPoseCalculation.Hold(body, settings, selectedData) :
                HeldItemPoseCalculation.Resolve(body.ToWorld(returnStart), body.Rotation * returnRotation, body, settings, data, out _);
            pose = t >= 1f ? destination : HeldItemPoseCalculation.Resolve(Vector3.Lerp(body.ToWorld(returnStart), destination.FollowPosition, t),
                Quaternion.Slerp(body.Rotation * returnRotation, destination.WristRotation, t), body, settings, data, out _);
            weight = Mathf.Lerp(returnWeight, holding ? 1f : 0f, t);
            hasPose = true;
        }

        private void SetTarget(float reach)
        {
            if (weight <= 0f) { ClearTarget(); return; }
            if (targetInstalled && installedWeight == weight && installedReach == reach) return;
            avatar.SetHandTarget(AvatarIKGoal.RightHand, target, weight, weight, reach);
            targetInstalled = true;
            installedWeight = weight;
            installedReach = reach;
        }
        private void ClearTarget()
        {
            if (!targetInstalled) return;
            avatar.ClearHandTarget(AvatarIKGoal.RightHand);
            targetInstalled = false;
            installedWeight = -1f;
        }
    }
}
