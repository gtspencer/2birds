using System.Diagnostics;
using UnityEngine;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.IO;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine.SceneManagement;
#endif

namespace TwoBirds
{
    public enum AILogLevel { Info, Warning, Error }

    public struct AILogContext
    {
        public string subject, related_subject, operation, side;
        public uint? generation, epoch, local_tick, movement_tick, server_tick;
        public int? connection, owner;
        public bool? is_owner;
        public long? incarnation;
    }

    public static class AILogger
    {
        internal static float[] V(Vector3 value) => new[] { value.x, value.y, value.z };
        internal static float[] Q(Quaternion value) => new[] { value.x, value.y, value.z, value.w };
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private static AILogWriter writer;
        private static JObject session = new();
        private static int mainThread;
        private static long incarnation;
        [ThreadStatic] private static bool reportingFailure;
        internal static AILogCapture Capture { get; private set; }
        public static bool Enabled => writer != null && writer.Usable;
        internal static string OutputPath => writer?.DirectoryPath;
        internal static long TimeUs => writer?.TimeUs ?? 0;
        internal static long NewIncarnation() => ++incarnation;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            Shutdown();
            session = new JObject();
            incarnation = 0;
            mainThread = Thread.CurrentThread.ManagedThreadId;
            reportingFailure = false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Start()
        {
            string[] args = Environment.GetCommandLineArgs();
            if (Array.IndexOf(args, "-aiLogOff") >= 0) return;
            try
            {
                string root = Application.isEditor ? Path.Combine(Application.dataPath, "..", "Logs", "AI") :
                    Path.Combine(Application.persistentDataPath, "AILogs");
                int index = Array.IndexOf(args, "-aiLogDir");
                if (index >= 0)
                {
                    if (index + 1 >= args.Length || !Path.IsPathFullyQualified(args[index + 1]))
                        throw new ArgumentException("-aiLogDir requires an absolute directory.");
                    root = args[index + 1];
                }
                root = Path.GetFullPath(root);
                var metadata = new JObject
                {
                    ["unity_version"] = Application.unityVersion, ["game_version"] = Application.version,
                    ["build_guid"] = Application.buildGUID, ["editor"] = Application.isEditor,
                    ["development"] = UnityEngine.Debug.isDebugBuild, ["protocol"] = SessionController.Protocol,
                    ["transport_configuration"] = Array.IndexOf(args, "-localNetworking") >= 0 ? "local" : "steam_or_solo",
                    ["output_root"] = root,
                    ["sampling"] = JObject.FromObject(new { baseline_hz = 2, burst_hz = 10, burst_seconds = 5, entities = 8 }),
                    ["limits"] = JObject.FromObject(new { records = 4096, queue_bytes = 8 * 1024 * 1024, reserved_records = 64, reserved_bytes = 256 * 1024, record_bytes = 64 * 1024, segment_bytes = 16 * 1024 * 1024, segments = 8, png_bytes = 16 * 1024 * 1024, screenshot_bytes = 64 * 1024 * 1024, inactive_runs = 5, flush_s = 1, shutdown_wait_s = 2 }),
                    ["families"] = new JArray("logger", "session", "unity", "marker", "player", "item", "input", "state", "capture", "screenshot")
                };
                writer = new AILogWriter(root, metadata, ReportFailure);
                Application.logMessageReceivedThreaded += UnityLog;
                Application.quitting += Shutdown;
                SceneManager.activeSceneChanged += SceneChanged;
#if UNITY_EDITOR
                UnityEditor.EditorApplication.playModeStateChanged += PlayModeChanged;
                UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
#endif
                Log("logger.run.started", new { capture_available = CaptureAvailable });
                Log("capture.availability", new { available = CaptureAvailable, reason = CaptureAvailable ? "available" : "batch_or_headless" });
            }
            catch (Exception exception) { ReportFailure(exception.Message); }
        }

