package com.usblanbridge.core;

/**
 * The device id an ePOS-Print client names in its URL (the devid query parameter, or the id given to the SDK's
 * createDevice) and the links built from it. A real Epson answers "local_printer"; this bridge answers whatever
 * id the user set, so a POS app configured for a named printer keeps working.
 *
 * Deliberately free of Android imports so it can be verified on a desktop JVM.
 */
public final class EposDeviceId {

    public static final String DEFAULT = "local_printer";
    public static final int MAX_LENGTH = 32;
    public static final String SERVICE_PATH = "/cgi-bin/epos/service.cgi";

    private EposDeviceId() {
    }

    /** Keeps only letters, digits, '_', '-' and '.', at most 32 of them; anything empty becomes the default. */
    public static String sanitize(String id) {
        if (id == null) return DEFAULT;
        StringBuilder sb = new StringBuilder();
        for (char c : id.trim().toCharArray()) {
            if (Character.isLetterOrDigit(c) || c == '_' || c == '-' || c == '.') sb.append(c);
            if (sb.length() == MAX_LENGTH) break;
        }
        return sb.length() == 0 ? DEFAULT : sb.toString();
    }

    /** The value of a query parameter in a request path, decoded, or null when absent. */
    public static String queryValue(String path, String name) {
        if (path == null || name == null) return null;
        int q = path.indexOf('?');
        if (q < 0) return null;
        for (String pair : path.substring(q + 1).split("&")) {
            int eq = pair.indexOf('=');
            String k = eq < 0 ? pair : pair.substring(0, eq);
            if (!decode(k).equalsIgnoreCase(name)) continue;
            return eq < 0 ? "" : decode(pair.substring(eq + 1));
        }
        return null;
    }

    /** The full ePOS-Print URL a client pastes: scheme, host, port, path, devid and the usual timeout. */
    public static String serviceUrl(boolean https, String host, int port, String deviceId) {
        return base(https, host, port) + SERVICE_PATH + "?devid=" + sanitize(deviceId) + "&timeout=10000";
    }

    /** The page a client device opens once to trust the bridge's certificate. */
    public static String certificateUrl(String host, int httpsPort) {
        return base(true, host, httpsPort) + "/cert";
    }

    private static String base(boolean https, String host, int port) {
        boolean defaultPort = https ? port == 443 : port == 80;
        return (https ? "https://" : "http://") + host + (defaultPort ? "" : ":" + port);
    }

    private static String decode(String s) {
        try {
            return java.net.URLDecoder.decode(s, "UTF-8");
        } catch (Exception e) {
            return s;
        }
    }
}
