using System.Collections.Generic;
using UnityEngine;

namespace TwoBirds
{
    public sealed partial class BirdRegistry
    {
        private readonly Dictionary<uint, BirdView> views = new();
        private readonly Dictionary<ushort, Stack<BirdView>> viewPools = new();
        private readonly Dictionary<ushort, Stack<BirdBody>> bodyPools = new();
        private readonly Dictionary<uint, ushort> viewSpecies = new();
        private readonly List<BirdBody> bodies = new();
        private readonly HashSet<uint> deathEffects = new();
        private readonly Queue<uint> effectHistory = new();
        private int pooledViews, pooledBodies;
        internal Dictionary<uint, BirdRecord> LiveRecords => records;
        internal BirdSpecies Species(ushort id) => species[id];
        internal float MaximumScareRadius { get; private set; }

        internal bool TryGetSplatView(uint life, out BirdView view) => views.TryGetValue(life, out view) && view;
        internal bool TryGetSplatBird(Collider collider, out uint life, out BirdView view)
        {
            view = null;
            if (!shapeLives.TryGetValue(collider.GetEntityId(), out life)) return false;
            views.TryGetValue(life, out view);
            return true;
        }

        private void Present(double now)
        {
            var player = SessionController.Instance.LocalPlayer;
            Vector3 origin = player ? player.transform.position : Vector3.zero;
            int animated = 0, audible = 0;
            foreach (uint life in lives)
            {
                var record = records[life];
                var pose = BirdMotion.Evaluate(BirdMotion.Current(record, now), now, Delta);
                float distance = (pose.Position - origin).sqrMagnitude;
                bool show = distance < settings.ViewDistance * settings.ViewDistance && !hitReporter.Predicted(life);
                if (!show) { ReturnView(life); continue; }
                if (!views.TryGetValue(life, out var view))
                {
                    var bird = species[record.Species];
                    if (!bird.ViewPrefab) continue;
                    if (viewPools.TryGetValue(bird.Id, out var pool) && pool.Count > 0) { view = pool.Pop(); pooledViews--; }
                    else view = Instantiate(bird.ViewPrefab, transform);
                    view.Rent(bird, settings); views.Add(life, view); viewSpecies.Add(life, bird.Id);
                }
                bool animate = distance < settings.AnimationDistance * settings.AnimationDistance && animated++ < settings.AnimatedLimit;
                bool hear = distance < settings.AudioDistance * settings.AudioDistance && audible++ < settings.SoundLimit;
                view.Present(pose, true, animate, hear);
                if (!Host && record.Interrupt == BirdInterrupt.Calm && !record.HasNext && record.Route.Kind != BirdMotionKind.Hold && record.Route.Kind != BirdMotionKind.Orbit &&
                    record.Route.OrbitRadius == 0f && now > record.Route.End(Delta) + settings.RetrySeconds / Delta) RequestExpired(record);
            }
            for (int i = bodies.Count - 1; i >= 0; i--)
                if (bodies[i].Step(this)) ReturnBody(i);
        }

        private void ReturnView(uint life)
        {
            if (!views.Remove(life, out var view)) return;
            SplatTargetLifetime.Invalidate(view.transform);
            ushort id = viewSpecies[life]; viewSpecies.Remove(life); view.Return();
            if (pooledViews >= 64) { Destroy(view.gameObject); return; }
            if (!viewPools.TryGetValue(id, out var pool)) viewPools.Add(id, pool = new Stack<BirdView>());
            pool.Push(view); pooledViews++;
        }

