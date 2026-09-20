import com.usblanbridge.core.BrandedPrintTarget;
import com.usblanbridge.core.CutFilter;
import com.usblanbridge.core.EposDeviceId;
import com.usblanbridge.core.EventLog;
import com.usblanbridge.core.FooterInjector;
import com.usblanbridge.core.License;
import com.usblanbridge.core.NoCutPrintTarget;
import com.usblanbridge.core.PrintTarget;
import com.usblanbridge.core.RasterEncoder;
import com.usblanbridge.core.TelemetryLog;
import com.usblanbridge.core.TicketText;
import com.usblanbridge.core.TlsCertificate;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.IOException;
import java.math.BigInteger;
import java.nio.file.Files;
import java.security.KeyPair;
import java.security.KeyPairGenerator;
import java.security.Signature;
import java.security.cert.X509Certificate;
import java.security.spec.ECGenParameterSpec;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Base64;
import java.util.Collection;
import java.util.Date;
import java.util.List;
import java.util.Random;

import javax.net.ssl.SSLContext;
import javax.net.ssl.SSLServerSocket;
import javax.net.ssl.SSLSocket;
import javax.net.ssl.TrustManager;
import javax.net.ssl.X509TrustManager;

/**
 * Desktop self-test for the pure-Java parts behind Android's print dialog: the raster encoder that turns a
 * rendered page into ESC/POS, the footer injector that stamps every ticket, and the licence check.
 *
 * Run it with android/tools/run-responder-test.cmd, which runs the responder test as well. Exit code is the
 * number of failures.
 */
public final class CoreSelfTest {

    private static int passed;
    private static int failed;

    private static final byte[] FOOTER = FooterInjector.footerFor("digitalstudio.PRO");
    private static final byte[] CUT4 = bytes(0x1D, 0x56, 0x42, 0x00);
    private static final byte[] CUT3 = bytes(0x1D, 0x56, 0x01);

    public static void main(String[] args) throws Exception {
        rasterBlankPageIsEmpty();
        rasterTrimsBlankRows();
        rasterBitsAreMsbFirst();
        rasterSplitsIntoBands();
        rasterDithersMidGray();

        footerBeforeCut();
        footerBeforeThreeByteCut();
        footerBeforeOldCut();
        footerAtEndWithoutCut();
        noFooterForDrawerPulseOnly();
        noFooterForInitOnly();
        rasterPayloadNotMistakenForCut();
        qrPayloadNotMistakenForCut();
        twoTicketsTwoFooters();
        splitAnywhereGivesSameOutput();
        brandedTargetWrapsJob();

        cutFilterRemovesEveryForm();
        cutFilterKeepsPayloads();
        cutFilterSplitAnywhere();
        cutFilterPassesThroughWithoutCuts();
        noCutTargetFollowsTheSwitch();
        footerThenNoCut();
        ticketRenders();

        licenceRoundTrip();
        embeddedPublicKeyIsSet();
        base64RoundTrip();

        derPrimitives();
        certificateBuildsAndVerifies();
        certificateMaterialIsReusedAndRemade();
        certificateServesTls();
        eposDeviceIdRules();
        telemetryLogWritesRows();
        eventLogKeepsNewestFirst();

        System.out.println();
        System.out.println("Passed: " + passed + "   Failed: " + failed);
        System.exit(failed);
    }

    // ------------------------------------------------------------------ raster

    private static void rasterBlankPageIsEmpty() {
        byte[] gray = new byte[16 * 4];
        Arrays.fill(gray, (byte) 255);
        check("blank page encodes to nothing", RasterEncoder.encode(gray, 16, 4, true).length == 0);
    }

    private static void rasterTrimsBlankRows() {
        byte[] gray = new byte[16 * 5];
        Arrays.fill(gray, (byte) 255);
        Arrays.fill(gray, 2 * 16, 3 * 16, (byte) 0);   // only row 2 is black
        byte[] out = RasterEncoder.encode(gray, 16, 5, false);
        byte[] expected = concat(bytes(0x1D, 0x76, 0x30, 0x00, 0x02, 0x00, 0x01, 0x00), bytes(0xFF, 0xFF));
        check("blank rows above and below are trimmed, one row of two bytes remains", Arrays.equals(expected, out));
    }

    private static void rasterBitsAreMsbFirst() {
        byte[] gray = new byte[10];
        Arrays.fill(gray, (byte) 255);
        gray[0] = 0;
        gray[9] = 0;
        byte[] out = RasterEncoder.encode(gray, 10, 1, false);
        byte[] expected = concat(bytes(0x1D, 0x76, 0x30, 0x00, 0x02, 0x00, 0x01, 0x00), bytes(0x80, 0x40));
        check("leftmost pixel is the top bit; pixel 9 lands in bit 6 of byte 1", Arrays.equals(expected, out));
    }

