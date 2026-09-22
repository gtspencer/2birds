using UnityEngine;

namespace TwoBirds
{
    internal sealed class ItemCartPhysics
    {
        private readonly WorldItem item;
        private readonly WorldItemRegistry registry;
        private readonly ItemMotion[] history = new ItemMotion[128];
        private ItemMotion saved;
        private bool running, replaying;

        internal ItemCartPhysics(WorldItem item, WorldItemRegistry registry)
        { this.item = item; this.registry = registry; }

        internal void Start()
        {
            if (running) return;
            running = true;
            registry.PredictionManager.OnPreReconcile += BeforeReconcile;
            registry.PredictionManager.OnPreReplicateReplay += BeforeReplay;
            registry.PredictionManager.OnPostReconcile += AfterReconcile;
        }

        internal void Stop()
        {
            if (!running) return;
            running = replaying = false;
            registry.PredictionManager.OnPreReconcile -= BeforeReconcile;
            registry.PredictionManager.OnPreReplicateReplay -= BeforeReplay;
            registry.PredictionManager.OnPostReconcile -= AfterReconcile;
            System.Array.Clear(history, 0, history.Length);
        }

        private ItemMotion Capture() => RigidbodyMotionState.Capture(item.Body, item.Record.Motion,
            registry.LocalTick, false, Vector3.zero);

        internal void BeforePhysics()
        {
            if (item.ReleaseAvailable && item.MotionAvailable)
                history[registry.LocalTick % (uint)history.Length] = Capture();
        }

        private void BeforeReconcile(uint clientTick, uint serverTick)
        {
            replaying = item.ReleaseAvailable && item.MotionAvailable;
            if (replaying) saved = Capture();
        }

        private void BeforeReplay(uint clientTick, uint serverTick)
        {
            if (!replaying) return;
            var motion = history[clientTick % (uint)history.Length];
            Restore(motion.Id == saved.Id && motion.Tick == clientTick ? motion : saved);
        }

        private void AfterReconcile(uint clientTick, uint serverTick)
        {
            if (!replaying) return;
            Restore(saved);
            replaying = false;
        }

        private void Restore(ItemMotion motion)
        {
            RigidbodyMotionState.Apply(item.Body, motion, Vector3.zero);
            if (motion.Sleeping) item.Body.Sleep();
        }

        internal void Contact(Collision collision, bool entering = true)
        {
            if (!item.ReleaseAvailable || registry.Replaying || !collision.rigidbody ||
                !GolfCartNetwork.Bodies.TryGetValue(collision.rigidbody, out var cart) || !cart.ReportsWorldEffects ||
                !entering && collision.impulse.sqrMagnitude <= 0.000001f) return;
            if (entering) item.MotionBoundary = true;
            registry.ReportCartContact(item);
        }
    }
}
