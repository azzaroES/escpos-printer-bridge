package com.usblanbridge.core;

import java.nio.charset.Charset;
import java.security.KeyFactory;
import java.security.PublicKey;
import java.security.Signature;
import java.security.spec.X509EncodedKeySpec;
import java.util.ArrayList;
import java.util.List;

/**
 * Licence keys that switch the printed footer off, bound to at most two devices.
 *
 * A key is "DSPRO." + base64url(payload) + "." + base64url(signature). The payload is plain text,
 * "2|licensee|date|device1,device2", and the signature is ECDSA P-256 over it, made with a private key that is
 * not in this repository. The matching public key is embedded below, so the app can check a key offline, on a
 * till that never sees the internet, and nobody can mint keys without the private half.
 *
 * Binding is by device id rather than MAC address because Android stopped letting apps read the MAC in
 * version 6 (Wi-Fi) and 10 (Bluetooth); every app sees 02:00:00:00:00:00. The device id the app shows serves
 * the same purpose: the key works on the devices it names and nowhere else.
 *
 * Being open source, anyone can of course rebuild the app without the footer. The key exists for people who use
 * the published APK and would rather pay than compile.
 *
 * Deliberately free of Android imports: the same class verifies keys in the app, in the desktop self-test and
 * in the key generator.
 */
public final class License {

    public static final String PREFIX = "DSPRO.";
    public static final int MAX_DEVICES = 2;
    private static final String VERSION = "2";

    /** X.509 SubjectPublicKeyInfo, base64. Generate your own pair with tools/run-keygen.cmd and paste it here. */
    public static final String PUBLIC_KEY =
            "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEMZxEEAT82+mEb1HIH4L4GEa+HG/u6+ulchz/l6RhZE2lyQAsPKSYWuq/9Xh/uJL87HwQ6Q84RnVHsdojBj6DNw==";

    private static final Charset UTF8 = Charset.forName("UTF-8");

    /** What a genuine key says. */
    public static final class Info {
        public final String licensee;
        public final String issued;
        public final String[] devices;

        Info(String licensee, String issued, String[] devices) {
            this.licensee = licensee;
            this.issued = issued;
            this.devices = devices;
        }

        /** True when the key names this device. */
        public boolean allows(String deviceId) {
            String wanted = normalizeDevice(deviceId);
            if (wanted.isEmpty()) return false;
            for (String d : devices) {
                if (d.equals(wanted)) return true;
            }
            return false;
        }
    }

    private License() {
    }

    /** The licence details when the key is genuine and names this device, otherwise null. Never throws. */
    public static Info check(String key, String deviceId) {
        return check(key, PUBLIC_KEY, deviceId);
    }

    public static Info check(String key, String publicKeyBase64, String deviceId) {
        Info info = inspect(key, publicKeyBase64);
        return info != null && info.allows(deviceId) ? info : null;
    }

    /** Verifies the signature and reads the payload without regard to the device, so the app can say why a key was refused. */
    public static Info inspect(String key) {
        return inspect(key, PUBLIC_KEY);
    }

    public static Info inspect(String key, String publicKeyBase64) {
        if (key == null || publicKeyBase64 == null || publicKeyBase64.isEmpty()) return null;
        String k = key.trim();
        if (!k.startsWith(PREFIX)) return null;
        String[] parts = k.substring(PREFIX.length()).split("\\.");
        if (parts.length != 2) return null;
        byte[] payload = decode(parts[0]);
        byte[] signature = decode(parts[1]);
        if (payload == null || signature == null || payload.length == 0 || signature.length == 0) return null;

        try {
            PublicKey pk = KeyFactory.getInstance("EC").generatePublic(new X509EncodedKeySpec(decode(publicKeyBase64)));
            Signature s = Signature.getInstance("SHA256withECDSA");
            s.initVerify(pk);
            s.update(payload);
            if (!s.verify(signature)) return null;
        } catch (Throwable t) {
            return null;
        }

        String[] fields = new String(payload, UTF8).split("\\|", -1);
        if (fields.length < 4 || !VERSION.equals(fields[0]) || fields[1].isEmpty()) return null;
        List<String> devices = new ArrayList<>();
        for (String d : fields[3].split(",")) {
            String n = normalizeDevice(d);
            if (!n.isEmpty()) devices.add(n);
        }
        if (devices.isEmpty() || devices.size() > MAX_DEVICES) return null;
        return new Info(fields[1], fields[2], devices.toArray(new String[0]));
    }

