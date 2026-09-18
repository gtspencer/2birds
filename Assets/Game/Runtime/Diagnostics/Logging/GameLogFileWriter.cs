using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;

namespace TwoBirds
{
    internal sealed class GameLogFileWriter
    {
        const int MaxQueueCount = 4096;
        const int MaxQueueBytes = 8 * 1024 * 1024;
        const int BatchSize = 128;
        const int MaxSegmentBytes = 10 * 1024 * 1024;
        const int MaxSegments = 10;
        const long MaxRetentionBytes = 100L * 1024 * 1024;
        const int RetentionDays = 7;
        const int PerEntryOverhead = 256;
        const float FlushIntervalSec = 1f;
        const float DropReportIntervalSec = 1f;

        readonly object queueLock = new();
        readonly Queue<QueuedEntry> queue = new();
        int currentCount;
        int currentBytes;
        long droppedCount;
        readonly long[] droppedByLevel = new long[6];

        readonly AutoResetEvent signal = new(false);
        readonly ManualResetEventSlim shutdownComplete = new(false);
        volatile bool shutdownRequested;
        volatile bool expediteRequested;

        readonly string basePath;
        readonly string sessionId;
        readonly string sessionDirName;
        string sessionDirPath;
        string currentFilePath;
        int segmentNumber;
        long segmentBytes;
        StreamWriter writer;
        FileStream lockFile;

        volatile GameLogFileState state = GameLogFileState.Opening;
        string lastFailure;

        readonly string buildKind;
        readonly GameLogLevel buildDefault;
        volatile FilterPolicy headerPolicy;
        readonly string appVersion;
        readonly string unityVersion;
        readonly string buildGuid;
        readonly Stopwatch elapsed;
        readonly Action<string> onFailure;

        long highestWrittenSequence = -1;
        long flushedThroughSequence = -1;
        long flushTargetSequence = -1;
        readonly ManualResetEventSlim flushEvent = new(false);
        readonly object flushLock = new();
        volatile bool hadLoss;

        long lastReportedDrops;
        float lastDropReportSec;

        Thread workerThread;

        struct QueuedEntry
        {
            public GameLogEntry Entry;
            public long Sequence;
        }

        public int PendingCount { get { lock (queueLock) return currentCount; } }
        public long DroppedCount => Interlocked.Read(ref droppedCount);
        public string CurrentPath => currentFilePath;
        public GameLogFileState State => state;
        public string LastFailure => lastFailure;
        public bool IsWorkerDone => shutdownComplete.IsSet;

        public GameLogFileWriter(string basePath, string sessionId, string buildKind,
            GameLogLevel buildDefault, FilterPolicy initialPolicy,
            string appVersion, string unityVersion, string buildGuid,
            Stopwatch elapsed, Action<string> onFailure)
        {
            this.basePath = basePath;
            this.sessionId = sessionId;
            this.buildKind = buildKind;
            this.buildDefault = buildDefault;
            this.headerPolicy = initialPolicy;
            this.appVersion = appVersion;
            this.unityVersion = unityVersion;
            this.buildGuid = buildGuid;
            this.elapsed = elapsed;
            this.onFailure = onFailure;

            string ts = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfffK", CultureInfo.InvariantCulture)
                .Replace(":", "");
            int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            string shortGuid = Guid.NewGuid().ToString("N").Substring(0, 8);
            sessionDirName = $"twobirds-{ts}-p{pid}-{shortGuid}";

            workerThread = new Thread(WorkerLoop)
            {
                Name = "GameLog.FileWriter",
                IsBackground = true
            };
            workerThread.Start();
        }

        internal void UpdateHeaderPolicy(FilterPolicy policy)
        {
            headerPolicy = policy;
        }