        internal static bool CaptureAvailable => !Application.isBatchMode &&
            SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null;

#if UNITY_EDITOR
        private static void PlayModeChanged(UnityEditor.PlayModeStateChange state)
        {
            if (state == UnityEditor.PlayModeStateChange.ExitingPlayMode) Shutdown();
        }
#endif
        private static void SceneChanged(Scene before, Scene after) =>
            Log("session.scene", new { before = before.name, after = after.name });

        internal static void Bind(SessionController host)
        {
            if (!Enabled) return;
            Capture?.Dispose();
            Capture = new AILogCapture(host, writer);
        }

        internal static void Session(uint? attempt, uint? networkSession, int? connection = null)
        {
            if (!Enabled) return;
            var next = new JObject();
            if (attempt.HasValue) next["attempt"] = attempt.Value;
            if (networkSession.HasValue) next["network_session"] = networkSession.Value;
            if (connection.HasValue) next["connection"] = connection.Value;
            Volatile.Write(ref session, next);
        }

        internal static JObject Context(AILogContext context)
        {
            var result = (JObject)Volatile.Read(ref session).DeepClone();
            var fields = JObject.FromObject(context, JsonSerializer.Create(new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
            result.Merge(fields);
            return result;
        }

        private static void UnityLog(string message, string stack, LogType type)
        {
            var run = writer;
            if (run == null || !run.Usable || reportingFailure) return;
            bool onMain = Thread.CurrentThread.ManagedThreadId == mainThread;
            int messageLength = message?.Length ?? 0, stackLength = stack?.Length ?? 0;
            run.Record("unity.log", new
            {
                message = Shorten(message), stack = Shorten(stack), log_type = type.ToString(),
                message_original_length = messageLength, stack_original_length = stackLength,
                truncated = messageLength > 4000 || stackLength > 4000
            }, Context(default), type == LogType.Warning ? AILogLevel.Warning :
                type == LogType.Log ? AILogLevel.Info : AILogLevel.Error, 1, onMain ? Time.frameCount : (int?)null);
        }

        private static string Shorten(string value) => value != null && value.Length > 4000 ? value.Substring(0, 4000) : value;

        private static void ReportFailure(string reason)
        {
            if (reportingFailure) return;
            reportingFailure = true;
            try { UnityEngine.Debug.LogError("AI logging disabled: " + reason); }
            finally { reportingFailure = false; }
        }

        internal static void Shutdown()
        {
            if (Enabled) Log("session.shutdown", new { reason = "recorder_lifecycle_shutdown" });
            Application.logMessageReceivedThreaded -= UnityLog;
            Application.quitting -= Shutdown;
            SceneManager.activeSceneChanged -= SceneChanged;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.playModeStateChanged -= PlayModeChanged;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= Shutdown;
#endif
            Capture?.Dispose();
            Capture = null;
            var run = writer;
            writer = null;
            run?.Close();
        }
#else
        public static bool Enabled => false;
#endif

        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public static void Log(string eventName, object data, AILogContext context = default,
            AILogLevel level = AILogLevel.Info, int eventVersion = 1)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var run = writer;
            if (run == null || !run.Usable) return;
            run.Record(eventName, data, Context(context), level, eventVersion, Time.frameCount);
#endif
        }

        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public static void Screenshot()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (!Enabled) return;
            if (Capture != null) Capture.Screenshot();
            else
            {
                long id = writer.NextCapture();
                Log("screenshot.requested", new { capture_id = id, request_frame = Time.frameCount, request_t_us = TimeUs });
                Log("screenshot.skipped", new { capture_id = id, reason = "session_host_unavailable" });
            }
#endif
        }

        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        internal static void Burst(string reason)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (Enabled) Capture?.Burst(reason);
#endif
        }

        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        internal static void Focus(WorldItem item)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (Enabled) Capture?.Focus(item);
#endif
        }
    }
}
