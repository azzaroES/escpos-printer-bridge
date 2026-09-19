package com.usblanbridge.core;

import java.io.ByteArrayOutputStream;

/**
 * Inserts a footer into an ESC/POS stream, once per ticket, immediately before the cut.
 *
 * The stream is never buffered as a whole. Bytes pass through as they arrive and only an incomplete command is
 * held back, the same approach EscPosResponder takes, so this can sit on a live socket. Commands that carry a
 * payload, such as raster images, QR codes, barcodes and graphics, are skipped by their declared length,
 * because image data can legitimately contain the bytes of a cut command. Getting that wrong would stamp the
 * footer into the middle of a logo. The command shapes live in {@link EscPosCommand}, shared with CutFilter.
 *
 * A ticket with no printable content, such as a bare cash-drawer pulse or a status query, gets no footer:
 * nothing was printed, so there is nothing to append to. A job that prints but never cuts gets the footer at
 * the end. Input bytes are never dropped or altered, only added to, so a parsing gap can at worst miss a footer.
 *
 * Deliberately free of Android imports so it can be verified on a desktop JVM.
 */
public final class FooterInjector {

    private static final int ESC = EscPosCommand.ESC;
    private static final int FS = EscPosCommand.FS;
    private static final int GS = EscPosCommand.GS;
    /** A command longer than this is not one we know; release it as text rather than hold forever. */
    private static final int MAX_HELD = 1024;

    private final byte[] footer;
    private final ByteArrayOutputStream out = new ByteArrayOutputStream(4096);
    private final ByteArrayOutputStream held = new ByteArrayOutputStream(16);
    private long skip;          // payload bytes still to pass through untouched
    private boolean content;    // printable output seen since the last cut
    private int footers;

    public FooterInjector(byte[] footer) {
        this.footer = footer == null ? new byte[0] : footer;
    }

    /** The bytes that print a centred footer line and restore left alignment and the plain font. */
    public static byte[] footerFor(String text) {
        ByteArrayOutputStream b = new ByteArrayOutputStream(64);
        b.write(0x0A);
        b.write(0x1B); b.write(0x21); b.write(0x00);   // ESC ! 0: plain font, no bold, no double
        b.write(0x1D); b.write(0x21); b.write(0x00);   // GS ! 0: normal size
        b.write(0x1B); b.write(0x61); b.write(0x01);   // centre
        byte[] t = text.getBytes(java.nio.charset.Charset.forName("US-ASCII"));
        b.write(t, 0, t.length);
        b.write(0x0A);
        b.write(0x1B); b.write(0x61); b.write(0x00);   // left
        return b.toByteArray();
    }

    public void process(byte[] data, int offset, int length) {
        for (int i = 0; i < length; i++) accept(data[offset + i] & 0xFF);
    }

    /** Call once at the end of the job: releases held bytes and appends the footer if the ticket printed but never cut. */
    public void finish() {
        if (held.size() > 0) {
            byte[] h = held.toByteArray();
            held.reset();
            out.write(h, 0, h.length);
        }
        if (content && footer.length > 0) {
            out.write(footer, 0, footer.length);
            footers++;
        }
        content = false;
        skip = 0;
    }

    /** Output produced so far; clears the buffer. */
    public byte[] takeOutput() {
        byte[] b = out.toByteArray();
        out.reset();
        return b;
    }

    public int footersInserted() {
        return footers;
    }

    private void accept(int b) {
        if (skip > 0) {
            skip--;
            out.write(b);
            return;
        }
        if (held.size() == 0) {
            if (b == ESC || b == GS || b == FS) {
                held.write(b);
                return;
            }
            out.write(b);
            if (b == 0x0A || b >= 0x20) content = true;
            return;
        }

        held.write(b);
        byte[] h = held.toByteArray();
        EscPosCommand c = EscPosCommand.classify(h);
        if (c == null) {
            if (h.length >= MAX_HELD) {
                held.reset();
                out.write(h, 0, h.length);
                content = true;
            }
            return;
        }

        held.reset();
        if (c.cut) {
            if (content && footer.length > 0) {
                out.write(footer, 0, footer.length);
                footers++;
            }
            content = false;
        }
        out.write(h, 0, h.length);
        skip = c.payload;
        if (c.prints) content = true;
    }
}