        public bool Enqueue(GameLogEntry entry, long sequence, bool isError)
        {
            if (state == GameLogFileState.Failed)
            {
                Interlocked.Increment(ref droppedCount);
                hadLoss = true;
                return false;
            }

            int textLen = entry.TextPayloadLength + entry.Scope.Length;
            int entryBytes = textLen * 2 + PerEntryOverhead;

            lock (queueLock)
            {
                int reserveCount = MaxQueueCount / 10;
                int reserveBytes = MaxQueueBytes / 10;

                if (!isError && (currentCount >= MaxQueueCount - reserveCount ||
                                 currentBytes + entryBytes > MaxQueueBytes - reserveBytes))
                {
                    droppedCount++;
                    droppedByLevel[(int)entry.Level]++;
                    hadLoss = true;
                    return false;
                }

                if (currentCount >= MaxQueueCount || currentBytes + entryBytes > MaxQueueBytes)
                {
                    droppedCount++;
                    droppedByLevel[(int)entry.Level]++;
                    hadLoss = true;
                    return false;
                }

                queue.Enqueue(new QueuedEntry { Entry = entry, Sequence = sequence });
                currentCount++;
                currentBytes += entryBytes;
                return true;
            }
        }

        public void Signal() => signal.Set();

        public void RequestExpedite() => expediteRequested = true;

        public void RequestShutdown()
        {
            shutdownRequested = true;
        }

        public GameLogFlushResult FlushThrough(long targetSequence, TimeSpan timeout)
        {
            if (state == GameLogFileState.Failed || state == GameLogFileState.Disabled)
                return GameLogFlushResult.Unavailable;

            if (Thread.CurrentThread == workerThread)
                return GameLogFlushResult.Unavailable;

            lock (flushLock)
            {
                if (flushedThroughSequence >= targetSequence)
                    return hadLoss ? GameLogFlushResult.CompletedWithLoss : GameLogFlushResult.Completed;

                if (targetSequence > flushTargetSequence)
                    flushTargetSequence = targetSequence;
            }

            signal.Set();

            var sw = Stopwatch.StartNew();
            while (true)
            {
                long remainMs = (long)timeout.TotalMilliseconds - sw.ElapsedMilliseconds;
                if (remainMs <= 0) return GameLogFlushResult.TimedOut;

                if (state == GameLogFileState.Failed)
                    return GameLogFlushResult.Unavailable;

                lock (flushLock)
                {
                    if (flushedThroughSequence >= targetSequence)
                        return hadLoss ? GameLogFlushResult.CompletedWithLoss : GameLogFlushResult.Completed;
                    flushEvent.Reset();
                }

                flushEvent.Wait(TimeSpan.FromMilliseconds(Math.Min(remainMs, 500)));
            }
        }

        public GameLogFlushResult WaitForShutdown(TimeSpan timeout)
        {
            if (shutdownComplete.Wait(timeout))
                return hadLoss ? GameLogFlushResult.CompletedWithLoss : GameLogFlushResult.Completed;
            return GameLogFlushResult.TimedOut;
        }

        void WorkerLoop()
        {
            try
            {
                if (!OpenSession())
                {
                    state = GameLogFileState.Failed;
                    DrainAndDiscard();
                    try { onFailure?.Invoke(lastFailure ?? "Failed to open session"); }
                    catch { }
                    return;
                }

                RunRetention();
                state = GameLogFileState.Ready;

                var batch = new List<QueuedEntry>(BatchSize);
                var deadline = Stopwatch.StartNew();
                bool dirty = false;

                while (!shutdownRequested)
                {
                    if (dirty)
                        signal.WaitOne(TimeSpan.FromSeconds(FlushIntervalSec));
                    else
                        signal.WaitOne();

                    while (true)
                    {
                        batch.Clear();
                        DequeueBatch(batch);

                        if (batch.Count == 0) break;

                        dirty = true;
                        WriteBatch(batch);

                        bool shouldFlush = expediteRequested;
                        if (shouldFlush) expediteRequested = false;

                        if (!shouldFlush)
                            shouldFlush = HasPendingFlushTarget();

                        if (!shouldFlush)
                            shouldFlush = deadline.Elapsed.TotalSeconds >= FlushIntervalSec;

                        if (shouldFlush)
                        {
                            FlushToDisk(false);
                            dirty = false;
                            deadline.Restart();
                        }
                    }

                    if (dirty && deadline.Elapsed.TotalSeconds >= FlushIntervalSec)
                    {
                        FlushToDisk(false);
                        dirty = false;
                        deadline.Restart();
                    }

                    ServicePendingFlush();
                    TryEmitDropDiagnostic();
                }

                var finalBatch = new List<QueuedEntry>(BatchSize);
                while (true)
                {
                    finalBatch.Clear();
                    DequeueBatch(finalBatch);
                    if (finalBatch.Count == 0) break;
                    WriteBatch(finalBatch);
                }

                FlushToDisk(true);

                lock (flushLock)
                {
                    flushedThroughSequence = long.MaxValue;
                    flushEvent.Set();
                }
            }
            catch (Exception ex)
            {
                state = GameLogFileState.Failed;
                lastFailure = ex.GetType().Name + ": " + ex.Message;
                DrainAndDiscard();
                lock (flushLock) { flushEvent.Set(); }
                try { onFailure?.Invoke(lastFailure); }
                catch { }
            }
            finally
            {
                CloseStream();
                ReleaseLock();
                shutdownComplete.Set();
            }
        }

