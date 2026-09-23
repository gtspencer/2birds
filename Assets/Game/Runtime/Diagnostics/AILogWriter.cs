#if UNITY_INCLUDE_INSTRUMENTATION
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TwoBirds
{
    internal sealed class AILogWriter
    {
        private const int RecordLimit = 64 * 1024, ByteLimit = 8 * 1024 * 1024, SegmentLimit = 16 * 1024 * 1024;
        private static readonly UTF8Encoding Utf8 = new(false);
        private readonly object gate = new();
        private readonly Queue<Entry> queue = new();
        private readonly AutoResetEvent wake = new(false);
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Thread worker;
        private readonly FileStream runLock;
        private readonly Action<string> failure;
        private readonly string run = Guid.NewGuid().ToString();
        private readonly Dictionary<string, Loss> losses = new();
        private readonly Dictionary<string, long> counts = new();
        private readonly Dictionary<string, long> screenshotOutcomes = new();
        private readonly Queue<Segment> segments = new();
        private readonly Queue<(string path, long bytes)> images = new();
        private long sequence, writtenSequence, captures, pendingBytes, plannedBytes, imageBytes, evictedSegments, evictedImages;
        private int pendingRecords, segmentIndex;
        private int failureReported;
        private bool stopping, imagePending, finished;
        private volatile bool usable = true;
        private StreamWriter output;
        private Segment current;

        private sealed class Entry
        {
            internal string Json, Event;
            internal int Bytes;
            internal long Sequence, Time;
            internal bool Flush, Boundary;
            internal byte[] Png;
            internal JObject Capture, Context;
        }

        private sealed class Segment
        {
            internal string File;
            internal long First, Last, FirstTime, LastTime, MinTime, MaxTime;
        }

        private sealed class Loss
        {
            internal long Total, Pending, First, Last;
        }

        private sealed class PlainDataOnly : JsonConverter
        {
            public override bool CanConvert(Type type) => typeof(UnityEngine.Object).IsAssignableFrom(type) ||
                type == typeof(UnityEngine.Vector3) || type == typeof(UnityEngine.Quaternion) || type == typeof(UnityEngine.Vector2);
            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer) =>
                throw new JsonSerializationException("Copy Unity state into plain values and numeric arrays.");
            public override object ReadJson(JsonReader reader, Type type, object value, JsonSerializer serializer) => throw new NotSupportedException();
        }

        internal string DirectoryPath { get; }
        internal bool Usable => usable;
        internal long TimeUs => (long)(clock.ElapsedTicks * (1000000d / Stopwatch.Frequency));
        internal long NextCapture() => Interlocked.Increment(ref captures);
        internal bool ImagePending { get { lock (gate) return imagePending; } }

        internal AILogWriter(string root, JObject metadata, Action<string> failure)
        {
            this.failure = failure;
            DateTime start = DateTime.UtcNow;
            Directory.CreateDirectory(root);
            DirectoryPath = Path.Combine(root, start.ToString("yyyyMMddTHHmmssfffZ") + "-" + run);
            Directory.CreateDirectory(DirectoryPath);
            runLock = new FileStream(Path.Combine(DirectoryPath, "run.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            try
            {
                metadata["recorder"] = "TwoBirds.AILogger";
                metadata["v"] = 1;
                metadata["run"] = run;
                metadata["utc_start"] = start.ToString("O");
                metadata["process_id"] = Process.GetCurrentProcess().Id;
                File.WriteAllText(Path.Combine(DirectoryPath, "run.json"), metadata.ToString(Formatting.None), Utf8);
                Directory.CreateDirectory(Path.Combine(DirectoryPath, "screenshots"));
                PruneRuns(root);
            }
            catch { runLock.Dispose(); throw; }
            worker = new Thread(WriteLoop) { IsBackground = true, Name = "AI log writer" };
            worker.Start();
        }

        private static void PruneRuns(string root)
        {
            int inactive = 0;
            foreach (string directory in Directory.GetDirectories(root).OrderByDescending(Path.GetFileName))
            {
                string name = Path.GetFileName(directory);
                if (name.Length < 37 || !Guid.TryParse(name.Substring(name.Length - 36), out var id)) continue;
                string metadataPath = Path.Combine(directory, "run.json");
                string lockPath = Path.Combine(directory, "run.lock");
                if (!File.Exists(metadataPath) || !File.Exists(lockPath)) continue;
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                    using (var handle = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        var metadata = JObject.Parse(File.ReadAllText(metadataPath));
                        if ((string)metadata["recorder"] != "TwoBirds.AILogger" || (string)metadata["run"] != id.ToString()) continue;
                        if (++inactive <= 5) continue;
                        // Keep the lock held while deleting this recorder-owned run's contents.
                        foreach (string file in Directory.GetFiles(directory))
                            if (file != lockPath && file != metadataPath) File.Delete(file);
                        string shots = Path.Combine(directory, "screenshots");
                        if (Directory.Exists(shots) && (File.GetAttributes(shots) & FileAttributes.ReparsePoint) == 0)
                            Directory.Delete(shots, true);
                        File.Delete(metadataPath);
                    }
                    File.Delete(lockPath);
                    Directory.Delete(directory);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (JsonException) { }
            }
        }

        internal void Record(string name, object payload, JObject context, AILogLevel level, int version, int? frame)
        {
            if (!usable) return;
            long observed = TimeUs;
            JObject data;
            try
            {
                var serializer = JsonSerializer.Create(new JsonSerializerSettings
                {
                    Converters = { new PlainDataOnly() }, ReferenceLoopHandling = ReferenceLoopHandling.Error
                });
                data = payload == null ? new JObject() : JObject.FromObject(payload, serializer);
                var invalid = new JArray();
                foreach (var value in data.Descendants().OfType<JValue>().ToArray())
                {
                    if (value.Type != JTokenType.Float) continue;
                    double number = value.Value<double>();
                    if (!double.IsNaN(number) && !double.IsInfinity(number)) continue;
                    invalid.Add(value.Path);
                    value.Replace(JValue.CreateNull());
                }
                if (invalid.Count > 0) data["non_finite_fields"] = invalid;
            }
            catch (Exception exception)
            {
                data = new JObject { ["source_event"] = name, ["reason"] = exception.GetType().Name };
                name = "logger.serialization_failed";
                level = AILogLevel.Error;
            }
            lock (gate)
            {
                if (stopping || !usable) return;
                Admit(name, data, context, level, version, frame, observed);
                wake.Set();
            }
        }

        private JObject Envelope(string name, JObject data, JObject context, AILogLevel level, int version, int? frame, long observed, long seq) => new()
        {
            ["v"] = 1, ["run"] = run, ["seq"] = seq, ["t_us"] = observed, ["frame"] = frame,
            ["event"] = name, ["event_v"] = version, ["level"] = level.ToString().ToLowerInvariant(),
            ["ctx"] = context ?? new JObject(), ["data"] = data
        };

        private void Admit(string name, JObject data, JObject context, AILogLevel level, int version, int? frame, long observed)
        {
            string sourceName = name;
            if (name.StartsWith("screenshot."))
            {
                screenshotOutcomes.TryGetValue(name, out long count);
                screenshotOutcomes[name] = count + 1;
            }
            bool captureSample = name == "state.sample" && data["capture_id"] != null && data["capture_id"].Type != JTokenType.Null;
            bool critical = captureSample || level == AILogLevel.Error || name == "marker" || name.StartsWith("logger.") || name.StartsWith("screenshot.") || name.StartsWith("capture.");
            string json = Envelope(name, data, context, level, version, frame, observed, sequence + 3).ToString(Formatting.None);
            int bytes = Utf8.GetByteCount(json) + 1;
            if (bytes > RecordLimit)
            {
                data = new JObject { ["source_event"] = name, ["original_bytes"] = bytes, ["reason"] = "record_limit" };
                name = "logger.truncated";
                version = 1;
                level = AILogLevel.Warning;
                critical = true;
                context = new JObject();
                json = Envelope(name, data, context, level, version, frame, observed, sequence + 3).ToString(Formatting.None);
                bytes = Utf8.GetByteCount(json) + 1;
            }
            int slots = critical ? 4096 : name == "state.sample" ? 2048 : 4032;
            long budget = critical ? ByteLimit : name == "state.sample" ? ByteLimit / 2 : ByteLimit - 256 * 1024;
            if (pendingRecords + 3 > slots || pendingBytes + bytes + 8192 > budget)
            {
                Drop(sourceName, observed, "queue_pressure");
                return;
            }
            if (segmentIndex == 0 || plannedBytes + bytes + 4096 > SegmentLimit)
            {
                if (segmentIndex > 0)
                    Enqueue("logger.segment.ended", new JObject { ["index"] = segmentIndex }, null, AILogLevel.Info, 1, null, observed, true);
                segmentIndex++;
                plannedBytes = 0;
                Enqueue("logger.segment.started", new JObject
                {
                    ["index"] = segmentIndex, ["first_retained_seq"] = sequence + 1, ["first_retained_t_us"] = observed,
                    ["cumulative_losses"] = LossTotals(), ["segments_evicted"] = Math.Max(0, segmentIndex - 8),
                    ["images_evicted"] = Interlocked.Read(ref evictedImages)
                }, null, AILogLevel.Info, 1, null, observed, true, true);
            }
            Enqueue(name, data, context, level, version, frame, observed, critical);
        }

        private void Enqueue(string name, JObject data, JObject context, AILogLevel level, int version, int? frame, long observed, bool flush, bool boundary = false)
        {
            string json = Envelope(name, data, context, level, version, frame, observed, ++sequence).ToString(Formatting.None);
            int bytes = Utf8.GetByteCount(json) + 1;
            queue.Enqueue(new Entry { Json = json, Event = name, Bytes = bytes, Sequence = sequence, Time = observed, Flush = flush, Boundary = boundary });
            pendingRecords++;
            pendingBytes += bytes;
            plannedBytes += bytes;
        }

        private void Drop(string name, long observed, string reason)
        {
            string family = name.Split('.')[0];
            if (family != "state" && family != "player" && family != "item" && family != "unity" && family != "screenshot" &&
                family != "logger" && family != "marker" && family != "capture" && family != "session" && family != "input") family = "other";
            string key = family + ":" + reason;
            if (!losses.TryGetValue(key, out var loss)) losses[key] = loss = new Loss();
            if (loss.Pending++ == 0) loss.First = loss.Last = observed;
            loss.Total++;
            loss.First = Math.Min(loss.First, observed);
            loss.Last = Math.Max(loss.Last, observed);
        }

        private JObject LossTotals() => JObject.FromObject(losses.ToDictionary(pair => pair.Key, pair => pair.Value.Total));

        private void ReportLosses()
        {
            lock (gate)
            {
                if (pendingRecords > 4000 || pendingBytes > ByteLimit - 128 * 1024) return;
                foreach (var pair in losses)
                {
                    var loss = pair.Value;
                    if (loss.Pending == 0) continue;
                    string[] key = pair.Key.Split(':');
                    Admit("logger.dropped", new JObject
                    {
                        ["count"] = loss.Pending, ["event_class"] = key[0], ["reason"] = key[1],
                        ["from_t_us"] = loss.First, ["to_t_us"] = loss.Last, ["cumulative"] = loss.Total
                    }, null, AILogLevel.Warning, 1, null, TimeUs);
                    loss.Pending = 0;
                }
            }
        }

        internal bool QueueImage(byte[] png, JObject capture, JObject context)
        {
            lock (gate)
            {
                if (stopping || !usable || imagePending) return false;
                imagePending = true;
                queue.Enqueue(new Entry { Png = png, Capture = capture, Context = context });
                wake.Set();
            }
            return true;
        }

        private void SaveImage(Entry entry)
        {
            var data = entry.Capture;
            long id = (long)data["capture_id"];
            string name = $"shot-{id}-f{data["capture_frame"]}.png";
            string relative = "screenshots/" + name;
            string absolute = Path.Combine(DirectoryPath, "screenshots", name);
            int bytes = entry.Png.Length;
            try
            {
                File.WriteAllBytes(absolute, entry.Png);
            }
            catch (Exception exception)
            {
                Internal("screenshot.failed", new JObject { ["capture_id"] = id, ["reason"] = exception.GetType().Name }, entry.Context);
                if (exception is IOException || exception is UnauthorizedAccessException)
                {
                    usable = false;
                    lock (gate) stopping = true;
                    ReportFailure(exception);
                }
                return;
            }
            finally
            {
                entry.Png = null;
                lock (gate) imagePending = false;
            }
            images.Enqueue((absolute, bytes));
            imageBytes += bytes;
            data["file_name"] = name;
            data["path"] = relative;
            data["absolute_path"] = absolute;
            data["bytes"] = bytes;
            Internal("screenshot.saved", data, entry.Context);
            while (imageBytes > 64 * 1024 * 1024)
            {
                var old = images.Dequeue();
                File.Delete(old.path);
                imageBytes -= old.bytes;
                Interlocked.Increment(ref evictedImages);
                Internal("logger.artifact.evicted", new JObject { ["path"] = "screenshots/" + Path.GetFileName(old.path), ["bytes"] = old.bytes });
            }
        }

        private void Internal(string name, JObject data, JObject context = null)
        {
            lock (gate) Admit(name, data, context, AILogLevel.Info, 1, null, TimeUs);
        }

        private void WriteLoop()
        {
            bool ended = false;
            bool dirty = false;
            long nextFlush = 1000000;
            try
            {
                while (true)
                {
                    ReportLosses();
                    Entry entry = null;
                    lock (gate)
                    {
                        if (queue.Count > 0)
                        {
                            entry = queue.Dequeue();
                            if (entry.Png == null) { pendingRecords--; pendingBytes -= entry.Bytes; }
                        }
                        else if (stopping)
                        {
                            if (ended) break;
                            Admit("logger.run.ended", new JObject { ["reason"] = "shutdown" }, null, AILogLevel.Info, 1, null, TimeUs);
                            ended = true;
                            continue;
                        }
                    }
                    if (entry != null)
                    {
                        if (entry.Png != null) SaveImage(entry);
                        else { Write(entry); dirty = true; }
                    }
                    if (dirty && (entry?.Flush == true || TimeUs >= nextFlush))
                    {
                        output?.Flush();
                        dirty = false;
                        nextFlush = TimeUs + 1000000;
                    }
                    if (entry == null) wake.WaitOne(dirty ? (int)Math.Max(1, Math.Min(1000, (nextFlush - TimeUs) / 1000)) : Timeout.Infinite);
                }
                output?.Flush();
                var summary = new JObject
                {
                    ["v"] = 1, ["run"] = run, ["final_seq"] = writtenSequence,
                    ["retained_segments"] = JArray.FromObject(segments.Select(s => new { file = s.File, first_seq = s.First, last_seq = s.Last, first_t_us = s.FirstTime, last_t_us = s.LastTime, min_t_us = s.MinTime, max_t_us = s.MaxTime })),
                    ["counts"] = JObject.FromObject(counts), ["losses"] = LossTotals(),
                    ["segments_evicted"] = evictedSegments, ["images_evicted"] = evictedImages,
                    ["screenshots"] = JObject.FromObject(screenshotOutcomes)
                };
                File.WriteAllText(Path.Combine(DirectoryPath, "summary.json"), summary.ToString(Formatting.None), Utf8);
            }
            catch (Exception exception)
            {
                usable = false;
                ReportFailure(exception);
            }
            finally
            {
                usable = false;
                try { output?.Dispose(); } catch (IOException) { }
                runLock.Dispose();
                lock (gate)
                {
                    finished = true;
                    wake.Dispose();
                }
            }
        }

        private void Write(Entry entry)
        {
            if (entry.Boundary)
            {
                output?.Dispose();
                int index = (int)JObject.Parse(entry.Json)["data"]["index"];
                string name = $"events-{index:D6}.jsonl";
                output = new StreamWriter(Path.Combine(DirectoryPath, name), false, Utf8) { NewLine = "\n" };
                current = new Segment { File = name, First = entry.Sequence, FirstTime = entry.Time, MinTime = entry.Time, MaxTime = entry.Time };
                segments.Enqueue(current);
                while (segments.Count > 8)
                {
                    var old = segments.Dequeue();
                    File.Delete(Path.Combine(DirectoryPath, old.File));
                    evictedSegments++;
                }
            }
            output.WriteLine(entry.Json);
            current.Last = entry.Sequence;
            current.LastTime = entry.Time;
            current.MinTime = Math.Min(current.MinTime, entry.Time);
            current.MaxTime = Math.Max(current.MaxTime, entry.Time);
            writtenSequence = entry.Sequence;
            counts.TryGetValue(entry.Event, out long count);
            counts[entry.Event] = count + 1;
        }

        internal void Close()
        {
            lock (gate)
            {
                stopping = true;
                if (!finished) wake.Set();
            }
            worker.Join(2000);
        }

        private void ReportFailure(Exception exception)
        {
            if (Interlocked.Exchange(ref failureReported, 1) == 0) failure(exception.Message);
        }
    }
}
#endif
