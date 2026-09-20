package com.usblanbridge;

import android.app.ActivityManager;
import android.bluetooth.BluetoothAdapter;
import android.bluetooth.BluetoothDevice;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.hardware.usb.UsbDevice;
import android.hardware.usb.UsbManager;
import android.net.TrafficStats;
import android.net.wifi.WifiInfo;
import android.net.wifi.WifiManager;
import android.os.BatteryManager;
import android.os.Build;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.PowerManager;

import com.usblanbridge.core.BluetoothPrintTarget;
import com.usblanbridge.core.EventLog;
import com.usblanbridge.core.Log;
import com.usblanbridge.core.PrintHistory;
import com.usblanbridge.core.RawServer;
import com.usblanbridge.core.TelemetryLog;
import com.usblanbridge.core.UsbPrintTarget;
import com.usblanbridge.print.PrinterCatalog;

import java.io.BufferedReader;
import java.io.File;
import java.io.FileReader;
import java.util.ArrayList;
import java.util.Date;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Set;
import java.util.concurrent.CopyOnWriteArrayList;

/**
 * Reads what Android lets an app read about the phone, once a second while the app is open and less often in
 * the background: processor clock, RAM, Wi-Fi signal and traffic, the USB and Bluetooth printers, the battery
 * in full, the temperatures, and the events the printer produced. Keeps a minute of one-second history and an
 * hour of one-minute battery history, and writes a telemetry row every five seconds.
 *
 * Honesty about the readings: Android hides system CPU load from apps since 8.0, so the Processor tile shows
 * the cores' clock as a share of their maximum and is marked as an estimate. GPU load is not available at all.
 * The CPU thermal zone is read where the phone exposes it; the battery sensor is always there.
 */
public final class DeviceMonitor {

    public static final int HISTORY_SECONDS = 60;
    public static final int HISTORY_MINUTES = 60;
    public static final int TELEMETRY_EVERY_MS = 5000;
    public static final int FAST_MS = 1000;
    public static final int BACKGROUND_MS = 5000;
    public static final int THROTTLED_MS = 30000;

    public enum Quality { LIVE, EST, NA }

    public interface Listener {
        void onSnapshot(Snapshot s);
    }

    /** Everything Android reports about the battery. Nullable fields are not available on this phone. */
    public static final class Battery {
        public boolean present;
        public int percent = -1;
        public int status;
        public int plugged;
        public boolean charging;
        public String stateText = "Unknown";
        public String sourceText = "Unplugged";
        public Integer ma;
        public Integer avgMa;
        public Float volts;
        public Float watts;
        public Float tempC;
        public Integer chargeMah;
        public int chargeCounterRaw;
        public boolean chargePlausible;
        public String health = "Unknown";
        public String technology = "";
        public String timeToFull = "—";
        public Integer cycleCount;
    }

    public static final class Snapshot {
        public long time;
        public float cpuPct = Float.NaN;
        public float cpuGHz = Float.NaN;
        public int cpuCores;
        public int cpuRead;
        public float ramPct = Float.NaN;
        public long ramUsedMb;
        public long ramTotalMb;
        public boolean lowMemory;
        public boolean wifiConnected;
        public int wifiSignalPct = -1;
        public int wifiLinkMbps = -1;
        public int wifiRssi;
        public int wifiFrequency;
        public float rxKBps;
        public float txKBps;
        public String usbText = "None";
        public String usbSub = "";
        public Boolean usbOn;
        public String btText = "Off";
        public String btSub = "";
        public Boolean btOn;
        public Float batTempC;
        public Float cpuTempC;
        public String cpuZone;
        public String thermalStatus;
        public Float thermalHeadroom;
        public Battery battery = new Battery();
        public boolean throttled;
        public long throttleLeftMs;
        public float[] cpu, ram, wifi, tBat, tCpu;
        public Boolean[] usbHist, btHist;
        public float[] levelMin, maMin, tBatMin;
        public List<EventLog.Event> events = new ArrayList<>();
        public File telemetryToday;
        public long telemetryBytesToday;
        public int telemetryFiles;
        public int eventsSinceStart;
        public boolean telemetryOn;
    }

    private static DeviceMonitor instance;

    public static synchronized DeviceMonitor get(Context c) {
        if (instance == null) instance = new DeviceMonitor(c.getApplicationContext());
        return instance;
    }

