using System.Collections.Generic;
using FishNet.Component.Prediction;
using UnityEngine;

namespace TwoBirds
{
    [RequireComponent(typeof(Rigidbody))]
    public sealed partial class WorldItem : MonoBehaviour, IInteractable
    {
        private struct ContactSegment
        {
            public Vector3 End;
            public Vector3 Velocity;
            public float Seconds;
            public uint Tick;
        }

        [SerializeField] private Transform visualRoot;
        [SerializeField, Tooltip("Replaces the default hover tooltip text when set.")] private string tooltipTextOverride;
        [SerializeField, Tooltip("Show only the interaction glyph in the hover tooltip.")] private bool hideTooltipText;
        private readonly ItemMotion[] history = new ItemMotion[128];
        private readonly ItemMotion[] samples = new ItemMotion[16];
        private Collider[] colliders;
        private Renderer[] renderers;
        private Transform[] parts;
        private WorldItemRegistry registry;
        private OfflineRigidbody offlineRigidbody;
        private Collider ignoredPlayer;
        private float ignoreUntil;
        private float sampleReceivedAt;
        private int sampleCount;
        private uint localLaunchTick;
        private uint historyStart;
        private Vector3 visualOffset;
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
        private Vector3 incomingVelocity;
        private bool incomingSampled;
        private Vector3 physicsStartSphere;
        private readonly List<ContactSegment> physicsSegments = new();
        private float contactSeconds;
        private bool physicsContactSampled;
        private PlayerItemHitbox contactPlayer;
        private uint contactGeneration;
        private uint contactReset;
        private Vector3 previousSphere;
        private Vector3 previousCorrectionOffset;
        private Vector3 previousPlayer;
        private Vector3 previousPlayerVelocity;
        private Vector3 previousPlayerCorrection;
        private double presentedMotionTick;
        private double previousMotionTick;
        private bool hasContactPose;
        private bool touchingPlayer;
        private bool rebaseContactPose;
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
        public bool CanInteract => registry != null && Record.State == WorldItemState.World &&
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
            impactSphere = GetComponentInChildren<SphereCollider>(true);
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

        private void OnDisable() => InterruptUse();

        internal void Initialize(WorldItemRegistry owner, ItemDefinition definition, ItemRecord record, bool predicted)
        {
            bool firstInitialization = Definition == null;
            Vector3 initialScale = transform.localScale;
            InterruptUse();
            registry = owner;
            Definition = definition;
            defaultScale = definition.WorldPrefab.transform.localScale;
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
                if (!Simulating)
                {
                    sampleCount = 0;
                    AddSample(record.Motion);
                }
            }
        }

        internal void ApplyRecord(ItemRecord record)
        {
            bool wasPredicted = Predicted;
            bool activeSimulation = !optimisticPickup && !Body.isKinematic && Record.State == WorldItemState.World;
            bool preserveMotion = activeSimulation &&
                record.State == WorldItemState.World && record.Simulator >= 0 && registry.Simulates(record) &&
                (wasPredicted || Simulating && record.Motion.Revision == Record.Motion.Revision) &&
                record.Releaser == Record.Releaser && record.Operation == Record.Operation;
            bool newRelease = record.State == WorldItemState.World &&
                (Record.State != WorldItemState.World || record.Releaser != releasePlayer || record.Operation != releaseOperation);
            bool smoothCosmetic = CosmeticRotation && !newRelease &&
                Record.State == WorldItemState.World && sampleCount > 0;
            SetRecord(record);
            optimisticPickup = false;
            if (record.State == WorldItemState.Held)
            {
                cosmeticSpin = Vector3.zero;
                ClearVisualOffset();
                ClearIgnore();
                Predicted = false;
                StopBody();
                SetLayer(registry.HeldLayer);
                foreach (var collider in colliders) collider.enabled = false;
                AttachHolder();
                sampleCount = 0;
                return;
            }

            transform.SetParent(null, true);
            transform.localScale = defaultScale;
            offlineRigidbody.SetPredictionManager(registry.PredictionManager);
            CacheImpactSphere();
            SetLayer(registry.WorldLayer);
            SetVisible(true);
            foreach (var collider in colliders) collider.enabled = true;
            Body.collisionDetectionMode = CollisionDetectionMode.Discrete;
            Body.isKinematic = !Simulating || record.Sleeping && !registry.Simulates(record);
            Body.collisionDetectionMode = Body.isKinematic ? CollisionDetectionMode.Discrete :
                birdRock ? CollisionDetectionMode.ContinuousDynamic : Definition.CollisionDetection;
            if (!preserveMotion && (!activeSimulation || !wasPredicted || record.Sleeping || !Simulating))
                CorrectBody(record.Motion, wasPredicted || smoothCosmetic);
            cosmeticSpin = record.Sleeping ? Vector3.zero : record.Motion.AngularVelocity;
            if (newRelease) { rebaseContactPose = false; birdRebase = false; }
            if (!Body.isKinematic && record.Sleeping) Body.Sleep();
            if (record.Sleeping) Predicted = false;
            if (!Simulating)
            {
                sampleCount = 0;
                AddSample(record.Motion);
            }
            if (newRelease && !record.Sleeping && registry.TryGetPlayer(record.Releaser, out var releaser))
                IgnorePlayer(releaser.Hitbox);
        }

