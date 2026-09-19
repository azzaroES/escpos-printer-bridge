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
 * A record of every job the phone handled: when, from which device, how it arrived, how big, whether it printed,
 * and a readable extract of the text that reached the printer.
 *
 * Mirrors the Windows PrintHistory so both builds answer the same question the same way. Rows are appended to a
 * daily CSV next to the log file, which can be opened on the phone or pulled over adb.
 */
public final class PrintHistory {

    public static final class Record {
        public Date time;
        public String source;
        public String printer;
        public String path;
        public long bytes;
        public String status;
        /** Readable text, flattened to one line for the CSV. */
        public String preview;
        /** The ticket as it would look on paper, line by line, with markers for images, barcodes, QR codes and the cut. */
        public String ticket;

        public boolean failed() {
            return status == null || !status.equalsIgnoreCase("Printed");
        }

        public String timeText() {
            return new SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.US).format(time);
        }
    }

    public interface Listener {
        void onRecord(Record record);
    }

    private static final int MAX_IN_MEMORY = 500;
    private static final int PREVIEW_CHARS = 400;

    private static final List<Record> RECORDS = new ArrayList<>();
    private static final List<Listener> LISTENERS = new ArrayList<>();

    private PrintHistory() {
    }

    public static List<Record> snapshot() {
        synchronized (RECORDS) {
            return new ArrayList<>(RECORDS);
        }
    }

    public static int count() {
        synchronized (RECORDS) {
            return RECORDS.size();
        }
    }

    public static void clear() {
        synchronized (RECORDS) {
            RECORDS.clear();
        }
    }

    public static void addListener(Listener l) {
        synchronized (RECORDS) {
            LISTENERS.add(l);
        }
    }

    public static void removeListener(Listener l) {
        synchronized (RECORDS) {
            LISTENERS.remove(l);
        }
    }

    public static Record add(String source, String printer, String path, byte[] data, int length, String status) {
        Record r = new Record();
        r.time = new Date();
        r.source = source == null ? "?" : source;
        r.printer = printer == null ? "?" : printer;
        r.path = path == null ? "" : path;
        r.bytes = length;
        r.status = status == null ? "Printed" : status;
        r.preview = extractText(data, length, PREVIEW_CHARS);
        try {
            r.ticket = TicketText.render(data, length);
        } catch (Throwable t) {
            r.ticket = "";
        }

        List<Listener> copy;
        synchronized (RECORDS) {
            RECORDS.add(r);
            while (RECORDS.size() > MAX_IN_MEMORY) RECORDS.remove(0);
            copy = new ArrayList<>(LISTENERS);
        }

        appendCsv(r);

        for (Listener l : copy) {
            try {
                l.onRecord(r);
            } catch (Throwable ignored) {
            }
        }
        return r;
    }

    /**
     * Pulls the human-readable part out of an ESC/POS stream. Control bytes and escape sequences are dropped,
     * so what remains is roughly the text printed on the receipt.
     */
    public static String extractText(byte[] data, int length, int maxChars) {
        if (data == null || length <= 0) return "";
        StringBuilder sb = new StringBuilder();
        boolean lastWasSpace = false;

        for (int i = 0; i < length && sb.length() < maxChars; i++) {
            int b = data[i] & 0xFF;

            if (b == 0x1B || b == 0x1D) {
                i += skipCommand(data, length, i);
                continue;
            }

            if (b == 0x0A || b == 0x0D) {
                if (!lastWasSpace) {
                    sb.append(' ');
                    lastWasSpace = true;
                }
                continue;
            }

            if (b >= 0x20 && b <= 0x7E) {
                char c = (char) b;
                if (c == ' ') {
                    if (lastWasSpace) continue;
                    lastWasSpace = true;
                } else {
                    lastWasSpace = false;
                }
                sb.append(c);
            } else if (b >= 0xA0) {
                sb.append((char) b);
                lastWasSpace = false;
            }
        }

        String text = sb.toString().trim();
        if (text.length() >= maxChars) text += "...";
        return text;
    }

    /** Extra bytes to skip for a command starting at i. Conservative, not exhaustive. */
    private static int skipCommand(byte[] data, int length, int i) {
        int lead = data[i] & 0xFF;
        int next = (i + 1 < length) ? (data[i + 1] & 0xFF) : 0;

        if (lead == 0x1B) {
            switch (next) {
                case 0x40:
                    return 1;
                case 0x61: case 0x4D: case 0x2D:
                case 0x45: case 0x47: case 0x7B:
                case 0x33: case 0x4A: case 0x64:
                case 0x74: case 0x56: case 0x21:
                    return 2;
                case 0x70:
                    return 4;
                default:
                    return 1;
            }
        }

        switch (next) {
            case 0x21: case 0x42: case 0x62:
            case 0x48: case 0x66: case 0x77:
            case 0x68: case 0x72: case 0x49:
            case 0x61: case 0x6B:
                return 2;
            case 0x56:
                return 3;
            default:
                return 1;
        }
    }

    private static synchronized void appendCsv(Record r) {
        File dir = Log.getDirectory();
        if (dir == null) return;
        try {
            String day = new SimpleDateFormat("yyyyMMdd", Locale.US).format(r.time);
            File file = new File(dir, "prints-" + day + ".csv");
            boolean isNew = !file.exists();
            Writer writer = new OutputStreamWriter(new FileOutputStream(file, true), "UTF-8");
            try {
                if (isNew) writer.write("Time,Source,Printer,Path,Bytes,Status,Text\n");
                writer.write(csv(r.timeText()) + "," + csv(r.source) + "," + csv(r.printer) + ","
                        + csv(r.path) + "," + r.bytes + "," + csv(r.status) + "," + csv(r.preview) + "\n");
            } finally {
                writer.close();
            }
        } catch (Throwable ignored) {
        }
    }

    private static String csv(String value) {
        if (value == null) return "\"\"";
        return "\"" + value.replace("\"", "\"\"") + "\"";
    }
}
