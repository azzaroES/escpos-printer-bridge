package com.usblanbridge.core;

import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.Inet4Address;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.InterfaceAddress;
import java.net.NetworkInterface;
import java.net.Socket;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.Locale;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;

/**
 * Sweeps the local subnet for network printers.
 *
 * Typing an IP address by hand only works if you already know it, which is rarely true on a shop network. This
 * finds the candidates instead: every host that accepts a connection on a printing port, and where possible the
 * printer's own model name, obtained by asking it the same way a POS application would.
 *
 * Ports probed, in order of usefulness:
 *   9100  RAW / JetDirect, what this bridge speaks
 *   515   LPR / LPD
 *   631   IPP
 *
 * A host answering 9100 is additionally asked to identify itself with GS I 67. A real ESC/POS printer replies
 * with a header byte, its model name, and a NUL. That distinguishes an actual receipt printer from anything else
 * that happens to hold port 9100 open.
 */
public final class NetworkPrinterScanner {

    public static final int PORT_RAW = 9100;
    public static final int PORT_LPR = 515;
    public static final int PORT_IPP = 631;

    private static final int CONNECT_TIMEOUT_MS = 400;
    private static final int IDENTIFY_TIMEOUT_MS = 700;
    private static final int THREADS = 48;
    /** Refuse to sweep enormous subnets; a /22 is already 1022 hosts. */
    private static final int MAX_HOSTS = 1024;

    /** A responding host. */
    public static final class Found {
        public final String host;
        public final int port;
        /** Model name if the device identified itself, otherwise null. */
        public final String model;

        Found(String host, int port, String model) {
            this.host = host;
            this.port = port;
            this.model = model;
        }

        public boolean isReceiptPrinter() {
            return port == PORT_RAW && model != null;
        }

        public String label() {
            StringBuilder sb = new StringBuilder(host).append(':').append(port);
            if (model != null) sb.append("  ").append(model);
            else if (port == PORT_RAW) sb.append("  raw printing port open");
            else if (port == PORT_LPR) sb.append("  LPR");
            else if (port == PORT_IPP) sb.append("  IPP");
            return sb.toString();
        }

        @Override
        public String toString() {
            return label();
        }
    }

    public interface Progress {
        void onProgress(int done, int total);
    }

    /** The address range that will be swept, or null when there is no usable network. */
    public static final class Range {
        public final String first;
        public final String last;
        public final int hostCount;
        public final String localAddress;
        final byte[] base;
        final int prefix;

        Range(byte[] base, int prefix, String first, String last, int hostCount, String localAddress) {
            this.base = base;
            this.prefix = prefix;
            this.first = first;
            this.last = last;
            this.hostCount = hostCount;
            this.localAddress = localAddress;
        }

        public String describe() {
            return first + " - " + last + "  (" + hostCount + " addresses, this device is " + localAddress + ")";
        }
    }

    private NetworkPrinterScanner() {
    }

    /** Works out which addresses to sweep from the device's own Wi-Fi address and netmask. */
    public static Range localRange() {
        try {
            for (NetworkInterface nic : Collections.list(NetworkInterface.getNetworkInterfaces())) {
                if (!nic.isUp() || nic.isLoopback()) continue;
                for (InterfaceAddress ia : nic.getInterfaceAddresses()) {
                    InetAddress addr = ia.getAddress();
                    if (!(addr instanceof Inet4Address) || addr.isLoopbackAddress()) continue;

                    int prefix = ia.getNetworkPrefixLength();
                    if (prefix <= 0 || prefix > 30) continue;

                    byte[] ip = addr.getAddress();
                    byte[] base = new byte[4];
                    for (int i = 0; i < 4; i++) {
                        int bitsHere = Math.max(0, Math.min(8, prefix - i * 8));
                        int mask = bitsHere == 0 ? 0 : (0xFF << (8 - bitsHere)) & 0xFF;
                        base[i] = (byte) (ip[i] & mask);
                    }

                    long total = (1L << (32 - prefix)) - 2;   // drop network and broadcast
                    if (total < 1) continue;
                    int hosts = (int) Math.min(total, MAX_HOSTS);

                    String first = hostAt(base, prefix, 1);
                    String last = hostAt(base, prefix, hosts);
                    return new Range(base, prefix, first, last, hosts, addr.getHostAddress());
                }
            }
        } catch (Exception e) {
            Log.e("Could not work out the local address range", e);
        }
        return null;
    }

