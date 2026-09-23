using System;
using System.Net;

namespace UsbLanPrinterBridge.Core
{
    /// <summary>
    /// Runs the scale channel: opens the reader (serial or TCP), publishes GET /scale, and, when the endpoint is
    /// asked to live on its own LAN address, leases that address through the <see cref="BridgeManager"/> exactly as a
    /// printer mapping does. Every start/stop and connection change is written to <see cref="ScaleLog"/> and the log.
    /// </summary>
    public sealed class ScaleService
    {
        private readonly BridgeManager _manager;   // optional: needed only for the firewall rule and a virtual IP
        private readonly object _gate = new object();

        private ScaleReader _reader;
        private ScaleServer _server;
        private IPAddress _leased;

        public ScaleService(BridgeManager manager) { _manager = manager; }

        public bool Running { get; private set; }
        public ScaleReader Reader { get { return _reader; } }
        public ScaleServer Server { get { return _server; } }

        /// <summary>The /scale URLs the endpoint is reachable at, or empty.</summary>
        public string Endpoint
        {
            get
            {
                ScaleServer s = _server;
                return (s != null && s.IsRunning && s.LocalEndPoint != null) ? "http://" + s.LocalEndPoint + "/scale" : "";
            }
        }

        public static string Validate(ScaleConfig cfg)
        {
            if (cfg == null) return "No scale configuration.";
            if (cfg.IsTcp)
            {
                if (string.IsNullOrWhiteSpace(cfg.Host)) return "No network-scale host set.";
                if (cfg.TcpPort < 1 || cfg.TcpPort > 65535) return "The network-scale port must be between 1 and 65535.";
            }
            else
            {
                if (string.IsNullOrWhiteSpace(cfg.PortName)) return "No serial (COM) port selected for the scale.";
                if (cfg.Baud < 300 || cfg.Baud > 921600) return "The scale baud rate looks wrong (" + cfg.Baud + ").";
            }
            if (cfg.HttpPort < 1 || cfg.HttpPort > 65535) return "The /scale port must be between 1 and 65535.";
            IPAddress ip;
            if (!TryBindAddress(cfg.BindAddress, out ip)) return "\"" + cfg.BindAddress + "\" is not a valid IPv4 address for the scale endpoint.";
            return null;
        }

        public static bool TryBindAddress(string text, out IPAddress ip)
        {
            ip = null;
            if (string.IsNullOrWhiteSpace(text) || text.Trim() == "*" || text.Trim().Equals("any", StringComparison.OrdinalIgnoreCase))
            {
                ip = IPAddress.Any; return true;
            }
            if (!IPAddress.TryParse(text.Trim(), out ip)) return false;
            if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) { ip = null; return false; }
            return true;
        }

        public StartOutcome Start(ScaleConfig cfg)
        {
            string problem = Validate(cfg);
            if (problem != null) return StartOutcome.Fail(problem);

            lock (_gate)
            {
                if (Running) Stop();

                // 1. Reader: a fresh source each (re)connect so it can recover after unplug.
                Func<IScaleSource> factory;
                if (cfg.IsTcp)
                {
                    string host = cfg.Host.Trim(); int port = cfg.TcpPort;
                    factory = () => new TcpScaleSource(host, port);
                }
                else
                {
                    string com = cfg.PortName.Trim(); int baud = cfg.Baud, data = cfg.DataBits;
                    var parity = SerialScaleSource.ParseParity(cfg.Parity);
                    var stop = SerialScaleSource.ParseStopBits(cfg.StopBits);
                    factory = () => new SerialScaleSource(com, baud, data, parity, stop);
                }

                var reader = new ScaleReader(factory, cfg.PollBytes(), cfg.PollIntervalMs);

                // 2. Endpoint address: lease a virtual IP if it is not one this PC already has.
                IPAddress ip;
                TryBindAddress(cfg.BindAddress, out ip);
                if (_manager != null)
                {
                    _manager.EnsureFirewallRule();
                    StartOutcome lease = _manager.LeaseAddress(ip, cfg.Adapter);
                    if (!lease.Success) return lease;
                    if (!ip.Equals(IPAddress.Any) && !NetworkHelper.IsLocalAddress(ip)) _leased = ip;
                    if (!string.IsNullOrEmpty(lease.Message)) Logger.Info(lease.Message);
                }

                string displayUnit = cfg.DisplayUnit;
                Func<ScaleReading> current = string.IsNullOrEmpty(displayUnit)
                    ? (Func<ScaleReading>)(() => reader.Current)
                    : () => reader.Current.InUnit(displayUnit);
                var server = new ScaleServer(current, reader.RecentRaw, () => reader.Status, () => reader.Connected);
                server.Log += m => Logger.Info("[scale http] " + m);

                try
                {
                    server.Start(ip, cfg.HttpPort);
                }
                catch (Exception ex)
                {
                    if (_leased != null && _manager != null) { _manager.ReleaseAddress(_leased); _leased = null; }
                    ScaleLog.Error("Endpoint failed", "Could not bind " + ip + ":" + cfg.HttpPort + " — " + ex.Message);
                    return StartOutcome.Fail("The /scale endpoint could not start on " + ip + ":" + cfg.HttpPort + ": " + ex.Message);
                }

                reader.Start();

                _reader = reader;
                _server = server;
                Running = true;

                string where = cfg.IsTcp ? (cfg.Host + ":" + cfg.TcpPort) : (cfg.PortName + " @ " + cfg.Baud);
                Logger.Info("Scale started: reading " + where + ", published at " + Endpoint);
                ScaleLog.Info("Started", "reading " + where + ", endpoint " + Endpoint);
                return StartOutcome.Ok("Scale reading " + where + ", published at " + Endpoint);
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                if (_server != null) { try { _server.Stop(); } catch { } _server = null; }
                if (_reader != null) { try { _reader.Stop(); } catch { } _reader = null; }
                if (_leased != null && _manager != null) { try { _manager.ReleaseAddress(_leased); } catch { } _leased = null; }
                if (Running) { Logger.Info("Scale stopped."); ScaleLog.Info("Stopped", ""); }
                Running = false;
            }
        }
    }
}