    private static void rasterSplitsIntoBands() {
        byte[] gray = new byte[8 * 600];   // all black, one byte per row
        byte[] out = RasterEncoder.encode(gray, 8, 600, false);
        boolean ok = out.length == 3 * 8 + 600;
        ok &= out[6] == 0 && out[7] == 1;                       // first band: 256 rows
        int second = 8 + 256;
        ok &= out[second + 6] == 0 && out[second + 7] == 1;     // second band: 256 rows
        int third = second + 8 + 256;
        ok &= (out[third + 6] & 0xFF) == 88 && out[third + 7] == 0;   // last band: 88 rows
        check("600 rows go out as bands of 256, 256 and 88", ok);
    }

    private static void rasterDithersMidGray() {
        int w = 64, h = 64;
        byte[] gray = new byte[w * h];
        Arrays.fill(gray, (byte) 128);
        byte[] bits = RasterEncoder.toBits(gray, w, h, true);
        int black = 0;
        for (byte b : bits) black += Integer.bitCount(b & 0xFF);
        double ratio = black / (double) (w * h);
        check("mid gray dithers to roughly half black (" + Math.round(ratio * 100) + "%)", ratio > 0.3 && ratio < 0.7);

        Arrays.fill(gray, (byte) 0);
        byte[] allBlack = RasterEncoder.toBits(gray, w, h, true);
        boolean solid = true;
        for (byte b : allBlack) solid &= b == (byte) 0xFF;
        check("black stays solid black under dithering", solid);
    }

    // ------------------------------------------------------------------ footer

    private static void footerBeforeCut() {
        byte[] in = concat(ascii("Hello\n"), CUT4);
        byte[] out = run(in);
        check("footer goes in front of GS V 66 0", Arrays.equals(concat(ascii("Hello\n"), FOOTER, CUT4), out));
    }

    private static void footerBeforeThreeByteCut() {
        byte[] out = run(concat(ascii("Hi\n"), CUT3));
        check("footer goes in front of the three-byte GS V 1", Arrays.equals(concat(ascii("Hi\n"), FOOTER, CUT3), out));
    }

    private static void footerBeforeOldCut() {
        byte[] cut = bytes(0x1B, 0x69);
        byte[] out = run(concat(ascii("Hi\n"), cut));
        check("footer goes in front of the old ESC i cut", Arrays.equals(concat(ascii("Hi\n"), FOOTER, cut), out));
    }

    private static void footerAtEndWithoutCut() {
        byte[] out = run(ascii("Hi\n"));
        check("a ticket that never cuts gets the footer at the end", Arrays.equals(concat(ascii("Hi\n"), FOOTER), out));
    }

    private static void noFooterForDrawerPulseOnly() {
        byte[] pulse = bytes(0x1B, 0x70, 0x00, 0x19, 0xFA);
        check("a bare drawer pulse prints nothing, so no footer", Arrays.equals(pulse, run(pulse)));
    }

    private static void noFooterForInitOnly() {
        byte[] in = concat(bytes(0x1B, 0x40), bytes(0x1B, 0x64, 0x03), CUT4);
        check("init, feed and cut with no content get no footer", Arrays.equals(in, run(in)));
    }

    private static void rasterPayloadNotMistakenForCut() {
        // Two bytes per row, two rows: the payload is exactly the bytes of a cut command.
        byte[] raster = concat(bytes(0x1D, 0x76, 0x30, 0x00, 0x02, 0x00, 0x02, 0x00), CUT4);
        byte[] in = concat(raster, CUT4);
        byte[] out = run(in);
        check("cut bytes inside raster data are left alone; the real cut gets the footer",
                Arrays.equals(concat(raster, FOOTER, CUT4), out));
    }

    private static void qrPayloadNotMistakenForCut() {
        byte[] qr = concat(bytes(0x1D, 0x28, 0x6B, 0x04, 0x00, 0x31, 0x50), bytes(0x1D, 0x56));
        byte[] in = concat(qr, CUT3);
        check("cut bytes inside a GS ( k payload are left alone", Arrays.equals(concat(qr, FOOTER, CUT3), run(in)));
    }

    private static void twoTicketsTwoFooters() {
        byte[] in = concat(ascii("A\n"), CUT4, ascii("B\n"), CUT4);
        FooterInjector f = new FooterInjector(FOOTER);
        f.process(in, 0, in.length);
        f.finish();
        byte[] out = f.takeOutput();
        check("two tickets in one job each get a footer",
                Arrays.equals(concat(ascii("A\n"), FOOTER, CUT4, ascii("B\n"), FOOTER, CUT4), out) && f.footersInserted() == 2);
    }

    private static void splitAnywhereGivesSameOutput() {
        byte[] raster = concat(bytes(0x1D, 0x76, 0x30, 0x00, 0x02, 0x00, 0x02, 0x00), CUT4);
        byte[] qr = concat(bytes(0x1D, 0x28, 0x6B, 0x04, 0x00, 0x31, 0x50), bytes(0x1D, 0x56));
        byte[] stream = concat(bytes(0x1B, 0x40), ascii("Shop\n"), raster, qr, bytes(0x1B, 0x70, 0x00, 0x19, 0xFA),
                bytes(0x1B, 0x64, 0x02), CUT4, ascii("Kitchen\n"), CUT3, bytes(0x1B, 0x70, 0x00, 0x19, 0xFA));
        byte[] reference = run(stream);
        boolean ok = true;
        for (int cut = 1; cut < stream.length; cut++) {
            FooterInjector f = new FooterInjector(FOOTER);
            f.process(stream, 0, cut);
            f.process(stream, cut, stream.length - cut);
            f.finish();
            if (!Arrays.equals(reference, f.takeOutput())) ok = false;
        }
        check("output is identical wherever the TCP read splits the stream", ok);
        check("that stream gets exactly two footers", countFooters(reference) == 2);
    }

