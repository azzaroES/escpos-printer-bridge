using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Prng;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities;
using Org.BouncyCastle.X509;

namespace UsbLanPrinterBridge.Core
{
    /// <summary>
    /// Generates and caches a self-signed TLS certificate for the HTTPS ePOS endpoint. Uses BouncyCastle so it
    /// works on every Windows from 8 upwards without admin rights (the .NET 4.5 X509 classes cannot create certs).
    ///
    /// The certificate is written to the data directory as a PFX (private key, used by the server) and a CER
    /// (public certificate, which the user installs as trusted on client devices / browsers to remove warnings).
    /// It is regenerated automatically if the set of local IP addresses it must cover changes.
    /// </summary>
    public static class SelfSignedCertificate
    {
        private const string PfxPassword = "usblanbridge"; // local artifact; protection is the key file's ACL, not this
        public const string FriendlyName = "USB LAN Printer Bridge (self-signed)";

        private static readonly object Gate = new object();
        private static X509Certificate2 _cached;
        private static string _cachedSans;

        public static string PfxPath { get { return Path.Combine(CertDir, "bridge.pfx"); } }
        public static string CerPath { get { return Path.Combine(CertDir, "bridge.cer"); } }
        private static string CertDir { get { return Path.Combine(ConfigStore.DataDirectory, "certs"); } }

        /// <summary>
        /// Returns a usable server certificate covering the given addresses (plus localhost). Reuses the cached
        /// PFX when it already covers them; otherwise creates a new one. Never throws for a missing file.
        /// </summary>
        public static X509Certificate2 GetOrCreate(IEnumerable<IPAddress> addresses)
        {
            var ips = new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { "127.0.0.1" };
            if (addresses != null)
                foreach (IPAddress a in addresses)
                    if (a != null && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !a.Equals(IPAddress.Any))
                        ips.Add(a.ToString());
            string sanKey = string.Join(",", ips.ToArray());

            lock (Gate)
            {
                if (_cached != null && _cachedSans == sanKey) return _cached;

                // Try to reuse a PFX on disk if it still covers the requested addresses.
                if (_cached == null && File.Exists(PfxPath) && SansFileMatches(sanKey))
                {
                    try
                    {
                        X509Certificate2 loaded = LoadPfx(File.ReadAllBytes(PfxPath));
                        if (loaded != null && loaded.HasPrivateKey)
                        {
                            _cached = loaded;
                            _cachedSans = sanKey;
                            return _cached;
                        }
                        Logger.Warn("The stored HTTPS certificate has no usable private key; generating a new one.");
                    }
                    catch { /* fall through and regenerate */ }
                }

                X509Certificate2 created = Create(ips.ToList(), sanKey);
                _cached = created;
                _cachedSans = sanKey;
                return created;
            }
        }

        private static bool SansFileMatches(string sanKey)
        {
            try
            {
                string marker = PfxPath + ".sans";
                return File.Exists(marker) && File.ReadAllText(marker).Trim() == sanKey;
            }
            catch { return false; }
        }

