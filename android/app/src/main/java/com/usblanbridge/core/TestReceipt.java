package com.usblanbridge.core;

import java.io.ByteArrayOutputStream;
import java.nio.charset.Charset;
import java.text.SimpleDateFormat;
import java.util.Date;
import java.util.Locale;

/** Builds a small ESC/POS test receipt in plain ASCII, so it prints legibly on any receipt printer. */
public final class TestReceipt {

    private static final Charset ASCII = Charset.forName("US-ASCII");

    private TestReceipt() {
    }

    public static byte[] build(String printerName, String endpoint) {
        ByteArrayOutputStream out = new ByteArrayOutputStream(512);
        raw(out, 0x1B, 0x40);          // ESC @ initialise
        raw(out, 0x1B, 0x61, 0x01);    // centre
        raw(out, 0x1B, 0x21, 0x30);    // double width and height
        text(out, "BRIDGE TEST\n");
        raw(out, 0x1B, 0x21, 0x00);
        text(out, "USB LAN Printer Bridge\n");
        raw(out, 0x1B, 0x61, 0x00);    // left
        text(out, "------------------------------\n");
        text(out, "Printer : " + trim(printerName, 20) + "\n");
        text(out, "Address : " + trim(endpoint, 20) + "\n");
        text(out, "Time    : " + new SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.US).format(new Date()) + "\n");
        text(out, "------------------------------\n");
        text(out, "If you can read this, the\nphone is sharing this printer\nover Wi-Fi.\n");
        text(out, "\n\n\n\n");
        raw(out, 0x1D, 0x56, 0x42, 0x00); // feed and partial cut, ignored if no cutter
        return out.toByteArray();
    }

    private static void raw(ByteArrayOutputStream out, int... bytes) {
        for (int b : bytes) out.write(b & 0xFF);
    }

    private static void text(ByteArrayOutputStream out, String s) {
        byte[] b = s.getBytes(ASCII);
        out.write(b, 0, b.length);
    }

    private static String trim(String s, int max) {
        if (s == null) return "";
        return s.length() <= max ? s : s.substring(0, max - 1) + "~";
    }
}
