package com.usblanbridge.core;

import java.io.BufferedInputStream;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.net.SocketTimeoutException;
import java.nio.charset.Charset;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.concurrent.atomic.AtomicLong;

import javax.net.ssl.SSLServerSocketFactory;

/**
 * Emulates an Epson ePOS-Print device so browser and Android POS apps written against the Epson ePOS SDK can
 * print to whatever printer this phone is sharing.
 *
 * Note the ports. An unrooted Android app cannot bind ports below 1024, so this cannot listen on 80 or 443 the
 * way the Windows build does. Clients must include the port in the URL: plain HTTP on 8080, and HTTPS on 8443
 * when the server is given a TLS factory. Raw printing on 9100 has no such restriction, which is why that
 * remains the path that works from an IP address alone.
 *
 * Responses mirror a real Epson TM printer byte for byte, including status 251658262. The device id in the URL
 * (devid) must be the one the phone answers to; another id gets DeviceNotFound, as a real printer answers.
 */
public final class EposHttpServer {

    /** The status word a real Epson returns when all is well, 0x0F000016. */
    public static final int OK_STATUS = 251658262;
    public static final String EPOS_NAMESPACE = EposPrintConverter.EPOS_NAMESPACE;
    public static final String DEFAULT_DEVICE_ID = "local_printer";

    /** Asked per request which device id this endpoint answers to. */
    public interface DeviceIdSource {
        String deviceId();
    }

    private static final Charset UTF8 = Charset.forName("UTF-8");
    private static final Charset ASCII = Charset.forName("US-ASCII");

    private final PrintTarget target;
    private final DeviceIdSource deviceId;
    private final SSLServerSocketFactory tls;
    private final byte[] certificateDer;
    private final AtomicLong requests = new AtomicLong();
    private final AtomicLong jobs = new AtomicLong();

    private volatile ServerSocket serverSocket;
    private volatile boolean running;
    private volatile int port;
    private volatile int httpsPort;

    public EposHttpServer(PrintTarget target) {
        this(target, null, null, null);
    }

    /**
     * @param deviceId       which device id to answer; null answers any
     * @param tls            a TLS server socket factory for an HTTPS endpoint; null for plain HTTP
     * @param certificateDer the public certificate served at /cert/UsbLanPrinterBridge.cer; may be null
     */
    public EposHttpServer(PrintTarget target, DeviceIdSource deviceId, SSLServerSocketFactory tls, byte[] certificateDer) {
        this.target = target;
        this.deviceId = deviceId;
        this.tls = tls;
        this.certificateDer = certificateDer;
    }

    public boolean isRunning() {
        return running;
    }

    public boolean isSecure() {
        return tls != null;
    }

    public String scheme() {
        return tls != null ? "https" : "http";
    }

    public int port() {
        return port;
    }

    /** Tells the plain-HTTP server where the HTTPS twin lives, for its hints and the /cert page. */
    public void setHttpsPort(int httpsPort) {
        this.httpsPort = httpsPort;
    }

    public long getRequests() {
        return requests.get();
    }

    public long getJobs() {
        return jobs.get();
    }

    /** Keeps only the characters a device id may contain; empty becomes the default. */
    public static String sanitizeDeviceId(String id) {
        return EposDeviceId.sanitize(id);
    }

    /** The value of a query parameter in a request path, or null. */
    public static String queryValue(String path, String name) {
        return EposDeviceId.queryValue(path, name);
    }

    public void start(int listenPort) throws IOException {
        if (running) return;
        ServerSocket ss = tls != null ? tls.createServerSocket() : new ServerSocket();
        ss.setReuseAddress(true);
        ss.bind(new InetSocketAddress(listenPort), 32);
        serverSocket = ss;
        port = ss.getLocalPort();
        running = true;
        Thread t = new Thread(new Runnable() {
            @Override
            public void run() {
                acceptLoop();
            }
        }, tls != null ? "epos-https-accept" : "epos-accept");
        t.setDaemon(true);
        t.start();
        Log.i("ePOS-Print ready on " + scheme() + " port " + port + " at /cgi-bin/epos/service.cgi");
    }

    public void stop() {
        running = false;
        ServerSocket ss = serverSocket;
        serverSocket = null;
        if (ss != null) {
            try {
                ss.close();
            } catch (Throwable ignored) {
            }
        }
    }

