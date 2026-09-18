import com.usblanbridge.core.EscPosResponder;

import java.util.Arrays;
import java.util.Random;

/**
 * Desktop self-test for the Android EscPosResponder.
 *
 * EscPosResponder deliberately has no Android imports, so it can be compiled and exercised on a normal JVM.
 * That matters because the responder is the piece that makes POS software accept the bridge as a real printer,
 * and the expected values here were captured from a genuine Epson TM-T20II on the LAN.
 *
 * Run it with android/tools/run-responder-test.cmd. Exit code is the number of failures.
 */
public final class ResponderSelfTest {

    private static int passed;
    private static int failed;

    public static void main(String[] args) {
        plainDataPassesThrough();
        statusRequestsAnswered();
        unknownStatusArgPassesThrough();
        dleDc4PassesThrough();
        printerIdQueries();
        automaticStatusBack();
        transmitStatus();
        processIdEcho();
        processIdSplitAcrossChunks();
        qrPrintDataUntouched();
        flushReleasesHeldBytes();
        largePayloadIsByteExact();

        System.out.println();
        System.out.println("Passed: " + passed + "   Failed: " + failed);
        System.exit(failed);
    }

    // ------------------------------------------------------------------ tests

    private static void plainDataPassesThrough() {
        EscPosResponder r = new EscPosResponder();
        byte[] in = ascii("Hello receipt\n");
        r.process(in, 0, in.length);
        check("plain data forwarded", Arrays.equals(in, r.forwardBytes()));
        check("plain data produces no reply", r.replyLength() == 0);
    }

    private static void statusRequestsAnswered() {
        // Captured from a real TM-T20II: DLE EOT 1 -> 0x16, DLE EOT 2..4 -> 0x12.
        EscPosResponder r = new EscPosResponder();
        byte[] in = concat(ascii("AB"), bytes(0x10, 0x04, 0x01), ascii("CD"), bytes(0x10, 0x04, 0x04));
        r.process(in, 0, in.length);
        check("status queries stripped from print data", Arrays.equals(ascii("ABCD"), r.forwardBytes()));
        check("DLE EOT 1 -> 0x16, DLE EOT 4 -> 0x12", Arrays.equals(bytes(0x16, 0x12), r.replyBytes()));

        EscPosResponder r2 = new EscPosResponder();
        byte[] in2 = bytes(0x10, 0x04, 0x02, 0x10, 0x04, 0x03);
        r2.process(in2, 0, in2.length);
        check("DLE EOT 2 and 3 -> 0x12", Arrays.equals(bytes(0x12, 0x12), r2.replyBytes()));
    }

    private static void unknownStatusArgPassesThrough() {
        EscPosResponder r = new EscPosResponder();
        byte[] in = bytes(0x10, 0x04, 0x09, 0x41);
        r.process(in, 0, in.length);
        check("DLE EOT with an out-of-range argument is print data", Arrays.equals(in, r.forwardBytes()));
        check("no reply for unknown status argument", r.replyLength() == 0);
    }

    private static void dleDc4PassesThrough() {
        EscPosResponder r = new EscPosResponder();
        byte[] in = bytes(0x10, 0x14, 0x01, 0x00, 0x05); // real-time drawer pulse: an action, not a query
        r.process(in, 0, in.length);
        check("DLE DC4 drawer pulse reaches the printer", Arrays.equals(in, r.forwardBytes()));
        check("DLE DC4 produces no reply", r.replyLength() == 0);
    }

    private static void printerIdQueries() {
        EscPosResponder r = new EscPosResponder();
        r.process(bytes(0x1D, 0x49, 0x01), 0, 3);
        check("GS I 1 -> 0x63", Arrays.equals(bytes(0x63), r.replyBytes()));
        check("GS I 1 not forwarded", r.forwardLength() == 0);

        r.process(bytes(0x1D, 0x49, 0x43), 0, 3);
        check("GS I 67 -> 0x5F model NUL",
                Arrays.equals(concat(bytes(0x5F), ascii("TM-T20II"), bytes(0x00)), r.replyBytes()));

        EscPosResponder custom = new EscPosResponder();
        custom.setModelName("TM-T88VI");
        custom.process(bytes(0x1D, 0x49, 0x43), 0, 3);
        check("GS I 67 honours a configured model name",
                Arrays.equals(concat(bytes(0x5F), ascii("TM-T88VI"), bytes(0x00)), custom.replyBytes()));
    }

    private static void automaticStatusBack() {
        EscPosResponder r = new EscPosResponder();
        r.process(bytes(0x1D, 0x61, 0xFF), 0, 3);
        check("GS a -> 14 00 00 0F", Arrays.equals(bytes(0x14, 0x00, 0x00, 0x0F), r.replyBytes()));
        check("GS a not forwarded", r.forwardLength() == 0);
    }

