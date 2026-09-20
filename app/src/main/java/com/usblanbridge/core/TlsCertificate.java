package com.usblanbridge.core;

import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.math.BigInteger;
import java.nio.charset.Charset;
import java.security.KeyFactory;
import java.security.KeyPair;
import java.security.KeyPairGenerator;
import java.security.KeyStore;
import java.security.PrivateKey;
import java.security.SecureRandom;
import java.security.Signature;
import java.security.cert.Certificate;
import java.security.cert.CertificateFactory;
import java.security.cert.X509Certificate;
import java.security.spec.PKCS8EncodedKeySpec;
import java.text.SimpleDateFormat;
import java.util.ArrayList;
import java.util.Calendar;
import java.util.Collections;
import java.util.Date;
import java.util.List;
import java.util.Locale;
import java.util.TimeZone;

import javax.net.ssl.KeyManagerFactory;
import javax.net.ssl.SSLContext;
import javax.net.ssl.SSLServerSocketFactory;

/**
 * A self-signed TLS certificate for the phone's HTTPS ePOS endpoint, built by hand in DER so it works the same
 * on every Android from 5.0 up, with no keystore quirks and no extra library.
 *
 * Why by hand: the Android keystore can mint self-signed certificates, but before Android 9 it signs them
 * with a placeholder signature that browsers reject outright (not even a warning to click through), and it
 * cannot put the phone's IP addresses in the certificate. This builder writes a normal X.509 v3 certificate
 * with the addresses in the subject alternative names, signed with SHA-256 and RSA, so a browser shows the
 * usual "self-signed" warning once and remembers the choice.
 *
 * The key pair and certificate are kept in files; a new certificate is made when the phone's addresses change.
 * Deliberately free of Android imports so it can be verified on a desktop JVM.
 */
public final class TlsCertificate {

    public static final String COMMON_NAME = "USB LAN Printer Bridge";
    private static final int VALID_DAYS = 3650;
    private static final Charset UTF8 = Charset.forName("UTF-8");

    /** A key pair with its certificate, ready for a server socket. */
    public static final class Material {
        public final PrivateKey key;
        public final X509Certificate certificate;
        public final byte[] certificateDer;
        /** True when this material was just generated rather than loaded from disk. */
        public boolean generated;
        /** The addresses in the certificate, comma separated. */
        public String addresses = "";
        /** Anything worth logging that happened while loading, or null. */
        public String note;

        Material(PrivateKey key, X509Certificate certificate, byte[] der) {
            this.key = key;
            this.certificate = certificate;
            this.certificateDer = der;
        }

