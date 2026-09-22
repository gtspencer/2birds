using System.Collections.Generic;
using FishNet.Component.Prediction;
using UnityEngine;

namespace TwoBirds
{
    [RequireComponent(typeof(Rigidbody), typeof(SphereCollider), typeof(OfflineRigidbody))]
    public sealed class PebbleProjectile : MonoBehaviour
    {
        [SerializeField] private Transform visualRoot;
        private readonly RigidbodyMotionState motion = new();
        private readonly HashSet<Collider> touching = new();
        private readonly List<Collider> separated = new();
        private readonly List<Collider> shooterColliders = new();
        private readonly List<(Collider Target, ItemContactPhysics.Contact Contact)> impacts = new();
        private readonly RaycastHit[] crossings = new RaycastHit[32];
        private Collider[] overlaps = new Collider[32];
        private Rigidbody body;
        private OfflineRigidbody offline;
        private SphereCollider sphere;
        private ItemPlayerContact playerContact;
        private WorldItemRegistry world;
        private PebbleRegistry registry;
        private SlingshotDefinition definition;
        private Vector3 incoming, stepStart;
        private double presentedTick;
        private uint? boundaryTick;
        private ItemMotion? terminalMotion;
        private float radius, nextThreat;
        private bool ended, simulating;
        internal PebbleRecord Record { get; private set; }
        internal bool Simulating => simulating;
        internal bool Ended => ended;
        internal Vector3 PresentedPosition => visualRoot.position;
        public float Radius => sphere ? radius : GetComponent<SphereCollider>().radius * transform.lossyScale.x;

        private void Awake()
        {
            body = GetComponent<Rigidbody>();
            sphere = GetComponent<SphereCollider>();
            offline = GetComponent<OfflineRigidbody>();
            radius = sphere.radius * Mathf.Max(transform.lossyScale.x, transform.lossyScale.y, transform.lossyScale.z);
            sphere.hasModifiableContacts = true;
            sphere.includeLayers = LayerMask.GetMask("PlayerItemHitbox", "BirdQuery", "GolfCart", "ItemWorld");
            sphere.excludeLayers = LayerMask.GetMask("ItemHeld", "PlayerEffectReceiver", "PotionEffect");
            body.interpolation = RigidbodyInterpolation.None;
            body.sleepThreshold = 0f;
            body.linearDamping = body.angularDamping = 0f;
        }

        internal void Initialize(PebbleRegistry owner, WorldItemRegistry world, SlingshotDefinition definition,
            PebbleRecord record, bool simulate, Vector3 departure, bool rebase)
        {
            registry = owner; this.world = world; this.definition = definition;
            playerContact ??= new ItemPlayerContact(world, motion, value => value.Position, ReportImpact);
            Record = record;
            ended = false;
            simulating = simulate;
            body.collisionDetectionMode = CollisionDetectionMode.Discrete;
            body.rotation = Quaternion.identity;
            body.isKinematic = false;
            body.angularVelocity = Vector3.zero;
            body.isKinematic = !simulate;
            body.useGravity = true;
            sphere.enabled = simulate;
            if (simulate)
            {
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                ItemContactPhysics.Register(sphere, definition.ReboundRetention);
            }
            offline.SetPredictionManager(simulate ? world.PredictionManager : null);
            RigidbodyMotionState.Apply(body, record.Motion, Vector3.zero);
            motion.Reset(); motion.Add(record.Motion);
            motion.Departure(departure, world.CorrectionDuration);
            presentedTick = record.Motion.Tick;
            playerContact.Reset();
            if (!record.ShooterCleared && world.TryGetPlayer(record.Shooter, out var shooter))
            {
                shooterColliders.AddRange(shooter.GetComponentsInChildren<Collider>(true));
                if (!shooterColliders.Contains(shooter.Hitbox.Collider)) shooterColliders.Add(shooter.Hitbox.Collider);
                foreach (var collider in shooterColliders) if (collider) Physics.IgnoreCollision(sphere, collider, true);
            }
            if (record.Touching) SeedContacts();
            playerContact.Rebase = rebase || record.Motion.Path != 0;
            SamplePlayer(world.LocalInventory ? world.LocalInventory.Hitbox : null);
        }

