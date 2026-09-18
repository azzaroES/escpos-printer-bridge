using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace UsbLanPrinterBridge.Core
{
    /// <summary>
    /// Thin wrapper over winspool.drv so raw bytes can be pushed straight to a Windows printer queue.
    /// All calls are the Unicode (W) entry points, which exist on every Windows since 2000.
    /// </summary>
    internal static class RawPrinterHelper
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DOC_INFO_1
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string pDocName;
            [MarshalAs(UnmanagedType.LPWStr)] public string pOutputFile;
            [MarshalAs(UnmanagedType.LPWStr)] public string pDatatype;
        }

        [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool OpenPrinter([MarshalAs(UnmanagedType.LPWStr)] string szPrinter, out IntPtr hPrinter, IntPtr pDefault);

        [DllImport("winspool.drv", EntryPoint = "ClosePrinter", SetLastError = true)]
        private static extern bool ClosePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint StartDocPrinter(IntPtr hPrinter, int level, ref DOC_INFO_1 pDocInfo);

        [DllImport("winspool.drv", EntryPoint = "EndDocPrinter", SetLastError = true)]
        private static extern bool EndDocPrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", EntryPoint = "AbortPrinter", SetLastError = true)]
        private static extern bool AbortPrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", EntryPoint = "StartPagePrinter", SetLastError = true)]
        private static extern bool StartPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", EntryPoint = "EndPagePrinter", SetLastError = true)]
        private static extern bool EndPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", EntryPoint = "WritePrinter", SetLastError = true)]
        private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);

        private static Win32Exception LastError(string what)
        {
            int code = Marshal.GetLastWin32Error();
            var inner = new Win32Exception(code);
            return new Win32Exception(code, what + ": " + inner.Message + " (error " + code + ")");
        }

        /// <summary>Opens a printer handle; throws Win32Exception if the queue does not exist or access is denied.</summary>
        public static IntPtr Open(string printerName)
        {
            if (string.IsNullOrWhiteSpace(printerName)) throw new ArgumentException("Printer name is empty.", "printerName");
            IntPtr h;
            if (!OpenPrinter(printerName, out h, IntPtr.Zero) || h == IntPtr.Zero)
                throw LastError("OpenPrinter(\"" + printerName + "\")");
            return h;
        }

        public static void Close(IntPtr hPrinter)
        {
            if (hPrinter != IntPtr.Zero) ClosePrinter(hPrinter);
        }

        /// <summary>Starts a RAW document. Returns the spooler job id.</summary>
        public static uint StartRawDocument(IntPtr hPrinter, string docName, string outputFile)
        {
            var di = new DOC_INFO_1
            {
                pDocName = string.IsNullOrEmpty(docName) ? "LAN Bridge Document" : docName,
                pOutputFile = string.IsNullOrEmpty(outputFile) ? null : outputFile,
                pDatatype = "RAW"
            };
            uint jobId = StartDocPrinter(hPrinter, 1, ref di);
            if (jobId == 0) throw LastError("StartDocPrinter");
            if (!StartPagePrinter(hPrinter))
            {
                var ex = LastError("StartPagePrinter");
                EndDocPrinter(hPrinter);
                throw ex;
            }
            return jobId;
        }

        /// <summary>Writes the whole buffer, looping on partial writes.</summary>
        public static void Write(IntPtr hPrinter, byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException("buffer");
            if (offset < 0 || count < 0 || offset + count > buffer.Length) throw new ArgumentOutOfRangeException("count");
            if (count == 0) return;

            GCHandle pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                IntPtr basePtr = pin.AddrOfPinnedObject();
                int done = 0;
                while (done < count)
                {
                    int written;
                    IntPtr p = IntPtr.Add(basePtr, offset + done);
                    if (!WritePrinter(hPrinter, p, count - done, out written))
                        throw LastError("WritePrinter");
                    if (written <= 0)
                        throw new Win32Exception("WritePrinter wrote 0 bytes; the printer port is not accepting data.");
                    done += written;
                }
            }
            finally
            {
                pin.Free();
            }
        }

        public static void EndRawDocument(IntPtr hPrinter)
        {
            bool pageOk = EndPagePrinter(hPrinter);
            Win32Exception pageErr = pageOk ? null : LastError("EndPagePrinter");
            if (!EndDocPrinter(hPrinter)) throw LastError("EndDocPrinter");
            if (pageErr != null) throw pageErr;
        }

        public static void AbortDocument(IntPtr hPrinter)
        {
            AbortPrinter(hPrinter);
        }
    }
}
