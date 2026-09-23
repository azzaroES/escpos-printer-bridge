package com.usblanbridge.core;

import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.util.ArrayList;
import java.util.List;

/**
 * Reads a network scale that streams its weight over TCP (host:port), parses each line into a {@link ScaleReading},
 * and keeps the latest and the latest STABLE reading for the /scale endpoint. Reconnects on its own and keeps a
 * ring of the raw lines to identify an unknown scale. Connect/disconnect/error go to the log and the event timeline.
 *
 * Only the network transport is implemented on Android: it fits the "the scale can live anywhere" model and needs no
 * USB-serial driver. A serial scale is reached by plugging it into any machine running the bridge (a PC, or another
 * phone) and reading it over TCP from here.
 */
public final class ScaleReader {

    private final String host;
    private final int port;
    private final int reconnectDelayMs = 3000;
    private final int stableStaleMs = 4000;

    private final Object gate = new Object();
    private final String[] rawRing = new String[20];
    private int rawPos;

    private volatile Thread thread;
    private volatile boolean running;
    private volatile ScaleReading last = ScaleReading.EMPTY;
    private volatile ScaleReading lastStable = ScaleReading.EMPTY;
    private volatile boolean connected;
    private volatile String status = "stopped";
    private String lastStableLogged;

    public ScaleReader(String host, int port) {
        this.host = host;
        this.port = port;
    }

    public boolean isConnected() { return connected; }
    public String status() { return status; }
    public ScaleReading last() { return last; }

    /** What the POS should treat as "the weight now": the last stable reading if fresh, else the latest. */
    public ScaleReading current() {
        ScaleReading s = lastStable;
        if (s.ok && (System.currentTimeMillis() - s.atMs) <= stableStaleMs) return s;
        return last;
    }

    public String[] recentRaw() {
        synchronized (gate) {
            List<String> list = new ArrayList<>(rawRing.length);
            for (int i = 0; i < rawRing.length; i++) {
                int idx = (rawPos - 1 - i + rawRing.length * 2) % rawRing.length;
                String s = rawRing[idx];
                if (s != null && !s.isEmpty()) list.add(s);
            }
            return list.toArray(new String[0]);
        }
    }

    public void start() {
        if (running) return;
        running = true;
        Thread t = new Thread(new Runnable() {
            @Override public void run() { runLoop(); }
        }, "scale-reader");
        t.setDaemon(true);
        thread = t;
        t.start();
    }

    public void stop() {
        running = false;
        Thread t = thread;
        thread = null;
        if (t != null) t.interrupt();
        connected = false;
        status = "stopped";
    }

    private void runLoop() {
        while (running) {
            Socket socket = null;
            try {
                status = "connecting to " + host + ":" + port;
                socket = new Socket();
                socket.connect(new InetSocketAddress(host, port), 4000);
                socket.setSoTimeout(400);
                connected = true;
                status = "connected to " + host + ":" + port;
                Log.i("Scale connected: " + host + ":" + port);
                EventLog.warning("Scale connected: " + host + ":" + port);
                readStream(socket.getInputStream());
            } catch (Exception e) {
                connected = false;
                status = "error: " + e.getMessage();
                if (running) {
                    Log.w("Scale " + host + ":" + port + " disconnected: " + e);
                    EventLog.offline("Scale disconnected: " + e.getMessage());
                }
            } finally {
                connected = false;
                if (socket != null) try { socket.close(); } catch (Throwable ignored) { }
            }
            if (!running) break;
            status = "reconnecting in " + (reconnectDelayMs / 1000) + "s";
            sleep(reconnectDelayMs);
        }
        status = "stopped";
    }

    private void readStream(InputStream in) throws Exception {
        StringBuilder line = new StringBuilder(64);
        byte[] buf = new byte[256];
        while (running) {
            int n;
            try {
                n = in.read(buf);
            } catch (java.net.SocketTimeoutException t) {
                continue; // no data this tick
            }
            if (n < 0) throw new java.io.IOException("the scale closed the connection");
            for (int i = 0; i < n; i++) {
                byte b = buf[i];
                if (b == '\n' || b == '\r') {
                    if (line.length() > 0) { onLine(line.toString()); line.setLength(0); }
                } else if (line.length() < 512) {
                    line.append((char) (b & 0xFF));
                }
            }
        }
    }

    private void onLine(String raw) {
        synchronized (gate) { rawRing[rawPos] = raw; rawPos = (rawPos + 1) % rawRing.length; }
        ScaleReading r = ScaleReading.parse(raw);
        if (!r.ok) return;
        last = r;
        if (r.stable) {
            lastStable = r;
            String key = ScaleReading.num(r.weight) + r.unit;
            if (!key.equals(lastStableLogged)) {
                lastStableLogged = key;
                Log.i("Scale stable: " + ScaleReading.num(r.weight) + (r.unit.isEmpty() ? "" : " " + r.unit) + "  (raw: " + raw.trim() + ")");
            }
        }
    }

    private void sleep(int ms) {
        int slept = 0;
        while (running && slept < ms) { try { Thread.sleep(100); } catch (InterruptedException e) { return; } slept += 100; }
    }
}
