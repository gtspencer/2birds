using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Serializing;
using FishNet.Transporting;
using UnityEngine;

namespace TwoBirds
{
    public sealed partial class BirdRegistry
    {
        private sealed class Transfer
        {
            public NetworkConnection Connection;
            public uint Attempt, Snapshot, Sequence;
            public BirdRecord[] Records;
            public int Cursor, Chunk;
            public float Credit, EventCredit;
            public readonly Queue<BirdEvent> Events = new();
        }
        private readonly HashSet<NetworkConnection> observers = new();
        private readonly List<Transfer> transfers = new();
        private readonly Dictionary<uint, BirdRecord> staging = new();
        private readonly HashSet<int> receivedChunks = new();
        private readonly List<BirdEvent> stagedEvents = new();
        private readonly HashSet<uint> repairs = new();
        private readonly Dictionary<uint, uint> expiredRequests = new();
        private readonly List<BirdRecord> recordBatch = new(4);
        private readonly List<BirdDigestEntry> digestBatch = new(8);
        private uint nextSnapshot, receivingSnapshot, snapshotSequence, receivedSequence;
        private int expectedCount, digestCursor;
        private double nextDigest;
        private bool receiving;

        private void RegisterMessages()
        {
            var server = network.ServerManager; var client = network.ClientManager;
            server.RegisterBroadcast<BirdBaselineRequest>(SendBaseline);
            server.RegisterBroadcast<BirdRecordRequest>(SendRecord);
            server.RegisterBroadcast<BirdScareReport>(ReceiveScare);
            server.RegisterBroadcast<BirdHitReport>(ReceiveHits);
            client.RegisterBroadcast<BirdBaselineStart>(StartBaseline);
            client.RegisterBroadcast<BirdBaselineChunk>(ReceiveChunk);
            client.RegisterBroadcast<BirdBaselineComplete>(CompleteBaseline);
            client.RegisterBroadcast<BirdEvent>(ReceiveEvent);
            client.RegisterBroadcast<BirdDigest>(ReceiveDigest);
            client.RegisterBroadcast<BirdRecordReply>(ReceiveRecord);
            client.RegisterBroadcast<BirdHitResult>(ReceiveHitResult);
            client.RegisterBroadcast<BirdReward>(ReceiveReward);
            client.RegisterBroadcast<BirdPlayerToken>(ReceivePlayerToken);
            server.OnRemoteConnectionState += ConnectionChanged;
        }

        private void UnregisterMessages()
        {
            var server = network.ServerManager; var client = network.ClientManager;
            server.UnregisterBroadcast<BirdBaselineRequest>(SendBaseline);
            server.UnregisterBroadcast<BirdRecordRequest>(SendRecord);
            server.UnregisterBroadcast<BirdScareReport>(ReceiveScare);
            server.UnregisterBroadcast<BirdHitReport>(ReceiveHits);
            client.UnregisterBroadcast<BirdBaselineStart>(StartBaseline);
            client.UnregisterBroadcast<BirdBaselineChunk>(ReceiveChunk);
            client.UnregisterBroadcast<BirdBaselineComplete>(CompleteBaseline);
            client.UnregisterBroadcast<BirdEvent>(ReceiveEvent);
            client.UnregisterBroadcast<BirdDigest>(ReceiveDigest);
            client.UnregisterBroadcast<BirdRecordReply>(ReceiveRecord);
            client.UnregisterBroadcast<BirdHitResult>(ReceiveHitResult);
            client.UnregisterBroadcast<BirdReward>(ReceiveReward);
            client.UnregisterBroadcast<BirdPlayerToken>(ReceivePlayerToken);
            server.OnRemoteConnectionState -= ConnectionChanged;
        }

        private void RequestBaseline() => network.ClientManager.Broadcast(new BirdBaselineRequest { Attempt = attempt, Fingerprint = fingerprint });

