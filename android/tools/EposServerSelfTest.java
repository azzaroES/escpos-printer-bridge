import com.usblanbridge.core.EposDeviceId;
import com.usblanbridge.core.EposHttpServer;
import com.usblanbridge.core.EventLog;
import com.usblanbridge.core.Log;
import com.usblanbridge.core.PrintHistory;
import com.usblanbridge.core.PrintTarget;
import com.usblanbridge.core.TlsCertificate;

import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.Socket;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.security.cert.X509Certificate;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.Map;

import javax.net.ssl.HostnameVerifier;
import javax.net.ssl.HttpsURLConnection;
import javax.net.ssl.SSLContext;
import javax.net.ssl.SSLSession;
import javax.net.ssl.TrustManager;
import javax.net.ssl.X509TrustManager;

/**
 * Desktop self-test for the phone's ePOS-Print endpoint, plain and over HTTPS, with Android's logcat stubbed.
 * It starts both servers on free ports and talks to them the way a browser or the Epson SDK would: the CORS
 * preflight, a print with the right and the wrong device id, the certificate page and download over both
 * schemes, and the two classic mistakes (HTTPS spoken to the HTTP port, HTTP spoken to the HTTPS port).
 */
public final class EposServerSelfTest {

    private static int passed;
    private static int failed;
    private static final List<String> LOG = new ArrayList<>();

    private static final String EPOS_XML = "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
            + "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body>"
            + "<epos-print xmlns=\"http://www.epson-pos.com/schemas/2011/03/epos-print\">"
            + "<text>Order #1042&#10;</text><feed line=\"2\"/><cut type=\"feed\"/>"
            + "</epos-print></s:Body></s:Envelope>";

