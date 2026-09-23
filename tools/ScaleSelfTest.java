import com.usblanbridge.core.ScaleReader;
import com.usblanbridge.core.ScaleReading;
import com.usblanbridge.core.ScaleServer;

import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.charset.Charset;

/**
 * Desktop self-test for the Android scale core (ScaleReading parser + exact EU/US units, the TCP reader, and the
 * /scale endpoint), mirroring the Windows tests. No Android device needed; these classes are Android-free apart
 * from the Log wrapper, which the tools/stubs stand-in satisfies. Exit code is the number of failures.
 */
public final class ScaleSelfTest {

    private static int passed;
    private static int failed;
    private static final Charset ASCII = Charset.forName("US-ASCII");

    public static void main(String[] args) throws Exception {
        parserFormats();
        unitConversion();
        endToEndTcp();

        System.out.println();
        System.out.println("Scale self-test: " + passed + " passed, " + failed + " failed.");
        System.exit(failed);
    }

    private static void parserFormats() {
        reading("ST,GS,+  1.234kg", true, 1.234, "kg", true);
        reading("US,GS,+  0.500 kg", true, 0.5, "kg", false);
        reading("+001.250 kg", true, 1.25, "kg", true);
        reading("  2.000kg ", true, 2.0, "kg", true);
        reading("0.750 lb", true, 0.75, "lb", true);
        reading("1,234kg", true, 1.234, "kg", true);
        reading("-0.005 kg", true, -0.005, "kg", true);
        reading("W  12.34 oz", true, 12.34, "oz", true);
        check(!ScaleReading.parse("no weight here").ok, "a line with no number must not parse");
        check(ScaleReading.parse("weird").raw.equals("weird"), "the raw line is preserved");
    }

    private static void unitConversion() {
        check(round10(ScaleReading.convert(1, "kg", "lb")) == 2.2046226218, "1 kg -> lb");
        check(ScaleReading.convert(1, "lb", "kg") == 0.45359237, "1 lb -> kg");
        check(ScaleReading.convert(1, "lb", "oz") == 16.0, "1 lb -> oz");
        check(ScaleReading.convert(1000, "g", "kg") == 1.0, "1000 g -> kg");
        check(Double.isNaN(ScaleReading.convert(1, "kg", "furlong")), "unknown unit does not convert");

        ScaleReading kg = ScaleReading.parse("2.000 kg");
        check(kg.hasGrams && kg.grams == 2000.0, "canonical grams for 2 kg");
        check(kg.toJson().contains("\"grams\":2000"), "grams published in JSON");

        ScaleReading asLb = kg.inUnit("lb");
        check(asLb.unit.equals("lb") && Math.abs(asLb.weight - 4.4092) < 1e-9, "2 kg shown as lb = " + asLb.weight);
        check(asLb.grams == 2000.0, "grams stays canonical across a unit view");

        ScaleReading noUnit = ScaleReading.parse("1.500");
        check(noUnit.ok && !noUnit.hasGrams, "a reading with no unit has no canonical grams");
        check(noUnit.inUnit("kg").unit.isEmpty(), "a reading with no unit cannot be converted");
    }

    private static void endToEndTcp() throws Exception {
        ServerSocket listener = new ServerSocket();
        listener.bind(new InetSocketAddress("127.0.0.1", 0));
        final int scalePort = listener.getLocalPort();
        final boolean[] stop = { false };
        Thread feeder = new Thread(() -> {
            try (Socket c = listener.accept()) {
                OutputStream os = c.getOutputStream();
                byte[] line = "ST,GS,+  1.234kg\r\n".getBytes(ASCII);
                while (!stop[0]) { os.write(line); os.flush(); Thread.sleep(80); }
            } catch (Exception ignored) { }
        });
        feeder.setDaemon(true);
        feeder.start();

        ScaleReader reader = new ScaleReader("127.0.0.1", scalePort);
        reader.start();
        ScaleServer server = null;
        try {
            long deadline = System.currentTimeMillis() + 6000;
            while (System.currentTimeMillis() < deadline && !(reader.current().ok && reader.current().weight == 1.234)) Thread.sleep(50);
            check(reader.current().ok, "the reader received no weight from the fake scale");
            check(reader.current().weight == 1.234, "weight was " + reader.current().weight);
            check(reader.current().unit.equals("kg"), "unit was '" + reader.current().unit + "'");
            check(reader.current().stable, "the reading should be stable");
            check(reader.isConnected(), "the reader should report connected");

            server = new ScaleServer(new ScaleServer.Provider() {
                public ScaleReading current() { return reader.current(); }
                public String[] raw() { return reader.recentRaw(); }
                public String status() { return reader.status(); }
                public boolean connected() { return reader.isConnected(); }
            });
            server.start(0);
            int httpPort = server.port();

            String resp = get("127.0.0.1", httpPort, "/scale");
            check(resp.contains("\"weight\":1.234"), "GET /scale weight: " + resp);
            check(resp.contains("\"unit\":\"kg\""), "GET /scale unit");
            check(resp.contains("\"grams\":1234"), "GET /scale canonical grams");
            check(resp.contains("\"stable\":true"), "GET /scale stable");

            String raw = get("127.0.0.1", httpPort, "/scale/raw");
            check(raw.contains("1.234kg"), "GET /scale/raw raw line: " + raw);
        } finally {
            if (server != null) server.stop();
            reader.stop();
            stop[0] = true;
            try { listener.close(); } catch (Exception ignored) { }
        }
    }

    private static String get(String host, int port, String path) throws Exception {
        try (Socket c = new Socket(host, port)) {
            OutputStream os = c.getOutputStream();
            os.write(("GET " + path + " HTTP/1.1\r\nHost: " + host + "\r\nConnection: close\r\n\r\n").getBytes(ASCII));
            os.flush();
            InputStream in = c.getInputStream();
            ByteArrayOutputStream bos = new ByteArrayOutputStream();
            byte[] buf = new byte[1024];
            int n;
            while ((n = in.read(buf)) > 0) bos.write(buf, 0, n);
            String all = new String(bos.toByteArray(), Charset.forName("UTF-8"));
            int idx = all.indexOf("\r\n\r\n");
            return idx >= 0 ? all.substring(idx + 4) : all;
        }
    }

    private static void reading(String line, boolean ok, double weight, String unit, boolean stable) {
        ScaleReading r = ScaleReading.parse(line);
        check(r.ok == ok, "parse ok for '" + line + "'");
        if (!ok) return;
        check(r.weight == weight, "weight " + r.weight + " != " + weight + " for '" + line + "'");
        check(r.unit.equals(unit), "unit '" + r.unit + "' != '" + unit + "' for '" + line + "'");
        check(r.stable == stable, "stable " + r.stable + " != " + stable + " for '" + line + "'");
    }

    private static double round10(double v) { return Math.round(v * 1e10) / 1e10; }

    private static void check(boolean cond, String what) {
        if (cond) { passed++; System.out.println("PASS  " + what); }
        else { failed++; System.out.println("FAIL  " + what); }
    }
}
