package com.usblanbridge.core;

import java.io.ByteArrayOutputStream;

/**
 * Renders an ESC/POS job as the ticket would look on paper: the text line by line, centred and right-aligned
 * lines padded to the paper width, blank lines for feeds, and a marker for everything that is not text:
 * [image 384x200], [barcode: 12345], [QR data: https://...], [drawer opened], and a dashed cut line.
 * Image and symbol payloads are skipped by their declared length, so pixel bytes never show up as garbage.
 *
 * Mirrors the Windows EscPosTicketText. Deliberately free of Android imports so it can be verified on a desktop JVM.
 */
public final class TicketText {

    /** Characters per line for the alignment padding: font A on an 80 mm roll. */
    public static final int COLUMNS = 48;
    private static final String CUT_LINE = "- - - - - - - - - -  cut  - - - - - - - - - -";
    private static final int MAX_HELD = 1024;
    private static final int ESC = EscPosCommand.ESC;
    private static final int FS = EscPosCommand.FS;
    private static final int GS = EscPosCommand.GS;

    private final StringBuilder out = new StringBuilder();
    private final StringBuilder line = new StringBuilder();
    private final ByteArrayOutputStream held = new ByteArrayOutputStream(16);
    private final ByteArrayOutputStream collect = new ByteArrayOutputStream();
    private final int maxChars;
    private long skip;
    private byte[] collectingHeader;   // header of a barcode / symbol whose data is arriving as payload
    private int align;
    private boolean truncated;

    private TicketText(int maxChars) {
        this.maxChars = maxChars;
    }

    public static String render(byte[] data, int length) {
        return render(data, length, 8000);
    }

    public static String render(byte[] data, int length, int maxChars) {
        if (data == null || length <= 0) return "";
        TicketText t = new TicketText(maxChars);
        for (int i = 0; i < length; i++) t.accept(data[i] & 0xFF);
        t.flushCollection();
        if (t.line.length() > 0) t.endLine();
        String s = t.out.toString();
        int end = s.length();
        while (end > 0 && (s.charAt(end - 1) == '\n' || s.charAt(end - 1) == ' ')) end--;
        return s.substring(0, end);
    }

    private void accept(int b) {
        if (skip > 0) {
            skip--;
            if (collectingHeader != null && collect.size() < 2048) collect.write(b);
            return;
        }
        if (held.size() == 0) {
            if (b == ESC || b == GS || b == FS) {
                held.write(b);
                return;
            }
            text(b);
            return;
        }
        held.write(b);
        byte[] h = held.toByteArray();
        EscPosCommand c = EscPosCommand.classify(h);
        if (c == null) {
            if (h.length >= MAX_HELD) held.reset();     // not a command we know: drop it from the rendering
            return;
        }
        held.reset();
        command(h, c);
        skip = c.payload;
    }

    private void text(int b) {
        flushCollection();
        if (b == 0x0A) {
            endLine();
        } else if (b == 0x09) {
            line.append("    ");
        } else if (b >= 0x20 && b <= 0x7E) {
            line.append((char) b);
        } else if (b >= 0xA0) {
            line.append((char) b);          // Latin-1 accented characters, as the printer's code page mostly is
        } else if (b >= 0x80) {
            line.append('?');
        }
        // other control bytes print nothing
    }