    private final Context app;
    private final Prefs prefs;
    private final TelemetryLog telemetry;
    private final Set<String> holders = new HashSet<>();
    private final List<Listener> listeners = new CopyOnWriteArrayList<>();
    private HandlerThread thread;
    private Handler handler;
    private volatile boolean running;
    private volatile boolean visible;
    private volatile Snapshot last;

    private final float[] cpu = nan(HISTORY_SECONDS), ram = nan(HISTORY_SECONDS), wifi = nan(HISTORY_SECONDS);
    private final float[] tBat = nan(HISTORY_SECONDS), tCpu = nan(HISTORY_SECONDS);
    private final Boolean[] usbHist = new Boolean[HISTORY_SECONDS], btHist = new Boolean[HISTORY_SECONDS];
    private final float[] levelMin = nan(HISTORY_MINUTES), maMin = nan(HISTORY_MINUTES), tBatMin = nan(HISTORY_MINUTES);
    private long lastMinute = -1;
    private long lastRx = -1, lastTx = -1, lastNetTime;
    private long lastTelemetry;
    private boolean throttledSeen;
    private String cpuZonePath;
    private String cpuZoneName;
    private long zoneScanTime;
    private int eventsSinceStart;

    private final Runnable tick = new Runnable() {
        @Override
        public void run() {
            if (!running) return;
            try {
                sample();
            } catch (Throwable t) {
                Log.w("Device monitor: " + t);
            }
            if (running && handler != null) handler.postDelayed(this, interval());
        }
    };

    private DeviceMonitor(Context app) {
        this.app = app;
        this.prefs = new Prefs(app);
        File dir = app.getExternalFilesDir(null);
        this.telemetry = new TelemetryLog(dir == null ? app.getFilesDir() : dir);

        EventLog.addListener(new EventLog.Listener() {
            @Override
            public void onEvent(EventLog.Event e) {
                eventsSinceStart++;
                telemetry.noteEvent(e.time, e.kind.name().toLowerCase(Locale.US) + ": " + e.text);
            }
        });
        PrintHistory.addListener(new PrintHistory.Listener() {
            @Override
            public void onRecord(PrintHistory.Record r) {
                String size = RawServer.formatBytes(r.bytes);
                if (!r.failed()) {
                    EventLog.printed("Printed " + size + " · " + r.printer + " · " + r.path + " · from " + r.source);
                } else if (looksOffline(r.status)) {
                    EventLog.offline("Printer not answering · " + r.printer + " · " + r.status);
                } else {
                    EventLog.failed("Job failed · " + size + " · " + r.printer + " · " + r.status);
                }
            }
        });
    }

    private static boolean looksOffline(String status) {
        if (status == null) return false;
        String s = status.toLowerCase(Locale.US);
        return s.contains("refused") || s.contains("unreachable") || s.contains("timed out") || s.contains("timeout")
                || s.contains("no route") || s.contains("not answering") || s.contains("disconnected") || s.contains("not connected");
    }

    // ------------------------------------------------------------------ lifecycle

    /** Keeps the monitor running while at least one holder wants it (the activity while open, the service while sharing). */
    public synchronized void acquire(String holder) {
        holders.add(holder);
        if (running) return;
        running = true;
        thread = new HandlerThread("device-monitor");
        thread.start();
        handler = new Handler(thread.getLooper());
        handler.post(tick);
        Log.i("Device monitor started; telemetry in " + telemetry.fileFor(new Date()).getParentFile());
    }

    public synchronized void release(String holder) {
        holders.remove(holder);
        if (!holders.isEmpty() || !running) return;
        running = false;
        Handler h = handler;
        handler = null;
        if (h != null) h.removeCallbacks(tick);
        HandlerThread t = thread;
        thread = null;
        if (t != null) t.quitSafely();
    }

    /** Once a second while the cards are on screen; less often otherwise. */
    public void setVisible(boolean on) {
        boolean was = visible;
        visible = on;
        Handler h = handler;
        if (on && !was && h != null && running) {
            h.removeCallbacks(tick);
            h.post(tick);
        }
    }

    private long interval() {
        if (visible) return FAST_MS;
        return throttledSeen ? THROTTLED_MS : BACKGROUND_MS;
    }

