using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace UsbLanPrinterBridge.Core
{
    public sealed class StartOutcome
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public static StartOutcome Ok(string msg) { return new StartOutcome { Success = true, Message = msg }; }
        public static StartOutcome Fail(string msg) { return new StartOutcome { Success = false, Message = msg }; }
    }

    /// <summary>
    /// Owns the running listeners, the virtual IP addresses the app added, and the firewall rule.
    /// All public methods are synchronous and may block for a few seconds (netsh, duplicate address detection);
    /// call them from a worker thread, never from the UI thread.
    /// </summary>
    public sealed class BridgeManager
    {
        private sealed class VirtualIpLease
        {
            public IPAddress Address;
            public string InterfaceName;
            public int InterfaceIndex;
            public int RefCount;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<string, BridgeListener> _listeners = new Dictionary<string, BridgeListener>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<HttpBridgeServer>> _eposServers = new Dictionary<string, List<HttpBridgeServer>>(StringComparer.Ordinal);
        private readonly Dictionary<string, VirtualIpLease> _leases = new Dictionary<string, VirtualIpLease>(StringComparer.Ordinal);
        private bool _firewallEnsured;

        /// <summary>When false, ePOS HTTP/HTTPS endpoints are not started even if a mapping enables them (used by tests).</summary>
        public bool EnableEposServers = true;

        public BridgeManager()
        {
            JobIdleTimeoutMs = 1500;
            AutoFirewallRule = true;
            IsElevated = DetectElevation();
            TargetFactory = m => new SpoolerPrintTarget(m.PrinterName);
            ExecutablePath = ResolveExePath();
        }

        public int JobIdleTimeoutMs { get; set; }
        public bool AutoFirewallRule { get; set; }
        public bool IsElevated { get; set; }
        public string ExecutablePath { get; set; }

        /// <summary>Creates the print target for a mapping. Replaced by the test harness with an in-memory target.</summary>
        public Func<MappingConfig, IPrintTarget> TargetFactory { get; set; }

        /// <summary>Raised whenever a listener changes state (from worker threads).</summary>
        public event Action<BridgeListener> ListenerChanged;

        public static bool DetectElevation()
        {
            try
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        private static string ResolveExePath()
        {
            try { return Process.GetCurrentProcess().MainModule.FileName; }
            catch { return ""; }
        }

        public BridgeListener GetListener(MappingConfig mapping)
        {
            lock (_gate)
            {
                BridgeListener l;
                return mapping != null && _listeners.TryGetValue(mapping.Id, out l) ? l : null;
            }
        }

        public bool IsRunning(MappingConfig mapping)
        {
            BridgeListener l = GetListener(mapping);
            return l != null && l.IsRunning;
        }

        public int RunningCount
        {
            get { lock (_gate) return _listeners.Values.Count(l => l.IsRunning); }
        }

        public IEnumerable<BridgeListener> Listeners
        {
            get { lock (_gate) return _listeners.Values.ToList(); }
        }

        // ------------------------------------------------------------------ validation

        public static string Validate(MappingConfig m)
        {
            if (m == null) return "No mapping.";
            if (string.IsNullOrWhiteSpace(m.PrinterName)) return "No printer selected.";
            IPAddress ip;
            if (!TryParseBindAddress(m.BindAddress, out ip)) return "\"" + m.BindAddress + "\" is not a valid IPv4 address.";
            if (m.Port < 1 || m.Port > 65535) return "Port must be between 1 and 65535.";
            return null;
        }

        public static bool TryParseBindAddress(string text, out IPAddress ip)
        {
            ip = null;
            if (string.IsNullOrWhiteSpace(text) || text.Trim() == "*" || text.Trim().Equals("any", StringComparison.OrdinalIgnoreCase))
            {
                ip = IPAddress.Any;
                return true;
            }
            if (!IPAddress.TryParse(text.Trim(), out ip)) return false;
            if (ip.AddressFamily != AddressFamily.InterNetwork) { ip = null; return false; }
            return true;
        }

        // ------------------------------------------------------------------ start / stop

        public StartOutcome StartMapping(MappingConfig mapping)
        {
            string problem = Validate(mapping);
            if (problem != null) return StartOutcome.Fail(problem);
            if (IsRunning(mapping)) return StartOutcome.Ok("Already running.");

            IPAddress ip;
            TryParseBindAddress(mapping.BindAddress, out ip);
            string label = mapping.PrinterName + " @ " + ip + ":" + mapping.Port;

            // 1. Make sure the address exists on this machine.
            bool leased = false;
            if (!ip.Equals(IPAddress.Any) && !NetworkHelper.IsLocalAddress(ip))
            {
                StartOutcome lease = AcquireVirtualIp(ip, mapping.Adapter);
                if (!lease.Success) return lease;
                leased = true;
                if (!string.IsNullOrEmpty(lease.Message)) Logger.Info(lease.Message);
            }
            else if (ip.Equals(IPAddress.Any))
            {
                Logger.Info(label + ": listening on all addresses of this PC.");
            }
            else
            {
                Logger.Info(label + ": " + ip + " is already an address of this PC; using it directly.");
            }

            // 2. Firewall.
            EnsureFirewallRule();

            // 3. Bind.
            BridgeListener listener;
            lock (_gate)
            {
                if (!_listeners.TryGetValue(mapping.Id, out listener))
                {
                    listener = new BridgeListener(mapping, TargetFactory(mapping));
                    listener.Log += OnListenerLog;
                    listener.StateChanged += OnListenerState;
                    _listeners[mapping.Id] = listener;
                }
                else if (!listener.TargetMatches(mapping))
                {
                    // The row was pointed at a different printer while this bridge was stopped. The cached listener
                    // still holds a target built for the previous printer, so without rebuilding it here the grid
                    // would show the new printer while every job kept going to the old one.
                    string previous = listener.TargetPrinterName;
                    listener.Retarget(mapping, TargetFactory(mapping));
                    Logger.Info("Mapping " + mapping.EndpointText + " now prints to \"" + mapping.PrinterName
                                + "\" instead of \"" + previous + "\".");
                }
            }
            listener.JobIdleTimeoutMs = JobIdleTimeoutMs;

            try
            {
                listener.Start(ip, mapping.Port);
            }
            catch (SocketException ex)
            {
                if (leased) ReleaseVirtualIp(ip);
                string why = DescribeBindError(ex, ip, mapping.Port);
                Logger.Error(label + ": " + why);
                listener.MarkError(why);
                return StartOutcome.Fail(why);
            }
            catch (Exception ex)
            {
                if (leased) ReleaseVirtualIp(ip);
                Logger.Error(label + ": " + ex.Message);
                listener.MarkError(ex.Message);
                return StartOutcome.Fail(ex.Message);
            }

            Logger.Info("Bridge started: " + label + "  →  \"" + mapping.PrinterName + "\"");

            // The ePOS endpoints print through the same per-printer NO CUT filter as the raw port.
            if (EnableEposServers && mapping.EposEnabled)
            {
                BridgeListener captured = listener;
                StartEposServers(mapping, ip, new NoCutPrintTarget(listener.Target, () => captured.Mapping.NoCut));
            }

            return StartOutcome.Ok("Listening on " + ip + ":" + mapping.Port);
        }

        /// <summary>Alternate ports served in addition to the configured ones, so clients using either convention connect.</summary>
        private static readonly int[] AlternateHttpPorts = { 8008 };
        private static readonly int[] AlternateHttpsPorts = { 8043 };

        private void StartEposServers(MappingConfig mapping, IPAddress ip, IPrintTarget target)
        {
            var servers = new List<HttpBridgeServer>();

            // Certificate covering every local address, so HTTPS works on whichever address the client uses.
            X509Certificate2 cert = null;
            try
            {
                var addresses = new List<IPAddress>(NetworkHelper.GetHostIPv4Addresses());
                if (!addresses.Contains(ip)) addresses.Add(ip);
                cert = SelfSignedCertificate.GetOrCreate(addresses);
            }
            catch (Exception ex)
            {
                Logger.Warn("Could not prepare the HTTPS certificate: " + ex.Message + " (the HTTPS ePOS endpoints will be skipped).");
            }

            foreach (int port in Distinct(mapping.EposHttpPort, AlternateHttpPorts))
                StartOneEposServer(servers, mapping, ip, target, port, null);

            if (cert != null)
                foreach (int port in Distinct(mapping.EposHttpsPort, AlternateHttpsPorts))
                    StartOneEposServer(servers, mapping, ip, target, port, cert);

            lock (_gate) _eposServers[mapping.Id] = servers;

            if (servers.Count == 0)
                Logger.Warn("No ePOS endpoint could be started for " + ip + ". Web/Android POS apps will not find this printer.");
        }

        private static List<int> Distinct(int primary, int[] alternates)
        {
            var list = new List<int> { primary };
            foreach (int p in alternates) if (!list.Contains(p)) list.Add(p);
            return list;
        }

        private void StartOneEposServer(List<HttpBridgeServer> servers, MappingConfig mapping, IPAddress ip, IPrintTarget target, int port, X509Certificate2 cert)
        {
            string scheme = cert == null ? "http" : "https";
            try
            {
                var server = new HttpBridgeServer(target, cert, () => mapping.EposDeviceId);
                server.Log += msg => Logger.Info("[ePOS " + scheme + " " + ip + ":" + port + "] " + msg);
                server.Start(ip, port);
                servers.Add(server);
                Logger.Info("ePOS-Print ready: " + scheme + "://" + ip + (IsDefaultPort(scheme, port) ? "" : ":" + port) + "/cgi-bin/epos/service.cgi"
                            + (cert != null ? "  (accept/install the certificate on the client the first time)" : ""));
            }
            catch (Exception ex)
            {
                Logger.Warn("ePOS " + scheme + " endpoint on " + ip + ":" + port + " could not start: " + ex.Message
                            + (port == 80 || port == 443 ? "  (another program may own this port; the alternate port is still served)" : ""));
            }
        }

        private static bool IsDefaultPort(string scheme, int port)
        {
            return (scheme == "http" && port == 80) || (scheme == "https" && port == 443);
        }

        private void StopEposServers(string mappingId)
        {
            List<HttpBridgeServer> servers;
            lock (_gate)
            {
                if (!_eposServers.TryGetValue(mappingId, out servers)) return;
                _eposServers.Remove(mappingId);
            }
            foreach (HttpBridgeServer s in servers) { try { s.Stop(); } catch { } }
        }

        /// <summary>Human-readable ePOS endpoint URLs for a running mapping (empty if none).</summary>
        public List<string> EposEndpoints(MappingConfig mapping)
        {
            var result = new List<string>();
            if (mapping == null) return result;
            List<HttpBridgeServer> servers;
            lock (_gate) { if (!_eposServers.TryGetValue(mapping.Id, out servers)) return result; servers = servers.ToList(); }
            foreach (HttpBridgeServer s in servers)
                if (s.IsRunning && s.LocalEndPoint != null)
                    result.Add(s.Scheme + "://" + s.LocalEndPoint + "/cgi-bin/epos/service.cgi");
            return result;
        }

        public bool HasRunningEposServers(MappingConfig mapping)
        {
            if (mapping == null) return false;
            List<HttpBridgeServer> servers;
            lock (_gate) { if (!_eposServers.TryGetValue(mapping.Id, out servers)) return false; return servers.Exists(s => s.IsRunning); }
        }

        public void StopMapping(MappingConfig mapping)
        {
            if (mapping != null) StopEposServers(mapping.Id);
            BridgeListener listener = GetListener(mapping);
            if (listener == null) return;
            bool wasRunning = listener.IsRunning;
            IPEndPoint ep = listener.LocalEndPoint;
            listener.Stop();
            if (wasRunning && ep != null)
            {
                ReleaseVirtualIp(ep.Address);
                Logger.Info("Bridge stopped: " + mapping.PrinterName + " @ " + ep);
            }
        }

        public void StopAll()
        {
            List<BridgeListener> all;
            lock (_gate) all = _listeners.Values.ToList();
            foreach (BridgeListener l in all)
                StopMapping(l.Mapping);
            ReleaseAllVirtualIps();
        }

        public void Forget(MappingConfig mapping)
        {
            StopMapping(mapping);
            lock (_gate) _listeners.Remove(mapping.Id);
        }

        private static string DescribeBindError(SocketException ex, IPAddress ip, int port)
        {
            switch (ex.SocketErrorCode)
            {
                case SocketError.AddressAlreadyInUse:
                    return "Port " + port + " on " + ip + " is already used by another program (error 10048).";
                case SocketError.AddressNotAvailable:
                    return "Address " + ip + " is not available on this PC (error 10049). Wait a moment and retry, or choose another address.";
                case SocketError.AccessDenied:
                    return "Windows refused port " + port + " (error 10013). It may be reserved (check: netsh int ipv4 show excludedportrange protocol=tcp) or blocked by security software.";
                default:
                    return "Cannot listen on " + ip + ":" + port + ": " + ex.Message + " (error " + (int)ex.SocketErrorCode + ").";
            }
        }

        // ------------------------------------------------------------------ virtual IPs

        private StartOutcome AcquireVirtualIp(IPAddress ip, string preferredAdapter)
        {
            string key = ip.ToString();
            lock (_gate)
            {
                VirtualIpLease existing;
                if (_leases.TryGetValue(key, out existing))
                {
                    existing.RefCount++;
                    return StartOutcome.Ok(null);
                }
            }

            if (!IsElevated)
                return StartOutcome.Fail("Adding the virtual address " + ip + " requires administrator rights. Restart the app as administrator, or use one of this PC's existing addresses (or 0.0.0.0).");

            List<AdapterInfo> adapters = NetworkHelper.GetAdapters();
            if (adapters.Count == 0)
                return StartOutcome.Fail("No connected network adapter found. Connect to the network and try again.");

            AdapterInfo adapter = NetworkHelper.PickAdapter(ip, preferredAdapter, adapters);
            if (!string.IsNullOrWhiteSpace(preferredAdapter) && !string.Equals(adapter.Name, preferredAdapter, StringComparison.OrdinalIgnoreCase))
                Logger.Warn("Adapter \"" + preferredAdapter + "\" is not connected; using \"" + adapter.Name + "\" instead.");

            IPAddress mask = NetworkHelper.GuessMask(adapter, ip);

            // PickAdapter returns the adapter whose subnet contains the address when one does, so reaching here
            // with no match means no connected adapter can carry it. The bridge would start cleanly and then be
            // silently unreachable, which is worse than refusing. This bites hardest on a phone hotspot, where
            // the subnet is a /28 rather than the /24 people expect.
            if (!adapter.Addresses.Any(a => a.Contains(ip)))
            {
                string suggestion = NetworkHelper.SuggestInSubnet(adapter.Addresses.FirstOrDefault(), new string[0]);
                return StartOutcome.Fail(
                    ip + " is not on any network this PC is connected to, so no other device could reach it. "
                    + "This PC: " + NetworkHelper.DescribeUsableRanges() + ". "
                    + (suggestion != null ? "Try " + suggestion + " instead, " : "Use an address in that range, ")
                    + "or set the address to 0.0.0.0 to listen on every address this PC has. "
                    + "A phone hotspot is the usual cause: it uses a small subnet such as 172.20.10.0/28, "
                    + "so an address like 192.168.1.201 cannot work there.");
            }

            if (NetworkHelper.IsAddressInUseOnLan(ip, 400))
                Logger.Warn(ip + " already answers to ping — another device may be using this address. Pick a different one if printing fails.");

            Logger.Info("Adding " + ip + "/" + new IpEntry { Address = ip, Mask = mask }.PrefixLength + " to adapter \"" + adapter.Name + "\" (DHCP settings are left untouched)...");
            CommandResult add = NetworkHelper.AddAddress(adapter, ip, mask);
            if (!add.Success && !NetworkHelper.IsLocalAddress(ip))
            {
                return StartOutcome.Fail("Windows could not add " + ip + " to \"" + adapter.Name + "\": " + add.OutputOneLine
                    + "  (Tip: bind to one of this PC's existing addresses or 0.0.0.0 to avoid virtual addresses altogether.)");
            }

            if (!NetworkHelper.WaitForAddressUsable(ip, 6000))
            {
                NetworkHelper.RemoveAddress(adapter.Index, ip);
                return StartOutcome.Fail(ip + " was rejected by Windows (duplicate address on the network or adapter went down). Choose another address.");
            }

            lock (_gate)
            {
                _leases[key] = new VirtualIpLease { Address = ip, InterfaceName = adapter.Name, InterfaceIndex = adapter.Index, RefCount = 1 };
            }
            return StartOutcome.Ok("Virtual address " + ip + " is active on \"" + adapter.Name + "\" alongside its normal address.");
        }

        public bool HasVirtualIps
        {
            get { lock (_gate) return _leases.Count > 0; }
        }

        /// <summary>
        /// Reserves a LAN address for a non-printer device (the scale) so it can have its own IP, using the same
        /// virtual-address machinery as the printers. Any/loopback/existing addresses need no lease and return Ok.
        /// </summary>
        public StartOutcome LeaseAddress(IPAddress ip, string preferredAdapter)
        {
            if (ip == null || ip.Equals(IPAddress.Any) || NetworkHelper.IsLocalAddress(ip)) return StartOutcome.Ok(null);
            return AcquireVirtualIp(ip, preferredAdapter);
        }

        /// <summary>Releases an address taken with <see cref="LeaseAddress"/>. Safe to call for Any/local addresses.</summary>
        public void ReleaseAddress(IPAddress ip)
        {
            if (ip == null || ip.Equals(IPAddress.Any) || NetworkHelper.IsLocalAddress(ip)) return;
            ReleaseVirtualIp(ip);
        }

        /// <summary>
        /// Re-adds virtual addresses that disappeared (adapter reconnected, driver reset, ...). The listener socket stays
        /// bound to the address in the meantime, so once the address is back, printing resumes. Call from a worker thread.
        /// </summary>
        public int RepairVirtualIps()
        {
            List<VirtualIpLease> leases;
            lock (_gate) leases = _leases.Values.ToList();
            int repaired = 0;
            foreach (VirtualIpLease lease in leases)
            {
                if (NetworkHelper.IsLocalAddress(lease.Address)) continue;

                List<AdapterInfo> adapters = NetworkHelper.GetAdapters();
                AdapterInfo adapter = adapters.FirstOrDefault(a => a.Index == lease.InterfaceIndex)
                    ?? adapters.FirstOrDefault(a => string.Equals(a.Name, lease.InterfaceName, StringComparison.OrdinalIgnoreCase));
                if (adapter == null)
                {
                    Logger.Warn("Virtual address " + lease.Address + " is gone and adapter \"" + lease.InterfaceName + "\" is not connected. Will keep retrying.");
                    continue;
                }

                IPAddress mask = NetworkHelper.GuessMask(adapter, lease.Address);
                CommandResult r = NetworkHelper.AddAddress(adapter, lease.Address, mask);
                if (r.Success && NetworkHelper.WaitForAddressUsable(lease.Address, 4000))
                {
                    lock (_gate)
                    {
                        lease.InterfaceIndex = adapter.Index;
                        lease.InterfaceName = adapter.Name;
                    }
                    Logger.Info("Re-added virtual address " + lease.Address + " on \"" + adapter.Name + "\" (it had disappeared).");
                    repaired++;
                }
                else
                {
                    Logger.Warn("Could not re-add " + lease.Address + " on \"" + adapter.Name + "\": " + r.OutputOneLine);
                }
            }
            return repaired;
        }

        private void ReleaseVirtualIp(IPAddress ip)
        {
            if (ip == null) return;
            VirtualIpLease lease;
            lock (_gate)
            {
                if (!_leases.TryGetValue(ip.ToString(), out lease)) return;
                lease.RefCount--;
                if (lease.RefCount > 0) return;
                _leases.Remove(ip.ToString());
            }
            CommandResult r = NetworkHelper.RemoveAddress(lease.InterfaceIndex, lease.Address);
            if (r.Success) Logger.Info("Removed virtual address " + lease.Address + " from \"" + lease.InterfaceName + "\".");
            else Logger.Warn("Could not remove " + lease.Address + " from \"" + lease.InterfaceName + "\": " + r.OutputOneLine);
        }

        /// <summary>Removes every address this process added (called at exit as a safety net).</summary>
        public void ReleaseAllVirtualIps()
        {
            List<VirtualIpLease> leases;
            lock (_gate)
            {
                leases = _leases.Values.ToList();
                _leases.Clear();
            }
            foreach (VirtualIpLease lease in leases)
            {
                CommandResult r = NetworkHelper.RemoveAddress(lease.InterfaceIndex, lease.Address);
                if (r.Success) Logger.Info("Removed virtual address " + lease.Address + " from \"" + lease.InterfaceName + "\".");
                else Logger.Warn("Could not remove " + lease.Address + ": " + r.OutputOneLine);
            }
        }

        // ------------------------------------------------------------------ firewall

        public void EnsureFirewallRule()
        {
            if (!AutoFirewallRule || _firewallEnsured) return;
            _firewallEnsured = true;
            if (!IsElevated)
            {
                Logger.Warn("Not running as administrator: cannot add the Windows Firewall rule. If devices cannot connect, allow \"" + ExecutablePath + "\" through the firewall.");
                return;
            }
            if (string.IsNullOrEmpty(ExecutablePath)) return;
            CommandResult r = NetworkHelper.AddFirewallRule(ExecutablePath);
            if (r.Success) Logger.Info("Windows Firewall rule \"" + NetworkHelper.FirewallRuleName + "\" is in place.");
            else Logger.Warn("Could not add the firewall rule: " + r.OutputOneLine);
        }

        // ------------------------------------------------------------------ events

        private void OnListenerLog(BridgeListener listener, string message)
        {
            Logger.Info("[" + listener.Mapping.EndpointText + "] " + message);
        }

        private void OnListenerState(BridgeListener listener)
        {
            var h = ListenerChanged;
            if (h != null) { try { h(listener); } catch { } }
        }
    }
}
