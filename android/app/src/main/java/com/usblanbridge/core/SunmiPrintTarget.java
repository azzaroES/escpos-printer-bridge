package com.usblanbridge.core;

import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.ServiceConnection;
import android.os.IBinder;
import android.os.RemoteException;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

import woyou.aidlservice.jiuiv5.ICallback;
import woyou.aidlservice.jiuiv5.IWoyouService;

/**
 * The built-in thermal printer of a Sunmi or compatible POS terminal.
 *
 * The printer is not a USB device here. It is wired to the board and reached through the vendor's own service,
 * which exposes sendRAWData for raw ESC/POS. That is exactly what the bridge already produces, so the bytes go
 * straight through with no translation.
 *
 * A known trap with this interface is that binder transaction ids come from AIDL declaration order, and a
 * mismatch against the terminal's firmware binds successfully but then prints nothing. So this class refuses to
 * print until {@link #validate()} has confirmed the service answers an identity call sensibly.
 */
public final class SunmiPrintTarget implements PrintTarget {

    private static final int CHUNK = 32 * 1024;   // stay well under the binder transaction limit
    private static final int BIND_TIMEOUT_MS = 8000;

    private final Context context;
    private final Object lock = new Object();

    private IWoyouService service;
    private ServiceConnection connection;
    private String serviceVersion;
    private String printerModel;
    private volatile boolean validated;

    public SunmiPrintTarget(Context context) {
        this.context = context.getApplicationContext();
    }

    @Override
    public String getName() {
        String model = printerModel;
        if (model != null && model.length() > 0) return "Built-in printer (" + model + ")";
        return "Built-in printer";
    }

    public String getServiceVersion() {
        return serviceVersion;
    }

    public boolean isValidated() {
        return validated;
    }

    /** Binds the vendor printer service. Throws with a readable reason if it is absent or refuses. */
    public void connect() throws IOException {
        synchronized (lock) {
            if (service != null) return;
        }

        if (!PrinterScanner.hasSunmiPrinterService(context)) {
            throw new IOException("No built-in printer service on this device. "
                    + PrinterScanner.describeDevice() + " does not appear to be a Sunmi terminal.");
        }

        final CountDownLatch ready = new CountDownLatch(1);
        ServiceConnection conn = new ServiceConnection() {
            @Override
            public void onServiceConnected(ComponentName name, IBinder binder) {
                synchronized (lock) {
                    service = IWoyouService.Stub.asInterface(binder);
                }
                ready.countDown();
            }

            @Override
            public void onServiceDisconnected(ComponentName name) {
                synchronized (lock) {
                    service = null;
                    validated = false;
                }
                Log.w("Built-in printer service disconnected.");
            }
        };

        Intent intent = new Intent();
        intent.setPackage(PrinterScanner.SUNMI_PACKAGE);
        intent.setAction(PrinterScanner.SUNMI_ACTION);

        boolean bound;
        try {
            bound = context.bindService(intent, conn, Context.BIND_AUTO_CREATE);
        } catch (Throwable t) {
            throw new IOException("Could not bind the built-in printer service: " + t.getMessage());
        }
        if (!bound) {
            throw new IOException("The built-in printer service refused the connection.");
        }
        connection = conn;

        try {
            if (!ready.await(BIND_TIMEOUT_MS, TimeUnit.MILLISECONDS)) {
                throw new IOException("The built-in printer service did not respond within "
                        + (BIND_TIMEOUT_MS / 1000) + " seconds.");
            }
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            throw new IOException("Interrupted while connecting to the built-in printer service.");
        }

        String problem = validate();
        if (problem != null) throw new IOException(problem);
        Log.i("Built-in printer ready: " + getName() + ", service version " + serviceVersion);
    }

    /**
     * Confirms the interface really matches this firmware, by asking for identity strings and checking they look
     * like answers rather than noise. Returns null when everything is sound, otherwise the reason it is not.
     *
     * This exists because a transaction-id mismatch does not throw; it silently misroutes calls. Printing without
     * this check is how you end up with a printer that "binds fine" and never prints.
     */
    public String validate() {
        IWoyouService svc;
        synchronized (lock) {
            svc = service;
        }
        if (svc == null) return "The built-in printer service is not connected.";

        try {
            serviceVersion = svc.getServiceVersion();
        } catch (Throwable t) {
            return "The built-in printer service rejected a basic call: " + t
                    + ". Its interface does not match this firmware, so printing through it would silently fail.";
        }

        if (serviceVersion == null || serviceVersion.trim().isEmpty() || !isPrintable(serviceVersion)) {
            return "The built-in printer service returned an unreadable version string, "
                    + "which means the interface does not line up with this firmware. Refusing to print through it.";
        }

        try {
            printerModel = svc.getPrinterModal();
        } catch (Throwable ignored) {
            printerModel = null;   // optional; some firmware omits it
        }

        validated = true;
        return null;
    }

    private static boolean isPrintable(String s) {
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            if (c < 0x20 && c != '\t' && c != '\n' && c != '\r') return false;
        }
        return true;
    }

    public void close() {
        ServiceConnection conn;
        synchronized (lock) {
            conn = connection;
            connection = null;
            service = null;
            validated = false;
        }
        if (conn != null) {
            try {
                context.unbindService(conn);
            } catch (Throwable ignored) {
            }
        }
    }

    @Override
    public Job startJob(String documentName) throws IOException {
        connect();
        return new SunmiJob();
    }

    /** Buffers the receipt, then hands it to the print head in binder-sized chunks. */
    private final class SunmiJob implements Job {
        private final ByteArrayOutputStream buffer = new ByteArrayOutputStream(8192);
        private boolean done;

        @Override
        public void write(byte[] data, int offset, int count) {
            if (count > 0) buffer.write(data, offset, count);
        }

        @Override
        public void complete() throws IOException {
            if (done) return;
            done = true;

            IWoyouService svc;
            synchronized (lock) {
                svc = service;
            }
            if (svc == null) throw new IOException("The built-in printer service disconnected mid-job.");
            if (!validated) throw new IOException("The built-in printer interface was never validated; refusing to print.");

            byte[] all = buffer.toByteArray();
            if (all.length == 0) return;

            final StringBuilder failure = new StringBuilder();
            ICallback callback = new ICallback.Stub() {
                @Override
                public void onRunResult(boolean isSuccess) {
                    if (!isSuccess) failure.append("the printer reported failure");
                }

                @Override
                public void onReturnString(String result) {
                }

                @Override
                public void onRaiseException(int code, String msg) {
                    failure.append("printer exception ").append(code).append(": ").append(msg);
                }

                @Override
                public void onPrintResult(int code, String msg) {
                    if (code != 0 && msg != null && !msg.isEmpty()) {
                        failure.append("print result ").append(code).append(": ").append(msg);
                    }
                }
            };

            try {
                int sent = 0;
                while (sent < all.length) {
                    int piece = Math.min(CHUNK, all.length - sent);
                    byte[] slice = new byte[piece];
                    System.arraycopy(all, sent, slice, 0, piece);
                    svc.sendRAWData(slice, callback);
                    sent += piece;
                }
            } catch (RemoteException e) {
                throw new IOException("The built-in printer service failed mid-job: " + e.getMessage());
            }

            if (failure.length() > 0) throw new IOException(failure.toString());
        }

        @Override
        public void abort() {
            done = true;
            buffer.reset();
        }

        @Override
        public void close() throws IOException {
            complete();
        }
    }
}
