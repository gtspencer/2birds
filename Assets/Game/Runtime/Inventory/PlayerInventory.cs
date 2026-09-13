using System;
using FishNet.Connection;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace TwoBirds
{
    public struct InventoryRequest
    {
        public ulong RequestId, ExpectedRevision, EntryId;
        public int Source, Destination;
        public bool Drop;
    }
    public struct InventoryResult
    {
        public ulong RequestId, Revision;
        public InventoryStatus Status;
    }
    public sealed class PlayerInventory : NetworkBehaviour
    {
        [SerializeField] private ItemCatalog catalog;
        private readonly SyncVar<InventorySnapshot> state = new(new SyncTypeSettings(ReadPermission.OwnerOnly));
        private InventoryModel model;
        private InventorySnapshot confirmed;
        private InventoryResult lastResult;
        private ulong nextRequest, pendingId;
        private InventoryResult? pendingResult;
        private float pendingSince, nextRefresh;
        public ItemCatalog Catalog => catalog;
        public InventorySnapshot Snapshot => confirmed.Copy();
        public bool Pending => pendingId != 0;
        public int PendingSource { get; private set; } = -1;
        public string Message { get; private set; } = "";
        public IInventoryDropSink DropSink { private get; set; }
        public event Action Changed;
        private void Awake()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (InventoryValidation.Enabled) catalog = InventoryValidation.Catalog;
#endif
            state.OnChange += StateChanged;
        }
        public override void OnStartServer()
        {
            model = new InventoryModel(catalog);
            lastResult = default;
            state.Value = model.Snapshot;
        }
        public override void OnStartClient() { if (IsOwner) Accept(state.Value); }
        private void StateChanged(InventorySnapshot previous, InventorySnapshot next, bool asServer)
        {
            if (IsOwner && IsClientInitialized) Accept(next);
        }
        private void Accept(InventorySnapshot snapshot)
        {
            if (snapshot.Slots == null || snapshot.Revision < confirmed.Revision) return;
            confirmed = snapshot.Copy();
            CompletePending();
            Changed?.Invoke();
        }
        public InventoryAddResult ServerAdd(string definitionId, int quantity, ulong uniqueId = 0)
        {
            if (!IsServerInitialized || model == null) return new InventoryAddResult { Status = InventoryStatus.Invalid, Remainder = quantity };
            var result = model.Add(definitionId, quantity, uniqueId);
            if (result.Accepted > 0) state.Value = model.Snapshot;
            return result;
        }
        public bool Request(int source, int destination, bool drop)
        {
            if (!IsOwner || !IsClientInitialized || Pending || source < 0 || source >= InventoryModel.SlotCount || nextRequest == ulong.MaxValue) return false;
            var entry = confirmed.Get(source);
            if (entry.IsEmpty) return false;
            pendingId = ++nextRequest;
            PendingSource = source;
            pendingResult = null;
            pendingSince = Time.unscaledTime;
            nextRefresh = pendingSince + 5f;
            Message = "Waiting for inventory...";
            Changed?.Invoke();
            Submit(new InventoryRequest { RequestId = pendingId, ExpectedRevision = confirmed.Revision,
                Source = source, EntryId = entry.EntryId, Destination = destination, Drop = drop });
            return true;
        }
        [ServerRpc(RequireOwnership = true)]
        private void Submit(InventoryRequest request, NetworkConnection sender = null)
        {
            if (sender == null || sender != Owner || !sender.IsActive || model == null) return;
            InventoryResult result;
            if (request.RequestId == lastResult.RequestId && request.RequestId != 0) result = lastResult;
            else if (request.RequestId == 0 || request.RequestId < lastResult.RequestId)
                result = new InventoryResult { RequestId = request.RequestId, Revision = model.Revision, Status = InventoryStatus.Invalid };
            else
            {
                var status = model.Mutate(request.ExpectedRevision, request.Source, request.EntryId, request.Destination, request.Drop, DropSink, Owner.ClientId);
                if (status == InventoryStatus.Success) state.Value = model.Snapshot;
                result = new InventoryResult { RequestId = request.RequestId, Revision = model.Revision, Status = status };
                lastResult = result;
            }
            ReceiveResult(sender, result, model.Snapshot);
        }
        [TargetRpc]
        private void ReceiveResult(NetworkConnection owner, InventoryResult result, InventorySnapshot snapshot)
        {
            if (!IsOwner) return;
            if (result.RequestId == pendingId) pendingResult = result;
            Accept(snapshot);
        }
        private void CompletePending()
        {
            if (!Pending || !pendingResult.HasValue || confirmed.Revision < pendingResult.Value.Revision) return;
            Message = Feedback(pendingResult.Value.Status);
            pendingId = 0;
            PendingSource = -1;
            pendingResult = null;
        }
        public static string Feedback(InventoryStatus status) => status switch
        {
            InventoryStatus.Success or InventoryStatus.NoChange => "",
            InventoryStatus.Full => "That stack is full",
            InventoryStatus.DropUnavailable => "Dropping items is not available yet",
            InventoryStatus.TransferFailed => "Could not drop this item",
            InventoryStatus.Stale => "Inventory changed. Please try again",
            _ => "That move is not available"
        };
        private void Update()
        {
            if (!IsOwner || !Pending || Time.unscaledTime < nextRefresh) return;
            Message = "Waiting for connection and inventory...";
            nextRefresh = Time.unscaledTime + 5f;
            Changed?.Invoke();
            RefreshState(pendingId);
        }
        [ServerRpc(RequireOwnership = true)]
        private void RefreshState(ulong requestId, NetworkConnection sender = null)
        {
            if (sender == null || sender != Owner || !sender.IsActive || model == null) return;
            if (lastResult.RequestId == requestId) ReceiveResult(sender, lastResult, model.Snapshot);
            else ReceiveState(sender, model.Snapshot);
        }
        [TargetRpc]
        private void ReceiveState(NetworkConnection owner, InventorySnapshot snapshot) { if (IsOwner) Accept(snapshot); }
        public override void OnOwnershipClient(NetworkConnection previousOwner) { if (!IsOwner) Clear(); }
        public override void OnStopClient() => Clear();
        private void Clear()
        {
            confirmed = default;
            pendingId = nextRequest = 0;
            pendingResult = null;
            PendingSource = -1;
            Message = "";
            Changed?.Invoke();
        }
        public override void OnStopServer() { model?.Dispose(); model = null; DropSink = null; }
    }
}