        internal void Alert(uint life) { if (views.TryGetValue(life, out var view)) view.Alert(); }
        internal Vector3 PresentationOffset(uint life) => views.TryGetValue(life, out var view) ? view.Correction : Vector3.zero;
        internal void PredictDeath(uint life, ushort speciesId, Vector3 position) => ShowDeath(life, speciesId, position);
        private void ShowDeath(uint life, ushort speciesId, Vector3 position)
        {
            if (!deathEffects.Add(life)) return;
            effectHistory.Enqueue(life);
            while (effectHistory.Count > 1024) deathEffects.Remove(effectHistory.Dequeue());
            var player = SessionController.Instance.LocalPlayer;
            Vector3 origin = player ? player.transform.position : Vector3.zero;
            float distance = (position - origin).sqrMagnitude;
            if (distance >= settings.ViewDistance * settings.ViewDistance) { ReturnView(life); return; }
            var bird = species[speciesId];
            if (bird.BodyPrefab)
            {
                if (bodies.Count >= settings.BodyLimit)
                {
                    int farthest = -1;
                    float farthestDistance = distance;
                    for (int i = 0; i < bodies.Count; i++)
                    {
                        float bodyDistance = (bodies[i].transform.position - origin).sqrMagnitude;
                        if (bodyDistance <= farthestDistance) continue;
                        farthest = i; farthestDistance = bodyDistance;
                    }
                    if (farthest < 0) { ReturnView(life); return; }
                    ReturnBody(farthest);
                }
                BirdBody body;
                if (bodyPools.TryGetValue(speciesId, out var pool) && pool.Count > 0) { body = pool.Pop(); pooledBodies--; }
                else body = Instantiate(bird.BodyPrefab, transform);
                views.TryGetValue(life, out var view);
                body.Rent(life, bird, settings, view, position, predictionManager); bodies.Add(body);
            }
            ReturnView(life);
        }
        internal void RestorePrediction(uint life)
        {
            deathEffects.Remove(life);
            for (int i = bodies.Count - 1; i >= 0; i--) if (bodies[i].Life == life) ReturnBody(i);
        }
        private void ReturnBody(int index)
        {
            var body = bodies[index]; bodies.RemoveAt(index); body.Return();
            if (pooledBodies >= settings.BodyLimit) { Destroy(body.gameObject); return; }
            if (!bodyPools.TryGetValue(body.Species, out var pool)) bodyPools.Add(body.Species, pool = new Stack<BirdBody>());
            pool.Push(body); pooledBodies++;
        }
        internal bool WaterAt(Vector3 point, out float surface)
        {
            foreach (var habitat in waterHabitats)
                if (point.y <= habitat.WaterHeight && habitat.Contains(point))
                { surface = habitat.WaterHeight; return true; }
            surface = 0f; return false;
        }
        internal bool WaterCrossing(Vector3 from, Vector3 to, out float surface)
        {
            if (WaterAt(to, out surface)) return true;
            foreach (var habitat in waterHabitats)
            {
                if (from.y < habitat.WaterHeight || to.y > habitat.WaterHeight) continue;
                float amount = Mathf.InverseLerp(from.y, to.y, habitat.WaterHeight);
                Vector3 crossing = Vector3.Lerp(from, to, amount);
                if (!habitat.Contains(crossing)) continue;
                surface = habitat.WaterHeight; return true;
            }
            return false;
        }
        private void ClearPresentation()
        {
            foreach (var view in views.Values) if (view) Destroy(view.gameObject);
            foreach (var pool in viewPools.Values) foreach (var view in pool) if (view) Destroy(view.gameObject);
            foreach (var body in bodies) if (body) { body.Return(); Destroy(body.gameObject); }
            foreach (var pool in bodyPools.Values) foreach (var body in pool) if (body) Destroy(body.gameObject);
            views.Clear(); viewSpecies.Clear(); viewPools.Clear(); bodies.Clear(); bodyPools.Clear(); deathEffects.Clear(); effectHistory.Clear();
            pooledViews = pooledBodies = 0;
        }
        public void Honk(GolfCartNetwork cart)
        {
            if (active && ready && !Replaying) hitReporter.Threat(BirdThreatKind.Horn, (uint)cart.ObjectId, cart.transform.position, settings.HornRadius, Now, true);
        }
        internal void CartBefore(GolfCartNetwork cart)
        {
            if (!active || !ready || Replaying || !cart.ReportsWorldEffects) return;
            if (Host && !cartDrivers.ContainsKey((cart.ObjectId, cart.Epoch, cart.StateRevision))) RememberCart(cart);
            hitReporter.CartBefore(cart);
        }
        internal void CartAfter(GolfCartNetwork cart) { if (active && ready && !Replaying && cart.ReportsWorldEffects) hitReporter.CartAfter(cart); }
    }
}
