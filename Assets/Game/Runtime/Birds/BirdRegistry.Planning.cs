using System;
using UnityEngine;
using Random = UnityEngine.Random;

namespace TwoBirds
{
    public sealed partial class BirdRegistry
    {
        private bool TrySpawn(Vacancy vacancy, double now)
        {
            var zone = zones[vacancy.Zone]; var bird = species[vacancy.Species];
            Vector3 point;
            ushort perch = 0, habitat = 0;
            BirdRoute route;
            var seed = new BirdRecord { Species = bird.Id, Zone = zone.Id, Biome = zone.Biome };
            if (bird.Behavior == BirdBehavior.Bush || bird.Behavior == BirdBehavior.Soaring && Random.value < 0.5f)
            {
                if (!ChoosePerch(seed, bird, zone, Vector3.zero, false, out var destination)) return false;
                point = destination.Position(bird); perch = destination.Id;
                route = Hold(point, destination.transform.eulerAngles.y, now);
            }
            else
            {
                var kind = bird.Behavior == BirdBehavior.Water ? BirdHabitatKind.Water : bird.Behavior == BirdBehavior.Ground ? BirdHabitatKind.Ground : BirdHabitatKind.Air;
                if (!ChooseHabitat(seed.Biome, kind, out var volume)) return false;
                point = volume.Sample(); habitat = volume.Id;
                if (kind == BirdHabitatKind.Ground && !GroundPoint(point, bird, out point)) return false;
                if (!zone.Contains(point) || !volume.Contains(point)) return false;
                route = Hold(point, Random.Range(0f, 360f), now); route.Habitat = habitat;
                if (kind == BirdHabitatKind.Air && !Orbit(seed, bird, point, Vector3.zero, now, volume, out route)) return false;
            }
            if (!ClearPoint(point, bird.Radius)) return false;
            foreach (uint life in lives)
            {
                var other = records[life];
                Vector3 position = BirdMotion.Evaluate(BirdMotion.Current(other, now), now, Delta).Position;
                float spacing = bird.Radius + species[other.Species].Radius;
                if ((point - position).sqrMagnitude < spacing * spacing * 4f) return false;
            }
            if (nextLife == uint.MaxValue) { SessionController.Instance.Leave("Bird life ID capacity exhausted."); return false; }
            seed.Life = ++nextLife; seed.Revision = 1; seed.Occupied = perch;
            route.Revision = 1; BirdMotion.Quantize(ref route); seed.Route = route;
            records.Add(seed.Life, seed); lives.Add(seed.Life);
            if (perch != 0) claims.Add(perch, (seed.Life, 1));
            decisions[seed.Life] = now + (route.Kind == BirdMotionKind.Orbit ? route.Seconds : Random.Range(bird.IdleSeconds.x, bird.IdleSeconds.y)) / Delta;
            Publish(BirdEventKind.Spawn, seed);
            return true;
        }

        private BirdRoute Hold(Vector3 point, float facing, double tick) => new()
        {
            Kind = BirdMotionKind.Hold, Activity = BirdActivity.Idle, A = BirdMotion.Quantize(point), D = BirdMotion.Quantize(point),
            Facing = facing, StartTick = (uint)Math.Ceiling(tick)
        };

        private void PlanNormal(uint life, double now)
        {
            var record = records[life];
            if (record.HasNext || record.Interrupt != BirdInterrupt.Calm)
            { decisions[life] = now + settings.RetrySeconds / Delta; return; }
            double start = Math.Max(now + 0.15d / Delta, record.Route.Kind == BirdMotionKind.Orbit ? now : record.Route.End(Delta));
            if (!BuildRoute(record, start, false, Vector3.zero, out var route))
            { decisions[life] = now + settings.RetrySeconds / Delta; return; }
            QueueRoute(ref record, route);
            records[life] = record;
            var bird = species[record.Species];
            decisions[life] = route.End(Delta) + (route.Kind == BirdMotionKind.Orbit ? -0.15f : route.OrbitRadius > 0f ? route.OrbitSeconds : Random.Range(bird.IdleSeconds.x, bird.IdleSeconds.y)) / Delta;
            Publish(BirdEventKind.Plan, record);
        }

