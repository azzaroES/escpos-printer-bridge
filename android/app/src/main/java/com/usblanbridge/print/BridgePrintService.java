package com.usblanbridge.print;

import android.os.Handler;
import android.os.Looper;
import android.os.ParcelFileDescriptor;
import android.print.PrintAttributes;
import android.print.PrintJobId;
import android.print.PrintJobInfo;
import android.print.PrinterCapabilitiesInfo;
import android.print.PrinterId;
import android.print.PrinterInfo;
import android.printservice.PrintJob;
import android.printservice.PrintService;
import android.printservice.PrinterDiscoverySession;

import com.usblanbridge.core.Log;
import com.usblanbridge.core.PrintHistory;
import com.usblanbridge.core.PrintTarget;
import com.usblanbridge.core.RasterEncoder;

import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.charset.Charset;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.atomic.AtomicBoolean;

/**
 * Makes this app a printer as far as Android is concerned.
 *
 * Android's print framework talks to print services, not to printers. Once this one is enabled, the Print
 * menu of every app on the phone lists the printers the app can reach, and choosing one hands over the pages
 * as a PDF rendered with the app's own layout. That PDF is rasterised and sent as ESC/POS, so the paper shows
 * exactly what was on screen, styling and all. No third-party print service is needed.
 *
 * Threading is dictated by the framework: PrintJob and PrinterDiscoverySession may only be touched on the
 * main thread, so rendering and printing happen on a worker and results are posted back.
 */
public final class BridgePrintService extends PrintService {

    private static final PrintAttributes.MediaSize ROLL_80 =
            new PrintAttributes.MediaSize("roll_80mm", "80 mm receipt roll", 3150, 11693);
    private static final PrintAttributes.MediaSize ROLL_58 =
            new PrintAttributes.MediaSize("roll_58mm", "58 mm receipt roll", 2283, 11693);
    private static final PrintAttributes.Resolution DPI_203 =
            new PrintAttributes.Resolution("203dpi", "203 dpi", 203, 203);
    /** Five millimetres, enough for the unprintable edge of both roll widths. */
    private static final PrintAttributes.Margins MARGINS = new PrintAttributes.Margins(196, 0, 196, 0);

    private static final byte[] INIT = {0x1B, 0x40};
    private static final byte[] CUT = {0x1D, 0x56, 0x42, 0x00};   // feed to the cutter, partial cut
    private static final int MAX_COPIES = 10;

    private final Handler main = new Handler(Looper.getMainLooper());
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private final Map<PrintJobId, AtomicBoolean> cancelFlags = new ConcurrentHashMap<>();

    @Override
    public void onCreate() {
        super.onCreate();
        Log.setDirectory(getExternalFilesDir(null));
    }

    @Override
    public void onDestroy() {
        worker.shutdownNow();
        super.onDestroy();
    }

    @Override
    protected PrinterDiscoverySession onCreatePrinterDiscoverySession() {
        return new Session();
    }

    @Override
    protected void onRequestCancelPrintJob(PrintJob job) {
        AtomicBoolean flag = cancelFlags.get(job.getId());
        if (flag != null) flag.set(true);
        else job.cancel();
    }

    @Override
    protected void onPrintJobQueued(final PrintJob job) {
        final PrintJobInfo info = job.getInfo();
        final String localId = info.getPrinterId() == null ? null : info.getPrinterId().getLocalId();
        final String label = info.getLabel() == null ? "Document" : info.getLabel();
        final PrintAttributes attributes = info.getAttributes();
        final int copies = Math.max(1, Math.min(MAX_COPIES, info.getCopies()));
        final ParcelFileDescriptor data = job.getDocument().getData();

        if (localId == null || data == null) {
            job.fail("Nothing to print.");
            closeQuietly(data);
            return;
        }
        if (!job.start()) {
            closeQuietly(data);
            return;
        }

        final AtomicBoolean cancelled = new AtomicBoolean();
        cancelFlags.put(job.getId(), cancelled);
        worker.execute(new Runnable() {
            @Override
            public void run() {
                String failure = null;
                try {
                    print(localId, label, attributes, copies, data, cancelled);
                } catch (Throwable t) {
                    failure = t.getMessage() == null ? t.toString() : t.getMessage();
                    Log.e("Android print of \"" + label + "\" failed", t);
                } finally {
                    closeQuietly(data);
                }
                final String problem = failure;
                main.post(new Runnable() {
                    @Override
                    public void run() {
                        cancelFlags.remove(job.getId());
                        try {
                            if (cancelled.get()) job.cancel();
                            else if (problem == null) job.complete();
                            else job.fail(problem);
                        } catch (Throwable t) {
                            Log.w("Could not report the print result to Android: " + t);
                        }
                    }
                });
            }
        });
    }

