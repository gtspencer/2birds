using System;
using System.Collections.Generic;
using UnityEngine;

namespace TwoBirds
{
    internal sealed class BirdHitReporter
    {
        private readonly BirdRegistry registry;
        private readonly Dictionary<Vector3Int, List<uint>> grid = new();
        private readonly HashSet<uint> candidates = new();
        private readonly List<uint> largeRoutes = new();
        private readonly Dictionary<uint, (Vector3Int min, Vector3Int max)> lifeCells = new();
        private readonly HashSet<uint> dirtyLives = new();
        private readonly Stack<List<uint>> bucketPool = new();
        private readonly Dictionary<uint, uint> predicted = new();
        private readonly Dictionary<(uint rock, uint operation), bool> resolved = new();
        private readonly Queue<(uint rock, uint operation)> resolvedOrder = new();
        private readonly List<Pending> pending = new();
        private readonly List<Contact> contacts = new();
        private readonly List<uint> scare = new(64);
        private readonly Dictionary<(uint source, uint life, BirdThreatKind kind), double> encounters = new();
        private readonly List<(uint source, uint life, BirdThreatKind kind)> expiredEncounters = new();
        private readonly Dictionary<int, CartSample> carts = new();
        private readonly List<BirdHit> hitBatch = new(12);
        private uint contactSequence;
        private double nextThreats;
        private const float CellSize = 8f;
        private struct Contact { public uint Life; public float Fraction; public BirdHit Hit; }
        private sealed class Pending { public BirdHitReport Report; public bool Waiting; }
        private sealed class CartSample
        {
            public BoxShape[] Boxes;
            public Vector3 Position;
            public Quaternion Rotation;
            public uint Epoch, Revision;
            public double Tick;
            public bool Valid;
        }
        private struct BoxShape { public Vector3 Center, Half; public Quaternion Rotation; }

        public BirdHitReporter(BirdRegistry owner) => registry = owner;
        public void Dirty(uint life) => dirtyLives.Add(life);
        public bool Predicted(uint life) => predicted.ContainsKey(life);
        private static Vector3Int Cell(Vector3 point) => Vector3Int.FloorToInt(point / CellSize);

        private void EnsureGrid()
        {
            foreach (uint life in dirtyLives)
            {
                largeRoutes.Remove(life);
                if (lifeCells.Remove(life, out var cells))
                    for (int x = cells.min.x; x <= cells.max.x; x++)
                        for (int y = cells.min.y; y <= cells.max.y; y++)
                            for (int z = cells.min.z; z <= cells.max.z; z++)
                            {
                                var key = new Vector3Int(x, y, z);
                                var bucket = grid[key];
                                bucket.Remove(life);
                                if (bucket.Count > 0) continue;
                                grid.Remove(key);
                                if (bucketPool.Count < 512) bucketPool.Push(bucket);
                            }
                if (!registry.LiveRecords.TryGetValue(life, out var record)) continue;
                Bounds bounds = RouteBounds(record.Route);
                if (record.HasNext) bounds.Encapsulate(RouteBounds(record.Next));
                bounds.Expand(registry.Species(record.Species).Radius * 2f + 0.64f);
                Vector3Int min = Cell(bounds.min), max = Cell(bounds.max);
                if (CellCount(min, max) > 512) { largeRoutes.Add(record.Life); continue; }
                lifeCells.Add(life, (min, max));
                for (int x = min.x; x <= max.x; x++)
                    for (int y = min.y; y <= max.y; y++)
                        for (int z = min.z; z <= max.z; z++)
                        {
                            var key = new Vector3Int(x, y, z);
                            if (!grid.TryGetValue(key, out var bucket)) grid.Add(key, bucket = bucketPool.Count > 0 ? bucketPool.Pop() : new List<uint>());
                            bucket.Add(record.Life);
                        }
            }
            dirtyLives.Clear();
        }
        private static long CellCount(Vector3Int min, Vector3Int max) => (long)(max.x - min.x + 1) * (max.y - min.y + 1) * (max.z - min.z + 1);

