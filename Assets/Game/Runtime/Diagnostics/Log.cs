using System;
using System.Diagnostics;
using UnityEngine;
using Object = UnityEngine.Object;

public static class Log
{
    private static int _markerCount = 0;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetMarkers() => _markerCount = 0;
    
    
    [HideInCallstack, Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
    public static void Info(object message, Object context = null)
    {
        UnityEngine.Debug.Log(message, context);
    }

    [HideInCallstack, Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
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

    [HideInCallstack, Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
    public static void Marker()
    {
        UnityEngine.Debug.Log($"MARKER ({++_markerCount})");
    }
}