    private void acceptLoop() {
        while (running) {
            final Socket socket;
            try {
                ServerSocket ss = serverSocket;
                if (ss == null) break;
                socket = ss.accept();
            } catch (IOException e) {
                if (running) Log.w("ePOS accept failed: " + e.getMessage());
                break;
            }
            Thread t = new Thread(new Runnable() {
                @Override
                public void run() {
                    handle(socket);
                }
            }, "epos-client");
            t.setDaemon(true);
            t.start();
        }
    }

    private void handle(Socket socket) {
        String remote = String.valueOf(socket.getRemoteSocketAddress());
        Request req = null;
        try {
            socket.setSoTimeout(15000);
            if (tls != null) {
                try {
                    ((javax.net.ssl.SSLSocket) socket).startHandshake();
                } catch (IOException tlsFailure) {
                    Log.w("TLS handshake with " + remote + " failed: " + tlsFailure.getMessage()
                            + ". A device that does not trust the certificate yet? Open https://<phone address>:" + port + "/cert on it once and accept the warning.");
                    return;
                }
            }
            BufferedInputStream in = new BufferedInputStream(socket.getInputStream(), 8192);
            OutputStream out = socket.getOutputStream();

            if (tls == null) {
                // A TLS ClientHello always starts with 0x16. Reading it as HTTP would just time out after 15 s
                // while the client waits for a ServerHello, so say what happened instead.
                in.mark(1);
                int first = in.read();
                in.reset();
                if (first == 0x16) {
                    Log.w("Client " + remote + " spoke HTTPS to the plain-HTTP port " + port + ". "
                            + (httpsPort > 0 ? "Use https://<phone address>:" + httpsPort + "/cgi-bin/epos/service.cgi for HTTPS, or http:// on this port."
                            : "This port speaks plain HTTP; use http://, or enable HTTPS in the app."));
                    EventLog.warning("ePOS client " + remote + " used https on the http port " + port + (httpsPort > 0 ? "; HTTPS is on " + httpsPort : ""));
                    write(out, 400, "Bad Request", "text/plain; charset=utf-8", ("This port speaks plain HTTP. Use http://, or https://<phone address>:" + (httpsPort > 0 ? httpsPort : 8443) + "/ for HTTPS.").getBytes(UTF8), null);
                    return;
                }
            }

            req = Request.read(in);
            if (req == null) return;
            requests.incrementAndGet();

            String origin = req.header("Origin");

            if ("OPTIONS".equals(req.method)) {
                List<String> extra = new ArrayList<>();
                extra.add("Access-Control-Allow-Origin: " + (origin == null ? "*" : origin));
                extra.add("Vary: Origin");
                extra.add("Access-Control-Allow-Methods: GET, POST, OPTIONS");
                String want = req.header("Access-Control-Request-Headers");
                extra.add("Access-Control-Allow-Headers: " + (want == null ? "Content-Type, SOAPAction, If-Modified-Since" : want));
                extra.add("Access-Control-Max-Age: 86400");
                // Chrome requires this when a public https page calls a private LAN address.
                if ("true".equalsIgnoreCase(req.header("Access-Control-Request-Private-Network"))) {
                    extra.add("Access-Control-Allow-Private-Network: true");
                }
                write(out, 204, "No Content", null, new byte[0], extra);
                return;
            }

            String pathOnly = req.path;
            int q = pathOnly.indexOf('?');
            if (q >= 0) pathOnly = pathOnly.substring(0, q);
            while (pathOnly.length() > 1 && pathOnly.endsWith("/")) pathOnly = pathOnly.substring(0, pathOnly.length() - 1);

            boolean isEpos = req.path.contains("/cgi-bin/epos") || req.path.contains("service.cgi");
            if ("POST".equals(req.method) && isEpos) {
                handleEpos(out, req, origin, remote);
                return;
            }

            if ("GET".equals(req.method)) {
                if (pathOnly.equalsIgnoreCase("/cert")) {
                    write(out, 200, "OK", "text/html; charset=utf-8", certPage(req).getBytes(UTF8), cors(origin));
                    return;
                }
                if (pathOnly.toLowerCase(Locale.US).startsWith("/cert") && pathOnly.toLowerCase(Locale.US).endsWith(".cer")) {
                    if (certificateDer == null) {
                        write(out, 404, "Not Found", "text/plain; charset=utf-8", "No certificate yet.".getBytes(UTF8), cors(origin));
                    } else {
                        List<String> h = cors(origin);
                        h.add("Content-Disposition: attachment; filename=\"UsbLanPrinterBridge.cer\"");
                        write(out, 200, "OK", "application/x-x509-ca-cert", certificateDer, h);
                    }
                    return;
                }
                String html = "<!doctype html><meta charset=utf-8><title>USB LAN Printer Bridge</title>"
                        + "<body style='font-family:sans-serif;margin:2em'>"
                        + "<h2>USB LAN Printer Bridge</h2><p>Sharing <b>" + escape(target.getName()) + "</b>.</p>"
                        + "<p>ePOS-Print endpoint: <code>/cgi-bin/epos/service.cgi?devid=" + escape(wantedDeviceId()) + "</code></p>"
                        + "<p>Requests: " + getRequests() + ", jobs: " + getJobs() + "</p></body>";
                write(out, 200, "OK", "text/html; charset=utf-8", html.getBytes(UTF8), cors(origin));
                return;
            }

            write(out, 404, "Not Found", "text/plain; charset=utf-8", "Not found".getBytes(UTF8), cors(origin));
        } catch (SocketTimeoutException timeout) {
            Log.w("ePOS request from " + remote + " timed out before it was complete"
                    + (req == null ? " (no full request line arrived; a client waiting for something this port does not speak?)" : " after " + req.method + " " + req.path));
        } catch (Exception e) {
            Log.w("ePOS request from " + remote + (req == null ? "" : " (" + req.method + " " + req.path + ")") + " failed: " + e);
        } finally {
            try {
                socket.close();
            } catch (Throwable ignored) {
            }
        }
    }

