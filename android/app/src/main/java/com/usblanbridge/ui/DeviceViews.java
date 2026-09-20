package com.usblanbridge.ui;

import android.animation.Animator;
import android.animation.AnimatorListenerAdapter;
import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.DashPathEffect;
import android.graphics.Paint;
import android.graphics.Path;
import android.graphics.RectF;
import android.graphics.Typeface;
import android.view.Gravity;
import android.view.MotionEvent;
import android.view.View;
import android.view.ViewGroup;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.TextView;

import java.util.ArrayList;
import java.util.List;

/**
 * The custom views behind the Device and Battery cards: a line chart with a touch crosshair and tooltip, an
 * on/off strip, an event timeline, a value tile with a quality chip, and a card that flips over.
 *
 * Plain framework views drawn with Canvas, so the app still carries no dependency. Colours follow the same
 * validated palette as the Windows build: four series colours that stay apart for colour-blind readers, and
 * the four status colours reserved for events.
 */
public final class DeviceViews {

    public static final int SERIES_1 = 0xFF2A78D6;
    public static final int SERIES_2 = 0xFFEB6834;
    public static final int SERIES_3 = 0xFF1BAF7A;
    public static final int SERIES_4 = 0xFFEDA100;
    public static final int GOOD = 0xFF0CA30C;
    public static final int WARNING = 0xFFFAB219;
    public static final int SERIOUS = 0xFFEC835A;
    public static final int CRITICAL = 0xFFD03B3B;
    public static final int TEXT = 0xFF111827;
    public static final int MUTED = 0xFF6B7280;
    public static final int GRID = 0xFFE5E7EB;
    public static final int OFF = 0xFFE5E7EB;
    public static final int TIP_BG = 0xEE111827;

    private DeviceViews() {
    }

    static int dp(View v, float value) {
        return Math.round(v.getResources().getDisplayMetrics().density * value);
    }

    static float sp(View v, float value) {
        return v.getResources().getDisplayMetrics().scaledDensity * value;
    }

    // ------------------------------------------------------------------ line chart

    /** What a tooltip says at a given sample index. */
    public interface Tooltip {
        String[] lines(int index);
    }

    public static final class Series {
        public final String name;
        public final int color;
        public float[] values = new float[0];
        public boolean fill;

        public Series(String name, int color) {
            this.name = name;
            this.color = color;
        }
    }

    public static final class ChartView extends View {
        private final List<Series> series = new ArrayList<>();
        private float min = 0, max = 100;
        private String[] axisLabels = {"100", "50", "0"};
        private String[] timeLabels = {"60 s ago", "30 s", "now"};
        private Float threshold;
        private String thresholdLabel;
        private boolean bars;
        private final List<int[]> markers = new ArrayList<>();
        private int hover = -1;
        private Tooltip tooltip;
        private String emptyText = "Waiting for readings…";

        private final Paint linePaint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Paint fillPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Paint gridPaint = new Paint();
        private final Paint textPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Paint tipPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Paint tipText = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Path path = new Path();
        private final RectF rect = new RectF();
        private final Runnable clearHover = new Runnable() {
            @Override
            public void run() {
                hover = -1;
                invalidate();
            }
        };

        public ChartView(Context c) {
            super(c);
            linePaint.setStyle(Paint.Style.STROKE);
            linePaint.setStrokeWidth(dp(this, 2));
            linePaint.setStrokeCap(Paint.Cap.ROUND);
            linePaint.setStrokeJoin(Paint.Join.ROUND);
            fillPaint.setStyle(Paint.Style.FILL);
            gridPaint.setColor(GRID);
            gridPaint.setStrokeWidth(dp(this, 1));
            textPaint.setColor(MUTED);
            textPaint.setTextSize(sp(this, 10));
            tipPaint.setColor(TIP_BG);
            tipText.setColor(Color.WHITE);
            tipText.setTextSize(sp(this, 11));
            tipText.setTypeface(Typeface.DEFAULT_BOLD);
        }

        public ChartView range(float lo, float hi, String... labels) {
            min = lo;
            max = hi;
            if (labels != null && labels.length > 0) axisLabels = labels;
            return this;
        }

        public ChartView times(String... labels) {
            timeLabels = labels;
            return this;
        }

