using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Managing.Predicting;
using FishNet.Transporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TwoBirds
{
    [DefaultExecutionOrder(200)]
    public sealed class WorldItemRegistry : MonoBehaviour
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
        private readonly Dictionary<int, PlayerInventory> players = new();
        private readonly Dictionary<byte, Stack<WorldItem>> pools = new();
        private readonly HashSet<uint> activePhysicsItems = new();
        private readonly HashSet<NetworkConnection> observers = new();
        private readonly List<uint> cleanup = new();
        private readonly List<ItemMotion> motionBatch = new(BatchSize);
        private readonly List<ItemRecord> lifecycleBatch = new(BatchSize);
        private NetworkManager network;
        private PredictionManager predictionManager;
        private uint epoch;
        private uint sessionId;
        private bool worldReady;
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

        private void Awake()
        {
            Instance = this;
            network = GetComponent<NetworkManager>();
            WorldLayer = LayerMask.NameToLayer("ItemWorld");
            HeldLayer = LayerMask.NameToLayer("ItemHeld");
            EnvironmentMask = Physics.DefaultRaycastLayers & ~LayerMask.GetMask("ItemWorld", "ItemHeld", "Player", "PlayerItemHitbox");
        }

        private void Start()
        {
            predictionManager = GetComponent<PredictionManager>();
            network.ServerManager.RegisterBroadcast<ItemBaselineRequest>(SendBaseline);
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
            foreach (var seed in FindObjectsByType<BakedPickup>(FindObjectsSortMode.None))
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
                    Holder = -1, Releaser = -1
                };
                var item = seed.GetComponent<WorldItem>();
                items.Add(record.Motion.Id, item);
                records.Add(record.Motion.Id, record);
                item.Initialize(this, itemRegistry.Get(record.DefinitionId), record, false);
                activePhysicsItems.Add(record.Motion.Id);
            }
            SessionController.Instance.WorldReady(sessionId, epoch);
        }

        public void JoinWorld(Scene scene)
        {
            if (IsHost) return;
            worldScene = scene;
            sessionId = SessionController.Instance.SessionId;
            worldReady = true;
            foreach (var seed in FindObjectsByType<BakedPickup>(FindObjectsSortMode.None))
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
                if (record.State == WorldItemState.World && items.TryGetValue(record.Motion.Id, out var item))
                {
                    record.Motion = item.Capture(ServerTick);
                    record.Sleeping = item.Body.IsSleeping();
                }
                lifecycleBatch.Add(record);
                if (lifecycleBatch.Count == BatchSize) FlushLifecycle(connection);
            }
            FlushLifecycle(connection);
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
            foreach (var record in message.Items)
            {
                uint id = record.Motion.Id;
                if (records.TryGetValue(id, out var previous) && !Newer(record.Motion, previous.Motion)) continue;
                records[id] = record;
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
            if (!records.TryGetValue(motion.Id, out var record) || motion.Revision > record.Motion.Revision)
            {
                if (!earlyMotion.TryGetValue(motion.Id, out var earlier) || Newer(motion, earlier))
                    earlyMotion[motion.Id] = motion;
                return;
            }
            if (record.State != WorldItemState.World || !Newer(motion, record.Motion)) return;
            record.Motion = motion;
            record.Sleeping = false;
            records[motion.Id] = record;
            if (!pendingReleases.ContainsKey(motion.Id) && items.TryGetValue(motion.Id, out var item)) item.ReceiveMotion(motion);
        }

        private static bool Newer(ItemMotion next, ItemMotion previous) =>
            next.Revision > previous.Revision || next.Revision == previous.Revision && next.Tick > previous.Tick;

        public bool TryGetItem(uint id, out WorldItem item) => items.TryGetValue(id, out item);
        public bool TryGetRecord(uint id, out ItemRecord record) => records.TryGetValue(id, out record);
        public bool TryGetPlayer(int id, out PlayerInventory player) => players.TryGetValue(id, out player);
        public ItemDefinition GetDefinition(byte id) => itemRegistry.Get(id);

        internal void RegisterPlayer(PlayerInventory player)
        {
            players[player.ObjectId] = player;
            if (player.IsOwner) LocalInventory = player;
            else if (LocalInventory == player) LocalInventory = null;
            RefreshHolders();
        }

        internal void UnregisterPlayer(PlayerInventory player)
        {
            cleanup.Clear();
            foreach (var record in records.Values)
                if (record.State == WorldItemState.Held && record.Holder == player.ObjectId) cleanup.Add(record.Motion.Id);
            foreach (uint id in cleanup)
            {
                if (IsHost) Remove(id);
                else if (items.TryGetValue(id, out var item)) item.transform.SetParent(transform, true);
            }
            players.Remove(player.ObjectId);
            if (LocalInventory == player) LocalInventory = null;
        }

        private void DropDepartingItems(NetworkConnection connection)
        {
            if (!worldReady || !IsHost || SessionController.Instance.Phase == SessionPhase.Stopping) return;
            foreach (var player in players.Values)
            {
                if (!player || player.Owner != connection) continue;
                var seating = player.GetComponent<PlayerSeating>();
                var pose = seating.Seated ? seating.Cart.GetSeat(seating.SeatIndex).Rider : player.transform;
                Vector3 origin = pose.position;
                Vector3 velocity = seating.PointVelocity;
                cleanup.Clear();
                float spacing = 0.24f;
                foreach (var record in records.Values)
                {
                    if (record.State != WorldItemState.Held || record.Holder != player.ObjectId) continue;
                    cleanup.Add(record.Motion.Id);
                    spacing = Mathf.Max(spacing, items[record.Motion.Id].DropDiameter + 0.05f);
                }
                int width = Mathf.CeilToInt(Mathf.Sqrt(cleanup.Count));
                for (int i = 0; i < cleanup.Count; i++)
                {
                    uint id = cleanup[i];
                    Vector3 offset = new((i % width - (width - 1) * 0.5f) * spacing,
                        1f + i / width * spacing, 1.5f);
                    Release(id, 0, new ItemMotion { Position = origin + Quaternion.Euler(0f, pose.eulerAngles.y, 0f) * offset,
                        Rotation = items[id].Body.rotation, Velocity = velocity }, player);
                }
            }
        }

        public void RefreshHolders()
        {
            foreach (var item in items.Values)
                if (item != null && item.Definition != null) item.AttachHolder();
            LocalInventory?.RefreshHeldPresentation();
        }

        internal void SetHeld(uint id, PlayerInventory holder, bool equipped)
        {
            var record = records[id];
            record.Motion = items[id].Capture(ServerTick);
            record.Motion.Revision++;
            record.State = WorldItemState.Held;
            record.Holder = holder.ObjectId;
            record.Equipped = equipped;
            record.Sleeping = false;
            record.Operation = 0;
            record.Releaser = -1;
            records[id] = record;
            items[id].ApplyRecord(record);
            activePhysicsItems.Remove(id);
            Publish(record);
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

        internal void PredictRelease(uint id, uint operation, ItemMotion motion, PlayerInventory player)
        {
            if (IsHost) return;
            var record = records[id];
            record.Motion = motion;
            record.State = WorldItemState.World;
            record.Sleeping = false;
            record.Holder = -1;
            record.Releaser = player.ObjectId;
            record.Operation = operation;
            record.LaunchTick = ServerTick;
            pendingReleases[id] = operation;
            items[id].Initialize(this, itemRegistry.Get(record.DefinitionId), record, true);
            items[id].Launch(motion);
        }

        internal void Release(uint id, uint operation, ItemMotion motion, PlayerInventory player)
        {
            var record = records[id];
            motion.Id = id;
            motion.Tick = ServerTick;
            motion.Revision = record.Motion.Revision + 1;
            record.Motion = motion;
            record.State = WorldItemState.World;
            record.Holder = -1;
            record.Equipped = false;
            record.Sleeping = false;
            record.Releaser = player.ObjectId;
            record.Operation = operation;
            record.LaunchTick = ServerTick;
            records[id] = record;
            items[id].ApplyRecord(record);
            activePhysicsItems.Add(id);
            Publish(record);
        }

        internal void Rollback(uint id, uint operation)
        {
            if (pendingReleases.TryGetValue(id, out uint pending) && pending != operation) return;
            pendingReleases.Remove(id);
            if (items.TryGetValue(id, out var item) && records.TryGetValue(id, out var record))
                item.Initialize(this, itemRegistry.Get(record.DefinitionId), record, false);
        }

        public void Remove(uint id)
        {
            if (!IsHost || !records.TryGetValue(id, out var record) || record.State == WorldItemState.Removed) return;
            record.State = WorldItemState.Removed;
            record.Motion.Revision++;
            record.Motion.Tick = ServerTick;
            records[id] = record;
            Pool(id);
            Publish(record);
        }

        private void BeforePhysics(float delta)
        {
            if (!worldReady || Replaying) return;
            foreach (var player in players.Values)
                if (!player.Hitbox.Suspended) player.Hitbox.FollowMotor();
            foreach (var item in items.Values)
                if (item != null && item.Definition != null && item.gameObject.activeSelf) item.BeforePhysics();
        }

        private void AfterPhysics(float delta)
        {
            if (!worldReady || Replaying || IsHost) return;
            foreach (var item in items.Values)
                if (item != null && item.Definition != null && item.gameObject.activeSelf) item.AfterPhysics(delta);
        }

        private void LateUpdate()
        {
            if (!worldReady || Replaying) return;
            PlayerItemHitbox victim = LocalInventory != null && LocalInventory.IsOwner ? LocalInventory.Hitbox : null;
            if (victim != null) victim.SamplePresentation();
            foreach (var item in items.Values)
            {
                if (item == null || item.Definition == null || !item.gameObject.activeSelf) continue;
                item.Present();
                if (!IsHost) item.SamplePlayerContact(victim);
            }
        }

        private void AfterTick()
        {
            if (!worldReady || Replaying) return;
            cleanup.Clear();
            foreach (var item in items.Values)
            {
                if (item == null || item.Definition == null || !item.gameObject.activeSelf) continue;
                item.Tick();
                if (!IsHost || item.Record.State != WorldItemState.World) continue;
                uint id = item.Record.Motion.Id;
                Vector3 position = item.Body.position;
                if (!Finite(position) || !Finite(item.Body.linearVelocity) || !Finite(item.Body.angularVelocity) ||
                    position.y < settings.FallBoundary || !worldBounds.Contains(position))
                {
                    cleanup.Add(id);
                    continue;
                }
                if (!item.Body.IsSleeping())
                {
                    if (activePhysicsItems.Add(id))
                    {
                        var record = records[id];
                        record.Sleeping = false;
                        records[id] = record;
                        item.SetRecord(record);
                    }
                }
                else if (activePhysicsItems.Remove(id))
                {
                    var record = records[id];
                    record.Motion = item.Capture(ServerTick);
                    record.Motion.Revision++;
                    record.Sleeping = true;
                    records[id] = record;
                    item.SetRecord(record);
                    Publish(record);
                }
            }
            foreach (uint id in cleanup) Remove(id);
            if (!IsHost || ServerTick % (uint)Mathf.Max(1, Mathf.RoundToInt((float)(1d / TickDelta) / snapshotRate)) != 0) return;
            foreach (uint id in activePhysicsItems)
            {
                motionBatch.Add(items[id].Capture(ServerTick));
                if (motionBatch.Count == BatchSize) FlushMotion();
            }
            FlushMotion();
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

        private void FlushMotion()
        {
            if (motionBatch.Count == 0) return;
            network.ServerManager.Broadcast(observers, new ItemMotionBatch { Epoch = epoch, Items = motionBatch }, channel: Channel.Unreliable);
            motionBatch.Clear();
        }

        private WorldItem Rent(byte definition)
        {
            if (pools.TryGetValue(definition, out var pool) && pool.Count > 0) return pool.Pop();
            var item = Instantiate(itemRegistry.Get(definition).WorldPrefab).GetComponent<WorldItem>();
            SceneManager.MoveGameObjectToScene(item.gameObject, worldScene);
            return item;
        }

        private void Pool(uint id)
        {
            activePhysicsItems.Remove(id);
            pendingReleases.Remove(id);
            if (!items.Remove(id, out var item) || item == null) return;
            if (item.Definition == null)
            {
                Destroy(item.gameObject);
                return;
            }
            byte definition = item.Record.DefinitionId;
            item.ReturnToPool();
            if (!pools.TryGetValue(definition, out var pool)) pools[definition] = pool = new Stack<WorldItem>();
            pool.Push(item);
        }

        private void ConnectionChanged(NetworkConnection connection, RemoteConnectionStateArgs args)
        {
            if (args.ConnectionState == RemoteConnectionState.Stopped) observers.Remove(connection);
        }

        public void EndWorld()
        {
            worldReady = false;
            foreach (var item in items.Values)
                if (item != null) Destroy(item.gameObject);
            items.Clear();
            foreach (var pool in pools.Values)
                foreach (var item in pool)
                    if (item != null) Destroy(item.gameObject);
            pools.Clear();
            records.Clear();
            players.Clear();
            pendingReleases.Clear();
            earlyMotion.Clear();
            activePhysicsItems.Clear();
            observers.Clear();
            motionBatch.Clear();
            lifecycleBatch.Clear();
            LocalInventory = null;
            epoch = 0;
            sessionId = 0;
        }

        internal static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

        private void OnDestroy()
        {
            network.ServerManager.UnregisterBroadcast<ItemBaselineRequest>(SendBaseline);
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
