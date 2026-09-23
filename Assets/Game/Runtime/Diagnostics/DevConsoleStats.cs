#if UNITY_INCLUDE_INSTRUMENTATION
using System;
using System.Collections.Generic;
using System.Text;
using FishNet.Transporting;

namespace TwoBirds
{
    internal sealed class DevConsoleStats
    {
        private readonly SessionController session;
        private readonly Dictionary<int, (long sent, long received)> baseline = new();
        private readonly Dictionary<int, (long sent, long received)> previous = new();
        private readonly StringBuilder text = new();
        private uint sessionId;

        internal DevConsoleStats(SessionController session)
        {
            this.session = session;
            sessionId = session.SessionId;
            Copy(session.PayloadTraffic, baseline);
        }

        internal void SessionChanged()
        {
            if (sessionId == session.SessionId) return;
            sessionId = session.SessionId;
            Copy(session.PayloadTraffic, baseline);
            BeginSample();
        }

        internal void BeginSample() => Copy(session.PayloadTraffic, previous);

        internal string Sample(int frames, double elapsed)
        {
            text.Clear();
            text.AppendLine($"FPS {frames / elapsed:F0}  ·  {elapsed * 1000 / frames:F1} ms/frame");
            var network = session.Network;
            bool server = network.IsServerStarted;
            bool client = network.IsClientStarted;
            text.AppendLine($"{session.Phase}  ·  {(server ? "Host" : client ? "Client" : "Offline")}");
            if (!server && !client) return text.ToString().TrimEnd();
            text.AppendLine($"{(session.DiagnosticTransport is GameTransport ? "UDP" : "Steam P2P")}  ·  Tick {network.TimeManager.TickRate} Hz");
            text.AppendLine($"RTT {(server ? "local" : network.TimeManager.RoundTripTime + " ms")}  ·  Players {session.Roster.Length}/{session.Capacity}");
            text.AppendLine($"Host: {session.DiagnosticEndpoint}");
            if (session.LocalNetworking && session.ShareEndpoint.Length > 0)
                text.AppendLine($"LAN: {session.ShareEndpoint}");

            long sent = 0, received = 0, sentDelta = 0, receivedDelta = 0;
            foreach (var peer in session.PayloadTraffic)
            {
                if (server ? peer.Key < 0 : peer.Key != -1) continue;
                baseline.TryGetValue(peer.Key, out var start);
                previous.TryGetValue(peer.Key, out var last);
                sent += Math.Max(0, peer.Value.sent - start.sent);
                received += Math.Max(0, peer.Value.received - start.received);
                sentDelta += Math.Max(0, peer.Value.sent - last.sent);
                receivedDelta += Math.Max(0, peer.Value.received - last.received);
            }
            text.AppendLine(server ? "Server payload (includes host loopback)" : "Client payload");
            text.AppendLine($"Up {Rate(sentDelta, elapsed)}  ·  Down {Rate(receivedDelta, elapsed)}");
            text.AppendLine($"Total up {sent / 1048576d:F2} MiB  ·  down {received / 1048576d:F2} MiB");

            if (server)
            {
                text.AppendLine("Client sockets · Up → host / Down ← host");
                foreach (var peer in session.PayloadTraffic)
                {
                    if (peer.Key < 0 || session.DiagnosticTransport.GetConnectionState(peer.Key) != RemoteConnectionState.Started) continue;
                    previous.TryGetValue(peer.Key, out var last);
                    string address = session.DiagnosticTransport.GetConnectionAddress(peer.Key);
                    text.AppendLine($"  #{peer.Key} {address}");
                    text.AppendLine($"    Up {Rate(peer.Value.received - last.received, elapsed)}  Down {Rate(peer.Value.sent - last.sent, elapsed)}");
                }
            }
            text.Append("Payload only; excludes transport overhead");
            BeginSample();
            return text.ToString();
        }

        private static string Rate(long bytes, double elapsed) => $"{Math.Max(0, bytes) / elapsed / 1024:F1} KiB/s";

        private static void Copy(Dictionary<int, (long sent, long received)> source,
            Dictionary<int, (long sent, long received)> destination)
        {
            destination.Clear();
            foreach (var peer in source) destination.Add(peer.Key, peer.Value);
        }
    }
}
#endif
