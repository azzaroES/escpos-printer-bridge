package com.usblanbridge.core;

import java.io.IOException;

/**
 * Wraps any print target so the emergency NO CUT switch applies to it: while the switch is on, every cutter
 * command is removed from every job, whatever the sending app asked for. Every path through the app, raw 9100,
 * ePOS, the test receipt and Android's own print dialog, ends in a PrintTarget, so this one wrapper covers them.
 *
 * The policy is asked on every write, not once per job, so flipping the switch takes effect immediately, even
 * on a job that is already streaming.
 */
public final class NoCutPrintTarget implements PrintTarget {

    /** Says whether the switch is on: lines to feed per removed cut (0 or more), or -1 when cuts are to be forwarded. */
    public interface Policy {
        int feedLines();
    }

    /** Told about every cut removed, for the log. */
    public interface Listener {
        void cutRemoved(String printer, String document, String command, int feedLines);
    }

    private final PrintTarget inner;
    private final Policy policy;
    private final Listener listener;

    public NoCutPrintTarget(PrintTarget inner, Policy policy, Listener listener) {
        this.inner = inner;
        this.policy = policy;
        this.listener = listener;
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
        return new FilteredJob(inner.startJob(documentName), documentName == null ? "" : documentName);
    }

    private final class FilteredJob implements Job {
        private final Job inner;
        private final String document;
        private CutFilter filter;
        private boolean done;

        FilteredJob(Job inner, String document) {
            this.inner = inner;
            this.document = document;
        }

        @Override
        public void write(byte[] buffer, int offset, int count) throws IOException {
            if (count <= 0) return;
            final int feed = policy == null ? -1 : policy.feedLines();
            if (feed >= 0) {
                if (filter == null) {
                    filter = new CutFilter(feed, new CutFilter.Listener() {
                        @Override
                        public void cutRemoved(String command) {
                            if (listener != null) listener.cutRemoved(getName(), document, command, feed);
                        }
                    });
                }
                filter.setFeedLines(feed);
                filter.process(buffer, offset, count);
                byte[] ready = filter.takeOutput();
                if (ready.length > 0) inner.write(ready, 0, ready.length);
                return;
            }
            // Switched off while this job was streaming: release whatever was held, then pass through.
            if (filter != null) flushFilter();
            inner.write(buffer, offset, count);
        }

        private void flushFilter() throws IOException {
            CutFilter f = filter;
            filter = null;
            f.finish();
            byte[] tail = f.takeOutput();
            if (tail.length > 0) inner.write(tail, 0, tail.length);
        }

        @Override
        public void complete() throws IOException {
            if (done) return;
            done = true;
            if (filter != null) flushFilter();
            inner.complete();
        }

        @Override
        public void abort() {
            done = true;
            filter = null;
            inner.abort();
        }

        @Override
        public void close() throws IOException {
            if (!done) complete();
        }
    }
}
