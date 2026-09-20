package com.usblanbridge.core;

import java.util.ArrayList;
import java.util.List;

/**
 * Printer-related things that happened at a known time, kept so they can be drawn on the same timeline as the
 * device graphs: an order printed, a cut removed, the printer not answering, a job failed.
 *
 * Deliberately free of Android imports so it can be verified on a desktop JVM.
 */
public final class EventLog {

    public enum Kind { PRINTED, WARNING, OFFLINE, FAILED }

    public static final class Event {
        public final long time;
        public final Kind kind;
        public final String text;

        Event(long time, Kind kind, String text) {
            this.time = time;
            this.kind = kind;
            this.text = text;
        }
    }

    public interface Listener {
        void onEvent(Event e);
    }

    private static final int MAX = 200;
    private static final List<Event> EVENTS = new ArrayList<>();
    private static final List<Listener> LISTENERS = new ArrayList<>();

    private EventLog() {
    }

    public static Event add(Kind kind, String text) {
        Event e = new Event(System.currentTimeMillis(), kind, text == null ? "" : text);
        List<Listener> copy;
        synchronized (EVENTS) {
            EVENTS.add(0, e);
            while (EVENTS.size() > MAX) EVENTS.remove(EVENTS.size() - 1);
            copy = new ArrayList<>(LISTENERS);
        }
        for (Listener l : copy) {
            try {
                l.onEvent(e);
            } catch (Throwable ignored) {
            }
        }
        return e;
    }

    public static void printed(String text) { add(Kind.PRINTED, text); }
    public static void warning(String text) { add(Kind.WARNING, text); }
    public static void offline(String text) { add(Kind.OFFLINE, text); }
    public static void failed(String text) { add(Kind.FAILED, text); }

    /** Newest first. */
    public static List<Event> snapshot() {
        synchronized (EVENTS) {
            return new ArrayList<>(EVENTS);
        }
    }

    public static void clear() {
        synchronized (EVENTS) {
            EVENTS.clear();
        }
    }

    public static void addListener(Listener l) {
        synchronized (EVENTS) {
            LISTENERS.add(l);
        }
    }

    public static void removeListener(Listener l) {
        synchronized (EVENTS) {
            LISTENERS.remove(l);
        }
    }

    public static int count() {
        synchronized (EVENTS) {
            return EVENTS.size();
        }
    }
}
