using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace TwoBirds
{
    internal enum GameLogLifecycle { Uninitialized, Running, Stopping, Stopped }

    internal enum GameLogFileState { Disabled, Opening, Ready, Failed }

    public enum GameLogFlushResult { Completed, CompletedWithLoss, TimedOut, Unavailable }

    internal sealed class FilterPolicy
    {
        public readonly GameLogLevel? GlobalOverride;
        public readonly Dictionary<string, GameLogLevel> ScopeOverrides;

        public FilterPolicy(GameLogLevel? globalOverride, Dictionary<string, GameLogLevel> scopeOverrides)
        {
            GlobalOverride = globalOverride;
            ScopeOverrides = scopeOverrides is { Count: > 0 } ? scopeOverrides : null;
        }
    }

    public sealed class GameLogStatus
    {
        public readonly string LifecycleState;
        public readonly string BuildKind;
        public readonly GameLogLevel BuildDefault;
        public readonly GameLogLevel? GlobalOverride;
        public readonly string FileState;
        public readonly bool CaptureAttached;
        public readonly string FilePath;
        public readonly string SessionId;
        public readonly int PendingFileEntries;
        public readonly int PendingConsoleEntries;
        public readonly long DroppedFileEntries;
        public readonly long DroppedConsoleEntries;
        public readonly long TruncatedEntries;
        public readonly long RejectedOutsideSession;
        public readonly string LastFileFailure;

        internal GameLogStatus(GameLogLifecycle lifecycle, string buildKind,
            GameLogLevel buildDefault, GameLogLevel? globalOverride,
            GameLogFileState fileState, bool captureAttached, string filePath,
            string sessionId, int pendingFile, int pendingConsole,
            long droppedFile, long droppedConsole, long truncated,
            long rejected, string lastFileFailure)
        {
            LifecycleState = lifecycle.ToString();
            BuildKind = buildKind;
            BuildDefault = buildDefault;
            GlobalOverride = globalOverride;
            FileState = fileState.ToString();
            CaptureAttached = captureAttached;
            FilePath = filePath;
            SessionId = sessionId;
            PendingFileEntries = pendingFile;
            PendingConsoleEntries = pendingConsole;
            DroppedFileEntries = droppedFile;
            DroppedConsoleEntries = droppedConsole;
            TruncatedEntries = truncated;
            RejectedOutsideSession = rejected;
            LastFileFailure = lastFileFailure;
        }
    }

    internal sealed class GameLogService
    {
        readonly object admissionLock = new();
        readonly string sessionId;
        readonly string buildKind;
        readonly GameLogLevel buildDefault;
        readonly Stopwatch elapsed;
        readonly bool consoleEnabled;
        readonly bool fileEnabled;

        volatile FilterPolicy currentPolicy;
        readonly object policyLock = new();

        long nextSequence;
        long highestFileAdmittedSequence = -1;
        long nextEmissionId;
        long rejectedOutsideSession;
        long truncatedCount;
        volatile GameLogLifecycle lifecycle;

        readonly GameLogFileWriter fileWriter;
        readonly GameLogConsoleWriter consoleWriter;
        GameLogUnityCapture capture;

        public string SessionId => sessionId;
        public bool FileEnabled => fileEnabled;
        public bool ConsoleEnabled => consoleEnabled;
        internal GameLogLifecycle Lifecycle => lifecycle;
        internal bool IsWorkerDone => fileWriter?.IsWorkerDone ?? true;

        public GameLogService(GameLogOptions options, SynchronizationContext mainContext)
        {
            sessionId = Guid.NewGuid().ToString();
            elapsed = Stopwatch.StartNew();

            bool isEditor = false;
#if UNITY_EDITOR
            isEditor = true;
#endif
            if (isEditor)
            {
                buildKind = "Editor";
                buildDefault = GameLogLevel.Info;
            }
            else if (UnityEngine.Debug.isDebugBuild)
            {
                buildKind = "Development";
                buildDefault = GameLogLevel.Info;
            }
            else
            {
                buildKind = "Release";
                buildDefault = GameLogLevel.Error;
            }

            GameLogLevel? initialGlobalOverride = options?.MinimumLevel;
            Dictionary<string, GameLogLevel> normalizedOverrides = null;
            if (options?.ScopeOverrides is { Count: > 0 })
            {
                normalizedOverrides = new Dictionary<string, GameLogLevel>();
                foreach (var kv in options.ScopeOverrides)
                {
                    string normalized = NormalizeScope(kv.Key);
                    if (normalizedOverrides.Count < 128 || normalizedOverrides.ContainsKey(normalized))
                        normalizedOverrides[normalized] = kv.Value;
                }
            }
            currentPolicy = new FilterPolicy(initialGlobalOverride, normalizedOverrides);

            consoleEnabled = options?.ConsoleEnabled ?? true;
            fileEnabled = options?.FileEnabled ?? true;

            string appVersion = UnityEngine.Application.version;
            string unityVersion = UnityEngine.Application.unityVersion;
            string buildGuid = UnityEngine.Application.buildGUID;

            if (fileEnabled)
            {
                string basePath = options?.DirectoryPath;
                if (string.IsNullOrEmpty(basePath))
                    basePath = System.IO.Path.Combine(UnityEngine.Application.persistentDataPath, "Logs");
                fileWriter = new GameLogFileWriter(basePath, sessionId, buildKind, buildDefault,
                    currentPolicy, appVersion, unityVersion, buildGuid, elapsed, OnFileFailure);
            }

            if (consoleEnabled)
                consoleWriter = new GameLogConsoleWriter(mainContext, sessionId, AllocateEmissionId);

            lifecycle = GameLogLifecycle.Running;
        }

        void OnFileFailure(string failure)
        {
            if (consoleWriter == null) return;
            long emId = AllocateEmissionId();
            string marker = $"[GameLog:{sessionId}:{emId}]";
            string text = $"[Error] [GameLog] {marker} File logging failed: {failure}";
            UnityEngine.Debug.LogError(text);
        }

        long AllocateEmissionId()
        {
            lock (admissionLock) { return nextEmissionId++; }
        }

        public void AttachCapture()
        {
            if (!fileEnabled || capture != null) return;
            capture = new GameLogUnityCapture(this);
            capture.Attach();
        }

        public void SetMinimumLevel(GameLogLevel? minimum)
        {
            FilterPolicy newPolicy;
            lock (policyLock)
            {
                var prev = currentPolicy;
                newPolicy = new FilterPolicy(minimum,
                    prev?.ScopeOverrides != null
                        ? new Dictionary<string, GameLogLevel>(prev.ScopeOverrides)
                        : null);
                currentPolicy = newPolicy;
            }
            fileWriter?.UpdateHeaderPolicy(newPolicy);
        }

        public void SetScopeMinimum(string scope, GameLogLevel minimum)
        {
            scope = NormalizeScope(scope);
            FilterPolicy newPolicy;
            lock (policyLock)
            {
                var prev = currentPolicy;
                var newOverrides = prev?.ScopeOverrides != null
                    ? new Dictionary<string, GameLogLevel>(prev.ScopeOverrides)
                    : new Dictionary<string, GameLogLevel>();
                if (newOverrides.Count < 128 || newOverrides.ContainsKey(scope))
                    newOverrides[scope] = minimum;
                newPolicy = new FilterPolicy(prev?.GlobalOverride, newOverrides);
                currentPolicy = newPolicy;
            }
            fileWriter?.UpdateHeaderPolicy(newPolicy);
        }

        public void RemoveScopeMinimum(string scope)
        {
            scope = NormalizeScope(scope);
            FilterPolicy newPolicy;
            lock (policyLock)
            {
                var prev = currentPolicy;
                if (prev?.ScopeOverrides == null || !prev.ScopeOverrides.ContainsKey(scope))
                    return;
                var newOverrides = new Dictionary<string, GameLogLevel>(prev.ScopeOverrides);
                newOverrides.Remove(scope);
                newPolicy = new FilterPolicy(prev.GlobalOverride, newOverrides);
                currentPolicy = newPolicy;
            }
            fileWriter?.UpdateHeaderPolicy(newPolicy);
        }

        public bool IsEnabled(GameLogLevel level, string scope)
        {
            if (lifecycle != GameLogLifecycle.Running) return false;
            scope = NormalizeScope(scope);
            return level >= ResolveThreshold(scope);
        }

        GameLogLevel ResolveThreshold(string scope)
        {
            var policy = currentPolicy;
            var overrides = policy?.ScopeOverrides;
            var global = policy?.GlobalOverride;

            if (overrides is { Count: > 0 })
            {
                string candidate = scope;
                while (candidate.Length > 0)
                {
                    if (overrides.TryGetValue(candidate, out var scopeLevel))
                        return scopeLevel;
                    int dot = candidate.LastIndexOf('.');
                    candidate = dot >= 0 ? candidate.Substring(0, dot) : "";
                }
            }

            return global ?? buildDefault;
        }

        public void Admit(GameLogLevel level, string scope, string message,
            Exception exception, bool routeToConsole)
        {
            scope = NormalizeScope(scope);

            if (lifecycle != GameLogLifecycle.Running)
            {
                Interlocked.Increment(ref rejectedOutsideSession);
                return;
            }

            if (level < ResolveThreshold(scope)) return;

            var entry = GameLogEntry.Create(level, scope, message, exception,
                elapsed.ElapsedMilliseconds, sessionId, routeToConsole);

            if (entry.Truncated) Interlocked.Increment(ref truncatedCount);

            lock (admissionLock)
            {
                if (lifecycle != GameLogLifecycle.Running)
                {
                    Interlocked.Increment(ref rejectedOutsideSession);
                    return;
                }

                long seq = nextSequence++;

                if (fileEnabled && fileWriter != null)
                {
                    if (fileWriter.Enqueue(entry, seq, level >= GameLogLevel.Error))
                        highestFileAdmittedSequence = seq;
                }

                if (routeToConsole && consoleEnabled && consoleWriter != null)
                {
                    long emId = nextEmissionId++;
                    consoleWriter.Enqueue(entry, emId, sessionId);
                }
            }

            if (level >= GameLogLevel.Error && fileEnabled && fileWriter != null)
                fileWriter.RequestExpedite();

            if (fileEnabled && fileWriter != null)
                fileWriter.Signal();
        }

        public void AdmitUnity(UnityEngine.LogType type, string condition, string stackTrace)
        {
            if (lifecycle != GameLogLifecycle.Running) return;
            if (!fileEnabled || fileWriter == null) return;

            var level = type switch
            {
                UnityEngine.LogType.Warning => GameLogLevel.Warning,
                UnityEngine.LogType.Error or UnityEngine.LogType.Assert
                    or UnityEngine.LogType.Exception => GameLogLevel.Error,
                _ => GameLogLevel.Info
            };

            if (level < ResolveThreshold("Unity")) return;

            var entry = GameLogEntry.CreateFromUnity(type, condition, stackTrace,
                elapsed.ElapsedMilliseconds, sessionId);

            if (entry.Truncated) Interlocked.Increment(ref truncatedCount);

            lock (admissionLock)
            {
                if (lifecycle != GameLogLifecycle.Running) return;
                long seq = nextSequence++;
                if (fileWriter.Enqueue(entry, seq, level >= GameLogLevel.Error))
                    highestFileAdmittedSequence = seq;
            }

            if (level >= GameLogLevel.Error)
                fileWriter.RequestExpedite();

            fileWriter.Signal();
        }

        public long GetHighestEmissionId()
        {
            lock (admissionLock) { return nextEmissionId - 1; }
        }

        public GameLogStatus GetStatus()
        {
            int pendingFile = 0, pendingConsole = 0;
            long droppedFile = 0, droppedConsole = 0;
            string filePath = null;
            GameLogFileState fileState = GameLogFileState.Disabled;
            string fileFailure = null;

            if (fileWriter != null)
            {
                pendingFile = fileWriter.PendingCount;
                droppedFile = fileWriter.DroppedCount;
                filePath = fileWriter.CurrentPath;
                fileState = fileWriter.State;
                fileFailure = fileWriter.LastFailure;
            }
            if (consoleWriter != null)
            {
                pendingConsole = consoleWriter.PendingCount;
                droppedConsole = consoleWriter.DroppedCount;
            }

            var policy = currentPolicy;
            return new GameLogStatus(lifecycle, buildKind, buildDefault, policy?.GlobalOverride,
                fileState, capture?.IsAttached ?? false, filePath, sessionId,
                pendingFile, pendingConsole, droppedFile, droppedConsole,
                Interlocked.Read(ref truncatedCount),
                Interlocked.Read(ref rejectedOutsideSession), fileFailure);
        }

        public GameLogFlushResult Flush(TimeSpan timeout)
        {
            if (fileWriter == null) return GameLogFlushResult.Unavailable;
            if (lifecycle != GameLogLifecycle.Running && lifecycle != GameLogLifecycle.Stopping)
                return GameLogFlushResult.Unavailable;

            long target;
            lock (admissionLock) { target = highestFileAdmittedSequence; }
            if (target < 0) return GameLogFlushResult.Completed;
            return fileWriter.FlushThrough(target, timeout);
        }

        public GameLogFlushResult Shutdown(TimeSpan timeout)
        {
            capture?.Detach();

            lock (admissionLock)
            {
                if (lifecycle != GameLogLifecycle.Running)
                    return GameLogFlushResult.Unavailable;
                lifecycle = GameLogLifecycle.Stopping;
            }

            var deadline = Stopwatch.StartNew();

            consoleWriter?.DrainSync();

            var result = GameLogFlushResult.Completed;
            if (fileWriter != null)
            {
                long remainMs = (long)timeout.TotalMilliseconds - deadline.ElapsedMilliseconds;
                if (remainMs < 0) remainMs = 0;

                fileWriter.RequestShutdown();
                fileWriter.Signal();
                result = fileWriter.WaitForShutdown(TimeSpan.FromMilliseconds(remainMs));
            }

            if (result != GameLogFlushResult.TimedOut)
                lifecycle = GameLogLifecycle.Stopped;

            return result;
        }

        internal static string NormalizeScope(string scope)
        {
            if (string.IsNullOrWhiteSpace(scope)) return "General";
            scope = scope.Trim();
            if (scope.Length > 128)
            {
                int end = 128;
                if (char.IsHighSurrogate(scope[end - 1])) end--;
                scope = scope.Substring(0, end);
            }
            return scope;
        }
    }
}
