package com.usblanbridge.ui;

import android.content.ClipData;
import android.content.Context;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.view.DragEvent;
import android.view.Gravity;
import android.view.MotionEvent;
import android.view.View;
import android.view.ViewGroup;
import android.view.ViewParent;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import com.usblanbridge.ui.DeviceViews.LineView;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;

/**
 * Accordion sections: every card on the screen, and every block inside a card, has a header that collapses it
 * with a tap and a grip that drags it to another position. What is collapsed and in which order the sections
 * sit is remembered, so the screen shows what its user wants to see.
 */
public final class Sections {

    /** Where collapsed flags and orders are kept. */
    public interface Store {
        boolean isCollapsed(String key, boolean def);

        void setCollapsed(String key, boolean collapsed);

        String getOrder(String group);

        void setOrder(String group, String csv);
    }

    private static final int TEXT = 0xFF111827;
    private static final int MUTED = 0xFF6B7280;
    private static final int LINE = 0xFFE5E7EB;

    private Sections() {
    }

    static int dp(View v, float value) {
        return Math.round(v.getResources().getDisplayMetrics().density * value);
    }

    /**
     * A titled block: a header row with a grip, a chevron, the title, an optional one-line summary shown while
     * collapsed, and any extra controls on the right; the content below it. Card style draws the white rounded
     * panel; subsection style is a plain block with a thin line under its header.
     */
    public static final class SectionView extends LinearLayout {
        public final String key;
        private final Store store;
        private final boolean card;
        private final LinearLayout header;
        private final TextView grip;
        private final TextView chevron;
        private final TextView title;
        private final LineView summary;
        private final LinearLayout extras;
        private final FrameLayout content;
        private boolean collapsed;
        private final List<Runnable> onToggle = new ArrayList<>();

        public SectionView(Context c, String key, String titleText, boolean card, int background, Store store) {
            super(c);
            this.key = key;
            this.store = store;
            this.card = card;
            setOrientation(VERTICAL);
            if (card) {
                GradientDrawable bg = new GradientDrawable();
                bg.setColor(background);
                bg.setCornerRadius(dp(this, 14));
                setBackground(bg);
                setElevation(dp(this, 1));
                setPadding(dp(this, 10), dp(this, 6), dp(this, 16), dp(this, 14));
                LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
                lp.bottomMargin = dp(this, 12);
                setLayoutParams(lp);
            } else {
                setPadding(0, dp(this, 4), 0, 0);
                setLayoutParams(new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
            }

            header = new LinearLayout(c);
            header.setOrientation(HORIZONTAL);
            header.setGravity(Gravity.CENTER_VERTICAL);
            header.setClickable(true);
            header.setContentDescription("section_" + key);
            header.setMinimumHeight(dp(this, card ? 40 : 32));
            header.setOnClickListener(new OnClickListener() {
                @Override
                public void onClick(View v) {
                    toggle();
                }
            });

            grip = new TextView(c);
            grip.setText("≡");
            grip.setTextSize(card ? 18 : 15);
            grip.setTextColor(0xFF9CA3AF);
            grip.setGravity(Gravity.CENTER);
            grip.setContentDescription("grip_" + key);
            grip.setPadding(dp(this, 4), 0, dp(this, 4), 0);
            LinearLayout.LayoutParams glp = new LinearLayout.LayoutParams(dp(this, 26), dp(this, 36));
            header.addView(grip, glp);
            grip.setOnTouchListener(new OnTouchListener() {
                @Override
                public boolean onTouch(View v, MotionEvent e) {
                    if (e.getActionMasked() == MotionEvent.ACTION_DOWN) {
                        Column column = column();
                        if (column != null) {
                            column.startDragging(SectionView.this);
                            return true;
                        }
                    }
                    return false;
                }
            });

            chevron = new TextView(c);
            chevron.setText("▾");
            chevron.setTextSize(card ? 16 : 13);
            chevron.setTextColor(MUTED);
            chevron.setGravity(Gravity.CENTER);
            chevron.setContentDescription("chev_" + key);
            header.addView(chevron, new LinearLayout.LayoutParams(dp(this, 20), ViewGroup.LayoutParams.WRAP_CONTENT));

            title = new TextView(c);
            title.setText(titleText);
            title.setTextSize(card ? 15 : 12.5f);
            title.setTextColor(TEXT);
            title.setTypeface(Typeface.DEFAULT_BOLD);
            title.setPadding(dp(this, 2), 0, dp(this, 6), 0);
            header.addView(title, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT));

            // The summary takes whatever width the title and the controls leave. While the section is open it
            // stays in place, invisible, so the controls keep to the right edge.
            summary = new LineView(c, card ? 12 : 11, MUTED, false);
            summary.setVisibility(INVISIBLE);
            summary.setContentDescription("summary_" + key);
            header.addView(summary, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));

