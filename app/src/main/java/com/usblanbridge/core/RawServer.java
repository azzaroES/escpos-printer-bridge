package com.usblanbridge.core;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.net.SocketTimeoutException;
import java.text.SimpleDateFormat;
import java.util.Collections;
import java.util.Date;
import java.util.HashSet;
import java.util.Locale;
import java.util.Set;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicLong;

/**
 * The RAW / JetDirect listener, normally on port 9100. This is the endpoint that works when a POS app only asks
 * for an IP address, because 9100 is the universal default for raw network printing.
 *
 * One connection is one job, except that a silence longer than the idle timeout also closes the current job.
 * That matches real printers while coping with POS software that holds one connection open and streams
 * receipt after receipt through it.
 *
 * The start of every job is captured so the print log can show what was actually printed.
 */
public final class RawServer {

    /** How much of each job to keep for the printed-text preview. */
    private static final int MAX_CAPTURE = 128 * 1024;

    private final PrintTarget target;
    private final boolean statusReplies;
    private final String modelName;
    private final int idleTimeoutMs;
    private final int port;

    private final Set<Socket> clients = Collections.synchronizedSet(new HashSet<Socket>());
    private final AtomicLong bytesReceived = new AtomicLong();
    private final AtomicLong jobsCompleted = new AtomicLong();
    private final AtomicInteger activeClients = new AtomicInteger();

    private volatile ServerSocket serverSocket;
    private volatile boolean running;

    public RawServer(PrintTarget target, boolean statusReplies, String modelName, int idleTimeoutMs) {
        this(target, statusReplies, modelName, idleTimeoutMs, 9100);
    }

    public RawServer(PrintTarget target, boolean statusReplies, String modelName, int idleTimeoutMs, int port) {
        this.target = target;
        this.statusReplies = statusReplies;
        this.modelName = modelName;
        this.idleTimeoutMs = idleTimeoutMs;
        this.port = port;
    }

    public long getBytesReceived() {
        return bytesReceived.get();
    }

    public long getJobsCompleted() {
        return jobsCompleted.get();
    }

    public int getActiveClients() {
        return activeClients.get();
    }

    public boolean isRunning() {
        return running;
    }

    public void start(int listenPort) throws IOException {
        if (running) return;
        ServerSocket ss = new ServerSocket();
        ss.setReuseAddress(true);
        ss.bind(new InetSocketAddress(listenPort), 32);
        serverSocket = ss;
        running = true;
        Thread acceptThread = new Thread(new Runnable() {
            @Override
            public void run() {
                acceptLoop();
            }
        }, "raw-accept");
        acceptThread.setDaemon(true);
        acceptThread.start();
        Log.i("Raw printing ready on port " + listenPort);
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
        synchronized (clients) {
            for (Socket s : new HashSet<>(clients)) {
                try {
                    s.close();
                } catch (Throwable ignored) {
                }
            }
            clients.clear();
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
                if (running) Log.w("Raw accept failed: " + e.getMessage());
                break;
            }
            clients.add(socket);
            Thread t = new Thread(new Runnable() {
                @Override
                public void run() {
                    handle(socket);
                }
            }, "raw-client");
            t.setDaemon(true);
            t.start();
        }
    }

    private void handle(Socket socket) {
        String remote = String.valueOf(socket.getRemoteSocketAddress());
        activeClients.incrementAndGet();
        Log.i("Client connected: " + remote);

        PrintTarget.Job job = null;
        long jobBytes = 0;
        ByteArrayOutputStream capture = new ByteArrayOutputStream();
        EscPosResponder responder = null;
        if (statusReplies) {
            responder = new EscPosResponder();
            responder.setModelName(modelName);
        }

        byte[] buffer = new byte[32 * 1024];
        try {
            socket.setTcpNoDelay(true);
            if (idleTimeoutMs > 0) socket.setSoTimeout(idleTimeoutMs);
            InputStream in = socket.getInputStream();
            OutputStream out = socket.getOutputStream();

            while (true) {
                int n;
                try {
                    n = in.read(buffer);
                } catch (SocketTimeoutException timeout) {
                    if (job != null) {
                        job = finish(job, jobBytes, remote, "idle", capture);
                        jobBytes = 0;
                    }
                    continue;
                }
                if (n < 0) break;
                bytesReceived.addAndGet(n);

                byte[] data;
                int len;
                if (responder != null) {
                    responder.process(buffer, 0, n);
                    if (responder.replyLength() > 0) {
                        try {
                            out.write(responder.replyBytes());
                            out.flush();
                        } catch (IOException ignored) {
                            // client may have half-closed; keep printing
                        }
                    }
                    data = responder.forwardBytes();
                    len = data.length;
                } else {
                    data = buffer;
                    len = n;
                }

                if (len > 0) {
                    if (job == null) job = target.startJob("LAN " + remote + " " + stamp());
                    job.write(data, 0, len);
                    jobBytes += len;
                    capture(capture, data, len);
                }
            }

            if (responder != null) {
                responder.flush();
                byte[] tail = responder.forwardBytes();
                if (tail.length > 0) {
                    if (job == null) job = target.startJob("LAN " + remote);
                    job.write(tail, 0, tail.length);
                    jobBytes += tail.length;
                    capture(capture, tail, tail.length);
                }
            }
        } catch (Exception e) {
            Log.e("Print from " + remote + " failed", e);
            record(remote, jobBytes, capture, "Failed: " + e.getMessage());
            if (job != null) {
                job.abort();
                job = null;
            }
        } finally {
            if (job != null) finish(job, jobBytes, remote, "disconnect", capture);
            clients.remove(socket);
            try {
                socket.close();
            } catch (Throwable ignored) {
            }
            activeClients.decrementAndGet();
            Log.i("Client disconnected: " + remote);
        }
    }

    private static void capture(ByteArrayOutputStream capture, byte[] data, int length) {
        if (capture == null || data == null || length <= 0) return;
        if (capture.size() >= MAX_CAPTURE) return;
        capture.write(data, 0, Math.min(length, MAX_CAPTURE - capture.size()));
    }

    private void record(String remote, long bytes, ByteArrayOutputStream capture, String status) {
        try {
            byte[] data = capture == null ? null : capture.toByteArray();
            PrintHistory.add(remote, target.getName(), "raw " + port, data, data == null ? 0 : data.length, status);
        } catch (Throwable ignored) {
        } finally {
            if (capture != null) capture.reset();
        }
    }

    private PrintTarget.Job finish(PrintTarget.Job job, long bytes, String remote, String reason,
                                   ByteArrayOutputStream capture) {
        try {
            job.complete();
            jobsCompleted.incrementAndGet();
            Log.i("Job printed: " + formatBytes(bytes) + " from " + remote + ", " + reason);
            record(remote, bytes, capture, "Printed");
        } catch (Exception e) {
            Log.e("Could not finish job from " + remote, e);
            record(remote, bytes, capture, "Failed: " + e.getMessage());
        }
        return null;
    }

    private static String stamp() {
        return new SimpleDateFormat("HH:mm:ss", Locale.US).format(new Date());
    }

    public static String formatBytes(long bytes) {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return String.format(Locale.US, "%.1f KB", bytes / 1024.0);
        return String.format(Locale.US, "%.2f MB", bytes / (1024.0 * 1024.0));
    }
}
