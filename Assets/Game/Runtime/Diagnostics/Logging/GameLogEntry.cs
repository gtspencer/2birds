using System;
using System.Globalization;
using UnityEngine;

namespace TwoBirds
{
    internal sealed class GameLogEntry
    {
        const int MaxTextLength = 16384;
        const int MaxExceptionHalf = MaxTextLength / 2;

        public readonly GameLogLevel Level;
        public readonly string Scope;
        public readonly string Message;
        public readonly string Exception;
        public readonly string StackTrace;
        public readonly string Source;
        public readonly string TimestampUtc;
        public readonly long ElapsedMs;
        public readonly long Sequence;
        public readonly string SessionId;
        public readonly int ThreadId;
        public readonly bool Truncated;
        public readonly bool RouteToConsole;

        public int TextPayloadLength => Message.Length + Exception.Length + StackTrace.Length;

        GameLogEntry(GameLogLevel level, string scope, string message, string exception,
            string stackTrace, string source, string timestampUtc, long elapsedMs,
            long sequence, string sessionId, int threadId, bool truncated, bool routeToConsole)
        {
            Level = level;
            Scope = scope;
            Message = message;
            Exception = exception;
            StackTrace = stackTrace;
            Source = source;
            TimestampUtc = timestampUtc;
            ElapsedMs = elapsedMs;
            Sequence = sequence;
            SessionId = sessionId;
            ThreadId = threadId;
            Truncated = truncated;
            RouteToConsole = routeToConsole;
        }

        public static GameLogEntry Create(GameLogLevel level, string scope, string message,
            System.Exception exception, long elapsedMs, string sessionId,
            bool routeToConsole)
        {
            bool truncated = false;
            message ??= "";
            string exText = "";
            if (exception != null)
            {
                try { exText = exception.ToString(); }
                catch { exText = exception.GetType().FullName ?? "Exception"; }
            }

            if (exText.Length > MaxExceptionHalf)
            {
                exText = TruncateSafe(exText, MaxExceptionHalf);
                truncated = true;
            }

            int messageLimit = MaxTextLength - exText.Length;
            if (message.Length > messageLimit)
            {
                message = TruncateSafe(message, messageLimit);
                truncated = true;
            }

            string timestamp = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            return new GameLogEntry(level, scope, message, exText, "", "GameLog",
                timestamp, elapsedMs, 0, sessionId,
                System.Threading.Thread.CurrentThread.ManagedThreadId, truncated, routeToConsole);
        }

        public static GameLogEntry CreateFromUnity(LogType type, string condition, string stackTrace,
            long elapsedMs, string sessionId)
        {
            bool truncated = false;
            string message = condition ?? "";
            string stack = stackTrace ?? "";

            if (stack.Length > MaxExceptionHalf)
            {
                stack = TruncateSafe(stack, MaxExceptionHalf);
                truncated = true;
            }

            int messageLimit = MaxTextLength - stack.Length;
            if (message.Length > messageLimit)
            {
                message = TruncateSafe(message, messageLimit);
                truncated = true;
            }

            var level = type switch
            {
                LogType.Warning => GameLogLevel.Warning,
                LogType.Error or LogType.Assert or LogType.Exception => GameLogLevel.Error,
                _ => GameLogLevel.Info
            };

            string timestamp = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            return new GameLogEntry(level, "Unity", message, "", stack, "Unity",
                timestamp, elapsedMs, 0, sessionId,
                System.Threading.Thread.CurrentThread.ManagedThreadId, truncated, false);
        }

        static string TruncateSafe(string text, int limit)
        {
            if (limit <= 0) return "";
            if (limit >= text.Length) return text;
            int end = limit;
            if (char.IsHighSurrogate(text[end - 1])) end--;
            return text.Substring(0, end);
        }

        [Serializable]
        internal struct JsonDto
        {
            public int schemaVersion;
            public string kind;
            public string source;
            public string timestampUtc;
            public long elapsedMs;
            public long sequence;
            public string sessionId;
            public int threadId;
            public string level;
            public string scope;
            public string message;
            public string exception;
            public string stackTrace;
            public bool truncated;
        }

        public string ToJson(long sequence)
        {
            var dto = new JsonDto
            {
                schemaVersion = 1,
                kind = "log",
                source = Source,
                timestampUtc = TimestampUtc,
                elapsedMs = ElapsedMs,
                sequence = sequence,
                sessionId = SessionId,
                threadId = ThreadId,
                level = Level.ToString(),
                scope = Scope,
                message = Message,
                exception = Exception,
                stackTrace = StackTrace,
                truncated = Truncated
            };
            return JsonUtility.ToJson(dto, false);
        }

        public string FormatConsole(long emissionId, string sessionGuid)
        {
            string prefix = $"[{Level}] [{Scope}]";
            string marker = $" [GameLog:{sessionGuid}:{emissionId}]";
            string body = Message;
            if (Exception.Length > 0) body = body + "\n" + Exception;
            return prefix + marker + " " + body;
        }

        public LogType UnityLogType => Level switch
        {
            GameLogLevel.Warning => LogType.Warning,
            GameLogLevel.Error or GameLogLevel.Fatal => LogType.Error,
            _ => LogType.Log
        };
    }
}
