using FishNet.Component.Prediction;
using UnityEngine;

namespace TwoBirds
{
    [RequireComponent(typeof(Rigidbody))]
    public sealed partial class WorldItem : MonoBehaviour, IInteractable
    {
        [SerializeField] private Transform visualRoot;
        [SerializeField, Tooltip("Replaces the default hover tooltip text when set.")] private string tooltipTextOverride;
        [SerializeField, Tooltip("Show only the interaction glyph in the hover tooltip.")] private bool hideTooltipText;
        private readonly RigidbodyMotionState motionState = new();
        private Collider[] colliders;
        private LayerMask[] colliderIncludes, colliderExcludes;
        private Renderer[] renderers;
        private Transform[] parts;
        private WorldItemRegistry registry;
        private OfflineRigidbody offlineRigidbody;
        private ItemCartPhysics cartPhysics;
        private Collider ignoredPlayer;
        private float ignoreUntil;
        private uint localLaunchTick;
        private uint historyStart;
        private Vector3 visualOffset;
        private Pose lastHeldPose;
        private int lastHeldBy = -1;
        private bool hasHeldPose, heldVisible, releaseHandoff;
        private uint handoffOperation, handoffPath;
        private int handoffReleaser;
        private float handoffEnd, handoffDuration;
        private Vector3 handoffOffset, appliedHandoff;
        private Quaternion handoffRotation = Quaternion.identity, appliedHandoffRotation = Quaternion.identity;
        private Vector3 gameplayVisualPosition;
        private Quaternion gameplayVisualRotation;
        private Vector3 handoffBaseLocalPosition;
        private Quaternion handoffBaseLocalRotation;
        internal Vector3 PresentedRootPosition => visualRoot.position;
        internal Quaternion PresentedRotation => visualRoot.rotation;
        private Quaternion visualRotation = Quaternion.identity;
        private Quaternion cosmeticRotation = Quaternion.identity;
        private Vector3 cosmeticSpin;
        private bool rollingSupported;
        private float correctionRemaining;
        private Vector3 defaultScale;
        private bool optimisticPickup;
        private float interactableAfter;
        private int playerHitboxMask;
        private int golfCartMask;
        private ItemUseBehaviour useBehaviour;
        private PlayerEquipment useUser;
        private SphereCollider impactSphere;
        private Vector3 sphereCenter;
        private Vector3 bodySphereCenter;
        private float sphereRadius;
        internal float DropDiameter { get; private set; }
        internal float ReleaseRadius { get; private set; }
        internal ItemReleaseSphere ReleaseSphere { get; private set; }
        private bool geometryCached;
        internal bool ReleaseAvailable => isActiveAndEnabled && Record.State == WorldItemState.World && !optimisticPickup && !RemovalPending;
        internal int PresentedHolder { get; private set; } = -1;
        private Vector3 incomingVelocity;
        private bool incomingSampled;
        private ItemPlayerContact playerContact;
        private double presentedMotionTick;
        private int releasePlayer = -1;
        private uint releaseOperation;

        public Rigidbody Body { get; private set; }
        public ItemDefinition Definition { get; private set; }
        public ItemRecord Record { get; private set; }
        public bool Predicted { get; private set; }
        internal bool Simulating => registry && (registry.Simulates(Record) || Predicted && Record.Simulator < 0);
        internal bool MotionAvailable => !optimisticPickup && !Body.isKinematic;
        internal bool MotionBoundary { get; set; }
        internal bool RemovalPending { get; private set; }
        private bool CosmeticRotation => !Definition.SyncRotation && !Simulating;
        private bool CenteredMotion => !Definition.SyncRotation && impactSphere;
        public bool IsCharging => useBehaviour != null && useBehaviour.IsCharging;
        public float Charge01 => useBehaviour != null ? useBehaviour.Charge01 : 0f;
        public string ActionText => "Pick up";
        public string TooltipTextOverride => tooltipTextOverride;
        public bool HideTooltipText => hideTooltipText;
        public string InputActionPath => "Player/Interact";
        public bool CanInteract => registry != null && (Record.State == WorldItemState.World || registry.OutputReady(Record)) &&
                                   !optimisticPickup && Record.Motion.Id != 0 &&
                                   Time.time >= interactableAfter;

