package com.usblanbridge;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.hardware.usb.UsbDevice;
import android.hardware.usb.UsbManager;
import android.net.nsd.NsdManager;
import android.net.nsd.NsdServiceInfo;
import android.net.wifi.WifiManager;
import android.os.Build;
import android.os.IBinder;
import android.os.PowerManager;

import com.usblanbridge.core.BluetoothPrintTarget;
import com.usblanbridge.core.EposHttpServer;
import com.usblanbridge.core.EventLog;
import com.usblanbridge.core.Log;
import com.usblanbridge.core.NetUtil;
import com.usblanbridge.core.PrintTarget;
import com.usblanbridge.core.RawServer;
import com.usblanbridge.core.EventLog;
import com.usblanbridge.core.ScaleReader;
import com.usblanbridge.core.ScaleReading;
import com.usblanbridge.core.ScaleServer;
import com.usblanbridge.core.SunmiPrintTarget;
import com.usblanbridge.core.TcpPrintTarget;
import com.usblanbridge.core.TlsCertificate;
import com.usblanbridge.core.UsbPrintTarget;
import com.usblanbridge.print.PrinterCatalog;

import java.io.File;
import java.util.HashMap;

import javax.net.ssl.SSLServerSocketFactory;

/**
 * Keeps the printer shared while the app is in the background or the screen is off.
 *
 * It runs as a foreground service of type connectedDevice, and holds a Wi-Fi lock and a partial wake lock so
 * the phone does not drop the listening sockets when it dozes. Without those, printing works only while the
 * screen is on, which is useless for a till.
 */
public final class BridgeService extends Service {

    public static final String ACTION_START = "com.usblanbridge.START";
    public static final String ACTION_STOP = "com.usblanbridge.STOP";

    private static final String CHANNEL_ID = "bridge";
    private static final int NOTIFICATION_ID = 1;

    private static volatile BridgeService instance;

    private RawServer rawServer;
    private EposHttpServer eposServer;
    private EposHttpServer httpsServer;
    private ScaleReader scaleReader;
    private ScaleServer scaleServer;
    private volatile int rawPort;
    private UsbPrintTarget usbTarget;
    private SunmiPrintTarget sunmiTarget;
    private BluetoothPrintTarget bluetoothTarget;
    private PrintTarget target;
    private WifiManager.WifiLock wifiLock;
    private PowerManager.WakeLock wakeLock;
    private volatile String statusText = "Stopped";
    private volatile String currentId;
    private NsdManager nsd;
    private NsdManager.RegistrationListener nsdListener;
    private volatile String advertisedName;

    public static boolean isRunning() {
        BridgeService s = instance;
        return s != null && s.rawServer != null && s.rawServer.isRunning();
    }

    public static String status() {
        BridgeService s = instance;
        return s == null ? "Stopped" : s.statusText;
    }

    public static RawServer rawServer() {
        BridgeService s = instance;
        return s == null ? null : s.rawServer;
    }

    /** The HTTPS ePOS endpoint while it is up, or null. */
    public static EposHttpServer httpsServer() {
        BridgeService s = instance;
        EposHttpServer h = s == null ? null : s.httpsServer;
        return h != null && h.isRunning() ? h : null;
    }

    /** Applies or lifts the cool-down while the bridge runs: Wi-Fi lock mode and network discovery. */
    public static void applyThrottle(boolean on) {
        BridgeService s = instance;
        if (s == null || s.rawServer == null) return;
        s.releaseWifiLock();
        s.acquireLocks();
        if (on) s.unadvertise();
        else if (s.nsdListener == null) s.advertise(s.rawPort);
    }

    /** The target the running bridge prints to, footer included, or null when stopped. */
    public static PrintTarget currentTarget() {
        BridgeService s = instance;
        return s == null ? null : s.target;
    }

    /** Catalogue id of the printer the running bridge uses, so the print service can share its connection. */
    public static String currentPrinterId() {
        BridgeService s = instance;
        return s == null ? null : s.currentId;
    }

    /** The name this phone announces itself under on the network, or null. Lets the print service skip itself. */
    public static String advertisedName() {
        BridgeService s = instance;
        return s == null ? null : s.advertisedName;
    }

    @Override
    public void onCreate() {
        super.onCreate();
        // Write the log and the print history where they can be read on the phone and pulled over adb.
        // Some phones suppress application logs from logcat entirely, so the file is the reliable record.
        Log.setDirectory(getExternalFilesDir(null));
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        String action = intent == null ? ACTION_START : intent.getAction();
        if (ACTION_STOP.equals(action)) {
            shutdown();
            stopSelf();
            return START_NOT_STICKY;
        }

        instance = this;
        startForegroundSafely("Starting...");

        new Thread(new Runnable() {
            @Override
            public void run() {
                try {
                    startBridge();
                } catch (Exception e) {
                    Log.e("Could not start the bridge", e);
                    EventLog.failed("Bridge could not start · " + e.getMessage());
                    statusText = "Error: " + e.getMessage();
                    updateNotification(statusText);
                }
            }
        }, "bridge-start").start();

        return START_STICKY;
    }

