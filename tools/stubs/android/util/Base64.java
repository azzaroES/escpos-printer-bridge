package android.util;

/** Desktop stand-in for android.util.Base64, backed by java.util.Base64. */
public final class Base64 {
    public static final int DEFAULT = 0;
    public static final int NO_WRAP = 2;
    public static final int NO_PADDING = 1;
    public static final int URL_SAFE = 8;

    private Base64() {
    }

    public static byte[] decode(String s, int flags) {
        String clean = s.replaceAll("\\s", "");
        return (flags & URL_SAFE) != 0 ? java.util.Base64.getUrlDecoder().decode(clean) : java.util.Base64.getMimeDecoder().decode(clean);
    }

    public static byte[] decode(byte[] b, int flags) {
        return decode(new String(b, java.nio.charset.StandardCharsets.US_ASCII), flags);
    }

    public static String encodeToString(byte[] b, int flags) {
        return java.util.Base64.getEncoder().encodeToString(b);
    }
}
