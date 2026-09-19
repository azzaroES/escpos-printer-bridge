package com.usblanbridge.core;

import java.io.ByteArrayOutputStream;

/**
 * Removes every cutter command from an ESC/POS stream: GS V in all its forms, and the older ESC i and ESC m.
 * Everything else passes through byte for byte, including cut-like bytes inside image and symbol payloads,
 * which are skipped by their declared length exactly as FooterInjector does.
 *
 * Each removed cut can be replaced by a line feed, so the receipt still comes out far enough to be torn off
 * by hand at the tear bar instead of stopping under the print head. The feed is only emitted when something
 * was printed since the previous cut, so a POS app that sends two cuts in a row does not produce two blank
 * strips.
 *
 * Deliberately free of Android imports so it can be verified on a desktop JVM.
 */
public final class CutFilter {

    /** Told about every cut removed, with the command's name, e.g. "GS V 66 0 (feed and cut)". */
    public interface Listener {
        void cutRemoved(String command);
    }

    private static final int ESC = EscPosCommand.ESC;
    private static final int FS = EscPosCommand.FS;
    private static final int GS = EscPosCommand.GS;
    private static final int MAX_HELD = 1024;

    private final ByteArrayOutputStream out = new ByteArrayOutputStream(4096);
    private final ByteArrayOutputStream held = new ByteArrayOutputStream(16);
    private final Listener listener;
    private int feedLines;
    private long skip;
    private boolean content;
    private int removed;

    public CutFilter(int feedLines, Listener listener) {
        this.feedLines = feedLines;
        this.listener = listener;
    }

    /** Lines to feed in place of each removed cut (0 = nothing). Can change between writes. */
    public void setFeedLines(int lines) {
        feedLines = lines;
    }

    public void process(byte[] data, int offset, int length) {
        for (int i = 0; i < length; i++) accept(data[offset + i] & 0xFF);
    }

    /** Call once at the end of the job: releases a held, incomplete command unchanged. */
    public void finish() {
        if (held.size() > 0) {
            byte[] h = held.toByteArray();
            held.reset();
            out.write(h, 0, h.length);
        }
        skip = 0;
    }

    /** Output produced so far; clears the buffer. */
    public byte[] takeOutput() {
        byte[] b = out.toByteArray();
        out.reset();
        return b;
    }

    public int cutsRemoved() {
        return removed;
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
            removed++;
            if (feedLines > 0 && content) {
                out.write(0x1B);
                out.write(0x64);                 // ESC d n: print and feed n lines
                out.write(Math.min(feedLines, 255));
            }
            content = false;
            if (listener != null) {
                try {
                    listener.cutRemoved(c.name);
                } catch (Throwable ignored) {
                }
            }
            return;
        }
        out.write(h, 0, h.length);
        skip = c.payload;
        if (c.prints) content = true;
    }
}
