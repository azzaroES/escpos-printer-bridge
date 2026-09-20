using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using UsbLanPrinterBridge;
using UsbLanPrinterBridge.Core;
using UsbLanPrinterBridge.UI;

namespace UsbLanPrinterBridge.Tests
{
    /// <summary>
    /// Self-contained test harness (no test framework needed): exercises the scanner, the TCP listener, the manager,
    /// the config store, the real spooler (against the XPS writer, redirected to a file) and renders the main window.
    /// Exit code = number of failed checks.
    /// </summary>
    internal static class Program
    {
        private static int _failed;
        private static int _passed;
        private static readonly string OutDir = Path.Combine(Path.GetDirectoryName(typeof(Program).Assembly.Location), "test-output");

        [STAThread]
        private static int Main(string[] args)
        {
            Directory.CreateDirectory(OutDir);
            ConfigStore.DataDirectory = Path.Combine(OutDir, "data");
            Logger.FileLoggingEnabled = true;

            Run("EscPos scanner: plain data passes through", Scanner_PlainData);
            Run("EscPos scanner: DLE EOT n stripped and answered", Scanner_StatusRequest);
            Run("EscPos scanner: request split across chunks", Scanner_SplitAcrossChunks);
            Run("EscPos scanner: DLE not followed by EOT passes through", Scanner_DleOther);
            Run("EscPos scanner: unknown DLE EOT argument passes through", Scanner_UnknownArg);
            Run("EscPos scanner: flush releases held bytes", Scanner_Flush);
            Run("EscPos scanner: 1 MB random payload survives intact", Scanner_LargeRandom);
            Run("EscPos responder: GS I / GS a / GS r answer like a real TM printer", Responder_GsQueries);
            Run("EscPos responder: GS ( H echoes the process id (SDK completion handshake)", Responder_ProcessId);
            Run("EscPos responder: GS ( H split across chunks still answered", Responder_ProcessIdSplit);
            Run("EscPos responder: QR print data (GS ( k) passes through untouched", Responder_QrPassesThrough);

            Run("No cut: every cutter command is removed and replaced by a feed", NoCut_RemovesCuts);
            Run("No cut: cut bytes inside image, graphics and QR payloads are left alone", NoCut_PayloadsSurvive);
            Run("No cut: output identical wherever the TCP read splits the stream", NoCut_SplitAnywhere);
            Run("No cut: a stream without cuts passes through byte for byte (incl. 256 KB random)", NoCut_PassThrough);
            Run("No cut: wrapper follows the switch mid-job", NoCut_WrapperFollowsSwitch);
            Run("No cut: per printer, end to end through two listeners, toggled while running", NoCut_Listener);
            Run("Ticket text: a job renders as the ticket looks on paper", Ticket_Renders);
            Run("Job summary: describes what a job told the printer", Summary_Describes);
            Run("Job summary: spots PCL/PostScript/ZPL jobs sent to a receipt printer", Summary_ForeignFormat);
            Run("Printer actions log: records, counts errors, writes the daily file", Actions_Log);
            Run("Device: printer actions become timeline events", Device_Events);
            Run("Device: one sample fills the tiles and writes a telemetry row; clear deletes it", Device_SampleAndTelemetry);
            Run("ePOS: the device id in the URL must match the mapping's (DeviceNotFound otherwise)", Epos_DeviceId);
            Run("HTTP server: /cert page and the .cer download", Http_CertPage);
            Run("Config: ePOS device id is sanitised and builds the links", Config_DeviceId);
            Run("Printer status: XPS writer is ready; an unknown printer is reported once", Status_Probe);

            Run("Listener: one connection = one job", Listener_SingleJob);
            Run("Listener: idle timeout splits jobs on a kept-open connection", Listener_IdleSplit);
            Run("Listener: idle timeout 0 waits for disconnect", Listener_NoIdle);
            Run("Listener: status query answered and not printed", Listener_StatusQuery);
            Run("Listener: status replies disabled → bytes forwarded", Listener_StatusDisabled);
            Run("Listener: 8 concurrent clients, 200 KB each", Listener_Concurrent);
            Run("Listener: stop while a client is connected flushes the job", Listener_StopFlushes);
            Run("Listener: half-close (shutdown send) ends the job", Listener_HalfClose);

            Run("Manager: start/stop on loopback, no netsh", Manager_Loopback);
            Run("Manager: 0.0.0.0 binds on all addresses", Manager_Any);
            Run("Manager: port in use gives a clear error", Manager_PortInUse);
            Run("Manager: validation messages", Manager_Validation);
            Run("Manager: virtual IP without admin is refused with a hint", Manager_NoAdminHint);
            Run("Manager: changing the printer re-points a restarted bridge", Manager_RetargetsPrinter);

            Run("ePOS: text + styles convert to ESC/POS", Epos_TextStyles);
            Run("ePOS: feed/cut/pulse/barcode/qr/command convert", Epos_Elements);
            Run("ePOS: image raster becomes GS v 0", Epos_Image);
            Run("ePOS: SOAP-wrapped document is found", Epos_SoapWrapped);
            Run("ePOS: malformed XML throws (→ SchemaError)", Epos_Malformed);
            Run("HTTP server: POST prints and returns success response", Http_PostPrints);
            Run("HTTP server: CORS preflight (OPTIONS) with private-network header", Http_Preflight);
            Run("HTTP server: bad XML returns success=false SchemaError", Http_BadXml);
            Run("HTTPS server: self-signed cert, real TLS POST prints", Https_PostPrints);
            Run("Print log: readable text recovered from an ESC/POS job", History_ExtractsReadableText);
            Run("Print log: records kept and appended to the daily CSV", History_RecordsAndWritesCsv);
            Run("Print log: raw job dump written when enabled", History_RawDump);
            Run("Cert: generated cert covers requested IPs", Cert_Sans);

            Run("Config: round trip through XML", Config_RoundTrip);
            Run("Network: adapters and address suggestion", Network_Adapters);
            Run("Network: netsh output capture works", Network_NetshRuns);
            Run("Network: address suggestion respects the real prefix length", Network_SuggestionRespectsPrefix);
            Run("IpHelper: unicast table layout matches .NET's view of the adapters", IpHelper_TableLayout);
            Run("IpHelper: add/remove address (needs admin; otherwise expects access denied)", IpHelper_AddRemove);
            Run("Printers: enumeration works", Printers_Enumerate);
            Run("Spooler: RAW job through winspool to XPS writer (redirected to file)", Spooler_XpsWriter);
            Run("Spooler: unknown printer throws a readable error", Spooler_UnknownPrinter);
            Run("UI: main window renders (screenshot)", Ui_Screenshot);
            Run("UI: print log window renders with jobs (screenshot)", Ui_PrintLogRenders);

            Console.WriteLine();
            Console.WriteLine("Passed: " + _passed + "   Failed: " + _failed);
            Console.WriteLine("Output: " + OutDir);
            return _failed;
        }

        // ------------------------------------------------------------------ helpers

