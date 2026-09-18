package com.usblanbridge.core;

import java.io.Closeable;
import java.io.IOException;

/** Somewhere print bytes can be sent: a USB printer, another network printer, or a test sink. */
public interface PrintTarget {

    String getName();

    /** Opens a job. Bytes written are delivered in order; complete() releases it. */
    Job startJob(String documentName) throws IOException;

    /** Closeable so a job can be used with try-with-resources; close() completes it if it is still open. */
    interface Job extends Closeable {
        void write(byte[] buffer, int offset, int count) throws IOException;

        /** Ends the job normally. Safe to call once. */
        void complete() throws IOException;

        /** Discards the job after a failure. */
        void abort();
    }
}
