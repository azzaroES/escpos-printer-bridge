using System;
using System.IO;
using System.IO.Ports;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace UsbLanPrinterBridge.Core
{
    /// <summary>A place a scale's bytes come from: a serial/USB COM port, or a network scale's TCP stream.</summary>
    public interface IScaleSource : IDisposable
    {
        string Describe { get; }
        /// <summary>Opens the source and returns a readable (and, for polling, writable) stream. Throws on failure.</summary>
        Stream Open();
    }

    /// <summary>Reads from a COM port (a serial scale, or a serial scale behind an RS232-to-USB converter).</summary>
    public sealed class SerialScaleSource : IScaleSource
    {
        private readonly string _port;
        private readonly int _baud;
        private readonly int _dataBits;
        private readonly Parity _parity;
        private readonly StopBits _stopBits;
        private SerialPort _sp;

        public SerialScaleSource(string port, int baud, int dataBits, Parity parity, StopBits stopBits)
        {
            _port = port; _baud = baud; _dataBits = dataBits; _parity = parity; _stopBits = stopBits;
        }

        public string Describe { get { return _port + " @ " + _baud + " " + _dataBits + Letter(_parity) + StopText(_stopBits); } }

        public Stream Open()
        {
            var sp = new SerialPort(_port, _baud, _parity, _dataBits, _stopBits);
            sp.ReadTimeout = 400;
            sp.WriteTimeout = 1000;
            sp.Handshake = Handshake.None;
            sp.DtrEnable = true;   // many scales only transmit when DTR/RTS are asserted
            sp.RtsEnable = true;
            sp.Open();
            _sp = sp;
            return sp.BaseStream;
        }

        public void Dispose() { try { if (_sp != null) _sp.Dispose(); } catch { } _sp = null; }

        private static char Letter(Parity p) { return p == Parity.Even ? 'E' : p == Parity.Odd ? 'O' : 'N'; }
        private static string StopText(StopBits s) { return s == StopBits.Two ? "2" : s == StopBits.OnePointFive ? "1.5" : "1"; }

        public static Parity ParseParity(string s)
        {
            if (string.IsNullOrEmpty(s)) return Parity.None;
            switch (s.Trim().ToLowerInvariant())
            {
                case "e": case "even": return Parity.Even;
                case "o": case "odd": return Parity.Odd;
                case "m": case "mark": return Parity.Mark;
                case "s": case "space": return Parity.Space;
                default: return Parity.None;
            }
        }

        public static StopBits ParseStopBits(string s)
        {
            if (string.IsNullOrEmpty(s)) return StopBits.One;
            switch (s.Trim())
            {
                case "2": return StopBits.Two;
                case "1.5": return StopBits.OnePointFive;
                default: return StopBits.One;
            }
        }

        /// <summary>COM ports present on this PC (includes RS232-to-USB adapters, which appear as COMx).</summary>
        public static string[] AvailablePorts()
        {
            try { return SerialPort.GetPortNames(); } catch { return new string[0]; }
        }
    }

    /// <summary>Reads from a network scale that streams its weight over TCP (host:port).</summary>
    public sealed class TcpScaleSource : IScaleSource
    {
        private readonly string _host;
        private readonly int _port;
        private TcpClient _client;

        public TcpScaleSource(string host, int port) { _host = host; _port = port; }

        public string Describe { get { return _host + ":" + _port + " (tcp)"; } }

        public Stream Open()
        {
            var client = new TcpClient();
            var ar = client.BeginConnect(_host, _port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(4000) || !client.Connected)
            {
                try { client.Close(); } catch { }
                throw new IOException("Could not connect to the network scale at " + _host + ":" + _port + " within 4s.");
            }
            client.EndConnect(ar);
            NetworkStream ns = client.GetStream();
            ns.ReadTimeout = 400;
            ns.WriteTimeout = 1000;
            _client = client;
            return ns;
        }

        public void Dispose() { try { if (_client != null) _client.Close(); } catch { } _client = null; }
    }

    /// <summary>
    /// Opens a scale source, reads its line-oriented output on a background thread, parses each line into a
    /// <see cref="ScaleReading"/>, and keeps the latest and the latest STABLE reading for the /scale endpoint.
    /// Auto-reconnects, keeps a ring of the raw lines (to identify an unknown scale), and can poll a scale that
    /// only sends a weight on request. Every connect/disconnect/error is written to <see cref="ScaleLog"/>.
    /// </summary>
    public sealed class ScaleReader
    {
        private readonly Func<IScaleSource> _factory;
        private readonly byte[] _pollCommand;      // null = passive (scale streams on its own)
        private readonly int _pollIntervalMs;
        private readonly int _reconnectDelayMs;
        private readonly int _stableStaleMs;

        private readonly object _gate = new object();
        private readonly string[] _rawRing = new string[20];
        private int _rawPos;

        private Thread _thread;
        private volatile bool _running;
        private volatile ScaleReading _last = ScaleReading.Empty;
        private volatile ScaleReading _lastStable = ScaleReading.Empty;
        private volatile bool _connected;
        private volatile string _status = "stopped";
        private string _lastStableLogged;

        public ScaleReader(Func<IScaleSource> factory, byte[] pollCommand, int pollIntervalMs)
        {
            if (factory == null) throw new ArgumentNullException("factory");
            _factory = factory;
            _pollCommand = (pollCommand != null && pollCommand.Length > 0) ? pollCommand : null;
            _pollIntervalMs = pollIntervalMs > 0 ? pollIntervalMs : 0;
            _reconnectDelayMs = 3000;
            _stableStaleMs = 4000;
        }

        public bool Connected { get { return _connected; } }
        public string Status { get { return _status; } }
        public ScaleReading Last { get { return _last; } }
        public ScaleReading LastStable { get { return _lastStable; } }

        /// <summary>What the POS should treat as "the weight now": the last stable reading if it is fresh, else the latest.</summary>
        public ScaleReading Current
        {
            get
            {
                ScaleReading stable = _lastStable;
                if (stable.Ok && (DateTime.UtcNow - stable.AtUtc).TotalMilliseconds <= _stableStaleMs) return stable;
                return _last;
            }
        }

        public string[] RecentRaw()
        {
            lock (_gate)
            {
                var list = new System.Collections.Generic.List<string>(_rawRing.Length);
                for (int i = 0; i < _rawRing.Length; i++)
                {
                    int idx = (_rawPos - 1 - i + _rawRing.Length * 2) % _rawRing.Length;
                    string s = _rawRing[idx];
                    if (!string.IsNullOrEmpty(s)) list.Add(s);
                }
                return list.ToArray();
            }
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(RunLoop) { IsBackground = true, Name = "ScaleReader" };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            Thread t = _thread;
            _thread = null;
            if (t != null) { try { t.Join(2000); } catch { } }
            _connected = false;
            _status = "stopped";
        }

        private void RunLoop()
        {
            while (_running)
            {
                IScaleSource source = null;
                Stream stream = null;
                try
                {
                    source = _factory();
                    _status = "opening " + source.Describe;
                    stream = source.Open();
                    _connected = true;
                    _status = "connected to " + source.Describe;
                    Logger.Info("Scale connected: " + source.Describe);
                    ScaleLog.Info("Connected", source.Describe);
                    ReadStream(stream);
                }
                catch (Exception ex)
                {
                    _connected = false;
                    _status = "error: " + ex.Message;
                    if (_running)
                    {
                        Logger.Warn("Scale on " + (source == null ? "?" : source.Describe) + " disconnected: " + ex.Message);
                        ScaleLog.Error("Disconnected", ex.Message);
                    }
                }
                finally
                {
                    _connected = false;
                    try { if (source != null) source.Dispose(); } catch { }
                }

                if (!_running) break;
                _status = "reconnecting in " + (_reconnectDelayMs / 1000) + "s";
                Sleep(_reconnectDelayMs);
            }
            _status = "stopped";
        }

        private void ReadStream(Stream stream)
        {
            var line = new StringBuilder(64);
            var buffer = new byte[256];
            int lastPoll = Environment.TickCount - _pollIntervalMs;
            bool sawAny = false;

            while (_running)
            {
                if (_pollCommand != null && _pollIntervalMs > 0 && Elapsed(lastPoll) >= _pollIntervalMs)
                {
                    lastPoll = Environment.TickCount;
                    try { stream.Write(_pollCommand, 0, _pollCommand.Length); stream.Flush(); }
                    catch (Exception ex) { throw new IOException("write (poll) failed: " + ex.Message, ex); }
                }

                int n;
                try
                {
                    n = stream.Read(buffer, 0, buffer.Length);
                }
                catch (TimeoutException) { continue; }            // serial read timeout: no data this tick
                catch (IOException ioex) when (IsTimeout(ioex)) { continue; } // tcp read timeout
                catch (Exception ex) { throw new IOException("read failed: " + ex.Message, ex); }

                if (n <= 0) throw new IOException("the scale closed the connection");

                for (int i = 0; i < n; i++)
                {
                    byte b = buffer[i];
                    if (b == (byte)'\n' || b == (byte)'\r')
                    {
                        if (line.Length > 0)
                        {
                            OnLine(line.ToString());
                            sawAny = true;
                            line.Length = 0;
                        }
                    }
                    else if (line.Length < 512)
                    {
                        line.Append((char)b);
                    }
                }

                if (sawAny) { /* keep looping */ }
            }
        }

        private void OnLine(string raw)
        {
            lock (_gate) { _rawRing[_rawPos] = raw; _rawPos = (_rawPos + 1) % _rawRing.Length; }

            ScaleReading r = ScaleParser.Parse(raw);
            if (!r.Ok) return;

            _last = r;
            if (r.Stable)
            {
                _lastStable = r;
                // Log stable-weight changes, throttled, so the file is a useful trail and not a flood.
                string key = r.Weight.ToString(System.Globalization.CultureInfo.InvariantCulture) + r.Unit;
                if (key != _lastStableLogged)
                {
                    _lastStableLogged = key;
                    ScaleLog.Info("Stable", r.Weight.ToString(System.Globalization.CultureInfo.InvariantCulture) + (r.Unit.Length > 0 ? " " + r.Unit : "") + "  (raw: " + raw.Trim() + ")");
                }
            }
        }

        private void Sleep(int ms)
        {
            int slept = 0;
            while (_running && slept < ms) { Thread.Sleep(100); slept += 100; }
        }

        private static int Elapsed(int since) { return Environment.TickCount - since; }

        private static bool IsTimeout(IOException ex)
        {
            var se = ex.InnerException as SocketException;
            return se != null && (se.SocketErrorCode == SocketError.TimedOut || se.SocketErrorCode == SocketError.WouldBlock);
        }
    }
}