        internal void AttachHolder()
        {
            if (Record.State != WorldItemState.Held) return;
            bool equipped = Record.Equipped && registry.TryGetPlayer(Record.Holder, out var player) && player.CanEquip;
            SetVisible(equipped);
            if (!equipped)
            {
                transform.SetParent(null, true);
                return;
            }
            registry.TryGetPlayer(Record.Holder, out var holder);
            transform.SetParent(holder.Equipment.HeldTransform, false);
            transform.localPosition = Vector3.zero;
            transform.localRotation = Definition.WorldPrefab.transform.localRotation;
            transform.localScale = defaultScale;
            ClearVisualOffset();
        }

        internal void PredictPickup()
        {
            ResetContactState();
            ClearIgnore();
            optimisticPickup = true;
            StopBody();
            foreach (var collider in colliders) collider.enabled = false;
            SetVisible(false);
        }

        internal void SetRecord(ItemRecord record)
        {
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
            int excludedLayers = record.Sleeping
                ? Body.excludeLayers.value | sleepMask
                : Body.excludeLayers.value & ~sleepMask;
            if (Body.excludeLayers.value == excludedLayers) return;
            Body.excludeLayers = excludedLayers;
            if (record.Sleeping && !Body.isKinematic) Body.Sleep();
        }
        internal void Launch(ItemMotion motion)
        {
            CorrectBody(motion, false);
            rebaseContactPose = false;
            birdRebase = false;
        }

        internal void PresentHeld(PlayerInventory holder, bool equipped)
        {
            equipped &= holder.CanEquip;
            if (Predicted) return;
            ResetContactState();
            ClearIgnore();
            StopBody();
            SetLayer(registry.HeldLayer);
            foreach (var collider in colliders) collider.enabled = false;
            SetVisible(equipped);
            if (!equipped) return;
            transform.SetParent(holder.Equipment.HeldTransform, false);
            transform.localPosition = Vector3.zero;
            transform.localRotation = Definition.WorldPrefab.transform.localRotation;
            transform.localScale = defaultScale;
            ClearVisualOffset();
        }

        internal ItemMotion Capture(uint tick)
        {
            return new ItemMotion
            {
                Id = Record.Motion.Id, Revision = Record.Motion.Revision, Tick = tick, Sequence = Record.Motion.Sequence,
                Path = Record.Motion.Path,
                Sleeping = !Body.isKinematic && Body.IsSleeping(),
                Position = CenteredMotion ? BodySpherePosition : Body.position, Rotation = Body.rotation,
                PositionIsSphereCenter = CenteredMotion,
                Velocity = Body.isKinematic ? Vector3.zero : CenteredMotion ? Body.GetPointVelocity(BodySpherePosition) : Body.linearVelocity,
                AngularVelocity = Body.isKinematic ? Vector3.zero : Body.angularVelocity
            };
        }