        private void Awake()
        {
            Body = GetComponent<Rigidbody>();
            offlineRigidbody = GetComponent<OfflineRigidbody>();
            useBehaviour = GetComponent<ItemUseBehaviour>();
            playerHitboxMask = LayerMask.GetMask("PlayerItemHitbox");
            golfCartMask = LayerMask.GetMask("GolfCart");
            colliders = GetComponentsInChildren<Collider>(true);
            colliderIncludes = new LayerMask[colliders.Length];
            colliderExcludes = new LayerMask[colliders.Length];
            for (int i = 0; i < colliders.Length; i++)
            {
                colliderIncludes[i] = colliders[i].includeLayers;
                colliderExcludes[i] = colliders[i].excludeLayers;
            }
            foreach (var collider in colliders)
                if (collider is SphereCollider sphere && !sphere.isTrigger) { impactSphere = sphere; break; }
            potionPresentation = GetComponent<PotionPresentation>();
            potionSensor = GetComponentInChildren<PotionContactSensor>(true);
            if (potionSensor) potionTrigger = potionSensor.GetComponent<Collider>();
            if (impactSphere) birdColliderId = impactSphere.GetEntityId();
            renderers = GetComponentsInChildren<Renderer>(true);
            parts = GetComponentsInChildren<Transform>(true);
            defaultScale = transform.localScale;
            foreach (var collider in colliders)
                if (!collider.isTrigger) DropDiameter = Mathf.Max(DropDiameter,
                    2f * ((collider.bounds.center - transform.position).magnitude + collider.bounds.extents.magnitude));
            Body.collisionDetectionMode = CollisionDetectionMode.Discrete;
            Body.isKinematic = true;
        }

        public void Interact() => registry.LocalInventory?.Collect(this);

        internal void CacheReleaseGeometry()
        {
            if (geometryCached) return;
            geometryCached = true;
            colliders ??= GetComponentsInChildren<Collider>(true);
            Vector3 scale = Definition ? Definition.WorldPrefab.transform.localScale : transform.localScale;
            ReleaseRadius = ItemReleaseClearance.EnvelopeRadius(transform, scale, colliders);
            ReleaseSphere = ItemReleaseClearance.Sphere(transform, scale, colliders, ReleaseRadius);
            DropDiameter = Mathf.Max(DropDiameter, 2f * ReleaseRadius);
        }

        internal void StartPickupCooldown()
        {
            if (Record.State != WorldItemState.World || registry.LocalInventory == null ||
                Record.Releaser != registry.LocalInventory.ObjectId) return;
            interactableAfter = Time.time + 1f;
        }

        public void BeginUse(PlayerEquipment user)
        {
            if (useBehaviour == null || !useBehaviour.isActiveAndEnabled) return;
            useUser = user;
            useBehaviour.BeginUse(user);
        }

        public void EndUse()
        {
            if (useUser == null) return;
            useUser = null;
            useBehaviour.EndUse();
        }

        public void CancelUse()
        {
            if (useUser == null) return;
            useUser = null;
            useBehaviour.CancelUse();
        }

        internal void InterruptUse()
        {
            if (useUser != null) useUser.CancelUse();
            CancelUse();
        }

        private void OnDisable() { InterruptUse(); CancelHandoff(); cartPhysics?.Stop(); hasHeldPose = false; }

        internal void Initialize(WorldItemRegistry owner, ItemDefinition definition, ItemRecord record, bool predicted)
        {
            bool firstInitialization = Definition == null;
            Vector3 initialScale = transform.localScale;
            InterruptUse();
            registry = owner;
            playerContact ??= new ItemPlayerContact(owner, motionState, MotionSpherePosition, ReportImpact);
            Definition = definition;
            if (definition.CollideWhileSleeping) cartPhysics ??= new ItemCartPhysics(this, owner);
            if (potionPresentation) potionPresentation.ApplyDefinition(definition);
            defaultScale = definition.WorldPrefab.transform.localScale;
            if (firstInitialization)
            {
                CacheReleaseGeometry();
            }
            birdRegistry = BirdRegistry.Instance;
            birdRock = birdRegistry && birdRegistry.IsRock(definition);
            ResetPresentation();
            Body.mass = definition.Mass;
            Body.linearDamping = definition.LinearDamping;
            Body.angularDamping = definition.AngularDamping;
            Body.sleepThreshold = definition.OverrideSleepThreshold ? definition.SleepThreshold : Physics.sleepThreshold;
            Body.maxLinearVelocity = definition.MaxSpeed > 0f ? definition.MaxSpeed : float.MaxValue;
            Body.maxAngularVelocity = Mathf.Max(50f, definition.InitialSpin.magnitude);
            Body.interpolation = RigidbodyInterpolation.None;
            foreach (var collider in colliders) collider.sharedMaterial = definition.PhysicsMaterial;
            Predicted = predicted;
            localLaunchTick = registry.LocalTick;
            historyStart = localLaunchTick;
            ApplyRecord(record);
            if (firstInitialization && record.State == WorldItemState.World)
            {
                transform.localScale = initialScale;
                CacheImpactSphere();
                if (record.Motion.PositionIsSphereCenter)
                {
                    Body.position = MotionBodyPosition(record.Motion, Body.rotation);
                    transform.position = Body.position;
                }
                if (!Simulating && cartPhysics == null)
                {
                    motionState.Count = 0;
                    AddSample(record.Motion);
                }
            }
        }