    private static void brandedTargetWrapsJob() throws IOException {
        MemoryTarget memory = new MemoryTarget();
        BrandedPrintTarget branded = new BrandedPrintTarget(memory, new BrandedPrintTarget.FooterSource() {
            @Override
            public byte[] footer() {
                return FOOTER;
            }
        });
        PrintTarget.Job job = branded.startJob("t");
        byte[] text = ascii("X\n");
        job.write(text, 0, text.length);
        job.write(CUT4, 0, CUT4.length);
        job.close();
        check("branded target delivers text, footer, cut", Arrays.equals(concat(text, FOOTER, CUT4), memory.bytes()));

        MemoryTarget plain = new MemoryTarget();
        BrandedPrintTarget licensed = new BrandedPrintTarget(plain, new BrandedPrintTarget.FooterSource() {
            @Override
            public byte[] footer() {
                return null;
            }
        });
        PrintTarget.Job j2 = licensed.startJob("t");
        j2.write(text, 0, text.length);
        j2.write(CUT4, 0, CUT4.length);
        j2.close();
        check("with no footer the bytes pass through untouched", Arrays.equals(concat(text, CUT4), plain.bytes()));
    }

    // ------------------------------------------------------------------ no cut

    private static byte[] feed(int lines) {
        return bytes(0x1B, 0x64, lines);
    }

    private static byte[] cutFilter(byte[] in, int feedLines) {
        CutFilter f = new CutFilter(feedLines, null);
        f.process(in, 0, in.length);
        f.finish();
        return f.takeOutput();
    }

    private static void cutFilterRemovesEveryForm() {
        byte[] in = concat(ascii("One\n"), CUT4, ascii("Two\n"), CUT3, ascii("Three\n"), bytes(0x1B, 0x69),
                ascii("Four\n"), bytes(0x1B, 0x6D), ascii("Five\n"), bytes(0x1D, 0x56, 0x00), ascii("Six\n"), bytes(0x1D, 0x56, 0x41, 0x10));
        final int[] events = {0};
        CutFilter f = new CutFilter(4, new CutFilter.Listener() {
            @Override
            public void cutRemoved(String command) {
                events[0]++;
            }
        });
        f.process(in, 0, in.length);
        f.finish();
        byte[] expected = concat(ascii("One\n"), feed(4), ascii("Two\n"), feed(4), ascii("Three\n"), feed(4),
                ascii("Four\n"), feed(4), ascii("Five\n"), feed(4), ascii("Six\n"), feed(4));
        check("all six cut forms are replaced by ESC d 4", Arrays.equals(expected, f.takeOutput()));
        check("six cuts counted and reported", f.cutsRemoved() == 6 && events[0] == 6);
        check("two cuts in a row give one feed", Arrays.equals(concat(ascii("A\n"), feed(3)), cutFilter(concat(ascii("A\n"), CUT4, CUT4), 3)));
        check("feed 0 removes the cut and adds nothing", Arrays.equals(ascii("A\n"), cutFilter(concat(ascii("A\n"), CUT4), 0)));
        byte[] initOnly = concat(bytes(0x1B, 0x40), bytes(0x1B, 0x64, 0x02), CUT4);
        check("a cut with nothing printed before it gets no feed", Arrays.equals(concat(bytes(0x1B, 0x40), bytes(0x1B, 0x64, 0x02)), cutFilter(initOnly, 4)));
    }

    private static void cutFilterKeepsPayloads() {
        byte[] raster = concat(bytes(0x1D, 0x76, 0x30, 0x00, 0x02, 0x00, 0x02, 0x00), CUT4);
        byte[] qr = concat(bytes(0x1D, 0x28, 0x6B, 0x04, 0x00, 0x31, 0x50), bytes(0x1D, 0x56));
        byte[] bitImage = concat(bytes(0x1B, 0x2A, 0x00, 0x04, 0x00), CUT4);
        byte[] graphics = concat(bytes(0x1D, 0x28, 0x4C, 0x04, 0x00), CUT4);
        byte[] barcode = concat(bytes(0x1D, 0x6B, 0x49, 0x04), CUT4);
        byte[] in = concat(ascii("Logo\n"), raster, qr, bitImage, graphics, barcode, CUT4);
        byte[] expected = concat(ascii("Logo\n"), raster, qr, bitImage, graphics, barcode, feed(4));
        check("cut bytes inside raster, QR, bit image, graphics and barcode payloads are left alone", Arrays.equals(expected, cutFilter(in, 4)));
    }