        private void SendBaseline(NetworkConnection connection, BirdBaselineRequest request, Channel channel)
        {
            if (!active || connection.IsLocalClient) return;
            observers.Remove(connection);
            transfers.RemoveAll(t => t.Connection == connection);
            var transfer = new Transfer { Connection = connection, Attempt = request.Attempt, Snapshot = ++nextSnapshot,
                Sequence = sequence, Records = new BirdRecord[records.Count] };
            records.Values.CopyTo(transfer.Records, 0);
            network.ServerManager.Broadcast(connection, new BirdBaselineStart { Attempt = request.Attempt, Epoch = epoch,
                Snapshot = transfer.Snapshot, Sequence = sequence, Fingerprint = fingerprint, Count = records.Count });
            if (request.Fingerprint != fingerprint) return;
            foreach (var token in playerTokens)
                network.ServerManager.Broadcast(connection, new BirdPlayerToken { Epoch = epoch, ObjectId = token.Key, Token = token.Value });
            transfers.Add(transfer);
        }

        private void SendTransfers()
        {
            for (int i = transfers.Count - 1; i >= 0; i--)
            {
                var transfer = transfers[i];
                if (!transfer.Connection.IsActive) { transfers.RemoveAt(i); continue; }
                if (transfer.Events.Count > 2048)
                {
                    var request = new BirdBaselineRequest { Attempt = transfer.Attempt, Fingerprint = fingerprint };
                    SendBaseline(transfer.Connection, request, Channel.Reliable);
                    continue;
                }
                bool snapshotPending = transfer.Cursor < transfer.Records.Length;
                float allowance = (float)Delta * 16384f;
                transfer.EventCredit = Mathf.Min(2000f, transfer.EventCredit + allowance * (snapshotPending ? 0.5f : 1f));
                if (snapshotPending)
                {
                    transfer.Credit = Mathf.Min(2000f, transfer.Credit + allowance * 0.5f);
                    if (transfer.Events.Count == 0)
                    {
                        transfer.Credit = Mathf.Min(2000f, transfer.Credit + transfer.EventCredit);
                        transfer.EventCredit = 0f;
                    }
                }
                int limit = Mathf.Min(1000, network.TransportManager.GetMTU(transfer.Connection.TransportIndex, (byte)Channel.Unreliable) - 96);
                if (snapshotPending)
                {
                    recordBatch.Clear();
                    var writer = WriterPool.Retrieve();
                    int size = 48;
                    while (transfer.Cursor < transfer.Records.Length)
                    {
                        writer.Clear(); writer.WriteBirdRecord(transfer.Records[transfer.Cursor]);
                        int bytes = writer.Length;
                        if (size + bytes > limit || size + bytes > transfer.Credit) break;
                        size += bytes;
                        recordBatch.Add(transfer.Records[transfer.Cursor++]);
                    }
                    writer.Store();
                    if (recordBatch.Count > 0)
                    {
                        network.ServerManager.Broadcast(transfer.Connection, new BirdBaselineChunk { Epoch = epoch,
                            Snapshot = transfer.Snapshot, Index = transfer.Chunk++, Records = recordBatch });
                        transfer.Credit -= size;
                    }
                }
                if (transfer.Cursor == transfer.Records.Length)
                {
                    transfer.EventCredit += transfer.Credit;
                    transfer.Credit = 0f;
                }
                var eventWriter = WriterPool.Retrieve();
                while (transfer.Events.Count > 0)
                {
                    eventWriter.Clear();
                    eventWriter.WriteBirdEvent(transfer.Events.Peek());
                    int bytes = eventWriter.Length + 48;
                    if (transfer.EventCredit < bytes) break;
                    network.ServerManager.Broadcast(transfer.Connection, transfer.Events.Dequeue());
                    transfer.EventCredit -= bytes;
                }
                eventWriter.Store();
                if (transfer.Cursor < transfer.Records.Length || transfer.Events.Count > 0) continue;
                network.ServerManager.Broadcast(transfer.Connection, new BirdBaselineComplete { Epoch = epoch,
                    Snapshot = transfer.Snapshot, Attempt = transfer.Attempt, Sequence = sequence });
                SendBalance(transfer.Connection);
                observers.Add(transfer.Connection);
                transfers.RemoveAt(i);
            }
        }

