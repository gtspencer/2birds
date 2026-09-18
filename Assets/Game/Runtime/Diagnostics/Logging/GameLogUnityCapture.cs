using System;
using System.Threading;
using UnityEngine;

namespace TwoBirds
{
    internal sealed class GameLogUnityCapture
    {
        readonly GameLogService service;
        readonly string sessionGuid;
        Application.LogCallback callback;
        volatile bool attached;

        [ThreadStatic] static bool reentrancyGuard;

        public bool IsAttached => attached;

        public GameLogUnityCapture(GameLogService service)
        {
            this.service = service;
            sessionGuid = service.SessionId;
        }

        public void Attach()
        {
            if (attached) return;
            callback = OnLogMessageReceived;
            Application.logMessageReceivedThreaded += callback;
            attached = true;
        }

        public void Detach()
        {
            if (!attached) return;
            Application.logMessageReceivedThreaded -= callback;
            attached = false;
        }

        void OnLogMessageReceived(string condition, string stackTrace, LogType type)
        {
            if (!attached) return;
            if (reentrancyGuard) return;

            reentrancyGuard = true;
            try
            {
                if (IsOurEcho(condition)) return;
                service.AdmitUnity(type, condition, stackTrace);
            }
            finally
            {
                reentrancyGuard = false;
            }
        }

        bool IsOurEcho(string condition)
        {
            if (string.IsNullOrEmpty(condition) || condition[0] != '[') return false;

            int firstClose = condition.IndexOf(']');
            if (firstClose < 1) return false;
            if (firstClose + 2 >= condition.Length
                || condition[firstClose + 1] != ' '
                || condition[firstClose + 2] != '[')
                return false;

            int secondClose = condition.IndexOf(']', firstClose + 3);
            if (secondClose < 0) return false;

            string markerPrefix = " [GameLog:" + sessionGuid + ":";
            int markerPos = secondClose + 1;
            if (markerPos + markerPrefix.Length > condition.Length) return false;

            if (string.Compare(condition, markerPos, markerPrefix, 0, markerPrefix.Length,
                    StringComparison.Ordinal) != 0)
                return false;

            int idStart = markerPos + markerPrefix.Length;
            int idEnd = condition.IndexOf(']', idStart);
            if (idEnd < 0 || idEnd == idStart) return false;

            string idStr = condition.Substring(idStart, idEnd - idStart);
            if (!long.TryParse(idStr, out long emissionId)) return false;

            return emissionId >= 0 && emissionId <= service.GetHighestEmissionId();
        }
    }
}