    public void addListener(Listener l) {
        listeners.add(l);
    }

    public void removeListener(Listener l) {
        listeners.remove(l);
    }

    public Snapshot last() {
        return last;
    }

    public TelemetryLog telemetry() {
        return telemetry;
    }

    /** Deletes every telemetry file. Returns how many. */
    public int clearTelemetry() {
        int n = telemetry.clear();
        Log.i("Telemetry logs cleared (" + n + " file" + (n == 1 ? "" : "s") + ").");
        return n;
    }

    // ------------------------------------------------------------------ sampling

    private void sample() {
        long now = System.currentTimeMillis();
        Snapshot s = new Snapshot();
        s.time = now;
        readCpu(s);
        readRam(s);
        readWifi(s);
        readNet(s, now);
        readPrinters(s);
        readBattery(s);
        readThermal(s, now);

        long until = prefs.getThrottleUntil();
        s.throttled = until > now;
        s.throttleLeftMs = s.throttled ? until - now : 0;
        if (throttledSeen && !s.throttled) Throttle.expired(app);
        throttledSeen = s.throttled;

        push(cpu, s.cpuPct);
        push(ram, s.ramPct);
        push(wifi, s.wifiConnected && s.wifiSignalPct >= 0 ? s.wifiSignalPct : Float.NaN);
        push(tBat, s.batTempC == null ? Float.NaN : s.batTempC);
        push(tCpu, s.cpuTempC == null ? Float.NaN : s.cpuTempC);
        push(usbHist, s.usbOn);
        push(btHist, s.btOn);

        long minute = now / 60000L;
        if (minute != lastMinute) {
            if (lastMinute >= 0) {
                long gap = Math.min(HISTORY_MINUTES, minute - lastMinute);
                for (long i = 0; i < gap; i++) {
                    push(levelMin, Float.NaN);
                    push(maMin, Float.NaN);
                    push(tBatMin, Float.NaN);
                }
            }
            lastMinute = minute;
        }
        levelMin[HISTORY_MINUTES - 1] = s.battery.percent >= 0 ? s.battery.percent : Float.NaN;
        maMin[HISTORY_MINUTES - 1] = s.battery.ma == null ? Float.NaN : s.battery.ma;
        tBatMin[HISTORY_MINUTES - 1] = s.battery.tempC == null ? Float.NaN : s.battery.tempC;

        s.cpu = cpu.clone();
        s.ram = ram.clone();
        s.wifi = wifi.clone();
        s.tBat = tBat.clone();
        s.tCpu = tCpu.clone();
        s.usbHist = usbHist.clone();
        s.btHist = btHist.clone();
        s.levelMin = levelMin.clone();
        s.maMin = maMin.clone();
        s.tBatMin = tBatMin.clone();
        s.events = EventLog.snapshot();

        s.telemetryOn = prefs.isTelemetryLogged();
        if (s.telemetryOn && now - lastTelemetry >= TELEMETRY_EVERY_MS) {
            lastTelemetry = now;
            telemetry.write(new Date(now),
                    f1(s.cpuPct), f1(s.ramPct), String.valueOf(s.ramUsedMb),
                    s.wifiConnected ? String.valueOf(s.wifiSignalPct) : "", s.wifiConnected ? String.valueOf(s.wifiLinkMbps) : "",
                    f1(s.rxKBps), f1(s.txKBps),
                    s.usbOn == null ? "" : s.usbOn ? "1" : "0", s.btOn == null ? "" : s.btOn ? "1" : "0",
                    s.batTempC == null ? "" : f1(s.batTempC), s.cpuTempC == null ? "" : f1(s.cpuTempC),
                    s.thermalStatus == null ? "" : s.thermalStatus,
                    s.battery.percent >= 0 ? String.valueOf(s.battery.percent) : "", s.battery.stateText,
                    s.battery.ma == null ? "" : String.valueOf(s.battery.ma), s.battery.volts == null ? "" : f2(s.battery.volts),
                    s.throttled ? "1" : "0");
        }
        s.telemetryToday = telemetry.fileFor(new Date(now));
        s.telemetryBytesToday = telemetry.bytesToday();
        s.telemetryFiles = telemetry.files().size();
        s.eventsSinceStart = eventsSinceStart;

        last = s;
        for (Listener l : listeners) {
            try {
                l.onSnapshot(s);
            } catch (Throwable ignored) {
            }
        }
    }

