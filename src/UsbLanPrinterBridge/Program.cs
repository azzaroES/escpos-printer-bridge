using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using UsbLanPrinterBridge.Core;
using UsbLanPrinterBridge.UI;

namespace UsbLanPrinterBridge
{
    public sealed class StartupOptions
    {
        /// <summary>Start every enabled mapping immediately and open minimised to the tray (used by the logon task).</summary>
        public bool AutoStart { get; set; }
        /// <summary>Do not try to elevate. Virtual IPs and the firewall rule won't work, everything else does.</summary>
        public bool NoElevate { get; set; }
        public bool Minimized { get; set; }
        /// <summary>Headless: generate/export the HTTPS public certificate to this path and exit.</summary>
        public string ExportCertPath { get; set; }

        public static StartupOptions Parse(string[] args)
        {
            var o = new StartupOptions();
            string[] arr = args ?? new string[0];
            for (int i = 0; i < arr.Length; i++)
            {
                string a = arr[i].Trim().TrimStart('-', '/').ToLowerInvariant();
                if (a == "autostart") { o.AutoStart = true; o.Minimized = true; }
                else if (a == "no-elevate" || a == "noelevate") o.NoElevate = true;
                else if (a == "minimized" || a == "tray") o.Minimized = true;
                else if (a == "export-cert") { o.ExportCertPath = (i + 1 < arr.Length) ? arr[++i] : "UsbLanPrinterBridge.cer"; }
            }
            return o;
        }
    }

    internal static class Program
    {
        public const string AppName = "USB LAN Printer Bridge";

        public static string Version
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                return v.Major + "." + v.Minor + "." + v.Build;
            }
        }

        [STAThread]
        private static void Main(string[] args)
        {
            // BouncyCastle is embedded as a resource so the app stays a single exe; resolve it on demand.
            AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbeddedAssembly;

            StartupOptions options = StartupOptions.Parse(args);

            if (options.ExportCertPath != null)
            {
                Environment.ExitCode = RunExportCert(options.ExportCertPath);
                return;
            }

            if (!options.NoElevate && !BridgeManager.DetectElevation())
            {
                if (TryRelaunchElevated(args)) return;
                // UAC cancelled: keep going without admin rights so the user can still bind to existing addresses.
            }

            bool createdNew;
            using (var mutex = new Mutex(true, @"Local\UsbLanPrinterBridge.SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show(AppName + " is already running. Look for its icon in the notification area.",
                        AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => ReportCrash(e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => ReportCrash(e.ExceptionObject as Exception);

                Logger.Info("---- " + AppName + " " + Version + " starting (" + Environment.OSVersion + ", .NET " + Environment.Version + ", admin=" + BridgeManager.DetectElevation() + ")");
                try
                {
                    Application.Run(new MainForm(options));
                }
                finally
                {
                    Logger.Shutdown();
                }
                GC.KeepAlive(mutex);
            }
        }

        /// <summary>Headless certificate export (also proves the embedded BouncyCastle loads from the single-file exe).</summary>
        private static int RunExportCert(string path)
        {
            try
            {
                string full = Core.SelfSignedCertificate.ExportPublicCertificate(path, Core.NetworkHelper.GetHostIPv4Addresses());
                Console.Out.WriteLine("Exported certificate to " + full);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Certificate export failed: " + ex.Message);
                return 1;
            }
        }

        private static bool TryRelaunchElevated(string[] args)
        {
            try
            {
                string exe = Process.GetCurrentProcess().MainModule.FileName;
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = string.Join(" ", (args ?? new string[0]).Select(Quote).ToArray()),
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = Path.GetDirectoryName(exe)
                };
                Process.Start(psi);
                return true;
            }
            catch (Win32Exception)
            {
                return false; // user declined the UAC prompt
            }
            catch
            {
                return false;
            }
        }

        private static string Quote(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return "\"\"";
            return arg.IndexOf(' ') >= 0 ? "\"" + arg + "\"" : arg;
        }

        private static void ReportCrash(Exception ex)
        {
            if (ex == null) return;
            try { Logger.Error("Unhandled exception: " + ex); } catch { }
            try
            {
                MessageBox.Show("Something went wrong:\n\n" + ex.Message + "\n\nDetails were written to the log folder.",
                    AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }

        private static readonly object ResolveGate = new object();
        private static System.Collections.Generic.Dictionary<string, Assembly> _resolved;

        private static Assembly ResolveEmbeddedAssembly(object sender, ResolveEventArgs args)
        {
            string simpleName = new AssemblyName(args.Name).Name;
            string resource = simpleName + ".dll";
            lock (ResolveGate)
            {
                if (_resolved == null) _resolved = new System.Collections.Generic.Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
                Assembly cached;
                if (_resolved.TryGetValue(resource, out cached)) return cached;

                Assembly self = Assembly.GetExecutingAssembly();
                using (Stream s = self.GetManifestResourceStream(resource))
                {
                    if (s == null) { _resolved[resource] = null; return null; }
                    var bytes = new byte[s.Length];
                    int read = 0;
                    while (read < bytes.Length)
                    {
                        int n = s.Read(bytes, read, bytes.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    Assembly loaded = Assembly.Load(bytes);
                    _resolved[resource] = loaded;
                    return loaded;
                }
            }
        }

        /// <summary>Loads the embedded multi-size app icon at the requested size.</summary>
        public static Icon LoadIcon(int size)
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"))
                {
                    if (s != null) return new Icon(s, size, size);
                }
            }
            catch { }
            return SystemIcons.Application;
        }
    }
}
