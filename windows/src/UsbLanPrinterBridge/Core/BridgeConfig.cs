using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;

namespace UsbLanPrinterBridge.Core
{
    /// <summary>One printer ↔ IP:port mapping (one row in the grid).</summary>
    public sealed class MappingConfig
    {
        public MappingConfig()
        {
            Id = Guid.NewGuid().ToString("N");
            Enabled = true;
            Port = 9100;
            Adapter = "";
            EscPosStatusReplies = true;
            EposEnabled = true;
            // ePOS-Print's own default is plain 80/443: the SDK builds "http://<address>/cgi-bin/epos/service.cgi"
            // with no port, so POS apps that only ask for an IP land here. 8008/8043 are served as alternates too.
            EposHttpPort = 80;
            EposHttpsPort = 443;
            EposModelName = "TM-T20II";
            EposDeviceId = DefaultEposDeviceId;
        }

        public const string DefaultEposDeviceId = "local_printer";

        /// <summary>
        /// The device id this mapping answers to in ePOS-Print requests (the "devid" in the URL, or createDevice in
        /// the SDK). A request naming another id gets DeviceNotFound, as a real printer answers.
        /// </summary>
        public string EposDeviceId { get; set; }

        /// <summary>Keeps only the characters a device id may contain; empty becomes the default.</summary>
        public static string SanitizeDeviceId(string id)
        {
            if (id == null) return DefaultEposDeviceId;
            var sb = new System.Text.StringBuilder();
            foreach (char c in id.Trim())
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.') sb.Append(c);
            return sb.Length == 0 ? DefaultEposDeviceId : (sb.Length > 32 ? sb.ToString(0, 32) : sb.ToString());
        }

        /// <summary>The ePOS-Print URL a client should use for this mapping, with the device id and the SDK's usual timeout.</summary>
        public string EposUrl(bool https, string host)
        {
            int port = https ? EposHttpsPort : EposHttpPort;
            bool defaultPort = https ? port == 443 : port == 80;
            return (https ? "https://" : "http://") + host + (defaultPort ? "" : ":" + port) + "/cgi-bin/epos/service.cgi?devid=" + SanitizeDeviceId(EposDeviceId) + "&timeout=10000";
        }

        /// <summary>The page a client opens once to trust this bridge's certificate.</summary>
        public string CertificateUrl(string host)
        {
            return "https://" + host + (EposHttpsPort == 443 ? "" : ":" + EposHttpsPort) + "/cert";
        }

        [XmlAttribute] public string Id { get; set; }
        public bool Enabled { get; set; }
        public string PrinterName { get; set; }
        public string BindAddress { get; set; }
        public int Port { get; set; }
        /// <summary>Adapter name for the virtual IP. Empty = choose automatically.</summary>
        public string Adapter { get; set; }
        public bool EscPosStatusReplies { get; set; }

        /// <summary>Also expose an Epson ePOS-Print HTTP/HTTPS endpoint so web/Android POS apps can print to this printer.</summary>
        public bool EposEnabled { get; set; }
        public int EposHttpPort { get; set; }
        public int EposHttpsPort { get; set; }
        /// <summary>Model name reported to clients that ask (GS I 67). Mirrors a real Epson so SDK clients accept the printer.</summary>
        public string EposModelName { get; set; }

        /// <summary>
        /// Emergency switch for this printer: remove every cutter command from every job sent to it, whatever the
        /// app or driver asked for. Read on every write, so it can be ticked while the bridge is running.
        /// </summary>
        public bool NoCut { get; set; }

        public string EndpointText { get { return (string.IsNullOrEmpty(BindAddress) ? "0.0.0.0" : BindAddress) + ":" + Port; } }

        public MappingConfig Clone()
        {
            return (MappingConfig)MemberwiseClone();
        }
    }

    /// <summary>The order the user dragged one group of sections into.</summary>
    public sealed class SectionOrder
    {
        [XmlAttribute] public string Group { get; set; }
        /// <summary>Section keys, comma separated.</summary>
        [XmlAttribute] public string Keys { get; set; }
    }

    [XmlRoot("UsbLanPrinterBridge")]
    public sealed class BridgeConfig
    {
        public BridgeConfig()
        {
            Mappings = new List<MappingConfig>();
            JobIdleTimeoutMs = 1500;
            AutoFirewallRule = true;
            AutoStartBridges = false;
            CloseToTray = true;
            NoCutFeedLines = 4;
            CollapsedSections = new List<string>();
            SectionOrders = new List<SectionOrder>();
        }

        public List<MappingConfig> Mappings { get; set; }

        /// <summary>Keys of the sections the user folded up (Device tab cards and their blocks, the settings rows).</summary>
        [XmlArrayItem("Key")] public List<string> CollapsedSections { get; set; }
        /// <summary>The order of each group of sections, as the user dragged them.</summary>
        public List<SectionOrder> SectionOrders { get; set; }

        public bool IsSectionCollapsed(string key) { return CollapsedSections != null && CollapsedSections.Contains(key); }

