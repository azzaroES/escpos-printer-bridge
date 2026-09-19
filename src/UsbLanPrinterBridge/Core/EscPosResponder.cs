using System;
using System.Collections.Generic;
using System.Text;

namespace UsbLanPrinterBridge.Core
{
    /// <summary>
    /// Answers the status and identity queries that Epson's ePOS SDK (and most POS software) sends down the raw
    /// 9100 stream, on behalf of a USB printer that can never reply through the Windows spooler.
    ///
    /// Every response value here was captured from a real Epson TM-T20II on the LAN, so clients that verify the
    /// printer before accepting it behave the same against the bridge as against genuine hardware:
    ///
    ///   DLE EOT 1  -> 0x16              printer status, online, no error
    ///   DLE EOT 2  -> 0x12              offline cause, none
    ///   DLE EOT 3  -> 0x12              error cause, none
    ///   DLE EOT 4  -> 0x12              paper sensor, paper present
    ///   GS  I  1   -> 0x63              printer model id
    ///   GS  I  67  -> 0x5F "TM-T20II" 0x00   model name, header + text + NUL
    ///   GS  a  n   -> 14 00 00 0F       automatic status back, 4 bytes
    ///   GS  ( H    -> 0x37 0x22 id 0x00 process id echo: how the SDK learns a job finished
    ///
    /// The queries are consumed rather than forwarded: they are not print data, and passing them to the spooler
    /// would create empty jobs. Everything else passes through untouched, including print commands that start with
    /// the same lead bytes such as GS ( k for QR codes.
    ///
    /// The parser is incremental. A query split across TCP reads is still recognised, and at most a few bytes are
    /// held back between calls; <see cref="Flush"/> releases them.
    /// </summary>
    public sealed class EscPosResponder
    {
        public const byte DLE = 0x10;
        public const byte EOT = 0x04;
        public const byte GS = 0x1D;

        /// <summary>Model name reported to GS I 67. Mirrors a widely supported Epson model so SDK clients accept it.</summary>
        public string ModelName { get; set; }

        private readonly List<byte> _buffer = new List<byte>(64);
        private byte[] _forward = new byte[8192];
        private byte[] _replies = new byte[64];

        public EscPosResponder() { ModelName = "TM-T20II"; }

        public byte[] Forward { get { return _forward; } }
        public int ForwardLength { get; private set; }
        public byte[] Replies { get { return _replies; } }
        public int ReplyLength { get; private set; }

        /// <summary>Number of queries answered so far (diagnostics).</summary>
        public int QueriesAnswered { get; private set; }

        /// <summary>The last few queries answered, by name, oldest first (diagnostics for the printer actions log).</summary>
        public List<string> RecentQueries { get { return _recent; } }
        private readonly List<string> _recent = new List<string>();
        private const int RecentLimit = 12;

        private void Note(string query)
        {
            if (_recent.Count >= RecentLimit) _recent.RemoveAt(0);
            _recent.Add(query);
        }

        private enum Match { Complete, Incomplete, No }

        public void Process(byte[] input, int offset, int count)
        {
            ForwardLength = 0;
            ReplyLength = 0;
            EnsureCapacity(ref _forward, count + _buffer.Count + 8);

            for (int i = 0; i < count; i++) _buffer.Add(input[offset + i]);

            int pos = 0;
            while (pos < _buffer.Count)
            {
                byte b = _buffer[pos];
                if (b != DLE && b != GS) { Emit(b); pos++; continue; }

                int consumed;
                Match m = TryMatch(pos, out consumed);
                if (m == Match.Complete) { pos += consumed; QueriesAnswered++; }
                else if (m == Match.No) { Emit(b); pos++; }
                else break; // incomplete: keep the rest for the next chunk
            }

            if (pos > 0) _buffer.RemoveRange(0, pos);

            // Safety valve: never hold an unbounded amount of data waiting for a query that will not complete.
            if (_buffer.Count > 64 * 1024) FlushBuffer();
        }

        /// <summary>Releases bytes held back as a possible query. Call when the client disconnects or the job ends.</summary>
        public void Flush()
        {
            ForwardLength = 0;
            ReplyLength = 0;
            FlushBuffer();
        }

        private void FlushBuffer()
        {
            EnsureCapacity(ref _forward, ForwardLength + _buffer.Count);
            for (int i = 0; i < _buffer.Count; i++) Emit(_buffer[i]);
            _buffer.Clear();
        }

