package com.usblanbridge;

import android.content.Context;

import com.usblanbridge.core.EventLog;
import com.usblanbridge.core.Log;

/**
 * The cool-down switch. Android lets no app slow the CPU or drive the fans, so the only honest thing the bridge
 * can do about a hot or draining phone is to throttle itself: drop the high-performance Wi-Fi lock, pause
 * discovery on the network, rasterise Print-menu pages at half resolution, and read the Device card's sensors
 * less often. Prints still go through. Everything comes back when the timer ends.
 *
 * The end time is kept in the preferences so the service, the print service and the activity all see it.
 */
public final class Throttle {

    public static final int MAX_MINUTES = 240;
    public static final int LOW_DPI = 100;

    private Throttle() {
    }

    public static boolean isActive(Context c) {
        return new Prefs(c).getThrottleUntil() > System.currentTimeMillis();
    }

    public static long leftMs(Context c) {
        long left = new Prefs(c).getThrottleUntil() - System.currentTimeMillis();
        return left > 0 ? left : 0;
    }

    public static int clampMinutes(int minutes) {
        return minutes < 1 ? 1 : minutes > MAX_MINUTES ? MAX_MINUTES : minutes;
    }

    public static void start(Context c, int minutes) {
        minutes = clampMinutes(minutes);
        Prefs p = new Prefs(c);
        p.setThrottleMinutes(minutes);
        p.setThrottleUntil(System.currentTimeMillis() + minutes * 60000L);
        Log.w("Cool-down: the bridge throttles itself for " + minutes + " min (normal Wi-Fi lock, no discovery, "
                + LOW_DPI + " dpi Print-menu rendering, slower sensor reads). Prints still go through.");
        EventLog.warning("Cool-down started · " + minutes + " min · bridge throttled");
        BridgeService.applyThrottle(true);
    }

    public static void stop(Context c) {
        end(c, false);
    }

    /** Called by the monitor when it notices the timer has run out. */
    public static void expired(Context c) {
        end(c, true);
    }

    private static void end(Context c, boolean expired) {
        Prefs p = new Prefs(c);
        if (p.getThrottleUntil() == 0) return;
        p.setThrottleUntil(0);
        Log.i(expired ? "Cool-down ended: the bridge is back to full performance." : "Cool-down stopped early: full performance again.");
        EventLog.warning(expired ? "Cool-down ended · full performance" : "Cool-down stopped · full performance");
        BridgeService.applyThrottle(false);
    }

    public static String leftText(long ms) {
        long s = Math.max(0, (ms + 999) / 1000);
        return (s / 60) + ":" + (s % 60 < 10 ? "0" : "") + (s % 60);
    }

    /** One line for the card: what is throttled right now, or that nothing is. The time left is shown separately. */
    public static String describe(Context c) {
        if (!isActive(c)) return "Full performance. Start a cool-down when the phone is hot or the battery must last.";
        return "Throttled: normal Wi-Fi lock, discovery paused, Print-menu pages at " + LOW_DPI + " dpi, sensors every 30 s in the background. Prints still go through.";
    }
}
