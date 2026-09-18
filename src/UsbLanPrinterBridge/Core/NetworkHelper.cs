using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace UsbLanPrinterBridge.Core
{
    public sealed class IpEntry
    {
        public IPAddress Address { get; set; }
        public IPAddress Mask { get; set; }

        public bool Contains(IPAddress other)
        {
            if (Address == null || Mask == null || other == null) return false;
            if (other.AddressFamily != AddressFamily.InterNetwork) return false;
            byte[] a = Address.GetAddressBytes(), m = Mask.GetAddressBytes(), o = other.GetAddressBytes();
            for (int i = 0; i < 4; i++)
                if ((a[i] & m[i]) != (o[i] & m[i])) return false;
            return true;
        }

        public int PrefixLength
        {
            get
            {
                if (Mask == null) return 0;
                int bits = 0;
                foreach (byte b in Mask.GetAddressBytes())
                    for (int i = 7; i >= 0; i--) if ((b & (1 << i)) != 0) bits++;
                return bits;
            }
        }

        public override string ToString() { return Address + "/" + PrefixLength; }
    }

    public sealed class AdapterInfo
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string Id { get; set; }
        /// <summary>IPv4 interface index (what iphlpapi and netsh use to identify the adapter).</summary>
        public int Index { get; set; }
        public NetworkInterfaceType Type { get; set; }
        public bool HasGateway { get; set; }
        public List<IpEntry> Addresses { get; set; }

        public override string ToString() { return Name; }
    }

    public sealed class CommandResult
    {
        public int ExitCode { get; set; }
        public string Output { get; set; }
        public bool Success { get { return ExitCode == 0; } }

        public string OutputOneLine
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Output)) return "";
                return string.Join(" | ", Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.Length > 0));
            }
        }
    }

    /// <summary>Adapter discovery, secondary ("virtual") IPv4 address management via netsh, and firewall rules.</summary>
    public static class NetworkHelper
    {
        public const string FirewallRuleName = "USB LAN Printer Bridge";

        // ------------------------------------------------------------------ discovery

        /// <summary>Up, non-loopback, non-tunnel adapters that carry at least one IPv4 address.</summary>
        public static List<AdapterInfo> GetAdapters()
        {
            var result = new List<AdapterInfo>();
            NetworkInterface[] nics;
            try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
            catch { return result; }

            foreach (NetworkInterface nic in nics)
            {
                try
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                    IPInterfaceProperties props = nic.GetIPProperties();
                    var addrs = new List<IpEntry>();
                    foreach (UnicastIPAddressInformation u in props.UnicastAddresses)
                    {
                        if (u.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        IPAddress mask = null;
                        try { mask = u.IPv4Mask; } catch { }
                        addrs.Add(new IpEntry { Address = u.Address, Mask = mask ?? IPAddress.Parse("255.255.255.0") });
                    }
                    if (addrs.Count == 0) continue;

                    int index = -1;
                    try { index = props.GetIPv4Properties().Index; } catch { }
                    if (index < 0) continue;

                    bool gateway = props.GatewayAddresses.Any(g => g.Address != null
                        && g.Address.AddressFamily == AddressFamily.InterNetwork
                        && !g.Address.Equals(IPAddress.Any));

                    result.Add(new AdapterInfo
                    {
                        Name = nic.Name,
                        Description = nic.Description,
                        Id = nic.Id,
                        Index = index,
                        Type = nic.NetworkInterfaceType,
                        HasGateway = gateway,
                        Addresses = addrs
                    });
                }
                catch
                {
                    // skip adapters that refuse to be queried
                }
            }

            // Adapters with a default gateway first (that's the "real" LAN), then Ethernet before Wi-Fi.
            return result
                .OrderByDescending(a => a.HasGateway)
                .ThenBy(a => a.Type == NetworkInterfaceType.Ethernet ? 0 : a.Type == NetworkInterfaceType.Wireless80211 ? 1 : 2)
                .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static IPAddress[] GetHostIPv4Addresses()
        {
            return GetAdapters().SelectMany(a => a.Addresses).Select(e => e.Address).Distinct().ToArray();
        }

        /// <summary>True when the address is already assigned to this computer on any adapter.</summary>
        public static bool IsLocalAddress(IPAddress ip)
        {
            if (ip == null) return false;
            if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any)) return true;
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    foreach (UnicastIPAddressInformation u in nic.GetIPProperties().UnicastAddresses)
                        if (u.Address.Equals(ip)) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Chooses the adapter a virtual IP should be added to: the user's explicit choice if it exists,
        /// otherwise the adapter whose subnet already contains the address, otherwise the primary LAN adapter.
        /// </summary>
        public static AdapterInfo PickAdapter(IPAddress ip, string preferredName, List<AdapterInfo> adapters)
        {
            if (adapters == null || adapters.Count == 0) return null;

            if (!string.IsNullOrWhiteSpace(preferredName))
            {
                AdapterInfo named = adapters.FirstOrDefault(a => string.Equals(a.Name, preferredName, StringComparison.OrdinalIgnoreCase));
                if (named != null) return named;
            }

            AdapterInfo bySubnet = adapters.FirstOrDefault(a => a.Addresses.Any(e => e.Contains(ip)));
            if (bySubnet != null) return bySubnet;

            return adapters[0];
        }

        /// <summary>Subnet mask to use for a virtual IP on the given adapter.</summary>
        public static IPAddress GuessMask(AdapterInfo adapter, IPAddress ip)
        {
            if (adapter != null)
            {
                IpEntry match = adapter.Addresses.FirstOrDefault(e => e.Contains(ip));
                if (match != null && match.Mask != null) return match.Mask;
                IpEntry first = adapter.Addresses.FirstOrDefault();
                if (first != null && first.Mask != null && !first.Mask.Equals(IPAddress.Any)) return first.Mask;
            }
            return IPAddress.Parse("255.255.255.0");
        }

        /// <summary>
        /// Suggests an unused address for a new mapping: the LAN's network with host part .200, .201, ...
        /// Falls back to 192.168.1.200 if the machine has no LAN address.
        /// </summary>
        public static string SuggestBindAddress(IEnumerable<string> alreadyUsed)
        {
            IpEntry lan = GetAdapters().SelectMany(a => a.Addresses).FirstOrDefault();
            if (lan == null) return "192.168.1.200";
            return SuggestInSubnet(lan, alreadyUsed) ?? lan.Address.ToString();
        }

        /// <summary>
        /// Picks a free address inside an adapter's own subnet, honouring its real prefix length.
        ///
        /// The prefix matters more than it looks. A /24 has 254 usable addresses, so the traditional x.x.x.200
        /// is fine. An iPhone hotspot hands out a /28, which has only fourteen, so an address ending .200 is
        /// outside the subnet entirely and nothing on that network can reach it. Getting this wrong produces a
        /// bridge that starts cleanly and is then silently unreachable.
        ///
        /// Returns null when the subnet is too small or the address is unusable.
        /// </summary>
        public static string SuggestInSubnet(IpEntry entry, IEnumerable<string> alreadyUsed)
        {
            if (entry == null || entry.Address == null || entry.Mask == null) return null;
            int prefix = entry.PrefixLength;
            if (prefix <= 0 || prefix > 30) return null;   // /31 and /32 have no room for a second host

            var used = new HashSet<string>(alreadyUsed ?? new string[0], StringComparer.OrdinalIgnoreCase);

            uint mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
            uint network = ToUInt32(entry.Address) & mask;
            uint broadcast = network | ~mask;
            uint firstHost = network + 1;
            uint lastHost = broadcast - 1;
            if (lastHost < firstHost) return null;

            // Prefer the familiar .200 when the subnet is actually big enough to contain it, then walk down
            // from the top of the range, which is where DHCP pools are least likely to reach.
            var candidates = new List<uint>();
            uint traditional = network + 200;
            if (traditional >= firstHost && traditional <= lastHost) candidates.Add(traditional);

            uint span = lastHost - firstHost + 1;
            uint limit = Math.Min(span, 4096u);
            for (uint k = 0; k < limit; k++) candidates.Add(lastHost - k);

            foreach (uint candidate in candidates)
            {
                IPAddress ip = ToAddress(candidate);
                string text = ip.ToString();
                if (used.Contains(text)) continue;
                if (IsLocalAddress(ip)) continue;      // already on this PC, so not a free address
                return text;
            }
            return null;
        }

        /// <summary>The connected adapter whose subnet contains this address, or null when none does.</summary>
        public static AdapterInfo FindAdapterForSubnet(IPAddress ip)
        {
            if (ip == null) return null;
            foreach (AdapterInfo adapter in GetAdapters())
                foreach (IpEntry entry in adapter.Addresses)
                    if (entry.Contains(ip)) return adapter;
            return null;
        }

        /// <summary>Describes the address ranges that would actually work right now, for error messages.</summary>
        public static string DescribeUsableRanges()
        {
            var parts = new List<string>();
            foreach (AdapterInfo adapter in GetAdapters())
                foreach (IpEntry entry in adapter.Addresses)
                {
                    string suggestion = SuggestInSubnet(entry, new string[0]);
                    parts.Add(adapter.Name + " is on " + entry
                              + (suggestion == null ? "" : ", so try " + suggestion));
                }
            return parts.Count == 0 ? "no connected network adapter" : string.Join("; ", parts.ToArray());
        }

        private static uint ToUInt32(IPAddress ip)
        {
            byte[] b = ip.GetAddressBytes();
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }

        private static IPAddress ToAddress(uint value)
        {
            return new IPAddress(new[]
            {
                (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value
            });
        }

        // ------------------------------------------------------------------ virtual IPs

        /// <summary>
        /// Adds a secondary IPv4 address to an adapter through iphlpapi. Runtime only (gone at reboot) and,
        /// unlike "netsh interface ipv4 add address", it leaves the adapter's DHCP configuration untouched.
        /// SkipAsSource keeps Windows from using the bridge address as the source of its own outbound traffic.
        /// </summary>
        public static CommandResult AddAddress(AdapterInfo adapter, IPAddress ip, IPAddress mask)
        {
            if (adapter == null) return new CommandResult { ExitCode = -1, Output = "No adapter." };
            int prefix = new IpEntry { Address = ip, Mask = mask }.PrefixLength;
            if (prefix <= 0 || prefix > 32) prefix = 24;
            int err;
            try
            {
                err = IpHelperApi.AddAddress(adapter.Index, ip, prefix, true);
            }
            catch (Exception ex)
            {
                return new CommandResult { ExitCode = -3, Output = ex.Message };
            }
            if (err == IpHelperApi.ERROR_OBJECT_ALREADY_EXISTS) err = IpHelperApi.ERROR_SUCCESS;
            return new CommandResult { ExitCode = err, Output = IpHelperApi.Describe(err) };
        }

        public static CommandResult RemoveAddress(int interfaceIndex, IPAddress ip)
        {
            int err;
            try
            {
                err = IpHelperApi.RemoveAddress(interfaceIndex, ip);
            }
            catch (Exception ex)
            {
                return new CommandResult { ExitCode = -3, Output = ex.Message };
            }
            if (err == IpHelperApi.ERROR_NOT_FOUND) err = IpHelperApi.ERROR_SUCCESS;
            return new CommandResult { ExitCode = err, Output = IpHelperApi.Describe(err) };
        }

        /// <summary>
        /// Network repair: puts an adapter back on DHCP for both its address and DNS servers.
        /// (Earlier versions used netsh to add the virtual address, which silently switched DHCP adapters to static.)
        /// </summary>
        public static CommandResult SetDhcp(string interfaceName)
        {
            CommandResult a = Run("netsh.exe", "interface ipv4 set address name=\"" + interfaceName + "\" source=dhcp", 20000);
            CommandResult d = Run("netsh.exe", "interface ipv4 set dnsservers name=\"" + interfaceName + "\" source=dhcp", 20000);
            string text = ("address: " + (a.Success ? "DHCP enabled" : a.OutputOneLine) + Environment.NewLine +
                           "DNS: " + (d.Success ? "DHCP enabled" : d.OutputOneLine)).Trim();
            // netsh returns 1 with "DHCP is already enabled on this interface" – treat that as success.
            bool okA = a.Success || a.Output.IndexOf("already enabled", StringComparison.OrdinalIgnoreCase) >= 0;
            bool okD = d.Success || d.Output.IndexOf("already", StringComparison.OrdinalIgnoreCase) >= 0;
            return new CommandResult { ExitCode = okA && okD ? 0 : 1, Output = text };
        }

        /// <summary>
        /// After an address is added, Windows may run duplicate-address detection and the address is "tentative"
        /// for up to a second or two. Binding a socket during that window fails with WSAEADDRNOTAVAIL, so wait it out.
        /// </summary>
        public static bool WaitForAddressUsable(IPAddress ip, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                DuplicateAddressDetectionState? state = GetDadState(ip);
                if (state.HasValue)
                {
                    if (state.Value == DuplicateAddressDetectionState.Preferred || state.Value == DuplicateAddressDetectionState.Deprecated)
                        return true;
                    if (state.Value == DuplicateAddressDetectionState.Duplicate || state.Value == DuplicateAddressDetectionState.Invalid)
                        return false;
                }
                Thread.Sleep(150);
            }
            // Unknown state after the timeout: let the bind attempt decide.
            return GetDadState(ip).HasValue;
        }

        public static DuplicateAddressDetectionState? GetDadState(IPAddress ip)
        {
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                    foreach (UnicastIPAddressInformation u in nic.GetIPProperties().UnicastAddresses)
                        if (u.Address.Equals(ip)) return u.DuplicateAddressDetectionState;
            }
            catch { }
            return null;
        }

        /// <summary>Pings the address before we claim it. A reply means another device already owns it.</summary>
        public static bool IsAddressInUseOnLan(IPAddress ip, int timeoutMs)
        {
            try
            {
                using (var ping = new Ping())
                {
                    PingReply reply = ping.Send(ip, timeoutMs);
                    return reply != null && reply.Status == IPStatus.Success;
                }
            }
            catch
            {
                return false;
            }
        }

        // ------------------------------------------------------------------ firewall

        public static CommandResult AddFirewallRule(string exePath)
        {
            RemoveFirewallRule(); // avoid piling up duplicates on every start
            string args = string.Format(CultureInfo.InvariantCulture,
                "advfirewall firewall add rule name=\"{0}\" dir=in action=allow program=\"{1}\" enable=yes profile=any description=\"Allows LAN devices to reach the printer bridge (RAW/9100).\"",
                FirewallRuleName, exePath);
            return Run("netsh.exe", args, 15000);
        }

        public static CommandResult RemoveFirewallRule()
        {
            return Run("netsh.exe", "advfirewall firewall delete rule name=\"" + FirewallRuleName + "\"", 15000);
        }

        public static bool FirewallRuleExists()
        {
            CommandResult r = Run("netsh.exe", "advfirewall firewall show rule name=\"" + FirewallRuleName + "\"", 15000);
            return r.Success && r.Output.IndexOf(FirewallRuleName, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ------------------------------------------------------------------ process helper

        public static CommandResult Run(string fileName, string arguments, int timeoutMs)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            try
            {
                psi.StandardOutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
                psi.StandardErrorEncoding = psi.StandardOutputEncoding;
            }
            catch { /* fall back to default encoding */ }

            try
            {
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return new CommandResult { ExitCode = -1, Output = "Failed to start " + fileName };
                    var stdout = p.StandardOutput.ReadToEndAsync();
                    var stderr = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        return new CommandResult { ExitCode = -2, Output = fileName + " timed out." };
                    }
                    p.WaitForExit(); // flush async readers
                    string text = (stdout.Result ?? "") + (string.IsNullOrWhiteSpace(stderr.Result) ? "" : Environment.NewLine + stderr.Result);
                    return new CommandResult { ExitCode = p.ExitCode, Output = text.Trim() };
                }
            }
            catch (Exception ex)
            {
                return new CommandResult { ExitCode = -3, Output = ex.Message };
            }
        }
    }
}