    private String wantedDeviceId() {
        return sanitizeDeviceId(deviceId == null ? DEFAULT_DEVICE_ID : deviceId.deviceId());
    }

    private void handleEpos(OutputStream out, Request req, String origin, String remote) throws IOException {
        String xml = new String(req.body, UTF8);
        String printJobId = extractPrintJobId(xml);
        String responseXml;

        String wanted = deviceId == null ? null : wantedDeviceId();
        String asked = queryValue(req.path, "devid");
        if (wanted != null && asked != null && asked.length() > 0 && !asked.equalsIgnoreCase(wanted)) {
            Log.w("ePOS request from " + remote + " asked for device id \"" + asked + "\"; this phone answers \"" + wanted + "\". Replied DeviceNotFound.");
            EventLog.warning("ePOS request refused: device id \"" + asked + "\" (this phone answers \"" + wanted + "\")");
            write(out, 200, "OK", "text/xml; charset=utf-8", buildResponse(false, "DeviceNotFound", 0, printJobId).getBytes(UTF8), cors(origin));
            return;
        }

        try {
            EposPrintConverter.Result result = EposPrintConverter.convert(xml);
            PrintTarget.Job job = target.startJob("ePOS " + remote);
            try {
                job.write(result.bytes, 0, result.bytes.length);
                job.complete();
            } catch (IOException e) {
                job.abort();
                throw e;
            }
            jobs.incrementAndGet();
            PrintHistory.add(remote, target.getName(), "ePOS " + scheme(), result.bytes, result.bytes.length, "Printed");
            Log.i("ePOS job printed: " + RawServer.formatBytes(result.bytes.length) + " from " + remote
                    + (result.warnings.isEmpty() ? "" : ", " + result.warnings.size() + " warning(s)"));
            for (String w : result.warnings) Log.w("ePOS: " + w);
            responseXml = buildResponse(true, "", OK_STATUS, printJobId);
        } catch (IllegalArgumentException bad) {
            Log.w("ePOS document rejected from " + remote + ": " + bad.getMessage());
            byte[] raw = xml.getBytes(UTF8);
            PrintHistory.add(remote, target.getName(), "ePOS " + scheme(), raw, raw.length, "Rejected: " + bad.getMessage());
            responseXml = buildResponse(false, "SchemaError", OK_STATUS, printJobId);
        } catch (Exception e) {
            Log.e("ePOS print from " + remote + " failed", e);
            byte[] raw = xml.getBytes(UTF8);
            PrintHistory.add(remote, target.getName(), "ePOS " + scheme(), raw, raw.length, "Failed: " + e.getMessage());
            responseXml = buildResponse(false, "EPTR_PRINT_SYSTEM_ERROR", OK_STATUS, printJobId);
        }

        write(out, 200, "OK", "text/xml; charset=utf-8", responseXml.getBytes(UTF8), cors(origin));
    }

