package com.usblanbridge.core;

/**
 * A complete ESC/POS command header, classified: how many payload bytes follow it, whether it is a cut, and
 * whether it puts marks on the paper. Shared by FooterInjector and CutFilter so both agree on where commands
 * start and end, which is what keeps image and QR data from being mistaken for a cut.
 *
 * Deliberately free of Android imports so it can be verified on a desktop JVM.
 */
final class EscPosCommand {

    static final int ESC = 0x1B;
    static final int FS = 0x1C;
    static final int GS = 0x1D;

    /** Bytes that follow the header and belong to it; they pass through untouched. */
    final long payload;
    final boolean cut;
    final boolean prints;
    /** Readable name for cuts, e.g. "GS V 66 0 (feed and cut)"; empty otherwise. */
    final String name;

    private EscPosCommand(long payload, boolean cut, boolean prints, String name) {
        this.payload = payload;
        this.cut = cut;
        this.prints = prints;
        this.name = name;
    }

    static final EscPosCommand PLAIN = new EscPosCommand(0, false, false, "");
    static final EscPosCommand PRINTS = new EscPosCommand(0, false, true, "");

    static EscPosCommand withPayload(long payload, boolean prints) {
        return new EscPosCommand(payload, false, prints, "");
    }

    static EscPosCommand cut(String name) {
        return new EscPosCommand(0, true, false, name);
    }

    /** Null while the held bytes are still an incomplete command. */
    static EscPosCommand classify(byte[] h) {
        if (h.length < 2) return null;
        int n = h[1] & 0xFF;
        switch (h[0] & 0xFF) {
            case ESC:
                return classifyEsc(h, n);
            case GS:
                return classifyGs(h, n);
            default:
                return classifyFs(h, n);
        }
    }

    private static EscPosCommand classifyEsc(byte[] h, int n) {
        switch (n) {
            case 0x69:                                              // ESC i, ESC m: the older cut commands
                return cut("ESC i (partial cut)");
            case 0x6D:
                return cut("ESC m (partial cut)");
            case 0x40: case 0x32: case 0x3C: case 0x4C: case 0x53: case 0x0C: case 0x76:   // ESC @ 2 < L S FF v: no parameter
                return PLAIN;
            case 0x2A: {                                            // ESC * m nL nH d...: bit image
                if (h.length < 5) return null;
                int m = h[2] & 0xFF;
                long dots = (h[3] & 0xFF) | ((h[4] & 0xFF) << 8);
                return withPayload(dots * ((m == 0 || m == 1) ? 1 : 3), true);
            }
            case 0x70:                                              // ESC p m t1 t2: drawer pulse, prints nothing
                return h.length < 5 ? null : PLAIN;
            case 0x63: case 0x66: case 0x42:                        // ESC c x n, ESC f n m, ESC B n t
                return h.length < 4 ? null : PLAIN;
            case 0x24: case 0x5C:                                   // ESC $ nL nH, ESC \ nL nH
                return h.length < 4 ? null : PLAIN;
            case 0x57:                                              // ESC W: page-mode print area, ten bytes
                return h.length < 10 ? null : PLAIN;
            case 0x44:                                              // ESC D n1..nk NUL: tab stops, NUL-terminated
                return (h.length >= 3 && h[h.length - 1] == 0) ? PLAIN : null;
            case 0x28: {                                            // ESC ( x pL pH d...: same shape as GS (
                if (h.length < 5) return null;
                return withPayload((h[3] & 0xFF) | ((h[4] & 0xFF) << 8), false);
            }
            case 0x26:                                              // ESC & y c1 c2: user-defined characters, header only
                return h.length < 5 ? null : PLAIN;
            default:                                                // ESC ! - 3 = E G J M R T U V a d t { ...: one parameter
                return h.length < 3 ? null : PLAIN;
        }
    }

    private static EscPosCommand classifyGs(byte[] h, int n) {
        switch (n) {
            case 0x56: {                                            // GS V m [n]: cut
                if (h.length < 3) return null;
                int m = h[2] & 0xFF;
                if (m == 0 || m == 48) return cut("GS V " + m + " (full cut)");
                if (m == 1 || m == 49) return cut("GS V " + m + " (partial cut)");
                if (h.length < 4) return null;                      // 65, 66, 97, 98, 103, 104 carry a feed amount
                return cut("GS V " + m + " " + (h[3] & 0xFF) + " (feed and cut)");
            }
            case 0x76: {                                            // GS v 0 m xL xH yL yH d...: raster image
                if (h.length < 3) return null;
                if ((h[2] & 0xFF) != 0x30) return PLAIN;
                if (h.length < 8) return null;
                long widthBytes = (h[4] & 0xFF) | ((h[5] & 0xFF) << 8);
                long rows = (h[6] & 0xFF) | ((h[7] & 0xFF) << 8);
                return withPayload(widthBytes * rows, true);
            }
            case 0x28: {                                            // GS ( x pL pH d...: symbols, graphics, status
                if (h.length < 5) return null;
                long len = (h[3] & 0xFF) | ((h[4] & 0xFF) << 8);
                int fn = h[2] & 0xFF;
                return withPayload(len, fn == 0x6B || fn == 0x4C);   // ( k symbols and ( L graphics print
            }
            case 0x38: {                                            // GS 8 L p1 p2 p3 p4 d...: large graphics
                if (h.length < 3) return null;
                if ((h[2] & 0xFF) != 0x4C) return PLAIN;
                if (h.length < 7) return null;
                long len = (h[3] & 0xFF) | ((h[4] & 0xFF) << 8) | ((long) (h[5] & 0xFF) << 16) | ((long) (h[6] & 0xFF) << 24);
                return withPayload(len, true);
            }
            case 0x2A: {                                            // GS * x y d...: define a downloaded bit image
                if (h.length < 4) return null;
                return withPayload((long) (h[2] & 0xFF) * (h[3] & 0xFF) * 8, false);
            }
            case 0x6B: {                                            // GS k: barcode, NUL-terminated below 65, length-prefixed above
                if (h.length < 3) return null;
                int m = h[2] & 0xFF;
                if (m < 65) return (h.length >= 4 && h[h.length - 1] == 0) ? PRINTS : null;
                if (h.length < 4) return null;
                return withPayload(h[3] & 0xFF, true);
            }
            case 0x2F:                                              // GS / m: print the downloaded bit image
                return h.length < 3 ? null : PRINTS;
            case 0x3A:                                              // GS : macro start and end
                return PLAIN;
            case 0x4C: case 0x57: case 0x50: case 0x5C: case 0x24:  // GS L W P \ $: two parameters
                return h.length < 4 ? null : PLAIN;
            case 0x5E:                                              // GS ^ r t m
                return h.length < 5 ? null : PLAIN;
            default:                                                // GS ! B E H I T a b f h r w ...: one parameter
                return h.length < 3 ? null : PLAIN;
        }
    }

    private static EscPosCommand classifyFs(byte[] h, int n) {
        switch (n) {
            case 0x26: case 0x2E:                                   // FS &, FS .: Kanji mode on and off
                return PLAIN;
            case 0x28: {                                            // FS ( x pL pH d...: same shape as GS (
                if (h.length < 5) return null;
                return withPayload((h[3] & 0xFF) | ((h[4] & 0xFF) << 8), false);
            }
            case 0x53:                                              // FS S n1 n2
                return h.length < 4 ? null : PLAIN;
            case 0x70:                                              // FS p n m: print a stored image
                return h.length < 4 ? null : PRINTS;
            default:                                                // FS ! - C W q ...: one parameter
                return h.length < 3 ? null : PLAIN;
        }
    }
}