    /** The text that gets signed. Kept here so the generator and the checker cannot drift apart. */
    public static byte[] payloadFor(String licensee, String issuedDate, String[] deviceIds) {
        if (deviceIds == null || deviceIds.length == 0) throw new IllegalArgumentException("A key needs at least one device id.");
        if (deviceIds.length > MAX_DEVICES) throw new IllegalArgumentException("A key covers at most " + MAX_DEVICES + " devices.");
        StringBuilder devices = new StringBuilder();
        for (String d : deviceIds) {
            String n = normalizeDevice(d);
            if (n.isEmpty()) throw new IllegalArgumentException("Not a device id: " + d);
            if (devices.length() > 0) devices.append(',');
            devices.append(n);
        }
        return (VERSION + "|" + licensee.replace("|", "/") + "|" + issuedDate + "|" + devices).getBytes(UTF8);
    }

    public static String format(byte[] payload, byte[] signature) {
        return PREFIX + encode(payload) + "." + encode(signature);
    }

    /** Device ids are compared as lower-case letters and digits only, so dashes and case from copying do not matter. */
    public static String normalizeDevice(String id) {
        if (id == null) return "";
        StringBuilder sb = new StringBuilder(id.length());
        for (int i = 0; i < id.length(); i++) {
            char c = Character.toLowerCase(id.charAt(i));
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.append(c);
        }
        return sb.toString();
    }

    // ------------------------------------------------------------------ base64url, hand-rolled because
    // java.util.Base64 only exists from Android 8 and this app runs on Android 5.

    private static final String ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

    public static String encode(byte[] data) {
        StringBuilder sb = new StringBuilder((data.length * 4 + 2) / 3);
        int i = 0;
        while (i + 3 <= data.length) {
            int v = ((data[i] & 0xFF) << 16) | ((data[i + 1] & 0xFF) << 8) | (data[i + 2] & 0xFF);
            sb.append(ALPHABET.charAt((v >> 18) & 63)).append(ALPHABET.charAt((v >> 12) & 63))
              .append(ALPHABET.charAt((v >> 6) & 63)).append(ALPHABET.charAt(v & 63));
            i += 3;
        }
        int rem = data.length - i;
        if (rem == 1) {
            int v = (data[i] & 0xFF) << 16;
            sb.append(ALPHABET.charAt((v >> 18) & 63)).append(ALPHABET.charAt((v >> 12) & 63));
        } else if (rem == 2) {
            int v = ((data[i] & 0xFF) << 16) | ((data[i + 1] & 0xFF) << 8);
            sb.append(ALPHABET.charAt((v >> 18) & 63)).append(ALPHABET.charAt((v >> 12) & 63))
              .append(ALPHABET.charAt((v >> 6) & 63));
        }
        return sb.toString();
    }

    /** Accepts both the url-safe and the standard alphabet, with or without padding. Null when malformed. */
    public static byte[] decode(String text) {
        if (text == null) return null;
        java.io.ByteArrayOutputStream out = new java.io.ByteArrayOutputStream(text.length() * 3 / 4 + 3);
        int buffer = 0;
        int bits = 0;
        int count = 0;
        for (int i = 0; i < text.length(); i++) {
            char c = text.charAt(i);
            if (c == '=' || c == '\r' || c == '\n' || c == ' ') continue;
            int v;
            if (c == '+') v = 62;
            else if (c == '/') v = 63;
            else v = ALPHABET.indexOf(c);
            if (v < 0) return null;
            buffer = (buffer << 6) | v;
            bits += 6;
            count++;
            if (bits >= 8) {
                bits -= 8;
                out.write((buffer >> bits) & 0xFF);
            }
        }
        if (count % 4 == 1) return null;
        return out.toByteArray();
    }
}
