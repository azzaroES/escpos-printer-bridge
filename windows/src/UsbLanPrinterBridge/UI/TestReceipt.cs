using System;
using System.Collections.Generic;
using System.Text;

namespace UsbLanPrinterBridge.UI
{
    /// <summary>Builds a small ESC/POS test receipt (plain ASCII, so it also prints legibly on non-ESC/POS text printers).</summary>
    internal static class TestReceipt
    {
        public static byte[] Build(string printerName, string endpoint, string via)
        {
            var b = new List<byte>();
            Action<byte[]> raw = bytes => b.AddRange(bytes);
            Action<string> text = s => b.AddRange(Encoding.ASCII.GetBytes(s.Replace("\n", "\r\n")));

            raw(new byte[] { 0x1B, 0x40 });             // ESC @  initialise
            raw(new byte[] { 0x1B, 0x61, 0x01 });       // ESC a 1 centre
            raw(new byte[] { 0x1B, 0x21, 0x30 });       // ESC ! 0x30 double width+height
            text("BRIDGE TEST\n");
            raw(new byte[] { 0x1B, 0x21, 0x00 });       // normal
            text("USB LAN Printer Bridge\n");
            raw(new byte[] { 0x1B, 0x61, 0x00 });       // left
            text("------------------------------\n");
            text("Printer : " + Truncate(printerName, 22) + "\n");
            text("Endpoint: " + endpoint + "\n");
            text("Path    : " + via + "\n");
            text("Host    : " + Truncate(Environment.MachineName, 22) + "\n");
            text("Time    : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\n");
            text("------------------------------\n");
            text("If you can read this, the\nbridge is working.\n");
            text("\n\n\n\n");
            raw(new byte[] { 0x1D, 0x56, 0x01 });       // GS V 1 partial cut (ignored by printers without a cutter)
            return b.ToArray();
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max - 1) + "~";
        }
    }
}