    public static void main(String[] args) throws Exception {
        // Java's HttpURLConnection refuses to send Origin and the Access-Control-Request-* headers unless told otherwise.
        System.setProperty("sun.net.http.allowRestrictedHeaders", "true");
        Log.addListener(new Log.Listener() {
            @Override
            public void onLine(String line) {
                synchronized (LOG) {
                    LOG.add(line);
                }
            }
        });
        final String[] deviceId = {"local_printer"};
        EposHttpServer.DeviceIdSource ids = new EposHttpServer.DeviceIdSource() {
            @Override
            public String deviceId() {
                return deviceId[0];
            }
        };
        MemoryTarget target = new MemoryTarget();
        TlsCertificate.Material material = TlsCertificate.generate(Arrays.asList("127.0.0.1"), Arrays.asList("localhost"));

        EposHttpServer http = new EposHttpServer(target, ids, null, material.certificateDer);
        EposHttpServer https = new EposHttpServer(target, ids, material.serverSocketFactory(), material.certificateDer);
        http.start(0);
        https.start(0);
        http.setHttpsPort(https.port());
        int hp = http.port(), sp = https.port();
        check("both servers took a free port (http " + hp + ", https " + sp + ")", hp > 0 && sp > 0 && hp != sp);
        trustEverything();

        try {
            // plain page
            Response r = get("http://127.0.0.1:" + hp + "/");
            check("GET / answers 200 with the bridge page and the device id", r.status == 200 && r.text().contains("USB LAN Printer Bridge") && r.text().contains("devid=local_printer"));

            // CORS preflight with private network access
            r = request("OPTIONS", "http://127.0.0.1:" + hp + EposDeviceId.SERVICE_PATH + "?devid=local_printer", null,
                    "Origin", "https://pos.example", "Access-Control-Request-Method", "POST", "Access-Control-Request-Private-Network", "true");
            check("preflight answers 204 with the origin echoed and private network allowed",
                    r.status == 204 && "https://pos.example".equals(r.header("Access-Control-Allow-Origin")) && "true".equals(r.header("Access-Control-Allow-Private-Network")));

            // print with the right id
            int before = PrintHistory.count();
            r = request("POST", "http://127.0.0.1:" + hp + EposDeviceId.SERVICE_PATH + "?devid=local_printer&timeout=10000", EPOS_XML, "Content-Type", "text/xml; charset=utf-8", "Origin", "https://pos.example");
            check("a print with the right device id succeeds with the real Epson status word",
                    r.status == 200 && r.text().contains("success=\"true\"") && r.text().contains("status=\"251658262\"") && "https://pos.example".equals(r.header("Access-Control-Allow-Origin")));
            byte[] printed = target.bytes();
            check("the ticket reached the printer as ESC/POS with the text and a cut", printed.length > 10 && new String(printed, StandardCharsets.ISO_8859_1).contains("Order #1042") && indexOf(printed, new byte[]{0x1D, 0x56}) >= 0);
            check("the job is in the print history", PrintHistory.count() == before + 1);
            check("a print without any devid is accepted too (SDK default)", request("POST", "http://127.0.0.1:" + hp + EposDeviceId.SERVICE_PATH, EPOS_XML).text().contains("success=\"true\""));

            // wrong id
            int len = target.bytes().length;
            EventLog.clear();
            r = request("POST", "http://127.0.0.1:" + hp + EposDeviceId.SERVICE_PATH + "?devid=kitchen", EPOS_XML, "Content-Type", "text/xml");
            check("another device id is refused with DeviceNotFound and nothing is printed",
                    r.status == 200 && r.text().contains("success=\"false\"") && r.text().contains("code=\"DeviceNotFound\"") && target.bytes().length == len);
            check("the refusal is a printer event", EventLog.count() == 1 && EventLog.snapshot().get(0).text.contains("kitchen"));
            deviceId[0] = "kitchen";
            r = request("POST", "http://127.0.0.1:" + hp + EposDeviceId.SERVICE_PATH + "?devid=kitchen", EPOS_XML, "Content-Type", "text/xml");
            check("after the id is changed in the settings the same request prints at once (no restart)", r.text().contains("success=\"true\"") && target.bytes().length > len);
            r = request("POST", "http://127.0.0.1:" + hp + EposDeviceId.SERVICE_PATH + "?devid=local_printer", EPOS_XML, "Content-Type", "text/xml");
            check("and local_printer is now the one refused", r.text().contains("DeviceNotFound"));
            deviceId[0] = "local_printer";

            // certificate page and download over http
            r = get("http://127.0.0.1:" + hp + "/cert");
            check("GET /cert over http points at the https certificate page", r.status == 200 && r.text().contains("https://127.0.0.1:" + sp + "/cert") && r.text().contains("Trust this bridge"));
            r = get("http://127.0.0.1:" + hp + "/cert/UsbLanPrinterBridge.cer");
            check("the .cer download is the certificate in DER with the right content type",
                    r.status == 200 && "application/x-x509-ca-cert".equals(r.header("Content-Type")) && Arrays.equals(r.body, material.certificateDer)
                            && r.header("Content-Disposition") != null && r.header("Content-Disposition").contains("UsbLanPrinterBridge.cer"));

            // over https
            r = get("https://127.0.0.1:" + sp + "/cert");
            check("GET /cert over https confirms the device now trusts the bridge and shows the https endpoint",
                    r.status == 200 && r.text().contains("This device now trusts the bridge") && r.text().contains("https://127.0.0.1:" + sp + EposDeviceId.SERVICE_PATH + "?devid=local_printer"));
            len = target.bytes().length;
            r = request("POST", "https://127.0.0.1:" + sp + EposDeviceId.SERVICE_PATH + "?devid=local_printer", EPOS_XML, "Content-Type", "text/xml");
            check("a print over https succeeds", r.status == 200 && r.text().contains("success=\"true\"") && target.bytes().length > len);
            check("the https job is recorded with its scheme", "ePOS https".equals(PrintHistory.snapshot().get(PrintHistory.count() - 1).path));

            // https spoken to the http port
            LOG.clear();
            EventLog.clear();
            Socket s = new Socket("127.0.0.1", hp);
            s.setSoTimeout(5000);
            byte[] hello = new byte[]{0x16, 0x03, 0x01, 0x00, 0x05, 0x01, 0x00, 0x00, 0x01, 0x00};
            s.getOutputStream().write(hello);
            s.getOutputStream().flush();
            byte[] reply = readAll(s.getInputStream());
            s.close();
            String replyText = new String(reply, StandardCharsets.ISO_8859_1);
            check("a TLS ClientHello on the http port gets a 400 telling the client which scheme to use", replyText.startsWith("HTTP/1.1 400") && replyText.contains("https://<phone address>:" + sp));
            check("and the log says a client spoke HTTPS to the plain-HTTP port", logContains("spoke HTTPS to the plain-HTTP port " + hp) && EventLog.count() == 1);

            // plain http spoken to the https port
            LOG.clear();
            Socket p = new Socket("127.0.0.1", sp);
            p.setSoTimeout(5000);
            p.getOutputStream().write("GET / HTTP/1.1\r\nHost: x\r\n\r\n".getBytes(StandardCharsets.US_ASCII));
            p.getOutputStream().flush();
            byte[] junk = readAll(p.getInputStream());
            p.close();
            Thread.sleep(300);
            check("plain http on the https port fails the handshake and the log points at /cert", logContains("TLS handshake with") && logContains("/cert"));

            // request line logged on failure
            LOG.clear();
            r = request("POST", "http://127.0.0.1:" + hp + EposDeviceId.SERVICE_PATH, "<not xml", "Content-Type", "text/xml");
            check("a broken document is rejected as SchemaError with the reason logged", r.text().contains("code=\"SchemaError\"") && logContains("ePOS document rejected"));

            check("request and job counters add up", http.getRequests() >= 8 && http.getJobs() == 3 && https.getJobs() == 1);
        } finally {
            http.stop();
            https.stop();
        }

        System.out.println();
        System.out.println("Passed: " + passed + "   Failed: " + failed);
        System.exit(failed);
    }