        private static Bounds RouteBounds(BirdRoute route)
        {
            var bounds = new Bounds(route.A, Vector3.zero);
            if (route.Kind == BirdMotionKind.Orbit)
                return new Bounds(route.A, (Abs(route.B) + Abs(route.C)) * 2f);
            if (route.Kind == BirdMotionKind.Curve)
            {
                bounds.Encapsulate(route.B); bounds.Encapsulate(route.C); bounds.Encapsulate(route.D);
                if (route.OrbitRadius > 0f) bounds.Encapsulate(new Bounds(route.D, Vector3.one * route.OrbitRadius * 4f));
            }
            if (route.Surface != null) foreach (var point in route.Surface) bounds.Encapsulate(point);
            return bounds;
        }
        private static Vector3 Abs(Vector3 value) => new(Mathf.Abs(value.x), Mathf.Abs(value.y), Mathf.Abs(value.z));

        private void Query(Bounds bounds)
        {
            EnsureGrid(); candidates.Clear();
            foreach (uint life in largeRoutes) candidates.Add(life);
            Vector3Int min = Cell(bounds.min), max = Cell(bounds.max);
            if (CellCount(min, max) > 4096)
            {
                foreach (uint life in registry.LiveRecords.Keys) candidates.Add(life);
                return;
            }
            for (int x = min.x; x <= max.x; x++)
                for (int y = min.y; y <= max.y; y++)
                    for (int z = min.z; z <= max.z; z++)
                        if (grid.TryGetValue(new Vector3Int(x, y, z), out var bucket))
                            foreach (uint life in bucket) candidates.Add(life);
        }

        public void RockContact(BirdHitReport source, BirdRecord bird, float speed, Vector3 position, bool waiting)
        {
            contacts.Clear();
            contacts.Add(new Contact { Life = bird.Life, Hit = new BirdHit { Life = bird.Life,
                Contact = ++contactSequence, Speed = speed, Position = BirdMotion.Quantize(position) } });
            QueueContacts(source, speed, waiting);
        }
        private void QueueContacts(BirdHitReport source, float speed, bool waiting)
        {
            contacts.Sort(CompareContacts);
            foreach (var contact in contacts)
            {
                if (predicted.ContainsKey(contact.Life)) continue;
                var bird = registry.LiveRecords[contact.Life]; var species = registry.Species(bird.Species);
                float lethal = species.LethalSpeed > 0f ? species.LethalSpeed : registry.Settings.LethalSpeed;
                if (source.Cart || speed >= lethal)
                {
                    predicted[contact.Life] = contact.Hit.Contact;
                    registry.PredictDeath(contact.Life, bird.Species, contact.Hit.Position);
                }
                var entry = FindPending(source, waiting);
                entry.Report.Hits.Add(contact.Hit);
            }
        }
        private static int CompareContacts(Contact a, Contact b) => a.Fraction.CompareTo(b.Fraction);
        private Pending FindPending(BirdHitReport source, bool waiting)
        {
            foreach (var entry in pending)
                if (entry.Report.Cart == source.Cart && entry.Report.Source == source.Source && entry.Report.Player == source.Player &&
                    entry.Report.Operation == source.Operation && entry.Report.MotionEpoch == source.MotionEpoch && entry.Report.SeatRevision == source.SeatRevision) return entry;
            if (resolved.TryGetValue((source.Source, source.Operation), out bool accepted)) waiting = false;
            source.Hits = new List<BirdHit>();
            var result = new Pending { Report = source, Waiting = waiting }; pending.Add(result); return result;
        }

