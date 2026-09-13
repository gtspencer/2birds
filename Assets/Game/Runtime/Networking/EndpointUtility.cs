using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TwoBirds
{
    public static class EndpointUtility
    {
        public readonly struct LanAddress
        {
            public readonly string Address;
            public readonly string Label;
            public LanAddress(string address, string adapter)
            {
                Address = address;
                Label = $"{address} — {adapter}";
            }
        }

        public static bool TryPort(string text, out ushort port) =>
            ushort.TryParse(text?.Trim(), out port) && port > 0;

        public static bool TryAddress(string text, out string address)
        {
            address = null;
            string[] parts = (text ?? "").Trim().Split('.');
            if (parts.Length != 4 || parts.Any(p => p.Length == 0 || p.Any(c => c < '0' || c > '9') || !byte.TryParse(p, out _)))
                return false;
            byte[] bytes = parts.Select(byte.Parse).ToArray();
            if (bytes[0] == 0 || bytes[0] >= 224 || bytes.All(b => b == 255))
                return false;
            address = new IPAddress(bytes).ToString();
            return true;
        }

        public static List<LanAddress> Discover()
        {
            var result = new List<LanAddress>();
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                foreach (var entry in adapter.GetIPProperties().UnicastAddresses)
                {
                    var ip = entry.Address;
                    if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip)) continue;
                    byte[] bytes = ip.GetAddressBytes();
                    if (bytes[0] == 169 && bytes[1] == 254) continue;
                    if (TryAddress(ip.ToString(), out string address))
                        result.Add(new LanAddress(address, adapter.Name));
                }
            }
            return result.OrderBy(a => a.Label, StringComparer.Ordinal).ToList();
        }
    }
}
