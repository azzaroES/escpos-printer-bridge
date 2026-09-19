package com.usblanbridge;

import android.content.Context;

import com.usblanbridge.core.BrandedPrintTarget;
import com.usblanbridge.core.FooterInjector;
import com.usblanbridge.core.License;
import com.usblanbridge.core.PrintTarget;

/**
 * The footer printed at the bottom of every ticket, and the licence key that removes it.
 *
 * Wrapping happens in one place, around whatever target the bridge or the print service opens, so every way of
 * printing behaves the same. The licence is re-read for each job, which is cheap because the last verified key
 * is remembered, so a key entered in the app applies to the very next ticket.
 */
public final class Branding {

    public static final String FOOTER_TEXT = "digitalstudio.PRO";
    public static final String PRICE_TEXT = "20 EUR";

    private static final byte[] FOOTER = FooterInjector.footerFor(FOOTER_TEXT);

    private static volatile String checkedKey;
    private static volatile License.Info checkedInfo;

    private Branding() {
    }

    /** The licence in force on this device, or null. */
    public static License.Info licence(Context context) {
        String key = new Prefs(context).getLicenseKey();
        if (key == null || key.isEmpty()) return null;
        if (key.equals(checkedKey)) return checkedInfo;
        License.Info info = License.check(key, DeviceId.get(context));
        checkedInfo = info;
        checkedKey = key;
        return info;
    }

    public static boolean isLicensed(Context context) {
        return licence(context) != null;
    }

    public static PrintTarget wrap(Context context, PrintTarget target) {
        final Context app = context.getApplicationContext();
        return new BrandedPrintTarget(target, new BrandedPrintTarget.FooterSource() {
            @Override
            public byte[] footer() {
                return isLicensed(app) ? null : FOOTER;
            }
        });
    }

    /** Two lines for the settings screen: the state of the footer, and this device's id for ordering a key. */
    public static String describe(Context context) {
        String device = DeviceId.pretty(DeviceId.get(context));
        License.Info info = licence(context);
        if (info != null) {
            return "Licensed to " + info.licensee + " on this device. No footer is printed.\nDevice ID: " + device;
        }
        String key = new Prefs(context).getLicenseKey();
        if (key != null && !key.isEmpty() && License.inspect(key) != null) {
            return "The key entered is genuine but names other devices, and a key covers at most "
                    + License.MAX_DEVICES + ". Send this device's ID to get it added.\nDevice ID: " + device;
        }
        return "\"" + FOOTER_TEXT + "\" is printed centred at the bottom of every ticket. A licence key removes it ("
                + PRICE_TEXT + ", up to " + License.MAX_DEVICES + " devices). Send this device's ID when ordering.\nDevice ID: " + device;
    }
}