    private static void cutFilterSplitAnywhere() {
        byte[] raster = concat(bytes(0x1D, 0x76, 0x30, 0x00, 0x02, 0x00, 0x02, 0x00), CUT4);
        byte[] qr = concat(bytes(0x1D, 0x28, 0x6B, 0x04, 0x00, 0x31, 0x50), bytes(0x1D, 0x56));
        byte[] stream = concat(bytes(0x1B, 0x40), ascii("Shop\n"), raster, qr, bytes(0x1B, 0x70, 0x00, 0x19, 0xFA),
                bytes(0x1B, 0x64, 0x02), CUT4, ascii("Kitchen\n"), CUT3, ascii("x"), bytes(0x1B, 0x69));
        byte[] reference = cutFilter(stream, 4);
        boolean ok = reference.length == stream.length - CUT4.length - CUT3.length - 2 + 9;
        for (int cut = 1; cut < stream.length; cut++) {
            CutFilter f = new CutFilter(4, null);
            f.process(stream, 0, cut);
            f.process(stream, cut, stream.length - cut);
            f.finish();
            if (!Arrays.equals(reference, f.takeOutput())) ok = false;
        }
        check("no-cut output is identical wherever the TCP read splits the stream", ok);
    }

    private static void cutFilterPassesThroughWithoutCuts() {
        byte[] realistic = concat(bytes(0x1B, 0x40), bytes(0x1B, 0x61, 0x01), ascii("SHOP\n"), bytes(0x1D, 0x21, 0x11),
                ascii("Total 12.50\n"), bytes(0x1D, 0x6B, 0x04), ascii("12345"), bytes(0x00), bytes(0x1B, 0x7A, 0x01),
                bytes(0x1B, 0x70, 0x00, 0x32, 0x32), bytes(0x1B, 0x64, 0x05), bytes(0x1B, 0x44, 0x08, 0x10, 0x00), bytes(0x0C), bytes(0x1B));
        check("a receipt without cuts passes through byte for byte, trailing lone ESC included", Arrays.equals(realistic, cutFilter(realistic, 4)));

        Random rnd = new Random(42);
        byte[] blob = new byte[128 * 1024];
        rnd.nextBytes(blob);
        for (int i = 0; i + 1 < blob.length; i++) {
            if ((blob[i] & 0xFF) == 0x1D && (blob[i + 1] & 0xFF) == 0x56) blob[i + 1] = 0;
            if ((blob[i] & 0xFF) == 0x1B && ((blob[i + 1] & 0xFF) == 0x69 || (blob[i + 1] & 0xFF) == 0x6D)) blob[i + 1] = 0;
        }
        CutFilter f = new CutFilter(4, null);
        ByteArrayOutputStream collected = new ByteArrayOutputStream();
        int pos = 0;
        while (pos < blob.length) {
            int n = Math.min(blob.length - pos, 1 + rnd.nextInt(3000));
            f.process(blob, pos, n);
            byte[] r = f.takeOutput();
            collected.write(r, 0, r.length);
            pos += n;
        }
        f.finish();
        byte[] tail = f.takeOutput();
        collected.write(tail, 0, tail.length);
        check("128 KB of random bytes without cut sequences survive intact", Arrays.equals(blob, collected.toByteArray()) && f.cutsRemoved() == 0);
    }

    private static void noCutTargetFollowsTheSwitch() throws IOException {
        MemoryTarget memory = new MemoryTarget();
        final int[] feedLines = {-1};
        final int[] reported = {0};
        NoCutPrintTarget wrapped = new NoCutPrintTarget(memory, new NoCutPrintTarget.Policy() {
            @Override
            public int feedLines() {
                return feedLines[0];
            }
        }, new NoCutPrintTarget.Listener() {
            @Override
            public void cutRemoved(String printer, String document, String command, int feed) {
                reported[0]++;
            }
        });
        PrintTarget.Job job = wrapped.startJob("t");
        byte[] a = concat(ascii("A\n"), CUT4);
        job.write(a, 0, a.length);
        feedLines[0] = 4;
        byte[] b = concat(ascii("B\n"), CUT4);
        job.write(b, 0, b.length);
        byte[] half = bytes(0x1D, 0x56);
        job.write(half, 0, half.length);
        feedLines[0] = -1;
        byte[] c = concat(bytes(0x42, 0x00), ascii("C\n"), CUT4);
        job.write(c, 0, c.length);
        job.close();
        byte[] expected = concat(ascii("A\n"), CUT4, ascii("B\n"), feed(4), bytes(0x1D, 0x56), bytes(0x42, 0x00), ascii("C\n"), CUT4);
        check("the wrapper filters only while the switch is on, and releases a held half-cut when it goes off",
                Arrays.equals(expected, memory.bytes()) && reported[0] == 1);
        check("unwrap returns the real target and the name is kept", wrapped.unwrap() == memory && "memory".equals(wrapped.getName()));
    }

    private static void footerThenNoCut() throws IOException {
        MemoryTarget memory = new MemoryTarget();
        NoCutPrintTarget noCut = new NoCutPrintTarget(memory, new NoCutPrintTarget.Policy() {
            @Override
            public int feedLines() {
                return 4;
            }
        }, null);
        BrandedPrintTarget branded = new BrandedPrintTarget(noCut, new BrandedPrintTarget.FooterSource() {
            @Override
            public byte[] footer() {
                return FOOTER;
            }
        });
        PrintTarget.Job job = branded.startJob("t");
        byte[] in = concat(ascii("A\n"), CUT4, ascii("B\n"), CUT4);
        job.write(in, 0, in.length);
        job.close();
        check("footer outside, NO CUT inside: each ticket ends footer, feed, no cut",
                Arrays.equals(concat(ascii("A\n"), FOOTER, feed(4), ascii("B\n"), FOOTER, feed(4)), memory.bytes()));
    }