    private static String hostAt(byte[] base, int prefix, int index) {
        long value = ((base[0] & 0xFFL) << 24) | ((base[1] & 0xFFL) << 16)
                | ((base[2] & 0xFFL) << 8) | (base[3] & 0xFFL);
        value += index;
        return ((value >> 24) & 0xFF) + "." + ((value >> 16) & 0xFF) + "."
                + ((value >> 8) & 0xFF) + "." + (value & 0xFF);
    }

    /**
     * Sweeps the local subnet. Blocking, so call it off the UI thread.
     * Receipt printers that identified themselves are listed first.
     */
    public static List<Found> scan(Progress progress) {
        final List<Found> results = Collections.synchronizedList(new ArrayList<Found>());
        Range range = localRange();
        if (range == null) {
            Log.w("No usable network, so there is nothing to scan. Connect to Wi-Fi first.");
            return results;
        }

        Log.i("Scanning " + range.describe() + " for printers on ports 9100, 515 and 631...");
        final int total = range.hostCount;
        final AtomicInteger done = new AtomicInteger();

        ExecutorService pool = Executors.newFixedThreadPool(THREADS);
        try {
            for (int i = 1; i <= total; i++) {
                final String host = hostAt(range.base, range.prefix, i);
                if (host.equals(range.localAddress)) {
                    done.incrementAndGet();
                    continue;
                }
                pool.execute(new Runnable() {
                    @Override
                    public void run() {
                        try {
                            probe(host, results);
                        } catch (Throwable ignored) {
                        } finally {
                            int n = done.incrementAndGet();
                            if (progress != null && (n % 16 == 0 || n == total)) {
                                try {
                                    progress.onProgress(n, total);
                                } catch (Throwable ignored) {
                                }
                            }
                        }
                    }
                });
            }
            pool.shutdown();
            pool.awaitTermination(90, TimeUnit.SECONDS);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            pool.shutdownNow();
        }

        List<Found> sorted = new ArrayList<>(results);
        // Confirmed receipt printers first, then other raw ports, then the rest.
        Collections.sort(sorted, (a, b) -> {
            int ra = rank(a), rb = rank(b);
            if (ra != rb) return ra - rb;
            return a.host.compareTo(b.host);
        });

        Log.i("Scan finished: " + sorted.size() + " printing port(s) answered.");
        for (Found f : sorted) Log.i("   " + f.label());
        return sorted;
    }

    private static int rank(Found f) {
        if (f.isReceiptPrinter()) return 0;
        if (f.port == PORT_RAW) return 1;
        if (f.port == PORT_LPR) return 2;
        return 3;
    }

    private static void probe(String host, List<Found> results) {
        if (isOpen(host, PORT_RAW)) {
            results.add(new Found(host, PORT_RAW, identify(host)));
            return;   // 9100 is what we want; no need to classify further
        }
        if (isOpen(host, PORT_LPR)) {
            results.add(new Found(host, PORT_LPR, null));
            return;
        }
        if (isOpen(host, PORT_IPP)) {
            results.add(new Found(host, PORT_IPP, null));
        }
    }

    private static boolean isOpen(String host, int port) {
        Socket s = new Socket();
        try {
            s.connect(new InetSocketAddress(host, port), CONNECT_TIMEOUT_MS);
            return true;
        } catch (Exception e) {
            return false;
        } finally {
            try {
                s.close();
            } catch (IOException ignored) {
            }
        }
    }

    /**
     * Asks a host on port 9100 to identify itself with GS I 67. An ESC/POS printer answers with
     * 0x5F, its model name, then 0x00. Returns the model, or null if it did not answer that way.
     */
    private static String identify(String host) {
        Socket s = new Socket();
        try {
            s.connect(new InetSocketAddress(host, PORT_RAW), CONNECT_TIMEOUT_MS);
            s.setSoTimeout(IDENTIFY_TIMEOUT_MS);
            OutputStream out = s.getOutputStream();
            out.write(new byte[]{0x1D, 0x49, 0x43});   // GS I 67
            out.flush();

            InputStream in = s.getInputStream();
            byte[] buf = new byte[64];
            int n = in.read(buf);
            if (n <= 0) return null;

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < n; i++) {
                int b = buf[i] & 0xFF;
                if (b == 0x5F || b == 0x00) continue;          // header and terminator
                if (b >= 0x20 && b <= 0x7E) sb.append((char) b);
            }
            String model = sb.toString().trim();
            return model.isEmpty() ? null : model;
        } catch (Exception e) {
            return null;
        } finally {
            try {
                s.close();
            } catch (IOException ignored) {
            }
        }
    }

    /** One-line summary for the log or a toast. */
    public static String summarise(List<Found> found) {
        if (found.isEmpty()) return "No printers answered on this network.";
        return String.format(Locale.US, "%d printing port(s) found, best match %s", found.size(), found.get(0).label());
    }
}