        void DequeueBatch(List<QueuedEntry> batch)
        {
            lock (queueLock)
            {
                int count = Math.Min(queue.Count, BatchSize);
                for (int i = 0; i < count; i++)
                {
                    var item = queue.Dequeue();
                    currentCount--;
                    int textLen = item.Entry.TextPayloadLength + item.Entry.Scope.Length;
                    currentBytes -= (textLen * 2 + PerEntryOverhead);
                    batch.Add(item);
                }
            }
        }

        bool HasPendingFlushTarget()
        {
            lock (flushLock) { return flushTargetSequence >= 0; }
        }

        void ServicePendingFlush()
        {
            lock (flushLock)
            {
                if (flushTargetSequence >= 0 && flushedThroughSequence >= flushTargetSequence)
                {
                    flushTargetSequence = -1;
                    flushEvent.Set();
                }
            }
        }

        void NotifyFlushProgress()
        {
            long written = highestWrittenSequence;
            lock (flushLock)
            {
                if (written > flushedThroughSequence)
                    flushedThroughSequence = written;

                if (flushTargetSequence >= 0 && flushedThroughSequence >= flushTargetSequence)
                {
                    flushTargetSequence = -1;
                    flushEvent.Set();
                }
            }
        }

        bool OpenSession()
        {
            try
            {
                sessionDirPath = Path.Combine(basePath, sessionDirName);
                Directory.CreateDirectory(sessionDirPath);

                string lockPath = Path.Combine(sessionDirPath, "active.lock");
                lockFile = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None);

                OpenNextSegment();
                return true;
            }
            catch (Exception ex)
            {
                lastFailure = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        void OpenNextSegment()
        {
            CloseStream();
            segmentNumber++;
            string fileName = segmentNumber.ToString("D4") + ".jsonl";
            currentFilePath = Path.Combine(sessionDirPath, fileName);
            segmentBytes = 0;

            var fs = new FileStream(currentFilePath, FileMode.CreateNew, FileAccess.Write,
                FileShare.Read);
            writer = new StreamWriter(fs, new UTF8Encoding(false), 8192);
            WriteSessionHeader();
        }

        void WriteSessionHeader()
        {
            var policy = headerPolicy;
            ScopeOverrideDto[] overrideDtos = null;
            if (policy?.ScopeOverrides is { Count: > 0 })
            {
                overrideDtos = new ScopeOverrideDto[policy.ScopeOverrides.Count];
                int i = 0;
                foreach (var kv in policy.ScopeOverrides)
                    overrideDtos[i++] = new ScopeOverrideDto { scope = kv.Key, level = kv.Value.ToString() };
            }

            var header = new SessionHeaderDto
            {
                schemaVersion = 1,
                kind = "session",
                sessionId = sessionId,
                segment = segmentNumber,
                timestampUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                applicationVersion = appVersion,
                unityVersion = unityVersion,
                buildGuid = buildGuid,
                buildKind = buildKind,
                processId = System.Diagnostics.Process.GetCurrentProcess().Id,
                buildDefault = this.buildDefault.ToString(),
                globalOverride = policy?.GlobalOverride?.ToString() ?? "",
                scopeOverrides = overrideDtos ?? Array.Empty<ScopeOverrideDto>()
            };
            string json = JsonUtility.ToJson(header, false);
            WriteLineRaw(json);
        }

        void WriteBatch(List<QueuedEntry> batch)
        {
            int i = 0;
            try
            {
                for (; i < batch.Count; i++)
                {
                    var item = batch[i];
                    string json = item.Entry.ToJson(item.Sequence);
                    int lineBytes = Encoding.UTF8.GetByteCount(json) + 1;

                    if (segmentBytes > 0 && segmentBytes + lineBytes > MaxSegmentBytes)
                        Rotate();

                    WriteLineRaw(json);
                    highestWrittenSequence = item.Sequence;
                    if (item.Entry.Truncated) hadLoss = true;
                }
            }
            catch
            {
                for (int j = i; j < batch.Count; j++)
                {
                    Interlocked.Increment(ref droppedCount);
                    droppedByLevel[(int)batch[j].Entry.Level]++;
                }
                hadLoss = true;
                throw;
            }
        }

        void WriteLineRaw(string json)
        {
            writer.WriteLine(json);
            segmentBytes += Encoding.UTF8.GetByteCount(json) + 1;
        }

        void Rotate()
        {
            if (segmentNumber >= MaxSegments)
                DeleteOldestSegment();

            OpenNextSegment();
            RunRetention();
        }

        void DeleteOldestSegment()
        {
            try
            {
                if (sessionDirPath == null) return;
                var files = Directory.GetFiles(sessionDirPath, "*.jsonl");
                if (files.Length <= 1) return;
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                File.Delete(files[0]);
            }
            catch { }
        }

        void FlushToDisk(bool hardFlush)
        {
            if (writer == null) return;
            writer.Flush();
            if (hardFlush) writer.BaseStream.Flush();
            NotifyFlushProgress();
        }

        void TryEmitDropDiagnostic()
        {
            long total = Interlocked.Read(ref droppedCount);
            if (total == lastReportedDrops) return;

            float elapsedSec = (float)elapsed.Elapsed.TotalSeconds;
            if (elapsedSec - lastDropReportSec < DropReportIntervalSec) return;
            lastDropReportSec = elapsedSec;
            lastReportedDrops = total;

            try
            {
                var sb = new StringBuilder("Dropped ");
                for (int i = 0; i < droppedByLevel.Length; i++)
                {
                    long count = droppedByLevel[i];
                    if (count > 0)
                        sb.Append(((GameLogLevel)i).ToString()).Append('=').Append(count).Append(' ');
                }
                sb.Append("total=").Append(total);

                var dto = new DiagnosticDto
                {
                    schemaVersion = 1,
                    kind = "diagnostic",
                    diagnosticType = "queue_overflow",
                    timestampUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    elapsedMs = elapsed.ElapsedMilliseconds,
                    sessionId = sessionId,
                    message = sb.ToString()
                };
                WriteLineRaw(JsonUtility.ToJson(dto, false));
            }
            catch { }
        }

        void RunRetention()
        {
            try
            {
                if (!Directory.Exists(basePath)) return;

                string cleanupLockPath = Path.Combine(basePath, ".cleanup.lock");
                FileStream cleanupLock;
                try
                {
                    cleanupLock = new FileStream(cleanupLockPath, FileMode.OpenOrCreate,
                        FileAccess.Write, FileShare.None);
                }
                catch { return; }

                try
                {
                    var dirs = Directory.GetDirectories(basePath, "twobirds-*");
                    var candidates = new List<(string path, DateTime created, long size)>();

                    foreach (var dir in dirs)
                    {
                        if (dir == sessionDirPath) continue;

                        string dirName = Path.GetFileName(dir);
                        if (!IsValidSessionDirName(dirName)) continue;

                        string candidateLockPath = Path.Combine(dir, "active.lock");
                        if (!File.Exists(candidateLockPath)) continue;

                        FileStream candidateLock;
                        try
                        {
                            candidateLock = new FileStream(candidateLockPath, FileMode.Open,
                                FileAccess.Write, FileShare.None);
                        }
                        catch { continue; }

                        long totalSize = 0;
                        foreach (var f in Directory.GetFiles(dir, "*.jsonl"))
                        {
                            try { totalSize += new FileInfo(f).Length; }
                            catch { }
                        }

                        var age = DateTime.UtcNow - Directory.GetCreationTimeUtc(dir);
                        if (age.TotalDays > RetentionDays)
                        {
                            DeleteSessionContents(dir);
                            candidateLock.Dispose();
                            try { File.Delete(candidateLockPath); } catch { }
                            try { Directory.Delete(dir); } catch { }
                            continue;
                        }

                        candidates.Add((dir, Directory.GetCreationTimeUtc(dir), totalSize));
                        candidateLock.Dispose();
                    }

                    candidates.Sort((a, b) => a.created.CompareTo(b.created));
                    long totalRetainedBytes = 0;
                    foreach (var c in candidates) totalRetainedBytes += c.size;

                    while (totalRetainedBytes > MaxRetentionBytes && candidates.Count > 0)
                    {
                        var oldest = candidates[0];
                        candidates.RemoveAt(0);

                        string candidateLockPath2 = Path.Combine(oldest.path, "active.lock");
                        FileStream recheck;
                        try
                        {
                            recheck = new FileStream(candidateLockPath2, FileMode.Open,
                                FileAccess.Write, FileShare.None);
                        }
                        catch { continue; }

                        long deletedBytes = 0;
                        foreach (var file in Directory.GetFiles(oldest.path, "*.jsonl"))
                        {
                            try
                            {
                                long len = new FileInfo(file).Length;
                                File.Delete(file);
                                deletedBytes += len;
                            }
                            catch { }
                        }

                        recheck.Dispose();
                        try { File.Delete(candidateLockPath2); } catch { }
                        try { Directory.Delete(oldest.path); } catch { }
                        totalRetainedBytes -= deletedBytes;
                    }
                }
                finally
                {
                    cleanupLock.Dispose();
                    try { File.Delete(cleanupLockPath); } catch { }
                }
            }
            catch { }
        }

        static void DeleteSessionContents(string dir)
        {
            foreach (var file in Directory.GetFiles(dir, "*.jsonl"))
            {
                try { File.Delete(file); }
                catch { }
            }
        }

        static bool IsValidSessionDirName(string name)
        {
            return name.Length > 20
                && name.StartsWith("twobirds-", StringComparison.Ordinal)
                && name.Contains("-p");
        }

        void DrainAndDiscard()
        {
            lock (queueLock)
            {
                while (queue.Count > 0)
                {
                    var item = queue.Dequeue();
                    droppedCount++;
                    droppedByLevel[(int)item.Entry.Level]++;
                }
                currentCount = 0;
                currentBytes = 0;
                hadLoss = true;
            }
        }

        void CloseStream()
        {
            try { writer?.Dispose(); }
            catch { }
            writer = null;
        }

        void ReleaseLock()
        {
            try { lockFile?.Dispose(); }
            catch { }
            lockFile = null;
        }

        [Serializable]
        struct ScopeOverrideDto
        {
            public string scope;
            public string level;
        }

        [Serializable]
        struct SessionHeaderDto
        {
            public int schemaVersion;
            public string kind;
            public string sessionId;
            public int segment;
            public string timestampUtc;
            public string applicationVersion;
            public string unityVersion;
            public string buildGuid;
            public string buildKind;
            public int processId;
            public string buildDefault;
            public string globalOverride;
            public ScopeOverrideDto[] scopeOverrides;
        }

        [Serializable]
        struct DiagnosticDto
        {
            public int schemaVersion;
            public string kind;
            public string diagnosticType;
            public string timestampUtc;
            public long elapsedMs;
            public string sessionId;
            public string message;
        }
    }
}
