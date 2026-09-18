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

        public string EndpointText { get { return (string.IsNullOrEmpty(BindAddress) ? "0.0.0.0" : BindAddress) + ":" + Port; } }

        public MappingConfig Clone()
        {
            return (MappingConfig)MemberwiseClone();
        }
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
        }

        public List<MappingConfig> Mappings { get; set; }

        /// <summary>Silence on a connection (ms) that ends the current print job. 0 = only on disconnect.</summary>
        public int JobIdleTimeoutMs { get; set; }
        public bool AutoFirewallRule { get; set; }
        /// <summary>Start every enabled mapping as soon as the app opens.</summary>
        public bool AutoStartBridges { get; set; }
        /// <summary>The window's close button hides to the tray instead of exiting.</summary>
        public bool CloseToTray { get; set; }

        public int SanitizedIdleTimeout
        {
            get { return JobIdleTimeoutMs < 0 ? 0 : JobIdleTimeoutMs > 600000 ? 600000 : JobIdleTimeoutMs; }
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
                    foreach (MappingConfig m in cfg.Mappings)
                    {
                        if (string.IsNullOrEmpty(m.Id)) m.Id = Guid.NewGuid().ToString("N");
                        if (m.Adapter == null) m.Adapter = "";
                        if (m.Port <= 0 || m.Port > 65535) m.Port = 9100;
                        if (m.EposHttpPort <= 0 || m.EposHttpPort > 65535) m.EposHttpPort = 80;
                        if (m.EposHttpsPort <= 0 || m.EposHttpsPort > 65535) m.EposHttpsPort = 443;
                        if (string.IsNullOrWhiteSpace(m.EposModelName)) m.EposModelName = "TM-T20II";
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
