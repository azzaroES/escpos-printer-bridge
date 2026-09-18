using System;
using System.Globalization;

namespace UsbLanPrinterBridge.Core
{
    /// <summary>
    /// "Start with Windows" for an elevated app. A plain Run-key entry is blocked by UAC for programs that need
    /// administrator rights, so a logon-triggered scheduled task with the highest run level is used instead.
    /// schtasks.exe exists on every Windows from XP onwards.
    /// </summary>
    public static class StartupHelper
    {
        public const string TaskName = "USB LAN Printer Bridge";

        public static bool IsEnabled()
        {
            CommandResult r = NetworkHelper.Run("schtasks.exe", "/Query /TN \"" + TaskName + "\"", 15000);
            return r.Success;
        }

        public static CommandResult Enable(string exePath)
        {
            string tr = "\\\"" + exePath + "\\\" --autostart";
            string args = string.Format(CultureInfo.InvariantCulture,
                "/Create /F /SC ONLOGON /RL HIGHEST /TN \"{0}\" /TR \"{1}\"", TaskName, tr);
            CommandResult r = NetworkHelper.Run("schtasks.exe", args, 20000);
            if (r.Success)
            {
                // Give the network a moment to come up before the bridge binds its addresses.
                NetworkHelper.Run("schtasks.exe", "/Change /TN \"" + TaskName + "\" /DELAY 0000:20", 15000);
            }
            return r;
        }

        public static CommandResult Disable()
        {
            return NetworkHelper.Run("schtasks.exe", "/Delete /F /TN \"" + TaskName + "\"", 15000);
        }
    }
}
