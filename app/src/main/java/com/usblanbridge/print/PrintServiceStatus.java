package com.usblanbridge.print;

import android.content.ComponentName;
import android.content.Context;
import android.os.Build;
import android.provider.Settings;

/**
 * Whether Android is currently letting this app act as a print service. From Android 7 a newly installed
 * print service is on by default and only a disabled list exists; before that the user has to switch it on.
 */
public final class PrintServiceStatus {

    private PrintServiceStatus() {
    }

    public static boolean isEnabled(Context context) {
        ComponentName me = new ComponentName(context, BridgePrintService.class);
        if (contains(read(context, "disabled_print_services"), me)) return false;
        if (Build.VERSION.SDK_INT < 24) return contains(read(context, "enabled_print_services"), me);
        return true;
    }

    public static String describe(Context context) {
        if (isEnabled(context)) {
            return "On. Every app's Print menu lists the printers on this phone: USB, Bluetooth, built-in and "
                    + "network. Pages print as they look on screen, styling included.";
        }
        return "Off. Open Android's print settings and switch on \"USB LAN Printer Bridge\"; after that every "
                + "app's Print menu lists this phone's printers.";
    }

    private static String read(Context context, String key) {
        try {
            return Settings.Secure.getString(context.getContentResolver(), key);
        } catch (Throwable t) {
            return null;
        }
    }

    private static boolean contains(String list, ComponentName me) {
        if (list == null || list.isEmpty()) return false;
        for (String item : list.split(":")) {
            ComponentName c = ComponentName.unflattenFromString(item.trim());
            if (c != null && c.equals(me)) return true;
        }
        return false;
    }
}