        private ItemContactFrame Frame => new() { Eligible = !ended, Simulating = simulating,
            BodyCenter = body.position, PresentedCenter = visualRoot.position, Radius = radius,
            IgnoredPlayer = !Record.ShooterCleared && world.TryGetPlayer(Record.Shooter, out var shooter) ? shooter.Hitbox.Collider : null,
            PresentedTick = presentedTick };

        internal void Associate(PebbleRecord record)
        {
            var current = Record;
            current.Motion.Id = record.Motion.Id;
            current.BirdPlayer = record.BirdPlayer;
            current.Simulator = record.Simulator;
            Record = current;
        }

        internal ItemMotion Capture(uint tick)
        {
            var sample = RigidbodyMotionState.Capture(body, Record.Motion, tick, true, body.position);
            sample.RotationOmitted = true;
            sample.Rotation = Quaternion.identity;
            sample.AngularVelocity = Vector3.zero;
            return sample;
        }
        internal void SetRecord(PebbleRecord record) => Record = record;

        internal void Receive(ItemMotion next)
        {
            if (simulating || ended) return;
            var record = Record; record.Motion = next; Record = record;
            motion.Add(next);
        }

        internal void ContactState(PebbleRecord next)
        {
            var record = Record;
            record.ShooterCleared = next.ShooterCleared; record.Touching = next.Touching;
            Record = record;
            if (next.ShooterCleared) ClearShooter();
            if (next.Motion.Sequence > Record.Motion.Sequence) Receive(next.Motion);
        }

        internal void Boundary(PebbleRecord next, bool terminal)
        {
            if (simulating || ended) return;
            if (next.Motion.Sequence > Record.Motion.Sequence) motion.Add(next.Motion);
            if (presentedTick > next.Motion.Tick)
            {
                playerContact.Reposition(next.Motion.Position, next.Motion.Position);
                RigidbodyMotionState.Apply(body, next.Motion, Vector3.zero);
                visualRoot.localPosition = Vector3.zero;
                presentedTick = next.Motion.Tick;
                SamplePlayer(world.LocalInventory ? world.LocalInventory.Hitbox : null);
                motion.Count = 0; motion.Add(next.Motion);
                motion.Departure(Vector3.zero, world.CorrectionDuration);
                boundaryTick = null;
                if (terminal) ended = true;
            }
            else boundaryTick ??= next.Motion.Tick;
            var record = next;
            if (Record.Motion.Sequence > next.Motion.Sequence) record.Motion = Record.Motion;
            Record = record;
            if (terminal) terminalMotion = next.Motion;
        }

        internal void BeforePhysics()
        {
            if (ended) return;
            UpdateShooter();
            if (!simulating) return;
            incoming = body.linearVelocity;
            stepStart = body.position;
            impacts.Clear();
            separated.Clear();
            int count = touching.Count > 0 ? QueryOverlaps(radius + 0.002f) : 0;
            foreach (var collider in touching)
                if (!collider || !collider.enabled || System.Array.IndexOf(overlaps, collider, 0, count) < 0) separated.Add(collider);
            foreach (var collider in separated) touching.Remove(collider);
            bool contactEnded = Record.Touching && touching.Count == 0;
            var record = Record; record.Touching = touching.Count > 0;
            if (contactEnded) { record.Motion = Capture(world.ServerTick); record.Motion.Sequence++; }
            Record = record;
            if (contactEnded) registry.Transition(this, PebbleEnd.None, false, default);
            playerContact.BeforePhysics(Frame, true);
        }

        private int QueryOverlaps(float extent)
        {
            int count;
            while ((count = Physics.OverlapSphereNonAlloc(body.position, extent, overlaps, ~0,
                QueryTriggerInteraction.Ignore)) == overlaps.Length)
                System.Array.Resize(ref overlaps, overlaps.Length * 2);
            return count;
        }

        private void UpdateShooter()
        {
            if (Record.ShooterCleared) return;
            int count = QueryOverlaps(radius);
            foreach (var collider in shooterColliders)
                if (collider && collider.enabled && !collider.isTrigger &&
                    System.Array.IndexOf(overlaps, collider, 0, count) >= 0) return;
            ClearShooter();
            var record = Record; record.ShooterCleared = true;
            if (simulating)
            {
                record.Motion = Capture(world.ServerTick);
                record.Motion.Sequence++;
            }
            Record = record;
            if (simulating) registry.Transition(this, PebbleEnd.None, false, default);
        }

