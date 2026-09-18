using System;
using System.Threading;

namespace TwoBirds
{
    public static class GameLog
    {
        static GameLogService service;
        static readonly object lifecycleLock = new();
        static long rejectedOutsideSession;

        public static void Initialize(GameLogOptions options = null)
        {
            lock (lifecycleLock)
            {
                if (service != null)
                {
                    var state = service.Lifecycle;
                    if (state == GameLogLifecycle.Running) return;
                    if (state == GameLogLifecycle.Stopping && !service.IsWorkerDone) return;
                    service = null;
                }
                options = options?.Copy() ?? new GameLogOptions();
                var ctx = SynchronizationContext.Current;
                service = new GameLogService(options, ctx);
                if (service.FileEnabled)
                    service.AttachCapture();
            }
        }

        public static bool IsEnabled(GameLogLevel level, string scope)
        {
            var svc = service;
            return svc != null && svc.IsEnabled(level, scope);
        }

        public static void Write(GameLogLevel level, string scope, string message,
            Exception exception = null)
        {
            var svc = service;
            if (svc == null)
            {
                Interlocked.Increment(ref rejectedOutsideSession);
                return;
            }
            svc.Admit(level, scope, message, exception, svc.ConsoleEnabled);
        }

        public static void Trace(string scope, string message) =>
            Write(GameLogLevel.Trace, scope, message);

        public static void Debug(string scope, string message) =>
            Write(GameLogLevel.Debug, scope, message);

        public static void Info(string scope, string message) =>
            Write(GameLogLevel.Info, scope, message);

        public static void Warning(string scope, string message) =>
            Write(GameLogLevel.Warning, scope, message);

        public static void Error(string scope, string message, Exception exception = null) =>
            Write(GameLogLevel.Error, scope, message, exception);

        public static void Fatal(string scope, string message, Exception exception = null) =>
            Write(GameLogLevel.Fatal, scope, message, exception);

        public static void SetMinimumLevel(GameLogLevel? minimum)
        {
            service?.SetMinimumLevel(minimum);
        }

        public static void SetScopeMinimum(string scope, GameLogLevel minimum)
        {
            service?.SetScopeMinimum(scope, minimum);
        }

        public static void RemoveScopeMinimum(string scope)
        {
            service?.RemoveScopeMinimum(scope);
        }

        public static GameLogStatus GetStatus()
        {
            var svc = service;
            if (svc != null) return svc.GetStatus();
            return new GameLogStatus(
                GameLogLifecycle.Uninitialized, "Unknown", GameLogLevel.Info, null,
                GameLogFileState.Disabled, false, null, null,
                0, 0, 0, 0, 0, Interlocked.Read(ref rejectedOutsideSession), null);
        }

        public static GameLogFlushResult Flush(TimeSpan timeout)
        {
            var svc = service;
            if (svc == null) return GameLogFlushResult.Unavailable;
            return svc.Flush(timeout);
        }

        public static GameLogFlushResult Shutdown(TimeSpan timeout)
        {
            GameLogService svc;
            lock (lifecycleLock)
            {
                svc = service;
                if (svc == null) return GameLogFlushResult.Unavailable;
            }

            var result = svc.Shutdown(timeout);

            if (result != GameLogFlushResult.TimedOut)
            {
                lock (lifecycleLock)
                {
                    if (service == svc) service = null;
                }
            }

            return result;
        }
    }
}
