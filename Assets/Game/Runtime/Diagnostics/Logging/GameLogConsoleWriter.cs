using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using UnityEngine;

namespace TwoBirds
{
    internal sealed class GameLogConsoleWriter
    {
        const int MaxCount = 512;
        const int MaxBytes = 1024 * 1024;
        const int DrainBatch = 64;
        const int PerEntryOverhead = 256;
        const float DropReportIntervalSec = 1f;

        readonly object queueLock = new();
        readonly Queue<ConsoleItem> queue = new();
        int currentCount;
        int currentBytes;
        bool drainScheduled;
        long droppedCount;
        readonly long[] droppedByLevel = new long[6];
        float lastDropReport;

        readonly SynchronizationContext context;
        readonly SendOrPostCallback drainCallback;
        readonly string sessionGuid;
        readonly Func<long> allocateEmissionId;
        bool available;

        struct ConsoleItem
        {
            public string Text;
            public LogType LogType;
        }

        public int PendingCount { get { lock (queueLock) return currentCount; } }
        public long DroppedCount => Interlocked.Read(ref droppedCount);

        public GameLogConsoleWriter(SynchronizationContext mainContext, string sessionGuid,
            Func<long> allocateEmissionId)
        {
            context = mainContext;
            available = context != null;
            drainCallback = DrainBatchCallback;
            this.sessionGuid = sessionGuid;
            this.allocateEmissionId = allocateEmissionId;
        }

        public void Enqueue(GameLogEntry entry, long emissionId, string sessionGuid)
        {
            if (!available) return;

            string text = entry.FormatConsole(emissionId, sessionGuid);
            int textLen = text.Length;

            lock (queueLock)
            {
                int entryBytes = textLen * 2 + PerEntryOverhead;
                bool isError = entry.Level >= GameLogLevel.Error;
                int reserveCount = MaxCount / 10;
                int reserveBytes = MaxBytes / 10;

                if (!isError && (currentCount >= MaxCount - reserveCount ||
                                 currentBytes + entryBytes > MaxBytes - reserveBytes))
                {
                    droppedCount++;
                    droppedByLevel[(int)entry.Level]++;
                    return;
                }

                if (currentCount >= MaxCount || currentBytes + entryBytes > MaxBytes)
                {
                    droppedCount++;
                    droppedByLevel[(int)entry.Level]++;
                    return;
                }

                queue.Enqueue(new ConsoleItem { Text = text, LogType = entry.UnityLogType });
                currentCount++;
                currentBytes += entryBytes;

                if (!drainScheduled)
                {
                    drainScheduled = true;
                    context.Post(drainCallback, null);
                }
            }
        }

        void DrainBatchCallback(object _)
        {
            int drained = 0;
            bool queueEmpty = false;

            while (drained < DrainBatch)
            {
                ConsoleItem item;
                lock (queueLock)
                {
                    if (queue.Count == 0)
                    {
                        drainScheduled = false;
                        queueEmpty = true;
                        break;
                    }
                    item = queue.Dequeue();
                    currentCount--;
                    currentBytes -= (item.Text.Length * 2 + PerEntryOverhead);
                }
                EmitToUnity(item);
                drained++;
            }

            if (!queueEmpty && drained >= DrainBatch)
            {
                lock (queueLock)
                {
                    if (queue.Count > 0)
                        context.Post(drainCallback, null);
                    else
                    {
                        drainScheduled = false;
                        queueEmpty = true;
                    }
                }
            }

            if (queueEmpty)
                TryEmitDropSummary();
        }

        void TryEmitDropSummary()
        {
            long total = Interlocked.Read(ref droppedCount);
            if (total == 0) return;
            float now = Time.realtimeSinceStartup;
            if (now - lastDropReport < DropReportIntervalSec) return;
            lastDropReport = now;

            long emId = allocateEmissionId();
            var sb = new StringBuilder("[Warning] [GameLog] [GameLog:");
            sb.Append(sessionGuid).Append(':').Append(emId);
            sb.Append("] Console dropped ").Append(total).Append(" entries");
            Debug.LogWarning(sb.ToString());
        }

        static void EmitToUnity(ConsoleItem item)
        {
            switch (item.LogType)
            {
                case LogType.Warning:
                    Debug.LogWarning(item.Text);
                    break;
                case LogType.Error:
                    Debug.LogError(item.Text);
                    break;
                default:
                    Debug.Log(item.Text);
                    break;
            }
        }

        public void DrainSync()
        {
            while (true)
            {
                ConsoleItem item;
                lock (queueLock)
                {
                    if (queue.Count == 0)
                    {
                        drainScheduled = false;
                        return;
                    }
                    item = queue.Dequeue();
                    currentCount--;
                    currentBytes -= (item.Text.Length * 2 + PerEntryOverhead);
                }
                EmitToUnity(item);
            }
        }
    }
}