        internal void ApplyRecord(ItemRecord record)
        {
            RemoveHandoffPose();
            bool beginHandoff = record.State == WorldItemState.World && Record.State == WorldItemState.Held &&
                hasHeldPose && lastHeldBy == record.Releaser && !record.Sleeping &&
                (heldVisible || registry.TryGetPlayer(record.Releaser, out var pendingHolder) &&
                    pendingHolder.Equipment.HeldPresentation.MatchesPendingRelease(record)) &&
                (!registry.LocalInventory || registry.LocalInventory.ObjectId != record.Releaser);
            Pose departure = lastHeldPose;
            bool wasPredicted = Predicted;
            bool activeSimulation = !optimisticPickup && !Body.isKinematic && Record.State == WorldItemState.World;
            bool preserveMotion = activeSimulation &&
                record.State == WorldItemState.World && record.Simulator >= 0 && registry.Simulates(record) &&
                (wasPredicted || Simulating && record.Motion.Revision == Record.Motion.Revision) &&
                record.Releaser == Record.Releaser && record.Operation == Record.Operation;
            bool newRelease = record.State == WorldItemState.World &&
                (Record.State != WorldItemState.World || record.Releaser != releasePlayer || record.Operation != releaseOperation);
            bool smoothCosmetic = CosmeticRotation && !newRelease &&
                Record.State == WorldItemState.World && motionState.Count > 0;
            SetRecord(record);
            if (cartPhysics != null && !registry.Simulates(record)) Predicted = false;
            for (int i = 0; i < colliders.Length; i++)
            {
                colliders[i].includeLayers = record.State == WorldItemState.CauldronOutput ? (LayerMask)0 : colliderIncludes[i];
                colliders[i].excludeLayers = record.State == WorldItemState.CauldronOutput ? (LayerMask)~0 : colliderExcludes[i];
            }
            potionSampled = false;
            if (potionTrigger) potionTrigger.enabled = record.State == WorldItemState.World && record.Armed;
            if (record.State != WorldItemState.Held) { heldVisible = false; hasHeldPose = false; }
            optimisticPickup = false;
            if (record.State == WorldItemState.Held)
            {
                cartPhysics?.Stop();
                cosmeticSpin = Vector3.zero;
                ClearVisualOffset();
                ClearIgnore();
                Predicted = false;
                StopBody();
                SetLayer(registry.HeldLayer);
                foreach (var collider in colliders) collider.enabled = false;
                AttachHolder();
                motionState.Count = 0;
                return;
            }

            if (record.State == WorldItemState.CauldronOutput)
            {
                cartPhysics?.Stop();
                Predicted = false;
                StopBody(); ClearIgnore(); ClearVisualOffset();
                PresentedHolder = -1;
                transform.SetParent(null, true);
                transform.localScale = defaultScale;
                SetLayer(registry.WorldLayer);
                foreach (var collider in colliders) collider.enabled = false;
                SetVisible(false);
                return;
            }
            PresentedHolder = -1;
            transform.SetParent(null, true);
            transform.localScale = defaultScale;
            offlineRigidbody.SetPredictionManager(cartPhysics != null ? null : registry.PredictionManager);
            cartPhysics?.Start();
            CacheImpactSphere();
            SetLayer(registry.WorldLayer);
            SetVisible(true);
            foreach (var collider in colliders) collider.enabled = collider != potionTrigger || record.Armed;
            Body.collisionDetectionMode = CollisionDetectionMode.Discrete;
            Body.isKinematic = cartPhysics == null && (!Simulating || record.Sleeping && !registry.Simulates(record));
            Body.collisionDetectionMode = Body.isKinematic ? CollisionDetectionMode.Discrete :
                birdRock ? CollisionDetectionMode.ContinuousDynamic : Definition.CollisionDetection;
            if (!preserveMotion && (!activeSimulation || !wasPredicted || record.Sleeping || !Simulating))
                CorrectBody(record.Motion, wasPredicted || smoothCosmetic);
            cosmeticSpin = record.Sleeping ? Vector3.zero : record.Motion.AngularVelocity;
            if (newRelease) { playerContact.Rebase = false; birdRebase = false; }
            if (!Body.isKinematic && record.Sleeping) Body.Sleep();
            if (record.Sleeping) Predicted = false;
            if (!Simulating && cartPhysics == null)
            {
                motionState.Count = 0;
                AddSample(record.Motion);
            }
            if (newRelease && !record.Sleeping && registry.TryGetPlayer(record.Releaser, out var releaser))
                IgnorePlayer(releaser.Hitbox);
            if (beginHandoff)
            {
                hasHeldPose = false;
                releaseHandoff = true;
                handoffOperation = record.Operation; handoffReleaser = record.Releaser; handoffPath = record.Motion.Path;
                handoffDuration = registry.CorrectionDuration;
                handoffEnd = Time.unscaledTime + handoffDuration;
                handoffOffset = departure.position - visualRoot.position;
                handoffRotation = departure.rotation * Quaternion.Inverse(visualRoot.rotation);
                ApplyHandoffPose();
            }
        }

