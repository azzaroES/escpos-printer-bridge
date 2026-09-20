package android.util;

/** Desktop stand-in for Android's logcat so the server classes can be exercised on a plain JVM. */
public final class Log {
    private Log() {
    }

    public static int i(String tag, String msg) {
        return 0;
    }

    public static int w(String tag, String msg) {
        return 0;
    }

    public static int e(String tag, String msg, Throwable t) {
        return 0;
    }

    public static int e(String tag, String msg) {
        return 0;
    }

    public static int d(String tag, String msg) {
        return 0;
    }
}