        private void ClearShooter()
        {
            foreach (var collider in shooterColliders) if (collider) Physics.IgnoreCollision(sphere, collider, false);
            shooterColliders.Clear();
        }

        private void SeedContacts()
        {
            int count = QueryOverlaps(radius + 0.002f);
            for (int i = 0; i < count; i++)
            {
                var collider = overlaps[i];
                if (collider != sphere && !ItemContactPhysics.NoImpulse(collider)) touching.Add(collider);
            }
        }

        private void OnCollisionEnter(Collision collision) => Contact(collision);
        private void OnCollisionStay(Collision collision) => Contact(collision);
        private void OnCollisionExit(Collision collision) => touching.Remove(collision.collider);
        private void Contact(Collision collision)
        {
            if (!simulating || ended || world.Replaying || ItemContactPhysics.NoImpulse(collision.collider)) return;
            if (touching.Contains(collision.collider)) return;
            if (!ItemContactPhysics.Take(sphere, collision.collider, out var contact))
            {
                if (collision.contactCount == 0) return;
                var point = collision.GetContact(0);
                contact = new ItemContactPhysics.Contact { Point = point.point, Normal = point.normal, Velocity = incoming };
            }
            touching.Add(collision.collider);
            impacts.Add((collision.collider, contact));
        }

        internal void AfterPhysics(float seconds)
        {
            if (!simulating || ended) return;
            impacts.Sort((a, b) => (a.Contact.Point - stepStart).sqrMagnitude.CompareTo((b.Contact.Point - stepStart).sqrMagnitude));
            for (int index = 0; index < impacts.Count; index++)
            {
                var impact = impacts[index];
                Vector3 center = impact.Contact.Point + impact.Contact.Normal * radius;
                Vector3 velocity = impact.Contact.Velocity;
                if (impact.Target.TryGetComponent<PlayerItemHitbox>(out var player))
                    playerContact.HostContact(player, Frame, velocity, -impact.Contact.Normal);
                playerContact.Segment(center, velocity, Mathf.Max(0.0001f, seconds), world.LocalTick);
                body.position = center;
                transform.position = center;
                SamplePlayer(world.LocalInventory ? world.LocalInventory.Hitbox : null);
                var record = Record;
                record.Impact++;
                record.Touching = true;
                record.ContactPoint = impact.Contact.Point; record.ContactNormal = impact.Contact.Normal;
                body.linearVelocity = Vector3.Reflect(velocity, impact.Contact.Normal) * definition.ReboundRetention;
                record.Motion = Capture(world.ServerTick);
                record.Motion.Sequence++; record.Motion.Path++; record.Motion.Boundary = true;
                record.Motion.Sleeping = false;
                Record = record;
                BirdHitReport bird = default;
                bool hasBird = BirdRegistry.Instance && BirdRegistry.Instance.ProjectileBirdContact(
                    new BirdHitReport { Source = record.Motion.Id == 0 ? record.Weapon : record.Motion.Id,
                        Player = record.BirdPlayer, Operation = record.Shot }, impact.Target, velocity.magnitude,
                    impact.Contact.Point, out bird);
                while (index + 1 < impacts.Count)
                {
                    var simultaneous = impacts[index + 1];
                    Vector3 otherCenter = simultaneous.Contact.Point + simultaneous.Contact.Normal * radius;
                    if ((otherCenter - center).sqrMagnitude > 0.000004f ||
                        (simultaneous.Contact.Velocity - velocity).sqrMagnitude > 0.0001f) break;
                    index++;
                    if (simultaneous.Target.TryGetComponent<PlayerItemHitbox>(out var otherPlayer))
                        playerContact.HostContact(otherPlayer, Frame, velocity, -simultaneous.Contact.Normal);
                    if (BirdRegistry.Instance && BirdRegistry.Instance.ProjectileBirdContact(
                        new BirdHitReport { Source = record.Motion.Id == 0 ? record.Weapon : record.Motion.Id,
                            Player = record.BirdPlayer, Operation = record.Shot }, simultaneous.Target, velocity.magnitude,
                        simultaneous.Contact.Point, out var otherBird))
                    {
                        if (hasBird) bird.Hits.AddRange(otherBird.Hits);
                        else { bird = otherBird; hasBird = true; }
                    }
                }
                ended = record.Impact >= 2;
                registry.Transition(this, ended ? PebbleEnd.Impact : PebbleEnd.None, hasBird, bird);
                if (ended) { StopPhysics(); break; }
            }
            if (!ended) playerContact.AfterPhysics(body.position, seconds);
            if (!ended) SweepKillVolumes();
        }

