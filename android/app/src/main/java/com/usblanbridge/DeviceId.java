package com.usblanbridge;

import android.content.Context;
import android.provider.Settings;

import com.usblanbridge.core.License;

import java.util.Locale;
import java.util.UUID;

/**
 * The identity a licence key is bound to.
 *
 * Android's ANDROID_ID is used because it is the only stable identifier an app can still read without
 * permissions: MAC addresses have been hidden from apps since Android 6, and the IMEI and serial number since
 * Android 10. From Android 8 the value is specific to the app's signing key, so a build signed with a different
 * key sees a different id. Ship every build with the same keystore once keys have been sold.
 */
public final class DeviceId {

    private DeviceId() {
    }

    /** Normalised id, letters and digits only, as it appears inside a licence key. */
    public static String get(Context context) {
        String id = null;
        try {
            id = Settings.Secure.getString(context.getContentResolver(), Settings.Secure.ANDROID_ID);
        } catch (Throwable ignored) {
        }
        String n = License.normalizeDevice(id);
        if (n.length() >= 8) return n;

        // A ROM without ANDROID_ID gets a random id that survives until the app's data is cleared.
        Prefs prefs = new Prefs(context);
        String stored = prefs.getFallbackDeviceId();
        if (stored == null || stored.isEmpty()) {
            stored = UUID.randomUUID().toString().replace("-", "").substring(0, 16);
            prefs.setFallbackDeviceId(stored);
        }
        return stored;
    }

    /** Upper case in groups of four, the form a customer copies into a message. */
    public static String pretty(String id) {
        String n = License.normalizeDevice(id).toUpperCase(Locale.US);
        StringBuilder sb = new StringBuilder(n.length() + n.length() / 4);
        for (int i = 0; i < n.length(); i++) {
            if (i > 0 && i % 4 == 0) sb.append('-');
            sb.append(n.charAt(i));
        }
        return sb.toString();
    }
}