    private void readCpu(Snapshot s) {
        int cores = 0;
        double pctSum = 0, ghzSum = 0;
        int read = 0;
        for (int i = 0; i < 64; i++) {
            File dir = new File("/sys/devices/system/cpu/cpu" + i);
            if (!dir.exists()) break;
            cores++;
            long cur = readLong(new File(dir, "cpufreq/scaling_cur_freq"));
            long max = readLong(new File(dir, "cpufreq/cpuinfo_max_freq"));
            if (cur > 0 && max > 0) {
                pctSum += cur * 100.0 / max;
                ghzSum += cur / 1e6;
                read++;
            }
        }
        s.cpuCores = cores;
        s.cpuRead = read;
        if (read > 0) {
            s.cpuPct = (float) (pctSum / read);
            s.cpuGHz = (float) (ghzSum / read);
        }
    }

    private void readRam(Snapshot s) {
        try {
            ActivityManager am = (ActivityManager) app.getSystemService(Context.ACTIVITY_SERVICE);
            if (am == null) return;
            ActivityManager.MemoryInfo mi = new ActivityManager.MemoryInfo();
            am.getMemoryInfo(mi);
            if (mi.totalMem <= 0) return;
            long used = mi.totalMem - mi.availMem;
            s.ramTotalMb = mi.totalMem / (1024 * 1024);
            s.ramUsedMb = used / (1024 * 1024);
            s.ramPct = (float) (used * 100.0 / mi.totalMem);
            s.lowMemory = mi.lowMemory;
        } catch (Throwable ignored) {
        }
    }

    @SuppressWarnings("deprecation")
    private void readWifi(Snapshot s) {
        try {
            WifiManager wm = (WifiManager) app.getSystemService(Context.WIFI_SERVICE);
            if (wm == null || !wm.isWifiEnabled()) return;
            WifiInfo info = wm.getConnectionInfo();
            if (info == null) return;
            int rssi = info.getRssi();
            int speed = info.getLinkSpeed();
            if (rssi <= -127 || rssi == 0 || speed <= 0) return;
            s.wifiConnected = true;
            s.wifiRssi = rssi;
            s.wifiLinkMbps = speed;
            s.wifiFrequency = info.getFrequency();
            // The same scale Android's own bars use (-100 dBm empty, -55 dBm full), computed here because some
            // vendor ROMs ignore the level count asked of WifiManager.calculateSignalLevel and answer 0 to 4.
            s.wifiSignalPct = Math.max(0, Math.min(100, Math.round((rssi + 100) * 100f / 45f)));
        } catch (Throwable ignored) {
        }
    }

    private void readNet(Snapshot s, long now) {
        long rx = TrafficStats.getTotalRxBytes();
        long tx = TrafficStats.getTotalTxBytes();
        if (rx < 0 || tx < 0) return;
        if (lastRx >= 0 && now > lastNetTime) {
            double secs = (now - lastNetTime) / 1000.0;
            s.rxKBps = (float) Math.max(0, (rx - lastRx) / 1024.0 / secs);
            s.txKBps = (float) Math.max(0, (tx - lastTx) / 1024.0 / secs);
        }
        lastRx = rx;
        lastTx = tx;
        lastNetTime = now;
    }

