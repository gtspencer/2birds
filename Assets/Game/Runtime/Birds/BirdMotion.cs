using System;
using UnityEngine;

namespace TwoBirds
{
    public enum BirdMotionKind : byte { Hold, Curve, Orbit, Surface }

    public struct BirdRoute
    {
        public uint Revision, StartTick;
        public float Seconds;
        public BirdMotionKind Kind;
        public BirdActivity Activity;
        public Vector3 A, B, C, D;
        public float Facing, Takeoff, Landing;
        public float OrbitRadius, OrbitSeconds;
        public ushort Habitat, Perch;
        public Vector3[] Surface;
        public double End(double delta) => StartTick + Seconds / delta;
    }

    public struct BirdRecord
    {
        public uint Life, Revision;
        public ushort Species, Zone, Biome, Occupied, Reserved;
        public BirdRoute Route, Next;
        public bool HasNext;
        public BirdInterrupt Interrupt;
        public uint FleeAt;
    }

    public struct BirdPose
    {
        public Vector3 Position, Velocity;
        public Quaternion Rotation;
        public BirdActivity Activity;
    }

    public static class BirdMotion
    {
        public static BirdRoute Current(in BirdRecord record, double tick) =>
            record.HasNext && tick >= record.Next.StartTick ? record.Next : record.Route;

        public static BirdPose Evaluate(in BirdRoute route, double tick, double delta)
        {
            float seconds = Mathf.Max(0f, (float)((tick - route.StartTick) * delta));
            float t = route.Seconds > 0f ? Mathf.Clamp01(seconds / route.Seconds) : 1f;
            Vector3 position = route.A, velocity = Vector3.zero;
            switch (route.Kind)
            {
                case BirdMotionKind.Curve:
                    float u = 1f - t;
                    position = u * u * u * route.A + 3f * u * u * t * route.B + 3f * u * t * t * route.C + t * t * t * route.D;
                    if (t < 1f) velocity = 3f * (u * u * (route.B - route.A) + 2f * u * t * (route.C - route.B) + t * t * (route.D - route.C)) / route.Seconds;
                    else if (route.OrbitRadius > 0f)
                    {
                        Vector3 forward = Vector3.ProjectOnPlane(route.D - route.C, Vector3.up).normalized;
                        Vector3 radial = Vector3.Cross(forward, Vector3.up) * route.OrbitRadius;
                        float angle = (seconds - route.Seconds) * Mathf.PI * 2f / route.OrbitSeconds;
                        position = route.D - radial + radial * Mathf.Cos(angle) + forward * route.OrbitRadius * Mathf.Sin(angle);
                        velocity = (-radial * Mathf.Sin(angle) + forward * route.OrbitRadius * Mathf.Cos(angle)) * (Mathf.PI * 2f / route.OrbitSeconds);
                    }
                    break;
                case BirdMotionKind.Orbit:
                    float phase = seconds * Mathf.PI * 2f / route.Seconds;
                    position = route.A + route.B * Mathf.Cos(phase) + route.C * Mathf.Sin(phase);
                    velocity = (-route.B * Mathf.Sin(phase) + route.C * Mathf.Cos(phase)) * (Mathf.PI * 2f / route.Seconds);
                    break;
                case BirdMotionKind.Surface:
                    if (route.Surface == null || route.Surface.Length < 2) break;
                    float part = t * (route.Surface.Length - 1);
                    int index = Mathf.Min((int)part, route.Surface.Length - 2);
                    position = Vector3.Lerp(route.Surface[index], route.Surface[index + 1], part - index);
                    if (t < 1f) velocity = (route.Surface[index + 1] - route.Surface[index]) * (route.Surface.Length - 1) / route.Seconds;
                    break;
            }
            BirdActivity activity = route.Activity;
            if (route.Kind == BirdMotionKind.Curve)
                activity = route.OrbitRadius > 0f && t >= 1f ? BirdActivity.Soar :
                    seconds < route.Takeoff ? BirdActivity.Takeoff : seconds >= route.Seconds - route.Landing
                    ? t >= 1f ? BirdActivity.Idle : BirdActivity.Landing : route.Activity;
            if (route.Kind == BirdMotionKind.Surface && t >= 1f) activity = BirdActivity.Idle;
            Quaternion rotation = velocity.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(velocity.normalized, Vector3.up) : Quaternion.Euler(0f, route.Facing, 0f);
            return new BirdPose { Position = position, Velocity = velocity, Rotation = rotation, Activity = activity };
        }

