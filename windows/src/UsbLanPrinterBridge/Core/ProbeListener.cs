using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace UsbLanPrinterBridge.Core
{
    /// <summary>
    /// Diagnostic net. Binds the other ports printing software commonly tries and records anything that arrives,
    /// including the bytes.
    ///
    /// This exists to answer one question: when an app says it cannot find the printer at an address, what is it
    /// actually doing? If it probes LPR on 515, or IPP on 631, or sends a discovery datagram, the bridge would
    /// otherwise be silent and the attempt would leave no trace at all. With this running, the attempt shows up
    /// in the log with a hex dump, which tells us which protocol to answer.
    ///
    /// Ports already served by the bridge are skipped, so this never competes with real printing.
    /// </summary>
    public sealed class ProbeListener
    {
        /// <summary>TCP ports worth watching: LPR, IPP, alternate JetDirect ports, and common HTTP alternates.</summary>
        public static readonly int[] DefaultTcpPorts = { 515, 631, 9101, 9102, 8000, 8008, 8080, 8443, 9200 };

        /// <summary>UDP ports used for printer discovery: Epson's own protocol, SNMP, and the SLP/WS-Discovery pair.</summary>
        public static readonly int[] DefaultUdpPorts = { 3289, 161, 427, 3702 };

        private readonly List<TcpListener> _tcp = new List<TcpListener>();
        private readonly List<UdpClient> _udp = new List<UdpClient>();
        private CancellationTokenSource _cts;
        private readonly object _gate = new object();

        public bool IsRunning { get; private set; }
        public IPAddress Address { get; private set; }

        public event Action<string> Log;

        private void Emit(string message)
        {
            Action<string> h = Log;
            if (h != null) { try { h(message); } catch { } }
        }

        /// <summary>Starts watching. Ports in <paramref name="skipTcpPorts"/> are already in use by the bridge.</summary>
        public void Start(IPAddress address, IEnumerable<int> skipTcpPorts)
        {
            lock (_gate)
            {
                if (IsRunning) return;
                Address = address;
                _cts = new CancellationTokenSource();
                CancellationToken token = _cts.Token;

                var skip = new HashSet<int>(skipTcpPorts ?? new int[0]);
                int tcpOk = 0, udpOk = 0;

                foreach (int port in DefaultTcpPorts)
                {
                    if (skip.Contains(port)) continue;
                    try
                    {
                        var listener = new TcpListener(address, port);
                        listener.Start(8);
                        _tcp.Add(listener);
                        tcpOk++;
                        TcpListener captured = listener;
                        int capturedPort = port;
                        Task.Run(() => AcceptLoop(captured, capturedPort, token));
                    }
                    catch
                    {
                        // port owned by something else; nothing to report
                    }
                }

                foreach (int port in DefaultUdpPorts)
                {
                    try
                    {
                        var client = new UdpClient();
                        client.ExclusiveAddressUse = false;
                        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                        client.Client.Bind(new IPEndPoint(address, port));
                        _udp.Add(client);
                        udpOk++;
                        UdpClient captured = client;
                        int capturedPort = port;
                        Task.Run(() => ReceiveLoop(captured, capturedPort, token));
                    }
                    catch
                    {
                    }
                }

                IsRunning = true;
                Emit("Connection tracing on " + address + ": watching " + tcpOk + " TCP and " + udpOk + " UDP port(s). "
                     + "Try adding the printer on the other device now; anything it sends will appear here.");
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                if (!IsRunning) return;
                IsRunning = false;
                try { if (_cts != null) _cts.Cancel(); } catch { }
                foreach (TcpListener l in _tcp) { try { l.Stop(); } catch { } }
                foreach (UdpClient u in _udp) { try { u.Close(); } catch { } }
                _tcp.Clear();
                _udp.Clear();
                _cts = null;
                Emit("Connection tracing stopped.");
            }
        }

        private async Task AcceptLoop(TcpListener listener, int port, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch { break; }

                string remote = "?";
                try { remote = client.Client.RemoteEndPoint.ToString(); } catch { }
                Emit("TRACE: " + remote + " connected to port " + port + " (" + Describe(port) + ").");

                TcpClient captured = client;
                int capturedPort = port;
                string capturedRemote = remote;
                var _ = Task.Run(async () =>
                {
                    try
                    {
                        using (captured)
                        using (NetworkStream stream = captured.GetStream())
                        {
                            captured.ReceiveTimeout = 4000;
                            var buffer = new byte[4096];
                            var readTask = stream.ReadAsync(buffer, 0, buffer.Length, ct);
                            Task done = await Task.WhenAny(readTask, Task.Delay(4000, ct)).ConfigureAwait(false);
                            if (done == readTask)
                            {
                                int n = await readTask.ConfigureAwait(false);
                                if (n > 0)
                                {
                                    Emit("TRACE: " + capturedRemote + " sent " + n + " byte(s) to port " + capturedPort + ":"
                                         + Environment.NewLine + PrintHistory.HexDump(buffer, n, 256));
                                }
                                else Emit("TRACE: " + capturedRemote + " closed port " + capturedPort + " without sending anything.");
                            }
                            else
                            {
                                Emit("TRACE: " + capturedRemote + " opened port " + capturedPort + " and sent nothing within 4 s. "
                                     + "That usually means it was only checking whether the port is open.");
                            }
                        }
                    }
                    catch { }
                });
            }
        }

        private async Task ReceiveLoop(UdpClient client, int port, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try { result = await client.ReceiveAsync().ConfigureAwait(false); }
                catch { break; }
                byte[] data = result.Buffer;
                Emit("TRACE: datagram from " + result.RemoteEndPoint + " on UDP " + port + " (" + Describe(port) + "), "
                     + data.Length + " byte(s):" + Environment.NewLine + PrintHistory.HexDump(data, data.Length, 192));
            }
        }

        private static string Describe(int port)
        {
            switch (port)
            {
                case 515: return "LPR/LPD printing";
                case 631: return "IPP printing";
                case 9101:
                case 9102: return "alternate JetDirect";
                case 8000:
                case 8080: return "HTTP alternate";
                case 8008: return "ePOS-Device";
                case 8443: return "HTTPS alternate";
                case 3289: return "Epson device discovery";
                case 161: return "SNMP";
                case 427: return "SLP discovery";
                case 3702: return "WS-Discovery";
                default: return "unassigned";
            }
        }
    }
}