        internal void AttachHolder()
        {
            if (Record.State != WorldItemState.Held) return;
            registry.TryGetPlayer(Record.Holder, out var holder);
            ApplyHeldAttachment(holder, Record.Equipped);
        }

        internal void PredictPickup()
        {
            registry.CancelItemContacts(Record.Motion.Id);
            ConsumeSplat();
            CancelHandoff();
            hasHeldPose = false;
            ResetContactState();
            ClearIgnore();
            optimisticPickup = true;
            StopBody();
            foreach (var collider in colliders) collider.enabled = false;
            SetVisible(false);
        }

        internal void SetRecord(ItemRecord record)
        {
            record = registry.PreserveSplatConsumption(record);
            if (releaseHandoff && (record.State != WorldItemState.World || record.Sleeping || record.Releaser != handoffReleaser ||
                record.Operation != handoffOperation || record.Motion.Path != handoffPath || record.Motion.Boundary)) CancelHandoff();
            if (record.State == WorldItemState.Removed || record.State == WorldItemState.Held &&
                (record.Holder != lastHeldBy || !record.Equipped))
                hasHeldPose = false;
            if (record.State != WorldItemState.World || record.Sleeping ||
                record.Releaser != releasePlayer || record.Operation != releaseOperation)
            {
                ResetContactState();
                releasePlayer = record.Releaser;
                releaseOperation = record.Operation;
            }
            if (record.State == WorldItemState.Removed) InterruptUse();
            Record = record;
            int sleepMask = playerHitboxMask | golfCartMask;
            int excludedLayers = record.Sleeping && !Definition.CollideWhileSleeping
                ? Body.excludeLayers.value | sleepMask
                : Body.excludeLayers.value & ~sleepMask;
            if (Body.excludeLayers.value == excludedLayers) return;
            Body.excludeLayers = excludedLayers;
            if (record.Sleeping && !Body.isKinematic) Body.Sleep();
        }
        internal void Launch(ItemMotion motion)
        {
            CorrectBody(motion, false);
            playerContact.Rebase = false;
            birdRebase = false;
        }

        internal void PredictCartContact(ItemRecord record)
        {
            Predicted = true;
            record.Sleeping = false;
            SetRecord(record);
        }

        internal void PresentHeld(PlayerInventory holder, bool equipped)
        {
            if (Predicted) return;
            ResetContactState();
            ClearIgnore();
            StopBody();
            SetLayer(registry.HeldLayer);
            foreach (var collider in colliders) collider.enabled = false;
            ApplyHeldAttachment(holder, equipped);
        }

        private void ApplyHeldAttachment(PlayerInventory holder, bool equipped)
        {
            PresentedHolder = holder ? holder.ObjectId : -1;
            bool visible = holder && equipped && holder.Equipment.HeldPresentation.CanShowHeldItem;
            heldVisible = visible;
            if (!holder || !equipped || !visible && !holder.Equipment.HeldPresentation.IsPendingRelease(Record.Motion.Id))
                hasHeldPose = false;
            SetVisible(visible);
            if (!visible)
            {
                transform.SetParent(null, true);
                ClearVisualOffset();
                return;
            }
            var equipment = holder.Equipment;
            Transform parent = equipment.HeldPresentation.Attachment;
            transform.SetParent(parent, false);
            Vector3 scale = parent.lossyScale;
            transform.localScale = new Vector3(defaultScale.x / scale.x, defaultScale.y / scale.y, defaultScale.z / scale.z);
            transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            ClearVisualOffset();
        }

        internal void CommitHeldPose(Pose pose, int holder)
        {
            if (Record.State != WorldItemState.Held || PresentedHolder != holder) return;
            transform.SetPositionAndRotation(pose.position, pose.rotation);
            lastHeldPose = new Pose(visualRoot.position, visualRoot.rotation);
            lastHeldBy = holder; hasHeldPose = true;
        }

        private void RemoveHandoffPose()
        {
            if (appliedHandoff == Vector3.zero && appliedHandoffRotation == Quaternion.identity) return;
            visualRoot.SetLocalPositionAndRotation(handoffBaseLocalPosition, handoffBaseLocalRotation);
            appliedHandoff = Vector3.zero; appliedHandoffRotation = Quaternion.identity;
        }

        private void CancelHandoff()
        {
            RemoveHandoffPose(); releaseHandoff = false;
        }

