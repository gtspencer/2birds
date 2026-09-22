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
            internal Pose Palm, LeftPalm, ItemPose;
            internal byte Progress;
            internal bool Clear;
        }
        private ReleaseSample committed;
        private Pose correctedItem;
        private Transform target, fallback, followBone, leftTarget;
        private SlingshotPresentation slingshot;
        private bool pullNeedsCorrection;
        private bool SlingshotRecovery => action.State == ItemActionState.Recovering && actionDefinition is SlingshotDefinition;
        private ItemDefinition selectedDefinition, actionDefinition, subscribedSelected, subscribedAction;
        private HeldItemPoseData selectedData, actionData;
        private ItemActionSnapshot action;
        private HeldItemPose pose, preparedPose;
        private HeldItemBodyFrame lastBody;
        private WorldItem projectile;
        private uint selectedId, preparedId;
        private Vector3 chargeStart, followStart, retainedPosition, returnStart;
        private Quaternion chargeRotation, retainedRotation, returnRotation;
        private Pose retainedItem, followItem, returnItem, returnLeft;
        private Pose retainedLeft, retainedRight, followLeft, followRight;
        private float leftWeight, returnLeftWeight, returnFrameWeight;
        private bool returnHeavy;
        private double age, clock, blendStart, returnSegmentStart;
        private float weight, returnWeight, blendDuration, effectiveReach, returnReach;
        private RecoveryStage stage;
        private bool running, hasPose, blending, prepared, tracking, submitted, targetInstalled;
        private bool recoveryNeedsPose, releaseUnavailable;
        private bool chargeFromBlend;
        private int advancedFrame = -1;

        internal bool CanShowHeldItem => running && inventory.CanEquip && networkState.HasActionSnapshot &&
            (networkState.ItemAction.State != ItemActionState.Recovering ||
                registry.GetDefinition(networkState.ItemAction.DefinitionId) is SlingshotDefinition);
        internal bool ReadyForUse => running && inventory.CanEquip && networkState.CanCharge &&
            networkState.ItemAction.State != ItemActionState.Recovering;
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
                if (!running || !inventory.CanEquip) return 0f;
                float destination = selectedDefinition && selectedData.Heavy ? 1f : 0f;
                if (action.State == ItemActionState.Recovering)
                {
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

        internal HeldItemBodyFrame HeavyBody(AvatarSettings settings)
        {
            bool remote = !inventory.IsOwner && avatar.Binding != null;
            var input = remote ? avatar.Input : playerAvatar.CurrentPlacement;
            bool attached = input.Seated || input.Carried && !input.ReleasePreview;
            Quaternion torso = remote ? avatar.transform.rotation : attached ? input.Facing.rotation :
                Quaternion.Euler(0f, input.Facing.rotation.eulerAngles.y, 0f);
            float seated = avatar.Binding != null ? avatar.State.Weights[(int)AvatarPose.Seated] : input.Seated ? 1f : 0f;
            return new HeldItemBodyFrame(settings, avatar.Registry, input, torso, seated, true);
        }
        internal bool IsPendingRelease(uint id) => running && networkState.HasActionSnapshot &&
            networkState.ItemAction.State == ItemActionState.Recovering && networkState.ItemAction.WorldId == id &&
            registry.GetDefinition(networkState.ItemAction.DefinitionId) is not SlingshotDefinition;
        internal bool MatchesPendingRelease(in ItemRecord record) => IsPendingRelease(record.Motion.Id) &&
            networkState.ItemAction.Operation == record.Operation;
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
            leftTarget = new GameObject("LeftItemHandTarget").transform;
            leftTarget.SetParent(transform, false);
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
            if (leftTarget) Destroy(leftTarget.gameObject);
            if (slingshot) slingshot.ResetPose();
            leftTarget = null; slingshot = null;
            target = fallback = followBone = null;
            resolvedSettings = boundSettings = null;
            boundBinding = null; preparedBody = null; committed = default;
            selectedDefinition = actionDefinition = null;
            selectedData = actionData = default;
            retainedItem = followItem = returnItem = returnLeft = default;
            retainedLeft = retainedRight = followLeft = followRight = default;
            leftWeight = returnLeftWeight = 0f; returnHeavy = false;
            SubscribeContent();
            projectile = null;
            prepared = hasPose = blending = tracking = submitted = recoveryNeedsPose = releaseUnavailable = false;
            selectedId = preparedId = 0;
            action = default;
            age = clock = 0;
            weight = 0f;
            effectiveReach = returnReach = 0f; chargeFromBlend = false;
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
            ClearLeftTarget();
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
                leftWeight = returnLeftWeight = 0f; returnHeavy = false;
                actionData = default;
                if (slingshot) slingshot.ResetPose();
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
            float frameWeight = HeavyFrameWeight;
            ClearLeftTarget();
            if (slingshot) slingshot.ResetPose();
            slingshot = id != 0 && registry.TryGetItem(id, out var selectedItem) && selectedItem
                ? selectedItem.GetComponent<SlingshotPresentation>() : null;
            selectedId = id;
            selectedDefinition = definition;
            SubscribeContent();
            if (definition) selectedData = registry.GetHeldPose(definition, inventory.IsOwner, selectedId);
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
            if (selectedDefinition) selectedData = registry.GetHeldPose(selectedDefinition, inventory.IsOwner, selectedId);
            if (actionDefinition && action.State != ItemActionState.Recovering)
                actionData = registry.GetHeldPose(actionDefinition, inventory.IsOwner, action.WorldId);
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
            if (SlingshotRecovery || tracking || releaseUnavailable || recoveryNeedsPose) return;
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
            var previousDefinition = actionDefinition;
            actionDefinition = action.DefinitionId == 0 ? null : registry.GetDefinition(action.DefinitionId);
            SubscribeContent();
            if (actionDefinition && (action.State != ItemActionState.Recovering || previousDefinition != actionDefinition))
                actionData = registry.GetHeldPose(actionDefinition, inventory.IsOwner, action.WorldId);
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
                submitted = !inventory.IsOwner;
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
                    RetainHeavyPalms(body, true);
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
            registry.RefreshHolder(inventory);
        }

        private double CurrentAge()
        {
            double now = Time.unscaledTimeAsDouble;
            age += System.Math.Max(0d, now - clock);
            if (action.State == ItemActionState.Charging && actionDefinition is SlingshotDefinition)
                age = networkState.ActionAge(action);
            else if (!inventory.IsOwner) age = System.Math.Max(age, networkState.ActionAge(action));
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
            var input = boundBinding != null ? avatar.Input : playerAvatar.CurrentPlacement;
            return HeldItemPoseCalculation.SlingshotCharge(body, data, slingshot, hold, start, progress, inventory.IsOwner,
                playerAvatar.Hands ? playerAvatar.Hands.CameraPose : player.AimPose,
                Quaternion.Euler(input.LookPitch, input.LookYaw, 0f), out reach);
        }

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
            if (HeavyFrameWeight > 0f) { body = HeavyBody(settings); return true; }
            var input = bound ? avatar.Input : playerAvatar.CurrentPlacement;
            bool attached = input.Seated || input.Carried && !input.ReleasePreview;
            Quaternion torso = bound ? avatar.transform.rotation : attached ? input.Facing.rotation :
                Quaternion.Euler(0f, input.Facing.rotation.eulerAngles.y, 0f);
            float seated = bound ? avatar.State.Weights[(int)AvatarPose.Seated] : input.Seated ? 1f : 0f;
            body = new HeldItemBodyFrame(settings, avatar.Registry, input, torso, seated);
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
            var data = action.State == ItemActionState.Charging ? actionData : selectedData;
            preparedPose = data.Heavy ? new HeldItemPose(committed.ItemPose, committed.LeftPalm, committed.Palm, lastBody) : new HeldItemPose(committed.Palm.position,
                committed.Palm.rotation * Quaternion.Inverse(lastBody.Measurements.RightWristToPalmRotation),
                lastBody.Measurements, lastBody.Scale, data);
            progress = committed.Progress;
            return true;
        }

        internal bool TryPreparePebble(WorldItem item, out Vector3 center, out float charge)
        {
            center = default; charge = 0f;
            if (!slingshot || item.Definition is not SlingshotDefinition definition ||
                !TryPrepareRelease(item, out var release, out _)) return false;
            charge = equipment.ItemCharge01(definition);
            var desired = new Pose(slingshot.Center, release.rotation);
            float radius = definition.PebblePrefab.Radius;
            if (!ItemReleaseClearance.TryResolve(desired, player.AimPose.position, radius, lastBody.Rotation,
                registry.EnvironmentMask, desired.position, 0.2f, out var allowed)) return false;
            Vector3 correction = allowed.position - desired.position;
            if (correction.sqrMagnitude > 0.000001f)
            {
                correctedItem = new Pose(release.position + correction, release.rotation);
                committed.Clear = false;
                if (!CorrectCommittedPose()) return false;
                playerAvatar.Hands.CommitCorrection();
            }
            center = slingshot.Center;
            if (!committed.Clear || !ItemReleaseClearance.TryResolve(new Pose(center, release.rotation),
                player.AimPose.position, radius, lastBody.Rotation, registry.EnvironmentMask, center, 0f, out _)) return false;
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
                    leftWeight, leftWeight, effectiveReach, fingers);
                return;
            }
            if (!slingshot || !CanShowHeldItem) { ClearLeftTarget(); return; }
            var state = selectedId == action.WorldId ? action.State : ItemActionState.Idle;
            float draw = state == ItemActionState.Charging
                ? ChargeProgress(age)
                : state == ItemActionState.Recovering ? action.ReleaseArcProgress / 255f : 0f;
            var data = state == ItemActionState.Idle ? selectedData : actionData;
            Pose palm = slingshot.Evaluate(frame, state, draw, age, body, data.SlingshotCharge.PullingHandDrawOffset);
            leftTarget.SetPositionAndRotation(palm.position, palm.rotation);
            if (slingshot.LeftWeight <= 0f) { ClearLeftTarget(); return; }
            avatar.HandTargets.Set(AvatarIKGoal.LeftHand, AvatarHandSource.Item, leftTarget,
                slingshot.LeftWeight, slingshot.LeftWeight, 0.85f, avatar.Registry.Animations.GripFingers);
        }

        private void ClearLeftTarget() => avatar.HandTargets.Clear(AvatarIKGoal.LeftHand, AvatarHandSource.Item);

        internal void ReleaseSubmitted()
        {
            submitted = true;
        }
        internal void RejectRelease(uint id, uint operation)
        {
            if (slingshot && selectedId == id && action.Operation == operation) slingshot.ResetPose();
            networkState.CompleteRecovery(id, operation);
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

        private void RetainHeavyPalms(in HeldItemBodyFrame body, bool start = false)
        {
            retainedLeft = HeavyItemPoseCalculation.ToLocal(pose.LeftPalm, body);
            retainedRight = HeavyItemPoseCalculation.ToLocal(new Pose(pose.FollowPosition, pose.FollowRotation), body);
            if (start) { followLeft = retainedLeft; followRight = retainedRight; }
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
            var data = action.State == ItemActionState.Idle || SlingshotRecovery ? selectedData : actionData;
            float reach = effectiveReach > 0f ? effectiveReach : data.Reach;
            HeldItemPose sample;
            if (data.Heavy && pose.Heavy)
            {
                var frame = pose.Item;
                var constraint = HeavyItemPoseCalculation.Reach(frame, body, data);
                if (constraint.TryProject(frame.position, out var position)) frame.position = position;
                sample = HeldItemPoseCalculation.FromItem(frame, body, data);
            }
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

        private void CompleteAtDeadline()
        {
            if (action.State != ItemActionState.Recovering || !actionDefinition || !inventory.IsOwner || !submitted) return;
            double end = actionDefinition is SlingshotDefinition sling ? sling.RecoverySeconds : Mathf.Max(0f, actionData.Settings.MaximumFollowDuration) +
                Mathf.Max(0f, actionData.Settings.EndPosePauseDuration) + Mathf.Max(0f, actionData.Settings.ReturnBlendDuration);
            if (age < end) return;
            submitted = false;
            networkState.CompleteRecovery(action.WorldId, action.Operation);
        }

        internal bool CommitHands(AvatarBinding binding)
        {
            if (!running || !hasPose) return true;
            Pose palm = binding != null ? binding.Palm(true) : new(pose.FollowPosition, pose.FollowRotation);
            var grip = action.State == ItemActionState.Charging ? actionData : selectedData;
            Pose itemPose = grip.Heavy ? pose.Item : new(palm.position + palm.rotation * grip.GripPosition, palm.rotation * grip.GripRotation);
            fallback.SetPositionAndRotation(grip.Heavy ? itemPose.position : palm.position, grip.Heavy ? itemPose.rotation : palm.rotation);
            bool clear = true;
            if (selectedId != 0 && registry.TryGetItem(selectedId, out var item) && item && CanShowHeldItem)
            {
                item.CommitHeldPose(itemPose, inventory.ObjectId);
                Vector3 previousLeft = leftTarget.position;
                PrepareLeft(itemPose, lastBody);
                pullNeedsCorrection = slingshot && slingshot.LeftWeight > 0f &&
                    (leftTarget.position - previousLeft).sqrMagnitude > 0.000001f;
                if (slingshot && binding != null) slingshot.CommitPalm(binding.Palm(false));
                if (inventory.IsOwner)
                {
                    bool accessible = ResolveClearance(itemPose, lastBody, grip, item, out correctedItem);
                    clear = accessible && (correctedItem.position - itemPose.position).sqrMagnitude < 0.000001f;
                    if (!accessible) correctedItem = itemPose;
                }
            }
            committed = new ReleaseSample { Item = selectedId, Generation = binding?.Generation ?? 0,
                Palm = palm, LeftPalm = binding != null ? binding.Palm(false) : pose.LeftPalm,
                ItemPose = itemPose, Clear = clear,
                Progress = (byte)Mathf.RoundToInt((action.State == ItemActionState.Charging ? ChargeProgress(age) : 0f) * 255f) };
            return clear && !pullNeedsCorrection;
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

        private bool ResolveClearance(Pose desired, in HeldItemBodyFrame body, in HeldItemPoseData data, WorldItem item, out Pose allowed)
        {
            if (data.Heavy)
                return ItemReleaseClearance.TryResolve(desired, player.AimPose.position, item.ReleaseSphere, body.Rotation,
                    registry.EnvironmentMask, HeavyItemPoseCalculation.Reach(desired, body, data), out allowed);
            var sample = HeldItemPoseCalculation.FromItem(desired, body, data);
            return ItemReleaseClearance.TryResolve(desired, player.AimPose.position, item.ReleaseRadius, body.Rotation,
                registry.EnvironmentMask, body.Shoulder + desired.position - sample.WristPosition,
                body.ArmLength * (effectiveReach > 0f ? effectiveReach : data.Reach), out allowed);
        }

        private void ApplyClearancePose(Pose allowed, in HeldItemBodyFrame body, in HeldItemPoseData data)
        {
            var corrected = HeldItemPoseCalculation.FromItem(allowed, body, data);
            pose = !data.Heavy && pose.Heavy && leftWeight > 0f
                ? new HeldItemPose(corrected.Item, pose.LeftPalm, new Pose(corrected.FollowPosition, corrected.FollowRotation), body)
                : corrected;
        }

        private void RefreshPose()
        {
            if (!running) return;
            if (!TryBody(out var body, out var settings)) { hasPose = false; ClearTarget(); return; }
            if (!inventory.CanEquip || !networkState.HasActionSnapshot)
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
                    RetainHeavyPalms(body, true);
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
                if (inventory.IsOwner && CanShowHeldItem && selectedId != 0 && registry.TryGetItem(selectedId, out var item) && item &&
                    ResolveClearance(pose.Item, body, data, item, out var allowed))
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
            double followEnd = Mathf.Max(0f, actionData.Settings.MaximumFollowDuration);
            double pauseEnd = followEnd + Mathf.Max(0f, actionData.Settings.EndPosePauseDuration);
            double returnEnd = pauseEnd + Mathf.Max(0f, actionData.Settings.ReturnBlendDuration);
            Quaternion palmToWrist = Quaternion.Inverse(body.Measurements.RightWristToPalmRotation);
            if (stage == RecoveryStage.Follow)
            {
                bool unavailable = releaseUnavailable || tracking && !Matches(projectile);
                bool beyond = false;
                if (actionData.Heavy)
                {
                    pose = RetainedHeavyPose(body);
                    leftWeight = 1f;
                    if (tracking && !unavailable && elapsed < followEnd)
                    {
                        var start = HeavyItemPoseCalculation.ToWorld(followItem, body);
                        float t = LeanTween.easeOutSine(0f, 1f, Mathf.Clamp01((float)elapsed / 0.08f));
                        Pose end = new(projectile.PresentedRootPosition, projectile.PresentedRotation);
                        var destination = HeldItemPoseCalculation.FromItem(end, body, actionData);
                        Pose frame = HeavyItemPoseCalculation.Blend(start, end, t);
                        var candidate = new HeldItemPose(frame,
                            HeavyItemPoseCalculation.Blend(HeavyItemPoseCalculation.ToWorld(followLeft, body), destination.LeftPalm, t),
                            HeavyItemPoseCalculation.Blend(HeavyItemPoseCalculation.ToWorld(followRight, body),
                                new Pose(destination.FollowPosition, destination.FollowRotation), t), body);
                        beyond = !HeavyItemPoseCalculation.InReach(candidate, body, actionData.Reach) ||
                            Physics.Linecast(body.Shoulder, candidate.FollowPosition, registry.EnvironmentMask, QueryTriggerInteraction.Ignore) ||
                            Physics.Linecast(body.LeftShoulder, candidate.LeftPalm.position, registry.EnvironmentMask, QueryTriggerInteraction.Ignore) ||
                            Physics.Linecast(pose.FollowPosition, candidate.FollowPosition, registry.EnvironmentMask, QueryTriggerInteraction.Ignore) ||
                            Physics.Linecast(pose.LeftPalm.position, candidate.LeftPalm.position, registry.EnvironmentMask, QueryTriggerInteraction.Ignore);
                        if (!beyond)
                        {
                            pose = candidate; retainedItem = HeavyItemPoseCalculation.ToLocal(frame, body);
                            RetainHeavyPalms(body);
                        }
                    }
                }
                else
                    pose = HeldItemPoseCalculation.Resolve(body.ToWorld(retainedPosition), body.Rotation * retainedRotation * palmToWrist,
                        body, settings, actionData, out _, soften: false);
                if (!actionData.Heavy && tracking && !unavailable && elapsed < followEnd)
                {
                    Quaternion palm = projectile.PresentedRotation * Quaternion.Inverse(actionData.GripRotation);
                    Vector3 follow = projectile.PresentedRootPosition - palm * actionData.GripPosition;
                    var candidate = HeldItemPoseCalculation.Resolve(Vector3.Lerp(body.ToWorld(followStart), follow,
                        LeanTween.easeOutSine(0f, 1f, Mathf.Clamp01((float)elapsed / 0.08f))),
                        palm * palmToWrist, body, settings, actionData, out beyond, soften: false);
                    if (Physics.Linecast(body.Shoulder, candidate.FollowPosition, registry.EnvironmentMask, QueryTriggerInteraction.Ignore)) beyond = true;
                    else
                    {
                        pose = candidate;
                        retainedPosition = body.ToLocal(pose.FollowPosition);
                        retainedRotation = Quaternion.Inverse(body.Rotation) * pose.FollowRotation;
                    }
                }
                if (unavailable || beyond || elapsed >= followEnd)
                {
                    projectile = null; tracking = false; releaseUnavailable = true;
                }
                if (elapsed >= followEnd) stage = RecoveryStage.Pause;
            }
            if (stage == RecoveryStage.Pause)
            {
                pose = actionData.Heavy ? RetainedHeavyPose(body) :
                    HeldItemPoseCalculation.Resolve(body.ToWorld(retainedPosition), body.Rotation * retainedRotation * palmToWrist,
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

        private HeldItemPose RetainedHeavyPose(in HeldItemBodyFrame body) => new(
            HeavyItemPoseCalculation.ToWorld(retainedItem, body), HeavyItemPoseCalculation.ToWorld(retainedLeft, body),
            HeavyItemPoseCalculation.ToWorld(retainedRight, body), body);

        private void Blend(in HeldItemBodyFrame body, AvatarSettings settings, float t, in HeldItemPoseData data)
        {
            bool holding = selectedDefinition && inventory.CanEquip;
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
                    var reach = HeavyItemPoseCalculation.Reach(frame, body, data);
                    if (reach.TryProject(frame.position, out var position)) frame.position = position;
                    pose = HeldItemPoseCalculation.FromItem(frame, body, data);
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
            avatar.HandTargets.Set(AvatarIKGoal.RightHand, AvatarHandSource.Item, target, weight, weight, reach, fingers);
            targetInstalled = true;
        }
        private void ClearTarget()
        {
            ClearLeftTarget();
            if (!targetInstalled) return;
            avatar.ClearHandTarget(AvatarIKGoal.RightHand);
            targetInstalled = false;
        }
    }
}
