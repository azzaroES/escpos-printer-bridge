package com.usblanbridge;

import android.content.ContentProvider;
import android.content.ContentValues;
import android.content.Context;
import android.database.Cursor;
import android.database.MatrixCursor;
import android.net.Uri;
import android.os.ParcelFileDescriptor;
import android.provider.OpenableColumns;

import java.io.File;
import java.io.FileNotFoundException;

/**
 * Hands one of the app's own log files to another app (a mail client, Drive, WhatsApp) through a content URI,
 * which is the only way Android 7 and later allow a file to be shared. The framework has no FileProvider and
 * this app carries no AndroidX, so this is the minimal equivalent: read-only, one directory, no queries.
 */
public final class FilesProvider extends ContentProvider {

    public static final String AUTHORITY = "com.usblanbridge.files";

    public static Uri uriFor(File file) {
        return new Uri.Builder().scheme("content").authority(AUTHORITY).appendPath(file.getName()).build();
    }

    private File resolve(Uri uri) throws FileNotFoundException {
        Context c = getContext();
        String name = uri.getLastPathSegment();
        if (c == null || name == null || name.contains("/") || name.contains("\\") || name.startsWith(".")) {
            throw new FileNotFoundException(String.valueOf(uri));
        }
        File dir = c.getExternalFilesDir(null);
        if (dir == null) dir = c.getFilesDir();
        File f = new File(dir, name);
        if (!f.isFile()) throw new FileNotFoundException(String.valueOf(uri));
        return f;
    }

    @Override
    public boolean onCreate() {
        return true;
    }

    @Override
    public ParcelFileDescriptor openFile(Uri uri, String mode) throws FileNotFoundException {
        if (!"r".equals(mode)) throw new FileNotFoundException("read-only");
        return ParcelFileDescriptor.open(resolve(uri), ParcelFileDescriptor.MODE_READ_ONLY);
    }

    @Override
    public Cursor query(Uri uri, String[] projection, String selection, String[] args, String order) {
        File f;
        try {
            f = resolve(uri);
        } catch (FileNotFoundException e) {
            return null;
        }
        MatrixCursor c = new MatrixCursor(new String[]{OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE});
        c.addRow(new Object[]{f.getName(), f.length()});
        return c;
    }

    @Override
    public String getType(Uri uri) {
        String name = uri.getLastPathSegment();
        if (name != null && name.endsWith(".csv")) return "text/csv";
        if (name != null && name.endsWith(".cer")) return "application/x-x509-ca-cert";
        return "text/plain";
    }

    @Override
    public Uri insert(Uri uri, ContentValues values) {
        return null;
    }

    @Override
    public int delete(Uri uri, String selection, String[] args) {
        return 0;
    }

    @Override
    public int update(Uri uri, ContentValues values, String selection, String[] args) {
        return 0;
    }
}
