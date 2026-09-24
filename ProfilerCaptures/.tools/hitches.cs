if (UnityEditorInternal.ProfilerDriver.enabled || UnityEditor.SessionState.GetBool("ProfilerTools.Active", false))
    throw new InvalidOperationException("Stop recording before analyzing.");
string root = System.IO.Path.GetFullPath((string)options["outputDirectory"]);
string run = (string)options["runName"];
if (string.IsNullOrWhiteSpace(run) || run.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
    throw new ArgumentException("runName must be a filename, without a directory.");
string output = System.IO.Path.Combine(root, run + "-hitches.json");
string errorPath = System.IO.Path.Combine(root, run + "-hitches-error.txt");
if (System.IO.File.Exists(output) || System.IO.File.Exists(errorPath)) throw new InvalidOperationException("Report already exists: " + output);
string[] parts = options["capturePaths"] is Newtonsoft.Json.Linq.JArray paths
    ? paths.Select(p => System.IO.Path.GetFullPath((string)p)).ToArray()
    : System.IO.Directory.GetFiles(root, run + "-part-*.data").OrderBy(p => p, StringComparer.Ordinal).ToArray();
if (parts.Length == 0) throw new InvalidOperationException("No captures found for " + run);
double longMs = (double?)options["longFrameMs"] ?? 20;
Func<string, bool> isWait = n => n.Contains("Wait") || n == "Idle" || n == "PlayerLoop" || n == "Main Thread";
var markerId = new Guid("5b1c0e4a-7d2f-4f8e-9a61-2b1d3c4e5f60"); // Log.ProfilerMarkerId

// Runs on the next editor update: CLI requests are capped at a few seconds of main-thread time.
UnityEditor.EditorApplication.CallbackFunction job = null;
job = () => { UnityEditor.EditorApplication.update -= job; try {

string[] tagNames = { "gc", "shader", "jit", "spawn", "load", "ui", "animation", "focus-device", "log", "render-wait" };
double[] tagMinMs = { 1, 0.5, 1, 1, 1, 1, 2, 1, 0.5, 5 };
Func<string, int> classify = n =>
{
    int m = 0;
    if (n == "GC.Collect") m |= 1 << 0;
    if (n.StartsWith("Shader.", StringComparison.Ordinal) || n.Contains("GraphicsPipeline")) m |= 1 << 1;
    if (n == "Mono.JIT") m |= 1 << 2;
    if (n.StartsWith("Instantiate", StringComparison.Ordinal)) m |= 1 << 3;
    if (n.StartsWith("Loading.", StringComparison.Ordinal) || n.Contains("Integrate Assets") || n.Contains("UnloadUnusedAssets") || n.StartsWith("SceneManager", StringComparison.Ordinal)) m |= 1 << 4;
    if (n.StartsWith("UIElements", StringComparison.Ordinal) || n.StartsWith("UIR.", StringComparison.Ordinal) || n.StartsWith("UIDocument", StringComparison.Ordinal)) m |= 1 << 5;
    if (n.StartsWith("Animators.", StringComparison.Ordinal) || n.StartsWith("Director.", StringComparison.Ordinal) || n.Contains("Skinning")) m |= 1 << 6;
    if (n.Contains("FocusChanged") || n.Contains("DeviceChange")) m |= 1 << 7;
    if (n.Contains("Debug.Log") || n.Contains("StackTraceUtility")) m |= 1 << 8;
    if (n.StartsWith("Gfx.WaitForPresent", StringComparison.Ordinal) || n == "Gfx.WaitForRenderThread" || n.Contains("WaitForLastPresent")) m |= 1 << 9;
    return m;
};

Func<List<double>, double, double> pct = (sorted, p) =>
{
    if (sorted.Count == 0) return 0;
    double pos = (sorted.Count - 1) * p; int i = (int)pos;
    return sorted[i] + (sorted[Math.Min(i + 1, sorted.Count - 1)] - sorted[i]) * (pos - i);
};
Func<List<double>, object> period = times =>
{
    if (times.Count < 3) return new { events = times.Count, periodic = false };
    var gaps = times.Zip(times.Skip(1), (a, b) => b - a).ToList();
    double mean = gaps.Average(), sd = Math.Sqrt(gaps.Sum(g => (g - mean) * (g - mean)) / gaps.Count);
    gaps.Sort();
    return new { events = times.Count, medianIntervalS = Math.Round(pct(gaps, .5), 2), cv = Math.Round(sd / mean, 2), periodic = sd / mean < 0.3 };
};

var seen = new HashSet<long>();
var frames = new List<(long ns, double ms, long alloc, double gc, string part, int index)>();
var markers = new List<(long ns, int value)>();
var longFrames = new List<(long ns, double ms, int mask, Dictionary<string, object> detail)>();
var runSelf = new Dictionary<string, double>();
var firstSeen = new Dictionary<string, (long ns, double self)>();
var laterSelf = new Dictionary<string, List<double>>();
var allocParents = new Dictionary<string, long>();
var masks = new Dictionary<string, int>();
var frameSelf = new Dictionary<string, double>();
var frameTags = new double[tagNames.Length];
var stack = new Stack<(int end, string name, int covered)>();
int skipped = 0;

foreach (string part in parts)
{
    if (!UnityEditorInternal.ProfilerDriver.LoadProfile(part, false)) throw new InvalidOperationException("Could not load " + part);
    string partName = System.IO.Path.GetFileName(part);
    for (int fi = UnityEditorInternal.ProfilerDriver.firstFrameIndex; fi <= UnityEditorInternal.ProfilerDriver.lastFrameIndex; fi++)
    {
        using (var f = UnityEditorInternal.ProfilerDriver.GetRawFrameDataView(fi, 0))
        {
            if (f == null || !f.valid) continue;
            long ns = (long)f.frameStartTimeNs;
            if (!seen.Add(ns)) { skipped++; continue; }
            bool isLong = f.frameTimeMs > longMs;
            frameSelf.Clear(); Array.Clear(frameTags, 0, frameTags.Length); stack.Clear();
            long alloc = 0; double gc = 0, physicsMs = 0; int steps = 0;
            for (int i = 0; i < f.sampleCount; i++)
            {
                while (stack.Count > 0 && stack.Peek().end < i) stack.Pop();
                string name = f.GetSampleName(i) ?? "?";
                double ms = f.GetSampleTimeMs(i);
                int descendants = f.GetSampleChildrenCountRecursive(i);
                double self = ms;
                for (int c = i + 1; c <= i + descendants; c += 1 + f.GetSampleChildrenCountRecursive(c)) self -= f.GetSampleTimeMs(c);
                frameSelf.TryGetValue(name, out var fs); frameSelf[name] = fs + Math.Max(0, self);
                if (name == "GC.Collect") gc += ms;
                else if (name == "Physics.Simulate") { steps++; physicsMs += ms; }
                else if (name == "GC.Alloc")
                {
                    long size = f.GetSampleMetadataCount(i) > 0 ? f.GetSampleMetadataAsLong(i, 0) : 0;
                    alloc += size;
                    string parent = stack.Count > 0 ? stack.Peek().name : "unknown";
                    allocParents.TryGetValue(parent, out var ab); allocParents[parent] = ab + size;
                }
                int covered = stack.Count > 0 ? stack.Peek().covered : 0, mask = 0;
                if (isLong)
                {
                    if (!masks.TryGetValue(name, out mask)) masks[name] = mask = classify(name);
                    for (int r = 0; r < tagNames.Length; r++) if ((mask & ~covered & (1 << r)) != 0) frameTags[r] += ms;
                }
                if (descendants > 0) stack.Push((i + descendants, name, covered | mask));
            }
            foreach (var kv in frameSelf)
            {
                runSelf.TryGetValue(kv.Key, out var rs); runSelf[kv.Key] = rs + kv.Value;
                if (!firstSeen.ContainsKey(kv.Key)) firstSeen[kv.Key] = (ns, kv.Value);
                else { if (!laterSelf.TryGetValue(kv.Key, out var l)) laterSelf[kv.Key] = l = new List<double>(); l.Add(kv.Value); }
            }
            for (int m = 0; m < f.GetFrameMetaDataCount(markerId, 0); m++)
            {
                var data = f.GetFrameMetaData<int>(markerId, 0, m);
                markers.Add((ns, data.Length > 0 ? data[0] : 0));
            }
            frames.Add((ns, f.frameTimeMs, alloc, gc, partName, fi));
            if (!isLong) continue;

            int tagMask = 0;
            var tags = new Dictionary<string, double>();
            for (int r = 0; r < tagNames.Length; r++)
                if (frameTags[r] >= tagMinMs[r]) { tagMask |= 1 << r; tags[tagNames[r]] = Math.Round(frameTags[r], 2); }
            if (steps >= 2) { tags["physics-catchup"] = Math.Round(physicsMs, 2); }
            frameSelf.TryGetValue("PlayerLoop", out var loopSelf); frameSelf.TryGetValue("Main Thread", out var rootSelf);
            if (loopSelf + rootSelf >= 10) { tags["untracked"] = Math.Round(loopSelf + rootSelf, 2); }
            double? renderMs = null;
            for (int t = 1; t < 128; t++)
            {
                using (var v = UnityEditorInternal.ProfilerDriver.GetRawFrameDataView(fi, t))
                {
                    if (v == null || !v.valid) break;
                    if (v.threadName != "Render Thread") continue;
                    double busy = 0;
                    for (int i = 0; i < v.sampleCount; i++)
                    {
                        string n = v.GetSampleName(i) ?? "?";
                        if (n.Contains("Wait") || n.Contains("Idle") || n.Contains("Semaphore")) continue;
                        double s = v.GetSampleTimeMs(i);
                        int d = v.GetSampleChildrenCountRecursive(i);
                        for (int c = i + 1; c <= i + d; c += 1 + v.GetSampleChildrenCountRecursive(c)) s -= v.GetSampleTimeMs(c);
                        busy += Math.Max(0, s);
                    }
                    renderMs = Math.Round(busy, 2);
                    break;
                }
            }
            longFrames.Add((ns, f.frameTimeMs, tagMask | (steps >= 2 ? 1 << 30 : 0) | (loopSelf + rootSelf >= 10 ? 1 << 29 : 0), new Dictionary<string, object>
            {
                ["ms"] = Math.Round(f.frameTimeMs, 2),
                ["capture"] = partName,
                ["frame"] = fi,
                ["tags"] = tags.Count > 0 ? (object)tags : "unknown",
                ["topSelf"] = frameSelf.Where(kv => !isWait(kv.Key)).OrderByDescending(kv => kv.Value).Take(5).ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 2)),
                ["physicsSteps"] = steps,
                ["allocBytes"] = alloc,
                ["renderThreadBusyMs"] = renderMs,
            }));
        }
    }
}
if (frames.Count == 0) throw new InvalidOperationException("No main-thread frames found.");

long origin = frames[0].ns;
Func<long, double> sec = ns => Math.Round((ns - origin) / 1e9, 2);
Func<List<(long ns, double ms, long alloc, double gc, string part, int index)>, object> stats = set =>
{
    var ms = set.Select(x => x.ms).OrderBy(x => x).ToList();
    double seconds = ms.Sum() / 1000;
    return new
    {
        frames = set.Count,
        seconds = Math.Round(seconds, 2),
        medianMs = Math.Round(pct(ms, .5), 2), p95Ms = Math.Round(pct(ms, .95), 2), p99Ms = Math.Round(pct(ms, .99), 2), maxMs = Math.Round(ms.Last(), 2),
        longFrames = ms.Count(x => x > longMs),
        allocBytesPerSec = seconds > 0 ? (long)(set.Sum(x => x.alloc) / seconds) : 0,
        gcCollects = set.Count(x => x.gc > 0),
    };
};

var bounds = new List<(long ns, string label)> { (origin, "start") };
bounds.AddRange(markers.Select(m => (m.ns, "M" + m.value)));
var segments = bounds.Select((b, i) =>
{
    long end = i + 1 < bounds.Count ? bounds[i + 1].ns : long.MaxValue;
    var set = frames.Where(x => x.ns >= b.ns && x.ns < end).ToList();
    return new { from = b.label, to = i + 1 < bounds.Count ? bounds[i + 1].label : "end", startS = sec(b.ns), stats = set.Count > 0 ? stats(set) : null };
}).ToList();

var tagSummary = tagNames.Concat(new[] { "physics-catchup", "untracked" }).Select((name, r) =>
{
    int bit = name == "physics-catchup" ? 1 << 30 : name == "untracked" ? 1 << 29 : 1 << r;
    var hits = longFrames.Where(x => (x.mask & bit) != 0).ToList();
    return new { name, hits };
}).Where(x => x.hits.Count > 0).ToDictionary(x => x.name, x => (object)new
{
    frames = x.hits.Count,
    worstMs = Math.Round(x.hits.Max(h => h.ms), 2),
    timing = period(x.hits.Select(h => (h.ns - origin) / 1e9).ToList()),
});
int unknown = longFrames.Count(x => x.mask == 0);

var firstUse = firstSeen.Where(kv => kv.Value.self >= 2 && !isWait(kv.Key))
    .Select(kv =>
    {
        laterSelf.TryGetValue(kv.Key, out var later);
        double warm = later == null ? 0 : pct(later.OrderBy(x => x).ToList(), .5);
        return new { name = kv.Key, atS = sec(kv.Value.ns), firstMs = Math.Round(kv.Value.self, 2), warmMedianMs = Math.Round(warm, 3), laterCalls = later?.Count ?? 0 };
    })
    .Where(x => x.firstMs >= 5 * Math.Max(x.warmMedianMs, 0.01) && x.atS > 0)
    .OrderByDescending(x => x.firstMs).Take(15).ToList();

var report = new
{
    run,
    captures = parts.Select(p => System.IO.Path.GetFileName(p)).ToArray(),
    duplicateFramesSkipped = skipped,
    longFrameMs = longMs,
    overall = stats(frames),
    markers = markers.Select(m => new { marker = m.value, atS = sec(m.ns) }),
    segments,
    tags = tagSummary,
    unknownLongFrames = unknown,
    periodic = new
    {
        gcCollect = period(frames.Where(x => x.gc > 0).Select(x => (x.ns - origin) / 1e9).ToList()),
        longFrames = period(longFrames.Select(x => (x.ns - origin) / 1e9).ToList()),
    },
    firstUse,
    worstFrames = longFrames.OrderByDescending(x => x.ms).Take(25).Select(x => { x.detail["atS"] = sec(x.ns); return x.detail; }),
    topSelfMs = runSelf.Where(kv => !isWait(kv.Key)).OrderByDescending(kv => kv.Value).Take(12).ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 1)),
    topAllocParentsBytes = allocParents.OrderByDescending(kv => kv.Value).Take(10).ToDictionary(kv => kv.Key, kv => kv.Value),
    notes = "Main thread, deduplicated across overlapping parts. Tags use inclusive time of the outermost matching sample. firstUse compares the first frame a sample appears (self time) to its later median; appearances in the capture's first frame are skipped. renderThreadBusyMs excludes wait/idle samples. 'untracked' is self time directly in PlayerLoop/Main Thread: a stall outside instrumented code (OS, window focus, driver, profiler save). Missing data is not zero cost.",
};
System.IO.File.WriteAllText(output, Newtonsoft.Json.JsonConvert.SerializeObject(report, Newtonsoft.Json.Formatting.Indented));
} catch (Exception error) { System.IO.File.WriteAllText(errorPath, error.ToString()); } };
UnityEditor.EditorApplication.update += job;
UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
return new { scheduled = true, output, error = errorPath };