        public ChartView threshold(Float value, String label) {
            threshold = value;
            thresholdLabel = label;
            return this;
        }

        public ChartView bars(boolean on) {
            bars = on;
            return this;
        }

        public ChartView tooltip(Tooltip t) {
            tooltip = t;
            return this;
        }

        public ChartView empty(String text) {
            emptyText = text;
            return this;
        }

        public Series add(Series s) {
            series.add(s);
            return s;
        }

        public void setMarkers(List<int[]> m) {
            markers.clear();
            if (m != null) markers.addAll(m);
        }

        public void refresh() {
            invalidate();
        }

        private int count() {
            int n = 0;
            for (Series s : series) n = Math.max(n, s.values.length);
            return n;
        }

        private boolean hasData() {
            for (Series s : series) for (float v : s.values) if (!Float.isNaN(v)) return true;
            return false;
        }

        /** Room for the axis labels: at least 30 dp, more when a label such as "−500 mA" needs it. */
        private float padLeft() {
            float widest = 0;
            for (String l : axisLabels) widest = Math.max(widest, textPaint.measureText(l));
            return Math.max(dp(this, 30), widest + dp(this, 8));
        }

        @Override
        protected void onDraw(Canvas canvas) {
            int w = getWidth(), h = getHeight();
            float padL = padLeft(), padR = dp(this, 8), padT = dp(this, 8), padB = dp(this, 16);
            float x0 = padL, x1 = w - padR, top = padT, bottom = h - padB;
            float ph = bottom - top;

            // grid and axis labels, three levels
            for (int i = 0; i < 3; i++) {
                float y = top + ph * i / 2f;
                canvas.drawLine(x0, y, x1, y, gridPaint);
                String label = axisLabels.length > i ? axisLabels[i] : "";
                textPaint.setTextAlign(Paint.Align.RIGHT);
                canvas.drawText(label, x0 - dp(this, 4), y + textPaint.getTextSize() / 3, textPaint);
            }
            // time labels
            if (timeLabels != null && timeLabels.length > 0) {
                float ty = h - dp(this, 3);
                for (int i = 0; i < timeLabels.length; i++) {
                    float x = x0 + (x1 - x0) * i / Math.max(1, timeLabels.length - 1);
                    textPaint.setTextAlign(i == 0 ? Paint.Align.LEFT : i == timeLabels.length - 1 ? Paint.Align.RIGHT : Paint.Align.CENTER);
                    canvas.drawText(timeLabels[i], x, ty, textPaint);
                }
            }

            int n = count();
            if (n < 2 || !hasData()) {
                textPaint.setTextAlign(Paint.Align.CENTER);
                canvas.drawText(emptyText, (x0 + x1) / 2, top + ph / 2, textPaint);
                return;
            }

            float step = (x1 - x0) / (n - 1);
            for (Series s : series) {
                if (bars) {
                    float zero = yOf(0, top, ph);
                    canvas.drawLine(x0, zero, x1, zero, gridPaint);
                    fillPaint.setColor(s.color);
                    float bw = Math.max(1, step - dp(this, 1));
                    for (int i = 0; i < s.values.length; i++) {
                        float v = s.values[i];
                        if (Float.isNaN(v)) continue;
                        float x = x0 + step * i;
                        float y = yOf(v, top, ph);
                        rect.set(x - bw / 2, Math.min(y, zero), x + bw / 2, Math.max(y, zero));
                        if (rect.height() < 1) rect.bottom = rect.top + 1;
                        canvas.drawRect(rect, fillPaint);
                    }
                    continue;
                }
                path.reset();
                boolean open = false;
                float lastX = 0, firstX = 0;
                for (int i = 0; i < s.values.length; i++) {
                    float v = s.values[i];
                    float x = x0 + step * i;
                    if (Float.isNaN(v)) {
                        if (open && s.fill) closeFill(canvas, s, firstX, lastX, bottom);
                        if (open) {
                            linePaint.setColor(s.color);
                            canvas.drawPath(path, linePaint);
                        }
                        path.reset();
                        open = false;
                        continue;
                    }
                    float y = yOf(v, top, ph);
                    if (!open) {
                        path.moveTo(x, y);
                        firstX = x;
                        open = true;
                    } else {
                        path.lineTo(x, y);
                    }
                    lastX = x;
                }
                if (open) {
                    if (s.fill) closeFill(canvas, s, firstX, lastX, bottom);
                    linePaint.setColor(s.color);
                    canvas.drawPath(path, linePaint);
                }
            }

            if (threshold != null) {
                float y = yOf(threshold, top, ph);
                Paint dash = new Paint(gridPaint);
                dash.setColor(CRITICAL);
                dash.setPathEffect(new DashPathEffect(new float[]{dp(this, 4), dp(this, 3)}, 0));
                Path p = new Path();
                p.moveTo(x0, y);
                p.lineTo(x1, y);
                canvas.drawPath(p, dash);
                if (thresholdLabel != null) {
                    textPaint.setTextAlign(Paint.Align.RIGHT);
                    int keep = textPaint.getColor();
                    textPaint.setColor(CRITICAL);
                    canvas.drawText(thresholdLabel, x1 - dp(this, 2), y - dp(this, 3), textPaint);
                    textPaint.setColor(keep);
                }
            }

            // event markers: a short tick at the base of the plot
            for (int[] m : markers) {
                if (m[0] < 0 || m[0] >= n) continue;
                float x = x0 + step * m[0];
                fillPaint.setColor(m[1]);
                rect.set(x - dp(this, 1.5f), bottom - dp(this, 7), x + dp(this, 1.5f), bottom);
                canvas.drawRoundRect(rect, dp(this, 1), dp(this, 1), fillPaint);
            }

            if (hover >= 0 && hover < n) {
                float x = x0 + step * hover;
                Paint cross = new Paint(gridPaint);
                cross.setColor(MUTED);
                canvas.drawLine(x, top, x, bottom, cross);
                for (Series s : series) {
                    if (hover < s.values.length && !Float.isNaN(s.values[hover])) {
                        fillPaint.setColor(s.color);
                        canvas.drawCircle(x, yOf(s.values[hover], top, ph), dp(this, 4), fillPaint);
                        fillPaint.setColor(Color.WHITE);
                        canvas.drawCircle(x, yOf(s.values[hover], top, ph), dp(this, 2), fillPaint);
                    }
                }
                String[] lines = tooltip == null ? defaultTip() : tooltip.lines(hover);
                if (lines != null && lines.length > 0) drawTip(canvas, lines, x, top, x0, x1);
            }
        }

