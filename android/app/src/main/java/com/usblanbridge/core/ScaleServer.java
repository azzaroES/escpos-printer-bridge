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
import java.util.List;
import java.util.Locale;

/**
 * Publishes the scale's weight so any POS on the LAN can read it, wireless by nature.
 *   GET /scale       -> {"ok":true,"weight":1.234,"unit":"kg","grams":1234,"stable":true,...}
 *   GET /scale/raw   -> {"connected":true,"status":"...","lines":["...",...]}
 * Full CORS + Private Network Access, like the ePOS endpoint. Cannot bind below 1024 on an unrooted phone, so the
 * default port is 8020 and the POS includes it in the URL.
 */
public final class ScaleServer {

    /** The reader's live state, already in the unit the endpoint should present. */
    public interface Provider {
        ScaleReading current();
        String[] raw();
        String status();
        boolean connected();
    }

    private static final Charset UTF8 = Charset.forName("UTF-8");
    private static final Charset ASCII = Charset.forName("US-ASCII");

    private final Provider provider;
    private volatile ServerSocket serverSocket;
    private volatile boolean running;
    private volatile int port;

    public ScaleServer(Provider provider) {
        this.provider = provider;
    }

    public boolean isRunning() { return running; }
    public int port() { return port; }

    public void start(int listenPort) throws IOException {
        if (running) return;
        ServerSocket ss = new ServerSocket();
        ss.setReuseAddress(true);
        ss.bind(new InetSocketAddress(listenPort), 32);
        serverSocket = ss;
        port = ss.getLocalPort();
        running = true;
        Thread t = new Thread(new Runnable() {
            @Override public void run() { acceptLoop(); }
        }, "scale-http-accept");
        t.setDaemon(true);
        t.start();
        Log.i("Scale published on http port " + port + " at /scale");
    }

    public void stop() {
        running = false;
        ServerSocket ss = serverSocket;
        serverSocket = null;
        if (ss != null) try { ss.close(); } catch (Throwable ignored) { }
    }

    private void acceptLoop() {
        while (running) {
            final Socket socket;
            try {
                ServerSocket ss = serverSocket;
                if (ss == null) break;
                socket = ss.accept();
            } catch (IOException e) {
                if (running) Log.w("Scale accept failed: " + e.getMessage());
                break;
            }
            Thread t = new Thread(new Runnable() {
                @Override public void run() { handle(socket); }
            }, "scale-client");
            t.setDaemon(true);
            t.start();
        }
    }

    private void handle(Socket socket) {
        try {
            socket.setSoTimeout(15000);
            InputStream in = socket.getInputStream();
            OutputStream out = socket.getOutputStream();
            Request req = Request.read(in);
            if (req == null) return;
            String origin = req.origin;

            if ("OPTIONS".equals(req.method)) {
                List<String> extra = new ArrayList<>();
                extra.add("Access-Control-Allow-Origin: " + (origin == null ? "*" : origin));
                extra.add("Vary: Origin");
                extra.add("Access-Control-Allow-Methods: GET, OPTIONS");
                extra.add("Access-Control-Allow-Headers: Content-Type");
                extra.add("Access-Control-Max-Age: 86400");
                extra.add("Access-Control-Allow-Private-Network: true");
                write(out, 204, "No Content", null, new byte[0], extra);
                return;
            }

            String p = req.path;
            int q = p.indexOf('?');
            if (q >= 0) p = p.substring(0, q);
            while (p.length() > 1 && p.endsWith("/")) p = p.substring(0, p.length() - 1);
            p = p.toLowerCase(Locale.US);

            if ("GET".equals(req.method) && (p.equals("/scale") || p.isEmpty() || p.equals("/"))) {
                ScaleReading r = provider != null ? provider.current() : ScaleReading.EMPTY;
                writeJson(out, 200, r.toJson(), origin);
                return;
            }
            if ("GET".equals(req.method) && p.equals("/scale/raw")) {
                writeJson(out, 200, rawJson(), origin);
                return;
            }
            writeJson(out, 404, "{\"error\":\"not found\"}", origin);
        } catch (Exception e) {
            Log.w("Scale request failed: " + e);
        } finally {
            try { socket.close(); } catch (Throwable ignored) { }
        }
    }

    private String rawJson() {
        StringBuilder sb = new StringBuilder();
        sb.append("{\"connected\":").append(provider != null && provider.connected());
        sb.append(",\"status\":\"").append(ScaleReading.esc(provider != null ? provider.status() : "")).append('"');
        sb.append(",\"lines\":[");
        String[] lines = provider != null ? provider.raw() : new String[0];
        for (int i = 0; i < lines.length; i++) {
            if (i > 0) sb.append(',');
            sb.append('"').append(ScaleReading.esc(lines[i])).append('"');
        }
        sb.append("]}");
        return sb.toString();
    }

    private static void writeJson(OutputStream out, int status, String json, String origin) throws IOException {
        List<String> extra = new ArrayList<>();
        extra.add("Access-Control-Allow-Origin: " + (origin == null ? "*" : origin));
        extra.add("Vary: Origin");
        extra.add("Cache-Control: no-store");
        write(out, status, status == 200 ? "OK" : (status == 404 ? "Not Found" : "Error"),
                "application/json; charset=utf-8", json.getBytes(UTF8), extra);
    }

    private static void write(OutputStream out, int status, String reason, String contentType, byte[] body, List<String> extra) throws IOException {
        StringBuilder sb = new StringBuilder();
        sb.append("HTTP/1.1 ").append(status).append(' ').append(reason).append("\r\n");
        sb.append("Server: UsbLanPrinterBridge-Scale\r\n");
        sb.append("Connection: close\r\n");
        if (contentType != null) sb.append("Content-Type: ").append(contentType).append("\r\n");
        sb.append("Content-Length: ").append(body.length).append("\r\n");
        if (extra != null) for (String h : extra) sb.append(h).append("\r\n");
        sb.append("\r\n");
        out.write(sb.toString().getBytes(ASCII));
        if (body.length > 0) out.write(body);
        out.flush();
    }

    private static final class Request {
        String method = "";
        String path = "";
        String origin;

        static Request read(InputStream in) throws IOException {
            ByteArrayOutputStream head = new ByteArrayOutputStream(512);
            int matched = 0, guard = 0;
            while (guard++ < 32 * 1024) {
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
            String[] parts = lines[0].split(" ");
            if (parts.length < 2) return null;
            Request r = new Request();
            r.method = parts[0].toUpperCase(Locale.US);
            r.path = parts[1];
            for (int i = 1; i < lines.length; i++) {
                int c = lines[i].indexOf(':');
                if (c <= 0) continue;
                if (lines[i].substring(0, c).trim().equalsIgnoreCase("Origin")) r.origin = lines[i].substring(c + 1).trim();
            }
            return r;
        }
    }
}
