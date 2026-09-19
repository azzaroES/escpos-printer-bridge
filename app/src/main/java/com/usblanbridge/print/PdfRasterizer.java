package com.usblanbridge.print;

import android.graphics.Bitmap;
import android.graphics.Color;
import android.graphics.Matrix;
import android.graphics.pdf.PdfRenderer;
import android.os.ParcelFileDescriptor;

import com.usblanbridge.core.Log;

import java.io.Closeable;
import java.io.IOException;

/**
 * Renders the pages Android's print framework hands over, which are PDFs, into gray pixels at the printer's dot
 * pitch. The page is rendered exactly as the app laid it out, so a web page keeps its CSS, fonts and images;
 * nothing is extracted or re-flowed.
 *
 * Pages are rendered in strips so a very long receipt page never needs a bitmap of its full height.
 */
final class PdfRasterizer implements Closeable {

    private static final int STRIP_ROWS = 512;
    /** About two metres of paper at 203 dpi. Anything longer is cut off with a warning rather than exhausting memory. */
    private static final int MAX_ROWS = 16384;

    private final PdfRenderer renderer;

    PdfRasterizer(ParcelFileDescriptor pdf) throws IOException {
        renderer = new PdfRenderer(pdf);
    }

    int pageCount() {
        return renderer.getPageCount();
    }

    /**
     * Renders one page scaled so its width spans the full paper, then keeps only the printable columns.
     * Landscape pages are turned so their long side runs down the paper. outSize receives width and height.
     */
    byte[] render(int index, int paperDots, int marginDots, int printableDots, int[] outSize) {
        PdfRenderer.Page page = renderer.openPage(index);
        try {
            float pw = page.getWidth();
            float ph = page.getHeight();
            boolean rotate = pw > ph;
            float across = rotate ? ph : pw;
            float along = rotate ? pw : ph;
            float scale = paperDots / across;
            int totalRows = Math.max(1, Math.round(along * scale));
            if (totalRows > MAX_ROWS) {
                Log.w("Page " + (index + 1) + " is " + totalRows + " dots long; printing the first " + MAX_ROWS + ".");
                totalRows = MAX_ROWS;
            }

            byte[] gray = new byte[printableDots * totalRows];
            Bitmap strip = Bitmap.createBitmap(paperDots, STRIP_ROWS, Bitmap.Config.ARGB_8888);
            int[] px = new int[paperDots];
            try {
                for (int top = 0; top < totalRows; top += STRIP_ROWS) {
                    int rows = Math.min(STRIP_ROWS, totalRows - top);
                    strip.eraseColor(Color.WHITE);
                    Matrix m = new Matrix();
                    m.setScale(scale, scale);
                    if (rotate) {
                        m.postRotate(90);
                        m.postTranslate(paperDots, 0);
                    }
                    m.postTranslate(0, -top);
                    page.render(strip, null, m, PdfRenderer.Page.RENDER_MODE_FOR_PRINT);

                    for (int y = 0; y < rows; y++) {
                        strip.getPixels(px, 0, paperDots, 0, y, paperDots, 1);
                        int o = (top + y) * printableDots;
                        for (int x = 0; x < printableDots; x++) {
                            int c = px[marginDots + x];
                            gray[o + x] = (byte) ((((c >> 16) & 0xFF) * 299 + ((c >> 8) & 0xFF) * 587 + (c & 0xFF) * 114) / 1000);
                        }
                    }
                }
            } finally {
                strip.recycle();
            }
            outSize[0] = printableDots;
            outSize[1] = totalRows;
            return gray;
        } finally {
            page.close();
        }
    }

    @Override
    public void close() {
        try {
            renderer.close();
        } catch (Throwable ignored) {
        }
    }
}
