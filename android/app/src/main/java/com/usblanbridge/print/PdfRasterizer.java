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
        return render(index, paperDots, marginDots, printableDots, outSize, false);
    }

    /**
     * With lowRes, the page is rendered at half the dot pitch (about 100 dpi) and each pixel is doubled, which
     * quarters the rendering work during a cool-down. The output has the same size either way.
     */
    byte[] render(int index, int paperDots, int marginDots, int printableDots, int[] outSize, boolean lowRes) {
        PdfRenderer.Page page = renderer.openPage(index);
        try {
            int div = lowRes ? 2 : 1;
            float pw = page.getWidth();
            float ph = page.getHeight();
            boolean rotate = pw > ph;
            float across = rotate ? ph : pw;
            float along = rotate ? pw : ph;
            float fullScale = paperDots / across;
            int totalRows = Math.max(1, Math.round(along * fullScale));
            if (totalRows > MAX_ROWS) {
                Log.w("Page " + (index + 1) + " is " + totalRows + " dots long; printing the first " + MAX_ROWS + ".");
                totalRows = MAX_ROWS;
            }

            int bw = paperDots / div;                 // bitmap width in rendered pixels
            float scale = bw / across;
            int smallRows = (totalRows + div - 1) / div;
            byte[] gray = new byte[printableDots * totalRows];
            Bitmap strip = Bitmap.createBitmap(bw, STRIP_ROWS, Bitmap.Config.ARGB_8888);
            int[] px = new int[bw];
            try {
                for (int top = 0; top < smallRows; top += STRIP_ROWS) {
                    int rows = Math.min(STRIP_ROWS, smallRows - top);
                    strip.eraseColor(Color.WHITE);
                    Matrix m = new Matrix();
                    m.setScale(scale, scale);
                    if (rotate) {
                        m.postRotate(90);
                        m.postTranslate(bw, 0);
                    }
                    m.postTranslate(0, -top);
                    page.render(strip, null, m, PdfRenderer.Page.RENDER_MODE_FOR_PRINT);

                    for (int y = 0; y < rows; y++) {
                        strip.getPixels(px, 0, bw, 0, y, bw, 1);
                        for (int r = 0; r < div; r++) {
                            int fullRow = (top + y) * div + r;
                            if (fullRow >= totalRows) break;
                            int o = fullRow * printableDots;
                            for (int x = 0; x < printableDots; x++) {
                                int sx = (marginDots + x) / div;
                                int c = px[sx < bw ? sx : bw - 1];
                                gray[o + x] = (byte) ((((c >> 16) & 0xFF) * 299 + ((c >> 8) & 0xFF) * 587 + (c & 0xFF) * 114) / 1000);
                            }
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
