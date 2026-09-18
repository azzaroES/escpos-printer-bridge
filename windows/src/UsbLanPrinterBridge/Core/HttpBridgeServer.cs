using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UsbLanPrinterBridge.Core
{
    /// <summary>
    /// A tiny HTTP/1.1 (and, with a certificate, HTTPS) server that emulates an Epson ePOS-Print device so that
    /// browser or Android POS apps built on the Epson ePOS SDK can print to a generic USB ESC/POS printer through
    /// the bridge. It answers POSTs to /cgi-bin/epos/service.cgi, converts the ePOS-Print XML to ESC/POS
    /// (<see cref="EposPrintConverter"/>), sends it to the print target, and returns the expected SOAP response.
    ///
    /// Full CORS + Private Network Access support is included because a POS web app served over HTTPS makes a
    /// cross-origin request to a private LAN address, which browsers gate behind both mechanisms.
    /// </summary>
    public sealed class HttpBridgeServer
    {
        private readonly IPrintTarget _target;
        private readonly X509Certificate2 _certificate; // null => plain HTTP
        private readonly object _gate = new object();
        private readonly HashSet<Socket> _sockets = new HashSet<Socket>();
        private TcpListener _listener;
        private CancellationTokenSource _cts;

        private long _requests;
        private long _jobs;

        public HttpBridgeServer(IPrintTarget target, X509Certificate2 certificate)
        {
            if (target == null) throw new ArgumentNullException("target");
            _target = target;
            _certificate = certificate;
        }

        public bool IsSecure { get { return _certificate != null; } }
        public string Scheme { get { return IsSecure ? "https" : "http"; } }
        public IPEndPoint LocalEndPoint { get; private set; }
        public long Requests { get { return Interlocked.Read(ref _requests); } }
        public long Jobs { get { return Interlocked.Read(ref _jobs); } }
        public bool IsRunning { get; private set; }

        public event Action<string> Log;

        public void Start(IPAddress address, int port)
        {
            lock (_gate)
            {
                if (IsRunning) return;
                var listener = new TcpListener(address, port);
                listener.Start(64);
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
            TcpListener listener;
            CancellationTokenSource cts;
            Socket[] socks;
            lock (_gate)
            {
                listener = _listener; cts = _cts; _listener = null; _cts = null; IsRunning = false;
                socks = new Socket[_sockets.Count]; _sockets.CopyTo(socks);
            }
            if (cts != null) { try { cts.Cancel(); } catch { } }
            if (listener != null) { try { listener.Stop(); } catch { } }
            foreach (Socket s in socks) { try { s.Close(); } catch { } }
        }

        private void Emit(string msg) { var h = Log; if (h != null) { try { h(msg); } catch { } } }

        /// <summary>Flattens an exception chain. "A call to SSPI failed" is meaningless without its inner exception.</summary>
        private static string Explain(Exception ex)
        {
            var sb = new StringBuilder();
            Exception current = ex;
            int depth = 0;
            while (current != null && depth++ < 5)
            {
                if (sb.Length > 0) sb.Append("  ->  ");
                sb.Append(current.Message);
                current = current.InnerException;
            }
            return sb.ToString();
        }

        /// <summary>Certificate details worth knowing when a handshake fails: no private key is a common cause.</summary>
        private string DescribeCertificate()
        {
            if (_certificate == null) return "none";
            try
            {
                return "subject " + _certificate.Subject
                       + ", private key " + (_certificate.HasPrivateKey ? "present" : "MISSING")
                       + ", valid to " + _certificate.NotAfter.ToString("yyyy-MM-dd");
            }
            catch (Exception ex)
            {
                return "unreadable: " + ex.Message;
            }
        }

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
                var _ = Task.Run(() => HandleConnection(client, sock, ct));
            }
        }

        private async Task HandleConnection(TcpClient client, Socket sock, CancellationToken ct)
        {
            string remote = "?";
            try { remote = sock.RemoteEndPoint.ToString(); } catch { }
            try
            {
                using (client)
                {
                    Stream stream = client.GetStream();
                    if (_certificate != null)
                    {
                        // A TLS ClientHello always starts with 0x16. Anything else means the caller spoke plain
                        // HTTP to the TLS port, which is the most common reason a handshake "fails" here.
                        // Peeking does not consume the byte, so the real handshake still sees the whole stream.
                        try
                        {
                            var peek = new byte[1];
                            int got = sock.Receive(peek, 0, 1, SocketFlags.Peek);
                            if (got == 1 && peek[0] != 0x16)
                            {
                                Emit("Client " + remote + " sent plain HTTP to the HTTPS port " + (LocalEndPoint == null ? "" : LocalEndPoint.Port.ToString())
                                     + ". It should use https://, or use the plain HTTP port instead. First byte was 0x" + peek[0].ToString("X2") + ".");
                                await WriteResponse(stream, 400, "Bad Request", "text/plain; charset=utf-8",
                                    Encoding.UTF8.GetBytes("This port speaks HTTPS. Use https:// for this address, or connect to the plain HTTP port."),
                                    null, null).ConfigureAwait(false);
                                return;
                            }
                        }
                        catch { /* peek is best effort; fall through to the handshake */ }

                        var ssl = new SslStream(stream, false);
                        try
                        {
                            await ssl.AuthenticateAsServerAsync(_certificate, false, System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls11 | System.Security.Authentication.SslProtocols.Tls, false).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            Emit("TLS handshake with " + remote + " failed: " + Explain(ex)
                                 + "  [certificate: " + DescribeCertificate() + "]");
                            return;
                        }
                        stream = ssl;
                    }

                    // Handle one request per connection (Connection: close). Enough for the ePOS SDK.
                    HttpRequest req = await HttpRequest.ReadAsync(stream, ct).ConfigureAwait(false);
                    if (req == null) return;
                    Interlocked.Increment(ref _requests);
                    await Route(stream, req, remote).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Emit("HTTP error from " + remote + ": " + ex.Message);
            }
            finally
            {
                lock (_gate) _sockets.Remove(sock);
            }
        }

        private async Task Route(Stream stream, HttpRequest req, string remote)
        {
            string origin = req.Header("Origin");

            if (req.Method == "OPTIONS")
            {
                await WritePreflight(stream, req, origin).ConfigureAwait(false);
                return;
            }

            bool isEpos = req.Path.IndexOf("/cgi-bin/epos", StringComparison.OrdinalIgnoreCase) >= 0
                          || req.Path.IndexOf("service.cgi", StringComparison.OrdinalIgnoreCase) >= 0;

            if (req.Method == "POST" && isEpos)
            {
                await HandleEpos(stream, req, origin, remote).ConfigureAwait(false);
                return;
            }

            if (req.Method == "GET")
            {
                await WriteStatusPage(stream, origin).ConfigureAwait(false);
                return;
            }

            await WriteResponse(stream, 404, "Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Not found"), origin, null).ConfigureAwait(false);
        }

        private async Task HandleEpos(Stream stream, HttpRequest req, string origin, string remote)
        {
            string bodyXml = req.BodyText();
            string printJobId = ExtractPrintJobId(bodyXml);

            string responseXml;
            try
            {
                IList<string> warnings;
                byte[] escpos = EposPrintConverter.Convert(bodyXml, out warnings);

                using (IPrintJob job = _target.StartJob("ePOS " + remote + " " + DateTime.Now.ToString("HH:mm:ss")))
                {
                    job.Write(escpos, 0, escpos.Length);
                    job.Complete();
                }
                Interlocked.Increment(ref _jobs);
                PrintHistory.Add(remote, _target.Name, "ePOS " + Scheme, escpos, escpos.Length, "Printed");
                Emit("ePOS job printed to \"" + _target.Name + "\": " + BridgeListener.FormatBytes(escpos.Length) + " from " + remote
                     + (warnings.Count > 0 ? "  (" + warnings.Count + " warning(s): " + string.Join("; ", ToArray(warnings)) + ")" : ""));
                responseXml = BuildResponse(true, "", OkStatus, printJobId);
            }
            catch (Exception ex)
            {
                Emit("ePOS print from " + remote + " failed: " + ex.Message);
                byte[] raw = Encoding.UTF8.GetBytes(bodyXml ?? "");
                PrintHistory.Add(remote, _target.Name, "ePOS " + Scheme, raw, raw.Length, "Failed: " + ex.Message);
                string code = ex is System.Xml.XmlException ? "SchemaError" : "EPTR_PRINT_SYSTEM_ERROR";
                responseXml = BuildResponse(false, code, OkStatus, printJobId);
            }

            byte[] bytes = Encoding.UTF8.GetBytes(responseXml);
            await WriteResponse(stream, 200, "OK", "text/xml; charset=utf-8", bytes, origin, null).ConfigureAwait(false);
        }

        private static string[] ToArray(IList<string> list)
        {
            var a = new string[list.Count];
            for (int i = 0; i < list.Count; i++) a[i] = list[i];
            return a;
        }

        // ------------------------------------------------------------------ response writers

        private async Task WritePreflight(Stream stream, HttpRequest req, string origin)
        {
            var extra = new List<string>();
            AddCors(extra, origin);
            extra.Add("Access-Control-Allow-Methods: GET, POST, OPTIONS");
            string reqHeaders = req.Header("Access-Control-Request-Headers");
            extra.Add("Access-Control-Allow-Headers: " + (string.IsNullOrEmpty(reqHeaders) ? "Content-Type, SOAPAction, If-Modified-Since" : reqHeaders));
            extra.Add("Access-Control-Max-Age: 86400");
            if (string.Equals(req.Header("Access-Control-Request-Private-Network"), "true", StringComparison.OrdinalIgnoreCase))
                extra.Add("Access-Control-Allow-Private-Network: true");
            await WriteResponse(stream, 204, "No Content", null, new byte[0], origin, extra).ConfigureAwait(false);
        }

        private async Task WriteStatusPage(Stream stream, string origin)
        {
            string html = "<!doctype html><meta charset=utf-8><title>USB LAN Printer Bridge</title>"
                        + "<body style='font-family:Segoe UI,Arial,sans-serif;margin:2em'>"
                        + "<h2>USB LAN Printer Bridge &mdash; ePOS-Print endpoint</h2>"
                        + "<p>This is the ePOS-Print service for printer <b>" + WebEncode(_target.Name) + "</b>.</p>"
                        + "<p>Point an Epson ePOS SDK client at <code>" + Scheme + "://&lt;this address&gt;/cgi-bin/epos/service.cgi?devid=local_printer</code>.</p>"
                        + "<p>Requests served: " + Requests + " &middot; jobs printed: " + Jobs + "</p>"
                        + (IsSecure ? "<p>If your browser warns about the certificate, accept it once for this address.</p>" : "")
                        + "</body>";
            await WriteResponse(stream, 200, "OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html), origin, null).ConfigureAwait(false);
        }

        private static async Task WriteResponse(Stream stream, int status, string reason, string contentType, byte[] body, string origin, List<string> extraHeaders)
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
            sb.Append("Server: UsbLanPrinterBridge\r\n");
            sb.Append("Connection: close\r\n");
            if (contentType != null) sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
            sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            if (extraHeaders != null) foreach (string h in extraHeaders) sb.Append(h).Append("\r\n");
            else AddCorsHeaders(sb, origin);
            sb.Append("\r\n");

            byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
            await stream.WriteAsync(head, 0, head.Length).ConfigureAwait(false);
            if (body.Length > 0) await stream.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }

        private static void AddCors(List<string> headers, string origin)
        {
            headers.Add("Access-Control-Allow-Origin: " + (string.IsNullOrEmpty(origin) ? "*" : origin));
            headers.Add("Vary: Origin");
        }

        private static void AddCorsHeaders(StringBuilder sb, string origin)
        {
            sb.Append("Access-Control-Allow-Origin: ").Append(string.IsNullOrEmpty(origin) ? "*" : origin).Append("\r\n");
            sb.Append("Vary: Origin\r\n");
        }

        // ------------------------------------------------------------------ ePOS response XML

        /// <summary>
        /// The status word a real Epson returns when all is well (0x0F000016), captured from a TM-T20II.
        /// Its low byte is the same 0x16 that DLE EOT 1 returns on the raw port.
        /// </summary>
        public const int OkStatus = 251658262;

        private static string BuildResponse(bool success, string code, int status, string printJobId)
        {
            // Byte-for-byte the shape a real TM printer returns, so SDK clients parse it identically.
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.Append("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body>");
            sb.Append("<response success=\"").Append(success ? "true" : "false")
              .Append("\" code=\"").Append(code ?? "")
              .Append("\" status=\"").Append(status)
              .Append("\" battery=\"0\" xmlns=\"").Append(EposPrintConverter.EposNamespace).Append("\">");
            if (!string.IsNullOrEmpty(printJobId)) sb.Append("<printjobid>").Append(WebEncode(printJobId)).Append("</printjobid>");
            sb.Append("</response>");
            sb.Append("</s:Body></s:Envelope>");
            return sb.ToString();
        }

        private static string ExtractPrintJobId(string xml)
        {
            if (string.IsNullOrEmpty(xml)) return null;
            int i = xml.IndexOf("<printjobid>", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            i += "<printjobid>".Length;
            int j = xml.IndexOf("</printjobid>", i, StringComparison.OrdinalIgnoreCase);
            return j < 0 ? null : xml.Substring(i, j - i).Trim();
        }

        private static string WebEncode(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        // ------------------------------------------------------------------ minimal HTTP request parser

        private sealed class HttpRequest
        {
            public string Method;
            public string Path;
            private readonly Dictionary<string, string> _headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            private byte[] _body = new byte[0];

            public string Header(string name) { string v; return _headers.TryGetValue(name, out v) ? v : null; }
            public string BodyText() { return Encoding.UTF8.GetString(_body); }

            public static async Task<HttpRequest> ReadAsync(Stream stream, CancellationToken ct)
            {
                // Read headers (up to CRLFCRLF). Bounded to avoid unbounded memory.
                var headerBytes = new List<byte>(1024);
                var one = new byte[1];
                int matched = 0;
                int guard = 0;
                while (guard++ < 64 * 1024)
                {
                    int n = await stream.ReadAsync(one, 0, 1, ct).ConfigureAwait(false);
                    if (n <= 0) return null; // connection closed before a full request line
                    byte b = one[0];
                    headerBytes.Add(b);
                    if ((matched == 0 || matched == 2) && b == 0x0D) matched++;
                    else if ((matched == 1 || matched == 3) && b == 0x0A) matched++;
                    else matched = (b == 0x0D) ? 1 : 0;
                    if (matched == 4) break;
                }

                string headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
                string[] lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
                if (lines.Length == 0 || lines[0].Length == 0) return null;

                var req = new HttpRequest();
                string[] parts = lines[0].Split(' ');
                if (parts.Length < 2) return null;
                req.Method = parts[0].ToUpperInvariant();
                req.Path = parts[1];

                for (int i = 1; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (line.Length == 0) continue;
                    int colon = line.IndexOf(':');
                    if (colon <= 0) continue;
                    string name = line.Substring(0, colon).Trim();
                    string value = line.Substring(colon + 1).Trim();
                    req._headers[name] = value;
                }

                int contentLength = 0;
                string cl = req.Header("Content-Length");
                if (cl != null) int.TryParse(cl, out contentLength);
                if (contentLength > 0)
                {
                    if (contentLength > 8 * 1024 * 1024) contentLength = 8 * 1024 * 1024; // sanity cap
                    var body = new byte[contentLength];
                    int read = 0;
                    while (read < contentLength)
                    {
                        int n = await stream.ReadAsync(body, read, contentLength - read, ct).ConfigureAwait(false);
                        if (n <= 0) break;
                        read += n;
                    }
                    req._body = read == contentLength ? body : Trim(body, read);
                }
                return req;
            }

            private static byte[] Trim(byte[] b, int len) { var r = new byte[len]; Array.Copy(b, r, len); return r; }
        }
    }
}