        public void Flush()
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var entry = pending[i];
                if (entry.Waiting) continue;
                for (int offset = 0; offset < entry.Report.Hits.Count; offset += 12)
                {
                    hitBatch.Clear();
                    int end = Math.Min(offset + 12, entry.Report.Hits.Count);
                    for (int j = offset; j < end; j++) hitBatch.Add(entry.Report.Hits[j]);
                    var report = entry.Report; report.Hits = hitBatch;
                    registry.SubmitHits(report);
                }
                pending.RemoveAt(i);
            }
        }

        public void ReleaseResolved(uint rock, uint operation, bool accepted)
        {
            resolved[(rock, operation)] = accepted;
            resolvedOrder.Enqueue((rock, operation));
            while (resolvedOrder.Count > 512) resolved.Remove(resolvedOrder.Dequeue());
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var entry = pending[i];
                if (entry.Report.Cart || entry.Report.Source != rock || entry.Report.Operation != operation) continue;
                if (accepted) entry.Waiting = false;
                else
                {
                    foreach (var hit in entry.Report.Hits) Confirmed(hit.Life, false, hit.Contact);
                    pending.RemoveAt(i);
                }
            }
        }

        public void Confirmed(uint life, bool dead, uint contact = 0)
        {
            if (!predicted.TryGetValue(life, out uint pendingContact) || !dead && pendingContact != contact) return;
            predicted.Remove(life);
            if (!dead && registry.LiveRecords.ContainsKey(life)) registry.RestorePrediction(life);
        }

        public void SampleThreats(double now)
        {
            if (now < nextThreats) return;
            nextThreats = now + 0.2d / registry.Delta;
            var player = WorldItemRegistry.Instance.LocalInventory;
            if (player) Threat(BirdThreatKind.Player, (uint)player.ObjectId, player.Hitbox.PresentedCenter, registry.MaximumScareRadius, now, false);
            foreach (var cart in GolfCartNetwork.Carts.Values)
                if (cart && cart.ReportsWorldEffects) Threat(BirdThreatKind.Cart, (uint)cart.ObjectId, cart.transform.position, registry.MaximumScareRadius, now, false);
            expiredEncounters.Clear();
            foreach (var pair in encounters) if (pair.Value < now - 5d / registry.Delta) expiredEncounters.Add(pair.Key);
            foreach (var key in expiredEncounters) encounters.Remove(key);
        }

        public void Threat(BirdThreatKind kind, uint source, Vector3 position, float radius, double now, bool horn)
        {
            Query(new Bounds(position, Vector3.one * radius * 2f)); scare.Clear();
            foreach (uint life in candidates)
            {
                if (predicted.ContainsKey(life) || !registry.LiveRecords.TryGetValue(life, out var record) || record.Interrupt != BirdInterrupt.Calm) continue;
                var key = (source, life, kind);
                if (encounters.TryGetValue(key, out double retry) && retry > now) continue;
                Vector3 target = BirdMotion.Evaluate(BirdMotion.Current(record, now), now, registry.Delta).Position;
                float distance = horn ? radius : registry.Species(record.Species).ScareRadius;
                if ((target - position).sqrMagnitude > distance * distance) continue;
                if (!horn && Physics.Linecast(position, target, registry.Settings.SolidMask, QueryTriggerInteraction.Ignore)) continue;
                encounters[key] = now + 2d / registry.Delta;
                registry.Alert(life);
                scare.Add(life);
                if (scare.Count == 64) SendScare(position);
            }
            if (scare.Count > 0) SendScare(position);
        }
        private void SendScare(Vector3 position)
        {
            registry.SubmitScare(new BirdScareReport { Position = position, Lives = scare });
            scare.Clear();
        }

        public void CartBefore(GolfCartNetwork cart)
        {
            if (!carts.TryGetValue(cart.ObjectId, out var sample))
            {
                var colliders = cart.Controller.Chassis;
                var boxes = new List<BoxShape>();
                foreach (var collider in colliders)
                {
                    if (collider.isTrigger) continue;
                    Vector3 size = Vector3.Scale(collider.size, collider.transform.lossyScale);
                    boxes.Add(new BoxShape { Center = Quaternion.Inverse(cart.transform.rotation) * (collider.transform.TransformPoint(collider.center) - cart.transform.position),
                        Half = Abs(size) * 0.5f, Rotation = Quaternion.Inverse(cart.transform.rotation) * collider.transform.rotation });
                }
                sample = new CartSample { Boxes = boxes.ToArray() }; carts.Add(cart.ObjectId, sample);
            }
            sample.Position = cart.Controller.Body.position; sample.Rotation = cart.Controller.Body.rotation;
            sample.Epoch = cart.Epoch; sample.Revision = cart.StateRevision; sample.Tick = registry.SimulationTick;
            sample.Valid = cart.ReportsWorldEffects;
        }
        public void ForgetCart(int id) => carts.Remove(id);

        public void CartAfter(GolfCartNetwork cart)
        {
            if (!carts.TryGetValue(cart.ObjectId, out var sample) || !sample.Valid || sample.Epoch != cart.Epoch || sample.Revision != cart.StateRevision) return;
            Vector3 end = cart.Controller.Body.position; Quaternion rotation = cart.Controller.Body.rotation;
            if ((end - sample.Position).sqrMagnitude < 0.0000001f && Quaternion.Angle(sample.Rotation, rotation) < 0.01f) return;
            var report = new BirdHitReport { Cart = true, Source = (uint)cart.ObjectId, Player = registry.PlayerToken(cart.DriverId), MotionEpoch = cart.Epoch, SeatRevision = cart.StateRevision };
            int steps = Mathf.Max(1, Mathf.CeilToInt(Quaternion.Angle(sample.Rotation, rotation) / 5f));
            contacts.Clear();
            foreach (var box in sample.Boxes)
                for (int step = 0; step < steps; step++)
                {
                    float a = (float)step / steps, b = (float)(step + 1) / steps;
                    Quaternion fromRotation = Quaternion.Slerp(sample.Rotation, rotation, a), toRotation = Quaternion.Slerp(sample.Rotation, rotation, b);
                    Vector3 from = Vector3.Lerp(sample.Position, end, a) + fromRotation * box.Center;
                    Vector3 to = Vector3.Lerp(sample.Position, end, b) + toRotation * box.Center;
                    var bounds = new Bounds(from, Vector3.one * box.Half.magnitude * 2f); bounds.Encapsulate(to); bounds.Expand(box.Half.magnitude * 2f);
                    Query(bounds);
                    foreach (uint life in candidates)
                    {
                        if (predicted.ContainsKey(life) || !registry.LiveRecords.TryGetValue(life, out var bird)) continue;
                        float radius = registry.Species(bird.Species).Radius;
                        Vector3 offset = registry.PresentationOffset(life);
                        Vector3 birdFrom = BirdMotion.Evaluate(BirdMotion.Current(bird, sample.Tick + a), sample.Tick + a, registry.Delta).Position + offset;
                        Vector3 birdTo = BirdMotion.Evaluate(BirdMotion.Current(bird, sample.Tick + b), sample.Tick + b, registry.Delta).Position + offset;
                        Vector3 localFrom = Quaternion.Inverse(fromRotation * box.Rotation) * (birdFrom - from);
                        Vector3 localTo = Quaternion.Inverse(toRotation * box.Rotation) * (birdTo - to);
                        if (step == 0 && BirdMotion.SweepBox(localFrom, localFrom, box.Half, radius, out _)) continue;
                        if (!BirdMotion.SweepBox(localFrom, localTo, box.Half, radius, out float fraction)) continue;
                        float t = Mathf.Lerp(a, b, fraction);
                        contacts.Add(new Contact { Life = life, Fraction = t, Hit = new BirdHit { Life = life,
                            Contact = ++contactSequence, Position = BirdMotion.Quantize(Vector3.Lerp(birdFrom, birdTo, fraction)) } });
                    }
                }
            QueueContacts(report, 0f, false);
        }

        public void Clear()
        {
            grid.Clear(); candidates.Clear(); largeRoutes.Clear(); lifeCells.Clear(); dirtyLives.Clear(); bucketPool.Clear(); predicted.Clear(); pending.Clear(); resolved.Clear(); resolvedOrder.Clear(); encounters.Clear(); carts.Clear();
            foreach (uint life in registry.LiveRecords.Keys) Dirty(life);
        }
    }
}