        private static X509Certificate2 Create(List<string> ipStrings, string sanKey)
        {
            var random = new SecureRandom(new CryptoApiRandomGenerator());
            var keyGen = new RsaKeyPairGenerator();
            keyGen.Init(new Org.BouncyCastle.Crypto.KeyGenerationParameters(random, 2048));
            AsymmetricCipherKeyPair keyPair = keyGen.GenerateKeyPair();

            var certGen = new X509V3CertificateGenerator();
            var serial = BigIntegers.CreateRandomInRange(BigInteger.One, BigInteger.ValueOf(long.MaxValue), random);
            certGen.SetSerialNumber(serial);

            var dn = new X509Name("CN=USB LAN Printer Bridge, O=USB LAN Printer Bridge");
            certGen.SetIssuerDN(dn);
            certGen.SetSubjectDN(dn);
            certGen.SetNotBefore(DateTime.UtcNow.AddDays(-1));
            certGen.SetNotAfter(DateTime.UtcNow.AddYears(10));
            certGen.SetPublicKey(keyPair.Public);

            certGen.AddExtension(X509Extensions.BasicConstraints, true, new BasicConstraints(false));
            certGen.AddExtension(X509Extensions.KeyUsage, true, new KeyUsage(KeyUsage.DigitalSignature | KeyUsage.KeyEncipherment));
            certGen.AddExtension(X509Extensions.ExtendedKeyUsage, false, new ExtendedKeyUsage(KeyPurposeID.IdKPServerAuth));

            var names = new List<GeneralName> { new GeneralName(GeneralName.DnsName, "localhost") };
            foreach (string ip in ipStrings) names.Add(new GeneralName(GeneralName.IPAddress, ip));
            certGen.AddExtension(X509Extensions.SubjectAlternativeName, false, new GeneralNames(names.ToArray()));

            ISignatureFactory sigFactory = new Asn1SignatureFactory("SHA256WithRSA", keyPair.Private, random);
            Org.BouncyCastle.X509.X509Certificate bcCert = certGen.Generate(sigFactory);

            // Package into a PKCS#12 store with the private key.
            var store = new Pkcs12StoreBuilder().Build();
            var entry = new X509CertificateEntry(bcCert);
            store.SetKeyEntry("bridge", new AsymmetricKeyEntry(keyPair.Private), new[] { entry });

            byte[] pfxBytes;
            using (var ms = new MemoryStream())
            {
                store.Save(ms, PfxPassword.ToCharArray(), random);
                pfxBytes = ms.ToArray();
            }

            try
            {
                Directory.CreateDirectory(CertDir);
                File.WriteAllBytes(PfxPath, pfxBytes);
                File.WriteAllText(PfxPath + ".sans", sanKey);
                // Public certificate (DER) for the user to install as trusted on clients.
                File.WriteAllBytes(CerPath, DotNetUtilities.ToX509Certificate(bcCert).Export(X509ContentType.Cert));
            }
            catch (Exception ex)
            {
                Logger.Warn("Could not save the HTTPS certificate to disk: " + ex.Message + " (a temporary in-memory certificate will be used).");
            }

            X509Certificate2 result = LoadPfx(pfxBytes);
            if (result == null || !result.HasPrivateKey)
                throw new InvalidOperationException("The generated certificate has no usable private key, so HTTPS cannot start.");
            try { result.FriendlyName = FriendlyName; } catch { }
            Logger.Info("Generated a self-signed HTTPS certificate for " + sanKey + " (valid 10 years).");
            return result;
        }

        /// <summary>
        /// Loads a PFX so that SslStream can use it as a server certificate.
        ///
        /// The key storage flags matter. Schannel needs to reach the private key, and which container it lands in
        /// depends on whether the process is elevated. A failure here shows up much later as the unhelpful
        /// "A call to SSPI failed", so each combination is tried in turn and the first usable one wins.
        /// </summary>
        private static X509Certificate2 LoadPfx(byte[] pfxBytes)
        {
            X509KeyStorageFlags[] attempts =
            {
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.MachineKeySet,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet,
                X509KeyStorageFlags.DefaultKeySet
            };

            Exception last = null;
            foreach (X509KeyStorageFlags flags in attempts)
            {
                try
                {
                    var candidate = new X509Certificate2(pfxBytes, PfxPassword, flags);
                    if (candidate.HasPrivateKey) return candidate;
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }

            if (last != null) Logger.Warn("Could not load the HTTPS certificate key: " + last.Message);
            return null;
        }

        /// <summary>Exports the public certificate to a file the user can copy to client devices. Returns the path.</summary>
        public static string ExportPublicCertificate(string destinationPath, IEnumerable<IPAddress> addresses)
        {
            X509Certificate2 cert = GetOrCreate(addresses);
            byte[] der = cert.Export(X509ContentType.Cert);
            File.WriteAllBytes(destinationPath, der);
            return destinationPath;
        }
    }
}