        public SSLServerSocketFactory serverSocketFactory() throws Exception {
            char[] pw = "bridge".toCharArray();
            KeyStore ks = KeyStore.getInstance(KeyStore.getDefaultType());
            ks.load(null, null);
            ks.setKeyEntry("bridge", key, pw, new Certificate[]{certificate});
            KeyManagerFactory kmf = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm());
            kmf.init(ks, pw);
            SSLContext ctx = SSLContext.getInstance("TLS");
            ctx.init(kmf.getKeyManagers(), null, null);
            return ctx.getServerSocketFactory();
        }
    }

    private TlsCertificate() {
    }

    /**
     * Loads the stored material when it still covers the given addresses, otherwise generates and stores new
     * material. Generation takes a second or two on an old phone; call it off the main thread.
     */
    public static Material load(File dir, List<String> ipAddresses) throws Exception {
        List<String> ips = new ArrayList<>();
        for (String ip : ipAddresses) if (ip != null && isIpv4(ip) && !ips.contains(ip)) ips.add(ip);
        if (!ips.contains("127.0.0.1")) ips.add("127.0.0.1");
        Collections.sort(ips);
        String sans = join(ips);

        File keyFile = new File(dir, "tls-key.p8");
        File certFile = new File(dir, "tls-cert.der");
        File sansFile = new File(dir, "tls-cert.sans");
        String note = null;
        if (keyFile.exists() && certFile.exists() && sansFile.exists() && sans.equals(new String(read(sansFile), UTF8).trim())) {
            try {
                PrivateKey key = KeyFactory.getInstance("RSA").generatePrivate(new PKCS8EncodedKeySpec(read(keyFile)));
                byte[] der = read(certFile);
                X509Certificate cert = parse(der);
                cert.checkValidity();
                Material m = new Material(key, cert, der);
                m.addresses = sans;
                return m;
            } catch (Exception e) {
                note = "Stored HTTPS certificate is unusable (" + e.getMessage() + "); made a new one.";
            }
        }

        Material m = generate(ips, Collections.singletonList("localhost"));
        m.generated = true;
        m.addresses = sans;
        m.note = note;
        try {
            if (!dir.exists()) dir.mkdirs();
            write(keyFile, m.key.getEncoded());
            write(certFile, m.certificateDer);
            write(sansFile, sans.getBytes(UTF8));
        } catch (IOException e) {
            m.note = "Could not save the HTTPS certificate: " + e.getMessage() + " (a temporary one is used).";
        }
        return m;
    }

    /** Builds new material without touching disk. */
    public static Material generate(List<String> ips, List<String> dnsNames) throws Exception {
        KeyPairGenerator gen = KeyPairGenerator.getInstance("RSA");
        gen.initialize(2048, new SecureRandom());
        KeyPair kp = gen.generateKeyPair();
        Calendar cal = Calendar.getInstance(TimeZone.getTimeZone("UTC"));
        cal.add(Calendar.DATE, -1);
        Date notBefore = cal.getTime();
        cal.add(Calendar.DATE, VALID_DAYS + 1);
        Date notAfter = cal.getTime();
        BigInteger serial = new BigInteger(63, new SecureRandom()).setBit(0);
        byte[] der = build(kp, COMMON_NAME, ips, dnsNames, notBefore, notAfter, serial);
        X509Certificate cert = parse(der);
        return new Material(kp.getPrivate(), cert, der);
    }

    public static X509Certificate parse(byte[] der) throws Exception {
        return (X509Certificate) CertificateFactory.getInstance("X.509").generateCertificate(new ByteArrayInputStream(der));
    }

    // ------------------------------------------------------------------ the certificate, in DER

    /** Certificate ::= SEQUENCE { tbsCertificate, signatureAlgorithm, signatureValue }, RFC 5280. */
    public static byte[] build(KeyPair kp, String commonName, List<String> ips, List<String> dnsNames, Date notBefore, Date notAfter, BigInteger serial) throws Exception {
        byte[] sha256WithRsa = seq(oid("1.2.840.113549.1.1.11"), nul());
        byte[] name = seq(set(seq(oid("2.5.4.3"), utf8(commonName))));

        ByteArrayOutputStream names = new ByteArrayOutputStream();
        for (String dns : dnsNames) names.write(tag(0x82, dns.getBytes(UTF8)));            // [2] dNSName
        for (String ip : ips) names.write(tag(0x87, ipv4Bytes(ip)));                        // [7] iPAddress
        byte[] extensions = seq(
                seq(oid("2.5.29.19"), bool(true), octet(seq())),                            // basicConstraints: not a CA, critical
                seq(oid("2.5.29.15"), bool(true), octet(bitstring(new byte[]{(byte) 0xA0}, 5))), // keyUsage: digitalSignature, keyEncipherment
                seq(oid("2.5.29.37"), octet(seq(oid("1.3.6.1.5.5.7.3.1")))),                // extKeyUsage: serverAuth
                seq(oid("2.5.29.17"), octet(seq(names.toByteArray()))));                   // subjectAltName

        byte[] tbs = seq(
                tag(0xA0, integer(BigInteger.valueOf(2))),                                  // [0] version v3
                integer(serial),
                sha256WithRsa,
                name,                                                                       // issuer = subject: self-signed
                seq(time(notBefore), time(notAfter)),
                name,
                kp.getPublic().getEncoded(),                                                // SubjectPublicKeyInfo, already DER
                tag(0xA3, extensions));                                                     // [3] extensions

        Signature s = Signature.getInstance("SHA256withRSA");
        s.initSign(kp.getPrivate());
        s.update(tbs);
        byte[] signature = s.sign();
        return seq(tbs, sha256WithRsa, bitstring(signature, 0));
    }

    private static byte[] time(Date d) {
        Calendar c = Calendar.getInstance(TimeZone.getTimeZone("UTC"));
        c.setTime(d);
        boolean generalized = c.get(Calendar.YEAR) >= 2050;
        SimpleDateFormat f = new SimpleDateFormat(generalized ? "yyyyMMddHHmmss'Z'" : "yyMMddHHmmss'Z'", Locale.US);
        f.setTimeZone(TimeZone.getTimeZone("UTC"));
        return tag(generalized ? 0x18 : 0x17, f.format(d).getBytes(UTF8));
    }

    public static byte[] seq(byte[]... parts) { return tag(0x30, concat(parts)); }
    public static byte[] set(byte[]... parts) { return tag(0x31, concat(parts)); }
    public static byte[] octet(byte[] body) { return tag(0x04, body); }
    public static byte[] utf8(String s) { return tag(0x0C, s.getBytes(UTF8)); }
    public static byte[] nul() { return new byte[]{0x05, 0x00}; }
    public static byte[] bool(boolean v) { return new byte[]{0x01, 0x01, (byte) (v ? 0xFF : 0x00)}; }

    public static byte[] integer(BigInteger v) { return tag(0x02, v.toByteArray()); }

    public static byte[] bitstring(byte[] bits, int unusedBits) {
        byte[] body = new byte[bits.length + 1];
        body[0] = (byte) unusedBits;
        System.arraycopy(bits, 0, body, 1, bits.length);
        return tag(0x03, body);
    }

    /** OBJECT IDENTIFIER: first two arcs packed, the rest base-128 with continuation bits. */
    public static byte[] oid(String dotted) {
        String[] arcs = dotted.split("\\.");
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        out.write(Integer.parseInt(arcs[0]) * 40 + Integer.parseInt(arcs[1]));
        for (int i = 2; i < arcs.length; i++) {
            long v = Long.parseLong(arcs[i]);
            byte[] tmp = new byte[10];
            int n = 0;
            do {
                tmp[n++] = (byte) (v & 0x7F);
                v >>= 7;
            } while (v > 0);
            for (int j = n - 1; j >= 0; j--) out.write(tmp[j] | (j > 0 ? 0x80 : 0));
        }
        return tag(0x06, out.toByteArray());
    }

    public static byte[] tag(int tagByte, byte[] body) {
        byte[] len = length(body.length);
        byte[] out = new byte[1 + len.length + body.length];
        out[0] = (byte) tagByte;
        System.arraycopy(len, 0, out, 1, len.length);
        System.arraycopy(body, 0, out, 1 + len.length, body.length);
        return out;
    }

    public static byte[] length(int n) {
        if (n < 0x80) return new byte[]{(byte) n};
        if (n < 0x100) return new byte[]{(byte) 0x81, (byte) n};
        if (n < 0x10000) return new byte[]{(byte) 0x82, (byte) (n >> 8), (byte) n};
        return new byte[]{(byte) 0x83, (byte) (n >> 16), (byte) (n >> 8), (byte) n};
    }

    private static byte[] concat(byte[]... parts) {
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        for (byte[] p : parts) out.write(p, 0, p.length);
        return out.toByteArray();
    }

    static boolean isIpv4(String s) {
        String[] p = s.split("\\.");
        if (p.length != 4) return false;
        try {
            for (String x : p) { int v = Integer.parseInt(x); if (v < 0 || v > 255) return false; }
            return true;
        } catch (NumberFormatException e) {
            return false;
        }
    }

    private static byte[] ipv4Bytes(String ip) {
        String[] p = ip.split("\\.");
        return new byte[]{(byte) Integer.parseInt(p[0]), (byte) Integer.parseInt(p[1]), (byte) Integer.parseInt(p[2]), (byte) Integer.parseInt(p[3])};
    }

    private static String join(List<String> items) {
        StringBuilder sb = new StringBuilder();
        for (String s : items) { if (sb.length() > 0) sb.append(','); sb.append(s); }
        return sb.toString();
    }

    private static byte[] read(File f) throws IOException {
        InputStream in = new FileInputStream(f);
        try {
            ByteArrayOutputStream out = new ByteArrayOutputStream();
            byte[] buf = new byte[4096];
            int n;
            while ((n = in.read(buf)) > 0) out.write(buf, 0, n);
            return out.toByteArray();
        } finally {
            in.close();
        }
    }

    private static void write(File f, byte[] data) throws IOException {
        FileOutputStream out = new FileOutputStream(f);
        try {
            out.write(data);
        } finally {
            out.close();
        }
    }
}