        internal void Tick()
        {
            UpdateIgnore();
            if (Predicted && !optimisticPickup)
                history[registry.LocalTick % (uint)history.Length] = Capture(registry.LocalTick);
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
            if (!Predicted)
            {
                if (motion.Boundary)
                {
                    sampleCount = 0;
                    CorrectBody(motion, CosmeticRotation);
                }
                AddSample(motion);
                return;
            }

            uint correspondingTick = localLaunchTick + motion.Tick - Record.LaunchTick;
            var predicted = history[correspondingTick % (uint)history.Length];
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
            ResetBirdContact(true);
            ResetIncomingMotion();
            rebaseContactPose = true;
            presentedMotionTick = previousMotionTick = motion.Tick;
            Vector3 visiblePosition = visualRoot.position;
            Quaternion visibleRotation = visualRoot.rotation;
            Vector3 visibleCenter = PresentedSpherePosition;
            if (!motion.RotationOmitted) Body.rotation = motion.Rotation;
            Body.position = MotionBodyPosition(motion, Body.rotation);
            transform.SetPositionAndRotation(Body.position, Body.rotation);
            if (!Body.isKinematic)
            {
                if (!motion.RotationOmitted) Body.angularVelocity = motion.AngularVelocity;
                Body.linearVelocity = motion.PositionIsSphereCenter
                    ? motion.Velocity - Vector3.Cross(Body.angularVelocity, BodySpherePosition - Body.worldCenterOfMass)
                    : motion.Velocity;
            }
            if (smooth)
            {
                visualRoot.SetPositionAndRotation(visiblePosition, visibleRotation);
                visualOffset = CosmeticRotation ? visibleCenter - BodySpherePosition : visualRoot.localPosition;
                visualRotation = CosmeticRotation ? visibleRotation : visualRoot.localRotation;
                cosmeticRotation = visibleRotation;
                correctionRemaining = registry.CorrectionDuration;
            }
            else ClearVisualOffset();
            if (!motion.RotationOmitted) cosmeticSpin = motion.Sleeping ? Vector3.zero : motion.AngularVelocity;
            rollingSupported = false;
            if (hasContactPose)
            {
                previousSphere = PresentedSpherePosition;
                previousCorrectionOffset = previousSphere - BodySpherePosition;
            }
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
            if (sampleCount == samples.Length)
            {
                System.Array.Copy(samples, 1, samples, 0, samples.Length - 1);
                sampleCount--;
            }
            samples[sampleCount++] = motion;
            sampleReceivedAt = Time.unscaledTime;
        }

