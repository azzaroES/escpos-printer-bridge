package com.usblanbridge.core;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.charset.Charset;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.concurrent.atomic.AtomicLong;

/**
 * Emulates an Epson ePOS-Print device so browser and Android POS apps written against the Epson ePOS SDK can
 * print to whatever printer this phone is sharing.
 *
 * Note the port. An unrooted Android app cannot bind ports below 1024, so this cannot listen on 80 the way the
 * Windows build does. Clients must include the port in the URL. Raw printing on 9100 has no such restriction,
 * which is why that remains the path that works from an IP address alone.
 *
 * Responses mirror a real Epson TM printer byte for byte, including status 251658262.
 */
public final class EposHttpServer {

    /** The status word a real Epson returns when all is well, 0x0F000016. */
    public static final int OK_STATUS = 251658262;
    public static final String EPOS_NAMESPACE = EposPrintConverter.EPOS_NAMESPACE;

    private static final Charset UTF8 = Charset.forName("UTF-8");
    private static final Charset ASCII = Charset.forName("US-ASCII");

    private final PrintTarget target;
    private final AtomicLong requests = new AtomicLong();
    private final AtomicLong jobs = new AtomicLong();

    private volatile ServerSocket serverSocket;
    private volatile boolean running;

    public EposHttpServer(PrintTarget target) {
        this.target = target;
    }

    public boolean isRunning() {
        return running;
    }

    public long getRequests() {
        return requests.get();
    }

    public long getJobs() {
        return jobs.get();
    }

    public void start(int port) throws IOException {
        if (running) return;
        ServerSocket ss = new ServerSocket();
        ss.setReuseAddress(true);
        ss.bind(new InetSocketAddress(port), 32);
        serverSocket = ss;
        running = true;
        Thread t = new Thread(new Runnable() {
            @Override
            public void run() {
                acceptLoop();
            }
        }, "epos-accept");
        t.setDaemon(true);
        t.start();
        Log.i("ePOS-Print ready on port " + port + " at /cgi-bin/epos/service.cgi");
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
        try {
            socket.setSoTimeout(15000);
            InputStream in = socket.getInputStream();
            OutputStream out = socket.getOutputStream();

            Request req = Request.read(in);
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

            boolean isEpos = req.path.contains("/cgi-bin/epos") || req.path.contains("service.cgi");
            if ("POST".equals(req.method) && isEpos) {
                handleEpos(out, req, origin, remote);
                return;
            }

            if ("GET".equals(req.method)) {
                String html = "<!doctype html><meta charset=utf-8><title>USB LAN Printer Bridge</title>"
                        + "<body style='font-family:sans-serif;margin:2em'>"
                        + "<h2>USB LAN Printer Bridge</h2><p>Sharing <b>" + escape(target.getName()) + "</b>.</p>"
                        + "<p>ePOS-Print endpoint: <code>/cgi-bin/epos/service.cgi</code></p>"
                        + "<p>Requests: " + getRequests() + ", jobs: " + getJobs() + "</p></body>";
                write(out, 200, "OK", "text/html; charset=utf-8", html.getBytes(UTF8), cors(origin));
                return;
            }

            write(out, 404, "Not Found", "text/plain; charset=utf-8", "Not found".getBytes(UTF8), cors(origin));
        } catch (Exception e) {
            Log.w("ePOS request from " + remote + " failed: " + e);
        } finally {
            try {
                socket.close();
            } catch (Throwable ignored) {
            }
        }
    }

    private void handleEpos(OutputStream out, Request req, String origin, String remote) throws IOException {
        String xml = new String(req.body, UTF8);
        String printJobId = extractPrintJobId(xml);
        String responseXml;

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
            PrintHistory.add(remote, target.getName(), "ePOS http", result.bytes, result.bytes.length, "Printed");
            Log.i("ePOS job printed: " + RawServer.formatBytes(result.bytes.length) + " from " + remote
                    + (result.warnings.isEmpty() ? "" : ", " + result.warnings.size() + " warning(s)"));
            for (String w : result.warnings) Log.w("ePOS: " + w);
            responseXml = buildResponse(true, "", OK_STATUS, printJobId);
        } catch (IllegalArgumentException bad) {
            Log.w("ePOS document rejected from " + remote + ": " + bad.getMessage());
            byte[] raw = xml.getBytes(UTF8);
            PrintHistory.add(remote, target.getName(), "ePOS http", raw, raw.length, "Rejected: " + bad.getMessage());
            responseXml = buildResponse(false, "SchemaError", OK_STATUS, printJobId);
        } catch (Exception e) {
            Log.e("ePOS print from " + remote + " failed", e);
            byte[] raw = xml.getBytes(UTF8);
            PrintHistory.add(remote, target.getName(), "ePOS http", raw, raw.length, "Failed: " + e.getMessage());
            responseXml = buildResponse(false, "EPTR_PRINT_SYSTEM_ERROR", OK_STATUS, printJobId);
        }

        write(out, 200, "OK", "text/xml; charset=utf-8", responseXml.getBytes(UTF8), cors(origin));
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