        private void ApplyHandoffPose()
        {
            if (!releaseHandoff) return;
            float remaining = Mathf.Clamp01((handoffEnd - Time.unscaledTime) / handoffDuration);
            if (remaining <= 0f || Record.Sleeping || MotionBoundary || !Body.isKinematic && Body.IsSleeping())
            { CancelHandoff(); return; }
            Vector3 offset = handoffOffset * remaining;
            Vector3 physical = visualRoot.position;
            if (Physics.CheckSphere(physical + offset, ReleaseRadius, registry.EnvironmentMask, QueryTriggerInteraction.Ignore) ||
                offset.sqrMagnitude > 0.000001f && Physics.SphereCast(physical, ReleaseRadius, offset.normalized,
                    out _, offset.magnitude, registry.EnvironmentMask, QueryTriggerInteraction.Ignore))
            { CancelHandoff(); return; }
            gameplayVisualPosition = physical; gameplayVisualRotation = visualRoot.rotation;
            handoffBaseLocalPosition = visualRoot.localPosition; handoffBaseLocalRotation = visualRoot.localRotation;
            appliedHandoff = offset;
            appliedHandoffRotation = Quaternion.Slerp(Quaternion.identity, handoffRotation, remaining);
            visualRoot.SetPositionAndRotation(physical + offset, appliedHandoffRotation * gameplayVisualRotation);
        }

        internal void DetachHeldPresentation()
        {
            heldVisible = hasHeldPose = false;
            transform.SetParent(null, true);
            SetVisible(false);
            ClearVisualOffset();
        }

        internal ItemMotion Capture(uint tick)
        {
            return RigidbodyMotionState.Capture(Body, Record.Motion, tick, CenteredMotion, BodySpherePosition);
        }

        internal void Tick()
        {
            UpdateIgnore();
            if (Predicted && !optimisticPickup)
                motionState.History[registry.LocalTick % (uint)motionState.History.Length] = Capture(registry.LocalTick);
        }

        internal void ReceiveMotion(ItemMotion motion)
        {
            if (Record.Simulator >= 0 && registry.Simulates(Record)) return;
            if (motion.Path != Record.Motion.Path) motion.Boundary = true;
            if (motion.RotationOmitted)
            {
                motion.Rotation = Record.Motion.Rotation;
                motion.AngularVelocity = Record.Motion.AngularVelocity;
            }
            var record = Record;
            record.Motion = motion;
            record.Sleeping = motion.Sleeping;
            if (motion.Sleeping && Predicted)
            {
                ApplyRecord(record);
                return;
            }
            SetRecord(record);
            if (optimisticPickup) return;
            if (cartPhysics != null)
            {
                CorrectBody(motion, true);
                if (motion.Sleeping) Body.Sleep();
                return;
            }
            if (!Predicted)
            {
                if (motion.Boundary)
                {
                    motionState.Count = 0;
                    CorrectBody(motion, CosmeticRotation);
                }
                AddSample(motion);
                return;
            }

            uint correspondingTick = localLaunchTick + motion.Tick - Record.LaunchTick;
            var predicted = motionState.History[correspondingTick % (uint)motionState.History.Length];
            if (correspondingTick >= registry.LocalTick) return;
            Vector3 predictedPosition = CenteredMotion ? MotionSpherePosition(predicted) : predicted.Position;
            Vector3 receivedPosition = CenteredMotion ? MotionSpherePosition(motion) : motion.Position;
            if (correspondingTick >= historyStart && predicted.Tick == correspondingTick &&
                (predictedPosition - receivedPosition).sqrMagnitude <= registry.CorrectionThreshold * registry.CorrectionThreshold)
                return;
            CorrectBody(motion, true);
            localLaunchTick = registry.LocalTick - 1 - (motion.Tick - Record.LaunchTick);
            historyStart = registry.LocalTick + 1;
        }

        private void CorrectBody(ItemMotion motion, bool smooth)
        {
            RemoveHandoffPose();
            ResetBirdContact(true);
            ResetIncomingMotion();
            playerContact.Rebase = true;
            presentedMotionTick = motion.Tick;
            Vector3 visiblePosition = visualRoot.position;
            Quaternion visibleRotation = visualRoot.rotation;
            Vector3 visibleCenter = PresentedSpherePosition;
            RigidbodyMotionState.Apply(Body, motion, bodySphereCenter);
            if (smooth)
            {
                visualRoot.SetPositionAndRotation(visiblePosition, visibleRotation);
                visualOffset = CosmeticRotation ? visibleCenter - BodySpherePosition : visualRoot.localPosition;
                visualRotation = CosmeticRotation ? visibleRotation : visualRoot.localRotation;
                cosmeticRotation = visibleRotation;
                correctionRemaining = registry.CorrectionDuration;
                if (!CosmeticRotation) motionState.Departure(visualOffset, registry.CorrectionDuration);
            }
            else ClearVisualOffset();
            if (!motion.RotationOmitted) cosmeticSpin = motion.Sleeping ? Vector3.zero : motion.AngularVelocity;
            rollingSupported = false;
            playerContact.Reposition(PresentedSpherePosition, BodySpherePosition);
        }

