using System;
using System.Collections.Generic;
using FishNet.Managing;
using FishNet.Managing.Predicting;
using FishNet.Managing.Timing;
using UnityEngine;
using UnityEngine.SceneManagement;
using Random = UnityEngine.Random;

namespace TwoBirds
{
    [DefaultExecutionOrder(210)]
    public sealed partial class BirdRegistry : MonoBehaviour
    {
        public static BirdRegistry Instance { get; private set; }
        [SerializeField] private BirdCatalog catalog;
        [SerializeField] private BirdSettings settings;
        private NetworkManager network;
        private PredictionManager predictionManager;
        private readonly Dictionary<uint, BirdRecord> records = new();
        private readonly Dictionary<ushort, BirdSpecies> species = new();
        private readonly Dictionary<ushort, BirdSpawnZone> zones = new();
        private readonly Dictionary<ushort, BirdHabitatVolume> habitats = new();
        private readonly Dictionary<ushort, BirdPerch> perches = new();
        private readonly Dictionary<ushort, (uint life, uint revision)> claims = new();
        private readonly Dictionary<uint, double> decisions = new();
        private readonly List<uint> lives = new();
        private readonly List<Vacancy> vacancies = new();
        private readonly Dictionary<ushort, double> zoneReplacement = new();
        private readonly BirdClock clock = new();
        private uint epoch, attempt, fingerprint, nextLife, sequence;
        private bool active, ready;
        private int decisionCursor;
        private double lastPresentationTick;
        internal double Delta => network.TimeManager.TickDelta;
        internal double Now
        {
            get
            {
                var tick = network.TimeManager.GetPreciseTick(TickType.Tick);
                return clock.Read((tick.Tick + tick.PercentAsDouble) * Delta) / Delta;
            }
        }
        internal bool Host => network.IsServerStarted;
        internal bool Replaying => predictionManager.IsReconciling;
        internal BirdSettings Settings => settings;
        internal uint Epoch => epoch;
        private sealed class Vacancy { public ushort Zone, Species; public double Due; public bool Warned; }

        private void Awake() { Instance = this; network = GetComponent<NetworkManager>(); }
        private void Start()
        {
            predictionManager = GetComponent<PredictionManager>();
            RegisterMessages();
            network.TimeManager.OnPostTick += Tick;
        }

        public void BeginWorld(Scene scene)
        {
            if (!Initialize(scene)) return;
            epoch = (uint)Random.Range(1, int.MaxValue);
            ready = true;
            foreach (var zone in zones.Values)
            {
                int count = Random.Range(zone.Minimum, zone.Maximum + 1);
                for (int i = 0; i < count; i++) AddVacancy(zone, Now + i * 0.1d / Delta);
            }
            SessionController.Instance.BirdsReady(attempt, epoch);
        }

        public void JoinWorld(Scene scene)
        {
            if (Host) return;
            if (!Initialize(scene)) return;
            RequestBaseline();
        }

