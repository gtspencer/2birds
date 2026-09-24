using System;
using System.Diagnostics;
using UnityEngine;
using Object = UnityEngine.Object;

public static class Log
{
    // Read by ProfilerCaptures/.tools/hitches.cs; keep in sync.
    public static readonly Guid ProfilerMarkerId = new("5b1c0e4a-7d2f-4f8e-9a61-2b1d3c4e5f60");
    private static int _markerCount = 0;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetMarkers() => _markerCount = 0;
    
    
    [HideInCallstack, Conditional("UNITY_INCLUDE_INSTRUMENTATION")]
    public static void Info(object message, Object context = null)
    {
        UnityEngine.Debug.Log(message, context);
    }

    [HideInCallstack, Conditional("UNITY_INCLUDE_INSTRUMENTATION")]
    public static void Warning(object message, Object context = null)
    {
        UnityEngine.Debug.LogWarning(message, context);
    }

    [HideInCallstack]
    public static void Error(object message, Object context = null)
    {
        UnityEngine.Debug.LogError(message, context);
    }

    [HideInCallstack]
    public static void Exception(Exception exception, Object context = null)
    {
        UnityEngine.Debug.LogException(exception, context);
    }

    [HideInCallstack, Conditional("UNITY_INCLUDE_INSTRUMENTATION")]
    public static void Marker()
    {
        UnityEngine.Debug.Log($"MARKER ({++_markerCount})");
        UnityEngine.Profiling.Profiler.EmitFrameMetaData(ProfilerMarkerId, 0, new[] { _markerCount });
    }
}
