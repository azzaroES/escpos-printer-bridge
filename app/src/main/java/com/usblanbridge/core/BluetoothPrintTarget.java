package com.usblanbridge.core;

import android.bluetooth.BluetoothAdapter;
import android.bluetooth.BluetoothDevice;
import android.bluetooth.BluetoothManager;
import android.bluetooth.BluetoothSocket;
import android.content.Context;
import android.os.Build;

import java.io.IOException;
import java.io.OutputStream;
import java.lang.reflect.Method;
import java.util.ArrayList;
import java.util.List;
import java.util.Set;
import java.util.UUID;

/**
 * A Bluetooth ESC/POS receipt printer, the classic serial-port kind.
 *
 * Most inexpensive thermal printers are Bluetooth only, and they all speak the same Serial Port Profile: open an
 * RFCOMM channel and write ESC/POS bytes. That matches what the bridge already produces.
 *
 * Two quirks of cheap hardware are handled deliberately:
 *   - The standard secure RFCOMM connect often fails on them, so a reflective fallback on channel 1 is tried.
 *   - Their receive buffers are tiny, so data goes out in small chunks with a pause, rather than in one burst
 *     that would be silently truncated.
 *
 * Only devices already paired in Android's Bluetooth settings are used. Discovery is deliberately not performed,
 * which keeps the app clear of location permissions.
 */
public final class BluetoothPrintTarget implements PrintTarget {

    /** Serial Port Profile. Every ESC/POS Bluetooth printer exposes this. */
    private static final UUID SPP_UUID = UUID.fromString("00001101-0000-1000-8000-00805F9B34FB");

    private static final int CHUNK = 2 * 1024;
    private static final int CHUNK_PAUSE_MS = 20;

    private final Context context;
    private final String address;
    private final Object lock = new Object();

    private BluetoothSocket socket;
    private OutputStream stream;
    private String deviceName;

    public BluetoothPrintTarget(Context context, String address) {
        this.context = context.getApplicationContext();
        this.address = address;
    }

    @Override
    public String getName() {
        if (deviceName != null && deviceName.length() > 0) return deviceName + " (Bluetooth)";
        return "Bluetooth printer " + address;
    }

    public static BluetoothAdapter adapter(Context context) {
        try {
            BluetoothManager manager = (BluetoothManager) context.getSystemService(Context.BLUETOOTH_SERVICE);
            return manager == null ? null : manager.getAdapter();
        } catch (Throwable t) {
            return null;
        }
    }

    /** True when the runtime permission needed to reach paired devices is missing. Only applies from Android 12. */
    public static boolean needsRuntimePermission(Context context) {
        if (Build.VERSION.SDK_INT < 31) return false;
        try {
            return context.checkSelfPermission("android.permission.BLUETOOTH_CONNECT")
                    != android.content.pm.PackageManager.PERMISSION_GRANTED;
        } catch (Throwable t) {
            return true;
        }
    }

    /** Paired devices, most likely printers first. Empty when Bluetooth is off or permission is missing. */
    public static List<BluetoothDevice> pairedDevices(Context context) {
        List<BluetoothDevice> likely = new ArrayList<>();
        List<BluetoothDevice> others = new ArrayList<>();
        BluetoothAdapter adapter = adapter(context);
        if (adapter == null || !adapter.isEnabled()) return likely;
        try {
            Set<BluetoothDevice> bonded = adapter.getBondedDevices();
            if (bonded == null) return likely;
            for (BluetoothDevice d : bonded) {
                if (looksLikePrinter(d)) likely.add(d);
                else others.add(d);
            }
        } catch (SecurityException e) {
            Log.w("Bluetooth permission is missing, so paired devices cannot be listed.");
            return likely;
        } catch (Throwable t) {
            Log.e("Could not list paired Bluetooth devices", t);
            return likely;
        }
        likely.addAll(others);
        return likely;
    }

    /** Bluetooth reports an imaging class for printers; the name is a weaker hint used as a fallback. */
    public static boolean looksLikePrinter(BluetoothDevice device) {
        try {
            if (device.getBluetoothClass() != null
                    && device.getBluetoothClass().getMajorDeviceClass() == android.bluetooth.BluetoothClass.Device.Major.IMAGING) {
                return true;
            }
        } catch (Throwable ignored) {
        }
        String name = safeName(device);
        if (name == null) return false;
        String lower = name.toLowerCase();
        return lower.contains("print") || lower.contains("pos") || lower.contains("thermal")
                || lower.contains("receipt") || lower.startsWith("mtp") || lower.startsWith("rpp");
    }