        public void SetSectionCollapsed(string key, bool collapsed)
        {
            if (CollapsedSections == null) CollapsedSections = new List<string>();
            CollapsedSections.Remove(key);
            if (collapsed) CollapsedSections.Add(key);
        }

        public string[] GetSectionOrder(string group)
        {
            if (SectionOrders == null) return null;
            foreach (SectionOrder o in SectionOrders)
                if (o.Group == group && !string.IsNullOrEmpty(o.Keys)) return o.Keys.Split(',');
            return null;
        }

        public void SetSectionOrder(string group, string[] keys)
        {
            if (SectionOrders == null) SectionOrders = new List<SectionOrder>();
            SectionOrders.RemoveAll(o => o.Group == group);
            SectionOrders.Add(new SectionOrder { Group = group, Keys = string.Join(",", keys ?? new string[0]) });
        }

        /// <summary>Silence on a connection (ms) that ends the current print job. 0 = only on disconnect.</summary>
        public int JobIdleTimeoutMs { get; set; }
        public bool AutoFirewallRule { get; set; }
        /// <summary>Start every enabled mapping as soon as the app opens.</summary>
        public bool AutoStartBridges { get; set; }
        /// <summary>The window's close button hides to the tray instead of exiting.</summary>
        public bool CloseToTray { get; set; }

        /// <summary>Lines fed in place of each cut removed by a mapping's NO CUT switch, so the receipt reaches the tear bar. 0 = nothing.</summary>
        public int NoCutFeedLines { get; set; }

        public int SanitizedIdleTimeout
        {
            get { return JobIdleTimeoutMs < 0 ? 0 : JobIdleTimeoutMs > 600000 ? 600000 : JobIdleTimeoutMs; }
        }

        public int SanitizedNoCutFeedLines
        {
            get { return NoCutFeedLines < 0 ? 0 : NoCutFeedLines > NoCutSettings.MaxFeedLines ? NoCutSettings.MaxFeedLines : NoCutFeedLines; }
        }
    }

    /// <summary>Loads/saves the XML configuration. Machine-wide (ProgramData) when writable, otherwise per-user.</summary>
    public static class ConfigStore
    {
        public const string AppFolderName = "UsbLanPrinterBridge";
        private static readonly object Gate = new object();
        private static string _dataDirectory;

        /// <summary>Directory that holds config.xml and the logs folder.</summary>
        public static string DataDirectory
        {
            get
            {
                lock (Gate)
                {
                    if (_dataDirectory == null) _dataDirectory = ResolveDataDirectory();
                    return _dataDirectory;
                }
            }
            set { lock (Gate) _dataDirectory = value; }
        }

        public static string ConfigPath { get { return Path.Combine(DataDirectory, "config.xml"); } }
        public static string LogDirectory { get { return Path.Combine(DataDirectory, "logs"); } }

        private static string ResolveDataDirectory()
        {
            string[] candidates =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppFolderName),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName)
            };
            foreach (string dir in candidates)
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    string probe = Path.Combine(dir, ".write-test");
                    File.WriteAllText(probe, "ok");
                    File.Delete(probe);
                    return dir;
                }
                catch
                {
                    // not writable here; try the next location
                }
            }
            return candidates[candidates.Length - 1];
        }

        public static BridgeConfig Load()
        {
            return Load(ConfigPath);
        }

        public static BridgeConfig Load(string path)
        {
            lock (Gate)
            {
                if (!File.Exists(path)) return new BridgeConfig();
                var serializer = new XmlSerializer(typeof(BridgeConfig));
                using (var stream = File.OpenRead(path))
                {
                    var cfg = (BridgeConfig)serializer.Deserialize(stream);
                    if (cfg.Mappings == null) cfg.Mappings = new List<MappingConfig>();
                    if (cfg.CollapsedSections == null) cfg.CollapsedSections = new List<string>();
                    if (cfg.SectionOrders == null) cfg.SectionOrders = new List<SectionOrder>();
                    foreach (MappingConfig m in cfg.Mappings)
                    {
                        if (string.IsNullOrEmpty(m.Id)) m.Id = Guid.NewGuid().ToString("N");
                        if (m.Adapter == null) m.Adapter = "";
                        if (m.Port <= 0 || m.Port > 65535) m.Port = 9100;
                        if (m.EposHttpPort <= 0 || m.EposHttpPort > 65535) m.EposHttpPort = 80;
                        if (m.EposHttpsPort <= 0 || m.EposHttpsPort > 65535) m.EposHttpsPort = 443;
                        if (string.IsNullOrWhiteSpace(m.EposModelName)) m.EposModelName = "TM-T20II";
                        m.EposDeviceId = MappingConfig.SanitizeDeviceId(m.EposDeviceId);
                    }
                    return cfg;
                }
            }
        }

        public static void Save(BridgeConfig config)
        {
            Save(config, ConfigPath);
        }

        public static void Save(BridgeConfig config, string path)
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temp = path + ".tmp";
                var serializer = new XmlSerializer(typeof(BridgeConfig));
                using (var stream = File.Create(temp))
                {
                    serializer.Serialize(stream, config);
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
        }
    }
}
