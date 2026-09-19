package com.usblanbridge.core;

import java.io.IOException;

/**
 * Wraps any print target so every ticket it prints carries the footer. Every path through the app, raw 9100,
 * ePOS, the test receipt and Android's own print dialog, ends in a PrintTarget, so this one wrapper covers
 * them all.
 *
 * The footer is asked for at the start of each job rather than fixed at construction, so entering a licence
 * key takes effect on the next ticket without restarting anything.
 */
public final class BrandedPrintTarget implements PrintTarget {

    /** Supplies the footer bytes for a job, or null or empty for none. */
    public interface FooterSource {
        byte[] footer();
    }

    private final PrintTarget inner;
    private final FooterSource source;

    public BrandedPrintTarget(PrintTarget inner, FooterSource source) {
        this.inner = inner;
        this.source = source;
    }

    public PrintTarget unwrap() {
        return inner;
    }

    @Override
    public String getName() {
        return inner.getName();
    }

    @Override
    public Job startJob(String documentName) throws IOException {
        byte[] footer = source == null ? null : source.footer();
        Job job = inner.startJob(documentName);
        if (footer == null || footer.length == 0) return job;
        return new BrandedJob(job, new FooterInjector(footer));
    }

    private static final class BrandedJob implements Job {
        private final Job inner;
        private final FooterInjector injector;
        private boolean done;

        BrandedJob(Job inner, FooterInjector injector) {
            this.inner = inner;
            this.injector = injector;
        }

        @Override
        public void write(byte[] buffer, int offset, int count) throws IOException {
            if (count <= 0) return;
            injector.process(buffer, offset, count);
            byte[] ready = injector.takeOutput();
            if (ready.length > 0) inner.write(ready, 0, ready.length);
        }

        @Override
        public void complete() throws IOException {
            if (done) return;
            done = true;
            injector.finish();
            byte[] tail = injector.takeOutput();
            if (tail.length > 0) inner.write(tail, 0, tail.length);
            inner.complete();
        }

        @Override
        public void abort() {
            done = true;
            inner.abort();
        }

        @Override
        public void close() throws IOException {
            if (!done) complete();
        }
    }
}