        private void QueueRoute(ref BirdRecord record, BirdRoute route)
        {
            if (record.Reserved != record.Route.Perch) ReleaseClaim(record.Reserved, record.Life, record.Revision);
            record.Revision++;
            route.Revision = record.Revision;
            BirdMotion.Quantize(ref route);
            record.Next = route; record.HasNext = true; record.Reserved = route.Perch;
            if (route.Perch != 0) claims[route.Perch] = (record.Life, route.Revision);
        }

        private bool BuildRoute(BirdRecord record, double start, bool flee, Vector3 threat, out BirdRoute route)
        {
            start = Math.Ceiling(start);
            var bird = species[record.Species];
            var pose = BirdMotion.Evaluate(record.Route, start, Delta);
            return bird.Behavior switch
            {
                BirdBehavior.Bush => BushRoute(record, bird, pose, start, flee, threat, out route),
                BirdBehavior.Soaring => SoaringRoute(record, bird, pose, start, flee, threat, out route),
                BirdBehavior.Water => WaterRoute(record, bird, pose, start, flee, threat, out route),
                _ => GroundRoute(record, bird, pose, start, flee, threat, out route)
            };
        }

        private bool BushRoute(BirdRecord record, BirdSpecies bird, BirdPose pose, double start, bool flee, Vector3 threat, out BirdRoute route)
        {
            route = default;
            if (!bird.Supports(BirdCapabilities.Flight) || !ChoosePerch(record, bird, null, threat, flee, out var perch)) return false;
            route = Curve(pose, perch.Position(bird), bird.FlightSpeed, bird.FlightHeight, start);
            route.Perch = perch.Id; route.Facing = perch.transform.eulerAngles.y;
            return ClearRoute(route, bird.Radius);
        }

        private bool SoaringRoute(BirdRecord record, BirdSpecies bird, BirdPose pose, double start, bool flee, Vector3 threat, out BirdRoute route)
        {
            route = default;
            if (!bird.Supports(BirdCapabilities.Flight)) return false;
            if ((record.Route.Kind == BirdMotionKind.Orbit || record.Route.OrbitRadius > 0f) && !flee && BushRoute(record, bird, pose, start, false, threat, out route)) return true;
            if (!ChooseHabitat(record.Biome, BirdHabitatKind.Air, out var air)) return false;
            if (air.Contains(pose.Position) && record.Occupied == 0 && !flee) return Orbit(record, bird, pose.Position, pose.Velocity, start, air, out route);
            Vector3 end = air.Sample();
            if (flee && Vector3.Dot(end - pose.Position, pose.Position - threat) <= 0f) return false;
            route = Curve(pose, end, bird.FlightSpeed, bird.FlightHeight, start);
            route.Habitat = air.Id; route.Landing = 0f;
            if (!ClearRoute(route, bird.Radius)) return false;
            Vector3 tangent = Vector3.ProjectOnPlane(route.D - route.C, Vector3.up).normalized;
            if (!Orbit(record, bird, route.D, tangent, route.End(Delta), air, out var orbit)) return false;
            route.OrbitRadius = orbit.B.magnitude; route.OrbitSeconds = orbit.Seconds;
            return true;
        }

        private bool WaterRoute(BirdRecord record, BirdSpecies bird, BirdPose pose, double start, bool flee, Vector3 threat, out BirdRoute route)
        {
            route = default;
            if (!bird.Supports(BirdCapabilities.Swim)) return false;
            BirdHabitatVolume water = null;
            if (record.Route.Habitat != 0) habitats.TryGetValue(record.Route.Habitat, out water);
            if (!water || water.Kind != BirdHabitatKind.Water)
                if (!ChooseHabitat(record.Biome, BirdHabitatKind.Water, out water)) return false;
            Vector3 end = water.Sample();
            Vector3 direction = Vector3.ProjectOnPlane(end - pose.Position, Vector3.up);
            float max = bird.SwimSpeed * Random.Range(bird.TravelSeconds.x, bird.TravelSeconds.y);
            if (direction.magnitude > max) end = pose.Position + direction.normalized * max;
            end.y = water.WaterHeight;
            if (flee && Vector3.Dot(end - pose.Position, pose.Position - threat) < 0f) return false;
            bool fly = bird.Supports(BirdCapabilities.Flight) && (flee || record.Route.Activity == BirdActivity.Swim);
            route = fly ? Curve(pose, end, bird.FlightSpeed, bird.FlightHeight, start) : Curve(pose, end, bird.SwimSpeed, 0f, start);
            if (!fly) { route.Activity = BirdActivity.Swim; route.Takeoff = route.Landing = 0f; }
            route.Habitat = water.Id;
            if (!water.Contains(end)) return false;
            return ClearRoute(route, bird.Radius);
        }