        internal void Present()
        {
            if (registry == null || optimisticPickup || Record.State != WorldItemState.World) return;
            Vector3 presentedVelocity = Vector3.zero;
            if (!Simulating && sampleCount > 0)
            {
                ItemMotion latest = samples[sampleCount - 1];
                double tick = latest.Tick + (Time.unscaledTime - sampleReceivedAt - registry.InterpolationDelay) / registry.TickDelta;
                presentedMotionTick = System.Math.Max(presentedMotionTick, System.Math.Max(samples[0].Tick, tick));
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
                    visualRoot.localPosition = visualOffset * remaining;
                    visualRoot.localRotation = Quaternion.Slerp(Quaternion.identity, visualRotation, remaining);
                }
            }
            if (!CosmeticRotation || sampleCount == 0) return;
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
        }

        private ItemMotion PresentedMotionAt(double tick, out Vector3 velocity)
        {
            ItemMotion from = samples[0], to = from;
            for (int i = 1; i < sampleCount; i++)
            {
                to = samples[i];
                if (to.Tick >= tick) break;
                from = to;
            }
            float amount = to.Tick == from.Tick ? 1f : Mathf.Clamp01((float)((tick - from.Tick) / (to.Tick - from.Tick)));
            var motion = new ItemMotion { Position = Vector3.Lerp(from.Position, to.Position, amount),
                Rotation = Quaternion.Slerp(from.Rotation, to.Rotation, amount), RotationOmitted = to.RotationOmitted,
                PositionIsSphereCenter = to.PositionIsSphereCenter };
            velocity = to.Tick > from.Tick && tick >= from.Tick
                ? (to.Position - from.Position) / (float)((to.Tick - from.Tick) * registry.TickDelta) : Vector3.zero;
            ItemMotion latest = samples[sampleCount - 1];
            if (!Record.Sleeping && tick > latest.Tick)
            {
                float seconds = Mathf.Min((float)((tick - latest.Tick) * registry.TickDelta), 0.1f);
                Vector3 travel = latest.Velocity * seconds;
                velocity = seconds < 0.1f ? latest.Velocity : Vector3.zero;
                RaycastHit hit;
                bool blocked = motion.PositionIsSphereCenter
                    ? Physics.SphereCast(latest.Position, sphereRadius, travel.normalized, out hit, travel.magnitude,
                        registry.EnvironmentMask, QueryTriggerInteraction.Ignore)
                    : Physics.Raycast(latest.Position, travel.normalized, out hit, travel.magnitude,
                        registry.EnvironmentMask, QueryTriggerInteraction.Ignore);
                if (!blocked) motion.Position += travel;
                else
                {
                    motion.Position = latest.Position + travel.normalized * Mathf.Max(0f, hit.distance - 0.01f);
                    velocity = Vector3.zero;
                }
            }
            return motion;
        }

        private Vector3 SphereAt(double tick)
        {
            ItemMotion motion = PresentedMotionAt(tick, out _);
            return MotionSpherePosition(motion);
        }

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
            foreach (var part in parts) part.gameObject.layer = layer;
        }

        private void SetVisible(bool visible)
        {
            foreach (var renderer in renderers) renderer.enabled = visible;
        }

        private void ClearVisualOffset()
        {
            visualRoot.localPosition = Vector3.zero;
            visualRoot.localRotation = Quaternion.identity;
            visualOffset = Vector3.zero;
            visualRotation = Quaternion.identity;
            cosmeticRotation = visualRoot.rotation;
            rollingSupported = false;
            correctionRemaining = 0f;
        }

        private void ResetPresentation()
        {
            ClearIgnore();
            ClearVisualOffset();
            presentedMotionTick = previousMotionTick = 0d;
            ResetContactState();
            System.Array.Clear(history, 0, history.Length);
            System.Array.Clear(samples, 0, samples.Length);
            sampleCount = 0;
            sampleReceivedAt = 0f;
            localLaunchTick = historyStart = 0;
            optimisticPickup = false;
            MotionBoundary = RemovalPending = false;
            cosmeticSpin = Vector3.zero;
        }

        internal void ReturnToPool()
        {
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
        private Vector3 PresentedSpherePosition => visualRoot.TransformPoint(sphereCenter);

        private bool ContactEligible => impactSphere != null && impactSphere.enabled && !impactSphere.isTrigger &&
            Definition.ImpulseMultiplier > 0f &&
            Record.State == WorldItemState.World && !Record.Sleeping &&
            !optimisticPickup && Record.Motion.Id != 0;

        internal void BeforePhysics()
        {
            BeforeBirdPhysics();
            UpdateIgnore();
            incomingSampled = ContactEligible && !Body.isKinematic && !Body.IsSleeping();
            incomingVelocity = incomingSampled ? Body.linearVelocity : Vector3.zero;
            if (!ContactEligible) ResetContactSamples();
            physicsContactSampled = incomingSampled && !registry.IsHost;
            if (physicsContactSampled) physicsStartSphere = BodySpherePosition;
        }

        internal void AfterPhysics(float seconds)
        {
            AfterBirdPhysics(seconds);
            if (!physicsContactSampled) return;
            Vector3 end = BodySpherePosition;
            physicsSegments.Add(new ContactSegment { End = end, Velocity = (end - physicsStartSphere) / seconds,
                Seconds = seconds, Tick = registry.LocalTick });
            contactSeconds += seconds;
            physicsContactSampled = false;
        }

        internal void SamplePlayerContact(PlayerItemHitbox player)
        {
            if (!ContactEligible || player == null || player.Suspended || !player.Motor.IsOwner ||
                !Body.isKinematic && Body.IsSleeping())
            {
                ResetContactSamples();
                return;
            }
            if (contactPlayer != player || contactGeneration != player.Motor.ImpactGeneration || contactReset != player.Motor.ResetRevision)
            {
                bool rebase = rebaseContactPose || contactPlayer != null;
                ResetContactSamples();
                contactPlayer = player;
                contactGeneration = player.Motor.ImpactGeneration;
                contactReset = player.Motor.ResetRevision;
                rebaseContactPose = rebase;
            }
            Vector3 sphere = PresentedSpherePosition;
            Vector3 correctionOffset = sphere - BodySpherePosition;
            Vector3 correctionDelta = correctionOffset - previousCorrectionOffset;
            Vector3 from = previousSphere + correctionDelta;
            Vector3 center = player.PresentedCenter;
            Vector3 playerCorrection = player.PresentationCorrection - previousPlayerCorrection;
            Vector3 playerFrom = previousPlayer + playerCorrection;
            UpdateIgnore();
            if (rebaseContactPose || correctionDelta.sqrMagnitude > 0f || playerCorrection.sqrMagnitude > 0f)
            {
                touchingPlayer = hasContactPose
                    ? player.OverlapsSphere(from, sphereRadius, playerFrom)
                    : player.OverlapsSphere(sphere, sphereRadius, center);
                rebaseContactPose = false;
            }
            if (hasContactPose)
            {
                if (Simulating)
                {
                    float elapsed = 0f;
                    Vector3 startPlayer = playerFrom;
                    foreach (var segment in physicsSegments)
                    {
                        elapsed += segment.Seconds;
                        Vector3 endPlayer = Vector3.Lerp(playerFrom, center, elapsed / contactSeconds);
                        Vector3 end = segment.End + correctionOffset;
                        Vector3 playerVelocity = player.IncomingVelocityAt(segment.Tick);
                        SweepContact(player, from, end, startPlayer, endPlayer, segment.Velocity, playerVelocity, playerVelocity);
                        from = end;
                        startPlayer = endPlayer;
                    }
                    if (physicsSegments.Count == 0)
                        SweepContact(player, from, sphere, playerFrom, center, Vector3.zero, previousPlayerVelocity, player.PresentedVelocity);
                }
                else SweepRemoteContacts(player, from, sphere, correctionOffset, playerFrom, center);
            }
            previousSphere = sphere;
            previousCorrectionOffset = correctionOffset;
            previousPlayer = center;
            previousPlayerVelocity = player.PresentedVelocity;
            previousPlayerCorrection = player.PresentationCorrection;
            previousMotionTick = presentedMotionTick;
            hasContactPose = true;
            physicsSegments.Clear();
            contactSeconds = 0f;
        }

        private void SweepRemoteContacts(PlayerItemHitbox player, Vector3 from, Vector3 sphere, Vector3 offset,
            Vector3 playerFrom, Vector3 center)
        {
            double start = previousMotionTick;
            if (sampleCount == 0 || presentedMotionTick <= start)
            {
                SweepContact(player, from, sphere, playerFrom, center, Vector3.zero, previousPlayerVelocity, player.PresentedVelocity);
                return;
            }
            if (start < samples[0].Tick)
            {
                start = samples[0].Tick;
                from = SphereAt(start) + offset;
                touchingPlayer = player.OverlapsSphere(from, sphereRadius, playerFrom);
            }
            double duration = presentedMotionTick - start;
            if (duration <= 0d)
            {
                SweepContact(player, from, sphere, playerFrom, center, Vector3.zero, previousPlayerVelocity, player.PresentedVelocity);
                return;
            }
            double cursor = start;
            Vector3 startPlayer = playerFrom;
            Vector3 startVelocity = previousPlayerVelocity;
            while (cursor < presentedMotionTick)
            {
                double endTick = presentedMotionTick;
                for (int i = 0; i < sampleCount; i++)
                    if (samples[i].Tick > cursor) { endTick = System.Math.Min(endTick, samples[i].Tick); break; }
                double extrapolationEnd = samples[sampleCount - 1].Tick + 0.1d / registry.TickDelta;
                if (extrapolationEnd > cursor) endTick = System.Math.Min(endTick, extrapolationEnd);
                float amount = (float)((endTick - start) / duration);
                Vector3 end = endTick == presentedMotionTick ? sphere : SphereAt(endTick) + offset;
                Vector3 endPlayer = Vector3.Lerp(playerFrom, center, amount);
                Vector3 endVelocity = Vector3.Lerp(previousPlayerVelocity, player.PresentedVelocity, amount);
                PresentedMotionAt((cursor + endTick) * 0.5d, out Vector3 rockVelocity);
                if ((end - from).sqrMagnitude == 0f) rockVelocity = Vector3.zero;
                SweepContact(player, from, end, startPlayer, endPlayer, rockVelocity, startVelocity, endVelocity);
                from = end;
                startPlayer = endPlayer;
                startVelocity = endVelocity;
                cursor = endTick;
            }
        }

        private void SweepContact(PlayerItemHitbox player, Vector3 from, Vector3 to, Vector3 playerFrom,
            Vector3 playerTo, Vector3 rockVelocity, Vector3 playerVelocityFrom, Vector3 playerVelocityTo)
        {
            if (ignoredPlayer != player.Collider && !touchingPlayer &&
                player.SweepSphere(from, to, sphereRadius, playerFrom, playerTo, out Vector3 intoPlayer, out float fraction))
                ReportImpact(player, rockVelocity, Vector3.Lerp(playerVelocityFrom, playerVelocityTo, fraction), intoPlayer);
            touchingPlayer = player.OverlapsSphere(to, sphereRadius, playerTo);
        }

        private void ResetIncomingMotion()
        {
            incomingVelocity = physicsStartSphere = default;
            physicsSegments.Clear();
            contactSeconds = 0f;
            incomingSampled = physicsContactSampled = false;
        }

        private void ResetContactSamples()
        {
            contactPlayer = null;
            contactGeneration = 0;
            contactReset = 0;
            previousSphere = previousPlayer = previousCorrectionOffset = default;
            previousPlayerVelocity = previousPlayerCorrection = default;
            previousMotionTick = presentedMotionTick;
            hasContactPose = touchingPlayer = false;
            rebaseContactPose = false;
            ResetIncomingMotion();
        }

        private void ResetContactState()
        {
            ResetBirdContact();
            ResetContactSamples();
            releasePlayer = -1;
            releaseOperation = 0;
        }

        private void ReportImpact(PlayerItemHitbox player, Vector3 rockVelocity, Vector3 playerVelocity, Vector3 intoPlayer)
        {
            float speed = Mathf.Max(0f, Vector3.Dot(rockVelocity - playerVelocity, intoPlayer));
            if (speed < Definition.MinimumImpactSpeed) return;
            Vector3 velocityChange = intoPlayer * speed * Mathf.Max(0f, Definition.ImpulseMultiplier);
            if (velocityChange.sqrMagnitude > 0f || !WorldItemRegistry.Finite(velocityChange))
                player.QueueItemImpact(Record.Motion.Id, Record.Releaser, Record.Operation,
                    registry.IsHost ? "host" : Predicted ? "predicted" : "snapshot", rockVelocity, playerVelocity, intoPlayer, velocityChange);
        }

        private void OnCollisionEnter(Collision collision)
        {
            BirdContact(collision);
            if (birdRock && registry && Simulating && !registry.Replaying && !optimisticPickup &&
                Record.State == WorldItemState.World && collision.impulse.sqrMagnitude > 0.000001f &&
                (birdRegistry.Settings.SolidMask.value & (1 << collision.collider.gameObject.layer)) != 0)
                MotionBoundary = true;
            if (registry == null || !registry.IsHost || !Simulating || registry.Replaying || !ContactEligible || !incomingSampled) return;
            var contact = collision.GetContact(0);
            if (contact.thisCollider != impactSphere) return;
            if (!collision.collider.TryGetComponent<PlayerItemHitbox>(out var player) || !player.Motor.IsOwner) return;
            UpdateIgnore();
            if (ignoredPlayer == player.Collider) return;
            ReportImpact(player, incomingVelocity, player.IncomingVelocity, -contact.normal);
        }

        private void OnTriggerEnter(Collider other)
        {
            if (registry && Simulating && !registry.Replaying && other.TryGetComponent<ItemKillVolume>(out _))
                RemovalPending = true;
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
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
