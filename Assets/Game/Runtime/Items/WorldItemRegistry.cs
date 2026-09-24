using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Managing.Predicting;
using FishNet.Transporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TwoBirds
{
    [DefaultExecutionOrder(150)]
    public sealed partial class WorldItemRegistry : MonoBehaviour
    {
        private const int BatchSize = 8;
        public static WorldItemRegistry Instance { get; private set; }
        [SerializeField] private ItemRegistry itemRegistry;
        [SerializeField] private GameSettings settings;
        [SerializeField, Range(15, 20)] private int snapshotRate = 20;
        [SerializeField, Min(0.001f)] private float correctionThreshold = 0.05f;
        [SerializeField, Min(0.01f)] private float correctionDuration = 0.075f;
        [SerializeField, Min(0f)] private float interpolationDelay = 0.1f;
        [SerializeField, Min(0f)] private float releaseGrace = 0.3f;
        [SerializeField] private Bounds worldBounds = new(Vector3.zero, Vector3.one * 2000f);
        private readonly Dictionary<uint, WorldItem> items = new();
        private readonly Dictionary<uint, ItemRecord> records = new();
        private readonly Dictionary<uint, ItemMotion> earlyMotion = new();
        private readonly Dictionary<uint, uint> pendingReleases = new();
        private readonly HashSet<uint> pendingCartContacts = new();
        private readonly Dictionary<int, PlayerInventory> players = new();
        private readonly Dictionary<byte, RigidbodyInstancePool<WorldItem>> pools = new();
        private readonly RuntimeItemIds runtimeIds = new();
        private readonly HashSet<NetworkConnection> observers = new();
        private readonly List<uint> cleanup = new();
        private readonly List<ItemMotion> motionBatch = new(BatchSize);
        private readonly List<ItemRecord> lifecycleBatch = new(BatchSize);
        private readonly List<(uint Id, Vector3 Origin, Vector3 Fallback, Quaternion Facing, int Retries)> departureDrops = new();
        private readonly List<(Vector3 Position, float Radius)> placedDrops = new();
        private readonly Collider[] dropOverlaps = new Collider[32];
        private float nextDepartureDrop;
        private NetworkManager network;
        private PredictionManager predictionManager;
        private uint epoch;
        private uint sessionId;
        private bool worldReady;
        internal uint AIEpoch => epoch;
        private Scene worldScene;

        public bool IsHost => network.IsServerStarted;
        internal PredictionManager PredictionManager => predictionManager;
        public bool Replaying => predictionManager.IsReconciling;
        public uint LocalTick => network.TimeManager.LocalTick;
        public uint ServerTick => network.TimeManager.Tick;
        public double TickDelta => network.TimeManager.TickDelta;
        public float CorrectionThreshold => correctionThreshold;
        public float CorrectionDuration => correctionDuration;
        public float InterpolationDelay => interpolationDelay;
        public float ReleaseGrace => releaseGrace;
        public int WorldLayer { get; private set; }
        public int HeldLayer { get; private set; }
        public int EnvironmentMask { get; private set; }
        public PlayerInventory LocalInventory { get; private set; }
        internal event System.Action<uint, int, int> PresentationChanged;

        public ItemRegistry Catalog => itemRegistry;
        public void BindCatalog(ItemRegistry catalog)
        {
            if (worldReady) throw new System.InvalidOperationException("Bind item content before BeginWorld.");
            itemRegistry = catalog;
        }

#if UNITY_INCLUDE_INSTRUMENTATION
        internal WorldItem SupplyAuthoringItem(byte definition, Vector3 position)
        {
            if (!worldReady || !IsHost || !GetDefinition(definition)) return null;
            var record = new ItemRecord
            {
                DefinitionId = definition, State = WorldItemState.World, Holder = -1, Releaser = -1, Simulator = -1,
                Motion = new ItemMotion { Id = runtimeIds.Allocate(), Revision = 1, Tick = ServerTick,
                    Position = position, Rotation = GetDefinition(definition).WorldPrefab.transform.localRotation }
            };
            var item = Rent(definition);
            records.Add(record.Motion.Id, record); items.Add(record.Motion.Id, item);
            item.gameObject.SetActive(true); item.Initialize(this, GetDefinition(definition), record, false);
            Publish(record); NotifyPresentation(record.Motion.Id, -1);
            return item;
        }
#endif

        internal HeldItemSettings HeldDefaults => itemRegistry.HeldItemDefaults;

        internal HeldItemPoseData GetHeldPose(ItemDefinition definition, bool firstPerson = false, uint worldId = 0) =>
            new(definition, itemRegistry.HeldItemDefaults, firstPerson, items.GetValueOrDefault(worldId));

        private void NotifyPresentation(uint id, int previousHolder)
        {
            int holder = items.TryGetValue(id, out var item) && item && item.Record.State == WorldItemState.Held
                ? item.Record.Holder : -1;
            PresentationChanged?.Invoke(id, previousHolder, holder);
        }

        internal WorldItem EquippedPresentation(int holder)
        {
            foreach (var item in items.Values)
                if (item && item.Definition && item.Record.State == WorldItemState.Held &&
                    item.Record.Holder == holder && item.Record.Equipped) return item;
            return null;
        }

        private void Awake()
        {
            Instance = this;
            network = GetComponent<NetworkManager>();
            WorldLayer = LayerMask.NameToLayer("ItemWorld");
            HeldLayer = LayerMask.NameToLayer("ItemHeld");
            EnvironmentMask = Physics.DefaultRaycastLayers & ~LayerMask.GetMask("ItemWorld", "ItemHeld", "Player", "PlayerItemHitbox", "BirdBody", "BirdQuery", "PlayerEffectReceiver", "PotionEffect");
        }

        private void Start()
        {
            predictionManager = GetComponent<PredictionManager>();
            RegisterPebbles();
            RegisterCraftingMessages();
            network.ServerManager.RegisterBroadcast<ItemBaselineRequest>(SendBaseline);
            network.ServerManager.RegisterBroadcast<ItemMotionBatch>(ReceiveSimulatorMotion);
            network.ServerManager.RegisterBroadcast<ItemCartContact>(ReceiveCartContact);
            network.ClientManager.RegisterBroadcast<ItemBaselineStart>(BeginBaseline);
            network.ClientManager.RegisterBroadcast<ItemBaselineComplete>(CompleteBaseline);
            network.ClientManager.RegisterBroadcast<ItemLifecycleBatch>(ReceiveLifecycle);
            network.ClientManager.RegisterBroadcast<ItemMotionBatch>(ReceiveMotion);
            network.ServerManager.OnRemoteConnectionState += ConnectionChanged;
            network.ServerManager.Objects.OnPreDestroyClientObjects += DropDepartingItems;
            network.TimeManager.OnPrePhysicsSimulation += BeforePhysics;
            network.TimeManager.OnPostPhysicsSimulation += AfterPhysics;
            network.TimeManager.OnPostTick += AfterTick;
        }

        public void BeginWorld(Scene scene)
        {
            worldScene = scene;
            sessionId = SessionController.Instance.SessionId;
            epoch = (uint)Random.Range(1, int.MaxValue);
            worldReady = true;
            foreach (var seed in FindObjectsByType<BakedPickup>())
            {
                if (seed.gameObject.scene != scene) continue;
                if (seed.BakedId == 0 || records.ContainsKey(seed.BakedId))
                {
                    Debug.LogError($"Bake a unique item ID for {seed.name} before starting the world.", seed);
                    seed.gameObject.SetActive(false);
                    continue;
                }
                var record = new ItemRecord
                {
                    Motion = new ItemMotion { Id = seed.BakedId, Revision = 1, Tick = ServerTick,
                        Position = seed.transform.position, Rotation = seed.transform.rotation },
                    DefinitionId = seed.ItemId, State = WorldItemState.World,
                    Holder = -1, Releaser = -1, Simulator = -1
                };
                var definition = itemRegistry.Get(record.DefinitionId);
                if (!definition && seed.Item)
                {
                    byte itemId = itemRegistry.GetId(seed.Item);
                    if (itemId != 0)
                    {
                        record.DefinitionId = itemId;
                        definition = seed.Item;
                    }
                }
                if (!definition)
                {
                    Debug.LogError($"'{seed.name}' has no valid ItemDefinition.", seed);
                    seed.gameObject.SetActive(false);
                    continue;
                }
                var item = seed.GetComponent<WorldItem>();
                items.Add(record.Motion.Id, item);
                records.Add(record.Motion.Id, record);
                item.Initialize(this, definition, record, false);
            }
            BeginCraftingWorld();
            SessionController.Instance.WorldReady(sessionId, epoch);
        }

        public void JoinWorld(Scene scene)
        {
            if (IsHost) return;
            worldScene = scene;
            sessionId = SessionController.Instance.SessionId;
            worldReady = true;
            foreach (var seed in FindObjectsByType<BakedPickup>())
            {
                if (seed.gameObject.scene != scene) continue;
                if (seed.BakedId != 0 && !items.ContainsKey(seed.BakedId))
                    items.Add(seed.BakedId, seed.GetComponent<WorldItem>());
                seed.gameObject.SetActive(false);
            }
            network.ClientManager.Broadcast(new ItemBaselineRequest { Session = sessionId });
        }

        private void SendBaseline(NetworkConnection connection, ItemBaselineRequest request, Channel channel)
        {
            if (!worldReady) return;
            observers.Add(connection);
            network.ServerManager.Broadcast(connection, new ItemBaselineStart { Session = request.Session, Epoch = epoch });
            lifecycleBatch.Clear();
            foreach (var saved in records.Values)
            {
                var record = saved;
                if (record.State == WorldItemState.World && items.TryGetValue(record.Motion.Id, out var item) && Simulates(record))
                {
                    record.Motion = item.Capture(ServerTick);
                    record.Sleeping = item.Body.IsSleeping();
                }
                lifecycleBatch.Add(record);
                if (lifecycleBatch.Count == BatchSize) FlushLifecycle(connection);
            }
            FlushLifecycle(connection);
            SendCraftingBaseline(connection);
            Pebbles.Baseline(connection);
            network.ServerManager.Broadcast(connection, new ItemBaselineComplete { Session = request.Session, Epoch = epoch });
        }

        private void BeginBaseline(ItemBaselineStart message, Channel channel)
        {
            if (IsHost || !worldReady || message.Session != sessionId || sessionId != SessionController.Instance.SessionId) return;
            epoch = message.Epoch;
        }

        private void CompleteBaseline(ItemBaselineComplete message, Channel channel)
        {
            if (IsHost || !worldReady || message.Session != sessionId || message.Epoch != epoch || epoch == 0) return;
            SessionController.Instance.WorldReady(sessionId, epoch);
        }

        private void ReceiveLifecycle(ItemLifecycleBatch message, Channel channel)
        {
            if (IsHost || !worldReady || message.Epoch != epoch) return;
            ApplyLifecycle(message.Items);
        }

        private void ApplyLifecycle(List<ItemRecord> changes)
        {
            foreach (var record in changes)
            {
                uint id = record.Motion.Id;
                if (records.TryGetValue(id, out var previous) && !Newer(record.Motion, previous.Motion)) continue;
                records[id] = record;
                pendingCartContacts.Remove(id);
                if (record.State == WorldItemState.Held) ClearPredictedClouds(id);
                if (record.State == WorldItemState.Removed)
                {
                    Pool(id);
                    earlyMotion.Remove(id);
                    continue;
                }
                if (pendingReleases.TryGetValue(id, out uint operation))
                {
                    if (LocalInventory == null || record.Operation != operation || record.Releaser != LocalInventory.ObjectId) continue;
                    pendingReleases.Remove(id);
                }
                if (!items.TryGetValue(id, out var item))
                {
                    item = Rent(record.DefinitionId);
                    items.Add(id, item);
                }
                if (!item.gameObject.activeSelf || item.Definition == null)
                {
                    item.gameObject.SetActive(true);
                    item.Initialize(this, itemRegistry.Get(record.DefinitionId), record, false);
                }
                else item.ApplyRecord(record);
                if (earlyMotion.Remove(id, out var motion)) ApplyMotion(motion);
                NotifyPresentation(id, previous.State == WorldItemState.Held ? previous.Holder : -1);
            }
            LocalInventory?.RefreshHeldPresentation();
        }

        private void ReceiveMotion(ItemMotionBatch message, Channel channel)
        {
            if (IsHost || !worldReady || epoch == 0 || message.Epoch != epoch) return;
            foreach (var motion in message.Items) ApplyMotion(motion);
        }

        private void ApplyMotion(ItemMotion motion)
        {
            if (Pebbles.ReceiveMotion(motion)) return;
            if (!records.ContainsKey(motion.Id) && !items.ContainsKey(motion.Id)) return;
            if (!records.TryGetValue(motion.Id, out var record) || motion.Revision > record.Motion.Revision)
            {
                if (!earlyMotion.TryGetValue(motion.Id, out var earlier) || Newer(motion, earlier))
                    earlyMotion[motion.Id] = motion;
                return;
            }
            if (record.State != WorldItemState.World || !Newer(motion, record.Motion)) return;
            if (Simulates(record)) return;
            if (motion.RotationOmitted)
            {
                motion.Rotation = record.Motion.Rotation;
                motion.AngularVelocity = record.Motion.AngularVelocity;
            }
            record.Motion = motion;
            record.Sleeping = motion.Sleeping;
            records[motion.Id] = record;
            if (!pendingReleases.ContainsKey(motion.Id) && items.TryGetValue(motion.Id, out var item)) item.ReceiveMotion(motion);
        }

        private static bool Newer(ItemMotion next, ItemMotion previous) =>
            next.Revision > previous.Revision || next.Revision == previous.Revision && next.Sequence > previous.Sequence;

        public bool TryGetItem(uint id, out WorldItem item) => items.TryGetValue(id, out item);
        public bool TryGetRecord(uint id, out ItemRecord record) => records.TryGetValue(id, out record);
        public bool TryGetPlayer(int id, out PlayerInventory player) => players.TryGetValue(id, out player);
        public ItemDefinition GetDefinition(byte id) => itemRegistry.Get(id);

        internal void RegisterPlayer(PlayerInventory player)
        {
            players[player.ObjectId] = player;
            BirdRegistry.Instance?.RegisterPlayer(player);
            if (player.IsOwner) LocalInventory = player;
            else if (LocalInventory == player) LocalInventory = null;
            ResolvePlayerEffects(player);
            RefreshHolders();
            player.Equipment.HeldPresentation.StartPresentation();
        }

        internal void UnregisterPlayer(PlayerInventory player)
        {
            BirdRegistry.Instance?.UnregisterPlayer(player);
            cleanup.Clear();
            foreach (var record in records.Values)
                if (record.State == WorldItemState.Held && record.Holder == player.ObjectId) cleanup.Add(record.Motion.Id);
            foreach (uint id in cleanup)
            {
                if (IsHost) Remove(id);
                else if (items.TryGetValue(id, out var item)) item.transform.SetParent(transform, true);
            }
            ForgetPlayerEffects(player);
            players.Remove(player.ObjectId);
            if (LocalInventory == player) LocalInventory = null;
        }

        private void DropDepartingItems(NetworkConnection connection)
        {
            if (!worldReady || !IsHost || SessionController.Instance.Phase == SessionPhase.Stopping) return;
            TakeOverMotion(connection.ClientId);
            foreach (var player in players.Values)
            {
                if (!player || player.Owner != connection) continue;
                var seating = player.GetComponent<PlayerSeating>();
                var pose = seating.Seated ? seating.Cart.GetSeat(seating.SeatIndex).Rider : player.transform;
                Vector3 origin = pose.position;
                cleanup.Clear();
                foreach (var record in records.Values)
                {
                    if (record.State != WorldItemState.Held || record.Holder != player.ObjectId) continue;
                    cleanup.Add(record.Motion.Id);
                }
                Vector3 fallback = player.GetComponent<PlayerMotor>().SpawnPoint;
                Quaternion facing = Quaternion.Euler(0f, pose.eulerAngles.y, 0f);
                foreach (uint id in cleanup)
                {
                    var record = records[id];
                    record.Motion = items[id].Capture(ServerTick);
                    record.Motion.Revision++;
                    record.State = WorldItemState.World;
                    record.Holder = -1;
                    record.Equipped = false;
                    record.Sleeping = false;
                    record.Simulator = -1;
                    records[id] = record;
                    items[id].ApplyRecord(record);
                    Publish(record);
                    departureDrops.Add((id, origin + Vector3.up, fallback + Vector3.up, facing, 0));
                }
            }
            PlaceDepartureDrops();
        }

        private void PlaceDepartureDrops()
        {
            nextDepartureDrop = Time.unscaledTime + 1f;
            placedDrops.Clear();
            for (int i = departureDrops.Count - 1; i >= 0; i--)
            {
                var drop = departureDrops[i];
                var item = items[drop.Id];
                float radius = Mathf.Max(0.05f, item.DropDiameter * 0.5f) + 0.025f;
                if (!FindDropPosition(drop.Origin, drop.Facing, radius, out Vector3 position) &&
                    !FindDropPosition(drop.Fallback, drop.Facing, radius, out position))
                {
                    if (drop.Retries < 5)
                    {
                        departureDrops[i] = (drop.Id, drop.Origin, drop.Fallback, drop.Facing, drop.Retries + 1);
                        continue;
                    }
                    position = item.Body.position;
                }
                placedDrops.Add((position, radius));
                Release(drop.Id, 0, new ItemMotion { Position = position, Rotation = item.Record.Motion.Rotation }, -1);
                departureDrops.RemoveAt(i);
            }
        }

        private bool FindDropPosition(Vector3 origin, Quaternion facing, float radius, out Vector3 position)
        {
            position = default;
            if (!InsideDropBounds(origin, radius) || Physics.CheckSphere(origin, radius, EnvironmentMask, QueryTriggerInteraction.Ignore))
                return false;
            float spacing = radius * 2f + 0.05f;
            int rings = Mathf.CeilToInt(4f / spacing);
            for (int ring = 0; ring <= rings; ring++)
            {
                int count = Mathf.Max(1, Mathf.CeilToInt(2f * Mathf.PI * ring));
                for (int step = 0; step < count; step++)
                {
                    float angle = step * 2f * Mathf.PI / count;
                    Vector3 candidate = origin + facing * new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle)) * (ring * spacing);
                    if (!InsideDropBounds(candidate, radius)) continue;
                    Vector3 travel = candidate - origin;
                    if (travel.sqrMagnitude > 0f && Physics.SphereCast(origin, radius, travel.normalized, out _, travel.magnitude,
                            EnvironmentMask, QueryTriggerInteraction.Ignore)) continue;
                    if (!Physics.SphereCast(candidate, radius, Vector3.down, out var ground, 4f,
                            EnvironmentMask, QueryTriggerInteraction.Ignore) || ground.normal.y < 0.5f) continue;
                    Vector3 landing = candidate + Vector3.down * ground.distance;
                    if (!InsideDropBounds(landing, radius)) continue;
                    bool blocked = false;
                    int fallOverlaps = Physics.OverlapCapsuleNonAlloc(candidate, landing, radius, dropOverlaps,
                        EnvironmentMask, QueryTriggerInteraction.Collide);
                    if (fallOverlaps == dropOverlaps.Length) continue;
                    for (int index = 0; index < fallOverlaps; index++)
                        if (dropOverlaps[index].GetComponent<ItemKillVolume>()) { blocked = true; break; }
                    if (blocked) continue;
                    foreach (var placed in placedDrops)
                        if ((candidate - placed.Position).sqrMagnitude < (radius + placed.Radius) * (radius + placed.Radius))
                        { blocked = true; break; }
                    if (blocked) continue;
                    int overlaps = Physics.OverlapSphereNonAlloc(candidate, radius, dropOverlaps,
                        EnvironmentMask | (1 << WorldLayer), QueryTriggerInteraction.Collide);
                    if (overlaps == dropOverlaps.Length) continue;
                    for (int index = 0; index < overlaps; index++)
                        if (!dropOverlaps[index].isTrigger || dropOverlaps[index].GetComponent<ItemKillVolume>())
                        { blocked = true; break; }
                    if (blocked) continue;
                    position = candidate;
                    return true;
                }
            }
            return false;
        }

        private bool InsideDropBounds(Vector3 position, float radius)
        {
            Vector3 margin = Vector3.one * radius;
            return Finite(position) && position.y - radius > settings.FallBoundary &&
                worldBounds.Contains(position - margin) && worldBounds.Contains(position + margin);
        }

        public void RefreshHolders()
        {
            foreach (var item in items.Values)
                if (item != null && item.Definition != null) item.AttachHolder();
            LocalInventory?.RefreshHeldPresentation();
        }

        internal void RefreshHolder(PlayerInventory holder)
        {
            foreach (var item in items.Values)
                if (item && item.Definition && item.Record.State == WorldItemState.Held && item.Record.Holder == holder.ObjectId)
                    item.AttachHolder();
            holder.RefreshHeldPresentation();
        }

        internal void DetachHolder(PlayerInventory holder)
        {
            foreach (var item in items.Values)
                if (item && item.PresentedHolder == holder.ObjectId)
                    item.DetachHeldPresentation();
        }

        internal void SetHeld(uint id, PlayerInventory holder, bool equipped)
        {
            var record = records[id];
            int previousHolder = record.State == WorldItemState.Held ? record.Holder : -1;
            BirdRegistry.Instance?.CompleteRelease(record);
            record.Motion = items[id].Capture(ServerTick);
            record.Motion.Revision++;
            record.Armed = false;
            record.Cauldron = -1;
            record.State = WorldItemState.Held;
            record.Holder = holder.ObjectId;
            record.Equipped = equipped;
            record.Sleeping = false;
            record.Operation = 0;
            record.Releaser = -1;
            record.BirdPlayer = 0;
            record.Simulator = -1;
            records[id] = record;
            items[id].ApplyRecord(record);
            PublishCollection(record);
            NotifyPresentation(id, previousHolder);
        }

        internal void UpdateEquipment(PlayerInventory holder, uint equipped)
        {
            foreach (var item in items.Values)
            {
                if (item.Definition == null) continue;
                var record = item.Record;
                if (record.State == WorldItemState.Held && record.Holder == holder.ObjectId && record.Equipped != (record.Motion.Id == equipped))
                    SetHeld(record.Motion.Id, holder, record.Motion.Id == equipped);
            }
        }

        private bool SimulateOnReleaser(ItemDefinition definition) => definition.SimulateOnReleasingClient ||
            definition is PotionDefinition || BirdRegistry.Instance && BirdRegistry.Instance.IsRock(definition);

        internal void PredictRelease(uint id, uint operation, ItemMotion motion, PlayerInventory player, ItemReleaseIntent intent)
        {
            if (IsHost) return;
            var record = records[id];
            record.Motion = motion;
            record.State = WorldItemState.World;
            record.Armed = intent == ItemReleaseIntent.Throw && itemRegistry.Get(record.DefinitionId) is PotionDefinition;
            record.Sleeping = false;
            record.Holder = -1;
            record.Releaser = player.ObjectId;
            record.BirdPlayer = BirdRegistry.Instance ? BirdRegistry.Instance.PlayerToken(player.ObjectId) : 0;
            record.Operation = operation;
            record.LaunchTick = ServerTick;
            pendingReleases[id] = operation;
            record.Simulator = SimulateOnReleaser(itemRegistry.Get(record.DefinitionId)) && player.Owner.IsActive
                ? player.Owner.ClientId : -1;
            items[id].Initialize(this, itemRegistry.Get(record.DefinitionId), record, true);
            items[id].Launch(motion);
            NotifyPresentation(id, player.ObjectId);
        }

        internal void Release(uint id, uint operation, ItemMotion motion, PlayerInventory player, ItemReleaseIntent intent)
            => Release(id, operation, motion, player.ObjectId, intent);

        private void Release(uint id, uint operation, ItemMotion motion, int releaser, ItemReleaseIntent intent = ItemReleaseIntent.Drop)
        {
            var record = records[id];
            int previousHolder = record.State == WorldItemState.Held ? record.Holder : -1;
            BirdRegistry.Instance?.CompleteRelease(record);
            motion.Id = id;
            motion.Tick = ServerTick;
            motion.Revision = record.Motion.Revision + 1;
            motion.Sequence = 0;
            motion.Path = 0;
            motion.Sleeping = motion.Boundary = motion.Removed = false;
            record.Motion = motion;
            record.State = WorldItemState.World;
            record.Holder = -1;
            record.Equipped = false;
            record.Sleeping = false;
            record.Armed = intent == ItemReleaseIntent.Throw && itemRegistry.Get(record.DefinitionId) is PotionDefinition;
            record.Cauldron = -1;
            record.Releaser = releaser;
            record.Operation = operation;
            record.LaunchTick = ServerTick;
            record.BirdPlayer = BirdRegistry.Instance ? BirdRegistry.Instance.PlayerToken(releaser) : 0;
            record.Simulator = SimulateOnReleaser(itemRegistry.Get(record.DefinitionId)) &&
                players.TryGetValue(releaser, out var simulator) && simulator.Owner.IsActive ? simulator.Owner.ClientId : -1;
            records[id] = record;
            BirdRegistry.Instance?.AcceptRelease(record);
            items[id].ApplyRecord(record);
            Publish(record);
            NotifyPresentation(id, previousHolder);
        }

        internal void Rollback(uint id, uint operation)
        {
            if (pendingReleases.TryGetValue(id, out uint pending) && pending != operation) return;
            pendingReleases.Remove(id);
            contacts.Remove(id);
            if (items.TryGetValue(id, out var item) && records.TryGetValue(id, out var record))
            {
                int previousHolder = item.Record.State == WorldItemState.Held ? item.Record.Holder : -1;
                item.Initialize(this, itemRegistry.Get(record.DefinitionId), record, false);
                NotifyPresentation(id, previousHolder);
            }
        }

        public void Remove(uint id)
        {
            if (!IsHost || !records.TryGetValue(id, out var record) || record.State == WorldItemState.Removed) return;
            BirdRegistry.Instance?.CompleteRelease(record);
            record.Armed = false;
            record.State = WorldItemState.Removed;
            record.Motion.Revision++;
            record.Motion.Tick = ServerTick;
            records[id] = record;
            Pool(id);
            Publish(record);
        }

        private void BeforePhysics(float delta)
        {
            if (!worldReady) return;
            BirdRegistry.Instance?.PrepareRockPhysics(ServerTick);
            if (Replaying) return;
            ItemContactPhysics.BeginStep();
            RefreshEffectPoses();
            foreach (var player in players.Values)
                if (!player.Hitbox.Suspended) player.Hitbox.FollowMotor();
            foreach (var item in items.Values)
                if (item != null && item.Definition != null && item.gameObject.activeSelf) { item.BeforePhysics(); item.BeforePotionPhysics(); }
            Pebbles.BeforePhysics();
        }

        private void AfterPhysics(float delta)
        {
            if (!worldReady || Replaying) return;
            RefreshEffectPoses();
            foreach (var item in items.Values)
                if (item != null && item.Definition != null && item.gameObject.activeSelf) { item.AfterPhysics(delta); item.AfterPotionPhysics(); }
            Pebbles.AfterPhysics(delta);
        }

        private void LateUpdate()
        {
            if (!worldReady || Replaying) return;
            StepCrafting();
            PlayerItemHitbox victim = LocalInventory != null && LocalInventory.IsOwner ? LocalInventory.Hitbox : null;
            if (victim != null) victim.SamplePresentation();
            foreach (var item in items.Values)
            {
                if (item == null || item.Definition == null || !item.gameObject.activeSelf) continue;
                item.Present();
                item.SampleBirdContacts();
                if (!IsHost || !item.Simulating) item.SamplePlayerContact(victim);
            }
            FlushPotionContacts();
            Pebbles.Present(victim);
        }

        private void Publish(ItemRecord record)
        {
            lifecycleBatch.Add(record);
            FlushLifecycle();
        }

        private void FlushLifecycle(NetworkConnection target = null)
        {
            if (lifecycleBatch.Count == 0) return;
            var message = new ItemLifecycleBatch { Epoch = epoch, Items = lifecycleBatch };
            if (target == null) network.ServerManager.Broadcast(observers, message);
            else network.ServerManager.Broadcast(target, message);
            lifecycleBatch.Clear();
        }

        private void FlushMotion(Channel channel = Channel.Unreliable, NetworkConnection excluded = null)
        {
            if (motionBatch.Count == 0) return;
            var message = new ItemMotionBatch { Epoch = epoch, Items = motionBatch };
            if (IsHost)
            {
                bool restore = excluded != null && observers.Remove(excluded);
                network.ServerManager.Broadcast(observers, message, channel: channel);
                if (restore) observers.Add(excluded);
            }
            else network.ClientManager.Broadcast(message, channel);
            motionBatch.Clear();
        }

        private WorldItem Rent(byte definition)
        {
            if (!pools.TryGetValue(definition, out var pool)) pools[definition] = pool = new();
            return pool.Rent(itemRegistry.Get(definition).WorldPrefab, worldScene);
        }

        private void Pool(uint id)
        {
            ClearPredictedClouds(id);
            pendingReleases.Remove(id);
            pendingCartContacts.Remove(id);
            contacts.Remove(id);
            if (!items.Remove(id, out var item) || item == null) return;
            if (item.Definition == null)
            {
                Destroy(item.gameObject);
                return;
            }
            byte definition = item.Record.DefinitionId;
            int previousHolder = item.Record.State == WorldItemState.Held ? item.Record.Holder : -1;
            item.ReturnToPool();
            NotifyPresentation(id, previousHolder);
            if (!pools.TryGetValue(definition, out var pool)) pools[definition] = pool = new();
            pool.Return(item);
        }

        private void ConnectionChanged(NetworkConnection connection, RemoteConnectionStateArgs args)
        {
            if (args.ConnectionState != RemoteConnectionState.Stopped) return;
            observers.Remove(connection);
            if (worldReady && IsHost) TakeOverMotion(connection.ClientId);
        }

        public void EndWorld()
        {
            Pebbles?.Clear();
            ItemContactPhysics.Clear();
            EndCraftingWorld();
            worldReady = false;
            foreach (var item in items.Values)
                if (item != null) Destroy(item.gameObject);
            items.Clear();
            foreach (var pool in pools.Values) pool.Clear();
            pools.Clear();
            records.Clear();
            players.Clear();
            pendingReleases.Clear();
            earlyMotion.Clear();
            pendingCartContacts.Clear();
            observers.Clear();
            motionBatch.Clear();
            lifecycleBatch.Clear();
            LocalInventory = null;
            departureDrops.Clear();
            placedDrops.Clear();
            epoch = 0;
            sessionId = 0;
        }

        internal static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

        private void OnDestroy()
        {
            UnregisterPebbles();
            UnregisterCraftingMessages();
            network.ServerManager.UnregisterBroadcast<ItemBaselineRequest>(SendBaseline);
            network.ServerManager.UnregisterBroadcast<ItemMotionBatch>(ReceiveSimulatorMotion);
            network.ServerManager.UnregisterBroadcast<ItemCartContact>(ReceiveCartContact);
            network.ClientManager.UnregisterBroadcast<ItemBaselineStart>(BeginBaseline);
            network.ClientManager.UnregisterBroadcast<ItemBaselineComplete>(CompleteBaseline);
            network.ClientManager.UnregisterBroadcast<ItemLifecycleBatch>(ReceiveLifecycle);
            network.ClientManager.UnregisterBroadcast<ItemMotionBatch>(ReceiveMotion);
            network.ServerManager.OnRemoteConnectionState -= ConnectionChanged;
            network.ServerManager.Objects.OnPreDestroyClientObjects -= DropDepartingItems;
            network.TimeManager.OnPrePhysicsSimulation -= BeforePhysics;
            network.TimeManager.OnPostPhysicsSimulation -= AfterPhysics;
            network.TimeManager.OnPostTick -= AfterTick;
            EndWorld();
            if (Instance == this) Instance = null;
        }
    }
}
