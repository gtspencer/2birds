#if UNITY_INCLUDE_INSTRUMENTATION
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using FishNet.Transporting.Tugboat;
using UnityEngine;

namespace TwoBirds
{
    // Opt-in standalone automation. No gameplay or command-line control is compiled into release builds.
    public sealed class MvpValidation : MonoBehaviour
    {
        private SessionController session;
        private string route;
        private GamePlayerSpawner spawner;
        private int inputTick;
        private StreamWriter events;
        private float nextReport;
        private float processDeadline;
        private readonly HashSet<PlayerMotor> tracked = new();
        private readonly Dictionary<int, (long sent, long received)> traffic = new();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Initialize()
        {
            if (Argument("-mvpMode") == null) return;
            var go = new GameObject("MVP validation");
            DontDestroyOnLoad(go);
            go.AddComponent<MvpValidation>();
        }

        internal static string Argument(string key)
        {
            var args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, key);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
        private static int Number(string key, int fallback) => int.TryParse(Argument(key), out int value) ? value : fallback;

        private IEnumerator Start()
        {
            processDeadline = Time.unscaledTime + Number("-mvpDelay", 0) + (Number("-mvpSeconds", 125) + 50) * Number("-mvpCycles", 1);
            string directory = Argument("-mvpOutput") ?? Path.Combine(Application.persistentDataPath, "Validation");
            Directory.CreateDirectory(directory);
            events = new StreamWriter(Path.Combine(directory, "session.log"));
            events.AutoFlush = true;
            events.WriteLine($"Unity {Application.unityVersion}; {SystemInfo.processorType}; GPU {SystemInfo.graphicsDeviceName}; route {Argument("-mvpRoute")}; FPS {Number("-mvpFps", 60)}");
            route = Argument("-mvpRoute") ?? "walk";
            yield return new WaitForSecondsRealtime(Number("-mvpDelay", 0));
            while (SessionController.Instance == null || SessionController.Instance.Network == null) yield return null;
            session = SessionController.Instance;
            session.SetFrameCap(Number("-mvpFps", 60));
            session.Changed += LogState;
            var mode = Enum.Parse<SessionMode>(Argument("-mvpMode"), true);
            if (mode != SessionMode.Solo && !session.LocalNetworking)
            {
                Debug.LogError("Address-based MVP Host/Join runs require -localNetworking.");
                Application.Quit(1);
                yield break;
            }
            for (int cycle = 0; cycle < Number("-mvpCycles", 1); cycle++)
            {
                inputTick = 0;
                session.StartSession(mode, Argument("-mvpIp") ?? "127.0.0.1", Argument("-mvpPort") ?? "7770");
                float started = Time.unscaledTime;
                int cancelAfter = Number("-mvpCancelAfter", -1);
                while (session.Phase != SessionPhase.InGame && session.Phase != SessionPhase.Idle && Time.unscaledTime - started < 45f)
                {
                    if (cancelAfter >= 0 && Time.unscaledTime - started >= cancelAfter) session.Leave();
                    if (Argument("-mvpCancelPhase") == session.Phase.ToString()) session.Leave();
                    if (mode == SessionMode.Host && session.CanStart &&
                        session.Roster.Length >= Mathf.Clamp(Number("-mvpPlayers", 1), 1, SessionController.MultiplayerCapacity))
                        session.StartGame();
                    yield return null;
                }
                if (session.Phase == SessionPhase.InGame)
                {
                    events.WriteLine("ENTERED_GAME");
                    float end = Time.unscaledTime + Number("-mvpSeconds", 125);
                    while (Time.unscaledTime < end && session.Phase == SessionPhase.InGame) yield return null;
                }
                else events.WriteLine("DID_NOT_ENTER_GAME");
                session.Leave();
                while (session.Phase != SessionPhase.Idle) yield return null;
                events.WriteLine($"CYCLE {cycle + 1} COMPLETE; roots={FindObjectsByType<SessionController>().Length}; players={FindObjectsByType<PlayerMotor>().Length}");
                yield return new WaitForSecondsRealtime(0.5f);
            }
            Application.Quit();
        }