    private void readPrinters(Snapshot s) {
        String inUse = BridgeService.currentPrinterId();
        try {
            UsbManager um = (UsbManager) app.getSystemService(Context.USB_SERVICE);
            UsbDevice printer = null;
            if (um != null) {
                for (UsbDevice d : um.getDeviceList().values()) {
                    if (UsbPrintTarget.looksPrintable(d)) {
                        printer = d;
                        break;
                    }
                }
            }
            if (printer == null) {
                s.usbText = "None";
                s.usbSub = "no printer on the OTG cable";
                s.usbOn = Boolean.FALSE;
            } else {
                String name = null;
                try {
                    name = printer.getProductName();
                } catch (Throwable ignored) {
                }
                if (name == null || name.isEmpty()) name = String.format(Locale.US, "%04X:%04X", printer.getVendorId(), printer.getProductId());
                boolean allowed = um.hasPermission(printer);
                boolean used = inUse != null && inUse.startsWith(PrinterCatalog.USB);
                s.usbText = name;
                s.usbSub = !allowed ? "no permission yet" : used ? "in use by the bridge" : "ready, permission granted";
                s.usbOn = allowed;
            }
        } catch (Throwable t) {
            s.usbText = "?";
            s.usbSub = "USB not readable";
            s.usbOn = null;
        }

        try {
            BluetoothAdapter adapter = BluetoothPrintTarget.adapter(app);
            if (adapter == null) {
                s.btText = "None";
                s.btSub = "this phone has no Bluetooth";
                s.btOn = null;
            } else if (!adapter.isEnabled()) {
                s.btText = "Off";
                s.btSub = "Bluetooth is switched off";
                s.btOn = Boolean.FALSE;
            } else {
                String address = prefs.getBluetoothAddress();
                if (address == null || address.isEmpty()) {
                    s.btText = "On";
                    s.btSub = "no printer chosen";
                    s.btOn = Boolean.FALSE;
                } else if (BluetoothPrintTarget.needsRuntimePermission(app)) {
                    s.btText = "On";
                    s.btSub = "allow Bluetooth access in the app";
                    s.btOn = null;
                } else {
                    BluetoothDevice chosen = null;
                    for (BluetoothDevice d : BluetoothPrintTarget.pairedDevices(app)) {
                        if (address.equalsIgnoreCase(d.getAddress())) {
                            chosen = d;
                            break;
                        }
                    }
                    boolean used = inUse != null && inUse.startsWith(PrinterCatalog.BT);
                    if (chosen == null) {
                        s.btText = address;
                        s.btSub = "no longer paired";
                        s.btOn = Boolean.FALSE;
                    } else {
                        String name = BluetoothPrintTarget.safeName(chosen);
                        s.btText = name == null || name.isEmpty() ? address : name;
                        s.btSub = used ? "in use by the bridge" : "paired";
                        s.btOn = Boolean.TRUE;
                    }
                }
            }
        } catch (Throwable t) {
            s.btText = "?";
            s.btSub = "Bluetooth not readable";
            s.btOn = null;
        }
    }

