if (UnityEditorInternal.ProfilerDriver.enabled || UnityEditor.SessionState.GetBool("ProfilerTools.Active", false))
    throw new InvalidOperationException("Stop recording before loading a capture for analysis.");
string capture = System.IO.Path.GetFullPath((string)options["capturePath"]);
string root = System.IO.Path.GetFullPath((string)options["outputDirectory"]);
string name = (string)options["outputName"] ?? System.IO.Path.GetFileNameWithoutExtension(capture);
if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
    throw new ArgumentException("outputName must be a filename, without a directory.");
string prefix = System.IO.Path.Combine(root, name);
string[] suffixes = { "-frames.csv", "-samples.json", "-allocations.json", "-callstacks.json", "-analysis.json" };
if (suffixes.Any(s => System.IO.File.Exists(prefix + s))) throw new InvalidOperationException("Choose a fresh outputName; exports already exist.");
bool stacks = (bool?)options["includeCallstacks"] ?? false;
if (stacks && (options["firstFrame"] == null || options["lastFrame"] == null))
    throw new ArgumentException("Call-stack extraction requires an explicit firstFrame and lastFrame; start with a small range.");
string stackFilter = (string)options["callstackFilter"] ?? "";
string sampleFilter = (string)options["sampleFilter"] ?? "";
if (!UnityEditorInternal.ProfilerDriver.LoadProfile(capture, false)) throw new InvalidOperationException("Could not load " + capture);
int first = (int?)options["firstFrame"] ?? UnityEditorInternal.ProfilerDriver.firstFrameIndex;
int last = (int?)options["lastFrame"] ?? UnityEditorInternal.ProfilerDriver.lastFrameIndex;
if (first < UnityEditorInternal.ProfilerDriver.firstFrameIndex || last > UnityEditorInternal.ProfilerDriver.lastFrameIndex || last < first)
    throw new ArgumentException("Requested frame range is outside the loaded capture.");