        private String[] defaultTip() {
            List<String> out = new ArrayList<>();
            for (Series s : series) {
                if (hover < s.values.length && !Float.isNaN(s.values[hover])) out.add(s.name + "  " + Math.round(s.values[hover]));
            }
            return out.toArray(new String[0]);
        }

        private void drawTip(Canvas canvas, String[] lines, float x, float top, float x0, float x1) {
            float lineH = tipText.getTextSize() * 1.45f;
            float padX = dp(this, 8), padY = dp(this, 6);
            float wMax = 0;
            for (String l : lines) wMax = Math.max(wMax, tipText.measureText(l));
            float bw = wMax + padX * 2, bh = lineH * lines.length + padY * 2 - lineH * 0.35f;
            float bx = x + dp(this, 10);
            if (bx + bw > x1) bx = x - dp(this, 10) - bw;
            if (bx < x0) bx = x0;
            float by = top;
            rect.set(bx, by, bx + bw, by + bh);
            canvas.drawRoundRect(rect, dp(this, 6), dp(this, 6), tipPaint);
            tipText.setTextAlign(Paint.Align.LEFT);
            for (int i = 0; i < lines.length; i++) {
                canvas.drawText(lines[i], bx + padX, by + padY + lineH * (i + 0.8f), tipText);
            }
        }

        private void closeFill(Canvas canvas, Series s, float firstX, float lastX, float bottom) {
            Path area = new Path(path);
            area.lineTo(lastX, bottom);
            area.lineTo(firstX, bottom);
            area.close();
            fillPaint.setColor((s.color & 0x00FFFFFF) | 0x33000000);
            canvas.drawPath(area, fillPaint);
        }

        private float yOf(float v, float top, float ph) {
            float t = (v - min) / (max - min);
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            return top + ph - t * ph;
        }

        private float downX, downY;
        private boolean claimed;