        private void SweepKillVolumes()
        {
            Vector3 travel = body.position - stepStart;
            if (travel.sqrMagnitude == 0f) return;
            int count = Physics.SphereCastNonAlloc(stepStart, radius, travel.normalized, crossings, travel.magnitude,
                ~0, QueryTriggerInteraction.Collide);
            for (int i = 0; i < count; i++)
                if (crossings[i].collider.TryGetComponent<ItemKillVolume>(out _)) { End(PebbleEnd.KillVolume); break; }
        }

        private void OnTriggerEnter(Collider other)
        {
            if (simulating && !ended && !world.Replaying && other.TryGetComponent<ItemKillVolume>(out _)) End(PebbleEnd.KillVolume);
        }

        internal void End(PebbleEnd reason)
        {
            if (ended) return;
            SamplePlayer(world.LocalInventory ? world.LocalInventory.Hitbox : null);
            var record = Record; record.Motion = Capture(world.ServerTick);
            record.Motion.Sequence++; record.Motion.Path++; record.Motion.Boundary = true; Record = record;
            ended = true;
            registry.Transition(this, reason, false, default);
            StopPhysics();
        }

        internal void Present(PlayerItemHitbox victim)
        {
            if (ended) return;
            bool terminal = false;
            if (!simulating && motion.Count > 0)
            {
                double tick = motion.Samples[motion.Count - 1].Tick +
                    (Time.unscaledTime - motion.ReceivedAt - world.InterpolationDelay) / world.TickDelta;
                presentedTick = System.Math.Max(presentedTick, System.Math.Max(motion.Samples[0].Tick, tick));
                terminal = terminalMotion.HasValue && presentedTick >= terminalMotion.Value.Tick;
                if (terminal) presentedTick = terminalMotion.Value.Tick;
                if (boundaryTick.HasValue && presentedTick >= boundaryTick.Value)
                {
                    motion.Departure(Vector3.zero, world.CorrectionDuration);
                    boundaryTick = null;
                }
                var sample = terminal ? terminalMotion.Value :
                    motion.Sample(presentedTick, false, radius, world.EnvironmentMask, world.TickDelta, out _);
                RigidbodyMotionState.Apply(body, sample, Vector3.zero);
            }
            motion.PresentOffset(visualRoot, world.CorrectionDuration);
            SamplePlayer(victim);
            if (terminal) ended = true;
            if (simulating && Time.unscaledTime >= nextThreat && body.linearVelocity.sqrMagnitude > 0.01f)
            {
                nextThreat = Time.unscaledTime + 0.2f;
                var birds = BirdRegistry.Instance;
                if (birds) birds.hitReporter?.Threat(BirdThreatKind.Rock, Record.Motion.Id, body.position,
                    birds.MaximumScareRadius, birds.Now, false);
            }
        }

        private void SamplePlayer(PlayerItemHitbox player)
        {
            if (!world.IsHost || !simulating) playerContact.SamplePlayerContact(player, Frame);
        }
        private void ReportImpact(PlayerItemHitbox player, Vector3 velocity, Vector3 playerVelocity, Vector3 normal) =>
            playerContact.Damage(player, definition.PebbleDamage, Vector3.zero);

        private void StopPhysics()
        {
            sphere.enabled = false;
            ItemContactPhysics.Unregister(sphere);
            offline.SetPredictionManager(null);
            if (!body.isKinematic) body.linearVelocity = body.angularVelocity = Vector3.zero;
            body.collisionDetectionMode = CollisionDetectionMode.Discrete;
            body.isKinematic = true;
        }

        internal void ResetForPool()
        {
            StopPhysics(); ClearShooter();
            playerContact.Reset(); motion.Reset();
            touching.Clear(); separated.Clear(); impacts.Clear();
            Record = default;
            incoming = stepStart = default; presentedTick = nextThreat = 0;
            boundaryTick = null; terminalMotion = null;
            ended = simulating = false;
            visualRoot.localPosition = Vector3.zero;
            gameObject.SetActive(false);
        }
    }
}