    private void command(byte[] h, EscPosCommand c) {
        flushCollection();
        int lead = h[0] & 0xFF;
        int n = h[1] & 0xFF;
        if (c.cut) {
            if (line.length() > 0) endLine();
            appendLine(CUT_LINE);
            return;
        }
        if (lead == ESC) {
            switch (n) {
                case 0x40:                                              // ESC @
                    align = 0;
                    break;
                case 0x61:                                              // ESC a n: alignment
                    if (h.length > 2) {
                        int a = h[2] & 0xFF;
                        align = (a == 1 || a == 49) ? 1 : (a == 2 || a == 50) ? 2 : 0;
                    }
                    break;
                case 0x64: {                                            // ESC d n: feed n lines
                    if (line.length() > 0) endLine();
                    int lines = Math.min(h[2] & 0xFF, 3);
                    for (int i = 0; i < lines; i++) appendLine("");
                    break;
                }
                case 0x4A: case 0x4B: case 0x65:                        // ESC J / K / e: one feed
                    if (line.length() > 0) endLine();
                    appendLine("");
                    break;
                case 0x70:                                              // ESC p: drawer
                    if (line.length() > 0) endLine();
                    appendLine("[drawer opened]");
                    break;
                case 0x2A:                                              // ESC *: bit image
                    if (line.length() > 0) endLine();
                    appendLine("[image]");
                    break;
                default:
                    break;
            }
            return;
        }
        if (lead == GS) {
            switch (n) {
                case 0x76: {                                            // GS v 0: raster image
                    if (h.length >= 8) {
                        if (line.length() > 0) endLine();
                        int widthBytes = (h[4] & 0xFF) | ((h[5] & 0xFF) << 8);
                        int rows = (h[6] & 0xFF) | ((h[7] & 0xFF) << 8);
                        appendLine("[image " + (widthBytes * 8) + "x" + rows + "]");
                    }
                    break;
                }
                case 0x38: case 0x2F:                                   // GS 8 L graphics, GS / print bit image
                    if (line.length() > 0) endLine();
                    appendLine("[image]");
                    break;
                case 0x28:
                    if (h.length > 2 && (h[2] & 0xFF) == 0x6B) {        // GS ( k: 2D symbol, data in the payload
                        if (line.length() > 0) endLine();
                        collectingHeader = h;
                    } else if (h.length > 2 && (h[2] & 0xFF) == 0x4C) { // GS ( L graphics
                        if (line.length() > 0) endLine();
                        appendLine("[image]");
                    }
                    break;
                case 0x6B: {                                            // GS k: barcode
                    if (line.length() > 0) endLine();
                    int m = h[2] & 0xFF;
                    if (m < 65) {
                        StringBuilder sb = new StringBuilder();
                        for (int i = 3; i < h.length - 1; i++) sb.append(printable(h[i] & 0xFF));
                        appendLine("[barcode: " + sb + "]");
                    } else {
                        collectingHeader = h;
                    }
                    break;
                }
                default:
                    break;
            }
            return;
        }
        if (lead == FS && n == 0x70) {                                  // FS p: print stored image
            if (line.length() > 0) endLine();
            appendLine("[image]");
        }
    }

    private void flushCollection() {
        byte[] h = collectingHeader;
        if (h == null) return;
        collectingHeader = null;
        byte[] p = collect.toByteArray();
        collect.reset();
        if ((h[1] & 0xFF) == 0x6B) {                                    // barcode with length-prefixed data
            StringBuilder sb = new StringBuilder();
            for (byte b : p) sb.append(printable(b & 0xFF));
            appendLine("[barcode: " + sb + "]");
            return;
        }
        // GS ( k payload: cn fn [m] d...  cn 49 = QR Code, 48 = PDF417
        if (p.length < 2) return;
        int cn = p[0] & 0xFF;
        int fn = p[1] & 0xFF;
        String kind = cn == 49 ? "QR" : cn == 48 ? "PDF417" : "2D symbol";
        if (fn == 0x50) {                                               // fn 80: store the data
            StringBuilder sb = new StringBuilder();
            for (int i = 3; i < p.length; i++) sb.append(printable(p[i] & 0xFF));
            if (p.length >= 2048) sb.append("...");
            appendLine("[" + kind + " data: " + sb + "]");
        } else if (fn == 0x51) {                                        // fn 81: print it
            appendLine("[" + kind + " code]");
        }
    }

    private static char printable(int b) {
        return (b >= 0x20 && b <= 0x7E) ? (char) b : b >= 0xA0 ? (char) b : '.';
    }

    private void endLine() {
        String text = line.toString();
        int end = text.length();
        while (end > 0 && text.charAt(end - 1) == ' ') end--;
        text = text.substring(0, end);
        line.setLength(0);
        if (text.length() > 0 && text.length() < COLUMNS) {
            if (align == 1) text = spaces((COLUMNS - text.length()) / 2) + text;
            else if (align == 2) text = spaces(COLUMNS - text.length()) + text;
        }
        appendLine(text);
    }

    private static String spaces(int n) {
        StringBuilder sb = new StringBuilder(n);
        for (int i = 0; i < n; i++) sb.append(' ');
        return sb.toString();
    }

    private void appendLine(String text) {
        if (truncated) return;
        if (out.length() + text.length() > maxChars) {
            truncated = true;
            out.append("[... ticket text truncated]\n");
            return;
        }
        out.append(text).append('\n');
    }
}
