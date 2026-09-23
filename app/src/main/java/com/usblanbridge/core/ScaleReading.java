package com.usblanbridge.core;

import java.util.Locale;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * One weight reading from a scale, plus the parser and the exact EU/US unit conversions. Deliberately free of
 * Android imports so it can be verified on a desktop JVM. Mirrors the Windows ScaleReading/ScaleParser/ScaleUnits.
 */
public final class ScaleReading {

    public final boolean ok;        // a weight was parsed
    public final double weight;
    public final String unit;       // "kg","g","lb","oz" or ""
    public final boolean stable;    // the scale reported a settled weight
    public final String raw;         // the line as received
    public final long atMs;          // System.currentTimeMillis when parsed
    public final boolean hasGrams;
    public final double grams;       // canonical weight in grams (the base for any conversion)

    public static final ScaleReading EMPTY = new ScaleReading(false, 0, "", false, "", false, 0);

    private ScaleReading(boolean ok, double weight, String unit, boolean stable, String raw, boolean hasGrams, double grams) {
        this.ok = ok;
        this.weight = weight;
        this.unit = unit == null ? "" : unit;
        this.stable = stable;
        this.raw = raw == null ? "" : raw;
        this.atMs = System.currentTimeMillis();
        this.hasGrams = hasGrams;
        this.grams = grams;
    }

    // ---- units (exact: 1 lb = 453.59237 g; 1 oz = 28.349523125 g) ----

    public static double gramsPer(String unit) {
        if (unit == null) return 0;
        switch (unit.trim().toLowerCase(Locale.US)) {
            case "g": return 1d;
            case "kg": return 1000d;
            case "lb": return 453.59237d;
            case "oz": return 28.349523125d;
            default: return 0d;
        }
    }

    public static boolean unitKnown(String unit) { return gramsPer(unit) > 0; }

    /** weight in `from` expressed in `to`; returns NaN when either unit is unknown. */
    public static double convert(double weight, String from, String to) {
        double gf = gramsPer(from), gt = gramsPer(to);
        if (gf <= 0 || gt <= 0) return Double.NaN;
        return weight * gf / gt;
    }

    /** The same reading in another unit (kg/g/lb/oz); returns itself when it cannot convert. */
    public ScaleReading inUnit(String target) {
        if (!ok || target == null || target.isEmpty()) return this;
        if (target.equalsIgnoreCase(unit)) return this;
        double v = convert(weight, unit, target);
        if (Double.isNaN(v)) return this;
        return new ScaleReading(true, round4(v), target.trim().toLowerCase(Locale.US), stable, raw, hasGrams, grams);
    }

    public String toJson() {
        StringBuilder sb = new StringBuilder();
        sb.append('{');
        sb.append("\"ok\":").append(ok);
        sb.append(",\"weight\":").append(num(weight));
        sb.append(",\"unit\":\"").append(esc(unit)).append('"');
        sb.append(",\"grams\":").append(hasGrams ? num(grams) : "null");
        sb.append(",\"stable\":").append(stable);
        sb.append(",\"raw\":\"").append(esc(raw)).append('"');
        long age = System.currentTimeMillis() - atMs;
        sb.append(",\"ageMs\":").append(age < 0 ? 0 : age);
        sb.append('}');
        return sb.toString();
    }

    // ---- parser ----

    private static final Pattern NUMBER_UNIT = Pattern.compile(
            "([-+]?\\d{1,7}(?:[.,]\\d{1,4})?)\\s*(kgs?|lbs?|ozs?|grams?|kg|lb|oz|g)\\b", Pattern.CASE_INSENSITIVE);
    private static final Pattern ANY_NUMBER = Pattern.compile("[-+]?\\d{1,7}(?:[.,]\\d{1,4})?");
    private static final Pattern UNIT_ALONE = Pattern.compile("\\b(kgs?|lbs?|ozs?|grams?|kg|lb|oz|g)\\b", Pattern.CASE_INSENSITIVE);
    private static final Pattern STABLE = Pattern.compile("\\bST\\b", Pattern.CASE_INSENSITIVE);
    private static final Pattern UNSTABLE = Pattern.compile("\\b(US|MO|MOTION)\\b", Pattern.CASE_INSENSITIVE);

    public static ScaleReading parse(String line) {
        if (line == null) return new ScaleReading(false, 0, "", false, "", false, 0);
        StringBuilder clean = new StringBuilder(line.length());
        for (int i = 0; i < line.length(); i++) {
            char c = line.charAt(i);
            if (c >= 0x20 && c < 0x7F) clean.append(c);
        }
        String s = clean.toString().trim();
        if (s.isEmpty()) return new ScaleReading(false, 0, "", false, line, false, 0);

        boolean stable = true;
        if (UNSTABLE.matcher(s).find()) stable = false;
        else if (STABLE.matcher(s).find()) stable = true;

        String numText = null;
        String unit = "";
        Matcher mu = NUMBER_UNIT.matcher(s);
        if (mu.find()) {
            numText = mu.group(1);
            unit = normalizeUnit(mu.group(2));
        } else {
            Matcher mn = ANY_NUMBER.matcher(s);
            if (mn.find()) numText = mn.group();
            Matcher ua = UNIT_ALONE.matcher(s);
            if (ua.find()) unit = normalizeUnit(ua.group());
        }
        if (numText == null) return new ScaleReading(false, 0, "", false, line, false, 0);

        double weight;
        try {
            weight = Double.parseDouble(numText.replace(',', '.'));
        } catch (NumberFormatException e) {
            return new ScaleReading(false, 0, "", false, line, false, 0);
        }

        double per = gramsPer(unit);
        boolean hasGrams = per > 0;
        return new ScaleReading(true, weight, unit, stable, line, hasGrams, hasGrams ? weight * per : 0);
    }

    private static String normalizeUnit(String u) {
        if (u == null) return "";
        switch (u.trim().toLowerCase(Locale.US)) {
            case "kg": case "kgs": return "kg";
            case "g": case "gram": case "grams": return "g";
            case "lb": case "lbs": return "lb";
            case "oz": case "ozs": return "oz";
            default: return u.toLowerCase(Locale.US);
        }
    }

    private static double round4(double v) { return Math.round(v * 10000d) / 10000d; }

    /** A clean JSON number: integers without a decimal point, otherwise up to 4 trimmed decimals. */
    static String num(double v) {
        if (v == Math.rint(v) && !Double.isInfinite(v)) return Long.toString((long) v);
        String s = String.format(Locale.US, "%.4f", v);
        int end = s.length();
        while (end > 0 && s.charAt(end - 1) == '0') end--;
        if (end > 0 && s.charAt(end - 1) == '.') end--;
        return s.substring(0, end);
    }

    static String esc(String s) {
        if (s == null) return "";
        StringBuilder sb = new StringBuilder(s.length() + 8);
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            switch (c) {
                case '"': sb.append("\\\""); break;
                case '\\': sb.append("\\\\"); break;
                case '\r': sb.append("\\r"); break;
                case '\n': sb.append("\\n"); break;
                case '\t': sb.append("\\t"); break;
                default:
                    if (c < 0x20) sb.append(String.format(Locale.US, "\\u%04x", (int) c));
                    else sb.append(c);
            }
        }
        return sb.toString();
    }
}