    public static String safeName(BluetoothDevice device) {
        try {
            return device.getName();
        } catch (SecurityException e) {
            return null;
        } catch (Throwable t) {
            return null;
        }
    }

    /** Opens the RFCOMM channel. Throws with a readable reason when it cannot. */
    public void connect() throws IOException {
        synchronized (lock) {
            if (socket != null && socket.isConnected()) return;
        }

        if (needsRuntimePermission(context)) {
            throw new IOException("Bluetooth permission has not been granted. Allow it in the app first.");
        }

        BluetoothAdapter adapter = adapter(context);
        if (adapter == null) throw new IOException("This device has no Bluetooth.");
        if (!adapter.isEnabled()) throw new IOException("Bluetooth is switched off. Turn it on and try again.");

        BluetoothDevice device;
        try {
            device = adapter.getRemoteDevice(address);
        } catch (Throwable t) {
            throw new IOException("Not a valid Bluetooth address: " + address);
        }
        deviceName = safeName(device);

        // Discovery, if running, makes connecting slow and unreliable.
        try {
            adapter.cancelDiscovery();
        } catch (Throwable ignored) {
        }

        IOException firstFailure;
        try {
            BluetoothSocket s = device.createRfcommSocketToServiceRecord(SPP_UUID);
            s.connect();
            attach(s);
            Log.i("Bluetooth printer connected: " + getName());
            return;
        } catch (SecurityException e) {
            throw new IOException("Bluetooth permission was refused while connecting.");
        } catch (IOException e) {
            firstFailure = e;
        }

        // Cheap printers frequently reject the service-record route; channel 1 usually works.
        try {
            Method fallback = device.getClass().getMethod("createRfcommSocket", int.class);
            BluetoothSocket s = (BluetoothSocket) fallback.invoke(device, 1);
            s.connect();
            attach(s);
            Log.i("Bluetooth printer connected on channel 1: " + getName());
            return;
        } catch (Throwable t) {
            Log.w("Bluetooth fallback connect also failed: " + t);
        }

        throw new IOException("Could not open a Bluetooth connection to " + getName()
                + ". Check it is switched on, in range, and paired. Original error: " + firstFailure.getMessage());
    }

    private void attach(BluetoothSocket s) throws IOException {
        synchronized (lock) {
            socket = s;
            stream = s.getOutputStream();
        }
    }

    public void close() {
        synchronized (lock) {
            if (stream != null) {
                try {
                    stream.close();
                } catch (Throwable ignored) {
                }
                stream = null;
            }
            if (socket != null) {
                try {
                    socket.close();
                } catch (Throwable ignored) {
                }
                socket = null;
            }
        }
    }

    @Override
    public Job startJob(String documentName) throws IOException {
        connect();
        return new BluetoothJob();
    }

    private final class BluetoothJob implements Job {
        private boolean done;

        @Override
        public void write(byte[] data, int offset, int count) throws IOException {
            if (count <= 0) return;
            OutputStream out;
            synchronized (lock) {
                out = stream;
            }
            if (out == null) throw new IOException("The Bluetooth connection is not open.");

            int sent = 0;
            while (sent < count) {
                int piece = Math.min(CHUNK, count - sent);
                out.write(data, offset + sent, piece);
                out.flush();
                sent += piece;
                if (sent < count) {
                    try {
                        Thread.sleep(CHUNK_PAUSE_MS);   // small buffers need a moment to drain
                    } catch (InterruptedException e) {
                        Thread.currentThread().interrupt();
                        throw new IOException("Interrupted while sending to the Bluetooth printer.");
                    }
                }
            }
        }

        @Override
        public void complete() throws IOException {
            if (done) return;
            done = true;
            OutputStream out;
            synchronized (lock) {
                out = stream;
            }
            if (out != null) out.flush();
        }

        @Override
        public void abort() {
            done = true;
        }

        @Override
        public void close() throws IOException {
            complete();
        }
    }
}
