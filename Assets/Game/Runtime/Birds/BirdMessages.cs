using System.Collections.Generic;
using FishNet.Broadcast;
using FishNet.Serializing;
using UnityEngine;

namespace TwoBirds
{
    public struct BirdBaselineRequest : IBroadcast { public uint Attempt, Fingerprint; }
    public struct BirdBaselineStart : IBroadcast { public uint Attempt, Epoch, Snapshot, Sequence, Fingerprint; public int Count; }
    public struct BirdBaselineChunk : IBroadcast { public uint Epoch, Snapshot; public int Index; public List<BirdRecord> Records; }
    public struct BirdBaselineComplete : IBroadcast { public uint Epoch, Snapshot, Attempt, Sequence; }
    public enum BirdEventKind : byte { Spawn, Plan, Death }
    public struct BirdEvent : IBroadcast
    {
        public uint Epoch, Sequence;
        public BirdEventKind Kind;
        public BirdRecord Record;
        public Vector3 Position;
    }
    public struct BirdDigestEntry { public uint Life, Revision; }
    public struct BirdDigest : IBroadcast { public uint Epoch, Sequence; public List<BirdDigestEntry> Entries; }
    public struct BirdRecordRequest : IBroadcast { public uint Epoch, Life; }
    public struct BirdRecordReply : IBroadcast { public uint Epoch, Life; public bool Alive; public BirdRecord Record; }
    public enum BirdThreatKind : byte { Player, Rock, Cart, Horn }
    public struct BirdScareReport : IBroadcast
    {
        public uint Epoch;
        public Vector3 Position;
        public List<uint> Lives;
    }
    public struct BirdHit
    {
        public uint Life, Contact;
        public float Speed;
        public Vector3 Position;
    }
    public struct BirdHitReport : IBroadcast
    {
        public uint Epoch, Source, Player, Operation, MotionEpoch, SeatRevision;
        public bool Cart;
        public List<BirdHit> Hits;
    }
    public struct BirdHitResult : IBroadcast { public uint Epoch, Life, Contact; public bool Dead; }
    public struct BirdPlayerToken : IBroadcast { public uint Epoch, Token; public int ObjectId; }
    public struct BirdReward : IBroadcast
    {
        public uint Epoch, Revision, Kills;
        public int Balance, Reward, Bonus;
        public bool Notify;
    }

