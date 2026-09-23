string root = System.IO.Path.GetFullPath((string)options["outputDirectory"]);
string run = (string)options["runName"];
if (string.IsNullOrWhiteSpace(run) || run.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
    throw new ArgumentException("runName must be a filename, without a directory.");
double duration = (double?)options["durationSeconds"] ?? 120;
double interval = (double?)options["saveIntervalSeconds"] ?? 20;
bool allocations = (bool?)options["allocationCallstacks"] ?? false;
if (duration <= 0 || interval <= 0 || double.IsNaN(duration) || double.IsInfinity(duration) || double.IsNaN(interval) || double.IsInfinity(interval))
    throw new ArgumentException("Durations must be positive finite seconds.");
const string activeKey = "ProfilerTools.Active";
if (UnityEditorInternal.ProfilerDriver.enabled || UnityEditor.SessionState.GetBool(activeKey, false))
    throw new InvalidOperationException("Stop the current recording before starting another.");
int target = UnityEditorInternal.ProfilerDriver.connectedProfiler;
if (target == -1 || !UnityEditorInternal.ProfilerDriver.IsIdentifierConnectable(target))
    throw new InvalidOperationException("Connect the Profiler to a running player first.");
string prefix = System.IO.Path.Combine(root, run);
System.IO.Directory.CreateDirectory(root);
if (System.IO.File.Exists(prefix + "-started.json") || System.IO.File.Exists(prefix + "-stop.txt") || System.IO.Directory.GetFiles(root, run + "-part-*").Length > 0)
    throw new InvalidOperationException("Use a fresh runName; this run already has files.");
var settings = typeof(UnityEditorInternal.ProfilerDriver).Assembly.GetType("UnityEditor.Profiling.ProfilerUserSettings");
var historyProperty = settings.GetProperty("frameCount", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
int history = (int)historyProperty.GetValue(null);
var areas = new[] { UnityEngine.Profiling.ProfilerArea.CPU, UnityEngine.Profiling.ProfilerArea.Memory, UnityEngine.Profiling.ProfilerArea.Physics, UnityEngine.Profiling.ProfilerArea.Rendering };
var previousAreas = areas.Select(a => UnityEditorInternal.ProfilerDriver.IsAreaEnabled(a)).ToArray();
bool previousDeep = UnityEditorInternal.ProfilerDriver.deepProfiling;
bool previousEditor = UnityEditorInternal.ProfilerDriver.profileEditor;
var previousMemory = UnityEditorInternal.ProfilerDriver.memoryRecordMode;
string recordingId = Guid.NewGuid().ToString("N");
string connectionName = UnityEditorInternal.ProfilerDriver.GetConnectionIdentifier(target);
DateTime started = DateTime.UtcNow;
var timer = System.Diagnostics.Stopwatch.StartNew();
int part = 0, savedThrough = -1;
double nextSave = interval;
UnityEditor.EditorApplication.CallbackFunction tick = null;
UnityEditor.AssemblyReloadEvents.AssemblyReloadCallback reload = null;
Action restore = () =>
{
    UnityEditor.EditorApplication.update -= tick;
    UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= reload;
    UnityEditorInternal.ProfilerDriver.enabled = false;
    UnityEditorInternal.ProfilerDriver.deepProfiling = previousDeep;
    UnityEditorInternal.ProfilerDriver.profileEditor = previousEditor;
    UnityEditorInternal.ProfilerDriver.memoryRecordMode = previousMemory;
    for (int i = 0; i < areas.Length; i++) UnityEditorInternal.ProfilerDriver.SetAreaEnabled(areas[i], previousAreas[i]);
    UnityEditor.SessionState.SetBool(activeKey, false);
};
Action<string> save = reason =>
{
    bool finished = reason != "rolling";
    if (finished) UnityEditorInternal.ProfilerDriver.enabled = false;
    int first = UnityEditorInternal.ProfilerDriver.firstFrameIndex;
    int last = UnityEditorInternal.ProfilerDriver.lastFrameIndex;
    string path = prefix + "-part-" + (++part).ToString("00");
    bool saved = first >= 0 && last >= first && UnityEditorInternal.ProfilerDriver.SaveProfile(path + ".data");
    var result = new { recordingId, run, started, captured = DateTime.UtcNow, elapsedSeconds = timer.Elapsed.TotalSeconds,
        finished, reason, saved, first, last, gapBeforePart = first > savedThrough + 1, connection = connectionName,
        allocationCallstacks = allocations, historyFrames = history, capture = saved ? path + ".data" : null };
    string json = Newtonsoft.Json.JsonConvert.SerializeObject(result, Newtonsoft.Json.Formatting.Indented);
    System.IO.File.WriteAllText(path + ".json", json);
    System.IO.File.WriteAllText(prefix + "-status.json", json);
    savedThrough = last;
    nextSave = timer.Elapsed.TotalSeconds + interval;
    if (last >= first && first >= 0 && !saved) throw new InvalidOperationException("SaveProfile failed: " + path);
};
Action<string> finish = reason => { try { save(reason); } finally { restore(); } };
tick = () =>
{
    try
    {
        if (UnityEditorInternal.ProfilerDriver.connectedProfiler != target) { restore(); System.IO.File.WriteAllText(prefix + "-error.txt", "Profiler target changed; recording stopped. Use the previously saved parts."); return; }
        if (System.IO.File.Exists(prefix + "-stop.txt")) { finish("requested"); return; }
        if (!UnityEditorInternal.ProfilerDriver.enabled) { finish("recording-disabled"); return; }
        if (timer.Elapsed.TotalSeconds >= duration) { finish("duration-limit"); return; }
        int last = UnityEditorInternal.ProfilerDriver.lastFrameIndex;
        if (last > savedThrough && (timer.Elapsed.TotalSeconds >= nextSave || last - savedThrough >= Math.Max(1, history / 2))) save("rolling");
    }
    catch (Exception error) { restore(); System.IO.File.WriteAllText(prefix + "-error.txt", error.ToString()); }
};
reload = () => { try { finish("assembly-reload"); } catch (Exception error) { System.IO.File.WriteAllText(prefix + "-error.txt", error.ToString()); } };
try
{
    System.IO.File.WriteAllText(prefix + "-started.json", Newtonsoft.Json.JsonConvert.SerializeObject(new { recordingId, run, started, duration, interval, allocations, connection = connectionName, previousDeep, previousEditor, previousMemory = previousMemory.ToString(), previousAreas, areas = areas.Select(a => a.ToString()).ToArray() }, Newtonsoft.Json.Formatting.Indented));
    UnityEditor.SessionState.SetBool(activeKey, true);
    UnityEditorInternal.ProfilerDriver.ClearAllFrames();
    UnityEditorInternal.ProfilerDriver.deepProfiling = false;
    UnityEditorInternal.ProfilerDriver.profileEditor = false;
    UnityEditorInternal.ProfilerDriver.memoryRecordMode = allocations ? UnityEditorInternal.ProfilerMemoryRecordMode.GCAlloc : UnityEditorInternal.ProfilerMemoryRecordMode.None;
    foreach (var area in areas) UnityEditorInternal.ProfilerDriver.SetAreaEnabled(area, true);
    UnityEditor.EditorApplication.update += tick;
    UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += reload;
    UnityEditorInternal.ProfilerDriver.enabled = true;
}
catch { restore(); throw; }
return new { recordingId, run, started, durationSeconds = duration, recording = UnityEditorInternal.ProfilerDriver.enabled, status = prefix + "-status.json", stop = prefix + "-stop.txt" };
