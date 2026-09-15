using UnityEngine;

namespace TwoBirds
{
    [RequireComponent(typeof(Rigidbody))]
    public sealed class WorldItem : MonoBehaviour, IInteractable
    {
        [SerializeField] private Transform visualRoot;
        private readonly ItemMotion[] history = new ItemMotion[128];
        private readonly ItemMotion[] samples = new ItemMotion[16];
        private Collider[] colliders;
        private Renderer[] renderers;
        private Transform[] parts;
        private WorldItemRegistry registry;
        private Collider ignoredPlayer;
        private float ignoreUntil;
        private float sampleReceivedAt;
        private int sampleCount;
        private uint localLaunchTick;
        private uint historyStart;
        private Vector3 visualOffset;
        private Quaternion visualRotation = Quaternion.identity;
        private float correctionRemaining;
        private Vector3 prefabScale;
        private bool optimisticPickup;
        private float interactableAfter;
        private int playerHitboxMask;
        private ItemUseBehaviour useBehaviour;
        private PlayerEquipment useUser;

        public Rigidbody Body { get; private set; }
        public ItemDefinition Definition { get; private set; }
        public ItemRecord Record { get; private set; }
        public bool Predicted { get; private set; }
        public bool IsCharging => useBehaviour != null && useBehaviour.IsCharging;
        public float Charge01 => useBehaviour != null ? useBehaviour.Charge01 : 0f;
        public string ActionText => "Pick up";
        public string InputActionPath => "Player/Interact";
        public bool CanInteract => registry != null && Record.State == WorldItemState.World &&
                                   !optimisticPickup && Record.Motion.Id != 0 &&
                                   Time.time >= interactableAfter;

        private void Awake()
        {
            Body = GetComponent<Rigidbody>();
            useBehaviour = GetComponent<ItemUseBehaviour>();
            playerHitboxMask = LayerMask.GetMask("PlayerItemHitbox");
            colliders = GetComponentsInChildren<Collider>(true);
            renderers = GetComponentsInChildren<Renderer>(true);
            parts = GetComponentsInChildren<Transform>(true);
            prefabScale = transform.localScale;
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
            InterruptUse();
            registry = owner;
            Definition = definition;
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
        }

        internal void ApplyRecord(ItemRecord record)
        {
            bool wasPredicted = Predicted;
            SetRecord(record);
            optimisticPickup = false;
            ClearIgnore();
            if (record.State == WorldItemState.Held)
            {
                Predicted = false;
                StopBody();
                SetLayer(registry.HeldLayer);
                foreach (var collider in colliders) collider.enabled = false;
                AttachHolder();
                sampleCount = 0;
                return;
            }

            transform.SetParent(null, true);
            transform.localScale = prefabScale;
            SetLayer(registry.WorldLayer);
            SetVisible(true);
            foreach (var collider in colliders) collider.enabled = true;
            Body.collisionDetectionMode = CollisionDetectionMode.Discrete;
            Body.isKinematic = !(registry.IsHost || Predicted) || record.Sleeping && !registry.IsHost;
            Body.collisionDetectionMode = Body.isKinematic ? CollisionDetectionMode.Discrete : Definition.CollisionDetection;
            if (!wasPredicted || record.Sleeping)
                CorrectBody(record.Motion, wasPredicted);
            if (!Body.isKinematic && record.Sleeping) Body.Sleep();
            if (record.Sleeping) Predicted = false;
            if (!registry.IsHost && !Predicted)
            {
                sampleCount = 0;
                AddSample(record.Motion);
            }
            if (!record.Sleeping && registry.TryGetPlayer(record.Releaser, out var releaser))
                IgnorePlayer(releaser.Hitbox);
        }

        internal void AttachHolder()
        {
            if (Record.State != WorldItemState.Held) return;
            bool equipped = Record.Equipped && registry.TryGetPlayer(Record.Holder, out _);
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
            transform.localScale = prefabScale;
            ClearVisualOffset();
        }

        internal void PredictPickup()
        {
            optimisticPickup = true;
            StopBody();
            foreach (var collider in colliders) collider.enabled = false;
            SetVisible(false);
        }

        internal void SetRecord(ItemRecord record)
        {
            if (record.State == WorldItemState.Removed) InterruptUse();
            Record = record;
            int excludedLayers = record.Sleeping
                ? Body.excludeLayers.value | playerHitboxMask
                : Body.excludeLayers.value & ~playerHitboxMask;
            if (Body.excludeLayers.value == excludedLayers) return;
            Body.excludeLayers = excludedLayers;
            if (record.Sleeping && !Body.isKinematic) Body.Sleep();
        }
        internal void Launch(ItemMotion motion) => CorrectBody(motion, false);

        internal void PresentHeld(PlayerInventory holder, bool equipped)
        {
            if (Predicted) return;
            StopBody();
            SetLayer(registry.HeldLayer);
            foreach (var collider in colliders) collider.enabled = false;
            SetVisible(equipped);
            if (!equipped) return;
            transform.SetParent(holder.Equipment.HeldTransform, false);
            transform.localPosition = Vector3.zero;
            transform.localRotation = Definition.WorldPrefab.transform.localRotation;
            transform.localScale = prefabScale;
            ClearVisualOffset();
        }

        internal ItemMotion Capture(uint tick)
        {
            return new ItemMotion
            {
                Id = Record.Motion.Id, Revision = Record.Motion.Revision, Tick = tick,
                Position = Body.position, Rotation = Body.rotation,
                Velocity = Body.isKinematic ? Vector3.zero : Body.linearVelocity,
                AngularVelocity = Body.isKinematic ? Vector3.zero : Body.angularVelocity
            };
        }