    private static void ticketRenders() {
        byte[] job = concat(
                bytes(0x1B, 0x40), bytes(0x1B, 0x61, 0x01), ascii("ACME STORE\n"), bytes(0x1B, 0x61, 0x00), ascii("Total   12.50\r\n"),
                bytes(0x1D, 0x76, 0x30, 0x00, 0x02, 0x00, 0x02, 0x00), CUT4,                   // raster 16x2, payload is a cut
                bytes(0x1D, 0x28, 0x6B, 0x09, 0x00, 0x31, 0x50, 0x30), ascii("HELLO!"),      // QR store data
                bytes(0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x51, 0x30),                       // QR print
                bytes(0x1D, 0x6B, 0x04), ascii("12345"), bytes(0x00),                         // barcode, NUL-terminated
                bytes(0x1D, 0x6B, 0x49, 0x03), ascii("ABC"),                                  // barcode, length-prefixed
                bytes(0x1B, 0x70, 0x00, 0x32, 0x32), bytes(0x1B, 0x64, 0x02),                 // drawer, feed 2
                bytes(0x1B, 0x61, 0x02), ascii("Thanks\n"),                                   // right aligned
                CUT4);
        String text = TicketText.render(job, job.length);
        String[] expected = {
                spaces(19) + "ACME STORE",
                "Total   12.50",
                "[image 16x2]",
                "[QR data: HELLO!]",
                "[QR code]",
                "[barcode: 12345]",
                "[barcode: ABC]",
                "[drawer opened]",
                "",
                "",
                spaces(42) + "Thanks",
                "- - - - - - - - - -  cut  - - - - - - - - - -"
        };
        String want = String.join("\n", expected);
        check("a job renders as the ticket looks on paper", want.equals(text));
        if (!want.equals(text)) System.out.println(text);
        check("an empty job renders as nothing", TicketText.render(null, 0).isEmpty());
        check("a bare status query renders as nothing", TicketText.render(bytes(0x10, 0x04, 0x01), 3).isEmpty());
        StringBuilder big = new StringBuilder("X\n");
        for (int i = 0; i < 2000; i++) big.append("line of text\n");
        byte[] bigBytes = ascii(big.toString());
        String capped = TicketText.render(bigBytes, bigBytes.length, 500);
        check("long tickets are capped", capped.length() < 600 && capped.contains("truncated"));
    }