        /**
         * A finger on the chart shows the crosshair, but the page must still scroll: the gesture is claimed from
         * the enclosing ScrollView only once it moves sideways more than up or down. A vertical drag is left to
         * the page, which cancels the touch here and clears the crosshair.
         */
        @Override
        public boolean onTouchEvent(MotionEvent e) {
            int n = count();
            if (n < 2) return false;
            switch (e.getActionMasked()) {
                case MotionEvent.ACTION_DOWN:
                    downX = e.getX();
                    downY = e.getY();
                    claimed = false;
                    setHover(e.getX(), n);
                    return true;
                case MotionEvent.ACTION_MOVE: {
                    if (!claimed) {
                        float dx = Math.abs(e.getX() - downX), dy = Math.abs(e.getY() - downY);
                        float slop = dp(this, 8);
                        if (dx > slop && dx > dy) {
                            claimed = true;
                            getParent().requestDisallowInterceptTouchEvent(true);
                        }
                    }
                    setHover(e.getX(), n);
                    return true;
                }
                case MotionEvent.ACTION_UP:
                    getParent().requestDisallowInterceptTouchEvent(false);
                    postDelayed(clearHover, 3000);
                    return true;
                case MotionEvent.ACTION_CANCEL:
                    getParent().requestDisallowInterceptTouchEvent(false);
                    hover = -1;
                    invalidate();
                    return true;
                default:
                    return super.onTouchEvent(e);
            }
        }

        private void setHover(float x, int n) {
            float padL = padLeft(), padR = dp(this, 8);
            float x0 = padL, x1 = getWidth() - padR;
            int i = Math.round((x - x0) / (x1 - x0) * (n - 1));
            hover = Math.max(0, Math.min(n - 1, i));
            removeCallbacks(clearHover);
            invalidate();
        }
    }

    // ------------------------------------------------------------------ on/off strip

    public static final class StripView extends View {
        private final List<String> labels = new ArrayList<>();
        private final List<Boolean[]> rows = new ArrayList<>();
        private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Paint textPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final RectF rect = new RectF();

        public StripView(Context c) {
            super(c);
            textPaint.setColor(MUTED);
            textPaint.setTextSize(sp(this, 10.5f));
        }

        public void set(List<String> names, List<Boolean[]> values) {
            labels.clear();
            labels.addAll(names);
            rows.clear();
            rows.addAll(values);
            invalidate();
        }

        @Override
        protected void onDraw(Canvas canvas) {
            int w = getWidth();
            float labelW = dp(this, 78);
            float rowH = dp(this, 14), gap = dp(this, 6);
            float y = 0;
            for (int r = 0; r < rows.size(); r++) {
                textPaint.setTextAlign(Paint.Align.LEFT);
                canvas.drawText(labels.get(r), 0, y + rowH * 0.78f, textPaint);
                Boolean[] v = rows.get(r);
                if (v != null && v.length > 0) {
                    float x0 = labelW, x1 = w;
                    float step = (x1 - x0) / v.length;
                    int start = -1;
                    Boolean cur = null;
                    for (int i = 0; i <= v.length; i++) {
                        Boolean b = i < v.length ? v[i] : null;
                        boolean same = i < v.length && (b == null ? cur == null : b.equals(cur));
                        if (i < v.length && start < 0) {
                            start = i;
                            cur = b;
                            continue;
                        }
                        if (same) continue;
                        if (start >= 0 && cur != null) {
                            paint.setColor(cur ? GOOD : OFF);
                            rect.set(x0 + step * start, y + dp(this, 2), x0 + step * i - dp(this, 0.5f), y + rowH - dp(this, 2));
                            canvas.drawRoundRect(rect, dp(this, 2), dp(this, 2), paint);
                        }
                        start = i < v.length ? i : -1;
                        cur = b;
                    }
                }
                y += rowH + gap;
            }
        }

        @Override
        protected void onMeasure(int widthSpec, int heightSpec) {
            int h = Math.max(1, rows.size()) * (dp(this, 14) + dp(this, 6));
            setMeasuredDimension(MeasureSpec.getSize(widthSpec), h);
        }
    }

    // ------------------------------------------------------------------ event timeline