        private void AddSample(ItemMotion motion)
        {
            if (CenteredMotion && !motion.PositionIsSphereCenter)
            {
                motion.Position = MotionSpherePosition(motion);
                Vector3 centerFromMass = bodySphereCenter - Body.centerOfMass;
                motion.Velocity += Vector3.Cross(motion.AngularVelocity, motion.Rotation * centerFromMass);
                motion.PositionIsSphereCenter = true;
            }
            motionState.Add(motion);
        }

        internal void Present()
        {
            RemoveHandoffPose();
            if (registry == null || optimisticPickup || Record.State != WorldItemState.World) return;
            Vector3 presentedVelocity = Vector3.zero;
            if (!Simulating && cartPhysics == null && motionState.Count > 0)
            {
                ItemMotion latest = motionState.Samples[motionState.Count - 1];
                double tick = latest.Tick + (Time.unscaledTime - motionState.ReceivedAt - registry.InterpolationDelay) / registry.TickDelta;
                presentedMotionTick = System.Math.Max(presentedMotionTick, System.Math.Max(motionState.Samples[0].Tick, tick));
                ItemMotion motion = PresentedMotionAt(presentedMotionTick, out presentedVelocity);
                if (!motion.RotationOmitted) Body.rotation = motion.Rotation;
                Body.position = MotionBodyPosition(motion, Body.rotation);
                transform.SetPositionAndRotation(Body.position, Body.rotation);
            }
            float remaining = 0f;
            if (correctionRemaining > 0f)
            {
                correctionRemaining = Mathf.Max(0f, correctionRemaining - Time.deltaTime);
                remaining = correctionRemaining / registry.CorrectionDuration;
                if (CosmeticRotation)
                {
                    if (Record.Sleeping) cosmeticRotation = Quaternion.Slerp(Body.rotation, visualRotation, remaining);
                }
                else
                {
                    motionState.PresentOffset(visualRoot, registry.CorrectionDuration);
                    visualRoot.localRotation = Quaternion.Slerp(Quaternion.identity, visualRotation, remaining);
                }
            }
            if (!CosmeticRotation || motionState.Count == 0) { ApplyHandoffPose(); return; }
            if (!Record.Sleeping)
            {
                float radius = impactSphere ? sphereRadius : DropDiameter * 0.25f;
                float tolerance = radius * (rollingSupported ? 0.1f : 0.05f);
                RaycastHit ground = default;
                rollingSupported = radius > 0f && Physics.Raycast(BodySpherePosition, Vector3.down, out ground,
                    radius * 2f + tolerance, registry.EnvironmentMask, QueryTriggerInteraction.Ignore) &&
                    ground.normal.y > 0.5f && ground.distance * ground.normal.y <= radius + tolerance;
                if (rollingSupported)
                {
                    Vector3 rolling = Vector3.Cross(ground.normal, presentedVelocity) / radius;
                    cosmeticSpin = Vector3.Lerp(cosmeticSpin, rolling, 1f - Mathf.Exp(-12f * Time.deltaTime));
                }
                else cosmeticSpin *= Mathf.Exp(-Definition.AngularDamping * Time.deltaTime);
                float speed = cosmeticSpin.magnitude;
                if (speed > 0f)
                    cosmeticRotation = Quaternion.AngleAxis(speed * Mathf.Rad2Deg * Time.deltaTime, cosmeticSpin / speed) * cosmeticRotation;
            }
            Vector3 center = BodySpherePosition + visualOffset * remaining;
            visualRoot.SetPositionAndRotation(center - cosmeticRotation * bodySphereCenter, cosmeticRotation);
            ApplyHandoffPose();
        }

        private ItemMotion PresentedMotionAt(double tick, out Vector3 velocity) =>
            motionState.Sample(tick, Record.Sleeping, sphereRadius, registry.EnvironmentMask, registry.TickDelta, out velocity);

        private Vector3 MotionSpherePosition(ItemMotion motion) => motion.PositionIsSphereCenter
            ? motion.Position : motion.Position + motion.Rotation * bodySphereCenter;

        private Vector3 MotionBodyPosition(ItemMotion motion, Quaternion rotation) => motion.PositionIsSphereCenter
            ? motion.Position - rotation * bodySphereCenter : motion.Position;

        internal void IgnorePlayer(PlayerItemHitbox player)
        {
            ClearIgnore();
            if (player == null) return;
            ignoredPlayer = player.Collider;
            ignoreUntil = Time.time + registry.ReleaseGrace;
            foreach (var collider in colliders) Physics.IgnoreCollision(collider, ignoredPlayer, true);
        }