        internal void Tick()
        {
            if (ignoredPlayer != null && (Time.time >= ignoreUntil || Separated())) ClearIgnore();
            if (Predicted && !optimisticPickup)
                history[registry.LocalTick % (uint)history.Length] = Capture(registry.LocalTick);
        }

        internal void ReceiveMotion(ItemMotion motion)
        {
            var record = Record;
            record.Motion = motion;
            record.Sleeping = false;
            SetRecord(record);
            if (optimisticPickup) return;
            if (!Predicted)
            {
                AddSample(motion);
                return;
            }

            uint correspondingTick = localLaunchTick + motion.Tick - Record.LaunchTick;
            var predicted = history[correspondingTick % (uint)history.Length];
            if (correspondingTick >= registry.LocalTick) return;
            if (correspondingTick >= historyStart && predicted.Tick == correspondingTick &&
                (predicted.Position - motion.Position).sqrMagnitude <= registry.CorrectionThreshold * registry.CorrectionThreshold)
                return;
            CorrectBody(motion, true);
            localLaunchTick = registry.LocalTick - 1 - (motion.Tick - Record.LaunchTick);
            historyStart = registry.LocalTick + 1;
        }

        private void CorrectBody(ItemMotion motion, bool smooth)
        {
            Vector3 visiblePosition = visualRoot.position;
            Quaternion visibleRotation = visualRoot.rotation;
            Body.position = motion.Position;
            Body.rotation = motion.Rotation;
            transform.SetPositionAndRotation(motion.Position, motion.Rotation);
            if (!Body.isKinematic)
            {
                Body.linearVelocity = motion.Velocity;
                Body.angularVelocity = motion.AngularVelocity;
            }
            if (smooth)
            {
                visualRoot.SetPositionAndRotation(visiblePosition, visibleRotation);
                visualOffset = visualRoot.localPosition;
                visualRotation = visualRoot.localRotation;
                correctionRemaining = registry.CorrectionDuration;
            }
            else ClearVisualOffset();
        }

        private void AddSample(ItemMotion motion)
        {
            if (sampleCount == samples.Length)
            {
                System.Array.Copy(samples, 1, samples, 0, samples.Length - 1);
                sampleCount--;
            }
            samples[sampleCount++] = motion;
            sampleReceivedAt = Time.unscaledTime;
        }

        private void LateUpdate()
        {
            if (registry == null || optimisticPickup || Record.State != WorldItemState.World) return;
            if (!registry.IsHost && !Predicted && sampleCount > 0)
            {
                ItemMotion latest = samples[sampleCount - 1];
                double tick = latest.Tick + (Time.unscaledTime - sampleReceivedAt - registry.InterpolationDelay) / registry.TickDelta;
                ItemMotion from = samples[0], to = from;
                for (int i = 1; i < sampleCount; i++)
                {
                    to = samples[i];
                    if (to.Tick >= tick) break;
                    from = to;
                }
                float amount = to.Tick == from.Tick ? 1f : Mathf.Clamp01((float)((tick - from.Tick) / (to.Tick - from.Tick)));
                Vector3 position = Vector3.Lerp(from.Position, to.Position, amount);
                Quaternion rotation = Quaternion.Slerp(from.Rotation, to.Rotation, amount);
                if (!Record.Sleeping && tick > latest.Tick)
                {
                    float seconds = Mathf.Min((float)((tick - latest.Tick) * registry.TickDelta), 0.1f);
                    Vector3 travel = latest.Velocity * seconds;
                    if (!Physics.Raycast(latest.Position, travel.normalized, out var hit, travel.magnitude,
                            registry.EnvironmentMask, QueryTriggerInteraction.Ignore)) position += travel;
                    else position = hit.point - travel.normalized * 0.01f;
                }
                Body.position = position;
                Body.rotation = rotation;
                transform.SetPositionAndRotation(position, rotation);
            }
            if (correctionRemaining > 0f)
            {
                correctionRemaining = Mathf.Max(0f, correctionRemaining - Time.deltaTime);
                float remaining = correctionRemaining / registry.CorrectionDuration;
                visualRoot.localPosition = visualOffset * remaining;
                visualRoot.localRotation = Quaternion.Slerp(Quaternion.identity, visualRotation, remaining);
            }
        }

        internal void IgnorePlayer(PlayerItemHitbox player)
        {
            ClearIgnore();
            if (player == null) return;
            ignoredPlayer = player.Collider;
            ignoreUntil = Time.time + registry.ReleaseGrace;
            foreach (var collider in colliders) Physics.IgnoreCollision(collider, ignoredPlayer, true);
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
            correctionRemaining = 0f;
        }

        private void ResetPresentation()
        {
            ClearIgnore();
            ClearVisualOffset();
            System.Array.Clear(history, 0, history.Length);
            sampleCount = 0;
            optimisticPickup = false;
        }

        internal void ReturnToPool()
        {
            InterruptUse();
            StopBody();
            ResetPresentation();
            Record = default;
            Predicted = false;
            transform.SetParent(registry.transform, false);
            transform.localScale = prefabScale;
            gameObject.SetActive(false);
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (registry == null || !registry.IsHost || registry.Replaying || Record.State != WorldItemState.World || Record.Motion.Id == 0) return;
            if (collision.collider.TryGetComponent<PlayerItemHitbox>(out var player))
                registry.HitPlayer(this, player, -collision.impulse);
        }

        private void OnTriggerEnter(Collider other)
        {
            if (registry != null && registry.IsHost && !registry.Replaying && other.TryGetComponent<ItemKillVolume>(out _))
                registry.Remove(Record.Motion.Id);
        }
    }
}