    public static final class EventMark {
        public final int index;
        public final int color;
        public final String clock;
        public final String text;

        public EventMark(int index, int color, String clock, String text) {
            this.index = index;
            this.color = color;
            this.clock = clock;
            this.text = text;
        }
    }

    public static final class EventStripView extends View {
        private final List<EventMark> marks = new ArrayList<>();
        private int count = 60;
        private int hover = -1;
        private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Paint textPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Paint tipPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Paint tipText = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final RectF rect = new RectF();
        private final Runnable clearHover = new Runnable() {
            @Override
            public void run() {
                hover = -1;
                invalidate();
            }
        };

        public EventStripView(Context c) {
            super(c);
            textPaint.setColor(MUTED);
            textPaint.setTextSize(sp(this, 10));
            tipPaint.setColor(TIP_BG);
            tipText.setColor(Color.WHITE);
            tipText.setTextSize(sp(this, 11));
        }

        public void set(List<EventMark> m, int samples) {
            marks.clear();
            marks.addAll(m);
            count = samples;
            invalidate();
        }

        @Override
        protected void onMeasure(int widthSpec, int heightSpec) {
            setMeasuredDimension(MeasureSpec.getSize(widthSpec), dp(this, 40));
        }

        @Override
        protected void onDraw(Canvas canvas) {
            int w = getWidth();
            float x0 = dp(this, 4), x1 = w - dp(this, 4);
            float lineY = dp(this, 14);
            paint.setColor(GRID);
            paint.setStrokeWidth(dp(this, 1));
            canvas.drawLine(x0, lineY, x1, lineY, paint);
            textPaint.setTextAlign(Paint.Align.LEFT);
            canvas.drawText((count) + " s ago", x0, dp(this, 36), textPaint);
            textPaint.setTextAlign(Paint.Align.CENTER);
            canvas.drawText((count / 2) + " s", (x0 + x1) / 2, dp(this, 36), textPaint);
            textPaint.setTextAlign(Paint.Align.RIGHT);
            canvas.drawText("now", x1, dp(this, 36), textPaint);
            float step = (x1 - x0) / Math.max(1, count - 1);
            for (int i = 0; i < marks.size(); i++) {
                EventMark m = marks.get(i);
                float x = x0 + step * m.index;
                paint.setColor(m.color);
                canvas.drawCircle(x, lineY, dp(this, i == hover ? 6 : 4.5f), paint);
                paint.setColor(Color.WHITE);
                canvas.drawCircle(x, lineY, dp(this, i == hover ? 3 : 2), paint);
            }
            if (hover >= 0 && hover < marks.size()) {
                EventMark m = marks.get(hover);
                String line = m.clock + " · " + m.text;
                float maxW = (x1 - x0) - dp(this, 8);
                if (tipText.measureText(line) > maxW) {
                    while (line.length() > 4 && tipText.measureText(line + "…") > maxW) line = line.substring(0, line.length() - 1);
                    line += "…";
                }
                float padX = dp(this, 8), bh = tipText.getTextSize() + dp(this, 10);
                float bw = tipText.measureText(line) + padX * 2;
                float x = x0 + step * m.index;
                float bx = x + dp(this, 8);
                if (bx + bw > x1) bx = x1 - bw;
                if (bx < x0) bx = x0;
                rect.set(bx, dp(this, 20), bx + bw, dp(this, 20) + bh);
                canvas.drawRoundRect(rect, dp(this, 6), dp(this, 6), tipPaint);
                tipText.setTextAlign(Paint.Align.LEFT);
                canvas.drawText(line, bx + padX, dp(this, 20) + bh - dp(this, 7), tipText);
            }
        }

        private float downX, downY;
        private boolean claimed;

