package com.usblanbridge.core;

import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.OutputStreamWriter;
import java.io.Writer;
import java.text.SimpleDateFormat;
import java.util.ArrayList;
import java.util.Date;
import java.util.List;
import java.util.Locale;

/**
 * One CSV row every few seconds with everything the Device card shows, a full timestamp, and the events that
 * happened since the previous row, so a graph can be rebuilt for any moment later. A file per day, kept for
 * two weeks, in the app's files directory next to the log.
 *
 * Deliberately free of Android imports so it can be verified on a desktop JVM.
 */
public final class TelemetryLog {

    public static final int KEEP_DAYS = 14;
    public static final String HEADER = "Time,CpuClockPct,RamPct,RamUsedMB,WifiSignalPct,WifiLinkMbps,RxKBps,TxKBps,UsbPrinter,BtPrinter,BatteryTempC,CpuTempC,ThermalStatus,BatteryPct,BatteryState,BatteryMa,BatteryV,Throttled,Events";

    private final File dir;
    private final List<String> pendingEvents = new ArrayList<>();
    private String currentDay;

    public TelemetryLog(File dir) {
        this.dir = dir;
    }

    public File fileFor(Date day) {
        return new File(dir, "telemetry-" + new SimpleDateFormat("yyyyMMdd", Locale.US).format(day) + ".csv");
    }

    /** Remembers an event for the next row. */
    public synchronized void noteEvent(long time, String text) {
        pendingEvents.add(new SimpleDateFormat("HH:mm:ss", Locale.US).format(new Date(time)) + " " + text);
    }

    /**
     * Appends one row. Values are given as already formatted strings, empty for "no reading"; the events noted
     * since the previous row are joined into the last column.
     */
    public synchronized void write(Date now, String... columns) {
        if (dir == null) return;
        try {
            if (!dir.exists()) dir.mkdirs();
            File file = fileFor(now);
            String day = new SimpleDateFormat("yyyyMMdd", Locale.US).format(now);
            boolean isNew = !file.exists();
            Writer w = new OutputStreamWriter(new FileOutputStream(file, true), "UTF-8");
            try {
                if (isNew) {
                    w.write(HEADER);
                    w.write("\n");
                }
                StringBuilder sb = new StringBuilder(new SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.US).format(now));
                for (String c : columns) {
                    sb.append(',');
                    sb.append(c == null ? "" : c);
                }
                sb.append(",\"");
                for (int i = 0; i < pendingEvents.size(); i++) {
                    if (i > 0) sb.append(" | ");
                    sb.append(pendingEvents.get(i).replace("\"", "\"\""));
                }
                sb.append("\"\n");
                w.write(sb.toString());
            } finally {
                w.close();
            }
            pendingEvents.clear();
            if (!day.equals(currentDay)) {
                currentDay = day;
                cleanup();
            }
        } catch (Throwable ignored) {
            // telemetry must never break printing
        }
    }

    /** Every telemetry file, oldest first. */
    public List<File> files() {
        List<File> out = new ArrayList<>();
        if (dir == null || !dir.exists()) return out;
        File[] all = dir.listFiles();
        if (all == null) return out;
        for (File f : all) if (f.getName().startsWith("telemetry-") && f.getName().endsWith(".csv")) out.add(f);
        java.util.Collections.sort(out);
        return out;
    }

    public long bytesToday() {
        File f = fileFor(new Date());
        return f.exists() ? f.length() : 0;
    }

    /** Deletes every telemetry file. Returns how many. */
    public synchronized int clear() {
        int n = 0;
        for (File f : files()) if (f.delete()) n++;
        pendingEvents.clear();
        return n;
    }

    private void cleanup() {
        long cutoff = System.currentTimeMillis() - KEEP_DAYS * 86400000L;
        for (File f : files()) if (f.lastModified() < cutoff) f.delete();
    }
}