    /**
     * The page a client device opens once. Over HTTPS, reaching it means the warning was accepted, so it says
     * the device now trusts the bridge; over HTTP it points at the https link.
     */
    private String certPage(Request req) {
        String host = req.header("Host");
        if (host == null || host.isEmpty()) host = "<phone address>:" + port;
        String hostOnly = host;
        int colon = hostOnly.lastIndexOf(':');
        if (colon > 0) hostOnly = hostOnly.substring(0, colon);
        String devid = wantedDeviceId();
        String endpoint = scheme() + "://" + host + "/cgi-bin/epos/service.cgi?devid=" + devid + "&timeout=10000";
        String cer = scheme() + "://" + host + "/cert/UsbLanPrinterBridge.cer";
        String body;
        if (tls != null) {
            body = "<div class=ok><svg width=40 height=40 viewBox='0 0 40 40'><circle cx=20 cy=20 r=19 fill='#dcf3dc'/><path d='M12 21 L17.5 26.5 L28 15' fill=none stroke='#0a5a0a' stroke-width=3.5 stroke-linecap=round stroke-linejoin=round/></svg>"
                    + "<h1>This device now trusts the bridge</h1></div>"
                    + "<p>Because you accepted the warning, apps and web pages on this device can print through<br><b>" + escape(endpoint) + "</b><br>with device id <b>" + escape(devid) + "</b>. Nothing else to do here. The trust is remembered by this browser for this address.</p>";
        } else {
            String https = "https://" + hostOnly + ":" + (httpsPort > 0 ? httpsPort : 8443) + "/cert";
            body = "<h1>Trust this bridge on this device</h1>"
                    + "<p>You opened the plain http address. To make this device trust the phone's certificate, open <a href='" + escape(https) + "'><b>" + escape(https) + "</b></a> and accept the warning once. Printing over plain http works without that: <b>" + escape(endpoint) + "</b>, device id <b>" + escape(devid) + "</b>.</p>";
        }
        return "<!doctype html><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'><title>USB LAN Printer Bridge — certificate</title>"
                + "<body style='font-family:system-ui,sans-serif;margin:0;color:#1b1b1b'><div style='max-width:640px;margin:0 auto;padding:36px 28px'>"
                + "<div style='font-size:13px;color:#52514e;margin-bottom:18px'>USB LAN Printer Bridge on this phone · " + escape(hostOnly) + "</div>"
                + "<style>.ok{display:flex;align-items:center;gap:14px}h1{font-size:24px;margin:0}p{font-size:15px;line-height:1.55}.box{border:1px solid #e3e3e0;border-radius:8px;padding:14px 16px;background:#fcfcfb;margin-top:18px}.btn{display:inline-block;background:#1d4ed8;color:#fff;text-decoration:none;font-weight:600;padding:9px 14px;border-radius:6px;margin-top:8px}</style>"
                + body
                + "<div class=box><b>Prefer no warning at all, on every browser and app on this device?</b><p style='margin:8px 0 0 0;font-size:13px;color:#52514e'>Install the phone's certificate once. Android: Settings, Security, Install a certificate, CA certificate. Windows: open the file, Install Certificate, Local Machine, Trusted Root Certification Authorities. iOS: install the profile, then enable full trust under Certificate Trust Settings.</p>"
                + "<a class=btn href='" + escape(cer) + "'>Download UsbLanPrinterBridge.cer</a></div>"
                + "<p style='font-size:12px;color:#52514e'>Self-signed certificate made by this phone for its own addresses; it is remade when the phone's address changes.</p>"
                + "</div></body>";
    }

    private static List<String> cors(String origin) {
        List<String> h = new ArrayList<>();
        h.add("Access-Control-Allow-Origin: " + (origin == null ? "*" : origin));
        h.add("Vary: Origin");
        return h;
    }