        /** Same rule as the chart: a sideways move claims the gesture, a vertical one scrolls the page. */
        @Override
        public boolean onTouchEvent(MotionEvent e) {
            switch (e.getActionMasked()) {
                case MotionEvent.ACTION_DOWN:
                    downX = e.getX();
                    downY = e.getY();
                    claimed = false;
                    setHover(e.getX());
                    return true;
                case MotionEvent.ACTION_MOVE: {
                    if (!claimed) {
                        float dx = Math.abs(e.getX() - downX), dy = Math.abs(e.getY() - downY);
                        float slop = dp(this, 8);
                        if (dx > slop && dx > dy) {
                            claimed = true;
                            getParent().requestDisallowInterceptTouchEvent(true);
                        }
                    }
                    setHover(e.getX());
                    return true;
                }
                case MotionEvent.ACTION_UP:
                    getParent().requestDisallowInterceptTouchEvent(false);
                    postDelayed(clearHover, 4000);
                    return true;
                case MotionEvent.ACTION_CANCEL:
                    getParent().requestDisallowInterceptTouchEvent(false);
                    hover = -1;
                    invalidate();
                    return true;
                default:
                    return super.onTouchEvent(e);
            }
        }

        private void setHover(float x) {
            float x0 = dp(this, 4), x1 = getWidth() - dp(this, 4);
            float step = (x1 - x0) / Math.max(1, count - 1);
            int best = -1;
            float bestD = dp(this, 14);
            for (int i = 0; i < marks.size(); i++) {
                float d = Math.abs(x - (x0 + step * marks.get(i).index));
                if (d < bestD) {
                    bestD = d;
                    best = i;
                }
            }
            hover = best;
            removeCallbacks(clearHover);
            invalidate();
        }
    }

    // ------------------------------------------------------------------ value tile

    /**
     * A tile drawn on a Canvas rather than built from TextViews, on purpose: its value changes every second, and
     * a TextView's setText fires an accessibility content-changed event and a layout pass each time. Drawing it
     * keeps the card cheap on an old phone and lets screen readers and UI automation see a quiet screen.
     */
    public static final class TileView extends View {
        private String label = "", value = "", sub = "";
        private int quality = 2;
        private final Paint bg = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Paint stroke = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Paint labelPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Paint valuePaint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Paint subPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Paint chipPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Paint chipText = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final RectF rect = new RectF();

        public TileView(Context c) {
            super(c);
            bg.setColor(Color.WHITE);
            stroke.setColor(GRID);
            stroke.setStyle(Paint.Style.STROKE);
            stroke.setStrokeWidth(dp(this, 1));
            labelPaint.setColor(MUTED);
            labelPaint.setTextSize(sp(this, 10.5f));
            labelPaint.setLetterSpacing(0.04f);
            valuePaint.setColor(TEXT);
            valuePaint.setTextSize(sp(this, 17));
            valuePaint.setTypeface(Typeface.DEFAULT_BOLD);
            subPaint.setColor(MUTED);
            subPaint.setTextSize(sp(this, 10.5f));
            chipText.setTextSize(sp(this, 9));
            chipText.setTypeface(Typeface.DEFAULT_BOLD);
        }

        /** quality: 0 live, 1 estimated, 2 not available. Redraws only when something changed. */
        public void set(String labelText, String valueText, String subText, int q) {
            String l = labelText == null ? "" : labelText.toUpperCase(java.util.Locale.US);
            String v = valueText == null ? "" : valueText;
            String s = subText == null ? "" : subText;
            if (l.equals(label) && v.equals(value) && s.equals(sub) && q == quality) return;
            label = l;
            value = v;
            sub = s;
            quality = q;
            invalidate();
        }

        @Override
        protected void onMeasure(int widthSpec, int heightSpec) {
            setMeasuredDimension(MeasureSpec.getSize(widthSpec), dp(this, 68));
        }