string sidecarPath = System.IO.Path.ChangeExtension(capture, ".json");
var sidecar = System.IO.File.Exists(sidecarPath) ? Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(sidecarPath)) : new Newtonsoft.Json.Linq.JObject();
string recordingId = (string)options["recordingId"] ?? (string)sidecar["recordingId"] ?? capture;
string csvId = "\"" + recordingId.Replace("\"", "\"\"") + "\"";
var totals = new System.Collections.Generic.Dictionary<string, double[]>();
var origins = new System.Collections.Generic.Dictionary<string, long[]>();
var stackTotals = new System.Collections.Generic.Dictionary<string, long[]>();
var stackFrames = new System.Collections.Generic.Dictionary<string, object[]>();
var methods = new System.Collections.Generic.Dictionary<ulong, object>();
var addresses = new System.Collections.Generic.List<ulong>();
int framesRead = 0, allocationSamplesWithoutStack = 0, allocationsWithoutSize = 0;
string threadName = null;
System.IO.Directory.CreateDirectory(root);
using (var writer = new System.IO.StreamWriter(prefix + "-frames.csv"))
{
    writer.WriteLine("recordingId,frame,startNs,mainThreadMs,playerLoopMs,waitForTargetFPSMs,presentWaitMs,physicsMs,gcAllocBytes,gcAllocCalls,gcCollectMs");
    for (int fi = first; fi <= last; fi++)
    {
        using (var f = UnityEditorInternal.ProfilerDriver.GetRawFrameDataView(fi, 0))
        {
            if (!f.valid) continue;
            if (f.threadName != "Main Thread") throw new InvalidOperationException("Thread 0 is not Main Thread; use a thread-specific analysis helper.");
            threadName = f.threadName;
            double loop = 0, wait = 0, present = 0, physics = 0, gcTime = 0;
            long bytes = 0, calls = 0;
            var parents = new System.Collections.Generic.Stack<(int end, string name)>();
            for (int i = 0; i < f.sampleCount; i++)
            {
                while (parents.Count > 0 && parents.Peek().end < i) parents.Pop();
                string sample = f.GetSampleName(i);
                double ms = f.GetSampleTimeMs(i);
                int descendants = f.GetSampleChildrenCountRecursive(i);
                double self = ms;
                for (int child = i + 1; child <= i + descendants; child += 1 + f.GetSampleChildrenCountRecursive(child)) self -= f.GetSampleTimeMs(child);
                if (!totals.TryGetValue(sample, out var total)) totals[sample] = total = new double[4];
                total[0]++; total[1] += ms; total[2] += Math.Max(0, self); total[3] = Math.Max(total[3], ms);
                if (sample == "PlayerLoop") loop += ms;
                if (sample == "WaitForTargetFPS") wait += ms;
                if (sample == "Gfx.WaitForPresentOnGfxThread") present += ms;
                if (sample == "Physics.Simulate") physics += ms;
                if (sample == "GC.Collect") gcTime += ms;
                if (sample == "GC.Alloc")
                {
                    bool hasSize = f.GetSampleMetadataCount(i) > 0;
                    long size = hasSize ? f.GetSampleMetadataAsLong(i, 0) : 0;
                    if (!hasSize) allocationsWithoutSize++;
                    bytes += size; calls++;
                    string parent = parents.Count > 0 ? parents.Peek().name : "unknown";
                    if (!origins.TryGetValue(parent, out var origin)) origins[parent] = origin = new long[2];
                    origin[0]++; origin[1] += size;
                    if (stacks)
                    {
                        addresses.Clear();
                        f.GetSampleCallstack(i, addresses);
                        if (addresses.Count == 0) allocationSamplesWithoutStack++;
                        else
                        {
                            string key = string.Join(";", addresses.Select(a => a.ToString("X")));
                            if (!stackFrames.TryGetValue(key, out var resolved))
                            {
                                resolved = addresses.Select(a =>
                                {
                                    if (!methods.TryGetValue(a, out var method)) methods[a] = method = f.ResolveMethodInfo(a);
                                    return (object)new { address = a.ToString("X"), method };
                                }).ToArray();
                                stackFrames[key] = resolved;
                            }
                            if (stackFilter.Length == 0 || Newtonsoft.Json.JsonConvert.SerializeObject(resolved).IndexOf(stackFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                if (!stackTotals.TryGetValue(key, out var value)) stackTotals[key] = value = new long[2];
                                value[0]++; value[1] += size;
                            }
                        }
                    }
                }
                if (descendants > 0) parents.Push((i + descendants, sample));
            }
            writer.WriteLine(FormattableString.Invariant($"{csvId},{fi},{f.frameStartTimeNs},{f.frameTimeMs},{loop},{wait},{present},{physics},{bytes},{calls},{gcTime}"));
            framesRead++;
        }
    }
}
Action<string, object> write = (suffix, value) => System.IO.File.WriteAllText(prefix + suffix, Newtonsoft.Json.JsonConvert.SerializeObject(value, Newtonsoft.Json.Formatting.Indented));
write("-samples.json", totals.Where(x => x.Key.IndexOf(sampleFilter, StringComparison.OrdinalIgnoreCase) >= 0).OrderByDescending(x => x.Value[2]).Select(x => new { name = x.Key, calls = x.Value[0], totalMs = x.Value[1], selfMs = x.Value[2], maxSampleMs = x.Value[3] }));
write("-allocations.json", origins.OrderByDescending(x => x.Value[1]).Select(x => new { parent = x.Key, calls = x.Value[0], bytes = x.Value[1] }));
if (stacks) write("-callstacks.json", stackTotals.OrderByDescending(x => x.Value[1]).Select(x => new { calls = x.Value[0], bytes = x.Value[1], frames = stackFrames[x.Key] }));
var result = new { capture, recordingId, first, last, framesRead, threadIndex = 0, threadName, includeCallstacks = stacks, sampleFilter, callstackFilter = stackFilter,
    allocationsWithoutSize, allocationSamplesWithoutStack, scope = "Main thread only. GPU, render-thread, memory and rendering counters are not exported. Sample filter affects only the sample aggregate, not frame totals. Call-stack filter affects only stack groups.", outputPrefix = prefix };
write("-analysis.json", result);
return result;