        private bool GroundRoute(BirdRecord record, BirdSpecies bird, BirdPose pose, double start, bool flee, Vector3 threat, out BirdRoute route)
        {
            route = default;
            if (!bird.Supports(BirdCapabilities.Ground) || !ChooseHabitat(record.Biome, BirdHabitatKind.Ground, out var ground)) return false;
            Vector3 direction = flee ? Vector3.ProjectOnPlane(pose.Position - threat, Vector3.up).normalized : Random.insideUnitSphere;
            direction.y = 0f;
            float distance = bird.GroundSpeed * Random.Range(bird.TravelSeconds.x, bird.TravelSeconds.y);
            bool hop = flee && bird.Supports(BirdCapabilities.ShortFlight);
            if (hop) distance = Mathf.Min(distance, bird.ShortFlightDistance);
            Vector3 end = pose.Position + direction.normalized * distance;
            if (!ground.Contains(end) || !GroundPoint(end, bird, out end)) return false;
            if (hop)
            {
                route = Curve(pose, end, bird.FlightSpeed, Mathf.Min(1f, bird.FlightHeight), start);
                route.Habitat = ground.Id;
                return ClearRoute(route, bird.Radius);
            }
            var points = new Vector3[9]; points[0] = pose.Position;
            for (int i = 1; i < points.Length; i++)
            {
                Vector3 candidate = Vector3.Lerp(pose.Position, end, i / 8f);
                if (!ground.Contains(candidate) || !GroundPoint(candidate, bird, out points[i]) ||
                    Mathf.Abs(points[i].y - points[i - 1].y) > settings.GroundStep) return false;
                Vector3 travel = points[i] - points[i - 1];
                if (Physics.SphereCast(points[i - 1], bird.Radius, travel.normalized, out _, travel.magnitude,
                    settings.SolidMask, QueryTriggerInteraction.Ignore)) return false;
            }
            route = new BirdRoute { StartTick = (uint)start, Seconds = Mathf.Max(0.2f, Vector3.Distance(pose.Position, end) / bird.GroundSpeed),
                Kind = BirdMotionKind.Surface, Activity = BirdActivity.GroundMove, A = points[0], D = end, Surface = points,
                Facing = Quaternion.LookRotation(direction).eulerAngles.y, Habitat = ground.Id };
            BirdMotion.Quantize(ref route);
            return true;
        }

        private BirdRoute Curve(BirdPose pose, Vector3 end, float speed, float height, double start)
        {
            Vector3 travel = end - pose.Position;
            float seconds = Mathf.Max(0.3f, (travel.magnitude + height) / speed);
            Vector3 tangent = pose.Velocity.sqrMagnitude > 0.01f ? pose.Velocity * (seconds / 3f) : travel / 3f + Vector3.up * height;
            Vector3 variation = Vector3.Cross(travel.normalized, Vector3.up) * Random.Range(-height * 0.25f, height * 0.25f);
            var route = new BirdRoute { StartTick = (uint)start, Seconds = seconds, Kind = BirdMotionKind.Curve,
                Activity = BirdActivity.Flight, A = pose.Position, B = pose.Position + tangent,
                C = end - travel / 3f + Vector3.up * height + variation, D = end, Takeoff = Mathf.Min(0.3f, seconds * 0.2f),
                Landing = Mathf.Min(0.4f, seconds * 0.2f), Facing = travel.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(travel).eulerAngles.y : pose.Rotation.eulerAngles.y };
            BirdMotion.Quantize(ref route);
            return route;
        }