        private void StartBaseline(BirdBaselineStart message, Channel channel)
        {
            if (Host || !active || message.Attempt != attempt || attempt != SessionController.Instance.SessionId) return;
            if (message.Fingerprint != fingerprint) { SessionController.Instance.Leave("Bird content differs from the host. Use matching catalogs, habitats, perches, and content versions."); return; }
            if (message.Epoch == epoch && message.Snapshot <= receivingSnapshot) return;
            if (epoch != message.Epoch) { clock.Reset(); hitReporter.Clear(); }
            epoch = message.Epoch; receivingSnapshot = message.Snapshot; snapshotSequence = message.Sequence;
            expectedCount = message.Count; receiving = true;
            staging.Clear(); receivedChunks.Clear(); stagedEvents.Clear(); repairs.Clear();
        }

        private void ReceiveChunk(BirdBaselineChunk message, Channel channel)
        {
            if (Host || !receiving || message.Epoch != epoch || message.Snapshot != receivingSnapshot || !receivedChunks.Add(message.Index)) return;
            foreach (var record in message.Records) staging[record.Life] = record;
        }

        private void CompleteBaseline(BirdBaselineComplete message, Channel channel)
        {
            if (Host || !receiving || message.Epoch != epoch || message.Snapshot != receivingSnapshot || message.Attempt != attempt) return;
            if (staging.Count != expectedCount) { RequestBaseline(); return; }
            ClearPresentation();
            records.Clear(); lives.Clear();
            foreach (var pair in staging) { records.Add(pair.Key, pair.Value); lives.Add(pair.Key); }
            receivedSequence = snapshotSequence;
            foreach (var update in stagedEvents) ApplyEvent(update, false);
            receivedSequence = message.Sequence;
            staging.Clear(); stagedEvents.Clear(); receivedChunks.Clear();
            hitReporter.Clear();
            AdvanceClaims(SimulationTick);
            receiving = false; ready = true;
            lastPresentationTick = Now;
            SessionController.Instance.BirdsReady(attempt, epoch);
        }

        private void Publish(BirdEventKind kind, BirdRecord record, Vector3 position = default)
        {
            hitReporter?.Dirty(record.Life);
            var message = new BirdEvent { Epoch = epoch, Sequence = ++sequence, Kind = kind, Record = record, Position = position };
            network.ServerManager.Broadcast(observers, message);
            foreach (var transfer in transfers) transfer.Events.Enqueue(message);
        }

        private void ReceiveEvent(BirdEvent message, Channel channel)
        {
            if (Host || !active || message.Epoch != epoch) return;
            if (receiving)
            {
                if (message.Sequence > snapshotSequence) stagedEvents.Add(message);
                if (stagedEvents.Count > 2048) { receiving = false; RequestBaseline(); }
                return;
            }
            if (ready) ApplyEvent(message, true);
        }

        private void ApplyEvent(BirdEvent message, bool effects)
        {
            if (message.Sequence <= receivedSequence) return;
            receivedSequence = message.Sequence;
            uint life = message.Record.Life;
            if (message.Kind == BirdEventKind.Death)
            {
                if (effects) ShowDeath(life, message.Record.Species, message.Position);
                RemoveLocal(life);
                hitReporter.Confirmed(life, true);
                return;
            }
            if (message.Kind == BirdEventKind.Plan && !records.ContainsKey(life)) return;
            if (records.TryGetValue(life, out var previous) && previous.Revision >= message.Record.Revision) return;
            if (message.Kind == BirdEventKind.Plan)
            {
                uint routeRevision = message.Record.Route.Revision;
                if (previous.Route.Revision == routeRevision) message.Record.Route = previous.Route;
                else if (previous.HasNext && previous.Next.Revision == routeRevision) message.Record.Route = previous.Next;
                else { RequestRecord(life); return; }
                message.Record.Species = previous.Species; message.Record.Zone = previous.Zone; message.Record.Biome = previous.Biome;
            }
            if (!records.ContainsKey(life)) lives.Add(life);
            double now = Now;
            if (effects && (!message.Record.HasNext || message.Record.Next.StartTick <= now) && views.TryGetValue(life, out var view))
                view.Rebase(message.Record, now, Delta);
            records[life] = message.Record;
            if (effects && message.Record.Interrupt == BirdInterrupt.WaitingToFlee && previous.Interrupt == BirdInterrupt.Calm) Alert(life);
            hitReporter.Dirty(life);
        }