    private static String spaces(int n) {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < n; i++) sb.append(' ');
        return sb.toString();
    }

    // ------------------------------------------------------------------ licence

    private static void licenceRoundTrip() throws Exception {
        KeyPairGenerator g = KeyPairGenerator.getInstance("EC");
        g.initialize(new ECGenParameterSpec("secp256r1"));
        KeyPair kp = g.generateKeyPair();
        String pub = Base64.getEncoder().encodeToString(kp.getPublic().getEncoded());

        String[] devices = {"A1B2-C3D4-E5F6-7890", "ffff0000eeee1111"};
        byte[] payload = License.payloadFor("Test Shop", "2026-09-18", devices);
        String key = sign(kp, payload);

        License.Info info = License.check(key, pub, "a1b2c3d4e5f67890");
        check("a signed key is accepted on the first device it names",
                info != null && "Test Shop".equals(info.licensee) && "2026-09-18".equals(info.issued));
        check("the second named device is accepted too, dashes and case ignored",
                License.check(key, pub, "FFFF-0000-EEEE-1111") != null);
        check("a third device is refused", License.check(key, pub, "1234123412341234") == null);
        check("an empty device id is refused", License.check(key, pub, "") == null);
        check("inspect still reads the key for a device it does not name",
                License.inspect(key, pub) != null && License.inspect(key, pub).devices.length == 2);

        String[] parts = key.substring(License.PREFIX.length()).split("\\.");
        byte[] forged = License.payloadFor("Other Shop", "2026-09-18", devices);
        String tampered = License.PREFIX + License.encode(forged) + "." + parts[1];
        check("changing the licensee invalidates the signature", License.inspect(tampered, pub) == null);
        byte[] moreDevices = License.payloadFor("Test Shop", "2026-09-18", new String[]{"a1b2c3d4e5f67890", "1234123412341234"});
        String swapped = License.PREFIX + License.encode(moreDevices) + "." + parts[1];
        check("changing the device list invalidates the signature", License.inspect(swapped, pub) == null);
        check("a key with the wrong prefix is rejected", License.inspect("XX" + key, pub) == null);
        check("garbage is rejected without throwing", License.inspect("DSPRO.hello.world", pub) == null);
        check("an empty public key accepts nothing", License.inspect(key, "") == null);
        check("a key for a different key pair is rejected", License.inspect(key, otherPublicKey()) == null);

        boolean refusedThree = false;
        try {
            License.payloadFor("Greedy", "2026-09-18", new String[]{"a", "b", "c"});
        } catch (IllegalArgumentException e) {
            refusedThree = true;
        }
        check("the generator refuses more than two devices", refusedThree);
        // Even a payload hand-built with three devices and correctly signed is refused by the checker.
        String three = sign(kp, "2|Greedy|2026-09-18|aaaa,bbbb,cccc".getBytes(java.nio.charset.StandardCharsets.UTF_8));
        check("the checker refuses a signed key naming three devices", License.inspect(three, pub) == null);
        String v1 = sign(kp, "1|Old|2026-09-18".getBytes(java.nio.charset.StandardCharsets.UTF_8));
        check("an unbound version-1 key is no longer accepted", License.inspect(v1, pub) == null);
    }

    private static String sign(KeyPair kp, byte[] payload) throws Exception {
        Signature s = Signature.getInstance("SHA256withECDSA");
        s.initSign(kp.getPrivate());
        s.update(payload);
        return License.format(payload, s.sign());
    }

    private static void embeddedPublicKeyIsSet() {
        byte[] spki = License.decode(License.PUBLIC_KEY);
        check("License.PUBLIC_KEY holds a P-256 public key", spki != null && spki.length >= 80);
        check("the embedded key rejects a made-up key", License.check("DSPRO.MXxhfDIwMjY.AAAA", "a1b2c3d4e5f67890") == null);
    }

    private static void base64RoundTrip() {
        Random rnd = new Random(7);
        boolean ok = true;
        for (int len = 0; len < 40; len++) {
            byte[] data = new byte[len];
            rnd.nextBytes(data);
            String enc = License.encode(data);
            if (!Arrays.equals(data, License.decode(enc))) ok = false;
            String std = Base64.getEncoder().encodeToString(data);
            if (!Arrays.equals(data, License.decode(std))) ok = false;
        }
        check("base64url round-trips and standard base64 with padding decodes", ok);
        check("an impossible base64 length is rejected", License.decode("A") == null);
    }

    // ------------------------------------------------------------------ helpers

    private static byte[] run(byte[] in) {
        FooterInjector f = new FooterInjector(FOOTER);
        f.process(in, 0, in.length);
        f.finish();
        return f.takeOutput();
    }

    private static int countFooters(byte[] out) {
        int count = 0;
        for (int i = 0; i + FOOTER.length <= out.length; i++) {
            boolean match = true;
            for (int j = 0; j < FOOTER.length && match; j++) match = out[i + j] == FOOTER[j];
            if (match) count++;
        }
        return count;
    }

    private static String otherPublicKey() throws Exception {
        KeyPairGenerator g = KeyPairGenerator.getInstance("EC");
        g.initialize(new ECGenParameterSpec("secp256r1"));
        return Base64.getEncoder().encodeToString(g.generateKeyPair().getPublic().getEncoded());
    }

    // ------------------------------------------------------------------ certificate, device id, telemetry, events

    private static void derPrimitives() {
        check("OID 1.2.840.113549.1.1.11 (sha256WithRSA) encodes as in RFC 8017",
                Arrays.equals(bytes(0x06, 0x09, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x01, 0x0B), TlsCertificate.oid("1.2.840.113549.1.1.11")));
        check("OID 2.5.29.17 (subjectAltName) encodes as 06 03 55 1D 11",
                Arrays.equals(bytes(0x06, 0x03, 0x55, 0x1D, 0x11), TlsCertificate.oid("2.5.29.17")));
        check("short, one-byte and two-byte lengths",
                Arrays.equals(bytes(0x7F), TlsCertificate.length(127))
                        && Arrays.equals(bytes(0x81, 0xC8), TlsCertificate.length(200))
                        && Arrays.equals(bytes(0x82, 0x01, 0x2C), TlsCertificate.length(300)));
        check("INTEGER 2 is 02 01 02", Arrays.equals(bytes(0x02, 0x01, 0x02), TlsCertificate.integer(BigInteger.valueOf(2))));
        check("BIT STRING carries the unused-bits byte", Arrays.equals(bytes(0x03, 0x02, 0x05, 0xA0), TlsCertificate.bitstring(bytes(0xA0), 5)));
        check("SEQUENCE of NULL is 30 02 05 00", Arrays.equals(bytes(0x30, 0x02, 0x05, 0x00), TlsCertificate.seq(TlsCertificate.nul())));
    }

    private static void certificateBuildsAndVerifies() throws Exception {
        TlsCertificate.Material m = TlsCertificate.generate(Arrays.asList("192.168.1.129", "127.0.0.1"), Arrays.asList("localhost"));
        X509Certificate c = m.certificate;
        boolean verified;
        try {
            c.verify(c.getPublicKey());
            verified = true;
        } catch (Exception e) {
            verified = false;
        }
        check("the certificate parses with the platform X.509 parser and its signature verifies", verified);
        check("signed with SHA-256 and RSA", c.getSigAlgName().toUpperCase().contains("SHA256"));
        check("subject and issuer are the bridge, so it is self-signed",
                c.getSubjectX500Principal().getName().contains("CN=" + TlsCertificate.COMMON_NAME)
                        && c.getIssuerX500Principal().equals(c.getSubjectX500Principal()));
        check("version 3 with a positive serial", c.getVersion() == 3 && c.getSerialNumber().signum() > 0);
        long days = (c.getNotAfter().getTime() - c.getNotBefore().getTime()) / 86400000L;
        check("valid about ten years (" + days + " days)", days >= 3650 && days <= 3652);
        boolean validNow;
        try {
            c.checkValidity(new Date());
            validNow = true;
        } catch (Exception e) {
            validNow = false;
        }
        check("valid today", validNow);
        check("not a CA (basic constraints critical, cA false)", c.getBasicConstraints() == -1 && c.getCriticalExtensionOIDs().contains("2.5.29.19"));
        boolean[] ku = c.getKeyUsage();
        check("key usage: digitalSignature and keyEncipherment", ku != null && ku[0] && ku[2]);
        List<String> eku = c.getExtendedKeyUsage();
        check("extended key usage: serverAuth", eku != null && eku.contains("1.3.6.1.5.5.7.3.1"));
        Collection<List<?>> sans = c.getSubjectAlternativeNames();
        boolean ip = false, ip2 = false, dns = false;
        if (sans != null) {
            for (List<?> san : sans) {
                int type = (Integer) san.get(0);
                if (type == 7 && "192.168.1.129".equals(san.get(1))) ip = true;
                if (type == 7 && "127.0.0.1".equals(san.get(1))) ip2 = true;
                if (type == 2 && "localhost".equals(san.get(1))) dns = true;
            }
        }
        check("subject alternative names carry both IP addresses and localhost", ip && ip2 && dns);
        check("the DER bytes are what the parser saw", Arrays.equals(m.certificateDer, c.getEncoded()));
    }

    private static void certificateMaterialIsReusedAndRemade() throws Exception {
        File dir = Files.createTempDirectory("bridge-tls").toFile();
        TlsCertificate.Material first = TlsCertificate.load(dir, Arrays.asList("192.168.1.129"));
        TlsCertificate.Material again = TlsCertificate.load(dir, Arrays.asList("192.168.1.129"));
        check("the first load generates and stores the material", first.generated && new File(dir, "tls-cert.der").exists() && new File(dir, "tls-key.p8").exists());
        check("a second load with the same address reuses it byte for byte", !again.generated && Arrays.equals(first.certificateDer, again.certificateDer));
        TlsCertificate.Material moved = TlsCertificate.load(dir, Arrays.asList("10.0.0.7"));
        check("a new address makes a new certificate", moved.generated && !Arrays.equals(first.certificateDer, moved.certificateDer));
        check("the addresses are recorded, sorted, with loopback added", "10.0.0.7,127.0.0.1".equals(moved.addresses));
        for (File f : dir.listFiles()) f.delete();
        dir.delete();
    }

    /** A real TLS handshake against the material, with a client that trusts anything, then a look at what it was shown. */
    private static void certificateServesTls() throws Exception {
        TlsCertificate.Material m = TlsCertificate.generate(Arrays.asList("127.0.0.1"), Arrays.asList("localhost"));
        final SSLServerSocket server = (SSLServerSocket) m.serverSocketFactory().createServerSocket(0);
        final byte[][] got = new byte[1][];
        Thread t = new Thread(new Runnable() {
            @Override
            public void run() {
                try {
                    SSLSocket s = (SSLSocket) server.accept();
                    s.startHandshake();
                    s.getOutputStream().write("hi".getBytes());
                    s.getOutputStream().flush();
                    s.close();
                } catch (Exception e) {
                    got[0] = null;
                }
            }
        });
        t.start();
        SSLContext ctx = SSLContext.getInstance("TLS");
        ctx.init(null, new TrustManager[]{new X509TrustManager() {
            public void checkClientTrusted(X509Certificate[] c, String a) {
            }

            public void checkServerTrusted(X509Certificate[] c, String a) {
                got[0] = c.length > 0 ? safeEncoded(c[0]) : null;
            }

            public X509Certificate[] getAcceptedIssuers() {
                return new X509Certificate[0];
            }
        }}, null);
        SSLSocket client = (SSLSocket) ctx.getSocketFactory().createSocket("127.0.0.1", server.getLocalPort());
        client.startHandshake();
        int b = client.getInputStream().read();
        client.close();
        t.join(5000);
        server.close();
        check("a TLS client completes the handshake and receives data (" + client.getSession().getProtocol() + ")", b == 'h');
        check("the client was shown the bridge's own certificate", got[0] != null && Arrays.equals(got[0], m.certificateDer));
    }

    private static byte[] safeEncoded(X509Certificate c) {
        try {
            return c.getEncoded();
        } catch (Exception e) {
            return null;
        }
    }

    private static void eposDeviceIdRules() {
        check("empty and null ids become local_printer", "local_printer".equals(EposDeviceId.sanitize("")) && "local_printer".equals(EposDeviceId.sanitize(null)) && "local_printer".equals(EposDeviceId.sanitize("  ")));
        check("spaces and punctuation are dropped, allowed characters kept", "kitchen-1_a.b".equals(EposDeviceId.sanitize(" kitchen-1 _a.b! ")));
        check("ids are cut at 32 characters", EposDeviceId.sanitize("abcdefghijklmnopqrstuvwxyz0123456789").length() == 32);
        check("devid is read from the query string", "kitchen".equals(EposDeviceId.queryValue("/cgi-bin/epos/service.cgi?devid=kitchen&timeout=10000", "devid")));
        check("parameter names are case-insensitive and values decoded", "a b".equals(EposDeviceId.queryValue("/x?DevId=a%20b", "devid")));
        check("a missing parameter is null, an empty one is empty", EposDeviceId.queryValue("/x", "devid") == null && "".equals(EposDeviceId.queryValue("/x?devid=", "devid")));
        check("the http link carries port, path, devid and timeout",
                "http://192.168.1.129:8080/cgi-bin/epos/service.cgi?devid=kitchen&timeout=10000".equals(EposDeviceId.serviceUrl(false, "192.168.1.129", 8080, "kitchen")));
        check("default ports are omitted", "https://h/cgi-bin/epos/service.cgi?devid=local_printer&timeout=10000".equals(EposDeviceId.serviceUrl(true, "h", 443, "")));
        check("the certificate link points at /cert over https", "https://192.168.1.129:8443/cert".equals(EposDeviceId.certificateUrl("192.168.1.129", 8443)));
    }

    private static void telemetryLogWritesRows() throws Exception {
        File dir = Files.createTempDirectory("bridge-telemetry").toFile();
        TelemetryLog log = new TelemetryLog(dir);
        Date now = new Date();
        log.write(now, "61.0", "55.2", "1830", "72", "144", "3.1", "0.4", "1", "0", "31.0", "41.5", "none", "78", "Charging", "1150", "3.94", "0");
        log.noteEvent(now.getTime(), "printed: Order #1042 printed · 1.2 KB");
        log.noteEvent(now.getTime(), "warning: Cut removed \"GS V\"");
        log.write(now, "62.0", "55.3", "1832", "71", "144", "0.0", "0.0", "1", "0", "31.0", "41.6", "none", "78", "Charging", "1140", "3.94", "0");
        File f = log.fileFor(now);
        List<String> lines = Files.readAllLines(f.toPath());
        check("the file starts with the header and holds one line per row", lines.size() == 3 && lines.get(0).equals(TelemetryLog.HEADER));
        check("the first row has a timestamp, the values and an empty event column",
                lines.get(1).matches("\\d{4}-\\d{2}-\\d{2} \\d{2}:\\d{2}:\\d{2},61\\.0,55\\.2,1830,72,144,3\\.1,0\\.4,1,0,31\\.0,41\\.5,none,78,Charging,1150,3\\.94,0,\"\""));
        check("the events noted since the previous row land in the last column, quotes doubled",
                lines.get(2).endsWith(",\"" + new java.text.SimpleDateFormat("HH:mm:ss", java.util.Locale.US).format(now) + " printed: Order #1042 printed · 1.2 KB | "
                        + new java.text.SimpleDateFormat("HH:mm:ss", java.util.Locale.US).format(now) + " warning: Cut removed \"\"GS V\"\"\""));
        check("files() lists it and bytesToday() counts it", log.files().size() == 1 && log.bytesToday() == f.length());
        check("clear() deletes every telemetry file", log.clear() == 1 && log.files().isEmpty() && !f.exists());
        dir.delete();
    }

    private static void eventLogKeepsNewestFirst() {
        EventLog.clear();
        final List<EventLog.Event> seen = new ArrayList<>();
        EventLog.Listener l = new EventLog.Listener() {
            @Override
            public void onEvent(EventLog.Event e) {
                seen.add(e);
            }
        };
        EventLog.addListener(l);
        EventLog.printed("first");
        EventLog.warning("second");
        EventLog.failed("third");
        List<EventLog.Event> all = EventLog.snapshot();
        check("events are kept newest first with their kind", all.size() == 3 && "third".equals(all.get(0).text) && all.get(0).kind == EventLog.Kind.FAILED && all.get(2).kind == EventLog.Kind.PRINTED);
        check("listeners hear every event", seen.size() == 3);
        for (int i = 0; i < 250; i++) EventLog.offline("bulk " + i);
        check("the list is capped at 200", EventLog.count() == 200 && "bulk 249".equals(EventLog.snapshot().get(0).text));
        EventLog.removeListener(l);
        EventLog.clear();
    }

    private static final class MemoryTarget implements PrintTarget {
        private final ByteArrayOutputStream sink = new ByteArrayOutputStream();

        byte[] bytes() {
            return sink.toByteArray();
        }

        @Override
        public String getName() {
            return "memory";
        }

        @Override
        public Job startJob(String documentName) {
            return new Job() {
                @Override
                public void write(byte[] buffer, int offset, int count) {
                    sink.write(buffer, offset, count);
                }

                @Override
                public void complete() {
                }

                @Override
                public void abort() {
                }

                @Override
                public void close() {
                }
            };
        }
    }

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
        return s.getBytes(java.nio.charset.StandardCharsets.US_ASCII);
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