        private void UpdateIgnore()
        {
            if (ignoredPlayer != null && (Time.time >= ignoreUntil || Separated())) ClearIgnore();
        }

        private bool Separated()
        {
            foreach (var collider in colliders)
                if (Physics.ComputePenetration(collider, collider.transform.position, collider.transform.rotation,
                    ignoredPlayer, ignoredPlayer.transform.position, ignoredPlayer.transform.rotation, out _, out _)) return false;
            return true;
        }

        private void ClearIgnore()
        {
            if (ignoredPlayer != null)
                foreach (var collider in colliders) Physics.IgnoreCollision(collider, ignoredPlayer, false);
            ignoredPlayer = null;
            ignoreUntil = 0f;
        }

        private void StopBody()
        {
            offlineRigidbody.SetPredictionManager(null);
            if (!Body.isKinematic)
            {
                Body.linearVelocity = Vector3.zero;
                Body.angularVelocity = Vector3.zero;
            }
            Body.collisionDetectionMode = CollisionDetectionMode.Discrete;
            Body.isKinematic = true;
        }

        private void SetLayer(int layer)
        {
            foreach (var part in parts)
                if (!potionSensor || part != potionSensor.transform) part.gameObject.layer = layer;
        }

        private void SetVisible(bool visible)
        {
            foreach (var renderer in renderers) renderer.enabled = visible;
        }

        private void ClearVisualOffset()
        {
            RemoveHandoffPose();
            visualRoot.localPosition = Vector3.zero;
            visualRoot.localRotation = Quaternion.identity;
            visualOffset = Vector3.zero;
            visualRotation = Quaternion.identity;
            cosmeticRotation = visualRoot.rotation;
            rollingSupported = false;
            correctionRemaining = 0f;
            motionState.Departure(Vector3.zero, 0f);
        }

        private void ResetPresentation()
        {
            CancelHandoff();
            hasHeldPose = false; lastHeldBy = -1;
            PresentedHolder = -1;
            ClearIgnore();
            ClearVisualOffset();
            presentedMotionTick = 0d;
            ResetContactState();
            System.Array.Clear(motionState.History, 0, motionState.History.Length);
            System.Array.Clear(motionState.Samples, 0, motionState.Samples.Length);
            motionState.Count = 0;
            motionState.ReceivedAt = 0f;
            localLaunchTick = historyStart = 0;
            optimisticPickup = false;
            MotionBoundary = RemovalPending = false;
            cosmeticSpin = Vector3.zero;
        }

        internal void ReturnToPool()
        {
            SplatTargetLifetime.Invalidate(transform);
            InterruptUse();
            StopBody();
            ResetPresentation();
            Record = default;
            Predicted = false;
            transform.SetParent(registry.transform, false);
            transform.localScale = defaultScale;
            gameObject.SetActive(false);
        }

        private void CacheImpactSphere()
        {
            if (impactSphere == null) return;
            sphereCenter = transform.InverseTransformPoint(impactSphere.transform.TransformPoint(impactSphere.center));
            bodySphereCenter = Vector3.Scale(sphereCenter, transform.lossyScale);
            Vector3 scale = impactSphere.transform.lossyScale;
            sphereRadius = impactSphere.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
        }

        private Vector3 BodySpherePosition => Body.position + Body.rotation * bodySphereCenter;
        private Vector3 PresentedSpherePosition => appliedHandoff == Vector3.zero && appliedHandoffRotation == Quaternion.identity
            ? visualRoot.TransformPoint(sphereCenter)
            : gameplayVisualPosition + gameplayVisualRotation * Vector3.Scale(sphereCenter, visualRoot.lossyScale);

        private bool ContactEligible => impactSphere && impactSphere.enabled && !impactSphere.isTrigger &&
            Record.State == WorldItemState.World && !Record.Sleeping &&
            !optimisticPickup && Record.Motion.Id != 0;

        private ItemContactFrame ContactFrame => new() { Eligible = ContactEligible, Simulating = Simulating,
            Sleeping = !Body.isKinematic && Body.IsSleeping(), PresentedCenter = PresentedSpherePosition,
            BodyCenter = BodySpherePosition, Radius = sphereRadius, IgnoredPlayer = ignoredPlayer,
            PresentedTick = presentedMotionTick };

        internal void BeforePhysics()
        {
            cartPhysics?.BeforePhysics();
            BeforeBirdPhysics();
            UpdateIgnore();
            incomingSampled = ContactEligible && !Body.isKinematic && !Body.IsSleeping();
            incomingVelocity = incomingSampled ? Body.linearVelocity : Vector3.zero;
            playerContact.BeforePhysics(ContactFrame, incomingSampled);
        }