        private void LogState() => events.WriteLine($"{Time.unscaledTime:F3} {session.Mode} {session.Phase} {session.Status}");
        private void Update()
        {
            if (processDeadline > 0f && Time.unscaledTime > processDeadline) { Debug.LogError("Validation watchdog expired."); Application.Quit(1); return; }
            if (session == null || Time.unscaledTime < nextReport) return;
            nextReport = Time.unscaledTime + 1f;
            if (spawner == null) spawner = FindAnyObjectByType<GamePlayerSpawner>();
            foreach (var motor in FindObjectsByType<PlayerMotor>())
            {
                if (!tracked.Add(motor)) continue;
                var diagnostics = motor.gameObject.AddComponent<PredictionDiagnostics>();
                diagnostics.Open(Argument("-mvpOutput") ?? Path.Combine(Application.persistentDataPath, "Validation"));
                if (motor.IsOwner) motor.GetComponent<PlayerInputReader>().AutomatedInput = RouteInput;
            }
            foreach (var peer in session.PayloadTraffic)
            {
                traffic.TryGetValue(peer.Key, out var previous);
                var current = peer.Value;
                events.WriteLine($"TRAFFIC peer={peer.Key} sentPayloadBytesPerSecond={current.sent - previous.sent} receivedPayloadBytesPerSecond={current.received - previous.received}");
                traffic[peer.Key] = current;
            }
            events.WriteLine($"FRAME time={Time.unscaledTime:F3} dt={Time.unscaledDeltaTime:F5} players={FindObjectsByType<PlayerMotor>().Length} cameras={Camera.allCamerasCount} listeners={FindObjectsByType<AudioListener>().Length}");
        }

        private MoveInput RouteInput()
        {
            // Collision routes need both participants before their warm-up clocks start.
            if (route == "collision" && (spawner == null || spawner.PlayerCount < 2)) return default;
            int tick = inputTick++;
            if (tick < 300 || route == "idle") return default;
            tick -= 300;
            if (route == "fall") return new MoveInput(Vector2.up, 0f, false);
            if (route == "impulse" && session.Network.IsServerStarted && tick % 600 == 0)
                session.LocalPlayer.QueueImpulse(new Vector3(800f, 320f, 0f));
            if (route == "collision")
            {
                float sign = session.LocalPlayer.GetComponent<PlayerNetworkState>().Snapshot.SpawnSlot == 0 ? 1f : -1f;
                return new MoveInput(tick % 600 < 240 ? Vector2.right * sign : Vector2.zero, sign * 90f, false);
            }
            // Ten-second square then two seconds resting. Peers keep their initial separation.
            int segment = tick % 720 / 150;
            Vector2 direction = segment switch { 0 => Vector2.up, 1 => Vector2.right, 2 => Vector2.down, 3 => Vector2.left, _ => Vector2.zero };
            return new MoveInput(direction, 0f, tick % 180 == 30 && segment < 4);
        }

        private void OnDestroy()
        {
            if (session != null) session.Changed -= LogState;
            events?.Dispose();
        }
    }

    public sealed class PredictionDiagnostics : MonoBehaviour
    {
        private readonly Dictionary<uint, Vector3> history = new();
        private readonly Queue<uint> ticks = new();
        private PlayerMotor motor;
        private FishNet.Managing.Timing.TimeManager timeManager;
        private FishNet.Managing.Predicting.PredictionManager predictionManager;
        private StreamWriter csv;
        private StreamWriter impactLog;
        private Vector3 beforeReplay;
        private long tickStarted;
        private long replayStarted;
        private int replayCount;
        private float correction;
        private double replayMs;
        private double tickMs;
        private uint lastTick;
        private uint authoritativeServerTick;
        private int overloadedTicks;
        private float lastTime;

        public void Open(string directory)
        {
            motor = GetComponent<PlayerMotor>();
            timeManager = motor.TimeManager;
            predictionManager = motor.PredictionManager;
            csv = new StreamWriter(Path.Combine(directory, $"player-{motor.OwnerId}-session-{SessionController.Instance.SessionId}.csv"));
            impactLog = new StreamWriter(Path.Combine(directory, $"impacts-{motor.OwnerId}-session-{SessionController.Instance.SessionId}.log")) { AutoFlush = true };
            motor.ImpactTraced += WriteImpact;
            csv.WriteLine("kind,time,owner,isOwner,isServer,localTick,serverTick,stateTick,rttMs,stateAgeMs,errorM,postReplayM,graphicsOffsetM,replayCount,tickMs,replayMs,actualHz,x,y,z,resetRevision,authoritativeServerTick");
            motor.Simulated += PostTick;
            motor.Reconciled += Reconcile;
            motor.TimeManager.OnPreTick += PreTick;
            motor.PredictionManager.OnPreReconcile += BeforeReplay;
            motor.PredictionManager.OnPostReplicateReplay += ReplayTick;
            motor.PredictionManager.OnPostReconcile += AfterReplay;
            lastTime = Time.unscaledTime;
            lastTick = motor.TimeManager.LocalTick;
        }

