package com.usblanbridge.print;

import android.app.PendingIntent;
import android.bluetooth.BluetoothDevice;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.hardware.usb.UsbDevice;
import android.hardware.usb.UsbManager;
import android.os.Build;

import com.usblanbridge.Branding;
import com.usblanbridge.BridgeService;
import com.usblanbridge.NoCut;
import com.usblanbridge.Prefs;
import com.usblanbridge.core.BluetoothPrintTarget;
import com.usblanbridge.core.Log;
import com.usblanbridge.core.PrintTarget;
import com.usblanbridge.core.PrinterScanner;
import com.usblanbridge.core.SunmiPrintTarget;
import com.usblanbridge.core.TcpPrintTarget;
import com.usblanbridge.core.UsbPrintTarget;

import java.io.IOException;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;

/**
 * The printers this device can offer to Android's print framework, and how to reach each one.
 *
 * Printer ids are the same strings the bridge uses for its own route, so when the bridge is already running
 * on the requested printer the open connection is shared instead of fighting over the USB interface or the
 * Bluetooth socket.
 */
public final class PrinterCatalog {

    /** Printer id prefixes. BridgeService builds the same ids for its own route so connections can be shared. */
    public static final String USB = "usb:";
    public static final String SUNMI = "sunmi";
    public static final String BT = "bt:";
    public static final String NET = "net:";

    private static final String ACTION_USB_PERMISSION = "com.usblanbridge.USB_PERMISSION_PRINT";
    private static final long USB_PERMISSION_WAIT_MS = 60000;

    static final class Entry {
        final String localId;
        final String name;
        final String description;

        Entry(String localId, String name, String description) {
            this.localId = localId;
            this.name = name;
            this.description = description;
        }
    }

    /** An opened target, with whether it belongs to the running bridge and so must not be closed here. */
    static final class Opened {
        final PrintTarget target;
        final boolean shared;

        Opened(PrintTarget target, boolean shared) {
            this.target = target;
            this.shared = shared;
        }
    }

    private PrinterCatalog() {
    }

    static List<Entry> local(Context context) {
        List<Entry> list = new ArrayList<>();
        Prefs prefs = new Prefs(context);

        try {
            UsbManager manager = (UsbManager) context.getSystemService(Context.USB_SERVICE);
            if (manager != null) {
                for (UsbDevice d : manager.getDeviceList().values()) {
                    if (!UsbPrintTarget.looksPrintable(d)) continue;
                    list.add(new Entry(USB + d.getDeviceName(), productName(d) + " (USB)",
                            manager.hasPermission(d) ? "USB printer on the OTG cable"
                                    : "USB printer; Android asks for permission on the first print"));
                }
            }
        } catch (Throwable t) {
            Log.w("Could not list USB printers: " + t);
        }

        if (PrinterScanner.hasSunmiPrinterService(context)) {
            list.add(new Entry(SUNMI, "Built-in printer", PrinterScanner.describeDevice()));
        }

        try {
            if (BluetoothPrintTarget.adapter(context) != null && !BluetoothPrintTarget.needsRuntimePermission(context)) {
                String chosen = prefs.getBluetoothAddress();
                for (BluetoothDevice d : BluetoothPrintTarget.pairedDevices(context)) {
                    boolean offer = BluetoothPrintTarget.looksLikePrinter(d)
                            || (chosen != null && chosen.equalsIgnoreCase(d.getAddress()));
                    if (!offer) continue;
                    String name = BluetoothPrintTarget.safeName(d);
                    if (name == null || name.isEmpty()) name = d.getAddress();
                    list.add(new Entry(BT + d.getAddress(), name + " (Bluetooth)", "Paired Bluetooth printer " + d.getAddress()));
                }
            }
        } catch (Throwable t) {
            Log.w("Could not list Bluetooth printers: " + t);
        }

        if (Prefs.TARGET_TCP.equals(prefs.getTargetMode()) || prefs.hasTcpHost()) {
            String host = prefs.getTcpHost();
            int port = prefs.getTcpPort();
            if (host != null && !host.trim().isEmpty()) {
                host = host.trim();
                list.add(new Entry(NET + host + ":" + port, "Network printer " + host, "Raw printing to " + host + ":" + port));
            }
        }
        return list;
    }