        private byte At(int index) { return _buffer[index]; }
        private int Available(int from) { return _buffer.Count - from; }

        private Match TryMatch(int pos, out int consumed)
        {
            consumed = 0;
            byte lead = At(pos);

            if (lead == DLE)
            {
                // DLE EOT n : real-time status request.
                if (Available(pos) < 2) return Match.Incomplete;
                if (At(pos + 1) != EOT) return Match.No;      // e.g. DLE DC4 drawer pulse: pass through
                if (Available(pos) < 3) return Match.Incomplete;
                byte n = At(pos + 2);
                if (n < 1 || n > 4) return Match.No;
                Reply(StatusFor(n));
                Note("DLE EOT " + n + " (" + (n == 1 ? "printer" : n == 2 ? "offline" : n == 3 ? "error" : "paper") + " status)");
                consumed = 3;
                return Match.Complete;
            }

            // lead == GS
            if (Available(pos) < 2) return Match.Incomplete;
            byte second = At(pos + 1);

            if (second == 0x49) // GS I n : transmit printer id
            {
                if (Available(pos) < 3) return Match.Incomplete;
                ReplyPrinterId(At(pos + 2));
                Note("GS I " + At(pos + 2) + " (printer id" + (At(pos + 2) >= 65 ? ", model name" : "") + ")");
                consumed = 3;
                return Match.Complete;
            }

            if (second == 0x61) // GS a n : enable automatic status back
            {
                if (Available(pos) < 3) return Match.Incomplete;
                Reply(0x14); Reply(0x00); Reply(0x00); Reply(0x0F);
                Note("GS a (auto status back)");
                consumed = 3;
                return Match.Complete;
            }

            if (second == 0x72) // GS r n : transmit status
            {
                if (Available(pos) < 3) return Match.Incomplete;
                Reply(0x00); // paper present / drawer low
                Note("GS r " + At(pos + 2) + " (paper/drawer status)");
                consumed = 3;
                return Match.Complete;
            }

            if (second == 0x28) // GS ( ... : only GS ( H is a query; GS ( k and friends are print data
            {
                if (Available(pos) < 3) return Match.Incomplete;
                if (At(pos + 2) != 0x48) return Match.No;
                if (Available(pos) < 5) return Match.Incomplete;
                int len = At(pos + 3) | (At(pos + 4) << 8);
                if (len < 2 || len > 1024) return Match.No; // implausible: treat as print data
                int total = 5 + len;
                if (Available(pos) < total) return Match.Incomplete;

                // GS ( H pL pH fn m d1..dk  ->  0x37 0x22 d1..dk 0x00
                Reply(0x37);
                Reply(0x22);
                for (int i = pos + 7; i < pos + total; i++) Reply(At(i));
                Reply(0x00);
                Note("GS ( H (process id echo)");
                consumed = total;
                return Match.Complete;
            }

            return Match.No;
        }

        private static byte StatusFor(byte n)
        {
            // Captured from a real TM-T20II: n=1 -> 0x16, n=2..4 -> 0x12.
            return n == 1 ? (byte)0x16 : (byte)0x12;
        }

        private void ReplyPrinterId(byte n)
        {
            switch (n)
            {
                case 1:
                case 49:
                    Reply(0x63); // printer model id
                    break;
                case 2:
                case 50:
                    Reply(0x00); // type id
                    break;
                case 3:
                case 51:
                    Reply(0x00); // firmware version id
                    break;
                default:
                    if (n >= 65)
                    {
                        // Header + text + NUL, the format the real printer uses for GS I 67.
                        Reply(0x5F);
                        foreach (byte c in Encoding.ASCII.GetBytes(ModelName ?? "TM-T20II")) Reply(c);
                        Reply(0x00);
                    }
                    else Reply(0x00);
                    break;
            }
        }

        private void Emit(byte b)
        {
            EnsureCapacity(ref _forward, ForwardLength + 1);
            _forward[ForwardLength++] = b;
        }

        private void Reply(byte b)
        {
            EnsureCapacity(ref _replies, ReplyLength + 1);
            _replies[ReplyLength++] = b;
        }

        private static void EnsureCapacity(ref byte[] buffer, int needed)
        {
            if (buffer.Length >= needed) return;
            int size = buffer.Length;
            while (size < needed) size *= 2;
            Array.Resize(ref buffer, size);
        }
    }
}