    // ------------------------------------------------------------------ helpers

    private static boolean logContains(String needle) {
        synchronized (LOG) {
            for (String l : LOG) if (l.contains(needle)) return true;
        }
        return false;
    }

    private static int indexOf(byte[] hay, byte[] needle) {
        outer:
        for (int i = 0; i <= hay.length - needle.length; i++) {
            for (int j = 0; j < needle.length; j++) if (hay[i + j] != needle[j]) continue outer;
            return i;
        }
        return -1;
    }

    private static byte[] readAll(InputStream in) {
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        try {
            byte[] buf = new byte[4096];
            int n;
            while ((n = in.read(buf)) > 0) out.write(buf, 0, n);
        } catch (Exception ignored) {
        }
        return out.toByteArray();
    }

    private static final class Response {
        int status;
        byte[] body = new byte[0];
        Map<String, List<String>> headers;

        String text() {
            return new String(body, StandardCharsets.UTF_8);
        }

        String header(String name) {
            if (headers == null) return null;
            for (Map.Entry<String, List<String>> e : headers.entrySet()) {
                if (e.getKey() != null && e.getKey().equalsIgnoreCase(name) && !e.getValue().isEmpty()) return e.getValue().get(0);
            }
            return null;
        }
    }

    private static Response get(String url) throws Exception {
        return request("GET", url, null);
    }

    private static Response request(String method, String url, String body, String... headers) throws Exception {
        HttpURLConnection c = (HttpURLConnection) new URL(url).openConnection();
        c.setRequestMethod(method);
        c.setConnectTimeout(5000);
        c.setReadTimeout(10000);
        c.setInstanceFollowRedirects(false);
        for (int i = 0; i + 1 < headers.length; i += 2) c.setRequestProperty(headers[i], headers[i + 1]);
        if (body != null) {
            c.setDoOutput(true);
            OutputStream out = c.getOutputStream();
            out.write(body.getBytes(StandardCharsets.UTF_8));
            out.close();
        }
        Response r = new Response();
        r.status = c.getResponseCode();
        r.headers = c.getHeaderFields();
        InputStream in = r.status >= 400 ? c.getErrorStream() : c.getInputStream();
        if (in != null) r.body = readAll(in);
        c.disconnect();
        return r;
    }

    private static void trustEverything() throws Exception {
        SSLContext ctx = SSLContext.getInstance("TLS");
        ctx.init(null, new TrustManager[]{new X509TrustManager() {
            public void checkClientTrusted(X509Certificate[] c, String a) {
            }

            public void checkServerTrusted(X509Certificate[] c, String a) {
            }

            public X509Certificate[] getAcceptedIssuers() {
                return new X509Certificate[0];
            }
        }}, null);
        HttpsURLConnection.setDefaultSSLSocketFactory(ctx.getSocketFactory());
        HttpsURLConnection.setDefaultHostnameVerifier(new HostnameVerifier() {
            public boolean verify(String h, SSLSession s) {
                return true;
            }
        });
    }

    private static final class MemoryTarget implements PrintTarget {
        private final ByteArrayOutputStream sink = new ByteArrayOutputStream();

        synchronized byte[] bytes() {
            return sink.toByteArray();
        }

        @Override
        public String getName() {
            return "memory printer";
        }

        @Override
        public Job startJob(String documentName) {
            return new Job() {
                @Override
                public void write(byte[] buffer, int offset, int count) {
                    synchronized (MemoryTarget.this) {
                        sink.write(buffer, offset, count);
                    }
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
}
