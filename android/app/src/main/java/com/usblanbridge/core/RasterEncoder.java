package com.usblanbridge.core;

import java.io.ByteArrayOutputStream;
import java.util.Arrays;

/**
 * Turns a grayscale page into ESC/POS raster bands (GS v 0), the one bit-image command every receipt printer
 * understands. This is how a page rendered by Android's print framework, styling and all, reaches a printer
 * that only knows how to put dots on paper.
 *
 * Deliberately free of Android imports so it can be verified on a desktop JVM. The Android side only has to
 * produce gray pixels; everything about dithering, trimming and the byte layout lives here.
 */
public final class RasterEncoder {

    /** Rows per GS v 0 command. Some firmware caps a single raster command, so pages go out in bands. */
    public static final int BAND_ROWS = 256;

    /** Printable dots across the paper at 203 dpi: 72 mm on an 80 mm roll, 48 mm on a 58 mm roll. */
    public static final int DOTS_80MM = 576;
    public static final int DOTS_58MM = 384;

    /** Full paper width in dots at 203 dpi, used to size the render before cropping to the printable area. */
    public static final int PAPER_80MM = 639;
    public static final int PAPER_58MM = 463;

    private RasterEncoder() {
    }

    /**
     * Encodes one page. gray holds width*height samples, 0 = black, 255 = white, row-major.
     * Blank rows at the top and bottom are dropped, so a page that is mostly empty does not feed a metre of
     * paper. Returns an empty array for a page with nothing on it.
     */
    public static byte[] encode(byte[] gray, int width, int height, boolean dither) {
        if (gray == null || width <= 0 || height <= 0 || gray.length < width * height) return new byte[0];
        byte[] bits = toBits(gray, width, height, dither);
        int bytesPerRow = (width + 7) / 8;
        int first = 0;
        int last = height - 1;
        while (first <= last && rowBlank(bits, first, bytesPerRow)) first++;
        while (last >= first && rowBlank(bits, last, bytesPerRow)) last--;
        if (first > last) return new byte[0];
        return bands(bits, bytesPerRow, first, last);
    }

    /**
     * One bit per pixel, most significant bit first, 1 = black, each row padded to whole bytes: the GS v 0
     * layout. With dithering on, Floyd-Steinberg error diffusion keeps photographs and logos recognisable;
     * without it every pixel is a plain threshold.
     */
    public static byte[] toBits(byte[] gray, int width, int height, boolean dither) {
        int bytesPerRow = (width + 7) / 8;
        byte[] bits = new byte[bytesPerRow * height];
        int[] errCur = new int[width + 2];
        int[] errNext = new int[width + 2];

        for (int y = 0; y < height; y++) {
            int rowIn = y * width;
            int rowOut = y * bytesPerRow;
            if (dither) {
                int[] t = errCur;
                errCur = errNext;
                errNext = t;
                Arrays.fill(errNext, 0);
            }
            for (int x = 0; x < width; x++) {
                int g = stretch(gray[rowIn + x] & 0xFF);
                if (dither) g += errCur[x + 1];
                boolean black = g < 128;
                if (black) bits[rowOut + (x >> 3)] |= (byte) (0x80 >> (x & 7));
                if (dither) {
                    int err = g - (black ? 0 : 255);
                    errCur[x + 2] += err * 7 / 16;
                    errNext[x] += err * 3 / 16;
                    errNext[x + 1] += err * 5 / 16;
                    errNext[x + 2] += err / 16;
                }
            }
        }
        return bits;
    }

    /**
     * Pushes anti-aliased edges toward solid black or white before dithering. Text rendered by a browser has
     * gray fringes on every letter; diffusing those as mid-tones makes the type look fuzzy, while snapping
     * them keeps it crisp. Genuine mid-tones, in the middle of the range, still dither.
     */
    private static int stretch(int g) {
        if (g <= 64) return 0;
        if (g >= 192) return 255;
        return (g - 64) * 255 / 128;
    }

    private static boolean rowBlank(byte[] bits, int row, int bytesPerRow) {
        int start = row * bytesPerRow;
        for (int i = 0; i < bytesPerRow; i++) {
            if (bits[start + i] != 0) return false;
        }
        return true;
    }

    private static byte[] bands(byte[] bits, int bytesPerRow, int first, int last) {
        ByteArrayOutputStream out = new ByteArrayOutputStream((last - first + 1) * bytesPerRow + 64);
        int row = first;
        while (row <= last) {
            int rows = Math.min(BAND_ROWS, last - row + 1);
            out.write(0x1D);
            out.write(0x76);
            out.write(0x30);
            out.write(0x00);                      // normal density
            out.write(bytesPerRow & 0xFF);
            out.write((bytesPerRow >> 8) & 0xFF);
            out.write(rows & 0xFF);
            out.write((rows >> 8) & 0xFF);
            out.write(bits, row * bytesPerRow, rows * bytesPerRow);
            row += rows;
        }
        return out.toByteArray();
    }
}