    /** Opens the printer behind an id. Blocks while a USB permission prompt is answered; call off the main thread. */
    static Opened open(Context context, String localId) throws IOException {
        PrintTarget running = BridgeService.currentTarget();
        if (running != null && localId.equals(BridgeService.currentPrinterId())) {
            return new Opened(running, true);
        }

        PrintTarget raw;
        if (localId.startsWith(USB)) {
            UsbManager manager = (UsbManager) context.getSystemService(Context.USB_SERVICE);
            if (manager == null) throw new IOException("This device has no USB host support.");
            UsbDevice device = manager.getDeviceList().get(localId.substring(USB.length()));
            if (device == null) throw new IOException("The USB printer is no longer connected.");
            if (!manager.hasPermission(device) && !requestUsbPermission(context, manager, device)) {
                throw new IOException("USB access to " + productName(device)
                        + " was not granted. Open the app and tap \"Grant USB access\", then print again.");
            }
            UsbPrintTarget usb = new UsbPrintTarget(manager, device);
            usb.open();
            raw = usb;
        } else if (SUNMI.equals(localId)) {
            SunmiPrintTarget sunmi = new SunmiPrintTarget(context);
            sunmi.connect();
            raw = sunmi;
        } else if (localId.startsWith(BT)) {
            BluetoothPrintTarget bt = new BluetoothPrintTarget(context, localId.substring(BT.length()));
            bt.connect();
            raw = bt;
        } else if (localId.startsWith(NET)) {
            String spec = localId.substring(NET.length());
            int colon = spec.lastIndexOf(':');
            if (colon <= 0) throw new IOException("Bad network printer address: " + spec);
            int port;
            try {
                port = Integer.parseInt(spec.substring(colon + 1));
            } catch (NumberFormatException e) {
                throw new IOException("Bad network printer port: " + spec);
            }
            raw = new TcpPrintTarget(spec.substring(0, colon), port);
        } else {
            throw new IOException("Unknown printer: " + localId);
        }
        return new Opened(Branding.wrap(context, NoCut.wrap(context, raw)), false);
    }

    static void release(Opened opened) {
        if (opened == null || opened.shared) return;
        PrintTarget t = NoCut.unwrapAll(opened.target);
        try {
            if (t instanceof UsbPrintTarget) ((UsbPrintTarget) t).close();
            else if (t instanceof SunmiPrintTarget) ((SunmiPrintTarget) t).close();
            else if (t instanceof BluetoothPrintTarget) ((BluetoothPrintTarget) t).close();
        } catch (Throwable ignored) {
        }
    }

    static String productName(UsbDevice device) {
        String name = null;
        try {
            name = device.getProductName();
        } catch (Throwable ignored) {
        }
        if (name == null || name.isEmpty()) {
            name = String.format("USB %04X:%04X", device.getVendorId(), device.getProductId());
        }
        return name;
    }

    /**
     * Shows Android's USB permission dialog and waits for the answer. The print job is already running, so
     * the user sees the prompt while their print is pending, which is the natural moment for it.
     */
    private static boolean requestUsbPermission(Context context, UsbManager manager, UsbDevice device) {
        final CountDownLatch answered = new CountDownLatch(1);
        final AtomicBoolean granted = new AtomicBoolean();
        BroadcastReceiver receiver = new BroadcastReceiver() {
            @Override
            public void onReceive(Context c, Intent intent) {
                granted.set(intent.getBooleanExtra(UsbManager.EXTRA_PERMISSION_GRANTED, false));
                answered.countDown();
            }
        };
        IntentFilter filter = new IntentFilter(ACTION_USB_PERMISSION);
        try {
            if (Build.VERSION.SDK_INT >= 34) {
                context.registerReceiver(receiver, filter, Context.RECEIVER_NOT_EXPORTED);
            } else {
                context.registerReceiver(receiver, filter);
            }
            Intent intent = new Intent(ACTION_USB_PERMISSION).setPackage(context.getPackageName());
            int flags = Build.VERSION.SDK_INT >= Build.VERSION_CODES.S ? PendingIntent.FLAG_MUTABLE : 0;
            manager.requestPermission(device, PendingIntent.getBroadcast(context, 1, intent, flags));
            Log.i("Asking for USB permission to print on " + productName(device) + "...");
            answered.await(USB_PERMISSION_WAIT_MS, TimeUnit.MILLISECONDS);
        } catch (Throwable t) {
            Log.w("USB permission request failed: " + t);
        } finally {
            try {
                context.unregisterReceiver(receiver);
            } catch (Throwable ignored) {
            }
        }
        return granted.get() || manager.hasPermission(device);
    }
}