    private void readBattery(Snapshot s) {
        Battery b = s.battery;
        try {
            IntentFilter filter = new IntentFilter(Intent.ACTION_BATTERY_CHANGED);
            Intent i = Build.VERSION.SDK_INT >= 33
                    ? app.registerReceiver(null, filter, Context.RECEIVER_NOT_EXPORTED)
                    : app.registerReceiver(null, filter);
            if (i == null) return;
            b.present = i.getBooleanExtra(BatteryManager.EXTRA_PRESENT, true);
            int level = i.getIntExtra(BatteryManager.EXTRA_LEVEL, -1);
            int scale = i.getIntExtra(BatteryManager.EXTRA_SCALE, 100);
            if (level >= 0 && scale > 0) b.percent = Math.round(level * 100f / scale);
            b.status = i.getIntExtra(BatteryManager.EXTRA_STATUS, BatteryManager.BATTERY_STATUS_UNKNOWN);
            b.plugged = i.getIntExtra(BatteryManager.EXTRA_PLUGGED, 0);
            b.charging = b.status == BatteryManager.BATTERY_STATUS_CHARGING || b.status == BatteryManager.BATTERY_STATUS_FULL;
            switch (b.status) {
                case BatteryManager.BATTERY_STATUS_CHARGING: b.stateText = "Charging"; break;
                case BatteryManager.BATTERY_STATUS_DISCHARGING: b.stateText = "Discharging"; break;
                case BatteryManager.BATTERY_STATUS_FULL: b.stateText = "Full"; break;
                case BatteryManager.BATTERY_STATUS_NOT_CHARGING: b.stateText = "Not charging"; break;
                default: b.stateText = "Unknown";
            }
            switch (b.plugged) {
                case BatteryManager.BATTERY_PLUGGED_AC: b.sourceText = "AC charger"; break;
                case BatteryManager.BATTERY_PLUGGED_USB: b.sourceText = "USB"; break;
                case BatteryManager.BATTERY_PLUGGED_WIRELESS: b.sourceText = "Wireless"; break;
                case 8: b.sourceText = "Dock"; break;
                default: b.sourceText = "Unplugged";
            }
            int mv = i.getIntExtra(BatteryManager.EXTRA_VOLTAGE, -1);
            if (mv > 0) b.volts = mv > 100 ? mv / 1000f : mv;   // a few phones report volts already
            int tenths = i.getIntExtra(BatteryManager.EXTRA_TEMPERATURE, Integer.MIN_VALUE);
            if (tenths != Integer.MIN_VALUE) {
                b.tempC = tenths / 10f;
                s.batTempC = b.tempC;
            }
            switch (i.getIntExtra(BatteryManager.EXTRA_HEALTH, BatteryManager.BATTERY_HEALTH_UNKNOWN)) {
                case BatteryManager.BATTERY_HEALTH_GOOD: b.health = "Good"; break;
                case BatteryManager.BATTERY_HEALTH_OVERHEAT: b.health = "Overheating"; break;
                case BatteryManager.BATTERY_HEALTH_DEAD: b.health = "Dead"; break;
                case BatteryManager.BATTERY_HEALTH_OVER_VOLTAGE: b.health = "Over voltage"; break;
                case BatteryManager.BATTERY_HEALTH_UNSPECIFIED_FAILURE: b.health = "Failure"; break;
                case BatteryManager.BATTERY_HEALTH_COLD: b.health = "Cold"; break;
                default: b.health = "Unknown";
            }
            String tech = i.getStringExtra(BatteryManager.EXTRA_TECHNOLOGY);
            b.technology = tech == null ? "" : tech;
            if (Build.VERSION.SDK_INT >= 34) {
                int cycles = i.getIntExtra("android.os.extra.CYCLE_COUNT", -1);
                if (cycles >= 0) b.cycleCount = cycles;
            }

            BatteryManager bm = (BatteryManager) app.getSystemService(Context.BATTERY_SERVICE);
            if (bm != null) {
                int cur = bm.getIntProperty(BatteryManager.BATTERY_PROPERTY_CURRENT_NOW);
                if (cur != Integer.MIN_VALUE && cur != 0) b.ma = normaliseCurrent(cur, b);
                int avg = bm.getIntProperty(BatteryManager.BATTERY_PROPERTY_CURRENT_AVERAGE);
                if (avg != Integer.MIN_VALUE && avg != 0) b.avgMa = normaliseCurrent(avg, b);
                int counter = bm.getIntProperty(BatteryManager.BATTERY_PROPERTY_CHARGE_COUNTER);
                if (counter != Integer.MIN_VALUE && counter > 0) {
                    // Documented as microampere-hours; some vendors report it in other units. Only a value that
                    // makes sense for a phone battery (50 mAh to 20 Ah) is shown as mAh; the rest is shown raw.
                    int mah = counter >= 100000 ? counter / 1000 : counter;
                    b.chargeMah = mah;
                    b.chargeCounterRaw = counter;
                    b.chargePlausible = mah >= 50 && mah <= 20000;
                }
                if (b.volts != null && b.ma != null) b.watts = Math.abs(b.ma) / 1000f * b.volts;
                if (Build.VERSION.SDK_INT >= 28 && b.charging) {
                    long eta = bm.computeChargeTimeRemaining();
                    b.timeToFull = eta > 0 ? durationText(eta) : b.status == BatteryManager.BATTERY_STATUS_FULL ? "full" : "unknown";
                } else {
                    b.timeToFull = b.charging ? "not reported" : "—";
                }
            }
        } catch (Throwable ignored) {
        }
    }

    /** Current in mA, positive while charging, negative while discharging, whatever sign the vendor chose. */
    private static int normaliseCurrent(int raw, Battery b) {
        int ma = Math.abs(raw) < 1000 ? raw : raw / 1000;   // most report microamps; a few milliamps
        boolean charging = b.plugged != 0 && b.status != BatteryManager.BATTERY_STATUS_DISCHARGING;
        if (charging && ma < 0) ma = -ma;
        if (!charging && ma > 0) ma = -ma;
        return ma;
    }

    private static String durationText(long ms) {
        long min = (ms + 30000) / 60000;
        if (min < 60) return min + " min";
        return (min / 60) + " h " + (min % 60) + " min";
    }