        private bool Initialize(Scene scene)
        {
            EndWorld();
            attempt = SessionController.Instance.SessionId;
            try
            {
                if (!settings || !catalog) throw new InvalidOperationException("Assign the bird settings and catalog on SessionRoot.");
                if (settings.BodyLimit < 1 || settings.BodyShrinkSeconds <= 0f || settings.RetrySeconds <= 0f || settings.LethalSpeed <= 0f)
                    throw new InvalidOperationException("Bird body limits, shrink duration, retry interval, and lethal speed must be positive.");
                foreach (var entry in catalog.Species)
                {
                    if (!entry || entry.Id == 0 || !species.TryAdd(entry.Id, entry)) throw new InvalidOperationException("Bird species IDs must be unique and nonzero.");
                    if (entry.Radius <= 0f || entry.FlightSpeed <= 0f || entry.GroundSpeed <= 0f || entry.SwimSpeed <= 0f ||
                        entry.IdleSeconds.x < 0f || entry.IdleSeconds.y < entry.IdleSeconds.x || entry.TravelSeconds.x <= 0f || entry.TravelSeconds.y < entry.TravelSeconds.x)
                        throw new InvalidOperationException($"Invalid bird tuning: {entry.name}.");
                    MaximumScareRadius = Mathf.Max(MaximumScareRadius, entry.ScareRadius);
                }
                foreach (var zone in FindObjectsByType<BirdSpawnZone>(FindObjectsSortMode.None))
                {
                    if (zone.gameObject.scene != scene) continue;
                    if (zone.Id == 0 || zone.Biome == 0 || !zones.TryAdd(zone.Id, zone)) throw new InvalidOperationException("Bird spawn IDs must be unique and nonzero.");
                    if (zone.Minimum < 0 || zone.Maximum < zone.Minimum || zone.ReplacementSeconds.x < 0f || zone.ReplacementSeconds.y < zone.ReplacementSeconds.x)
                        throw new InvalidOperationException($"Invalid population settings: {zone.name}.");
                    float weight = 0f;
                    foreach (var item in zone.Species)
                    {
                        if (!item.Species || !species.ContainsKey(item.Species.Id) || item.Weight < 0f) throw new InvalidOperationException($"Invalid species entry: {zone.name}.");
                        weight += item.Weight;
                    }
                    if (zone.Maximum > 0 && weight <= 0f) throw new InvalidOperationException($"No weighted species: {zone.name}.");
                }
                foreach (var habitat in FindObjectsByType<BirdHabitatVolume>(FindObjectsSortMode.None))
                {
                    if (habitat.gameObject.scene != scene) continue;
                    if (habitat.Id == 0 || habitat.Biome == 0 || !habitats.TryAdd(habitat.Id, habitat)) throw new InvalidOperationException("Bird habitat IDs must be unique and nonzero.");
                    if (habitat.Kind == BirdHabitatKind.Water && (Vector3.Dot(habitat.transform.up, Vector3.up) < 0.999f ||
                        !habitat.Contains(new Vector3(habitat.transform.position.x, habitat.WaterHeight, habitat.transform.position.z))))
                        throw new InvalidOperationException($"Water surface must be upright and inside its habitat: {habitat.name}.");
                }
                foreach (var perch in FindObjectsByType<BirdPerch>(FindObjectsSortMode.None))
                {
                    if (perch.gameObject.scene != scene) continue;
                    if (perch.Id == 0 || perch.Biome == 0 || !perches.TryAdd(perch.Id, perch)) throw new InvalidOperationException("Bird perch IDs must be unique and nonzero; duplicated volumes need new IDs.");
                }
                if (zones.Count > 0 && (LayerMask.NameToLayer("BirdBody") < 0 || LayerMask.NameToLayer("BirdQuery") < 0))
                    throw new InvalidOperationException("Create BirdBody and BirdQuery physics layers before authoring birds.");
                fingerprint = ContentFingerprint();
                active = true;
                hitReporter = new BirdHitReporter(this);
                return true;
            }
            catch (InvalidOperationException error)
            {
                Debug.LogError(error.Message, this);
                SessionController.Instance.Leave(error.Message);
                return false;
            }
        }

        private void AddVacancy(BirdSpawnZone zone, double due)
        {
            float sum = 0f;
            foreach (var item in zone.Species) sum += item.Weight;
            float roll = Random.value * sum;
            BirdSpecies selected = null;
            foreach (var item in zone.Species)
            {
                if (item.Weight <= 0f) continue;
                selected = item.Species;
                roll -= item.Weight;
                if (roll <= 0f) break;
            }
            if (selected) vacancies.Add(new Vacancy { Zone = zone.Id, Species = selected.Id, Due = due });
        }

        private void Tick()
        {
            if (!active || Replaying) return;
            double now = Now;
            hitReporter?.Flush();
            if (!Host) return;
            AdvanceClaims(now);
            bool worked = false;
            for (int i = 0; i < vacancies.Count; i++)
            {
                var vacancy = vacancies[i];
                if (vacancy.Due > now) continue;
                worked = true;
                if (TrySpawn(vacancy, now)) vacancies.RemoveAt(i);
                else
                {
                    vacancy.Due = now + settings.RetrySeconds / Delta;
                    if (!vacancy.Warned) { Debug.LogWarning($"No compatible starting habitat for {species[vacancy.Species].name} in {zones[vacancy.Zone].name}.", zones[vacancy.Zone]); vacancy.Warned = true; }
                }
                break;
            }
            if (!worked && lives.Count > 0)
            {
                for (int examined = 0; examined < lives.Count; examined++)
                {
                    decisionCursor %= lives.Count;
                    uint life = lives[decisionCursor++];
                    if (!decisions.TryGetValue(life, out double due) || due > now) continue;
                    if (records[life].Interrupt == BirdInterrupt.WaitingToFlee && !records[life].HasNext) RetryEscape(life, now);
                    else PlanNormal(life, now);
                    break;
                }
            }
            SendTransfers();
            SendDigest(now);
            ledger.Prune(Time.unscaledTime);
        }

        private void LateUpdate()
        {
            if (!active || !ready || Replaying) return;
            double now = Now;
            Present(now);
            hitReporter.SampleThreats(now);
            lastPresentationTick = now;
        }