        private void RemoveLocal(uint life)
        {
            records.Remove(life); lives.Remove(life); decisions.Remove(life); repairs.Remove(life);
            expiredRequests.Remove(life);
            ReturnView(life);
            hitReporter?.Dirty(life);
        }

        private void SendDigest(double now)
        {
            if (now < nextDigest || lives.Count == 0 || observers.Count == 0) return;
            int count = Mathf.Min(8, lives.Count);
            nextDigest = now + 2d / Delta * count / lives.Count;
            digestBatch.Clear();
            for (int i = 0; i < count; i++)
            {
                digestCursor %= lives.Count;
                var record = records[lives[digestCursor++]];
                digestBatch.Add(new BirdDigestEntry { Life = record.Life, Revision = record.Revision });
            }
            network.ServerManager.Broadcast(observers, new BirdDigest { Epoch = epoch, Sequence = sequence, Entries = digestBatch }, channel: Channel.Unreliable);
        }

        private void ReceiveDigest(BirdDigest message, Channel channel)
        {
            if (Host || !ready || receiving || message.Epoch != epoch || message.Sequence < receivedSequence) return;
            foreach (var entry in message.Entries)
                if (!records.TryGetValue(entry.Life, out var record) || record.Revision != entry.Revision)
                    RequestRecord(entry.Life);
        }

        private void RequestRecord(uint life)
        {
            if (repairs.Add(life)) network.ClientManager.Broadcast(new BirdRecordRequest { Epoch = epoch, Life = life });
        }
        private void RequestExpired(BirdRecord record)
        {
            if (expiredRequests.TryGetValue(record.Life, out uint revision) && revision == record.Revision) return;
            expiredRequests[record.Life] = record.Revision;
            RequestRecord(record.Life);
        }

        private void SendRecord(NetworkConnection connection, BirdRecordRequest message, Channel channel)
        {
            if (!active || message.Epoch != epoch || !observers.Contains(connection)) return;
            bool alive = records.TryGetValue(message.Life, out var record);
            network.ServerManager.Broadcast(connection, new BirdRecordReply { Epoch = epoch, Life = message.Life, Alive = alive, Record = record });
        }

        private void ReceiveRecord(BirdRecordReply message, Channel channel)
        {
            if (Host || !ready || message.Epoch != epoch || !repairs.Remove(message.Life)) return;
            if (!message.Alive) { RemoveLocal(message.Life); return; }
            if (!records.TryGetValue(message.Life, out var previous)) { RequestBaseline(); return; }
            if (message.Record.Revision < previous.Revision) return;
            if (views.TryGetValue(message.Life, out var view)) view.Rebase(message.Record, Now, Delta);
            records[message.Life] = message.Record;
            hitReporter.Dirty(message.Life);
        }

        private void ConnectionChanged(NetworkConnection connection, RemoteConnectionStateArgs args)
        {
            if (args.ConnectionState != RemoteConnectionState.Stopped) return;
            observers.Remove(connection); transfers.RemoveAll(t => t.Connection == connection);
        }

        private void ClearNetworking()
        {
            observers.Clear(); transfers.Clear(); staging.Clear(); receivedChunks.Clear(); stagedEvents.Clear(); repairs.Clear(); expiredRequests.Clear();
            receiving = false; receivingSnapshot = receivedSequence = 0; nextDigest = 0; digestCursor = 0;
        }
    }
}