        private bool Orbit(BirdRecord record, BirdSpecies bird, Vector3 position, Vector3 velocity, double start, BirdHabitatVolume air, out BirdRoute route)
        {
            float radius = Mathf.Max(2f, bird.FlightSpeed * Random.Range(bird.TravelSeconds.x, bird.TravelSeconds.y) / (2f * Mathf.PI));
            Vector3 forward = velocity.sqrMagnitude > 0.01f ? Vector3.ProjectOnPlane(velocity, Vector3.up).normalized : Vector3.forward;
            Vector3 radial = Vector3.Cross(forward, Vector3.up) * radius;
            route = new BirdRoute { StartTick = (uint)Math.Ceiling(start), Seconds = 2f * Mathf.PI * radius / bird.FlightSpeed,
                Kind = BirdMotionKind.Orbit, Activity = BirdActivity.Soar, A = position - radial, B = radial, C = forward * radius, Habitat = air.Id };
            BirdMotion.Quantize(ref route);
            for (int i = 0; i < 12; i++)
                if (!air.Contains(BirdMotion.Evaluate(route, start + route.Seconds / Delta * i / 12d, Delta).Position)) return false;
            return ClearRoute(route, bird.Radius);
        }

        private bool ClearRoute(BirdRoute route, float radius)
        {
            const int segments = 12;
            float deviation = route.Kind == BirdMotionKind.Orbit ? route.B.magnitude * (1f - Mathf.Cos(Mathf.PI / segments)) :
                6f * Mathf.Max((route.C - 2f * route.B + route.A).magnitude, (route.D - 2f * route.C + route.B).magnitude) / (8f * segments * segments);
            Vector3 from = BirdMotion.Evaluate(route, route.StartTick, Delta).Position;
            for (int i = 1; i <= segments; i++)
            {
                Vector3 to = BirdMotion.Evaluate(route, route.StartTick + route.Seconds / Delta * i / segments, Delta).Position;
                Vector3 travel = to - from;
                if (Physics.SphereCast(from, radius + deviation + 0.01f, travel.normalized, out _, travel.magnitude,
                    settings.SolidMask, QueryTriggerInteraction.Ignore)) return false;
                from = to;
            }
            return ClearPoint(from, radius);
        }

        private bool ClearPoint(Vector3 point, float radius) => !Physics.CheckSphere(point, radius, settings.SolidMask, QueryTriggerInteraction.Ignore);

        private bool GroundPoint(Vector3 point, BirdSpecies bird, out Vector3 supported)
        {
            supported = point;
            if (!Physics.Raycast(point + Vector3.up * 2f, Vector3.down, out var hit, 4f, settings.GroundMask, QueryTriggerInteraction.Ignore) ||
                Vector3.Angle(hit.normal, Vector3.up) > settings.GroundSlope) return false;
            supported = hit.point + Vector3.up * Mathf.Max(bird.LandingOffset, bird.Radius + 0.02f);
            return true;
        }

        private bool ChoosePerch(BirdRecord record, BirdSpecies bird, BirdSpawnZone zone, Vector3 threat, bool flee, out BirdPerch selected)
        {
            selected = null; int count = 0;
            Vector3 from = BirdMotion.Evaluate(record.Route, Now, Delta).Position;
            for (int kind = 0; kind < 2; kind++)
            {
                var perchKind = (BirdPerchKind)kind;
                if (!bird.Supports(perchKind == BirdPerchKind.Tree ? BirdCapabilities.Tree : BirdCapabilities.Ground) ||
                    !perchGroups.TryGetValue((record.Biome, perchKind), out var group)) continue;
                foreach (var perch in group)
                {
                    if (perch.Clearance < bird.Radius || claims.ContainsKey(perch.Id) ||
                        zone && !zone.Contains(perch.Position(bird)) || flee && Vector3.Dot(perch.Position(bird) - from, from - threat) <= 0f) continue;
                    if (Random.Range(0, ++count) == 0) selected = perch;
                }
            }
            return selected;
        }

        private bool ChooseHabitat(ushort biome, BirdHabitatKind kind, out BirdHabitatVolume selected)
        {
            selected = habitatGroups.TryGetValue((biome, kind), out var group) ? group[Random.Range(0, group.Count)] : null;
            return selected;
        }
    }
}