        public static Vector3 Quantize(Vector3 point) => new(Mathf.Round(point.x * 100f) * 0.01f,
            Mathf.Round(point.y * 100f) * 0.01f, Mathf.Round(point.z * 100f) * 0.01f);

        public static void Quantize(ref BirdRoute route)
        {
            route.A = Quantize(route.A); route.B = Quantize(route.B); route.C = Quantize(route.C); route.D = Quantize(route.D);
            if (route.Surface != null)
                for (int i = 0; i < route.Surface.Length; i++) route.Surface[i] = Quantize(route.Surface[i]);
        }

        public static bool Sweep(Vector3 from, Vector3 to, Vector3 birdFrom, Vector3 birdTo, float radius, out float fraction)
        {
            Vector3 origin = from - birdFrom, travel = to - from - (birdTo - birdFrom);
            float c = origin.sqrMagnitude - radius * radius;
            fraction = 0f;
            if (c <= 0f) return true;
            float a = travel.sqrMagnitude, b = Vector3.Dot(origin, travel);
            float discriminant = b * b - a * c;
            if (a < 0.0000001f || b >= 0f || discriminant < 0f) return false;
            fraction = (-b - Mathf.Sqrt(discriminant)) / a;
            return fraction <= 1f;
        }

        public static bool SweepBox(Vector3 from, Vector3 to, Vector3 half, float radius, out float fraction)
        {
            Span<float> boundaries = stackalloc float[8];
            boundaries[0] = 0f; boundaries[1] = 1f; int count = 2;
            Vector3 travel = to - from;
            for (int axis = 0; axis < 3; axis++)
            {
                if (Mathf.Abs(travel[axis]) < 0.000001f) continue;
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    float crossing = (half[axis] * sign - from[axis]) / travel[axis];
                    if (crossing > 0f && crossing < 1f) boundaries[count++] = crossing;
                }
            }
            for (int i = 1; i < count; i++)
            {
                float value = boundaries[i]; int j = i - 1;
                while (j >= 0 && boundaries[j] > value) { boundaries[j + 1] = boundaries[j]; j--; }
                boundaries[j + 1] = value;
            }
            for (int i = 0; i < count - 1; i++)
            {
                float lo = boundaries[i], hi = boundaries[i + 1], middle = (lo + hi) * 0.5f;
                float a = 0f, b = 0f, c = -radius * radius;
                for (int axis = 0; axis < 3; axis++)
                {
                    float sample = from[axis] + travel[axis] * middle;
                    if (Mathf.Abs(sample) <= half[axis]) continue;
                    float offset = from[axis] - Mathf.Sign(sample) * half[axis];
                    a += travel[axis] * travel[axis]; b += offset * travel[axis]; c += offset * offset;
                }
                if ((a * lo + 2f * b) * lo + c <= 0f) { fraction = lo; return true; }
                float discriminant = b * b - a * c;
                if (a <= 0f || discriminant < 0f) continue;
                float root = (-b - Mathf.Sqrt(discriminant)) / a;
                if (root >= lo && root <= hi) { fraction = root; return true; }
            }
            fraction = 0f; return false;
        }
    }

    internal sealed class BirdClock
    {
        private double seconds, sampledAt;
        private bool initialized;
        public double Read(double synchronizedSeconds)
        {
            double now = Time.unscaledTimeAsDouble;
            if (!initialized) { seconds = synchronizedSeconds; initialized = true; }
            else
            {
                double elapsed = now - sampledAt;
                double predicted = seconds + elapsed;
                double error = synchronizedSeconds - predicted;
                seconds = Math.Max(seconds, predicted + Math.Clamp(error, -elapsed * 0.1d, elapsed * 0.1d));
            }
            sampledAt = now;
            return seconds;
        }
        public void Reset() => initialized = false;
    }
}
