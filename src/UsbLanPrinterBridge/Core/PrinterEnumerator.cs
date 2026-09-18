using System;
using System.Collections.Generic;
using System.Drawing.Printing;
using System.Linq;
using System.Management;

namespace UsbLanPrinterBridge.Core
{
    public sealed class PrinterInfo
    {
        public string Name { get; set; }
        public string PortName { get; set; }
        public string DriverName { get; set; }
        public bool IsDefault { get; set; }
        public bool WorkOffline { get; set; }

        /// <summary>True when the queue is attached to a USBxxx port (the usbprint port monitor).</summary>
        public bool IsUsb
        {
            get { return !string.IsNullOrEmpty(PortName) && PortName.StartsWith("USB", StringComparison.OrdinalIgnoreCase); }
        }

        public string DisplayText
        {
            get
            {
                string port = string.IsNullOrEmpty(PortName) ? "" : "  [" + PortName + "]";
                string extra = IsUsb ? "  (USB)" : "";
                return Name + port + extra;
            }
        }

        public override string ToString() { return DisplayText; }
    }

    public static class PrinterEnumerator
    {
        /// <summary>
        /// Lists all installed printer queues. USB queues are listed first.
        /// Uses WMI (Win32_Printer) for port information and falls back to the plain printer list if WMI is unavailable.
        /// </summary>
        public static List<PrinterInfo> GetPrinters()
        {
            List<PrinterInfo> list = null;
            try
            {
                list = FromWmi();
            }
            catch
            {
                list = null;
            }

            if (list == null || list.Count == 0)
            {
                list = new List<PrinterInfo>();
                try
                {
                    foreach (string name in PrinterSettings.InstalledPrinters)
                        list.Add(new PrinterInfo { Name = name, PortName = "" });
                }
                catch
                {
                    // no printers or spooler service stopped
                }
            }

            return list
                .OrderByDescending(p => p.IsUsb)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<PrinterInfo> FromWmi()
        {
            var result = new List<PrinterInfo>();
            using (var searcher = new ManagementObjectSearcher("SELECT Name, PortName, DriverName, Default, WorkOffline FROM Win32_Printer"))
            using (ManagementObjectCollection items = searcher.Get())
            {
                foreach (ManagementBaseObject item in items)
                {
                    var info = new PrinterInfo
                    {
                        Name = Convert.ToString(item["Name"]),
                        PortName = Convert.ToString(item["PortName"]),
                        DriverName = Convert.ToString(item["DriverName"]),
                        IsDefault = ToBool(item["Default"]),
                        WorkOffline = ToBool(item["WorkOffline"])
                    };
                    if (!string.IsNullOrEmpty(info.Name)) result.Add(info);
                }
            }
            return result;
        }

        private static bool ToBool(object value)
        {
            try { return value != null && Convert.ToBoolean(value); }
            catch { return false; }
        }
    }
}