    private void startBridge() throws Exception {
        Prefs prefs = new Prefs(this);
        shutdownServers();

        // Footer outside, NO CUT inside: the footer is placed before the cut, then the cut is replaced by a feed.
        target = Branding.wrap(this, NoCut.wrap(this, buildTarget(prefs)));
        Log.i("Sharing printer: " + target.getName());
        if (prefs.isNoCut()) Log.w("NO CUT is on: cutter commands are removed from every ticket.");

        rawServer = new RawServer(target, prefs.isStatusReplies(), prefs.getModelName(),
                prefs.getIdleTimeoutMs(), prefs.getRawPort());
        rawServer.start(prefs.getRawPort());
        rawPort = prefs.getRawPort();

        if (prefs.isEposEnabled()) {
            EposHttpServer.DeviceIdSource ids = new EposHttpServer.DeviceIdSource() {
                @Override
                public String deviceId() {
                    return new Prefs(BridgeService.this).getEposDeviceId();
                }
            };
            byte[] der = null;
            SSLServerSocketFactory tls = null;
            try {
                // RSA key generation takes a moment on an old phone; this runs on the start thread, not the UI.
                TlsCertificate.Material m = TlsCertificate.load(new File(getFilesDir(), "tls"), NetUtil.getLanAddresses());
                if (m.note != null) Log.w(m.note);
                if (m.generated) Log.i("Made a self-signed HTTPS certificate for " + m.addresses + " (valid 10 years).");
                der = m.certificateDer;
                tls = m.serverSocketFactory();
            } catch (Throwable t) {
                Log.w("HTTPS certificate not available: " + t);
            }

            eposServer = new EposHttpServer(target, ids, null, der);
            eposServer.setHttpsPort(prefs.getEposHttpsPort());
            try {
                eposServer.start(prefs.getEposPort());
            } catch (Exception e) {
                Log.w("ePOS endpoint could not start on port " + prefs.getEposPort() + ": " + e.getMessage());
                eposServer = null;
            }
            if (tls != null) {
                httpsServer = new EposHttpServer(target, ids, tls, der);
                try {
                    httpsServer.start(prefs.getEposHttpsPort());
                    Log.i("ePOS over HTTPS answers device id \"" + prefs.getEposDeviceId() + "\"; clients trust it once at https://"
                            + (NetUtil.getLanAddress() == null ? "<phone address>" : NetUtil.getLanAddress()) + ":" + prefs.getEposHttpsPort() + "/cert");
                } catch (Exception e) {
                    Log.w("ePOS HTTPS endpoint could not start on port " + prefs.getEposHttpsPort() + ": " + e.getMessage());
                    httpsServer = null;
                }
            }
        }

        startScale(prefs);

        acquireLocks();
        if (Throttle.isActive(this)) Log.w("Cool-down is active: normal Wi-Fi lock, no network announcement until it ends.");
        else advertise(prefs.getRawPort());
        DeviceMonitor.get(this).acquire("service");

        String ip = NetUtil.getLanAddress();
        statusText = (ip == null ? "No Wi-Fi address" : ip + ":" + prefs.getRawPort())
                + "  ->  " + target.getName();
        Log.i("Bridge running. Point POS software at " + (ip == null ? "this phone" : ip)
                + " port " + prefs.getRawPort() + ".");
        updateNotification(statusText);
    }

    /** Starts the weighing scale reader + the /scale endpoint when enabled. A network scale streams over TCP. */
    private void startScale(Prefs prefs) {
        if (!prefs.isScaleEnabled()) return;
        final String host = prefs.getScaleHost().trim();
        if (host.isEmpty()) {
            Log.w("Scale is enabled but no host is set; not started.");
            return;
        }
        final String displayUnit = prefs.getScaleDisplayUnit();
        final ScaleReader reader = new ScaleReader(host, prefs.getScalePort());
        ScaleServer server = new ScaleServer(new ScaleServer.Provider() {
            @Override public ScaleReading current() {
                ScaleReading r = reader.current();
                return (displayUnit == null || displayUnit.isEmpty()) ? r : r.inUnit(displayUnit);
            }
            @Override public String[] raw() { return reader.recentRaw(); }
            @Override public String status() { return reader.status(); }
            @Override public boolean connected() { return reader.isConnected(); }
        });
        try {
            server.start(prefs.getScaleHttpPort());
            reader.start();
            scaleReader = reader;
            scaleServer = server;
            Log.i("Scale reading " + host + ":" + prefs.getScalePort() + ", published at /scale on port " + server.port());
            EventLog.warning("Scale started: " + host + ":" + prefs.getScalePort());
        } catch (Exception e) {
            Log.w("Scale endpoint could not start on port " + prefs.getScaleHttpPort() + ": " + e.getMessage());
            try { server.stop(); } catch (Throwable ignored) { }
        }
    }