        @Override
        protected void onDraw(Canvas canvas) {
            float w = getWidth(), h = getHeight();
            float r = dp(this, 10);
            rect.set(dp(this, 0.5f), dp(this, 0.5f), w - dp(this, 0.5f), h - dp(this, 0.5f));
            canvas.drawRoundRect(rect, r, r, bg);
            canvas.drawRoundRect(rect, r, r, stroke);

            float padX = dp(this, 9);
            String chip = quality == 0 ? "LIVE" : quality == 1 ? "EST" : "N/A";
            chipPaint.setColor(quality == 0 ? 0xFFDCF3DC : quality == 1 ? 0xFFFDEBC9 : 0xFFE5E7EB);
            chipText.setColor(quality == 0 ? 0xFF0A5A0A : quality == 1 ? 0xFF7A4A00 : 0xFF4B5563);
            float cw = chipText.measureText(chip) + dp(this, 8), ch = chipText.getTextSize() + dp(this, 4);
            rect.set(w - dp(this, 7) - cw, dp(this, 7), w - dp(this, 7), dp(this, 7) + ch);
            canvas.drawRoundRect(rect, dp(this, 3), dp(this, 3), chipPaint);
            canvas.drawText(chip, rect.left + dp(this, 4), rect.bottom - dp(this, 3.5f), chipText);

            float labelY = dp(this, 8) + labelPaint.getTextSize();
            canvas.drawText(ellipsize(label, labelPaint, w - padX * 2 - cw - dp(this, 6)), padX, labelY, labelPaint);
            float valueY = labelY + dp(this, 4) + valuePaint.getTextSize();
            canvas.drawText(ellipsize(value, valuePaint, w - padX * 2), padX, valueY, valuePaint);
            float subY = valueY + dp(this, 5) + subPaint.getTextSize();
            canvas.drawText(ellipsize(sub, subPaint, w - padX * 2), padX, subY, subPaint);
        }
    }

    /** One line of text drawn on a Canvas, for values that change every second (see TileView). */
    public static final class LineView extends View {
        private String text = "";
        private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private int gravity = Gravity.START;

        public LineView(Context c, float textSp, int color, boolean bold) {
            super(c);
            paint.setTextSize(sp(this, textSp));
            paint.setColor(color);
            if (bold) paint.setTypeface(Typeface.DEFAULT_BOLD);
        }

        public LineView gravity(int g) {
            gravity = g;
            return this;
        }

        public void set(String t) {
            if (t == null) t = "";
            if (t.equals(text)) return;
            text = t;
            invalidate();
        }

        public String get() {
            return text;
        }

        @Override
        protected void onMeasure(int widthSpec, int heightSpec) {
            int w = MeasureSpec.getMode(widthSpec) == MeasureSpec.UNSPECIFIED
                    ? Math.round(paint.measureText(text)) + getPaddingLeft() + getPaddingRight()
                    : MeasureSpec.getSize(widthSpec);
            setMeasuredDimension(w, Math.round(paint.getTextSize() * 1.4f) + getPaddingTop() + getPaddingBottom());
        }

        @Override
        protected void onDraw(Canvas canvas) {
            float avail = getWidth() - getPaddingLeft() - getPaddingRight();
            String t = ellipsize(text, paint, avail);
            float x = getPaddingLeft();
            if (gravity == Gravity.END) x = getWidth() - getPaddingRight() - paint.measureText(t);
            else if (gravity == Gravity.CENTER) x = getPaddingLeft() + (avail - paint.measureText(t)) / 2;
            canvas.drawText(t, x, getPaddingTop() + paint.getTextSize() * 1.05f, paint);
        }
    }

    static String ellipsize(String s, Paint p, float avail) {
        if (s == null) return "";
        if (avail <= 0 || p.measureText(s) <= avail) return s;
        return android.text.TextUtils.ellipsize(s, new android.text.TextPaint(p), avail, android.text.TextUtils.TruncateAt.END).toString();
    }

    // ------------------------------------------------------------------ flip card

    public static final class FlipView extends FrameLayout {
        private final View front;
        private final View back;
        private boolean flipped;
        private boolean animating;

        public FlipView(Context c, View front, View back) {
            super(c);
            this.front = front;
            this.back = back;
            addView(front, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
            addView(back, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
            back.setVisibility(GONE);
            setCameraDistance(getResources().getDisplayMetrics().density * 6000);
        }

        public boolean isFlipped() {
            return flipped;
        }

        public void flip() {
            if (animating) return;
            animating = true;
            final View from = flipped ? back : front;
            final View to = flipped ? front : back;
            animate().rotationY(90).setDuration(160).setListener(new AnimatorListenerAdapter() {
                @Override
                public void onAnimationEnd(Animator a) {
                    from.setVisibility(GONE);
                    to.setVisibility(VISIBLE);
                    setRotationY(-90);
                    animate().rotationY(0).setDuration(160).setListener(new AnimatorListenerAdapter() {
                        @Override
                        public void onAnimationEnd(Animator b) {
                            animating = false;
                            flipped = !flipped;
                        }
                    }).start();
                }
            }).start();
        }
    }
}