        private void ReleaseClaim(ushort perch, uint life, uint revision = uint.MaxValue)
        {
            if (perch != 0 && claims.TryGetValue(perch, out var owner) && owner.life == life && owner.revision <= revision) claims.Remove(perch);
        }

        private void AdvanceClaims(double now)
        {
            for (int i = 0; i < lives.Count; i++)
            {
                uint life = lives[i]; var record = records[life];
                if (record.HasNext && now >= record.Next.StartTick)
                {
                    ReleaseClaim(record.Occupied, life);
                    ReleaseClaim(record.Route.Perch, life);
                    record.Occupied = 0;
                    record.Route = record.Next; record.Next = default; record.HasNext = false;
                    if (record.Interrupt == BirdInterrupt.WaitingToFlee) record.Interrupt = BirdInterrupt.Fleeing;
                }
                if (record.Reserved != 0 && !record.HasNext && now >= record.Route.End(Delta))
                {
                    record.Occupied = record.Reserved; record.Reserved = 0;
                }
                if (record.Interrupt == BirdInterrupt.Fleeing && now >= record.Route.End(Delta) + 1d / Delta) record.Interrupt = BirdInterrupt.Calm;
                records[life] = record;
            }
        }

        public void EndWorld()
        {
            active = ready = false;
            ClearPresentation(); ClearNetworking();
            records.Clear(); species.Clear(); zones.Clear(); habitats.Clear(); perches.Clear(); claims.Clear();
            decisions.Clear(); lives.Clear(); vacancies.Clear(); zoneReplacement.Clear(); ledger.Clear();
            waitingThreats.Clear(); MaximumScareRadius = 0f;
            playerTokens.Clear(); tokenPlayers.Clear(); cartDrivers.Clear(); nextPlayerToken = 0;
            hitReporter = null;
            epoch = nextLife = sequence = 0; decisionCursor = 0; lastPresentationTick = 0;
            clock.Reset(); LocalBalance = 0; rewardRevision = 0;
            RewardChanged?.Invoke(default);
        }

        private void OnDestroy()
        {
            EndWorld();
            if (network) { network.TimeManager.OnPostTick -= Tick; UnregisterMessages(); }
            if (Instance == this) Instance = null;
        }

        private uint ContentFingerprint()
        {
            uint hash = 2166136261;
            void Number(int value) { unchecked { hash = (hash ^ (uint)value) * 16777619; } }
            void Float(float value) => Number(BitConverter.SingleToInt32Bits(value));
            void Vector(Vector3 value) { Float(value.x); Float(value.y); Float(value.z); }
            void Transform(Transform value) { Vector(value.position); Vector(value.eulerAngles); Vector(value.lossyScale); }
            Number(catalog.ContentVersion); Float(settings.LethalSpeed); Number(settings.MultiKillBonus);
            Number(settings.SolidMask); Number(settings.GroundMask); Float(settings.GroundSlope); Float(settings.GroundStep);
            var ids = new List<ushort>(species.Keys); ids.Sort();
            foreach (ushort id in ids)
            {
                var s = species[id]; Number(id); Number((int)s.Behavior); Number((int)s.Capabilities);
                Float(s.Radius); Float(s.LandingOffset); Float(s.FlightSpeed); Float(s.GroundSpeed); Float(s.SwimSpeed);
                Float(s.FlightHeight); Float(s.ShortFlightDistance); Float(s.ScareRadius); Float(s.ScareDelay); Float(s.LethalSpeed);
                Float(s.IdleSeconds.x); Float(s.IdleSeconds.y); Float(s.TravelSeconds.x); Float(s.TravelSeconds.y);
                Number(s.Reward); Vector(s.ModelScale); Vector(s.ModelOffset);
            }
            ids = new List<ushort>(zones.Keys); ids.Sort();
            foreach (ushort id in ids)
            {
                var z = zones[id]; Number(id); Number(z.Biome); Transform(z.transform); Vector(z.Size); Number(z.Minimum); Number(z.Maximum);
                Float(z.ReplacementSeconds.x); Float(z.ReplacementSeconds.y);
                foreach (var weight in z.Species) { Number(weight.Species.Id); Float(weight.Weight); }
            }
            ids = new List<ushort>(habitats.Keys); ids.Sort();
            foreach (ushort id in ids) { var h = habitats[id]; Number(id); Number(h.Biome); Number((int)h.Kind); Transform(h.transform); Vector(h.Size); Float(h.WaterHeight); }
            ids = new List<ushort>(perches.Keys); ids.Sort();
            foreach (ushort id in ids) { var p = perches[id]; Number(id); Number(p.Biome); Number((int)p.Kind); Transform(p.transform); Float(p.Clearance); }
            return hash;
        }
    }
}