        private static void Run(string name, Action test)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                test();
                _passed++;
                Console.WriteLine("PASS  " + name + "  (" + sw.ElapsedMilliseconds + " ms)");
            }
            catch (SkipException ex)
            {
                Console.WriteLine("SKIP  " + name + "  — " + ex.Message);
            }
            catch (Exception ex)
            {
                _failed++;
                Console.WriteLine("FAIL  " + name + "  — " + ex.Message);
                if (!(ex is CheckException)) Console.WriteLine("      " + ex);
            }
        }

        private sealed class CheckException : Exception { public CheckException(string m) : base(m) { } }
        private sealed class SkipException : Exception { public SkipException(string m) : base(m) { } }

        private static void Check(bool condition, string what)
        {
            if (!condition) throw new CheckException(what);
        }

        private static void CheckEqual(byte[] expected, byte[] actual, string what)
        {
            if (expected.Length != actual.Length)
                throw new CheckException(what + ": length " + actual.Length + " != expected " + expected.Length);
            for (int i = 0; i < expected.Length; i++)
                if (expected[i] != actual[i]) throw new CheckException(what + ": byte " + i + " is 0x" + actual[i].ToString("X2") + ", expected 0x" + expected[i].ToString("X2"));
        }

        private static byte[] Bytes(params int[] values) { return values.Select(v => (byte)v).ToArray(); }
        private static byte[] Ascii(string s) { return Encoding.ASCII.GetBytes(s); }
        private static byte[] Concat(params byte[][] parts) { return parts.SelectMany(p => p).ToArray(); }

        private static byte[] Slice(byte[] source, int length) { var r = new byte[length]; Array.Copy(source, r, length); return r; }

        private sealed class Harness : IDisposable
        {
            public MemoryPrintTarget Target = new MemoryPrintTarget("test");
            public BridgeListener Listener;
            public MappingConfig Mapping;
            public List<string> Logs = new List<string>();

            public Harness(bool escpos, int idleMs)
            {
                Mapping = new MappingConfig { PrinterName = "test", BindAddress = "127.0.0.1", Port = 0, EscPosStatusReplies = escpos };
                Listener = new BridgeListener(Mapping, Target) { JobIdleTimeoutMs = idleMs };
                Listener.Log += (l, m) => { lock (Logs) Logs.Add(m); };
                Listener.Start(IPAddress.Loopback, 0);
            }

            public int Port { get { return Listener.LocalEndPoint.Port; } }

            public TcpClient Connect()
            {
                var c = new TcpClient();
                c.Connect(IPAddress.Loopback, Port);
                return c;
            }

            public bool WaitForJobs(int count, int timeoutMs)
            {
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    if (Target.JobCount >= count) return true;
                    Thread.Sleep(20);
                }
                return Target.JobCount >= count;
            }

            public void Dispose() { Listener.Stop(); }
        }

        // ------------------------------------------------------------------ scanner

        private static void Scanner_PlainData()
        {
            var s = new EscPosResponder();
            byte[] input = Ascii("Hello receipt\n");
            s.Process(input, 0, input.Length);
            CheckEqual(input, Slice(s.Forward, s.ForwardLength), "forward");
            Check(s.ReplyLength == 0, "no reply expected");
        }

        private static void Scanner_StatusRequest()
        {
            var s = new EscPosResponder();
            byte[] input = Concat(Ascii("AB"), Bytes(0x10, 0x04, 0x01), Ascii("CD"), Bytes(0x10, 0x04, 0x04));
            s.Process(input, 0, input.Length);
            CheckEqual(Ascii("ABCD"), Slice(s.Forward, s.ForwardLength), "forward");
            // Values captured from a real TM-T20II: DLE EOT 1 -> 0x16, DLE EOT 4 -> 0x12.
            CheckEqual(Bytes(0x16, 0x12), Slice(s.Replies, s.ReplyLength), "replies");
        }

        private static void Scanner_SplitAcrossChunks()
        {
            var s = new EscPosResponder();
            byte[] c1 = Concat(Ascii("AB"), Bytes(0x10));
            s.Process(c1, 0, c1.Length);
            CheckEqual(Ascii("AB"), Slice(s.Forward, s.ForwardLength), "chunk 1 forward (DLE held back)");
            Check(s.ReplyLength == 0, "chunk 1 no reply");

            byte[] c2 = Bytes(0x04);
            s.Process(c2, 0, c2.Length);
            Check(s.ForwardLength == 0, "chunk 2 forwards nothing");
            Check(s.ReplyLength == 0, "chunk 2 no reply yet");

            byte[] c3 = Concat(Bytes(0x02), Ascii("C"));
            s.Process(c3, 0, c3.Length);
            CheckEqual(Ascii("C"), Slice(s.Forward, s.ForwardLength), "chunk 3 forward");
            CheckEqual(Bytes(0x12), Slice(s.Replies, s.ReplyLength), "chunk 3 reply");
        }

        private static void Scanner_DleOther()
        {
            var s = new EscPosResponder();
            byte[] input = Bytes(0x10, 0x14, 0x01, 0x00, 0x05); // DLE DC4 (drawer pulse) must pass through
            s.Process(input, 0, input.Length);
            CheckEqual(input, Slice(s.Forward, s.ForwardLength), "forward");
            Check(s.ReplyLength == 0, "no reply");

            s = new EscPosResponder();
            input = Bytes(0x10, 0x10, 0x04, 0x01); // DLE DLE EOT 1 → first DLE forwarded, then a real request
            s.Process(input, 0, input.Length);
            CheckEqual(Bytes(0x10), Slice(s.Forward, s.ForwardLength), "forward single DLE");
            CheckEqual(Bytes(0x16), Slice(s.Replies, s.ReplyLength), "reply");
        }

        private static void Scanner_UnknownArg()
        {
            var s = new EscPosResponder();
            byte[] input = Bytes(0x10, 0x04, 0x09, 0x41);
            s.Process(input, 0, input.Length);
            CheckEqual(input, Slice(s.Forward, s.ForwardLength), "forward");
            Check(s.ReplyLength == 0, "no reply");
        }

        private static void Scanner_Flush()
        {
            var s = new EscPosResponder();
            byte[] input = Bytes(0x41, 0x10, 0x04);
            s.Process(input, 0, input.Length);
            CheckEqual(Bytes(0x41), Slice(s.Forward, s.ForwardLength), "forward before flush");
            s.Flush();
            CheckEqual(Bytes(0x10, 0x04), Slice(s.Forward, s.ForwardLength), "flushed bytes");
        }

        // Expected values below were captured from the user's real Epson TM-T20II at 192.168.1.180.
        private static void Responder_GsQueries()
        {
            var s = new EscPosResponder { ModelName = "TM-T20II" };

            s.Process(Bytes(0x1D, 0x49, 0x01), 0, 3);                 // GS I 1: printer model id
            Check(s.ForwardLength == 0, "query not forwarded to the printer");
            CheckEqual(Bytes(0x63), Slice(s.Replies, s.ReplyLength), "GS I 1 -> 0x63");

            s.Process(Bytes(0x1D, 0x49, 0x43), 0, 3);                 // GS I 67: model name
            CheckEqual(Concat(Bytes(0x5F), Ascii("TM-T20II"), Bytes(0x00)), Slice(s.Replies, s.ReplyLength), "GS I 67 -> _TM-T20II NUL");

            s.Process(Bytes(0x1D, 0x61, 0xFF), 0, 3);                 // GS a 255: enable ASB
            CheckEqual(Bytes(0x14, 0x00, 0x00, 0x0F), Slice(s.Replies, s.ReplyLength), "GS a -> 14 00 00 0F");

            s.Process(Bytes(0x1D, 0x72, 0x01), 0, 3);                 // GS r 1: transmit status
            CheckEqual(Bytes(0x00), Slice(s.Replies, s.ReplyLength), "GS r 1 -> 0x00");
        }

        private static void Responder_ProcessId()
        {
            var s = new EscPosResponder();
            // GS ( H pL=6 pH=0 fn=48 m=48 "ABCD"  ->  0x37 0x22 "ABCD" 0x00
            byte[] req = Concat(Bytes(0x1D, 0x28, 0x48, 0x06, 0x00, 0x30, 0x30), Ascii("ABCD"));
            s.Process(req, 0, req.Length);
            Check(s.ForwardLength == 0, "process-id query not forwarded");
            CheckEqual(Concat(Bytes(0x37, 0x22), Ascii("ABCD"), Bytes(0x00)), Slice(s.Replies, s.ReplyLength), "process id echoed");
        }

        private static void Responder_ProcessIdSplit()
        {
            var s = new EscPosResponder();
            byte[] req = Concat(Bytes(0x1D, 0x28, 0x48, 0x06, 0x00, 0x30, 0x30), Ascii("WXYZ"));
            for (int cut = 1; cut < req.Length; cut++)
            {
                var fresh = new EscPosResponder();
                fresh.Process(req, 0, cut);
                Check(fresh.ReplyLength == 0, "no reply before the query is complete (cut " + cut + ")");
                fresh.Process(req, cut, req.Length - cut);
                CheckEqual(Concat(Bytes(0x37, 0x22), Ascii("WXYZ"), Bytes(0x00)), Slice(fresh.Replies, fresh.ReplyLength), "answered after split at " + cut);
                Check(fresh.ForwardLength == 0, "nothing forwarded (cut " + cut + ")");
            }
        }

        private static void Responder_QrPassesThrough()
        {
            var s = new EscPosResponder();
            // GS ( k QR store-data followed by print: must reach the printer unchanged.
            byte[] qr = Concat(Bytes(0x1D, 0x28, 0x6B, 0x08, 0x00, 0x31, 0x50, 0x30), Ascii("HELLO"),
                               Bytes(0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x51, 0x30));
            s.Process(qr, 0, qr.Length);
            s.Flush();
            var all = new MemoryStream();
            s.Process(qr, 0, qr.Length);
            all.Write(s.Forward, 0, s.ForwardLength);
            s.Flush();
            all.Write(s.Forward, 0, s.ForwardLength);
            CheckEqual(qr, all.ToArray(), "QR command stream untouched");
            Check(s.ReplyLength == 0, "no reply for print data");
        }

        private static void Scanner_LargeRandom()
        {
            var rnd = new Random(1234);
            byte[] data = new byte[1024 * 1024];
            rnd.NextBytes(data);
            // Remove every lead byte of a query sequence so the expected output equals the input exactly.
            for (int i = 0; i < data.Length; i++)
                if (data[i] == 0x10) data[i] = 0x11;
                else if (data[i] == 0x1D) data[i] = 0x1E;

            var s = new EscPosResponder();
            var output = new MemoryStream();
            int replies = 0;
            for (int offset = 0; offset < data.Length;)
            {
                int n = Math.Min(rnd.Next(1, 5000), data.Length - offset);
                s.Process(data, offset, n);
                output.Write(s.Forward, 0, s.ForwardLength);
                replies += s.ReplyLength;
                offset += n;
            }
            s.Flush();
            output.Write(s.Forward, 0, s.ForwardLength);
            CheckEqual(data, output.ToArray(), "1 MB stream");
            Check(replies == 0, "no replies expected, got " + replies);
        }

        // ------------------------------------------------------------------ listener

        private static void Listener_SingleJob()
        {
            using (var h = new Harness(true, 1500))
            {
                byte[] payload = Enumerable.Range(0, 100000).Select(i => (byte)(i * 7)).ToArray();
                using (TcpClient c = h.Connect())
                using (NetworkStream ns = c.GetStream())
                {
                    ns.Write(payload, 0, payload.Length);
                }
                Check(h.WaitForJobs(1, 5000), "job did not arrive");
                Check(h.Target.JobCount == 1, "expected exactly 1 job, got " + h.Target.JobCount);
                // strip any DLE EOT n sequences that appeared by chance in the synthetic payload
                var expected = new EscPosResponder();
                expected.Process(payload, 0, payload.Length);
                CheckEqual(Slice(expected.Forward, expected.ForwardLength), h.Target.Jobs[0], "job content");
                Check(h.Listener.JobsCompleted == 1 && h.Listener.BytesReceived == payload.Length, "counters");
            }
        }

        private static void Listener_IdleSplit()
        {
            using (var h = new Harness(true, 300))
            using (TcpClient c = h.Connect())
            using (NetworkStream ns = c.GetStream())
            {
                ns.Write(Ascii("RECEIPT-1"), 0, 9);
                Check(h.WaitForJobs(1, 3000), "first job after idle");
                ns.Write(Ascii("RECEIPT-2"), 0, 9);
                Check(h.WaitForJobs(2, 3000), "second job after idle");
                ns.Write(Ascii("RECEIPT-3"), 0, 9);
                c.Close();
                Check(h.WaitForJobs(3, 3000), "third job on disconnect");
                CheckEqual(Ascii("RECEIPT-1"), h.Target.Jobs[0], "job 1");
                CheckEqual(Ascii("RECEIPT-2"), h.Target.Jobs[1], "job 2");
                CheckEqual(Ascii("RECEIPT-3"), h.Target.Jobs[2], "job 3");
            }
        }

        private static void Listener_NoIdle()
        {
            using (var h = new Harness(false, 0))
            {
                using (TcpClient c = h.Connect())
                using (NetworkStream ns = c.GetStream())
                {
                    ns.Write(Ascii("part1"), 0, 5);
                    Thread.Sleep(700);
                    Check(h.Target.JobCount == 0, "job must not close while connected");
                    ns.Write(Ascii("part2"), 0, 5);
                }
                Check(h.WaitForJobs(1, 3000), "job on disconnect");
                CheckEqual(Ascii("part1part2"), h.Target.Jobs[0], "combined job");
            }
        }

        private static void Listener_StatusQuery()
        {
            using (var h = new Harness(true, 1500))
            using (TcpClient c = h.Connect())
            using (NetworkStream ns = c.GetStream())
            {
                c.ReceiveTimeout = 3000;
                ns.Write(Bytes(0x10, 0x04, 0x01), 0, 3);
                int reply = ns.ReadByte();
                Check(reply == 0x16, "expected status 0x16, got " + reply);
                ns.Write(Bytes(0x10, 0x04, 0x02), 0, 3);
                Check(ns.ReadByte() == 0x12, "second status reply");
                Thread.Sleep(200);
                Check(h.Target.JobCount == 0, "status requests must not create jobs");
                ns.Write(Ascii("X"), 0, 1);
                c.Close();
                Check(h.WaitForJobs(1, 3000), "job after data");
                CheckEqual(Ascii("X"), h.Target.Jobs[0], "job content excludes status requests");
            }
        }

        private static void Listener_StatusDisabled()
        {
            using (var h = new Harness(false, 1500))
            {
                using (TcpClient c = h.Connect())
                using (NetworkStream ns = c.GetStream())
                {
                    ns.Write(Bytes(0x10, 0x04, 0x01, 0x41), 0, 4);
                }
                Check(h.WaitForJobs(1, 3000), "job");
                CheckEqual(Bytes(0x10, 0x04, 0x01, 0x41), h.Target.Jobs[0], "raw pass-through");
            }
        }

        private static void Listener_Concurrent()
        {
            using (var h = new Harness(true, 1500))
            {
                const int clients = 8;
                var payloads = new byte[clients][];
                var rnd = new Random(99);
                for (int i = 0; i < clients; i++)
                {
                    payloads[i] = new byte[200 * 1024];
                    rnd.NextBytes(payloads[i]);
                    // Strip every query lead byte: random data would otherwise contain real DLE/GS queries,
                    // which the responder correctly consumes and answers instead of forwarding.
                    for (int k = 0; k < payloads[i].Length; k++)
                        if (payloads[i][k] == 0x10) payloads[i][k] = 0x11;
                        else if (payloads[i][k] == 0x1D) payloads[i][k] = 0x1E;
                    payloads[i][0] = (byte)i; // tag
                }
                Parallel.For(0, clients, i =>
                {
                    using (TcpClient c = h.Connect())
                    using (NetworkStream ns = c.GetStream())
                    {
                        int off = 0;
                        while (off < payloads[i].Length)
                        {
                            int n = Math.Min(7000, payloads[i].Length - off);
                            ns.Write(payloads[i], off, n);
                            off += n;
                        }
                    }
                });
                Check(h.WaitForJobs(clients, 10000), "all jobs arrived: " + h.Target.JobCount);
                byte[][] jobs = h.Target.Jobs;
                for (int i = 0; i < clients; i++)
                {
                    byte[] job = jobs.FirstOrDefault(j => j.Length > 0 && j[0] == (byte)i);
                    Check(job != null, "job for client " + i + " missing");
                    CheckEqual(payloads[i], job, "client " + i);
                }
            }
        }

        private static void Listener_StopFlushes()
        {
            var h = new Harness(false, 0);
            var c = h.Connect();
            NetworkStream ns = c.GetStream();
            ns.Write(Ascii("partial"), 0, 7);
            Thread.Sleep(300);
            h.Listener.Stop();
            Check(h.WaitForJobs(1, 3000), "job flushed on stop");
            CheckEqual(Ascii("partial"), h.Target.Jobs[0], "partial content");
            Check(h.Listener.State == BridgeState.Stopped, "state stopped");
            try { c.Close(); } catch { }
        }

        private static void Listener_HalfClose()
        {
            using (var h = new Harness(true, 0))
            using (TcpClient c = h.Connect())
            {
                NetworkStream ns = c.GetStream();
                ns.Write(Ascii("doc"), 0, 3);
                c.Client.Shutdown(SocketShutdown.Send);
                Check(h.WaitForJobs(1, 3000), "job after half-close");
                CheckEqual(Ascii("doc"), h.Target.Jobs[0], "content");
            }
        }

        // ------------------------------------------------------------------ no cut

        private static readonly byte[] Cut4 = { 0x1D, 0x56, 0x42, 0x00 };   // GS V 66 0: feed and partial cut
        private static readonly byte[] Cut3 = { 0x1D, 0x56, 0x01 };         // GS V 1: partial cut

        private static byte[] Feed(int lines) { return Bytes(0x1B, 0x64, lines); }

        private static byte[] RunCutFilter(byte[] input, int feedLines)
        {
            var f = new EscPosCutFilter { FeedLines = feedLines };
            f.Filter(input, 0, input.Length);
            byte[] a = Slice(f.Output, f.OutputLength);
            f.Flush();
            byte[] b = Slice(f.Output, f.OutputLength);
            return Concat(a, b);
        }

        private static void NoCut_RemovesCuts()
        {
            byte[] input = Concat(
                Ascii("One\n"), Cut4,
                Ascii("Two\n"), Cut3,
                Ascii("Three\n"), Bytes(0x1B, 0x69),          // ESC i
                Ascii("Four\n"), Bytes(0x1B, 0x6D),           // ESC m
                Ascii("Five\n"), Bytes(0x1D, 0x56, 0x00),     // GS V 0 full cut
                Ascii("Six\n"), Bytes(0x1D, 0x56, 0x31),      // GS V 49
                Ascii("Seven\n"), Bytes(0x1D, 0x56, 0x41, 0x10)); // GS V 65 16 feed and full cut
            var f = new EscPosCutFilter { FeedLines = 4 };
            int events = 0;
            f.CutRemoved += name => events++;
            f.Filter(input, 0, input.Length);
            byte[] expected = Concat(
                Ascii("One\n"), Feed(4), Ascii("Two\n"), Feed(4), Ascii("Three\n"), Feed(4), Ascii("Four\n"), Feed(4),
                Ascii("Five\n"), Feed(4), Ascii("Six\n"), Feed(4), Ascii("Seven\n"), Feed(4));
            CheckEqual(expected, Slice(f.Output, f.OutputLength), "all seven cut forms replaced by ESC d 4");
            Check(f.CutsRemoved == 7 && events == 7, "seven cuts counted and reported, got " + f.CutsRemoved + "/" + events);
            f.Flush();
            Check(f.OutputLength == 0, "nothing held back at the end");

            CheckEqual(Concat(Ascii("A\n"), Feed(3)), RunCutFilter(Concat(Ascii("A\n"), Cut4, Cut4), 3), "two cuts in a row give one feed");
            CheckEqual(Ascii("A\n"), RunCutFilter(Concat(Ascii("A\n"), Cut4), 0), "feed 0 removes the cut and adds nothing");
            byte[] initOnly = Concat(Bytes(0x1B, 0x40), Bytes(0x1B, 0x64, 0x02), Cut4);
            CheckEqual(Concat(Bytes(0x1B, 0x40), Bytes(0x1B, 0x64, 0x02)), RunCutFilter(initOnly, 4), "a cut with nothing printed before it gets no feed");
            CheckEqual(Concat(Ascii("A\n"), Feed(4), Bytes(0x1B, 0x70, 0x00, 0x19, 0xFA)), RunCutFilter(Concat(Ascii("A\n"), Cut4, Bytes(0x1B, 0x70, 0x00, 0x19, 0xFA)), 4), "drawer pulse after the cut is kept");
        }

        private static void NoCut_PayloadsSurvive()
        {
            byte[] raster = Concat(Bytes(0x1D, 0x76, 0x30, 0x00, 0x02, 0x00, 0x02, 0x00), Cut4);          // 2 bytes x 2 rows, payload is a cut
            byte[] qr = Concat(Bytes(0x1D, 0x28, 0x6B, 0x04, 0x00, 0x31, 0x50), Bytes(0x1D, 0x56));       // GS ( k, payload holds GS V
            byte[] bitImage = Concat(Bytes(0x1B, 0x2A, 0x00, 0x04, 0x00), Cut4);                          // ESC * 0, 4 columns, payload is a cut
            byte[] graphics = Concat(Bytes(0x1D, 0x28, 0x4C, 0x04, 0x00), Cut4);                          // GS ( L, payload is a cut
            byte[] big = Concat(Bytes(0x1D, 0x38, 0x4C, 0x03, 0x00, 0x00, 0x00), Cut3);                   // GS 8 L, payload is a cut
            byte[] barcode = Concat(Bytes(0x1D, 0x6B, 0x49, 0x04), Cut4);                                 // GS k 73 (length-prefixed), payload is a cut
            byte[] input = Concat(Ascii("Logo\n"), raster, qr, bitImage, graphics, big, barcode, Cut4);
            byte[] expected = Concat(Ascii("Logo\n"), raster, qr, bitImage, graphics, big, barcode, Feed(4));
            CheckEqual(expected, RunCutFilter(input, 4), "payload bytes untouched, only the real cut replaced");

            // The same stream with the switch off through the wrapper must be byte-identical.
            var memory = new MemoryPrintTarget();
            using (IPrintJob job = new NoCutPrintTarget(memory, () => false).StartJob("t")) { job.Write(input, 0, input.Length); job.Complete(); }
            CheckEqual(input, memory.Jobs[0], "switch off: nothing changes");
        }

        private static void NoCut_SplitAnywhere()
        {
            byte[] raster = Concat(Bytes(0x1D, 0x76, 0x30, 0x00, 0x02, 0x00, 0x02, 0x00), Cut4);
            byte[] qr = Concat(Bytes(0x1D, 0x28, 0x6B, 0x04, 0x00, 0x31, 0x50), Bytes(0x1D, 0x56));
            byte[] stream = Concat(Bytes(0x1B, 0x40), Ascii("Shop\n"), raster, qr, Bytes(0x1B, 0x70, 0x00, 0x19, 0xFA),
                Bytes(0x1B, 0x64, 0x02), Cut4, Ascii("Kitchen\n"), Cut3, Bytes(0x10, 0x14, 0x01, 0x00, 0x05), Bytes(0x10, 0x41), Ascii("x"), Bytes(0x1B, 0x69));
            byte[] reference = RunCutFilter(stream, 4);
            Check(reference.Length == stream.Length - Cut4.Length - Cut3.Length - 2 + 3 * 3, "reference removed three cuts and added three feeds");
            for (int cut = 1; cut < stream.Length; cut++)
            {
                var f = new EscPosCutFilter { FeedLines = 4 };
                f.Filter(stream, 0, cut);
                byte[] a = Slice(f.Output, f.OutputLength);
                f.Filter(stream, cut, stream.Length - cut);
                byte[] b = Slice(f.Output, f.OutputLength);
                f.Flush();
                byte[] c = Slice(f.Output, f.OutputLength);
                CheckEqual(reference, Concat(a, b, c), "split at " + cut);
            }
            // Byte by byte, the worst case.
            var one = new EscPosCutFilter { FeedLines = 4 };
            var outBytes = new List<byte>();
            for (int i = 0; i < stream.Length; i++) { one.Filter(stream, i, 1); outBytes.AddRange(Slice(one.Output, one.OutputLength)); }
            one.Flush(); outBytes.AddRange(Slice(one.Output, one.OutputLength));
            CheckEqual(reference, outBytes.ToArray(), "byte by byte");
        }

        private static void NoCut_PassThrough()
        {
            byte[] realistic = Concat(
                Bytes(0x1B, 0x40), Bytes(0x1B, 0x61, 0x01), Bytes(0x1B, 0x21, 0x30), Ascii("SHOP\n"), Bytes(0x1B, 0x21, 0x00),
                Bytes(0x1D, 0x21, 0x11), Ascii("Total 12.50\n"), Bytes(0x1D, 0x21, 0x00), Bytes(0x1B, 0x74, 0x10), Bytes(0xA9, 0xE9),
                Bytes(0x1D, 0x48, 0x02), Bytes(0x1D, 0x68, 0x50), Bytes(0x1D, 0x6B, 0x04), Ascii("12345"), Bytes(0x00),
                Bytes(0x1B, 0x7A, 0x01), Bytes(0x1D, 0x99), Bytes(0x1C, 0x21, 0x00),   // unknown ESC z, unknown GS 0x99, FS !
                Bytes(0x10, 0x04, 0x01), Bytes(0x10, 0x14, 0x01, 0x00, 0x05), Bytes(0x1B, 0x70, 0x00, 0x32, 0x32), Bytes(0x1B, 0x64, 0x05),
                Bytes(0x1B, 0x44, 0x08, 0x10, 0x00), Bytes(0x1D, 0x28, 0x45, 0x03, 0x00, 0x01, 0x02, 0x03), Bytes(0x0C), Bytes(0x1B));
            CheckEqual(realistic, RunCutFilter(realistic, 4), "realistic receipt without cuts is untouched (incl. a trailing lone ESC)");

            var rnd = new Random(1234);
            var blob = new byte[256 * 1024];
            rnd.NextBytes(blob);
            for (int i = 0; i + 1 < blob.Length; i++)
            {
                if (blob[i] == 0x1D && blob[i + 1] == 0x56) blob[i + 1] = 0x00;
                if (blob[i] == 0x1B && (blob[i + 1] == 0x69 || blob[i + 1] == 0x6D)) blob[i + 1] = 0x00;
            }
            var f = new EscPosCutFilter { FeedLines = 4 };
            var collected = new MemoryStream();
            int pos = 0;
            while (pos < blob.Length)
            {
                int n = Math.Min(blob.Length - pos, 1 + rnd.Next(5000));
                f.Filter(blob, pos, n);
                collected.Write(f.Output, 0, f.OutputLength);
                pos += n;
            }
            f.Flush();
            collected.Write(f.Output, 0, f.OutputLength);
            CheckEqual(blob, collected.ToArray(), "256 KB of random bytes without cut sequences survive intact");
            Check(f.CutsRemoved == 0, "no cuts reported in random data");
        }

        private static void NoCut_WrapperFollowsSwitch()
        {
            var memory = new MemoryPrintTarget();
            bool on = false;
            var wrapped = new NoCutPrintTarget(memory, () => on);
            NoCutSettings.FeedLines = 4;
            long removedBefore = NoCutSettings.CutsRemoved;
            using (IPrintJob job = wrapped.StartJob("mid-job"))
            {
                byte[] a = Concat(Ascii("A\n"), Cut4);
                job.Write(a, 0, a.Length);
                on = true;
                byte[] b = Concat(Ascii("B\n"), Cut4);
                job.Write(b, 0, b.Length);
                byte[] half = Bytes(0x1D, 0x56);   // half a cut, held back
                job.Write(half, 0, half.Length);
                on = false;
                byte[] c = Concat(Bytes(0x42, 0x00), Ascii("C\n"), Cut4);   // completes the held cut: released unchanged since the switch is off
                job.Write(c, 0, c.Length);
                job.Complete();
            }
            byte[] expected = Concat(Ascii("A\n"), Cut4, Ascii("B\n"), Feed(4), Bytes(0x1D, 0x56), Bytes(0x42, 0x00), Ascii("C\n"), Cut4);
            CheckEqual(expected, memory.Jobs[0], "only the part written while the switch was on is filtered");
            Check(NoCutSettings.CutsRemoved == removedBefore + 1, "global counter went up by one");
            Check(wrapped.Name == memory.Name, "wrapper keeps the name");
            Check(ReferenceEquals(NoCutPrintTarget.Unwrap(wrapped), memory), "unwrap returns the inner target");
        }

        /// <summary>NO CUT is per printer: two bridges, one with the switch on, get the same receipt and only one loses its cut.</summary>
        private static void NoCut_Listener()
        {
            NoCutSettings.FeedLines = 4;
            byte[] receipt = Concat(Bytes(0x1B, 0x40), Ascii("Receipt 1\n\n"), Bytes(0x10, 0x04, 0x01), Cut4);
            using (var noCut = new Harness(true, 1500))
            using (var cuts = new Harness(true, 1500))
            {
                noCut.Mapping.NoCut = true;      // ticked while running, exactly what the grid does
                cuts.Mapping.NoCut = false;
                foreach (Harness h in new[] { noCut, cuts })
                {
                    using (TcpClient c = h.Connect())
                    using (NetworkStream ns = c.GetStream())
                    {
                        ns.Write(receipt, 0, receipt.Length);
                        Check(ns.ReadByte() == 0x16, "status query still answered");
                    }
                    Check(h.WaitForJobs(1, 5000), "job arrived");
                    Check(h.Listener.JobsCompleted == 1, "job counted");
                }
                CheckEqual(Concat(Bytes(0x1B, 0x40), Ascii("Receipt 1\n\n"), Feed(4)), noCut.Target.Jobs[0], "the NO CUT printer gets a feed instead of the cut");
                CheckEqual(Concat(Bytes(0x1B, 0x40), Ascii("Receipt 1\n\n"), Cut4), cuts.Target.Jobs[0], "the other printer still gets its cut");

                // Untick while the bridge runs: the next receipt on the same listener keeps its cut.
                // (Read the status reply before closing: closing with an unread reply makes the client's stack send a
                // reset, and a reset can discard data the server has not read yet. Real POS apps read their replies.)
                noCut.Mapping.NoCut = false;
                using (TcpClient c = noCut.Connect())
                using (NetworkStream ns = c.GetStream())
                {
                    ns.Write(receipt, 0, receipt.Length);
                    Check(ns.ReadByte() == 0x16, "status query answered on the second connection");
                }
                Check(noCut.WaitForJobs(2, 5000), "second job arrived");
                CheckEqual(Concat(Bytes(0x1B, 0x40), Ascii("Receipt 1\n\n"), Cut4), noCut.Target.Jobs[1], "after unticking, the cut goes through again");
            }
            List<PrinterAction> actions = PrinterActionLog.Snapshot();
            Check(actions.Exists(a => a.What.StartsWith("Cut removed") && a.Detail.Contains("GS V 66 0")), "the printer actions log names the removed cut");
            Check(actions.Exists(a => a.What.StartsWith("Sent ") && a.Detail.Contains("line") && a.Detail.Contains("init") && a.HasTicket && a.Ticket.Contains("Receipt 1")), "the printer actions log describes the job and carries the ticket text");
            Check(actions.Exists(a => a.What.StartsWith("Answered 1 printer query") && a.Detail.Contains("DLE EOT 1")), "the printer actions log lists the answered query");
        }

        private static void Ticket_Renders()
        {
            byte[] job = Concat(
                Bytes(0x1B, 0x40), Bytes(0x1B, 0x61, 0x01), Ascii("ACME STORE\n"), Bytes(0x1B, 0x61, 0x00), Ascii("Total   12.50\r\n"),
                Bytes(0x1D, 0x76, 0x30, 0x00, 0x02, 0x00, 0x02, 0x00), Cut4,                                // raster 16x2, payload is a cut
                Bytes(0x1D, 0x28, 0x6B, 0x09, 0x00, 0x31, 0x50, 0x30), Ascii("HELLO!"),                   // QR store data
                Bytes(0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x51, 0x30),                                    // QR print
                Bytes(0x1D, 0x6B, 0x04), Ascii("12345"), Bytes(0x00),                                      // barcode, NUL-terminated
                Bytes(0x1D, 0x6B, 0x49, 0x03), Ascii("ABC"),                                               // barcode, length-prefixed
                Bytes(0x1B, 0x70, 0x00, 0x32, 0x32), Bytes(0x1B, 0x64, 0x02),                              // drawer, feed 2
                Bytes(0x1B, 0x61, 0x02), Ascii("Thanks\n"),                                                // right aligned
                Cut4);
            string text = EscPosTicketText.Render(job, job.Length);
            Console.WriteLine("      ticket:\n" + Indent(text));
            string[] expected =
            {
                new string(' ', 19) + "ACME STORE",
                "Total   12.50",
                "[image 16x2]",
                "[QR data: HELLO!]",
                "[QR code]",
                "[barcode: 12345]",
                "[barcode: ABC]",
                "[drawer opened]",
                "",
                "",
                new string(' ', 42) + "Thanks",
                "- - - - - - - - - -  cut  - - - - - - - - - -"
            };
            CheckEqual(Encoding.UTF8.GetBytes(string.Join("\n", expected)), Encoding.UTF8.GetBytes(text), "rendered ticket");

            Check(EscPosTicketText.Render(null, 0) == "", "empty job");
            Check(EscPosTicketText.Render(Bytes(0x10, 0x04, 0x01), 3) == "", "a bare status query renders as nothing");
            byte[] big = Concat(Ascii("X\n"), Enumerable.Repeat(Ascii("line of text\n"), 2000).SelectMany(b => b).ToArray());
            string capped = EscPosTicketText.Render(big, big.Length, 500);
            Check(capped.Length < 600 && capped.Contains("truncated"), "long tickets are capped");

            PrintHistory.Clear();
            PrintRecord rec = PrintHistory.Add("10.0.0.1:1", "P", "raw 9100", job, job.Length, "Printed");
            Check(rec.Ticket.Contains("ACME STORE") && rec.Ticket.Contains("[QR data: HELLO!]"), "the print log record carries the ticket");

            PrinterActionLog.Clear();
            PrinterAction a = PrinterActionLog.Add(ActionLevel.Info, "P", "10.0.0.1:1", "Sent 1 KB", "init; 3 lines", text);
            Check(a.HasTicket && a.FullText.Contains("      | Total   12.50") && a.FullText.Contains("      | [barcode: 12345]"), "the actions log line carries the ticket, indented");
            Check(File.ReadAllText(PrinterActionLog.FilePathFor(DateTime.Now)).Contains("| [QR data: HELLO!]"), "the daily file holds the ticket");
        }

        private static string Indent(string text)
        {
            return "        " + text.Replace("\n", "\n        ");
        }

        // ------------------------------------------------------------------ job summary / actions log / status

        private static void Summary_Describes()
        {
            byte[] job = Concat(
                Bytes(0x1B, 0x40), Bytes(0x1B, 0x61, 0x01), Ascii("ACME STORE\n"), Bytes(0x1D, 0x21, 0x11), Ascii("Total 12.50\n"),
                Bytes(0x1D, 0x76, 0x30, 0x00, 0x02, 0x00, 0x02, 0x00), Cut4,                     // raster whose payload is a cut
                Bytes(0x1D, 0x28, 0x6B, 0x06, 0x00, 0x31, 0x50), Ascii("HELL"),                    // QR data (pL = cn fn + 4 bytes)
                Bytes(0x1D, 0x6B, 0x04), Ascii("12345"), Bytes(0x00),                              // barcode
                Bytes(0x1B, 0x70, 0x00, 0x32, 0x32), Bytes(0x1B, 0x64, 0x03),                     // drawer, feed
                Bytes(0x1B, 0x7A, 0x01), Bytes(0x1B, 0x7A, 0x02),                                  // unknown ESC z, twice
                Bytes(0x1D, 0x28, 0x45, 0x03, 0x00, 0x01, 0x02, 0x03),                            // GS ( E settings
                Cut4);
            EscPosJobSummary s = EscPosJobSummary.Analyze(job, job.Length);
            string text = s.Describe();
            Console.WriteLine("      summary: " + text);
            Check(s.Count(EscPosKind.Init) == 1 && s.Count(EscPosKind.Cut) == 1 && s.Count(EscPosKind.Image) == 1, "init, one real cut, one image (payload cut not counted)");
            Check(s.Count(EscPosKind.Symbol) == 1 && s.Count(EscPosKind.Barcode) == 1 && s.Count(EscPosKind.Drawer) == 1 && s.Count(EscPosKind.Feed) == 1, "QR, barcode, drawer, feed");
            Check(s.TextLines == 2, "two lines of text, got " + s.TextLines);
            Check(s.UnknownCommands.Count == 1 && s.UnknownCommands["ESC 0x7A"] == 2, "unknown ESC z counted twice");
            Check(text.Contains("init") && text.Contains("2 lines of text") && text.Contains("image") && text.Contains("QR") && text.Contains("barcode") && text.Contains("drawer pulse") && text.Contains("cut") && text.Contains("unknown: ESC 0x7A x2"), "description mentions everything: " + text);
            List<string> problems = s.Problems();
            Check(problems.Count == 2, "two problems: unknown commands and a settings change, got " + problems.Count);
            Check(problems.Exists(p => p.Contains("GS ( E")), "settings change flagged");
            Check(!s.EndedInsideCommand, "complete job");

            byte[] truncated = Concat(Ascii("Hi\n"), Bytes(0x1D, 0x56));
            EscPosJobSummary t = EscPosJobSummary.Analyze(truncated, truncated.Length);
            Check(t.EndedInsideCommand && t.Describe().Contains("truncated"), "a job ending inside a command is flagged");
            Check(EscPosJobSummary.Analyze(null, 0).Describe() == "no printable content", "empty job");
            Check(EscPosJobSummary.Analyze(Bytes(0x1B, 0x70, 0x00, 0x32, 0x32), 5).Describe() == "drawer pulse", "a bare drawer pulse is described as such");
        }

        private static void Summary_ForeignFormat()
        {
            Check(Format(Ascii("%!PS-Adobe-3.0\n/Helvetica findfont")) == "PostScript", "PostScript");
            Check(Format(Ascii("%PDF-1.7\n")) == "PDF", "PDF");
            byte[] pjl = Concat(Bytes(0x1B), Ascii("%-12345X@PJL JOB\n"));
            Check(Format(pjl).Contains("PJL"), "PJL/PCL");
            Check(Format(Ascii("^XA^FO50,50^ADN,36,20^FDHello^FS^XZ")).Contains("ZPL"), "ZPL");
            byte[] escpos = Concat(Bytes(0x1B, 0x40), Ascii("Hello\n"), Cut4);
            Check(EscPosJobSummary.Analyze(escpos, escpos.Length).ForeignFormat == null, "ESC/POS is not flagged");
            byte[] bitImage = Concat(Bytes(0x1B, 0x2A, 0x00, 0x02, 0x00, 0xFF, 0xFF));
            Check(EscPosJobSummary.Analyze(bitImage, bitImage.Length).ForeignFormat == null, "an ESC * bit image is not mistaken for PCL");
            List<string> problems = EscPosJobSummary.Analyze(pjl, pjl.Length).Problems();
            Check(problems.Count == 1 && problems[0].Contains("not ESC/POS"), "foreign format is the one problem reported");
        }

        private static string Format(byte[] data)
        {
            return EscPosJobSummary.Analyze(data, data.Length).ForeignFormat ?? "";
        }

        private static void Actions_Log()
        {
            ConfigStore.DataDirectory = Path.Combine(OutDir, "data");
            PrinterActionLog.Clear();
            int added = 0;
            Action<PrinterAction> handler = a => added++;
            PrinterActionLog.ActionAdded += handler;
            try
            {
                PrinterActionLog.Info("EPSON TM", "192.168.1.55:5000", "Sent 1.2 KB to the printer", "init; 3 lines of text; cut");
                PrinterActionLog.Warn("EPSON TM", "watch", "Printer reports: Paper out", "Load a new roll.");
                PrinterActionLog.Error("EPSON TM", "spooler", "Printer stopped accepting data", "The printer is offline (error 1906)");
                Check(PrinterActionLog.Count == 3 && PrinterActionLog.ErrorCount == 1 && PrinterActionLog.WarningCount == 1, "three records, one error, one warning");
                Check(added == 3, "event raised for each record");
                List<PrinterAction> all = PrinterActionLog.Snapshot();
                Check(all[2].Level == ActionLevel.Error && all[2].Line.Contains("ERROR") && all[2].Line.Contains("[EPSON TM]") && all[2].Line.Contains("offline"), "line format: " + all[2].Line);
                string file = PrinterActionLog.FilePathFor(DateTime.Now);
                Check(File.Exists(file), "daily file written: " + file);
                string content = File.ReadAllText(file);
                Check(content.Contains("Paper out") && content.Contains("Printer stopped accepting data"), "file holds the records");
                PrinterActionLog.Clear();
                Check(PrinterActionLog.Count == 0 && PrinterActionLog.ErrorCount == 0, "clear resets counts");
            }
            finally
            {
                PrinterActionLog.ActionAdded -= handler;
            }
        }

        private static void Device_Events()
        {
            DeviceEvent e1 = DeviceMonitor.ToEvent(new PrinterAction { Time = DateTime.Now, Level = ActionLevel.Info, Printer = "TM", Source = "10.0.0.5:1", What = "Sent 1.2 KB to the printer (idle)", Detail = "init; 3 lines" });
            Check(e1 != null && e1.Kind == DeviceEventKind.Printed && e1.Text.StartsWith("Printed 1.2 KB") && e1.Text.Contains("TM") && e1.Text.Contains("10.0.0.5"), "a sent job is a green printed event: " + (e1 == null ? "null" : e1.Text));
            DeviceEvent e2 = DeviceMonitor.ToEvent(new PrinterAction { Time = DateTime.Now, Level = ActionLevel.Info, Printer = "TM", Source = "x", What = "Cut removed (NO CUT is on for this printer)", Detail = "GS V 66 0 (feed and cut) was not sent to the printer; a 4-line feed was sent instead" });
            Check(e2 != null && e2.Kind == DeviceEventKind.Warning && e2.Text.Contains("GS V 66 0"), "a removed cut is a warning event naming the command");
            DeviceEvent e3 = DeviceMonitor.ToEvent(new PrinterAction { Time = DateTime.Now, Level = ActionLevel.Warn, Printer = "TM", Source = "watch", What = "Printer reports: Paper out", Detail = "Load a roll." });
            Check(e3 != null && e3.Kind == DeviceEventKind.Offline, "a printer status change is an offline event");
            DeviceEvent e4 = DeviceMonitor.ToEvent(new PrinterAction { Time = DateTime.Now, Level = ActionLevel.Error, Printer = "TM", Source = "spooler", What = "Printer stopped accepting data after 300 bytes", Detail = "The printer is offline (error 1906). More text." });
            Check(e4 != null && e4.Kind == DeviceEventKind.Failed && e4.Text.Contains("error 1906") && !e4.Text.Contains("More text"), "an error is a failed event with the first clause of the detail");
            Check(DeviceMonitor.ToEvent(new PrinterAction { Time = DateTime.Now, Level = ActionLevel.Info, Printer = "TM", Source = "x", What = "Answered 2 printer queries on the printer's behalf", Detail = "" }) == null, "answered queries are not events");
            DeviceEvent e5 = DeviceMonitor.ToEvent(new PrinterAction { Time = DateTime.Now, Level = ActionLevel.Warn, Printer = "this PC", Source = "user", What = "Cooling: fans to max for 10 min", Detail = "System cooling policy set to Active." });
            Check(e5 != null && e5.Kind == DeviceEventKind.Warning && e5.Text.StartsWith("Cooling"), "cooling actions are warning events");
        }

        private static void Device_SampleAndTelemetry()
        {
            ConfigStore.DataDirectory = Path.Combine(OutDir, "data");
            using (var mon = new DeviceMonitor(() => 12345))
            {
                mon.ClearTelemetry();
                PrinterActionLog.Info("TM", "10.0.0.9:2", "Sent 900 B to the printer (idle)", "init; 2 lines");
                DeviceSnapshot s = mon.SampleOnce();
                Console.WriteLine("      cpu " + Math.Round(s.CpuPercent) + " % (" + s.CpuQuality + "), ram " + Math.Round(s.RamPercent) + " % of " + s.RamTotalGb.ToString("0.0") + " GB, gpu " + (s.GpuPercent.HasValue ? Math.Round(s.GpuPercent.Value) + " %" : "n/a") + " " + s.GpuName + ", temps " + s.TempQuality + " " + s.TempSource + ", net " + s.NetLabel + " " + (s.WifiSignalPercent.HasValue ? s.WifiSignalPercent + " %" : "") + ", usb " + s.UsbPrintersOnline + "/" + s.UsbPrinters + ", battery " + (s.Battery.Present ? s.Battery.Percent + " % " + s.Battery.StateText : "none"));
                Check(s.RamTotalGb > 0.5 && s.RamPercent > 0 && s.RamPercent <= 100, "RAM read");
                Check(s.Cores > 0, "core count");
                Check(s.Cpu != null && s.Cpu.Length == DeviceMonitor.HistorySeconds && s.Cpu[DeviceMonitor.HistorySeconds - 1].HasValue == (s.CpuQuality != ReadingQuality.NotAvailable), "history ring holds the newest sample last");
                Check(s.Events.Count >= 1 && s.Events[0].Kind == DeviceEventKind.Printed, "the printer action arrived as an event");
                string file = DeviceMonitor.TelemetryFileFor(DateTime.Now);
                Check(File.Exists(file), "telemetry file written: " + file);
                string[] lines;
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))   // the monitor keeps it open for appending
                using (var reader = new StreamReader(fs)) lines = reader.ReadToEnd().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
                Check(lines.Length >= 2 && lines[0].StartsWith("Time,CpuPct,") && lines[1].StartsWith(DateTime.Now.ToString("yyyy-MM-dd")), "header and one timestamped row");
                Check(lines[1].Contains("Sent 900 B") || lines[1].Contains("Printed 900 B"), "the event text rides in the row: " + lines[1]);
                Check(lines[1].Split(',').Length >= 26, "all columns present (" + lines[1].Split(',').Length + ")");
                int deleted = mon.ClearTelemetry();
                Check(deleted >= 1 && !File.Exists(file), "clear deleted the file");
                Check(mon.Snapshot().Events.Count == 0 || mon.SampleOnce().Events.Count == 0, "clear emptied the event list");
                mon.ClearTelemetry();
            }
        }

        private static void Epos_DeviceId()
        {
            var target = new MemoryPrintTarget("devid-test");
            var server = new HttpBridgeServer(target, null, () => "kitchen");
            server.Start(IPAddress.Loopback, 0);
            try
            {
                int port = server.LocalEndPoint.Port;
                byte[] job;
                string wrong = HttpPost(IPAddress.Loopback, port, "/cgi-bin/epos/service.cgi?devid=bar&timeout=5000", "<epos-print xmlns=\"" + EposPrintConverter.EposNamespace + "\"><text>Hi\n</text></epos-print>", out job, target);
                Check(wrong.Contains("success=\"false\"") && wrong.Contains("DeviceNotFound"), "another device id is refused with DeviceNotFound: " + wrong.Split('\n').Last());
                Check(target.JobCount == 0, "nothing printed for the wrong id");
                string right = HttpPost(IPAddress.Loopback, port, "/cgi-bin/epos/service.cgi?devid=Kitchen&timeout=5000", "<epos-print xmlns=\"" + EposPrintConverter.EposNamespace + "\"><text>Hi\n</text></epos-print>", out job, target);
                Check(right.Contains("success=\"true\"") && target.JobCount == 1, "the mapping's id (case-insensitive) prints");
                string none = HttpPost(IPAddress.Loopback, port, "/cgi-bin/epos/service.cgi", "<epos-print xmlns=\"" + EposPrintConverter.EposNamespace + "\"><text>Hi\n</text></epos-print>", out job, target);
                Check(none.Contains("success=\"true\"") && target.JobCount == 2, "a request without a device id still prints");
                Check(HttpBridgeServer.QueryValue("/x?a=1&devid=my%20printer&b", "devid") == "my printer" && HttpBridgeServer.QueryValue("/x", "devid") == null, "query parsing");
            }
            finally { server.Stop(); }
        }

        private static void Http_CertPage()
        {
            ConfigStore.DataDirectory = Path.Combine(OutDir, "data");
            SelfSignedCertificate.GetOrCreate(new[] { IPAddress.Loopback });
            var target = new MemoryPrintTarget("cert-test");
            var server = new HttpBridgeServer(target, null, () => "local_printer");
            server.Start(IPAddress.Loopback, 0);
            try
            {
                int port = server.LocalEndPoint.Port;
                string page = HttpGet(IPAddress.Loopback, port, "/cert");
                Check(page.StartsWith("HTTP/1.1 200") && page.Contains("Trust this bridge") && page.Contains("/cert/UsbLanPrinterBridge.cer") && page.Contains("devid=local_printer"), "the http cert page points at the https link and the download");
                byte[] der = HttpGetBytes(IPAddress.Loopback, port, "/cert/UsbLanPrinterBridge.cer", "application/x-x509-ca-cert");
                Check(der != null && der.Length > 200, "the .cer download has a body");
                var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(der);
                Check(cert.Subject.Contains("USB LAN Printer Bridge"), "the download is the bridge's certificate: " + cert.Subject);
            }
            finally { server.Stop(); }
        }

        private static string HttpGet(IPAddress ip, int port, string path)
        {
            using (var c = new TcpClient()) { c.Connect(ip, port); using (NetworkStream ns = c.GetStream()) { byte[] req = Ascii("GET " + path + " HTTP/1.1\r\nHost: " + ip + ":" + port + "\r\nConnection: close\r\n\r\n"); ns.Write(req, 0, req.Length); using (var ms = new MemoryStream()) { ns.CopyTo(ms); return Encoding.UTF8.GetString(ms.ToArray()); } } }
        }

        private static byte[] HttpGetBytes(IPAddress ip, int port, string path, string expectedType)
        {
            using (var c = new TcpClient()) { c.Connect(ip, port); using (NetworkStream ns = c.GetStream()) { byte[] req = Ascii("GET " + path + " HTTP/1.1\r\nHost: " + ip + ":" + port + "\r\nConnection: close\r\n\r\n"); ns.Write(req, 0, req.Length); using (var ms = new MemoryStream()) { ns.CopyTo(ms); byte[] all = ms.ToArray(); string head = Encoding.ASCII.GetString(all, 0, Math.Min(all.Length, 600)); int sep = head.IndexOf("\r\n\r\n", StringComparison.Ordinal); if (sep < 0 || !head.Contains(expectedType)) return null; var body = new byte[all.Length - sep - 4]; Array.Copy(all, sep + 4, body, 0, body.Length); return body; } } }
        }

        private static void Config_DeviceId()
        {
            Check(MappingConfig.SanitizeDeviceId(null) == "local_printer" && MappingConfig.SanitizeDeviceId("  ") == "local_printer", "empty becomes local_printer");
            Check(MappingConfig.SanitizeDeviceId("kitchen printer #2!") == "kitchenprinter2", "unsafe characters dropped");
            Check(MappingConfig.SanitizeDeviceId("bar_1.a-b") == "bar_1.a-b", "letters, digits, _ - . kept");
            var m = new MappingConfig { BindAddress = "192.168.1.200", EposDeviceId = "kitchen" };
            Check(m.EposUrl(true, "192.168.1.200") == "https://192.168.1.200/cgi-bin/epos/service.cgi?devid=kitchen&timeout=10000", "https link: " + m.EposUrl(true, "192.168.1.200"));
            Check(m.EposUrl(false, "192.168.1.200") == "http://192.168.1.200/cgi-bin/epos/service.cgi?devid=kitchen&timeout=10000", "http link");
            m.EposHttpsPort = 8043;
            Check(m.CertificateUrl("192.168.1.200") == "https://192.168.1.200:8043/cert" && m.EposUrl(true, "h").Contains(":8043/"), "non-default port appears in the links");
            string path = Path.Combine(OutDir, "devid.xml");
            var cfg = new BridgeConfig();
            cfg.Mappings.Add(new MappingConfig { PrinterName = "P", BindAddress = "10.0.0.1", EposDeviceId = "bar" });
            cfg.Mappings.Add(new MappingConfig { PrinterName = "Q", BindAddress = "10.0.0.2", EposDeviceId = "" });
            ConfigStore.Save(cfg, path);
            BridgeConfig back = ConfigStore.Load(path);
            Check(back.Mappings[0].EposDeviceId == "bar" && back.Mappings[1].EposDeviceId == "local_printer", "device ids round-trip and empty is repaired on load");
        }

        private static void Status_Probe()
        {
            PrinterQueueStatus unknown = PrinterStatusProbe.Query("No Such Printer 12345");
            Check(!unknown.Available && unknown.Error.Contains("not installed"), "unknown printer: " + unknown.Error);
            Check(unknown.HasProblem && unknown.Describe().Contains("not installed"), "described as a problem");

            int before = PrinterActionLog.Count;
            PrinterWatch.Check("No Such Printer 12345", "test");
            Check(PrinterActionLog.Count == before + 1, "first check logs the missing printer");
            PrinterWatch.Check("No Such Printer 12345", "test");
            Check(PrinterActionLog.Count == before + 1, "second check with the same state logs nothing");
            PrinterWatch.Forget("No Such Printer 12345");

            const string printer = "Microsoft XPS Document Writer";
            if (!PrinterEnumerator.GetPrinters().Any(p => p.Name == printer)) throw new SkipException("XPS writer not installed");
            PrinterQueueStatus xps = PrinterStatusProbe.Query(printer);
            Console.WriteLine("      XPS writer: " + xps.Describe() + " (flags 0x" + xps.StatusFlags.ToString("X") + ", attributes 0x" + xps.Attributes.ToString("X") + ")");
            Check(xps.Available, "queue opened");
            Check(!xps.IsPaused, "not paused");
        }

        // ------------------------------------------------------------------ manager

        private static BridgeManager NewManager()
        {
            var mgr = new BridgeManager { AutoFirewallRule = false, JobIdleTimeoutMs = 200 };
            mgr.TargetFactory = m => new MemoryPrintTarget(m.PrinterName);
            return mgr;
        }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        private static void Manager_Loopback()
        {
            BridgeManager mgr = NewManager();
            var m = new MappingConfig { PrinterName = "Memory", BindAddress = "127.0.0.1", Port = FreePort() };
            StartOutcome r = mgr.StartMapping(m);
            Check(r.Success, "start: " + r.Message);
            Check(mgr.IsRunning(m), "running");
            BridgeListener l = mgr.GetListener(m);
            var target = (MemoryPrintTarget)l.Target;
            using (var c = new TcpClient("127.0.0.1", m.Port))
            using (NetworkStream ns = c.GetStream()) ns.Write(Ascii("hi"), 0, 2);
            var sw = Stopwatch.StartNew();
            while (target.JobCount < 1 && sw.ElapsedMilliseconds < 3000) Thread.Sleep(20);
            Check(target.JobCount == 1, "job through manager");
            Check(mgr.StartMapping(m).Success, "second start is a no-op");
            mgr.StopMapping(m);
            Check(!mgr.IsRunning(m), "stopped");
            Check(mgr.RunningCount == 0, "running count");
            mgr.StopAll();
        }

        /// <summary>
        /// Regression: a listener is cached per mapping id and survives stop/start, so re-pointing a row at a
        /// different printer used to leave the original target in place and every job kept reaching the old printer.
        /// </summary>
        private static void Manager_RetargetsPrinter()
        {
            BridgeManager mgr = NewManager();
            var m = new MappingConfig { PrinterName = "Printer A", BindAddress = "127.0.0.1", Port = FreePort() };
            Check(mgr.StartMapping(m).Success, "start on Printer A");

            var first = (MemoryPrintTarget)mgr.GetListener(m).Target;
            Send(m.Port, "one");
            Check(WaitForJobs(first, 1), "first job reached Printer A");

            mgr.StopMapping(m);

            // Exactly what the grid does when the user picks another printer: same mapping object, new name.
            m.PrinterName = "Printer B";
            Check(mgr.StartMapping(m).Success, "restart on Printer B");

            var second = (MemoryPrintTarget)mgr.GetListener(m).Target;
            Check(!ReferenceEquals(first, second), "the listener must build a new target for the new printer");
            Check(second.Name == "Printer B", "target follows the mapping, got \"" + second.Name + "\"");

            Send(m.Port, "two");
            Check(WaitForJobs(second, 1), "second job reached Printer B");
            Check(first.JobCount == 1, "Printer A must receive nothing after the switch, got " + first.JobCount + " jobs");
            CheckEqual(Ascii("two"), second.Jobs[0], "content went to the new printer");

            mgr.StopAll();
        }

        private static void Send(int port, string text)
        {
            using (var c = new TcpClient("127.0.0.1", port))
            using (NetworkStream ns = c.GetStream())
            {
                byte[] data = Ascii(text);
                ns.Write(data, 0, data.Length);
            }
        }

        private static bool WaitForJobs(MemoryPrintTarget target, int count)
        {
            var sw = Stopwatch.StartNew();
            while (target.JobCount < count && sw.ElapsedMilliseconds < 3000) Thread.Sleep(20);
            return target.JobCount >= count;
        }

        private static void Manager_Any()
        {
            BridgeManager mgr = NewManager();
            var m = new MappingConfig { PrinterName = "Memory", BindAddress = "0.0.0.0", Port = FreePort() };
            StartOutcome r = mgr.StartMapping(m);
            Check(r.Success, "start any: " + r.Message);
            using (var c = new TcpClient("127.0.0.1", m.Port)) { }
            mgr.StopAll();
            Check(!mgr.IsRunning(m), "stopped");
        }

        private static void Manager_PortInUse()
        {
            int port = FreePort();
            var blocker = new TcpListener(IPAddress.Loopback, port);
            blocker.ExclusiveAddressUse = true;
            blocker.Start();
            try
            {
                BridgeManager mgr = NewManager();
                var m = new MappingConfig { PrinterName = "Memory", BindAddress = "127.0.0.1", Port = port };
                StartOutcome r = mgr.StartMapping(m);
                Check(!r.Success, "must fail");
                Check(r.Message.Contains("already used") || r.Message.Contains("10048") || r.Message.Contains("10013"), "message: " + r.Message);
                Check(mgr.GetListener(m).State == BridgeState.Error, "state error");
                mgr.StopAll();
            }
            finally { blocker.Stop(); }
        }

        private static void Manager_Validation()
        {
            Check(BridgeManager.Validate(new MappingConfig { PrinterName = "", BindAddress = "1.2.3.4" }) != null, "printer required");
            Check(BridgeManager.Validate(new MappingConfig { PrinterName = "P", BindAddress = "999.1.1.1" }) != null, "bad ip");
            Check(BridgeManager.Validate(new MappingConfig { PrinterName = "P", BindAddress = "fe80::1" }) != null, "ipv6 rejected");
            Check(BridgeManager.Validate(new MappingConfig { PrinterName = "P", BindAddress = "10.0.0.5", Port = 0 }) != null, "bad port");
            Check(BridgeManager.Validate(new MappingConfig { PrinterName = "P", BindAddress = "10.0.0.5", Port = 9100 }) == null, "valid");
            Check(BridgeManager.Validate(new MappingConfig { PrinterName = "P", BindAddress = "", Port = 9100 }) == null, "empty = any");
            IPAddress ip;
            Check(BridgeManager.TryParseBindAddress(" 192.168.1.200 ", out ip) && ip.ToString() == "192.168.1.200", "trim");
        }

        private static void Manager_NoAdminHint()
        {
            BridgeManager mgr = NewManager();
            mgr.IsElevated = false;
            var m = new MappingConfig { PrinterName = "Memory", BindAddress = "203.0.113.77", Port = FreePort() };
            StartOutcome r = mgr.StartMapping(m);
            Check(!r.Success, "must fail without admin");
            Check(r.Message.IndexOf("administrator", StringComparison.OrdinalIgnoreCase) >= 0, "hint: " + r.Message);
            Check(!mgr.IsRunning(m), "not running");
        }

        // ------------------------------------------------------------------ config / network / printers

        // ------------------------------------------------------------------ ePOS conversion

        private static bool Contains(byte[] haystack, byte[] needle)
        {
            if (needle.Length == 0) return true;
            for (int i = 0; i + needle.Length <= haystack.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < needle.Length; j++) if (haystack[i + j] != needle[j]) { ok = false; break; }
                if (ok) return true;
            }
            return false;
        }

        private static byte[] Epos(string body)
        {
            IList<string> warnings;
            string xml = "<epos-print xmlns=\"http://www.epson-pos.com/schemas/2011/03/epos-print\">" + body + "</epos-print>";
            return EposPrintConverter.Convert(xml, out warnings);
        }

        private static void Epos_TextStyles()
        {
            byte[] r = Epos("<text align=\"center\"/><text dw=\"true\" dh=\"true\"/><text em=\"true\">Hi</text><text align=\"left\"/>");
            Check(Contains(r, Bytes(0x1B, 0x40)), "starts with ESC @");
            Check(Contains(r, Bytes(0x1B, 0x61, 0x01)), "align center ESC a 1");
            Check(Contains(r, Bytes(0x1D, 0x21, 0x11)), "double w+h GS ! 0x11");
            Check(Contains(r, Bytes(0x1B, 0x45, 0x01)), "bold ESC E 1");
            Check(Contains(r, Ascii("Hi")), "text present");
            Check(Contains(r, Bytes(0x1B, 0x61, 0x00)), "align left ESC a 0");
        }

        private static void Epos_Elements()
        {
            byte[] feed = Epos("<feed line=\"3\"/>");
            Check(Contains(feed, Bytes(0x1B, 0x64, 0x03)), "feed 3 lines ESC d 3");

            byte[] cut = Epos("<cut type=\"feed\"/>");
            Check(Contains(cut, Bytes(0x1D, 0x56, 0x42, 0x00)), "cut GS V 66 0");

            byte[] pulse = Epos("<pulse drawer=\"drawer_1\" time=\"pulse_100\"/>");
            Check(Contains(pulse, Bytes(0x1B, 0x70, 0x00, 0x32, 0x32)), "drawer ESC p 0 50 50");

            byte[] bc = Epos("<barcode type=\"code128\" hri=\"below\" height=\"80\">{A123</barcode>");
            Check(Contains(bc, Bytes(0x1D, 0x48, 0x02)), "HRI below GS H 2");
            Check(Contains(bc, Bytes(0x1D, 0x68, 0x50)), "barcode height GS h 80");
            Check(Contains(bc, Bytes(0x1D, 0x6B, 0x49)), "code128 GS k 73");
            Check(Contains(bc, Ascii("{A123")), "barcode data");

            byte[] qr = Epos("<symbol type=\"qrcode_model_2\" level=\"level_m\" size=\"5\">HELLO</symbol>");
            Check(Contains(qr, Bytes(0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x43, 0x05)), "QR module size 5");
            Check(Contains(qr, Bytes(0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x45, 0x31)), "QR level M");
            Check(Contains(qr, Ascii("HELLO")), "QR data");

            byte[] cmd = Epos("<command>1B401A0A</command>");
            Check(Contains(cmd, Bytes(0x1B, 0x40, 0x1A, 0x0A)), "raw command hex decoded");
        }

        private static void Epos_Image()
        {
            // 8x2 image, 1 byte per row.
            byte[] raster = { 0xFF, 0x81 };
            string b64 = System.Convert.ToBase64String(raster);
            byte[] r = Epos("<image width=\"8\" height=\"2\">" + b64 + "</image>");
            Check(Contains(r, Bytes(0x1D, 0x76, 0x30, 0x00, 0x01, 0x00, 0x02, 0x00, 0xFF, 0x81)), "GS v 0 raster header + data");
        }

        private static void Epos_SoapWrapped()
        {
            IList<string> warnings;
            string soap = "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body>" +
                          "<epos-print xmlns=\"http://www.epson-pos.com/schemas/2011/03/epos-print\"><text>X</text></epos-print>" +
                          "</s:Body></s:Envelope>";
            byte[] r = EposPrintConverter.Convert(soap, out warnings);
            Check(Contains(r, Ascii("X")), "text extracted from SOAP body");
        }

        private static void Epos_Malformed()
        {
            IList<string> warnings;
            try { EposPrintConverter.Convert("<epos-print><text>unclosed", out warnings); Check(false, "should throw"); }
            catch (CheckException) { throw; }
            catch (System.Xml.XmlException) { /* expected */ }
        }

        // ------------------------------------------------------------------ HTTP server

        private const string SamplePrint =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body>" +
            "<epos-print xmlns=\"http://www.epson-pos.com/schemas/2011/03/epos-print\">" +
            "<text align=\"center\">Receipt\n</text><cut type=\"feed\"/>" +
            "</epos-print></s:Body></s:Envelope>";

        private static string HttpPost(IPAddress ip, int port, string path, string body, out byte[] jobBytes, MemoryPrintTarget target)
        {
            using (var c = new TcpClient())
            {
                c.Connect(ip, port);
                using (NetworkStream ns = c.GetStream())
                {
                    byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
                    var head = "POST " + path + " HTTP/1.1\r\nHost: " + ip + "\r\nContent-Type: text/xml; charset=utf-8\r\n" +
                               "Content-Length: " + bodyBytes.Length + "\r\nConnection: close\r\n\r\n";
                    byte[] headBytes = Encoding.ASCII.GetBytes(head);
                    ns.Write(headBytes, 0, headBytes.Length);
                    ns.Write(bodyBytes, 0, bodyBytes.Length);
                    ns.Flush();
                    string resp = new StreamReader(ns, Encoding.UTF8).ReadToEnd();
                    jobBytes = target != null && target.JobCount > 0 ? target.Jobs[target.JobCount - 1] : null;
                    return resp;
                }
            }
        }

        private static void Http_PostPrints()
        {
            var target = new MemoryPrintTarget("http-test");
            var server = new HttpBridgeServer(target, null);
            server.Start(IPAddress.Loopback, 0);
            try
            {
                byte[] job;
                string resp = HttpPost(IPAddress.Loopback, server.LocalEndPoint.Port, "/cgi-bin/epos/service.cgi?devid=local_printer", SamplePrint, out job, target);
                Check(resp.Contains("200"), "HTTP 200: " + resp.Split('\n')[0]);
                Check(resp.Contains("success=\"true\""), "success true in response");
                Check(resp.Contains("Access-Control-Allow-Origin"), "CORS header present");
                Check(target.JobCount == 1, "one job printed");
                Check(Contains(job, Ascii("Receipt")), "receipt text reached printer");
                Check(Contains(job, Bytes(0x1D, 0x56, 0x42, 0x00)), "cut command reached printer");
                Check(server.Jobs == 1, "server job counter");
            }
            finally { server.Stop(); }
        }

        private static void Http_Preflight()
        {
            var target = new MemoryPrintTarget();
            var server = new HttpBridgeServer(target, null);
            server.Start(IPAddress.Loopback, 0);
            try
            {
                using (var c = new TcpClient())
                {
                    c.Connect(IPAddress.Loopback, server.LocalEndPoint.Port);
                    using (NetworkStream ns = c.GetStream())
                    {
                        string req = "OPTIONS /cgi-bin/epos/service.cgi HTTP/1.1\r\nHost: x\r\nOrigin: https://pos.example\r\n" +
                                     "Access-Control-Request-Method: POST\r\nAccess-Control-Request-Headers: content-type\r\n" +
                                     "Access-Control-Request-Private-Network: true\r\nConnection: close\r\n\r\n";
                        byte[] b = Encoding.ASCII.GetBytes(req);
                        ns.Write(b, 0, b.Length);
                        ns.Flush();
                        string resp = new StreamReader(ns).ReadToEnd();
                        Check(resp.Contains("204"), "204 No Content: " + resp.Split('\n')[0]);
                        Check(resp.Contains("Access-Control-Allow-Origin: https://pos.example"), "echoes Origin");
                        Check(resp.Contains("Access-Control-Allow-Methods"), "allow-methods");
                        Check(resp.IndexOf("Access-Control-Allow-Private-Network: true", StringComparison.OrdinalIgnoreCase) >= 0, "private-network allow");
                    }
                }
            }
            finally { server.Stop(); }
        }

        private static void Http_BadXml()
        {
            var target = new MemoryPrintTarget();
            var server = new HttpBridgeServer(target, null);
            server.Start(IPAddress.Loopback, 0);
            try
            {
                byte[] job;
                string resp = HttpPost(IPAddress.Loopback, server.LocalEndPoint.Port, "/cgi-bin/epos/service.cgi", "<not-epos>oops", out job, target);
                Check(resp.Contains("success=\"false\""), "success false");
                Check(resp.Contains("SchemaError"), "SchemaError code");
                Check(target.JobCount == 0, "nothing printed");
            }
            finally { server.Stop(); }
        }

        private static void Https_PostPrints()
        {
            ConfigStore.DataDirectory = Path.Combine(OutDir, "data");
            var target = new MemoryPrintTarget("https-test");
            var cert = SelfSignedCertificate.GetOrCreate(new[] { IPAddress.Loopback });
            var server = new HttpBridgeServer(target, cert);
            server.Start(IPAddress.Loopback, 0);
            try
            {
                using (var c = new TcpClient())
                {
                    c.Connect(IPAddress.Loopback, server.LocalEndPoint.Port);
                    using (var ssl = new System.Net.Security.SslStream(c.GetStream(), false, (sender, certificate, chain, errors) => true))
                    {
                        ssl.AuthenticateAsClient("localhost");
                        byte[] bodyBytes = Encoding.UTF8.GetBytes(SamplePrint);
                        string head = "POST /cgi-bin/epos/service.cgi HTTP/1.1\r\nHost: localhost\r\nContent-Type: text/xml\r\n" +
                                      "Content-Length: " + bodyBytes.Length + "\r\nConnection: close\r\n\r\n";
                        byte[] headBytes = Encoding.ASCII.GetBytes(head);
                        ssl.Write(headBytes, 0, headBytes.Length);
                        ssl.Write(bodyBytes, 0, bodyBytes.Length);
                        ssl.Flush();
                        string resp = new StreamReader(ssl, Encoding.UTF8).ReadToEnd();
                        Check(resp.Contains("success=\"true\""), "success over TLS: " + resp.Split('\n')[0]);
                        Check(target.JobCount == 1, "printed over TLS");
                        Check(Contains(target.Jobs[0], Ascii("Receipt")), "text over TLS");
                    }
                }
            }
            finally { server.Stop(); }
        }

        private static void Cert_Sans()
        {
            ConfigStore.DataDirectory = Path.Combine(OutDir, "data");
            var cert = SelfSignedCertificate.GetOrCreate(new[] { IPAddress.Parse("192.168.1.201"), IPAddress.Loopback });
            Check(cert.HasPrivateKey, "cert has a private key (needed for TLS server auth)");
            string sans = "";
            foreach (var ext in cert.Extensions)
                if (ext.Oid != null && ext.Oid.Value == "2.5.29.17") sans = ext.Format(false);
            Console.WriteLine("      SAN: " + sans);
            Check(sans.Contains("192.168.1.201"), "SAN includes the requested IP");
            Check(File.Exists(SelfSignedCertificate.CerPath), "public .cer exported");
        }

        // ------------------------------------------------------------------ print log

        private static void History_ExtractsReadableText()
        {
            byte[] job = Concat(
                Bytes(0x1B, 0x40),                  // ESC @ initialise
                Bytes(0x1B, 0x61, 0x01),            // centre
                Ascii("ACME STORE\n"),
                Bytes(0x1D, 0x21, 0x11),            // GS ! double size
                Ascii("Total 12.50\n"),
                Bytes(0x1B, 0x70, 0x00, 0x32, 0x32), // drawer pulse
                Bytes(0x1D, 0x56, 0x42, 0x00));      // cut
            string text = PrintHistory.ExtractText(job, job.Length, 400);
            Console.WriteLine("      preview: " + text);
            Check(text.Contains("ACME STORE"), "shop name recovered from the byte stream");
            Check(text.Contains("Total 12.50"), "amount recovered from the byte stream");
            Check(text.IndexOf('') < 0, "no escape characters leak into the preview");
            Check(text.IndexOf('') < 0, "no GS characters leak into the preview");
        }

        private static void History_RecordsAndWritesCsv()
        {
            ConfigStore.DataDirectory = Path.Combine(OutDir, "data");
            PrintHistory.Clear();

            byte[] job = Concat(Bytes(0x1B, 0x40), Ascii("RECEIPT ONE\n"));
            PrintRecord ok = PrintHistory.Add("192.168.1.55:5000", "EPSON TM", "raw 9100", job, job.Length, "Printed");
            Check(ok != null && !ok.Failed, "successful job is not flagged as failed");
            Check(ok.Preview.Contains("RECEIPT ONE"), "preview holds the printed text");
            Check(ok.Time.Date == DateTime.Now.Date, "record carries a timestamp");

            PrintHistory.Add("192.168.1.55:5001", "EPSON TM", "ePOS http", job, job.Length, "Failed: out of paper");
            var all = PrintHistory.Snapshot();
            Check(all.Count == 2, "both jobs recorded, got " + all.Count);
            Check(all[1].Failed, "failed job is flagged");

            string csv = Path.Combine(ConfigStore.LogDirectory, "prints-" + DateTime.Now.ToString("yyyyMMdd") + ".csv");
            Check(File.Exists(csv), "daily CSV written to " + csv);
            string content = File.ReadAllText(csv);
            Check(content.Contains("RECEIPT ONE"), "CSV contains what was printed");
            Check(content.Contains("192.168.1.55:5000"), "CSV contains who sent it");
            Check(content.Contains("Failed: out of paper"), "CSV records the failure reason");
        }

        private static void History_RawDump()
        {
            ConfigStore.DataDirectory = Path.Combine(OutDir, "data");
            PrintHistory.Clear();
            bool previous = PrintHistory.SaveRawJobs;
            PrintHistory.SaveRawJobs = true;
            try
            {
                byte[] job = Concat(Bytes(0x1B, 0x40), Ascii("DUMP ME"));
                PrintHistory.Add("10.0.0.9:4000", "P", "raw 9100", job, job.Length, "Printed");
                Check(Directory.Exists(PrintHistory.JobDumpDirectory), "dump directory created");
                string[] files = Directory.GetFiles(PrintHistory.JobDumpDirectory, "*.bin");
                Check(files.Length > 0, "a raw dump file was written");
                byte[] written = File.ReadAllBytes(files[files.Length - 1]);
                CheckEqual(job, written, "dump matches the bytes sent to the printer");
            }
            finally
            {
                PrintHistory.SaveRawJobs = previous;
            }
        }

        private static void Config_RoundTrip()
        {
            string path = Path.Combine(OutDir, "roundtrip.xml");
            var cfg = new BridgeConfig { JobIdleTimeoutMs = 750, AutoFirewallRule = false, AutoStartBridges = true, CloseToTray = false, NoCutFeedLines = 7 };
            cfg.Mappings.Add(new MappingConfig { PrinterName = "EPSON TM-T20 Receipt", BindAddress = "192.168.1.200", Port = 9100, Adapter = "Ethernet", EscPosStatusReplies = true, Enabled = true, EposEnabled = true, EposHttpPort = 8008, EposHttpsPort = 8043, NoCut = true });
            cfg.Mappings.Add(new MappingConfig { PrinterName = "Zebra & \"Label\" <x>", BindAddress = "192.168.1.201", Port = 9101, Adapter = "", EscPosStatusReplies = false, Enabled = false });
            ConfigStore.Save(cfg, path);
            BridgeConfig back = ConfigStore.Load(path);
            Check(back.JobIdleTimeoutMs == 750 && !back.AutoFirewallRule && back.AutoStartBridges && !back.CloseToTray, "settings");
            Check(back.Mappings[0].NoCut && !back.Mappings[1].NoCut && back.NoCutFeedLines == 7, "per-mapping NO CUT switch and the shared feed count round-trip");
            Check(new BridgeConfig { NoCutFeedLines = 99 }.SanitizedNoCutFeedLines == NoCutSettings.MaxFeedLines && new BridgeConfig { NoCutFeedLines = -3 }.SanitizedNoCutFeedLines == 0, "feed count is clamped");
            Check(!new MappingConfig().NoCut && ConfigStore.Load(Path.Combine(OutDir, "missing.xml")).NoCutFeedLines == 4, "NO CUT defaults: off, 4 lines");
            Check(back.Mappings.Count == 2, "mapping count");
            Check(back.Mappings[0].Id == cfg.Mappings[0].Id && back.Mappings[1].Id == cfg.Mappings[1].Id, "ids preserved");
            Check(back.Mappings[1].PrinterName == "Zebra & \"Label\" <x>" && back.Mappings[1].Port == 9101 && !back.Mappings[1].Enabled && !back.Mappings[1].EscPosStatusReplies, "mapping 2");
            Check(back.Mappings[0].Adapter == "Ethernet" && back.Mappings[1].Adapter == "", "adapters");
            Check(back.Mappings[0].EposEnabled && back.Mappings[0].EposHttpPort == 8008 && back.Mappings[0].EposHttpsPort == 8043, "ePOS fields round-trip");
            Check(ConfigStore.Load(Path.Combine(OutDir, "missing.xml")).Mappings.Count == 0, "missing file → defaults");
        }

        private static void Network_Adapters()
        {
            List<AdapterInfo> adapters = NetworkHelper.GetAdapters();
            Console.WriteLine("      adapters: " + string.Join("; ", adapters.Select(a => a.Name + " " + string.Join(",", a.Addresses.Select(x => x.ToString()).ToArray()) + (a.HasGateway ? " gw" : "")).ToArray()));
            string suggestion = NetworkHelper.SuggestBindAddress(new string[0]);
            IPAddress ip;
            Check(IPAddress.TryParse(suggestion, out ip), "suggestion parses: " + suggestion);
            Console.WriteLine("      suggested: " + suggestion);
            string next = NetworkHelper.SuggestBindAddress(new[] { suggestion });
            Check(next != suggestion, "second suggestion differs");
            Check(NetworkHelper.IsLocalAddress(IPAddress.Loopback), "loopback is local");
            Check(!NetworkHelper.IsLocalAddress(IPAddress.Parse("203.0.113.9")), "TEST-NET is not local");
            if (adapters.Count > 0)
            {
                AdapterInfo a = adapters[0];
                Check(a.Addresses.Count > 0, "adapter has addresses");
                Check(NetworkHelper.IsLocalAddress(a.Addresses[0].Address), "own address is local");
                Check(NetworkHelper.PickAdapter(a.Addresses[0].Address, null, adapters).Name == a.Name || adapters.Count > 1, "pick by subnet");
                Check(NetworkHelper.PickAdapter(IPAddress.Parse("203.0.113.9"), "nonexistent", adapters) != null, "fallback pick");
                Check(new IpEntry { Address = IPAddress.Parse("192.168.1.5"), Mask = IPAddress.Parse("255.255.255.0") }.Contains(IPAddress.Parse("192.168.1.200")), "subnet contains");
                Check(!new IpEntry { Address = IPAddress.Parse("192.168.1.5"), Mask = IPAddress.Parse("255.255.255.0") }.Contains(IPAddress.Parse("192.168.2.200")), "subnet excludes");
                Check(new IpEntry { Address = IPAddress.Parse("10.0.0.1"), Mask = IPAddress.Parse("255.255.0.0") }.PrefixLength == 16, "prefix length");
            }
        }

        private static void Network_SuggestionRespectsPrefix()
        {
            // An iPhone hotspot is a /28. Usable hosts are .1 to .14, so anything ending .200 is unroutable.
            var hotspot = new IpEntry
            {
                Address = IPAddress.Parse("172.20.10.2"),
                Mask = IPAddress.Parse("255.255.255.240")
            };
            Check(hotspot.PrefixLength == 28, "mask parsed as /28, got /" + hotspot.PrefixLength);

            string suggestion = NetworkHelper.SuggestInSubnet(hotspot, new string[0]);
            Console.WriteLine("      /28 hotspot suggestion: " + suggestion);
            Check(suggestion != null, "a suggestion was produced for a /28");
            IPAddress parsed = IPAddress.Parse(suggestion);
            Check(hotspot.Contains(parsed), suggestion + " must be inside the /28, not an unroutable .200");
            Check(suggestion != "172.20.10.0" && suggestion != "172.20.10.15", "network and broadcast avoided: " + suggestion);

            // A /24 still has room for the familiar .200.
            var lan = new IpEntry
            {
                Address = IPAddress.Parse("192.168.9.136"),
                Mask = IPAddress.Parse("255.255.255.0")
            };
            string wide = NetworkHelper.SuggestInSubnet(lan, new string[0]);
            Console.WriteLine("      /24 suggestion: " + wide);
            Check(lan.Contains(IPAddress.Parse(wide)), wide + " must be inside the /24");
            Check(wide == "192.168.9.200", "a /24 keeps the traditional .200, got " + wide);

            // Already-used addresses are skipped.
            string avoided = NetworkHelper.SuggestInSubnet(lan, new[] { "192.168.9.200" });
            Check(avoided != "192.168.9.200", "an address already in use is not suggested again");
            Check(lan.Contains(IPAddress.Parse(avoided)), avoided + " must still be inside the /24");

            // A /31 has no room for a usable host.
            var tiny = new IpEntry { Address = IPAddress.Parse("10.0.0.1"), Mask = IPAddress.Parse("255.255.255.254") };
            Check(NetworkHelper.SuggestInSubnet(tiny, new string[0]) == null, "a /31 yields no suggestion");
        }

        private static void Network_NetshRuns()
        {
            CommandResult r = NetworkHelper.Run("netsh.exe", "interface ipv4 show addresses", 15000);
            Check(r.Success, "netsh exit code " + r.ExitCode + ": " + r.OutputOneLine);
            Check(r.Output.Length > 0, "netsh produced output");
            CommandResult bad = NetworkHelper.Run("netsh.exe", "interface ipv4 delete address name=\"NoSuchAdapter!\" address=203.0.113.1 store=active", 15000);
            Check(!bad.Success, "bogus delete fails (exit " + bad.ExitCode + ")");
            Check(bad.OutputOneLine.Length > 0, "error text captured: " + bad.OutputOneLine);
            Console.WriteLine("      netsh error sample: " + bad.OutputOneLine);
        }

        private static void IpHelper_TableLayout()
        {
            List<IpHelperApi.UnicastRow> rows = IpHelperApi.GetIPv4Rows();
            Check(rows.Count > 0, "table has rows");
            Console.WriteLine("      rows: " + string.Join("; ", rows.Select(r => "if" + r.InterfaceIndex + " " + r.Address + "/" + r.PrefixLength + (r.SkipAsSource ? " skip" : "") + " dad=" + r.DadState).ToArray()));
            List<AdapterInfo> adapters = NetworkHelper.GetAdapters();
            Check(adapters.Count > 0, "adapters present");
            foreach (AdapterInfo a in adapters)
            {
                Check(a.Index > 0, "adapter " + a.Name + " has an interface index");
                foreach (IpEntry e in a.Addresses)
                {
                    IpHelperApi.UnicastRow row = rows.FirstOrDefault(r => r.Address.Equals(e.Address));
                    Check(row != null, "row for " + e.Address);
                    Check(row.InterfaceIndex == a.Index, e.Address + ": interface index " + row.InterfaceIndex + " vs .NET " + a.Index);
                    Check(row.PrefixLength == e.PrefixLength, e.Address + ": prefix " + row.PrefixLength + " vs .NET " + e.PrefixLength);
                    Check(row.DadState == 4, e.Address + ": dad state " + row.DadState + " (expected Preferred=4)");
                }
            }
            // loopback must be there too, with a sane prefix
            IpHelperApi.UnicastRow lo = rows.FirstOrDefault(r => r.Address.Equals(IPAddress.Loopback));
            Check(lo != null && lo.PrefixLength == 8, "loopback row 127.0.0.1/8");
        }

        private static void IpHelper_AddRemove()
        {
            bool admin = BridgeManager.DetectElevation();
            NetworkInterface lo = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.NetworkInterfaceType == NetworkInterfaceType.Loopback);
            Check(lo != null, "loopback adapter");
            int loIndex = lo.GetIPProperties().GetIPv4Properties().Index;
            var testIp = IPAddress.Parse("127.77.66.55");
            var lease = new AdapterInfo { Name = lo.Name, Index = loIndex, Addresses = new List<IpEntry>() };

            CommandResult add = NetworkHelper.AddAddress(lease, testIp, IPAddress.Parse("255.255.255.255"));
            Console.WriteLine("      add → " + add.ExitCode + " " + add.OutputOneLine + (admin ? " (elevated)" : " (not elevated)"));
            if (!admin)
            {
                Check(add.ExitCode == IpHelperApi.ERROR_ACCESS_DENIED || add.ExitCode == 1314, "without admin the API must refuse with access denied, got " + add.ExitCode);
                return;
            }
            try
            {
                Check(add.Success, "add: " + add.OutputOneLine);
                Check(NetworkHelper.WaitForAddressUsable(testIp, 4000), "address usable");
                Check(NetworkHelper.IsLocalAddress(testIp), "address visible to .NET");
                IpHelperApi.UnicastRow row = IpHelperApi.GetIPv4Rows().FirstOrDefault(r => r.Address.Equals(testIp));
                Check(row != null && row.SkipAsSource && row.PrefixLength == 32, "row flags");
                var l = new TcpListener(testIp, 0);
                l.Start();
                l.Stop();
            }
            finally
            {
                CommandResult rm = NetworkHelper.RemoveAddress(loIndex, testIp);
                Console.WriteLine("      remove → " + rm.ExitCode + " " + rm.OutputOneLine);
                Check(rm.Success, "remove: " + rm.OutputOneLine);
            }
            Thread.Sleep(300);
            Check(!NetworkHelper.IsLocalAddress(testIp), "address gone after remove");
        }

        private static void Printers_Enumerate()
        {
            List<PrinterInfo> printers = PrinterEnumerator.GetPrinters();
            Console.WriteLine("      printers: " + string.Join("; ", printers.Select(p => p.DisplayText).ToArray()));
            Check(printers != null, "list");
            foreach (PrinterInfo p in printers) Check(!string.IsNullOrEmpty(p.Name), "name present");
        }

        private static void Spooler_XpsWriter()
        {
            const string printer = "Microsoft XPS Document Writer";
            if (!PrinterEnumerator.GetPrinters().Any(p => p.Name == printer)) throw new SkipException("printer not installed");
            string outFile = Path.Combine(OutDir, "spooler-raw-output.bin");
            if (File.Exists(outFile)) File.Delete(outFile);

            byte[] payload = Concat(Ascii("RAW-SPOOL-TEST "), Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray());
            var target = new SpoolerPrintTarget(printer, outFile);
            using (IPrintJob job = target.StartJob("Bridge harness"))
            {
                job.Write(payload, 0, 1000);
                job.Write(payload, 1000, payload.Length - 1000);
                job.Complete();
            }

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 8000 && (!File.Exists(outFile) || new FileInfo(outFile).Length < payload.Length)) Thread.Sleep(100);
            Check(File.Exists(outFile), "spooler wrote the redirected output file");
            byte[] got = File.ReadAllBytes(outFile);
            CheckEqual(payload, got, "spooled bytes");
        }

        private static void Spooler_UnknownPrinter()
        {
            try
            {
                new SpoolerPrintTarget("No Such Printer 12345").StartJob("x");
                Check(false, "expected an exception");
            }
            catch (CheckException) { throw; }
            catch (Exception ex)
            {
                Check(ex.Message.IndexOf("OpenPrinter", StringComparison.Ordinal) >= 0, "message mentions OpenPrinter: " + ex.Message);
                Console.WriteLine("      error text: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------ UI

        private static void Ui_PrintLogRenders()
        {
            ConfigStore.DataDirectory = Path.Combine(OutDir, "data");
            PrintHistory.Clear();

            byte[] sale = Concat(
                Bytes(0x1B, 0x40), Bytes(0x1B, 0x61, 0x01), Ascii("ACME STORE\n"),
                Bytes(0x1B, 0x61, 0x00), Ascii("Coffee        2.50\n"), Ascii("Pastry        3.20\n"),
                Ascii("Total         5.70\n"), Bytes(0x1D, 0x56, 0x42, 0x00));
            PrintHistory.Add("192.168.1.77:51000", "EPSON TM-T20II", "raw 9100", sale, sale.Length, "Printed");

            byte[] refund = Concat(Bytes(0x1B, 0x40), Ascii("REFUND        5.00\n"));
            PrintHistory.Add("192.168.1.90:4455", "EPSON TM-T20II", "ePOS http", refund, refund.Length, "Printed");
            PrintHistory.Add("192.168.1.90:4456", "EPSON TM-T20II", "ePOS http", refund, refund.Length, "Failed: printer offline");

            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    Application.EnableVisualStyles();
                    var form = new PrintLogForm();
                    form.Load += (s, e) =>
                    {
                        var t = new System.Windows.Forms.Timer { Interval = 1500 };
                        t.Tick += (s2, e2) =>
                        {
                            t.Stop();
                            try
                            {
                                using (var bmp = new Bitmap(form.Width, form.Height))
                                {
                                    form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                                    bmp.Save(Path.Combine(OutDir, "printlog.png"), ImageFormat.Png);
                                }
                            }
                            catch (Exception ex) { failure = ex; }
                            finally { form.Close(); }
                        };
                        t.Start();
                    };
                    Application.Run(form);
                }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Check(thread.Join(30000), "print log UI thread finished");
            if (failure != null) throw failure;

            string png = Path.Combine(OutDir, "printlog.png");
            Check(File.Exists(png) && new FileInfo(png).Length > 5000, "print log screenshot written: " + png);
        }

        private static void Ui_Screenshot()
        {
            string dataDir = Path.Combine(OutDir, "ui-data");
            Directory.CreateDirectory(dataDir);
            ConfigStore.DataDirectory = dataDir;
            var cfg = new BridgeConfig { CloseToTray = false }; // otherwise Close() just hides the window and Run() never returns
            string firstPrinter = PrinterEnumerator.GetPrinters().Select(p => p.Name).FirstOrDefault() ?? "EPSON TM-T88V Receipt";
            cfg.Mappings.Add(new MappingConfig { PrinterName = firstPrinter, BindAddress = "127.0.0.1", Port = FreePort(), EscPosStatusReplies = true });
            cfg.Mappings.Add(new MappingConfig { PrinterName = "Star TSP143 (unplugged)", BindAddress = "192.168.1.201", Port = 9100, EscPosStatusReplies = false, Enabled = false });
            ConfigStore.Save(cfg);

            Exception failure = null;
            int loadOpen = 0, loadFolded = 0, sysOpen = 0, sysFolded = 0;
            string[] cardsBefore = null, cardsAfter = null, blocksAfter = null, keys = null;
            bool settingsFolded = false;
            var thread = new Thread(() =>
            {
                try
                {
                    Application.EnableVisualStyles();
                    var form = new MainForm(new StartupOptions { NoElevate = true });
                    form.Load += (s, e) =>
                    {
                        int phase = 0;
                        var t = new System.Windows.Forms.Timer { Interval = 2500 };
                        t.Tick += (s2, e2) =>
                        {
                            try
                            {
                                if (phase == 0)
                                {
                                    using (var bmp = new Bitmap(form.Width, form.Height))
                                    {
                                        form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                                        bmp.Save(Path.Combine(OutDir, "mainform.png"), ImageFormat.Png);
                                    }
                                    phase = 1;
                                    form.SelectDeviceTab();   // a second capture with the Device tab showing, after a few samples
                                    t.Interval = 3500;
                                    return;
                                }
                                if (phase == 1)
                                {
                                    using (var bmp = new Bitmap(form.Width, form.Height))
                                    {
                                        form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                                        bmp.Save(Path.Combine(OutDir, "mainform-device.png"), ImageFormat.Png);
                                    }
                                    // fold a block and a whole card's worth of rows, drag a card and a block to new places
                                    DeviceTab tab = form.DeviceTabControl;
                                    keys = tab.SectionKeys;
                                    // The window is on the real desktop while this runs; if someone clicked a heading, unfold it again first.
                                    foreach (string k in keys) if (tab.IsSectionCollapsed(k)) { Console.WriteLine("      (section " + k + " was folded before the test touched it)"); tab.ToggleSection(k); }
                                    if (form.SettingsFolded) form.ToggleSettingsFold();
                                    loadOpen = tab.SectionHeight("sys.load");
                                    sysOpen = tab.SectionHeight("sys");
                                    tab.ToggleSection("sys.load");
                                    tab.ToggleSection("sys.temps");
                                    loadFolded = tab.SectionHeight("sys.load");
                                    sysFolded = tab.SectionHeight("sys");
                                    cardsBefore = tab.SectionOrder("device");
                                    tab.MoveSection("device", "cooling", 0);
                                    tab.MoveSection("sys", "sys.events", 0);
                                    cardsAfter = tab.SectionOrder("device");
                                    blocksAfter = tab.SectionOrder("sys");
                                    form.ToggleSettingsFold();
                                    settingsFolded = form.SettingsFolded;
                                    phase = 2;
                                    t.Interval = 1500;
                                    return;
                                }
                                t.Stop();
                                using (var bmp = new Bitmap(form.Width, form.Height))
                                {
                                    form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                                    bmp.Save(Path.Combine(OutDir, "mainform-device-folded.png"), ImageFormat.Png);
                                }
                                form.Close();
                            }
                            catch (Exception ex) { failure = ex; t.Stop(); form.Close(); }
                        };
                        t.Start();
                    };
                    // the form only exits on close when CloseToTray is off or the close is not user-initiated
                    form.FormClosing += (s, e) => { };
                    Application.Run(form);
                }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Check(thread.Join(30000), "UI thread finished");
            if (failure != null) throw failure;
            string png = Path.Combine(OutDir, "mainform.png");
            Check(File.Exists(png) && new FileInfo(png).Length > 5000, "screenshot written: " + png);
            string device = Path.Combine(OutDir, "mainform-device.png");
            Check(File.Exists(device) && new FileInfo(device).Length > 5000, "Device tab screenshot written: " + device);

            // accordion sections: fold, reorder, remember
            Check(keys != null && keys.Contains("sys") && keys.Contains("battery") && keys.Contains("cooling") && keys.Contains("sys.tiles") && keys.Contains("sys.load")
                && keys.Contains("sys.temps") && keys.Contains("sys.strips") && keys.Contains("sys.events") && keys.Contains("sys.telemetry")
                && keys.Contains("bat.level") && keys.Contains("bat.current") && keys.Contains("cool.fans") && keys.Contains("cool.cap"),
                "every card and every block inside one is a section: " + (keys == null ? "none" : string.Join(", ", keys)));
            Check(loadOpen > 150 && loadFolded < 40, "folding a block leaves only its heading (" + loadOpen + " px to " + loadFolded + " px)");
            Check(sysFolded < sysOpen - 250, "the card around it shrinks with it (" + sysOpen + " px to " + sysFolded + " px)");
            Check(cardsBefore != null && cardsBefore[0] == "sys" && cardsAfter != null && cardsAfter[0] == "cooling" && cardsAfter.Length == 3, "a card can be moved to the top: " + string.Join(", ", cardsAfter ?? new string[0]));
            Check(blocksAfter != null && blocksAfter[0] == "sys.events" && blocksAfter.Length == 6, "a block can be moved inside its card: " + string.Join(", ", blocksAfter ?? new string[0]));
            Check(settingsFolded, "the settings rows of the main window fold away");
            string folded = Path.Combine(OutDir, "mainform-device-folded.png");
            Check(File.Exists(folded) && new FileInfo(folded).Length > 5000, "folded screenshot written: " + folded);
            BridgeConfig saved = ConfigStore.Load();
            Check(saved.IsSectionCollapsed("sys.load") && saved.IsSectionCollapsed("sys.temps") && saved.IsSectionCollapsed("main.settings") && !saved.IsSectionCollapsed("sys.tiles"), "what is folded is saved in the configuration");
            string[] savedCards = saved.GetSectionOrder("device"), savedBlocks = saved.GetSectionOrder("sys");
            Check(savedCards != null && savedCards[0] == "cooling" && savedBlocks != null && savedBlocks[0] == "sys.events", "the dragged order is saved in the configuration");
            ConfigStore.DataDirectory = Path.Combine(OutDir, "data");
        }
    }
}
