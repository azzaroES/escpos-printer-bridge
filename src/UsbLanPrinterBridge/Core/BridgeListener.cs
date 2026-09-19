using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace UsbLanPrinterBridge.Core
{
    public enum BridgeState
    {
        Stopped,
        Starting,
        Listening,
        Error
    }

    /// <summary>
    /// One RAW/9100 listener bound to a single IP:port, forwarding every connection's bytes to one print target.
    ///
    /// Job segmentation: a spooler job is opened on the first byte of a connection and closed when the client
    /// disconnects or when no data arrives for <see cref="JobIdleTimeoutMs"/>. This matches real JetDirect
    /// printers (one connection = one job) while also coping with POS software that keeps one connection open
    /// and streams receipt after receipt through it.
    /// </summary>
    public sealed class BridgeListener
    {
        private readonly object _gate = new object();
        private readonly HashSet<TcpClient> _clients = new HashSet<TcpClient>();
        private TcpListener _listener;
        private CancellationTokenSource _cts;

        private long _bytesReceived;
        private long _jobsCompleted;
        private long _connectionsTotal;
        private int _activeConnections;

        /// <summary>The target jobs are actually written to: <see cref="Target"/> behind the NO CUT filter.</summary>
        private IPrintTarget _sink;

        public BridgeListener(MappingConfig mapping, IPrintTarget target)
        {
            if (mapping == null) throw new ArgumentNullException("mapping");
            if (target == null) throw new ArgumentNullException("target");
            Mapping = mapping;
            Target = target;
            _sink = new NoCutPrintTarget(target, () => Mapping.NoCut);
            TargetPrinterName = mapping.PrinterName;
            JobIdleTimeoutMs = 1500;
            State = BridgeState.Stopped;
            StatusText = "Stopped";
        }

        public MappingConfig Mapping { get; private set; }
        public IPrintTarget Target { get; private set; }

        /// <summary>
        /// The printer <see cref="Target"/> was built for. A listener outlives a stop/start cycle, so this is
        /// compared against the mapping on every start: without it, re-pointing a row at a different printer
        /// would leave the old target in place and every job would keep going to the previous printer.
        /// </summary>
        public string TargetPrinterName { get; private set; }

        public bool TargetMatches(MappingConfig mapping)
        {
            return mapping != null && string.Equals(TargetPrinterName, mapping.PrinterName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Points this listener at a different printer, keeping its counters. Only valid while stopped, which is
        /// what makes it safe: no job can be in flight, so no half-written document is split across two printers.
        /// </summary>
        public void Retarget(MappingConfig mapping, IPrintTarget target)
        {
            if (mapping == null) throw new ArgumentNullException("mapping");
            if (target == null) throw new ArgumentNullException("target");
            lock (_gate)
            {
                if (IsRunning) throw new InvalidOperationException("Stop the bridge before changing its printer.");
                Mapping = mapping;
                Target = target;
                _sink = new NoCutPrintTarget(target, () => Mapping.NoCut);
                TargetPrinterName = mapping.PrinterName;
            }
        }

        /// <summary>Milliseconds of silence that end the current job. 0 = only end jobs when the client disconnects.</summary>
        public int JobIdleTimeoutMs { get; set; }

        public BridgeState State { get; private set; }
        public string StatusText { get; private set; }
        public IPEndPoint LocalEndPoint { get; private set; }

        public long BytesReceived { get { return Interlocked.Read(ref _bytesReceived); } }
        public long JobsCompleted { get { return Interlocked.Read(ref _jobsCompleted); } }
        public long ConnectionsTotal { get { return Interlocked.Read(ref _connectionsTotal); } }
        public int ActiveConnections { get { return Volatile.Read(ref _activeConnections); } }

        public event Action<BridgeListener, string> Log;
        public event Action<BridgeListener> StateChanged;

        public bool IsRunning { get { return State == BridgeState.Listening || State == BridgeState.Starting; } }

        /// <summary>Binds and starts accepting. Throws SocketException when the endpoint cannot be bound.</summary>
        public void Start(IPAddress address, int port)
        {
            lock (_gate)
            {
                if (IsRunning) return;
                SetState(BridgeState.Starting, "Starting");

                var listener = new TcpListener(address, port);
                try
                {
                    listener.Start(64);
                }
                catch
                {
                    SetState(BridgeState.Error, "Bind failed");
                    throw;
                }

                _listener = listener;
                _cts = new CancellationTokenSource();
                LocalEndPoint = (IPEndPoint)listener.LocalEndpoint;
                SetState(BridgeState.Listening, "Listening on " + LocalEndPoint);

                CancellationToken token = _cts.Token;
                Task.Run(() => AcceptLoopAsync(listener, token));
            }
        }

        public void Stop()
        {
            TcpListener listener;
            CancellationTokenSource cts;
            TcpClient[] clients;
            lock (_gate)
            {
                listener = _listener;
                cts = _cts;
                _listener = null;
                _cts = null;
                clients = new TcpClient[_clients.Count];
                _clients.CopyTo(clients);
            }

            if (cts != null) { try { cts.Cancel(); } catch { } }
            if (listener != null) { try { listener.Stop(); } catch { } }
            foreach (TcpClient c in clients) { try { c.Close(); } catch { } }

            SetState(BridgeState.Stopped, "Stopped");
        }

        public void MarkError(string message)
        {
            SetState(BridgeState.Error, message);
        }

        private void SetState(BridgeState state, string text)
        {
            State = state;
            StatusText = text;
            var h = StateChanged;
            if (h != null) { try { h(this); } catch { } }
        }

        private void Emit(string message)
        {
            var h = Log;
            if (h != null) { try { h(this, message); } catch { } }
        }

        private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException ex)
                {
                    if (ct.IsCancellationRequested) break;
                    Emit("Accept failed: " + ex.Message + " (retrying)");
                    try { await Task.Delay(250, ct).ConfigureAwait(false); } catch { break; }
                    continue;
                }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested) break;
                    Emit("Listener stopped unexpectedly: " + ex.Message);
                    SetState(BridgeState.Error, "Error: " + ex.Message);
                    return;
                }

                lock (_gate) _clients.Add(client);
                Interlocked.Increment(ref _connectionsTotal);
                TcpClient captured = client;
                var _ = Task.Run(() => HandleClientAsync(captured, ct));
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            string remote = "?";
            try { remote = client.Client.RemoteEndPoint.ToString(); } catch { }

            Interlocked.Increment(ref _activeConnections);
            Emit("Client connected: " + remote);

            IPrintJob job = null;
            long jobBytes = 0;
            int answeredBefore = 0; // queries answered before the current job started, so each job's entry lists only its own
            var jobCapture = new MemoryStream();
            EscPosResponder scanner = Mapping.EscPosStatusReplies ? new EscPosResponder { ModelName = Mapping.EposModelName } : null;
            byte[] buffer = new byte[64 * 1024];

            try
            {
                try
                {
                    client.NoDelay = true;
                    client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                }
                catch { }

                using (NetworkStream stream = client.GetStream())
                {
                    while (!ct.IsCancellationRequested)
                    {
                        Task<int> readTask = stream.ReadAsync(buffer, 0, buffer.Length, ct);

                        if (job != null && JobIdleTimeoutMs > 0)
                        {
                            using (var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                            {
                                Task delay = Task.Delay(JobIdleTimeoutMs, delayCts.Token);
                                Task finished = await Task.WhenAny(readTask, delay).ConfigureAwait(false);
                                delayCts.Cancel();
                                if (finished != readTask)
                                {
                                    // Silence: the receipt/document is complete. Release it and keep the connection.
                                    job = FinishJob(job, jobBytes, remote, "idle", jobCapture, scanner, answeredBefore);
                                    jobBytes = 0;
                                    answeredBefore = scanner == null ? 0 : scanner.QueriesAnswered;
                                }
                            }
                        }

                        int n;
                        try { n = await readTask.ConfigureAwait(false); }
                        catch (OperationCanceledException) { break; }
                        catch (ObjectDisposedException) { break; }
                        catch (IOException) { break; } // reset by peer
                        if (n <= 0) break;

                        Interlocked.Add(ref _bytesReceived, n);

                        byte[] data = buffer;
                        int len = n;
                        if (scanner != null)
                        {
                            scanner.Process(buffer, 0, n);
                            if (scanner.ReplyLength > 0)
                            {
                                try { await stream.WriteAsync(scanner.Replies, 0, scanner.ReplyLength, ct).ConfigureAwait(false); }
                                catch { /* client may have half-closed; ignore */ }
                            }
                            data = scanner.Forward;
                            len = scanner.ForwardLength;
                        }

                        if (len > 0)
                        {
                            if (job == null)
                            {
                                job = _sink.StartJob("LAN Bridge " + remote + " " + DateTime.Now.ToString("HH:mm:ss"));
                            }
                            job.Write(data, 0, len);
                            jobBytes += len;
                            Capture(jobCapture, data, len);
                        }
                    }

                    if (scanner != null)
                    {
                        scanner.Flush();
                        if (scanner.ForwardLength > 0)
                        {
                            if (job == null) job = _sink.StartJob("LAN Bridge " + remote);
                            job.Write(scanner.Forward, 0, scanner.ForwardLength);
                            jobBytes += scanner.ForwardLength;
                            Capture(jobCapture, scanner.Forward, scanner.ForwardLength);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Emit("Error while printing from " + remote + ": " + ex.Message);
                List<string> ignored;
                string contents = Summarize(jobCapture, jobBytes, out ignored);
                string ticket = RenderTicket(jobCapture);
                RecordHistory(remote, jobBytes, jobCapture, "Failed: " + ex.Message);
                PrinterActionLog.Add(ActionLevel.Error, Target.Name, remote, "Job abandoned after " + FormatBytes(jobBytes),
                    ex.Message + ". The job contained: " + contents + ". Nothing of it will print; the client must send it again.", ticket);
                if (job != null)
                {
                    try { job.Abort(); } catch { }
                    try { job.Dispose(); } catch { }
                    job = null;
                }
            }
            finally
            {
                if (job != null) FinishJob(job, jobBytes, remote, "disconnect", jobCapture, scanner, answeredBefore);
                else LogQueries(scanner, answeredBefore, remote, true);
                lock (_gate) _clients.Remove(client);
                try { client.Close(); } catch { }
                Interlocked.Decrement(ref _activeConnections);
                Emit("Client disconnected: " + remote);
            }
        }

        /// <summary>Keeps the start of each job so the print log can show what was actually printed.</summary>
        private static void Capture(MemoryStream capture, byte[] data, int length)
        {
            const int MaxCapture = 128 * 1024;
            if (capture == null || data == null || length <= 0) return;
            if (capture.Length >= MaxCapture) return;
            int room = MaxCapture - (int)capture.Length;
            capture.Write(data, 0, Math.Min(length, room));
        }

        private void RecordHistory(string remote, long bytes, MemoryStream capture, string status)
        {
            try
            {
                string path = "raw " + (LocalEndPoint == null ? "9100" : LocalEndPoint.Port.ToString());
                byte[] data = capture == null ? null : capture.GetBuffer();
                int length = capture == null ? 0 : (int)capture.Length;
                PrintHistory.Add(remote, Target.Name, path, data, length, status);
            }
            catch { /* the log must never break printing */ }
            finally
            {
                if (capture != null) capture.SetLength(0);
            }
        }

        private IPrintJob FinishJob(IPrintJob job, long bytes, string remote, string reason, MemoryStream capture, EscPosResponder scanner, int answeredBefore)
        {
            // Analyse before RecordHistory, which resets the capture buffer.
            List<string> problems;
            string contents = Summarize(capture, bytes, out problems);
            string ticket = RenderTicket(capture);
            try
            {
                job.Complete();
                Interlocked.Increment(ref _jobsCompleted);
                Emit("Job sent to \"" + Target.Name + "\": " + FormatBytes(bytes) + " from " + remote + " (" + reason + ")");
                RecordHistory(remote, bytes, capture, "Printed");
                PrinterActionLog.Add(ActionLevel.Info, Target.Name, remote, "Sent " + FormatBytes(bytes) + " to the printer (" + reason + ")", contents, ticket);
                foreach (string p in problems) PrinterActionLog.Warn(Target.Name, remote, "Check this job", p);
            }
            catch (Exception ex)
            {
                Emit("Failed to finish job from " + remote + ": " + ex.Message);
                RecordHistory(remote, bytes, capture, "Failed: " + ex.Message);
                PrinterActionLog.Add(ActionLevel.Error, Target.Name, remote, "Job failed while being handed to Windows", ex.Message + ". The job contained: " + contents, ticket);
            }
            finally
            {
                try { job.Dispose(); } catch { }
            }
            LogQueries(scanner, answeredBefore, remote, false);
            return null;
        }

        /// <summary>The ticket as it would look on paper, from the captured start of the job.</summary>
        private static string RenderTicket(MemoryStream capture)
        {
            try
            {
                if (capture == null || capture.Length == 0) return "";
                return EscPosTicketText.Render(capture.GetBuffer(), (int)capture.Length);
            }
            catch { return ""; }
        }

        /// <summary>What the job told the printer, in words, from the captured start of the job.</summary>
        private static string Summarize(MemoryStream capture, long totalBytes, out List<string> problems)
        {
            problems = new List<string>();
            try
            {
                byte[] data = capture == null ? null : capture.GetBuffer();
                int length = capture == null ? 0 : (int)capture.Length;
                EscPosJobSummary s = EscPosJobSummary.Analyze(data, length);
                problems = s.Problems();
                string text = s.Describe();
                if (totalBytes > length) text += " (first " + FormatBytes(length) + " of " + FormatBytes(totalBytes) + " analysed)";
                return text;
            }
            catch (Exception ex)
            {
                return "could not analyse the job: " + ex.Message;
            }
        }

        /// <summary>Records the status/identity queries the bridge answered on the printer's behalf since the last job.</summary>
        private void LogQueries(EscPosResponder scanner, int answeredBefore, string remote, bool withoutJob)
        {
            if (scanner == null) return;
            int n = scanner.QueriesAnswered - answeredBefore;
            if (n <= 0) return;
            List<string> recent = scanner.RecentQueries;
            int take = Math.Min(n, recent.Count);
            string names = string.Join(", ", recent.GetRange(recent.Count - take, take).ToArray());
            PrinterActionLog.Info(Target.Name, remote,
                "Answered " + n + " printer quer" + (n == 1 ? "y" : "ies") + " on the printer's behalf" + (withoutJob ? " (no print data followed)" : ""),
                names + ". A USB printer cannot reply through the Windows spooler, so the bridge answers as a TM-T20II would.");
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("0.##") + " MB";
        }
    }
}
