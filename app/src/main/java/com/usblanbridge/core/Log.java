package com.usblanbridge.core;

import java.io.File;
import java.io.FileOutputStream;
import java.io.OutputStreamWriter;
import java.io.Writer;
import java.text.SimpleDateFormat;
import java.util.ArrayList;
import java.util.Date;
import java.util.List;
import java.util.Locale;

/**
 * Process-wide log.
 *
 * Lines go to three places: the in-app view, logcat, and a file. The file matters because some phones,
 * MediaTek-based ones in particular, suppress application logs from logcat entirely, which leaves no way to
 * find out what happened. The file is written to the app's external files directory, so it can be read on the
 * phone and pulled over adb without root.
 */
public final class Log {

    public interface Listener {
        void onLine(String line);
    }

    private static final String TAG = "UsbLanBridge";
    private static final int MAX_LINES = 500;
    private static final long MAX_FILE_BYTES = 2 * 1024 * 1024;

    private static final List<String> LINES = new ArrayList<>();
    private static final List<Listener> LISTENERS = new ArrayList<>();
    private static final SimpleDateFormat CLOCK = new SimpleDateFormat("HH:mm:ss", Locale.US);
    private static final SimpleDateFormat STAMP = new SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.US);

    private static File directory;

    private Log() {
    }

    /** Where the log file and the print history are written. Call once at start-up. */
    public static synchronized void setDirectory(File dir) {
        directory = dir;
        if (dir != null) {
            try {
                if (!dir.exists()) dir.mkdirs();
            } catch (Throwable ignored) {
            }
        }
    }

    public static synchronized File getDirectory() {
        return directory;
    }

    public static File getLogFile() {
        File dir = getDirectory();
        return dir == null ? null : new File(dir, "bridge.log");
    }

    public static void i(String message) {
        add("INFO ", message);
        android.util.Log.i(TAG, message);
    }

    public static void w(String message) {
        add("WARN ", message);
        android.util.Log.w(TAG, message);
    }

    public static void e(String message, Throwable t) {
        String text = message + (t == null ? "" : ": " + t);
        add("ERROR", text);
        android.util.Log.e(TAG, text, t);
    }

    private static void add(String level, String message) {
        Date now = new Date();
        String line = CLOCK.format(now) + "  " + level + "  " + message;
        List<Listener> copy;
        synchronized (LINES) {
            LINES.add(line);
            while (LINES.size() > MAX_LINES) LINES.remove(0);
            copy = new ArrayList<>(LISTENERS);
        }

        appendToFile(STAMP.format(now) + "  " + level + "  " + message);

        for (Listener l : copy) {
            try {
                l.onLine(line);
            } catch (Throwable ignored) {
            }
        }
    }

    private static synchronized void appendToFile(String line) {
        File file = getLogFile();
        if (file == null) return;
        try {
            if (file.exists() && file.length() > MAX_FILE_BYTES) {
                File old = new File(file.getParentFile(), "bridge.log.1");
                if (old.exists()) old.delete();
                file.renameTo(old);
            }
            Writer writer = new OutputStreamWriter(new FileOutputStream(file, true), "UTF-8");
            try {
                writer.write(line);
                writer.write("\n");
            } finally {
                writer.close();
            }
        } catch (Throwable ignored) {
            // logging must never break printing
        }
    }

    public static String snapshot() {
        synchronized (LINES) {
            StringBuilder sb = new StringBuilder();
            for (String s : LINES) sb.append(s).append('\n');
            return sb.toString();
        }
    }

    public static void addListener(Listener l) {
        synchronized (LINES) {
            LISTENERS.add(l);
        }
    }

    public static void removeListener(Listener l) {
        synchronized (LINES) {
            LISTENERS.remove(l);
        }
    }
}