            extras = new LinearLayout(c);
            extras.setOrientation(HORIZONTAL);
            extras.setGravity(Gravity.CENTER_VERTICAL | Gravity.END);
            LinearLayout.LayoutParams elp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
            header.addView(extras, elp);
            addView(header, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));

            if (!card) {
                View line = new View(c);
                line.setBackgroundColor(LINE);
                addView(line, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(this, 1)));
            }

            content = new FrameLayout(c);
            content.setPadding(card ? dp(this, 6) : 0, 0, 0, 0);
            addView(content, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));

            setCollapsed(store != null && store.isCollapsed(key, false), false);
        }

        public SectionView content(View v) {
            content.removeAllViews();
            content.addView(v, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
            return this;
        }

        /** A control that lives in the header, to the right of the title, and stays usable while collapsed. */
        public SectionView extra(View v) {
            LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
            lp.leftMargin = dp(this, 8);
            if (v.getLayoutParams() instanceof LinearLayout.LayoutParams) {
                LinearLayout.LayoutParams old = (LinearLayout.LayoutParams) v.getLayoutParams();
                lp.width = old.width == ViewGroup.LayoutParams.MATCH_PARENT || old.width == 0 ? ViewGroup.LayoutParams.WRAP_CONTENT : old.width;
                lp.height = old.height;
            }
            extras.addView(v, lp);
            return this;
        }

        /** Extras that make up the whole header (a NO CUT switch, say), taking the space the title leaves. */
        public SectionView extraFill(View v) {
            LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
            lp.leftMargin = dp(this, 8);
            extras.addView(v, lp);
            return this;
        }

        public SectionView titleColor(int color) {
            title.setTextColor(color);
            return this;
        }

        /** One line shown in the header while the section is collapsed, so the gist is visible without opening it. */
        public void setSummary(String text) {
            summary.set(text);
        }

        public void onToggle(Runnable r) {
            onToggle.add(r);
        }

        public boolean isCollapsed() {
            return collapsed;
        }

        public void toggle() {
            setCollapsed(!collapsed, true);
        }

        public void setCollapsed(boolean value, boolean animate) {
            collapsed = value;
            content.setVisibility(collapsed ? GONE : VISIBLE);
            summary.setVisibility(collapsed ? VISIBLE : INVISIBLE);
            if (animate) chevron.animate().rotation(collapsed ? -90 : 0).setDuration(150).start();
            else chevron.setRotation(collapsed ? -90 : 0);
            if (collapsed && card) setPadding(dp(this, 10), dp(this, 6), dp(this, 16), dp(this, 6));
            else if (card) setPadding(dp(this, 10), dp(this, 6), dp(this, 16), dp(this, 14));
            if (store != null) store.setCollapsed(key, collapsed);
            for (Runnable r : onToggle) r.run();
        }

        private Column column() {
            ViewParent p = getParent();
            return p instanceof Column ? (Column) p : null;
        }
    }

    /**
     * A vertical column of sections that can be dragged by their grips into a new order. The dragged section
     * moves live as the finger passes the middle of its neighbours, the page scrolls when the finger nears the
     * top or bottom of the screen, and the order is saved on drop.
     */
    public static final class Column extends LinearLayout {
        private final String group;
        private final Store store;
        private SectionView dragging;
        private final Handler handler = new Handler(Looper.getMainLooper());
        private int edgeScroll;
        private final Runnable autoScroll = new Runnable() {
            @Override
            public void run() {
                if (dragging == null || edgeScroll == 0) return;
                ScrollView sv = scrollView();
                if (sv != null) sv.scrollBy(0, edgeScroll);
                handler.postDelayed(this, 16);
            }
        };

        public Column(Context c, String group, Store store) {
            super(c);
            this.group = group;
            this.store = store;
            setOrientation(VERTICAL);
            setContentDescription("column_" + group);
            setOnDragListener(new OnDragListener() {
                @Override
                public boolean onDrag(View v, DragEvent e) {
                    return handleDrag(e);
                }
            });
        }

        public void add(SectionView s) {
            addView(s);
        }

        /** Puts the sections into the saved order; unknown keys keep their place at the end. */
        public void applySavedOrder() {
            String csv = store == null ? null : store.getOrder(group);
            if (csv == null || csv.isEmpty()) return;
            List<String> wanted = Arrays.asList(csv.split(","));
            List<SectionView> all = sections();
            List<SectionView> ordered = new ArrayList<>();
            for (String k : wanted) for (SectionView s : all) if (s.key.equals(k) && !ordered.contains(s)) ordered.add(s);
            for (SectionView s : all) if (!ordered.contains(s)) ordered.add(s);
            for (SectionView s : all) removeView(s);
            for (SectionView s : ordered) addView(s);
        }

        public List<SectionView> sections() {
            List<SectionView> out = new ArrayList<>();
            for (int i = 0; i < getChildCount(); i++) if (getChildAt(i) instanceof SectionView) out.add((SectionView) getChildAt(i));
            return out;
        }

        private void saveOrder() {
            if (store == null) return;
            StringBuilder sb = new StringBuilder();
            for (SectionView s : sections()) {
                if (sb.length() > 0) sb.append(',');
                sb.append(s.key);
            }
            store.setOrder(group, sb.toString());
        }

        void startDragging(SectionView s) {
            dragging = s;
            View.DragShadowBuilder shadow = new View.DragShadowBuilder(s.header);
            ClipData data = ClipData.newPlainText("section", s.key);
            boolean started;
            if (Build.VERSION.SDK_INT >= 24) started = s.startDragAndDrop(data, shadow, s, 0);
            else started = startDragCompat(s, data, shadow);
            if (started) s.setAlpha(0.35f);
            else dragging = null;
        }

        @SuppressWarnings("deprecation")
        private boolean startDragCompat(View v, ClipData data, View.DragShadowBuilder shadow) {
            return v.startDrag(data, shadow, v, 0);
        }

        private boolean handleDrag(DragEvent e) {
            switch (e.getAction()) {
                case DragEvent.ACTION_DRAG_STARTED:
                    return dragging != null && e.getLocalState() == dragging;
                case DragEvent.ACTION_DRAG_LOCATION: {
                    if (dragging == null) return true;
                    float y = e.getY();
                    int from = indexOfChild(dragging);
                    int to = from;
                    for (int i = 0; i < getChildCount(); i++) {
                        View child = getChildAt(i);
                        if (child == dragging) continue;
                        float mid = (child.getTop() + child.getBottom()) / 2f;
                        if (i < from && y < mid) {
                            to = i;
                            break;
                        }
                        if (i > from && y > mid) to = i;
                    }
                    if (to != from) {
                        removeView(dragging);
                        addView(dragging, to);
                    }
                    ScrollView sv = scrollView();
                    if (sv != null) {
                        int[] mine = new int[2], theirs = new int[2];
                        getLocationOnScreen(mine);
                        sv.getLocationOnScreen(theirs);
                        float inScroll = y + mine[1] - theirs[1];
                        int zone = dp(this, 90);
                        int step = dp(this, 14);
                        int was = edgeScroll;
                        edgeScroll = inScroll < zone ? -step : inScroll > sv.getHeight() - zone ? step : 0;
                        if (edgeScroll != 0 && was == 0) handler.post(autoScroll);
                    }
                    return true;
                }
                case DragEvent.ACTION_DRAG_EXITED:
                    edgeScroll = 0;
                    return true;
                case DragEvent.ACTION_DROP:
                    return true;
                case DragEvent.ACTION_DRAG_ENDED:
                    edgeScroll = 0;
                    if (dragging != null) {
                        dragging.setAlpha(1f);
                        dragging = null;
                        saveOrder();
                    }
                    return true;
                default:
                    return true;
            }
        }

        private ScrollView scrollView() {
            ViewParent p = getParent();
            while (p != null && !(p instanceof ScrollView)) p = p.getParent();
            return (ScrollView) p;
        }
    }

    /** A small chip such as LIVE or EST for a header. */
    public static TextView chip(Context c, String text, int bg, int fg) {
        TextView t = new TextView(c);
        t.setText(text);
        t.setTextSize(9);
        t.setTextColor(fg);
        t.setTypeface(Typeface.DEFAULT_BOLD);
        int p = Math.round(c.getResources().getDisplayMetrics().density * 4);
        t.setPadding(p, p / 4, p, p / 4);
        GradientDrawable d = new GradientDrawable();
        d.setColor(bg);
        d.setCornerRadius(p * 0.75f);
        t.setBackground(d);
        return t;
    }

    /** A legend entry (swatch + name) for a chart header. */
    public static View legend(Context c, String[] names, int[] colors) {
        LinearLayout r = new LinearLayout(c);
        r.setOrientation(LinearLayout.HORIZONTAL);
        r.setGravity(Gravity.CENTER_VERTICAL);
        float d = c.getResources().getDisplayMetrics().density;
        for (int i = 0; i < names.length; i++) {
            View swatch = new View(c);
            swatch.setBackgroundColor(colors[i]);
            LinearLayout.LayoutParams slp = new LinearLayout.LayoutParams(Math.round(12 * d), Math.round(2 * d));
            slp.leftMargin = Math.round(8 * d);
            slp.rightMargin = Math.round(4 * d);
            r.addView(swatch, slp);
            TextView t = new TextView(c);
            t.setText(names[i]);
            t.setTextSize(10.5f);
            t.setTextColor(MUTED);
            r.addView(t);
        }
        return r;
    }
}