    /** Byte-for-byte the shape a real TM printer returns, so SDK clients parse it identically. */
    static String buildResponse(boolean success, String code, int status, String printJobId) {
        StringBuilder sb = new StringBuilder();
        sb.append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.append("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body>");
        sb.append("<response success=\"").append(success ? "true" : "false")
                .append("\" code=\"").append(code == null ? "" : code)
                .append("\" status=\"").append(status)
                .append("\" battery=\"0\" xmlns=\"").append(EPOS_NAMESPACE).append("\">");
        if (printJobId != null && printJobId.length() > 0) {
            sb.append("<printjobid>").append(escape(printJobId)).append("</printjobid>");
        }
        sb.append("</response></s:Body></s:Envelope>");
        return sb.toString();
    }

    private static String extractPrintJobId(String xml) {
        if (xml == null) return null;
        int i = xml.indexOf("<printjobid>");
        if (i < 0) return null;
        i += "<printjobid>".length();
        int j = xml.indexOf("</printjobid>", i);
        return j < 0 ? null : xml.substring(i, j).trim();
    }

    private static String escape(String s) {
        if (s == null) return "";
        return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace("\"", "&quot;");
    }

    private static void write(OutputStream out, int status, String reason, String contentType,
                              byte[] body, List<String> extraHeaders) throws IOException {
        StringBuilder sb = new StringBuilder();
        sb.append("HTTP/1.1 ").append(status).append(' ').append(reason).append("\r\n");
        sb.append("Server: UsbLanPrinterBridge\r\n");
        sb.append("Connection: close\r\n");
        if (contentType != null) sb.append("Content-Type: ").append(contentType).append("\r\n");
        sb.append("Content-Length: ").append(body.length).append("\r\n");
        if (extraHeaders != null) for (String h : extraHeaders) sb.append(h).append("\r\n");
        sb.append("\r\n");
        out.write(sb.toString().getBytes(ASCII));
        if (body.length > 0) out.write(body);
        out.flush();
    }

    /** Minimal HTTP/1.1 request reader: request line, headers, and a Content-Length body. */
    private static final class Request {
        String method = "";
        String path = "";
        final Map<String, String> headers = new HashMap<>();
        byte[] body = new byte[0];

        String header(String name) {
            return headers.get(name.toLowerCase(Locale.US));
        }

        static Request read(InputStream in) throws IOException {
            ByteArrayOutputStream head = new ByteArrayOutputStream(1024);
            int matched = 0;
            int guard = 0;
            while (guard++ < 64 * 1024) {
                int b = in.read();
                if (b < 0) return null;
                head.write(b);
                if ((matched == 0 || matched == 2) && b == '\r') matched++;
                else if ((matched == 1 || matched == 3) && b == '\n') matched++;
                else matched = (b == '\r') ? 1 : 0;
                if (matched == 4) break;
            }

            String text = new String(head.toByteArray(), ASCII);
            String[] lines = text.split("\r\n");
            if (lines.length == 0 || lines[0].isEmpty()) return null;

            Request r = new Request();
            String[] parts = lines[0].split(" ");
            if (parts.length < 2) return null;
            r.method = parts[0].toUpperCase(Locale.US);
            r.path = parts[1];

            for (int i = 1; i < lines.length; i++) {
                int colon = lines[i].indexOf(':');
                if (colon <= 0) continue;
                r.headers.put(lines[i].substring(0, colon).trim().toLowerCase(Locale.US),
                        lines[i].substring(colon + 1).trim());
            }

            int length = 0;
            String cl = r.header("Content-Length");
            if (cl != null) {
                try {
                    length = Integer.parseInt(cl);
                } catch (NumberFormatException ignored) {
                }
            }
            if (length > 0) {
                if (length > 8 * 1024 * 1024) length = 8 * 1024 * 1024;
                byte[] body = new byte[length];
                int read = 0;
                while (read < length) {
                    int n = in.read(body, read, length - read);
                    if (n < 0) break;
                    read += n;
                }
                if (read == length) r.body = body;
                else {
                    byte[] partial = new byte[read];
                    System.arraycopy(body, 0, partial, 0, read);
                    r.body = partial;
                }
            }
            return r;
        }
    }
}