    private static void transmitStatus() {
        EscPosResponder r = new EscPosResponder();
        r.process(bytes(0x1D, 0x72, 0x01), 0, 3);
        check("GS r 1 -> 0x00", Arrays.equals(bytes(0x00), r.replyBytes()));
    }

    private static void processIdEcho() {
        // This is the handshake Epson's SDK waits for to decide a job finished.
        EscPosResponder r = new EscPosResponder();
        byte[] req = concat(bytes(0x1D, 0x28, 0x48, 0x06, 0x00, 0x30, 0x30), ascii("ABCD"));
        r.process(req, 0, req.length);
        check("GS ( H echoes 37 22 <id> 00",
                Arrays.equals(concat(bytes(0x37, 0x22), ascii("ABCD"), bytes(0x00)), r.replyBytes()));
        check("GS ( H not forwarded to the printer", r.forwardLength() == 0);
    }

    private static void processIdSplitAcrossChunks() {
        byte[] req = concat(bytes(0x1D, 0x28, 0x48, 0x06, 0x00, 0x30, 0x30), ascii("WXYZ"));
        byte[] expected = concat(bytes(0x37, 0x22), ascii("WXYZ"), bytes(0x00));
        boolean ok = true;
        for (int cut = 1; cut < req.length; cut++) {
            EscPosResponder r = new EscPosResponder();
            r.process(req, 0, cut);
            if (r.replyLength() != 0) ok = false;
            r.process(req, cut, req.length - cut);
            if (!Arrays.equals(expected, r.replyBytes())) ok = false;
            if (r.forwardLength() != 0) ok = false;
        }
        check("GS ( H answered no matter where the TCP read splits it", ok);
    }

    private static void qrPrintDataUntouched() {
        EscPosResponder r = new EscPosResponder();
        byte[] qr = concat(
                bytes(0x1D, 0x28, 0x6B, 0x08, 0x00, 0x31, 0x50, 0x30), ascii("HELLO"),
                bytes(0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x51, 0x30));
        r.process(qr, 0, qr.length);
        byte[] out = r.forwardBytes();
        r.flush();
        byte[] all = concat(out, r.forwardBytes());
        check("GS ( k QR print data passes through unchanged", Arrays.equals(qr, all));
        check("QR print data produces no reply", r.replyLength() == 0);
    }

    private static void flushReleasesHeldBytes() {
        EscPosResponder r = new EscPosResponder();
        byte[] in = bytes(0x41, 0x10, 0x04); // trailing DLE EOT is held pending its argument
        r.process(in, 0, in.length);
        check("held query prefix is not forwarded yet", Arrays.equals(bytes(0x41), r.forwardBytes()));
        r.flush();
        check("flush releases the held bytes", Arrays.equals(bytes(0x10, 0x04), r.forwardBytes()));
    }

    private static void largePayloadIsByteExact() {
        Random rnd = new Random(1234);
        byte[] data = new byte[1024 * 1024];
        rnd.nextBytes(data);
        // Remove query lead bytes so the expected output is exactly the input.
        for (int i = 0; i < data.length; i++) {
            if (data[i] == 0x10) data[i] = 0x11;
            else if (data[i] == 0x1D) data[i] = 0x1E;
        }

        EscPosResponder r = new EscPosResponder();
        java.io.ByteArrayOutputStream out = new java.io.ByteArrayOutputStream(data.length);
        int offset = 0;
        while (offset < data.length) {
            int n = Math.min(1 + rnd.nextInt(5000), data.length - offset);
            r.process(data, offset, n);
            byte[] f = r.forwardBytes();
            out.write(f, 0, f.length);
            offset += n;
        }
        r.flush();
        byte[] tail = r.forwardBytes();
        out.write(tail, 0, tail.length);
        check("1 MB stream survives arbitrary chunk boundaries byte for byte",
                Arrays.equals(data, out.toByteArray()));
    }

    // ------------------------------------------------------------------ helpers

    private static void check(String what, boolean condition) {
        if (condition) {
            passed++;
            System.out.println("PASS  " + what);
        } else {
            failed++;
            System.out.println("FAIL  " + what);
        }
    }

    private static byte[] bytes(int... values) {
        byte[] b = new byte[values.length];
        for (int i = 0; i < values.length; i++) b[i] = (byte) values[i];
        return b;
    }

    private static byte[] ascii(String s) {
        try {
            return s.getBytes("US-ASCII");
        } catch (Exception e) {
            return s.getBytes();
        }
    }

    private static byte[] concat(byte[]... parts) {
        int total = 0;
        for (byte[] p : parts) total += p.length;
        byte[] out = new byte[total];
        int at = 0;
        for (byte[] p : parts) {
            System.arraycopy(p, 0, out, at, p.length);
            at += p.length;
        }
        return out;
    }
}
