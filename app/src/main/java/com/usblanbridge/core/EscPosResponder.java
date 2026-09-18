package com.usblanbridge.core;

import java.io.ByteArrayOutputStream;
import java.io.UnsupportedEncodingException;

/**
 * Answers the status and identity queries POS software sends down the raw 9100 stream, on behalf of a USB
 * printer that cannot reply through the phone's USB pipe in the way clients expect.
 *
 * Every value here was captured from a real Epson TM-T20II, so SDK clients that verify a printer before
 * accepting it behave the same against the bridge as against genuine hardware:
 *
 *   DLE EOT 1 -> 0x16     DLE EOT 2..4 -> 0x12
 *   GS  I  1  -> 0x63     GS  I  67    -> 0x5F "TM-T20II" 0x00
 *   GS  a  n  -> 14 00 00 0F
 *   GS  ( H   -> 0x37 0x22 id 0x00      the process-id echo the Epson SDK waits for to confirm a job finished
 *
 * Queries are consumed rather than forwarded, because they are not print data. Everything else passes through
 * untouched, including print commands sharing the same lead byte such as GS ( k for QR codes.
 *
 * This is a port of the C# EscPosResponder in the Windows build; keep the two in step.
 */
public final class EscPosResponder {

    public static final byte DLE = 0x10;
    public static final byte EOT = 0x04;
    public static final byte GS = 0x1D;

    private static final int MAX_HELD = 64 * 1024;

    private String modelName = "TM-T20II";

    private byte[] pending = new byte[256];
    private int pendingLen;

    private final ByteArrayOutputStream forward = new ByteArrayOutputStream(8192);
    private final ByteArrayOutputStream replies = new ByteArrayOutputStream(64);

    private int queriesAnswered;

    public void setModelName(String name) {
        if (name != null && name.length() > 0) this.modelName = name;
    }

    public int getQueriesAnswered() {
        return queriesAnswered;
    }

    /** Bytes to send on to the printer after the last {@link #process} call. */
    public byte[] forwardBytes() {
        return forward.toByteArray();
    }

    public int forwardLength() {
        return forward.size();
    }

    /** Bytes to send back to the network client after the last {@link #process} call. */
    public byte[] replyBytes() {
        return replies.toByteArray();
    }

    public int replyLength() {
        return replies.size();
    }

    private enum Match {COMPLETE, INCOMPLETE, NO}

    public void process(byte[] input, int offset, int count) {
        forward.reset();
        replies.reset();

        ensurePending(pendingLen + count);
        System.arraycopy(input, offset, pending, pendingLen, count);
        pendingLen += count;

        int pos = 0;
        while (pos < pendingLen) {
            byte b = pending[pos];
            if (b != DLE && b != GS) {
                forward.write(b);
                pos++;
                continue;
            }
            int[] consumed = new int[1];
            Match m = tryMatch(pos, consumed);
            if (m == Match.COMPLETE) {
                pos += consumed[0];
                queriesAnswered++;
            } else if (m == Match.NO) {
                forward.write(b);
                pos++;
            } else {
                break; // incomplete: wait for the rest of the query
            }
        }

        if (pos > 0) {
            System.arraycopy(pending, pos, pending, 0, pendingLen - pos);
            pendingLen -= pos;
        }

        // Never hold an unbounded amount of data waiting for a query that will never complete.
        if (pendingLen > MAX_HELD) flushPending();
    }

    /** Releases bytes held back as a possible query. Call when the client disconnects or a job ends. */
    public void flush() {
        forward.reset();
        replies.reset();
        flushPending();
    }

    private void flushPending() {
        forward.write(pending, 0, pendingLen);
        pendingLen = 0;
    }

    private int available(int from) {
        return pendingLen - from;
    }

    private Match tryMatch(int pos, int[] consumed) {
        consumed[0] = 0;
        byte lead = pending[pos];

        if (lead == DLE) {
            if (available(pos) < 2) return Match.INCOMPLETE;
            if (pending[pos + 1] != EOT) return Match.NO; // DLE DC4 drawer pulse and friends pass through
            if (available(pos) < 3) return Match.INCOMPLETE;
            int n = pending[pos + 2] & 0xFF;
            if (n < 1 || n > 4) return Match.NO;
            replies.write(n == 1 ? 0x16 : 0x12);
            consumed[0] = 3;
            return Match.COMPLETE;
        }

        if (available(pos) < 2) return Match.INCOMPLETE;
        int second = pending[pos + 1] & 0xFF;

        if (second == 0x49) { // GS I n
            if (available(pos) < 3) return Match.INCOMPLETE;
            replyPrinterId(pending[pos + 2] & 0xFF);
            consumed[0] = 3;
            return Match.COMPLETE;
        }

        if (second == 0x61) { // GS a n, enable automatic status back
            if (available(pos) < 3) return Match.INCOMPLETE;
            replies.write(0x14);
            replies.write(0x00);
            replies.write(0x00);
            replies.write(0x0F);
            consumed[0] = 3;
            return Match.COMPLETE;
        }

        if (second == 0x72) { // GS r n, transmit status
            if (available(pos) < 3) return Match.INCOMPLETE;
            replies.write(0x00);
            consumed[0] = 3;
            return Match.COMPLETE;
        }

        if (second == 0x28) { // GS ( ... ; only GS ( H is a query
            if (available(pos) < 3) return Match.INCOMPLETE;
            if ((pending[pos + 2] & 0xFF) != 0x48) return Match.NO;
            if (available(pos) < 5) return Match.INCOMPLETE;
            int len = (pending[pos + 3] & 0xFF) | ((pending[pos + 4] & 0xFF) << 8);
            if (len < 2 || len > 1024) return Match.NO; // implausible: treat as print data
            int total = 5 + len;
            if (available(pos) < total) return Match.INCOMPLETE;

            replies.write(0x37);
            replies.write(0x22);
            for (int i = pos + 7; i < pos + total; i++) replies.write(pending[i]);
            replies.write(0x00);
            consumed[0] = total;
            return Match.COMPLETE;
        }

        return Match.NO;
    }

    private void replyPrinterId(int n) {
        switch (n) {
            case 1:
            case 49:
                replies.write(0x63);
                break;
            case 2:
            case 50:
            case 3:
            case 51:
                replies.write(0x00);
                break;
            default:
                if (n >= 65) {
                    replies.write(0x5F);
                    byte[] name;
                    try {
                        name = modelName.getBytes("US-ASCII");
                    } catch (UnsupportedEncodingException e) {
                        name = modelName.getBytes();
                    }
                    replies.write(name, 0, name.length);
                    replies.write(0x00);
                } else {
                    replies.write(0x00);
                }
                break;
        }
    }

    private void ensurePending(int needed) {
        if (pending.length >= needed) return;
        int size = pending.length;
        while (size < needed) size *= 2;
        byte[] bigger = new byte[size];
        System.arraycopy(pending, 0, bigger, 0, pendingLen);
        pending = bigger;
    }
}
