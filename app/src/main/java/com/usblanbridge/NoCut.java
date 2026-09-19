package com.usblanbridge;

import android.content.Context;

import com.usblanbridge.core.BrandedPrintTarget;
import com.usblanbridge.core.Log;
import com.usblanbridge.core.NoCutPrintTarget;
import com.usblanbridge.core.PrintTarget;

/**
 * The emergency NO CUT switch: while it is on, every cutter command is removed from every ticket, whatever the
 * POS app or the phone's print dialog asked for, and replaced by a short feed so the paper reaches the tear bar.
 *
 * Wrapping happens in one place, around whatever target the bridge or the print service opens, so every way of
 * printing behaves the same. The preference is read on every write, so ticking the box applies at once.
 */
public final class NoCut {

    private NoCut() {
    }

    public static PrintTarget wrap(Context context, PrintTarget target) {
        final Prefs prefs = new Prefs(context.getApplicationContext());
        return new NoCutPrintTarget(target, new NoCutPrintTarget.Policy() {
            @Override
            public int feedLines() {
                return prefs.isNoCut() ? prefs.getNoCutFeedLines() : -1;
            }
        }, new NoCutPrintTarget.Listener() {
            @Override
            public void cutRemoved(String printer, String document, String command, int feedLines) {
                Log.i("NO CUT: " + command + " removed from \"" + document + "\" on " + printer
                        + (feedLines > 0 ? ", fed " + feedLines + " lines instead." : "."));
            }
        });
    }

    /** Strips the footer and NO CUT layers, in whichever order they were applied, to reach the real printer. */
    public static PrintTarget unwrapAll(PrintTarget target) {
        PrintTarget t = target;
        for (int i = 0; i < 4; i++) {
            if (t instanceof BrandedPrintTarget) t = ((BrandedPrintTarget) t).unwrap();
            else if (t instanceof NoCutPrintTarget) t = ((NoCutPrintTarget) t).unwrap();
            else break;
        }
        return t;
    }

    public static String describe(Context context) {
        Prefs prefs = new Prefs(context);
        if (!prefs.isNoCut()) return "Cutter commands are forwarded to the printer as the app sends them.";
        return "NO CUT is ON: every cutter command is removed from every ticket and replaced by a "
                + prefs.getNoCutFeedLines() + "-line feed. Tear receipts by hand.";
    }
}