    private PrintTarget buildTarget(Prefs prefs) throws Exception {
        if (Prefs.TARGET_TCP.equals(prefs.getTargetMode())) {
            String host = prefs.getTcpHost().trim();
            currentId = PrinterCatalog.NET + host + ":" + prefs.getTcpPort();
            return new TcpPrintTarget(host, prefs.getTcpPort());
        }

        if (Prefs.TARGET_SUNMI.equals(prefs.getTargetMode())) {
            SunmiPrintTarget sunmi = new SunmiPrintTarget(this);
            sunmi.connect();   // binds and validates, throwing a readable reason if either fails
            sunmiTarget = sunmi;
            currentId = PrinterCatalog.SUNMI;
            return sunmi;
        }

        if (Prefs.TARGET_BLUETOOTH.equals(prefs.getTargetMode())) {
            String address = prefs.getBluetoothAddress();
            if (address == null || address.isEmpty()) {
                throw new IllegalStateException("No Bluetooth printer chosen yet. Pick one in the app first.");
            }
            BluetoothPrintTarget bt = new BluetoothPrintTarget(this, address);
            bt.connect();
            bluetoothTarget = bt;
            currentId = PrinterCatalog.BT + address;
            return bt;
        }

        UsbManager manager = (UsbManager) getSystemService(Context.USB_SERVICE);
        if (manager == null) throw new IllegalStateException("This phone has no USB host support.");

        HashMap<String, UsbDevice> devices = manager.getDeviceList();
        UsbDevice chosen = null;
        String wanted = prefs.getUsbDeviceName();
        if (wanted != null && wanted.length() > 0) chosen = devices.get(wanted);
        if (chosen == null) {
            for (UsbDevice d : devices.values()) {
                if (UsbPrintTarget.looksPrintable(d)) {
                    chosen = d;
                    break;
                }
            }
        }
        if (chosen == null) {
            throw new IllegalStateException("No USB printer found. Connect one with an OTG cable, or switch the target to a network printer.");
        }

        UsbPrintTarget usb = new UsbPrintTarget(manager, chosen);
        usb.open(); // fails fast with a readable reason if permission is missing
        usbTarget = usb;
        currentId = PrinterCatalog.USB + chosen.getDeviceName();
        return usb;
    }

    // ------------------------------------------------------------------ announcing on the network

    /**
     * Announces the raw port with DNS-SD as "_pdl-datastream._tcp", the standard name for port 9100 printing.
     * Another phone running this app then lists this printer in its Print menu, and CUPS on Linux or macOS
     * finds it too. Failure here is logged and otherwise harmless: typing the address still works.
     */
    private void advertise(int port) {
        try {
            nsd = (NsdManager) getSystemService(Context.NSD_SERVICE);
            if (nsd == null) return;
            NsdServiceInfo info = new NsdServiceInfo();
            info.setServiceName("Printer bridge " + Build.MODEL);
            info.setServiceType("_pdl-datastream._tcp.");
            info.setPort(port);
            try {
                info.setAttribute("ty", target.getName());
                info.setAttribute("pdl", "application/octet-stream");
            } catch (Throwable ignored) {
            }
            nsdListener = new NsdManager.RegistrationListener() {
                @Override
                public void onServiceRegistered(NsdServiceInfo registered) {
                    advertisedName = registered.getServiceName();
                    Log.i("Announced on the network as \"" + advertisedName + "\".");
                }

                @Override
                public void onRegistrationFailed(NsdServiceInfo i, int code) {
                    Log.w("Could not announce the printer on the network (code " + code + ").");
                }

                @Override
                public void onServiceUnregistered(NsdServiceInfo i) {
                }

                @Override
                public void onUnregistrationFailed(NsdServiceInfo i, int code) {
                }
            };
            nsd.registerService(info, NsdManager.PROTOCOL_DNS_SD, nsdListener);
        } catch (Throwable t) {
            Log.w("Could not announce the printer on the network: " + t);
            nsdListener = null;
        }
    }

    private void unadvertise() {
        NsdManager.RegistrationListener l = nsdListener;
        nsdListener = null;
        advertisedName = null;
        if (l != null && nsd != null) {
            try {
                nsd.unregisterService(l);
            } catch (Throwable ignored) {
            }
        }
    }