    private void readThermal(Snapshot s, long now) {
        if (cpuZonePath == null && now - zoneScanTime > 60000) {
            zoneScanTime = now;
            findCpuZone();
        }
        if (cpuZonePath != null) {
            long raw = readLong(new File(cpuZonePath));
            if (raw != Long.MIN_VALUE) {
                float c = raw > 1000 || raw < -1000 ? raw / 1000f : raw > 200 ? raw / 10f : raw;
                if (c > -40 && c < 150) {
                    s.cpuTempC = c;
                    s.cpuZone = cpuZoneName;
                }
            }
        }
        if (Build.VERSION.SDK_INT >= 29) {
            try {
                PowerManager pm = (PowerManager) app.getSystemService(Context.POWER_SERVICE);
                if (pm != null) {
                    switch (pm.getCurrentThermalStatus()) {
                        case PowerManager.THERMAL_STATUS_NONE: s.thermalStatus = "none"; break;
                        case PowerManager.THERMAL_STATUS_LIGHT: s.thermalStatus = "light"; break;
                        case PowerManager.THERMAL_STATUS_MODERATE: s.thermalStatus = "moderate"; break;
                        case PowerManager.THERMAL_STATUS_SEVERE: s.thermalStatus = "severe"; break;
                        case PowerManager.THERMAL_STATUS_CRITICAL: s.thermalStatus = "critical"; break;
                        case PowerManager.THERMAL_STATUS_EMERGENCY: s.thermalStatus = "emergency"; break;
                        case PowerManager.THERMAL_STATUS_SHUTDOWN: s.thermalStatus = "shutdown"; break;
                        default: s.thermalStatus = "unknown";
                    }
                    if (Build.VERSION.SDK_INT >= 30) {
                        float headroom = pm.getThermalHeadroom(10);
                        if (!Float.isNaN(headroom)) s.thermalHeadroom = headroom;
                    }
                }
            } catch (Throwable ignored) {
            }
        }
    }

    /** Picks the thermal zone that most looks like the processor: "cpu" first, then the SoC-level names. */
    private void findCpuZone() {
        String[] preferred = {"cpu", "soc", "tsens", "mtktscpu", "cluster", "core", "ap"};
        File root = new File("/sys/class/thermal");
        File[] zones = root.listFiles();
        if (zones == null) return;
        String bestPath = null, bestName = null;
        int bestRank = Integer.MAX_VALUE;
        for (File z : zones) {
            if (!z.getName().startsWith("thermal_zone")) continue;
            String type = readText(new File(z, "type"));
            if (type == null) continue;
            String t = type.trim().toLowerCase(Locale.US);
            for (int rank = 0; rank < preferred.length; rank++) {
                if (t.contains(preferred[rank]) && rank < bestRank) {
                    long v = readLong(new File(z, "temp"));
                    if (v == Long.MIN_VALUE || v == 0) break;
                    bestRank = rank;
                    bestPath = new File(z, "temp").getPath();
                    bestName = type.trim();
                    break;
                }
            }
        }
        cpuZonePath = bestPath;
        cpuZoneName = bestName;
    }

    // ------------------------------------------------------------------ small helpers

    private static float[] nan(int n) {
        float[] a = new float[n];
        java.util.Arrays.fill(a, Float.NaN);
        return a;
    }

    private static void push(float[] a, float v) {
        System.arraycopy(a, 1, a, 0, a.length - 1);
        a[a.length - 1] = v;
    }

    private static void push(Boolean[] a, Boolean v) {
        System.arraycopy(a, 1, a, 0, a.length - 1);
        a[a.length - 1] = v;
    }

    private static String f1(float v) {
        return Float.isNaN(v) ? "" : String.format(Locale.US, "%.1f", v);
    }

    private static String f2(float v) {
        return Float.isNaN(v) ? "" : String.format(Locale.US, "%.2f", v);
    }

    private static long readLong(File f) {
        String t = readText(f);
        if (t == null) return Long.MIN_VALUE;
        try {
            return Long.parseLong(t.trim());
        } catch (NumberFormatException e) {
            return Long.MIN_VALUE;
        }
    }

    private static String readText(File f) {
        BufferedReader r = null;
        try {
            r = new BufferedReader(new FileReader(f), 64);
            return r.readLine();
        } catch (Throwable t) {
            return null;
        } finally {
            if (r != null) {
                try {
                    r.close();
                } catch (Throwable ignored) {
                }
            }
        }
    }
}
