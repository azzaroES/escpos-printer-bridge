package com.usblanbridge.core;

import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.content.pm.ResolveInfo;
import android.hardware.usb.UsbDevice;
import android.hardware.usb.UsbManager;

import com.usblanbridge.Prefs;

import java.util.ArrayList;
import java.util.List;

/**
 * Finds what this particular device can actually print to.
 *
 * Phones and POS terminals differ enormously: a phone needs a USB printer on an OTG cable, while a Sunmi or
 * similar terminal has a thermal printer wired in and reached through the vendor's own service. Rather than make
 * the user guess, this reports every route that is genuinely available on the hardware in hand.
 */
public final class PrinterScanner {

    /** Package and action of Sunmi's built-in printer service. */
    public static final String SUNMI_PACKAGE = "woyou.aidlservice.jiuiv5";
    public static final String SUNMI_ACTION = "woyou.aidlservice.jiuiv5.IWoyouService";

    /** One printing route the device offers. */
    public static final class Finding {
        /** One of the Prefs TARGET_* values. */
        public final String targetMode;
        public final String label;
        public final String detail;
        public final boolean usable;
        /** For USB, the device name to remember. Empty otherwise. */
        public final String deviceId;

        Finding(String targetMode, String label, String detail, boolean usable, String deviceId) {
            this.targetMode = targetMode;
            this.label = label;
            this.detail = detail;
            this.usable = usable;
            this.deviceId = deviceId;
        }

        @Override
        public String toString() {
            return label + (usable ? "" : "  [" + detail + "]");
        }
    }

    private PrinterScanner() {
    }

    /** True when this looks like a Sunmi terminal with a built-in printer service installed. */
    public static boolean hasSunmiPrinterService(Context context) {
        PackageManager pm = context.getPackageManager();
        try {
            pm.getPackageInfo(SUNMI_PACKAGE, 0);
            return true;
        } catch (PackageManager.NameNotFoundException ignored) {
            // fall through and try resolving the service by action instead
        }
        try {
            Intent intent = new Intent(SUNMI_ACTION);
            intent.setPackage(SUNMI_PACKAGE);
            List<ResolveInfo> hits = pm.queryIntentServices(intent, 0);
            return hits != null && !hits.isEmpty();
        } catch (Throwable ignored) {
            return false;
        }
    }

    /** Best-effort guess at the hardware vendor, used only for the report text. */
    public static String describeDevice() {
        return android.os.Build.MANUFACTURER + " " + android.os.Build.MODEL;
    }

    /**
     * Examines the device and returns every printing route found, usable ones first.
     * Never throws; a route that cannot be inspected is reported as unusable with the reason.
     */
    public static List<Finding> scan(Context context) {
        List<Finding> usable = new ArrayList<>();
        List<Finding> unusable = new ArrayList<>();

        // ---- built-in vendor printer (Sunmi and compatible terminals)
        if (hasSunmiPrinterService(context)) {
            usable.add(new Finding(Prefs.TARGET_SUNMI,
                    "Built-in thermal printer",
                    "Sunmi printer service found on " + describeDevice(),
                    true, ""));
        } else {
            unusable.add(new Finding(Prefs.TARGET_SUNMI,
                    "Built-in thermal printer",
                    "no Sunmi printer service on this device",
                    false, ""));
        }

        // ---- USB printers over OTG
        UsbManager manager = null;
        try {
            manager = (UsbManager) context.getSystemService(Context.USB_SERVICE);
        } catch (Throwable ignored) {
        }

        if (manager == null) {
            unusable.add(new Finding(Prefs.TARGET_USB, "USB printer", "no USB host support", false, ""));
        } else {
            int seen = 0;
            for (UsbDevice device : manager.getDeviceList().values()) {
                seen++;
                String name = safeProductName(device);
                if (UsbPrintTarget.looksPrintable(device)) {
                    boolean permitted = manager.hasPermission(device);
                    usable.add(new Finding(Prefs.TARGET_USB,
                            "USB printer: " + name,
                            permitted ? "ready" : "needs permission",
                            permitted, device.getDeviceName()));
                } else {
                    unusable.add(new Finding(Prefs.TARGET_USB,
                            "USB device: " + name,
                            "no bulk OUT endpoint, so not a printer",
                            false, device.getDeviceName()));
                }
            }
            if (seen == 0) {
                unusable.add(new Finding(Prefs.TARGET_USB, "USB printer",
                        "nothing connected; attach one with an OTG cable", false, ""));
            }
        }

        // ---- paired Bluetooth printers
        if (BluetoothPrintTarget.adapter(context) == null) {
            unusable.add(new Finding(Prefs.TARGET_BLUETOOTH, "Bluetooth printer", "this device has no Bluetooth", false, ""));
        } else if (BluetoothPrintTarget.needsRuntimePermission(context)) {
            unusable.add(new Finding(Prefs.TARGET_BLUETOOTH, "Bluetooth printer", "needs Bluetooth permission", false, ""));
        } else {
            List<android.bluetooth.BluetoothDevice> paired = BluetoothPrintTarget.pairedDevices(context);
            if (paired.isEmpty()) {
                unusable.add(new Finding(Prefs.TARGET_BLUETOOTH, "Bluetooth printer",
                        "nothing paired, or Bluetooth is off; pair the printer in Android settings first", false, ""));
            } else {
                for (android.bluetooth.BluetoothDevice d : paired) {
                    String name = BluetoothPrintTarget.safeName(d);
                    if (name == null || name.isEmpty()) name = d.getAddress();
                    boolean likely = BluetoothPrintTarget.looksLikePrinter(d);
                    Finding f = new Finding(Prefs.TARGET_BLUETOOTH,
                            "Bluetooth: " + name,
                            likely ? "paired, looks like a printer" : "paired, may not be a printer",
                            likely, d.getAddress());
                    if (likely) usable.add(f); else unusable.add(f);
                }
            }
        }

        // ---- forwarding to a printer already on the network is always possible
        usable.add(new Finding(Prefs.TARGET_TCP,
                "Network printer",
                "forward to another printer by address",
                true, ""));

        usable.addAll(unusable);
        return usable;
    }

    /** Human-readable summary suitable for the log. */
    public static String summarise(List<Finding> findings) {
        StringBuilder sb = new StringBuilder();
        sb.append("Printer scan on ").append(describeDevice()).append(":");
        for (Finding f : findings) {
            sb.append("\n  ").append(f.usable ? "[ok]   " : "[no]   ").append(f.label).append("  -  ").append(f.detail);
        }
        return sb.toString();
    }

    private static String safeProductName(UsbDevice device) {
        String name = null;
        try {
            name = device.getProductName();
        } catch (Throwable ignored) {
        }
        if (name == null || name.isEmpty()) {
            name = String.format("%04X:%04X", device.getVendorId(), device.getProductId());
        }
        return name;
    }
}