    private void print(String localId, String label, PrintAttributes attributes, int copies,
                       ParcelFileDescriptor data, AtomicBoolean cancelled) throws IOException {
        // PdfRenderer needs a seekable file; the framework's descriptor is not guaranteed to be one.
        File temp = File.createTempFile("print", ".pdf", getCacheDir());
        PrinterCatalog.Opened opened = null;
        PrintTarget.Job job = null;
        PdfRasterizer pdf = null;
        int pages = 0;
        long bytes = 0;
        try {
            copy(data, temp);

            boolean narrow = attributes != null && attributes.getMediaSize() != null
                    && ROLL_58.getId().equals(attributes.getMediaSize().getId());
            int paperDots = narrow ? RasterEncoder.PAPER_58MM : RasterEncoder.PAPER_80MM;
            int printableDots = narrow ? RasterEncoder.DOTS_58MM : RasterEncoder.DOTS_80MM;
            int marginDots = (paperDots - printableDots) / 2;

            opened = PrinterCatalog.open(this, localId);
            PrintTarget target = opened.target;

            ParcelFileDescriptor fd = ParcelFileDescriptor.open(temp, ParcelFileDescriptor.MODE_READ_ONLY);
            try {
                pdf = new PdfRasterizer(fd);
                pages = pdf.pageCount();
                job = target.startJob(label);
                job.write(INIT, 0, INIT.length);
                for (int copy = 0; copy < copies; copy++) {
                    for (int i = 0; i < pages; i++) {
                        if (cancelled.get()) {
                            job.abort();
                            job = null;
                            Log.i("Android print of \"" + label + "\" cancelled.");
                            return;
                        }
                        int[] size = new int[2];
                        byte[] gray = pdf.render(i, paperDots, marginDots, printableDots, size);
                        byte[] raster = RasterEncoder.encode(gray, size[0], size[1], true);
                        if (raster.length > 0) {
                            job.write(raster, 0, raster.length);
                            bytes += raster.length;
                        }
                    }
                    job.write(CUT, 0, CUT.length);
                }
                job.complete();
                job = null;
            } finally {
                if (pdf != null) pdf.close();
                closeQuietly(fd);
            }

            Log.i("Android print: \"" + label + "\" -> " + target.getName() + ", " + pages + " page(s)"
                    + (copies > 1 ? " x" + copies : "") + ", " + bytes + " bytes of raster.");
            record(label, target.getName(), bytes, "Printed");
        } catch (IOException e) {
            if (job != null) job.abort();
            record(label, opened == null ? "?" : opened.target.getName(), bytes, "Failed: " + e.getMessage());
            throw e;
        } finally {
            if (opened != null) PrinterCatalog.release(opened);
            if (!temp.delete()) temp.deleteOnExit();
        }
    }

    private static void record(String label, String printer, long bytes, String status) {
        try {
            byte[] preview = label.getBytes(Charset.forName("UTF-8"));
            PrintHistory.add("Android print", printer, "system print", preview, preview.length, status);
        } catch (Throwable ignored) {
        }
    }

    private static void copy(ParcelFileDescriptor from, File to) throws IOException {
        InputStream in = new FileInputStream(from.getFileDescriptor());
        OutputStream out = new FileOutputStream(to);
        try {
            byte[] buffer = new byte[64 * 1024];
            int n;
            while ((n = in.read(buffer)) > 0) out.write(buffer, 0, n);
        } finally {
            out.close();
        }
    }

    private static void closeQuietly(ParcelFileDescriptor fd) {
        if (fd == null) return;
        try {
            fd.close();
        } catch (Throwable ignored) {
        }
    }

    // ------------------------------------------------------------------ discovery

    private final class Session extends PrinterDiscoverySession {
        private final RemoteBridgeFinder finder = new RemoteBridgeFinder(BridgePrintService.this);
        private final Set<String> offered = new HashSet<>();

        @Override
        public void onStartPrinterDiscovery(List<PrinterId> priorityList) {
            List<PrinterInfo> printers = new ArrayList<>();
            for (PrinterCatalog.Entry e : PrinterCatalog.local(BridgePrintService.this)) {
                if (offered.add(e.localId)) printers.add(describe(e));
            }
            if (!printers.isEmpty()) addPrinters(printers);
            Log.i("Android print: offering " + printers.size() + " printer(s) on this phone.");

            finder.start(new RemoteBridgeFinder.Listener() {
                @Override
                public void found(final String host, final int port, final String name) {
                    main.post(new Runnable() {
                        @Override
                        public void run() {
                            if (isDestroyed()) return;
                            String id = PrinterCatalog.NET + host + ":" + port;
                            if (!offered.add(id)) return;
                            List<PrinterInfo> one = new ArrayList<>();
                            one.add(describe(new PrinterCatalog.Entry(id, name, "Shared printer at " + host + ":" + port)));
                            addPrinters(one);
                            Log.i("Android print: found another bridge, \"" + name + "\" at " + host + ":" + port + ".");
                        }
                    });
                }
            });
        }

        @Override
        public void onStopPrinterDiscovery() {
            finder.stop();
        }

        @Override
        public void onValidatePrinters(List<PrinterId> printerIds) {
        }

        @Override
        public void onStartPrinterStateTracking(PrinterId printerId) {
        }

        @Override
        public void onStopPrinterStateTracking(PrinterId printerId) {
        }

        @Override
        public void onDestroy() {
            finder.stop();
        }

        private PrinterInfo describe(PrinterCatalog.Entry e) {
            PrinterId id = generatePrinterId(e.localId);
            PrinterCapabilitiesInfo caps = new PrinterCapabilitiesInfo.Builder(id)
                    .addMediaSize(ROLL_80, true)
                    .addMediaSize(ROLL_58, false)
                    .addResolution(DPI_203, true)
                    .setColorModes(PrintAttributes.COLOR_MODE_MONOCHROME, PrintAttributes.COLOR_MODE_MONOCHROME)
                    .setMinMargins(MARGINS)
                    .build();
            return new PrinterInfo.Builder(id, e.name, PrinterInfo.STATUS_IDLE)
                    .setDescription(e.description)
                    .setCapabilities(caps)
                    .build();
        }
    }
}
