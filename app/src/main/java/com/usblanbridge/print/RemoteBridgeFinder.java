package com.usblanbridge.print;

import android.content.Context;
import android.net.nsd.NsdManager;
import android.net.nsd.NsdServiceInfo;
import android.net.wifi.WifiManager;

import com.usblanbridge.BridgeService;
import com.usblanbridge.core.Log;

import java.net.InetAddress;
import java.util.ArrayDeque;
import java.util.Deque;

/**
 * Finds other bridges on the Wi-Fi, so a second phone with this app installed sees the first phone's printer
 * in its own Print menu with no typing at all.
 *
 * Bridges announce themselves as "_pdl-datastream._tcp", the standard DNS-SD name for raw port 9100 printing,
 * which is also what CUPS on Linux and macOS looks for. Android's NSD resolves one service at a time, so
 * resolutions are queued.
 */
final class RemoteBridgeFinder {

    static final String SERVICE_TYPE = "_pdl-datastream._tcp.";

    interface Listener {
        void found(String host, int port, String name);
    }

    private final Context context;
    private final NsdManager nsd;
    private final Deque<NsdServiceInfo> pending = new ArrayDeque<>();
    private NsdManager.DiscoveryListener discovery;
    private WifiManager.MulticastLock multicast;
    private boolean resolving;
    private Listener listener;

    RemoteBridgeFinder(Context context) {
        this.context = context.getApplicationContext();
        NsdManager m = null;
        try {
            m = (NsdManager) this.context.getSystemService(Context.NSD_SERVICE);
        } catch (Throwable ignored) {
        }
        nsd = m;
    }

    synchronized void start(Listener l) {
        if (nsd == null || discovery != null) return;
        listener = l;
        try {
            WifiManager wifi = (WifiManager) context.getSystemService(Context.WIFI_SERVICE);
            if (wifi != null) {
                multicast = wifi.createMulticastLock("UsbLanBridge-discovery");
                multicast.setReferenceCounted(false);
                multicast.acquire();
            }
        } catch (Throwable ignored) {
        }

        discovery = new NsdManager.DiscoveryListener() {
            @Override
            public void onStartDiscoveryFailed(String type, int code) {
                Log.w("Could not look for other bridges (code " + code + ").");
            }

            @Override
            public void onStopDiscoveryFailed(String type, int code) {
            }

            @Override
            public void onDiscoveryStarted(String type) {
            }

            @Override
            public void onDiscoveryStopped(String type) {
            }

            @Override
            public void onServiceFound(NsdServiceInfo info) {
                String own = BridgeService.advertisedName();
                if (own != null && own.equals(info.getServiceName())) return;   // that is this phone
                queue(info);
            }

            @Override
            public void onServiceLost(NsdServiceInfo info) {
            }
        };
        try {
            nsd.discoverServices(SERVICE_TYPE, NsdManager.PROTOCOL_DNS_SD, discovery);
        } catch (Throwable t) {
            Log.w("Could not look for other bridges: " + t);
            discovery = null;
        }
    }

    synchronized void stop() {
        if (nsd != null && discovery != null) {
            try {
                nsd.stopServiceDiscovery(discovery);
            } catch (Throwable ignored) {
            }
        }
        discovery = null;
        pending.clear();
        resolving = false;
        try {
            if (multicast != null && multicast.isHeld()) multicast.release();
        } catch (Throwable ignored) {
        }
        multicast = null;
    }

    private synchronized void queue(NsdServiceInfo info) {
        pending.add(info);
        resolveNext();
    }

    private synchronized void resolveNext() {
        if (resolving || pending.isEmpty() || nsd == null || discovery == null) return;
        NsdServiceInfo next = pending.poll();
        resolving = true;
        try {
            nsd.resolveService(next, new NsdManager.ResolveListener() {
                @Override
                public void onResolveFailed(NsdServiceInfo info, int code) {
                    done();
                }

                @Override
                public void onServiceResolved(NsdServiceInfo info) {
                    try {
                        InetAddress host = info.getHost();
                        Listener l = listener;
                        if (host != null && l != null && info.getPort() > 0) {
                            l.found(host.getHostAddress(), info.getPort(), info.getServiceName());
                        }
                    } catch (Throwable ignored) {
                    }
                    done();
                }
            });
        } catch (Throwable t) {
            done();
        }
    }

    private synchronized void done() {
        resolving = false;
        resolveNext();
    }
}