    public static class BirdSerializers
    {
        public static void WriteBirdRoute(this Writer writer, BirdRoute route)
        {
            writer.WriteUInt32(route.Revision); writer.WriteUInt32(route.StartTick);
            writer.WriteUInt8Unpacked((byte)route.Kind); writer.WriteUInt8Unpacked((byte)route.Activity);
            writer.WriteVector3(route.A);
            writer.WriteSingle(route.Facing);
            writer.WriteUInt16(route.Habitat); writer.WriteUInt16(route.Perch);
            if (route.Kind == BirdMotionKind.Hold) return;
            writer.WriteSingle(route.Seconds);
            if (route.Kind == BirdMotionKind.Surface)
            {
                int count = route.Surface.Length; writer.WriteUInt8Unpacked((byte)count);
                for (int i = 1; i < count; i++) WritePoint(writer, route.Surface[i], route.A);
                return;
            }
            Vector3 origin = route.Kind == BirdMotionKind.Orbit ? Vector3.zero : route.A;
            WritePoint(writer, route.B, origin); WritePoint(writer, route.C, origin);
            if (route.Kind == BirdMotionKind.Orbit) return;
            WritePoint(writer, route.D, route.A);
            writer.WriteSingle(route.Takeoff); writer.WriteSingle(route.Landing); writer.WriteSingle(route.OrbitRadius);
            if (route.OrbitRadius > 0f) writer.WriteSingle(route.OrbitSeconds);
        }
        public static BirdRoute ReadBirdRoute(this Reader reader)
        {
            var route = new BirdRoute { Revision = reader.ReadUInt32(), StartTick = reader.ReadUInt32(),
                Kind = (BirdMotionKind)reader.ReadUInt8Unpacked(), Activity = (BirdActivity)reader.ReadUInt8Unpacked(), A = reader.ReadVector3(), Facing = reader.ReadSingle() };
            route.Habitat = reader.ReadUInt16(); route.Perch = reader.ReadUInt16();
            if (route.Kind == BirdMotionKind.Hold) { route.D = route.A; return route; }
            route.Seconds = reader.ReadSingle();
            if (route.Kind == BirdMotionKind.Surface)
            {
                int count = reader.ReadUInt8Unpacked();
                route.Surface = new Vector3[count];
                route.Surface[0] = route.A;
                for (int i = 1; i < count; i++) route.Surface[i] = ReadPoint(reader, route.A);
                route.D = route.Surface[count - 1];
                return route;
            }
            Vector3 origin = route.Kind == BirdMotionKind.Orbit ? Vector3.zero : route.A;
            route.B = ReadPoint(reader, origin); route.C = ReadPoint(reader, origin);
            if (route.Kind == BirdMotionKind.Orbit) return route;
            route.D = ReadPoint(reader, route.A);
            route.Takeoff = reader.ReadSingle(); route.Landing = reader.ReadSingle(); route.OrbitRadius = reader.ReadSingle();
            if (route.OrbitRadius > 0f) route.OrbitSeconds = reader.ReadSingle();
            return route;
        }
        public static void WriteBirdEvent(this Writer writer, BirdEvent message)
        {
            writer.WriteUInt32(message.Epoch); writer.WriteUInt32(message.Sequence); writer.WriteUInt8Unpacked((byte)message.Kind);
            var record = message.Record;
            if (message.Kind == BirdEventKind.Spawn) { writer.WriteBirdRecord(record); return; }
            writer.WriteUInt32(record.Life);
            if (message.Kind == BirdEventKind.Death)
            {
                writer.WriteUInt16(record.Species); writer.WriteVector3(message.Position); return;
            }
            writer.WriteUInt32(record.Revision); writer.WriteUInt32(record.Route.Revision);
            writer.WriteUInt16(record.Occupied); writer.WriteUInt16(record.Reserved);
            writer.WriteUInt8Unpacked((byte)record.Interrupt); writer.WriteUInt32(record.FleeAt);
            writer.WriteBoolean(record.HasNext);
            if (record.HasNext) writer.WriteBirdRoute(record.Next);
        }
        public static BirdEvent ReadBirdEvent(this Reader reader)
        {
            var message = new BirdEvent { Epoch = reader.ReadUInt32(), Sequence = reader.ReadUInt32(), Kind = (BirdEventKind)reader.ReadUInt8Unpacked() };
            if (message.Kind == BirdEventKind.Spawn) { message.Record = reader.ReadBirdRecord(); return message; }
            message.Record.Life = reader.ReadUInt32();
            if (message.Kind == BirdEventKind.Death)
            {
                message.Record.Species = reader.ReadUInt16(); message.Position = reader.ReadVector3(); return message;
            }
            message.Record.Revision = reader.ReadUInt32(); message.Record.Route.Revision = reader.ReadUInt32();
            message.Record.Occupied = reader.ReadUInt16(); message.Record.Reserved = reader.ReadUInt16();
            message.Record.Interrupt = (BirdInterrupt)reader.ReadUInt8Unpacked(); message.Record.FleeAt = reader.ReadUInt32();
            message.Record.HasNext = reader.ReadBoolean();
            if (message.Record.HasNext) message.Record.Next = reader.ReadBirdRoute();
            return message;
        }
        public static void WriteBirdHitReport(this Writer writer, BirdHitReport report)
        {
            writer.WriteUInt32(report.Epoch); writer.WriteUInt32(report.Source); writer.WriteBoolean(report.Cart);
            if (report.Cart) { writer.WriteUInt32(report.MotionEpoch); writer.WriteUInt32(report.SeatRevision); }
            else { writer.WriteUInt32(report.Player); writer.WriteUInt32(report.Operation); }
            writer.WriteUInt32((uint)report.Hits.Count);
            foreach (var hit in report.Hits)
            {
                writer.WriteUInt32(hit.Life); writer.WriteUInt32(hit.Contact);
                if (!report.Cart) writer.WriteSingle(hit.Speed);
                writer.WriteVector3(hit.Position);
            }
        }
        public static BirdHitReport ReadBirdHitReport(this Reader reader)
        {
            var report = new BirdHitReport { Epoch = reader.ReadUInt32(), Source = reader.ReadUInt32(), Cart = reader.ReadBoolean() };
            if (report.Cart) { report.MotionEpoch = reader.ReadUInt32(); report.SeatRevision = reader.ReadUInt32(); }
            else { report.Player = reader.ReadUInt32(); report.Operation = reader.ReadUInt32(); }
            int count = (int)reader.ReadUInt32();
            report.Hits = new List<BirdHit>(count);
            for (int i = 0; i < count; i++)
            {
                var hit = new BirdHit { Life = reader.ReadUInt32(), Contact = reader.ReadUInt32() };
                if (!report.Cart) hit.Speed = reader.ReadSingle();
                hit.Position = reader.ReadVector3(); report.Hits.Add(hit);
            }
            return report;
        }
        public static void WriteBirdRecord(this Writer writer, BirdRecord record)
        {
            writer.WriteUInt32(record.Life); writer.WriteUInt32(record.Revision);
            writer.WriteUInt16(record.Species); writer.WriteUInt16(record.Zone); writer.WriteUInt16(record.Biome);
            writer.WriteUInt16(record.Occupied); writer.WriteUInt16(record.Reserved);
            writer.WriteUInt8Unpacked((byte)record.Interrupt); writer.WriteUInt32(record.FleeAt);
            writer.WriteBirdRoute(record.Route); writer.WriteBoolean(record.HasNext);
            if (record.HasNext) writer.WriteBirdRoute(record.Next);
        }
        public static BirdRecord ReadBirdRecord(this Reader reader)
        {
            var record = new BirdRecord
            {
                Life = reader.ReadUInt32(), Revision = reader.ReadUInt32(), Species = reader.ReadUInt16(), Zone = reader.ReadUInt16(), Biome = reader.ReadUInt16(),
                Occupied = reader.ReadUInt16(), Reserved = reader.ReadUInt16(), Interrupt = (BirdInterrupt)reader.ReadUInt8Unpacked(), FleeAt = reader.ReadUInt32(),
                Route = reader.ReadBirdRoute(), HasNext = reader.ReadBoolean()
            };
            if (record.HasNext) record.Next = reader.ReadBirdRoute();
            return record;
        }
        private static void WritePoint(Writer writer, Vector3 point, Vector3 origin)
        {
            Vector3 offset = point - origin;
            bool full = Mathf.Abs(offset.x) > 327.6f || Mathf.Abs(offset.y) > 327.6f || Mathf.Abs(offset.z) > 327.6f;
            writer.WriteBoolean(full);
            if (full) { writer.WriteVector3(point); return; }
            writer.WriteInt16((short)Mathf.RoundToInt(offset.x * 100f));
            writer.WriteInt16((short)Mathf.RoundToInt(offset.y * 100f));
            writer.WriteInt16((short)Mathf.RoundToInt(offset.z * 100f));
        }
        private static Vector3 ReadPoint(Reader reader, Vector3 origin) => reader.ReadBoolean() ? reader.ReadVector3() :
            origin + new Vector3(reader.ReadInt16(), reader.ReadInt16(), reader.ReadInt16()) * 0.01f;
    }
}