        private void PreTick() => tickStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        private void WriteImpact(string entry) => impactLog.WriteLine(entry);
        private static double Milliseconds(long start) => (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000d / System.Diagnostics.Stopwatch.Frequency;
        private void PostTick(uint tick, Vector3 position)
        {
            tickMs = Milliseconds(tickStarted);
            overloadedTicks = tickMs > timeManager.TickDelta * 1000d ? overloadedTicks + 1 : 0;
            if (overloadedTicks == 30) Debug.LogWarning("Prediction processing has exceeded the tick budget for 30 consecutive ticks. Inspect the TimeManager and prediction Profiler markers.");
            Remember(motor.IsOwner ? tick : motor.TimeManager.Tick, position);
            Write("tick", tick, 0f, position);
        }
        private void Remember(uint tick, Vector3 position)
        {
            if (!history.ContainsKey(tick)) ticks.Enqueue(tick);
            history[tick] = position;
            while (ticks.Count > 600) history.Remove(ticks.Dequeue());
        }
        private void Reconcile(uint tick, uint serverTick, Vector3 authority)
        {
            authoritativeServerTick = serverTick;
            if (history.TryGetValue(tick, out Vector3 predicted)) Write("reconcile", tick, Vector3.Distance(predicted, authority), authority);
        }
        private void BeforeReplay(uint clientTick, uint serverTick)
        { beforeReplay = motor.Body.position; replayCount = 0; replayStarted = System.Diagnostics.Stopwatch.GetTimestamp(); }
        private void ReplayTick(uint clientTick, uint serverTick)
        { replayCount++; Remember(motor.IsOwner ? clientTick : serverTick, motor.Body.position); }
        private void AfterReplay(uint clientTick, uint serverTick)
        { correction = Vector3.Distance(beforeReplay, motor.Body.position); replayMs = Milliseconds(replayStarted); }
        private void Write(string kind, uint stateTick, float error, Vector3 position)
        {
            var time = motor.TimeManager;
            float elapsed = Time.unscaledTime - lastTime;
            double hz = elapsed > 0 ? (time.LocalTick - lastTick) / elapsed : 0;
            double age = authoritativeServerTick != 0 && time.Tick >= authoritativeServerTick ? (time.Tick - authoritativeServerTick) * time.TickDelta * 1000d : 0;
            csv.WriteLine(FormattableString.Invariant($"{kind},{Time.unscaledTime:F4},{motor.OwnerId},{motor.IsOwner},{motor.IsServerInitialized},{time.LocalTick},{time.Tick},{stateTick},{time.RoundTripTime},{age:F3},{error:F6},{correction:F6},{motor.GetComponent<PlayerPresentation>().GraphicsOffset:F6},{replayCount},{tickMs:F4},{replayMs:F4},{hz:F2},{position.x:F6},{position.y:F6},{position.z:F6},{motor.ResetRevision},{authoritativeServerTick}"));
            if (elapsed >= 1f) { lastTime = Time.unscaledTime; lastTick = time.LocalTick; csv.Flush(); }
        }
        private void OnDestroy()
        {
            if (motor != null)
            {
                motor.Simulated -= PostTick;
                motor.Reconciled -= Reconcile;
                motor.ImpactTraced -= WriteImpact;
            }
            if (timeManager != null) timeManager.OnPreTick -= PreTick;
            if (predictionManager != null)
            {
                predictionManager.OnPreReconcile -= BeforeReplay;
                predictionManager.OnPostReplicateReplay -= ReplayTick;
                predictionManager.OnPostReconcile -= AfterReplay;
            }
            csv?.Dispose();
            impactLog?.Dispose();
        }
    }
}
#endif

