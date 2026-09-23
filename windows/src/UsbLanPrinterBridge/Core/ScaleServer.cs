using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UsbLanPrinterBridge.Core
{
    /// <summary>
    /// A tiny HTTP server that publishes the scale's weight so any POS on the LAN can read it, wireless by nature.
    ///   GET /scale       -> {"ok":true,"weight":1.234,"unit":"kg","stable":true,...}
    ///   GET /scale/raw   -> {"connected":true,"status":"...","lines":["...",...]}   (to identify an unknown scale)
    /// Full CORS + Private Network Access support, like the ePOS endpoint, so a POS page served over HTTPS from the
    /// LAN can call it. Binds to whatever address it is given, so it can sit on its own virtual IP.
    /// </summary>
    public sealed class ScaleServer
    {
        private readonly Func<ScaleReading> _current;
        private readonly Func<string[]> _raw;
        private readonly Func<string> _status;
        private readonly Func<bool> _connected;

        private readonly object _gate = new object();
        private readonly HashSet<Socket> _sockets = new HashSet<Socket>();
        private TcpListener _listener;
        private CancellationTokenSource _cts;

        public ScaleServer(Func<ScaleReading> current, Func<string[]> raw, Func<string> status, Func<bool> connected)
        {
            _current = current; _raw = raw; _status = status; _connected = connected;
        }

        public IPEndPoint LocalEndPoint { get; private set; }
        public bool IsRunning { get; private set; }
        public event Action<string> Log;

        public void Start(IPAddress address, int port)
        {
            lock (_gate)
            {
                if (IsRunning) return;
                var listener = new TcpListener(address, port);
                listener.Start(32);
                _listener = listener;
                _cts = new CancellationTokenSource();
                LocalEndPoint = (IPEndPoint)listener.LocalEndpoint;
                IsRunning = true;
                CancellationToken token = _cts.Token;
                Task.Run(() => AcceptLoop(listener, token));
            }
        }

        public void Stop()
        {
            TcpListener listener; CancellationTokenSource cts; Socket[] socks;
            lock (_gate)
            {
                listener = _listener; cts = _cts; _listener = null; _cts = null; IsRunning = false;
                socks = new Socket[_sockets.Count]; _sockets.CopyTo(socks);
            }
            if (cts != null) { try { cts.Cancel(); } catch { } }
            if (listener != null) { try { listener.Stop(); } catch { } }
            foreach (Socket s in socks) { try { s.Close(); } catch { } }
        }

        private void Emit(string m) { var h = Log; if (h != null) { try { h(m); } catch { } } }

        private async Task AcceptLoop(TcpListener listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { if (ct.IsCancellationRequested) break; continue; }
                catch { break; }
                Socket sock = client.Client;
                lock (_gate) _sockets.Add(sock);
                var _ = Task.Run(() => Handle(client, sock, ct));
            }
        }

        private async Task Handle(TcpClient client, Socket sock, CancellationToken ct)
        {
            try
            {
                using (client)
                {
                    var stream = client.GetStream();
                    Req req = await ReadRequest(stream, ct).ConfigureAwait(false);
                    if (req == null) return;
                    string method = req.Method, path = req.Path, origin = req.Origin;

                    if (method == "OPTIONS") { await Preflight(stream, origin).ConfigureAwait(false); return; }

                    string p = path;
                    int q = p.IndexOf('?');
                    if (q >= 0) p = p.Substring(0, q);
                    p = p.TrimEnd('/').ToLowerInvariant();

                    if (method == "GET" && (p == "/scale" || p == ""))
                    {
                        ScaleReading r = _current != null ? _current() : ScaleReading.Empty;
                        await WriteJson(stream, 200, r.ToJson(), origin).ConfigureAwait(false);
                        return;
                    }
                    if (method == "GET" && p == "/scale/raw")
                    {
                        await WriteJson(stream, 200, RawJson(), origin).ConfigureAwait(false);
                        return;
                    }
                    await WriteJson(stream, 404, "{\"error\":\"not found\"}", origin).ConfigureAwait(false);
                }
            }
            catch (Exception ex) { Emit("HTTP error: " + ex.Message); }
            finally { lock (_gate) _sockets.Remove(sock); }
        }

        private string RawJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\"connected\":").Append(_connected != null && _connected() ? "true" : "false");
            sb.Append(",\"status\":\"").Append(ScaleReading.JsonEscape(_status != null ? _status() : "")).Append('"');
            sb.Append(",\"lines\":[");
            string[] lines = _raw != null ? _raw() : new string[0];
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(ScaleReading.JsonEscape(lines[i])).Append('"');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        // ---- minimal HTTP ----

        private sealed class Req { public string Method; public string Path; public string Origin; }

        private static async Task<Req> ReadRequest(System.IO.Stream stream, CancellationToken ct)
        {
            var header = new List<byte>(512);
            var one = new byte[1];
            int matched = 0, guard = 0;
            while (guard++ < 32 * 1024)
            {
                int n;
                try { n = await stream.ReadAsync(one, 0, 1, ct).ConfigureAwait(false); }
                catch { return null; }
                if (n <= 0) return null;
                byte b = one[0];
                header.Add(b);
                if ((matched == 0 || matched == 2) && b == 0x0D) matched++;
                else if ((matched == 1 || matched == 3) && b == 0x0A) matched++;
                else matched = (b == 0x0D) ? 1 : 0;
                if (matched == 4) break;
            }
            string text = Encoding.ASCII.GetString(header.ToArray());
            string[] lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0) return null;
            string[] parts = lines[0].Split(' ');
            if (parts.Length < 2) return null;
            var req = new Req { Method = parts[0].ToUpperInvariant(), Path = parts[1] };
            for (int i = 1; i < lines.Length; i++)
            {
                int c = lines[i].IndexOf(':');
                if (c <= 0) continue;
                if (lines[i].Substring(0, c).Trim().Equals("Origin", StringComparison.OrdinalIgnoreCase))
                    req.Origin = lines[i].Substring(c + 1).Trim();
            }
            return req;
        }

        private static async Task Preflight(System.IO.Stream stream, string origin)
        {
            var extra = new List<string>
            {
                "Access-Control-Allow-Origin: " + (string.IsNullOrEmpty(origin) ? "*" : origin),
                "Vary: Origin",
                "Access-Control-Allow-Methods: GET, OPTIONS",
                "Access-Control-Allow-Headers: Content-Type",
                "Access-Control-Max-Age: 86400",
                "Access-Control-Allow-Private-Network: true"
            };
            await Write(stream, 204, "No Content", null, new byte[0], extra).ConfigureAwait(false);
        }

        private static async Task WriteJson(System.IO.Stream stream, int status, string json, string origin)
        {
            var extra = new List<string>
            {
                "Access-Control-Allow-Origin: " + (string.IsNullOrEmpty(origin) ? "*" : origin),
                "Vary: Origin"
            };
            await Write(stream, status, status == 200 ? "OK" : (status == 404 ? "Not Found" : "Error"),
                "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json), extra).ConfigureAwait(false);
        }

        private static async Task Write(System.IO.Stream stream, int status, string reason, string contentType, byte[] body, List<string> extra)
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
            sb.Append("Server: UsbLanPrinterBridge-Scale\r\n");
            sb.Append("Connection: close\r\n");
            sb.Append("Cache-Control: no-store\r\n");
            if (contentType != null) sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
            sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            if (extra != null) foreach (string h in extra) sb.Append(h).Append("\r\n");
            sb.Append("\r\n");
            byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
            await stream.WriteAsync(head, 0, head.Length).ConfigureAwait(false);
            if (body.Length > 0) await stream.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }
    }
}
