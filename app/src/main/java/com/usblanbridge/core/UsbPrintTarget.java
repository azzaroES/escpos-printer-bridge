package com.usblanbridge.core;

import android.hardware.usb.UsbConstants;
import android.hardware.usb.UsbDevice;
import android.hardware.usb.UsbDeviceConnection;
import android.hardware.usb.UsbEndpoint;
import android.hardware.usb.UsbInterface;
import android.hardware.usb.UsbManager;

import java.io.IOException;

/**
 * Sends raw bytes to a USB printer attached to the phone over OTG.
 *
 * Works with any printer that exposes a bulk OUT endpoint, which covers the USB printer class (7) used by
 * essentially all ESC/POS receipt printers, and vendor-specific interfaces as a fallback.
 *
 * The USB device is a single shared resource, so jobs are serialised: one job holds the target while it writes.
 */
public final class UsbPrintTarget implements PrintTarget {

    private static final int TRANSFER_TIMEOUT_MS = 8000;
    private static final int CHUNK = 8 * 1024;

    private final UsbManager manager;
    private final UsbDevice device;
    private final Object lock = new Object();

    private UsbDeviceConnection connection;
    private UsbInterface claimed;
    private UsbEndpoint endpointOut;

    public UsbPrintTarget(UsbManager manager, UsbDevice device) {
        this.manager = manager;
        this.device = device;
    }

    @Override
    public String getName() {
        String product = null;
        try {
            product = device.getProductName();
        } catch (Throwable ignored) {
        }
        if (product == null || product.isEmpty()) {
            product = String.format("USB %04X:%04X", device.getVendorId(), device.getProductId());
        }
        return product;
    }

    public UsbDevice getDevice() {
        return device;
    }

    /** Opens the device and claims a printing interface. Throws with a readable reason if it cannot. */
    public void open() throws IOException {
        synchronized (lock) {
            if (connection != null) return;

            if (!manager.hasPermission(device)) {
                throw new IOException("No USB permission for " + getName() + ". Grant it in the app first.");
            }

            UsbInterface printing = findPrintingInterface(device);
            if (printing == null) {
                throw new IOException(getName() + " has no bulk OUT endpoint, so it is not a printer this app can drive.");
            }

            UsbDeviceConnection conn = manager.openDevice(device);
            if (conn == null) {
                throw new IOException("Could not open " + getName() + ". Unplug and replug it, then grant permission again.");
            }
            if (!conn.claimInterface(printing, true)) {
                conn.close();
                throw new IOException("Another app is holding " + getName() + "; could not claim its interface.");
            }

            UsbEndpoint out = null;
            for (int i = 0; i < printing.getEndpointCount(); i++) {
                UsbEndpoint ep = printing.getEndpoint(i);
                if (ep.getType() == UsbConstants.USB_ENDPOINT_XFER_BULK
                        && ep.getDirection() == UsbConstants.USB_DIR_OUT) {
                    out = ep;
                    break;
                }
            }
            if (out == null) {
                conn.releaseInterface(printing);
                conn.close();
                throw new IOException(getName() + " exposed no bulk OUT endpoint.");
            }

            connection = conn;
            claimed = printing;
            endpointOut = out;
            Log.i("USB printer ready: " + getName());
        }
    }

    public void close() {
        synchronized (lock) {
            if (connection != null) {
                try {
                    if (claimed != null) connection.releaseInterface(claimed);
                } catch (Throwable ignored) {
                }
                try {
                    connection.close();
                } catch (Throwable ignored) {
                }
            }
            connection = null;
            claimed = null;
            endpointOut = null;
        }
    }

    /** An interface with a bulk OUT endpoint; printer class (7) preferred. */
    public static UsbInterface findPrintingInterface(UsbDevice device) {
        UsbInterface fallback = null;
        for (int i = 0; i < device.getInterfaceCount(); i++) {
            UsbInterface intf = device.getInterface(i);
            boolean hasBulkOut = false;
            for (int e = 0; e < intf.getEndpointCount(); e++) {
                UsbEndpoint ep = intf.getEndpoint(e);
                if (ep.getType() == UsbConstants.USB_ENDPOINT_XFER_BULK
                        && ep.getDirection() == UsbConstants.USB_DIR_OUT) {
                    hasBulkOut = true;
                    break;
                }
            }
            if (!hasBulkOut) continue;
            if (intf.getInterfaceClass() == UsbConstants.USB_CLASS_PRINTER) return intf;
            if (fallback == null) fallback = intf;
        }
        return fallback;
    }

    /** True when the device looks like something we can print to. */
    public static boolean looksPrintable(UsbDevice device) {
        return findPrintingInterface(device) != null;
    }

    @Override
    public Job startJob(String documentName) throws IOException {
        open();
        return new UsbJob();
    }

    private final class UsbJob implements Job {
        private boolean done;

        @Override
        public void write(byte[] buffer, int offset, int count) throws IOException {
            if (count <= 0) return;
            synchronized (lock) {
                if (connection == null || endpointOut == null) throw new IOException("USB printer is not open.");
                int sent = 0;
                while (sent < count) {
                    int piece = Math.min(CHUNK, count - sent);
                    // bulkTransfer cannot take an offset before API 18 semantics differ; copy the slice.
                    byte[] slice = new byte[piece];
                    System.arraycopy(buffer, offset + sent, slice, 0, piece);
                    int n = connection.bulkTransfer(endpointOut, slice, piece, TRANSFER_TIMEOUT_MS);
                    if (n < 0) {
                        throw new IOException("USB write failed after " + sent + " of " + count
                                + " bytes. The printer may be off, out of paper, or unplugged.");
                    }
                    sent += n;
                    if (n == 0) throw new IOException("USB printer stopped accepting data.");
                }
            }
        }

        @Override
        public void complete() {
            done = true;
        }

        @Override
        public void abort() {
            done = true;
        }

        @Override
        public void close() {
            if (!done) complete();
        }
    }
}
