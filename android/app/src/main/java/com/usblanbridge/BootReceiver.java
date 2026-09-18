package com.usblanbridge;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.os.Build;

import com.usblanbridge.core.Log;

/** Restarts the bridge after a reboot when the user asked for that. */
public final class BootReceiver extends BroadcastReceiver {

    @Override
    public void onReceive(Context context, Intent intent) {
        if (intent == null || !Intent.ACTION_BOOT_COMPLETED.equals(intent.getAction())) return;
        if (!new Prefs(context).isAutoStart()) return;

        Log.i("Boot completed; starting the bridge.");
        Intent service = new Intent(context, BridgeService.class);
        service.setAction(BridgeService.ACTION_START);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            context.startForegroundService(service);
        } else {
            context.startService(service);
        }
    }
}