        internal void AfterPhysics(float seconds)
        {
            AfterBirdPhysics(seconds);
            playerContact.AfterPhysics(BodySpherePosition, seconds);
        }

        internal void SamplePlayerContact(PlayerItemHitbox player)
        {
            UpdateIgnore();
            playerContact.SamplePlayerContact(player, ContactFrame);
        }

        private void ResetIncomingMotion()
        {
            incomingVelocity = default;
            incomingSampled = false;
            playerContact.ResetIncomingMotion();
        }

        private void ResetContactState()
        {
            ResetBirdContact();
            playerContact?.Reset();
            releasePlayer = -1;
            releaseOperation = 0;
        }

        private void ReportImpact(PlayerItemHitbox player, Vector3 rockVelocity, Vector3 playerVelocity, Vector3 intoPlayer, Vector3 point)
        {
            registry.QueueSplat(this, player.Collider, point, -intoPlayer);
            float speed = Mathf.Max(0f, Vector3.Dot(rockVelocity - playerVelocity, intoPlayer));
            float incoming = Vector3.Dot(rockVelocity, intoPlayer);
            bool qualifies = incoming >= Definition.MinimumImpactSpeed && speed >= Definition.MinimumImpactSpeed;
            bool shove = !Definition.DontPushPlayer && Definition.ImpulseMultiplier > 0f &&
                (Definition.UseSharedImpactThreshold ? qualifies : speed >= Definition.MinimumImpactSpeed);
            Vector3 velocityChange = shove ? intoPlayer * speed * Definition.ImpulseMultiplier : Vector3.zero;
            var tuning = player.Health.Settings;
            if (Definition.UseSharedImpactThreshold ? qualifies : incoming >= tuning.ItemDamageSpeed && speed >= tuning.ItemDamageSpeed)
            {
                playerContact.Damage(player, Definition.OverrideCollisionDamage ? Definition.CollisionDamage : tuning.ItemCollisionDamage, velocityChange);
            }
            if (velocityChange.sqrMagnitude > 0f || !WorldItemRegistry.Finite(velocityChange))
                player.QueueItemImpact(Record.Motion.Id, Record.Releaser, Record.Operation,
                    registry.IsHost ? "host" : Predicted ? "predicted" : "snapshot", rockVelocity, playerVelocity, intoPlayer, velocityChange);
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (registry && registry.Replaying) return;
            SplatCollision(collision);
            cartPhysics?.Contact(collision);
            CancelHandoff();
            BirdContact(collision);
            PotionCollision(collision);
            if (birdRock && registry && Simulating && !registry.Replaying && !optimisticPickup &&
                Record.State == WorldItemState.World && collision.impulse.sqrMagnitude > 0.000001f &&
                (birdRegistry.Settings.SolidMask.value & (1 << collision.collider.gameObject.layer)) != 0)
                MotionBoundary = true;
            if (registry == null || !registry.IsHost || !Simulating || registry.Replaying || !ContactEligible || !incomingSampled) return;
            var contact = collision.GetContact(0);
            if (contact.thisCollider != impactSphere) return;
            if (!collision.collider.TryGetComponent<PlayerItemHitbox>(out var player) || !player.Motor.IsOwner) return;
            UpdateIgnore();
            playerContact.HostContact(player, ContactFrame, incomingVelocity, -contact.normal, contact.point);
        }

        private void OnTriggerEnter(Collider other)
        {
            if (registry && Record.State == WorldItemState.World && Simulating && !registry.Replaying && other.TryGetComponent<ItemKillVolume>(out _))
                RemovalPending = true;
        }

#if UNITY_INCLUDE_INSTRUMENTATION
        internal object AISnapshot() => new
        {
            subject = "item:" + Record.Motion.Id, epoch = registry.AIEpoch, revision = Record.Motion.Revision,
            sequence = Record.Motion.Sequence, path = Record.Motion.Path, motion_tick = Record.Motion.Tick,
            local_tick = registry.LocalTick, server_tick = registry.ServerTick,
            releaser = Record.Releaser, operation = Record.Operation, launch_tick = Record.LaunchTick,
            simulator = Record.Simulator, simulating = Simulating, predicted = Predicted, holder = Record.Holder,
            state = Record.State.ToString(), sleeping = Record.Sleeping, equipped = Record.Equipped,
            body_position = AILogger.V(Body.position), body_rotation = AILogger.Q(Body.rotation),
            graphics_position = AILogger.V(visualRoot.position), graphics_rotation = AILogger.Q(visualRoot.rotation),
            body_velocity = AILogger.V(Body.linearVelocity), motion_velocity = AILogger.V(Record.Motion.Velocity),
            angular_velocity = AILogger.V(Body.angularVelocity), kinematic = Body.isKinematic
        };
#endif
    }
}