    @SuppressWarnings("deprecation")
    private void acquireLocks() {
        try {
            WifiManager wifi = (WifiManager) getApplicationContext().getSystemService(Context.WIFI_SERVICE);
            if (wifi != null && wifiLock == null) {
                // High-performance mode keeps the radio awake for quick prints; a cool-down settles for the normal lock.
                int mode = Throttle.isActive(this) ? WifiManager.WIFI_MODE_FULL : WifiManager.WIFI_MODE_FULL_HIGH_PERF;
                wifiLock = wifi.createWifiLock(mode, "UsbLanBridge");
                wifiLock.setReferenceCounted(false);
                wifiLock.acquire();
            }
            PowerManager power = (PowerManager) getSystemService(Context.POWER_SERVICE);
            if (power != null && wakeLock == null) {
                wakeLock = power.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "UsbLanBridge:wake");
                wakeLock.setReferenceCounted(false);
                wakeLock.acquire();
            }
        } catch (Exception e) {
            Log.w("Could not hold Wi-Fi and wake locks: " + e.getMessage());
        }
    }

    private void releaseWifiLock() {
        try {
            if (wifiLock != null && wifiLock.isHeld()) wifiLock.release();
        } catch (Exception ignored) {
        }
        wifiLock = null;
    }

    private void releaseLocks() {
        releaseWifiLock();
        try {
            if (wakeLock != null && wakeLock.isHeld()) wakeLock.release();
        } catch (Exception ignored) {
        }
        wakeLock = null;
    }

    private void shutdownServers() {
        unadvertise();
        currentId = null;
        if (rawServer != null) {
            rawServer.stop();
            rawServer = null;
        }
        if (eposServer != null) {
            eposServer.stop();
            eposServer = null;
        }
        if (httpsServer != null) {
            httpsServer.stop();
            httpsServer = null;
        }
        if (scaleServer != null) {
            scaleServer.stop();
            scaleServer = null;
        }
        if (scaleReader != null) {
            scaleReader.stop();
            scaleReader = null;
        }
        if (usbTarget != null) {
            usbTarget.close();
            usbTarget = null;
        }
        if (sunmiTarget != null) {
            sunmiTarget.close();
            sunmiTarget = null;
        }
        if (bluetoothTarget != null) {
            bluetoothTarget.close();
            bluetoothTarget = null;
        }
        target = null;
    }

    private void shutdown() {
        shutdownServers();
        releaseLocks();
        statusText = "Stopped";
        instance = null;
        DeviceMonitor.get(this).release("service");
        Log.i("Bridge stopped.");
        stopForegroundCompat();
    }

    @Override
    public void onDestroy() {
        shutdown();
        super.onDestroy();
    }

    // ------------------------------------------------------------------ notification

    private void startForegroundSafely(String text) {
        createChannel();
        Notification n = buildNotification(text);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            startForeground(NOTIFICATION_ID, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE);
        } else {
            startForeground(NOTIFICATION_ID, n);
        }
    }

    private void createChannel() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return;
        NotificationManager nm = (NotificationManager) getSystemService(Context.NOTIFICATION_SERVICE);
        if (nm == null) return;
        NotificationChannel channel = new NotificationChannel(CHANNEL_ID, "Printer bridge", NotificationManager.IMPORTANCE_LOW);
        channel.setDescription("Shows that the printer is being shared over Wi-Fi.");
        nm.createNotificationChannel(channel);
    }

    private Notification buildNotification(String text) {
        Intent open = new Intent(this, MainActivity.class);
        int flags = PendingIntent.FLAG_UPDATE_CURRENT;
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) flags |= PendingIntent.FLAG_IMMUTABLE;
        PendingIntent pending = PendingIntent.getActivity(this, 0, open, flags);

        Notification.Builder b = Build.VERSION.SDK_INT >= Build.VERSION_CODES.O
                ? new Notification.Builder(this, CHANNEL_ID)
                : new Notification.Builder(this);
        return b.setContentTitle("Printer shared over Wi-Fi")
                .setContentText(text)
                .setSmallIcon(android.R.drawable.stat_sys_upload)
                .setContentIntent(pending)
                .setOngoing(true)
                .build();
    }

    private void updateNotification(String text) {
        try {
            NotificationManager nm = (NotificationManager) getSystemService(Context.NOTIFICATION_SERVICE);
            if (nm != null) nm.notify(NOTIFICATION_ID, buildNotification(text));
        } catch (Exception ignored) {
        }
    }

    @SuppressWarnings("deprecation")
    private void stopForegroundCompat() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) stopForeground(Service.STOP_FOREGROUND_REMOVE);
        else stopForeground(true);
    }
}
