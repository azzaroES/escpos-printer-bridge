package com.usblanbridge;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.DialogInterface;
import android.content.Intent;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.provider.Settings;
import android.text.InputType;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.CompoundButton;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.Switch;
import android.widget.TextView;
import android.widget.Toast;

import com.usblanbridge.core.EventLog;
import com.usblanbridge.core.Log;
import com.usblanbridge.ui.DeviceViews;
import com.usblanbridge.ui.DeviceViews.ChartView;
import com.usblanbridge.ui.DeviceViews.EventMark;
import com.usblanbridge.ui.DeviceViews.EventStripView;
import com.usblanbridge.ui.DeviceViews.FlipView;
import com.usblanbridge.ui.DeviceViews.LineView;
import com.usblanbridge.ui.DeviceViews.Series;
import com.usblanbridge.ui.DeviceViews.StripView;
import com.usblanbridge.ui.DeviceViews.TileView;
import com.usblanbridge.ui.Sections;
import com.usblanbridge.ui.Sections.SectionView;

import java.text.SimpleDateFormat;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Date;
import java.util.List;
import java.util.Locale;

/**
 * The Device, Battery and Cool-down cards of the main screen, fed by the DeviceMonitor once a second.
 * Kept out of MainActivity so that file stays about printing.
 */
final class DeviceCards {

    private static final int TEXT = 0xFF111827;
    private static final int MUTED = 0xFF6B7280;
    private static final int CARD = 0xFFFFFFFF;
    private static final int CHIP = 0xFFE5E7EB;
    private static final int PRIMARY = 0xFF1D4ED8;
    private static final int RED = 0xFFB91C1C;
    private static final int PANEL = 0xFFF9FAFB;

    private final Activity a;
    private final Prefs prefs;
    private final Sections.Store store;
    private final SimpleDateFormat clock = new SimpleDateFormat("HH:mm:ss", Locale.US);

    // sections, for their collapsed summaries
    private SectionView deviceSection, batterySection, coolSection;

    // device card
    private final TileView[] tiles = new TileView[7];
    private ChartView loadChart, tempChart;
    private Series cpuSeries, ramSeries, wifiSeries, tBatSeries, tCpuSeries;
    private StripView strips;
    private EventStripView eventStrip;
    private LinearLayout eventList;
    private TextView eventsEmpty;
    private TextView telemetryText;
    private Switch telemetrySwitch;

    // battery card
    private FlipView flip;
    private Button flipButton;
    private TextView batLevel;
    private LineView batState;
    private ChartView levelChart, maChart;
    private Series levelSeries, maSeries;
    private final LineView[] kv = new LineView[12];
    private static final String[] KV_LABELS = {"State", "Plugged", "Current now", "Average current", "Voltage", "Power",
            "Temperature", "Charge counter", "Health", "Technology", "Time to full", "Cycle count"};

    // cool-down card
    private EditText minutesField;
    private Button startButton, stopButton;
    private LineView coolLeft;
    private TextView coolStatus, thermalText;
    private LinearLayout startRow, stopRow;

    private DeviceMonitor.Snapshot lastSnapshot;
    private float[] tBatMinCache;

    DeviceCards(Activity a, Prefs prefs, Sections.Store store) {
        this.a = a;
        this.prefs = prefs;
        this.store = store;
    }

    private SectionView section(String key, String title, boolean card) {
        return new SectionView(a, key, title, card, CARD, store);
    }

    // ------------------------------------------------------------------ device card

    /** The Device card: a column of collapsible, draggable subsections (tiles, graphs, strips, events, telemetry). */
    View deviceCard() {
        deviceSection = section("device", "Device", true);
        deviceSection.setContentDescription("card_device");
        // The refresh note gives way to the summary while the card is folded.
        final TextView every = text("every second while open", 11, MUTED, false);
        deviceSection.extra(every);
        every.setVisibility(deviceSection.isCollapsed() ? View.GONE : View.VISIBLE);
        deviceSection.onToggle(new Runnable() {
            @Override
            public void run() {
                every.setVisibility(deviceSection.isCollapsed() ? View.GONE : View.VISIBLE);
            }
        });
        LinearLayout body = new LinearLayout(a);
        body.setOrientation(LinearLayout.VERTICAL);
        Sections.Column column = new Sections.Column(a, "device", store);

        LinearLayout tilesBox = new LinearLayout(a);
        tilesBox.setOrientation(LinearLayout.VERTICAL);
        String[] labels = {"USB", "Wi-Fi", "Bluetooth", "Processor", "RAM", "Video (GPU)", "Temperatures"};
        for (int i = 0; i < tiles.length; i += 2) {
            LinearLayout r = row();
            ((LinearLayout.LayoutParams) r.getLayoutParams()).topMargin = dp(8);
            for (int j = i; j < i + 2; j++) {
                View v;
                if (j < tiles.length) {
                    tiles[j] = new TileView(a);
                    tiles[j].set(labels[j], "—", "", 2);
                    tiles[j].setContentDescription("tile_" + labels[j].toLowerCase(Locale.US).replaceAll("[^a-z]", ""));
                    v = tiles[j];
                } else {
                    v = new View(a);
                }
                LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MATCH_PARENT, 1f);
                if (j == i) lp.rightMargin = dp(4);
                else lp.leftMargin = dp(4);
                r.addView(v, lp);
            }
            tilesBox.addView(r);
        }
        column.add(section("d.tiles", "Tiles", false).content(tilesBox));

        SectionView loadSection = section("d.load", "Load, last 60 s (%)", false)
                .extra(Sections.legend(a, new String[]{"CPU clock", "RAM", "Wi-Fi signal"}, new int[]{DeviceViews.SERIES_1, DeviceViews.SERIES_2, DeviceViews.SERIES_3}));
        loadChart = new ChartView(a).range(0, 100, "100", "50", "0").tooltip(new DeviceViews.Tooltip() {
            @Override
            public String[] lines(int i) {
                DeviceMonitor.Snapshot s = lastSnapshot;
                if (s == null) return null;
                return new String[]{ago(i, s.cpu.length, "s"),
                        "CPU " + val(s.cpu[i], "%"), "RAM " + val(s.ram[i], "%"), "Wi-Fi " + val(s.wifi[i], "%")};
            }
        });
        cpuSeries = loadChart.add(new Series("CPU clock", DeviceViews.SERIES_1));
        ramSeries = loadChart.add(new Series("RAM", DeviceViews.SERIES_2));
        wifiSeries = loadChart.add(new Series("Wi-Fi signal", DeviceViews.SERIES_3));
        column.add(loadSection.content(wrap(loadChart, 120)));

        SectionView tempSection = section("d.temp", "Temperature, last 60 s (°C)", false)
                .extra(Sections.legend(a, new String[]{"Battery", "CPU zone"}, new int[]{DeviceViews.SERIES_1, DeviceViews.SERIES_2}));
        tempChart = new ChartView(a).range(0, 80, "80", "40", "0").threshold(50f, "throttle 50°").tooltip(new DeviceViews.Tooltip() {
            @Override
            public String[] lines(int i) {
                DeviceMonitor.Snapshot s = lastSnapshot;
                if (s == null) return null;
                return new String[]{ago(i, s.tBat.length, "s"), "Battery " + val(s.tBat[i], " °C"), "CPU " + val(s.tCpu[i], " °C")};
            }
        }).empty("No temperature reading yet");
        tBatSeries = tempChart.add(new Series("Battery", DeviceViews.SERIES_1));
        tCpuSeries = tempChart.add(new Series("CPU zone", DeviceViews.SERIES_2));
        column.add(tempSection.content(wrap(tempChart, 110)));

        strips = new StripView(a);
        strips.set(Arrays.asList("USB printer", "Bluetooth"), Arrays.asList(new Boolean[60], new Boolean[60]));
        LinearLayout stripsBox = new LinearLayout(a);
        stripsBox.setOrientation(LinearLayout.VERTICAL);
        stripsBox.setPadding(0, dp(10), 0, dp(4));
        stripsBox.addView(strips, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        column.add(section("d.strips", "USB and Bluetooth printers, last 60 s", false).content(stripsBox));

        LinearLayout eventsBox = new LinearLayout(a);
        eventsBox.setOrientation(LinearLayout.VERTICAL);
        eventStrip = new EventStripView(a);
        eventsBox.addView(eventStrip);
        eventsEmpty = hint("No printer events yet. Every ticket printed, cut removed, printer not answering or job failed is marked here, on the graphs above and in the telemetry log.");
        eventsBox.addView(eventsEmpty);
        eventList = new LinearLayout(a);
        eventList.setOrientation(LinearLayout.VERTICAL);
        eventsBox.addView(eventList);
        column.add(section("d.events", "Printer events, same timeline", false)
                .extra(Sections.legend(a, new String[]{"printed", "cut removed", "offline", "failed"}, new int[]{DeviceViews.GOOD, DeviceViews.WARNING, DeviceViews.SERIOUS, DeviceViews.CRITICAL}))
                .content(eventsBox));

        LinearLayout tele = panel();
        telemetryText = text("", 11.5f, MUTED, false);
        telemetryText.setPadding(0, dp(4), 0, 0);
        tele.addView(telemetryText);
        telemetrySwitch = new Switch(a);
        telemetrySwitch.setChecked(prefs.isTelemetryLogged());
        telemetrySwitch.setOnCheckedChangeListener(new CompoundButton.OnCheckedChangeListener() {
            @Override
            public void onCheckedChanged(CompoundButton b, boolean on) {
                prefs.setTelemetryLogged(on);
                Log.i(on ? "Telemetry log switched on." : "Telemetry log switched off.");
            }
        });
        tele.addView(switchRow("Write the telemetry log", telemetrySwitch));
        Button share = button("Share log file", new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                shareTelemetry();
            }
        });
        share.setContentDescription("btn_share_telemetry");
        Button clear = button("Clear telemetry logs…", new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                confirmClear();
            }
        });
        clear.setContentDescription("btn_clear_telemetry");
        tele.addView(buttonRow(share, clear));
        column.add(section("d.telemetry", "Telemetry log", false).content(tele));

        column.applySavedOrder();
        body.addView(column, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        body.addView(hint("LIVE = read from Android. EST = the closest reading an app is allowed: Android hides system CPU load since 8.0, "
                + "so the tile shows the cores' clock as a share of their maximum. Temperatures: the battery sensor always, the CPU thermal "
                + "zone where the phone exposes it, plus Android's own thermal status. Tap a heading to fold it, drag ≡ to reorder."));
        deviceSection.content(body);
        return deviceSection;
    }

    private View wrap(View chart, int heightDp) {
        LinearLayout box = new LinearLayout(a);
        box.setOrientation(LinearLayout.VERTICAL);
        box.addView(chart, chartParams(heightDp));
        return box;
    }

    private void shareTelemetry() {
        DeviceMonitor.Snapshot s = lastSnapshot;
        java.io.File f = s == null ? null : s.telemetryToday;
        if (f == null || !f.exists()) {
            toast("No telemetry file yet today.");
            return;
        }
        try {
            Intent send = new Intent(Intent.ACTION_SEND);
            send.setType("text/csv");
            send.putExtra(Intent.EXTRA_SUBJECT, "Printer bridge telemetry " + f.getName());
            send.putExtra(Intent.EXTRA_STREAM, FilesProvider.uriFor(f));
            send.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
            a.startActivity(Intent.createChooser(send, "Share telemetry log"));
        } catch (Exception e) {
            toast("Could not share: " + e.getMessage());
        }
    }

    private void confirmClear() {
        final DeviceMonitor m = DeviceMonitor.get(a);
        int n = m.telemetry().files().size();
        new AlertDialog.Builder(a)
                .setTitle("Delete all telemetry files?")
                .setMessage(n + " file" + (n == 1 ? "" : "s") + " in " + m.telemetry().fileFor(new Date()).getParent() + ". The event list and the printer log are kept.")
                .setPositiveButton("Delete", new DialogInterface.OnClickListener() {
                    @Override
                    public void onClick(DialogInterface d, int which) {
                        int deleted = m.clearTelemetry();
                        toast("Cleared " + deleted + " file" + (deleted == 1 ? "" : "s") + ".");
                    }
                })
                .setNegativeButton("Keep", null)
                .show();
    }

    // ------------------------------------------------------------------ battery card

    View batteryCard() {
        batterySection = section("battery", "Battery", true);
        batterySection.setContentDescription("card_battery");
        batterySection.extra(Sections.chip(a, "LIVE", 0xFFDCF3DC, 0xFF0A5A0A));
        flipButton = smallButton("↻ Details", new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                flip.flip();
                flipButton.setText(flip.isFlipped() ? "↻ Details" : "↻ Graphs");
            }
        });
        flipButton.setContentDescription("btn_flip_battery");
        batterySection.extra(flipButton);

        // front
        LinearLayout front = new LinearLayout(a);
        front.setOrientation(LinearLayout.VERTICAL);
        LinearLayout big = row();
        big.setGravity(Gravity.BOTTOM);
        batLevel = text("—", 34, TEXT, true);
        big.addView(batLevel);
        batState = new LineView(a, 13, MUTED, false);
        batState.setPadding(dp(10), 0, 0, dp(8));
        batState.setContentDescription("battery_state");
        big.addView(batState, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));
        front.addView(big);
        Sections.Column column = new Sections.Column(a, "battery", store);

        SectionView levelSection = section("b.level", "Charge, last 60 min (%)", false)
                .extra(Sections.legend(a, new String[]{"level"}, new int[]{DeviceViews.SERIES_1}));
        levelChart = new ChartView(a).range(0, 100, "100", "50", "0").times("60 min ago", "30 min", "now").tooltip(new DeviceViews.Tooltip() {
            @Override
            public String[] lines(int i) {
                DeviceMonitor.Snapshot s = lastSnapshot;
                if (s == null) return null;
                return new String[]{ago(i, s.levelMin.length, "min"), val(s.levelMin[i], " %"), val(s.maMin[i], " mA"), val(s.tBatMin[i], " °C")};
            }
        }).empty("Fills in as the minutes pass");
        levelSeries = levelChart.add(new Series("level", DeviceViews.SERIES_1));
        levelSeries.fill = true;
        column.add(levelSection.content(wrap(levelChart, 110)));

        SectionView maSection = section("b.current", "Current, last 60 min (mA, + charging / − discharging)", false);
        maChart = new ChartView(a).range(-2000, 2000, "+2 A", "0", "−2 A").times("60 min ago", "30 min", "now").bars(true).tooltip(new DeviceViews.Tooltip() {
            @Override
            public String[] lines(int i) {
                DeviceMonitor.Snapshot s = lastSnapshot;
                if (s == null) return null;
                return new String[]{ago(i, s.maMin.length, "min"), val(s.maMin[i], " mA")};
            }
        }).empty("No current reading on this phone");
        maSeries = maChart.add(new Series("current", DeviceViews.SERIES_2));
        column.add(maSection.content(wrap(maChart, 100)));
        column.applySavedOrder();
        front.addView(column, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));

        // back
        LinearLayout back = new LinearLayout(a);
        back.setOrientation(LinearLayout.VERTICAL);
        back.addView(text("Everything Android reports about this battery", 13, TEXT, true));
        for (int i = 0; i < KV_LABELS.length; i++) {
            LinearLayout r = row();
            r.setPadding(0, dp(3), 0, dp(3));
            TextView k = text(KV_LABELS[i], 12.5f, MUTED, false);
            k.setLayoutParams(new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));
            r.addView(k);
            kv[i] = new LineView(a, 12.5f, TEXT, false).gravity(Gravity.END);
            kv[i].set("—");
            r.addView(kv[i], new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));
            back.addView(r);
        }
        back.addView(hint("Source: BatteryManager (level, plugged, current, charge counter, health) and the battery-changed broadcast "
                + "(voltage, temperature, technology). Design capacity and wear are not exposed to apps without root, so they are not shown."));

        flip = new FlipView(a, front, back);
        LinearLayout body = new LinearLayout(a);
        body.setOrientation(LinearLayout.VERTICAL);
        body.setPadding(0, dp(4), 0, 0);
        body.addView(flip, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        batterySection.content(body);
        return batterySection;
    }

    // ------------------------------------------------------------------ cool-down card

    View coolDownCard() {
        coolSection = section("cooldown", "Cool-down · throttle", true);
        coolSection.setContentDescription("card_cooldown");
        coolSection.extra(Sections.chip(a, "EST", 0xFFFDEBC9, 0xFF7A4A00));
        LinearLayout card = new LinearLayout(a);
        card.setOrientation(LinearLayout.VERTICAL);

        startRow = row();
        startRow.setGravity(Gravity.CENTER_VERTICAL);
        ((LinearLayout.LayoutParams) startRow.getLayoutParams()).topMargin = dp(8);
        startRow.addView(text("Throttle the bridge for", 14, TEXT, false));
        minutesField = new EditText(a);
        minutesField.setText(String.valueOf(prefs.getThrottleMinutes()));
        minutesField.setInputType(InputType.TYPE_CLASS_NUMBER);
        minutesField.setSingleLine(true);
        minutesField.setTextSize(15);
        minutesField.setGravity(Gravity.CENTER);
        minutesField.setContentDescription("field_throttle_minutes");
        LinearLayout.LayoutParams mlp = new LinearLayout.LayoutParams(dp(56), ViewGroup.LayoutParams.WRAP_CONTENT);
        mlp.leftMargin = dp(6);
        startRow.addView(minutesField, mlp);
        TextView min = text("min", 14, TEXT, false);
        min.setPadding(dp(4), 0, dp(8), 0);
        startRow.addView(min);
        startButton = button("Start", new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                int minutes;
                try {
                    minutes = Integer.parseInt(minutesField.getText().toString().trim());
                } catch (Exception e) {
                    minutes = prefs.getThrottleMinutes();
                }
                minutes = Throttle.clampMinutes(minutes);
                minutesField.setText(String.valueOf(minutes));
                Throttle.start(a, minutes);
                toast("Cool-down for " + minutes + " min.");
                refreshCoolDown(null);
            }
        });
        startButton.setTextColor(Color.WHITE);
        startButton.setTypeface(Typeface.DEFAULT_BOLD);
        startButton.setBackground(rounded(PRIMARY, 10));
        startButton.setContentDescription("btn_throttle_start");
        startRow.addView(startButton, new LinearLayout.LayoutParams(0, dp(40), 1f));
        card.addView(startRow);

        stopRow = row();
        ((LinearLayout.LayoutParams) stopRow.getLayoutParams()).topMargin = dp(8);
        stopButton = button("Stop cool-down", new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                Throttle.stop(a);
                toast("Full performance again.");
                refreshCoolDown(null);
            }
        });
        stopButton.setTextColor(Color.WHITE);
        stopButton.setTypeface(Typeface.DEFAULT_BOLD);
        stopButton.setBackground(rounded(RED, 10));
        stopButton.setContentDescription("btn_throttle_stop");
        stopRow.addView(stopButton, new LinearLayout.LayoutParams(0, dp(44), 1f));
        coolLeft = new LineView(a, 15, 0xFF7A4A00, true).gravity(Gravity.END);
        coolLeft.setPadding(dp(12), 0, 0, 0);
        coolLeft.setContentDescription("throttle_left");
        stopRow.addView(coolLeft, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));
        stopRow.setVisibility(View.GONE);
        card.addView(stopRow);

        coolStatus = text("", 13, TEXT, false);
        coolStatus.setPadding(0, dp(8), 0, 0);
        coolStatus.setContentDescription("throttle_status");
        card.addView(coolStatus);

        card.addView(hint("What it does: Android lets no app slow the CPU or the fans. So the bridge throttles itself: it drops the "
                + "high-performance Wi-Fi lock, pauses network discovery, rasterises Print-menu pages at " + Throttle.LOW_DPI
                + " dpi instead of 203, and reads these cards every 30 s in the background. Prints still go through. "
                + "Everything comes back when the timer ends."));

        LinearLayout foot = row();
        foot.setGravity(Gravity.CENTER_VERTICAL);
        ((LinearLayout.LayoutParams) foot.getLayoutParams()).topMargin = dp(8);
        Button saver = button("Android battery saver…", new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                try {
                    a.startActivity(new Intent(Settings.ACTION_BATTERY_SAVER_SETTINGS));
                } catch (Exception e) {
                    try {
                        a.startActivity(new Intent(Settings.ACTION_SETTINGS));
                    } catch (Exception ignored) {
                    }
                }
            }
        });
        foot.addView(saver, new LinearLayout.LayoutParams(0, dp(40), 1f));
        thermalText = text("Thermal status: —", 12, MUTED, false);
        thermalText.setPadding(dp(10), 0, 0, 0);
        foot.addView(thermalText);
        card.addView(foot);
        refreshCoolDown(null);
        coolSection.content(card);
        return coolSection;
    }

    // ------------------------------------------------------------------ refresh

    /** Main thread. */
    void refresh(DeviceMonitor.Snapshot s) {
        lastSnapshot = s;
        DeviceMonitor.Battery b = s.battery;

        // tiles
        tiles[0].set("USB", s.usbText, s.usbSub, s.usbOn == null ? 2 : 0);
        tiles[1].set("Wi-Fi", s.wifiConnected ? s.wifiSignalPct + " %" : "Off",
                s.wifiConnected ? s.wifiLinkMbps + " Mb/s · ↓" + kb(s.rxKBps) + " ↑" + kb(s.txKBps) : "no Wi-Fi connection", 0);
        tiles[2].set("Bluetooth", s.btText, s.btSub, s.btOn == null ? 2 : 0);
        tiles[3].set("Processor", Float.isNaN(s.cpuPct) ? "N/A" : Math.round(s.cpuPct) + " %",
                Float.isNaN(s.cpuPct) ? "clock not readable on this phone" : String.format(Locale.US, "%.2f GHz avg · %d/%d cores", s.cpuGHz, s.cpuRead, s.cpuCores),
                Float.isNaN(s.cpuPct) ? 2 : 1);
        tiles[4].set("RAM", Float.isNaN(s.ramPct) ? "N/A" : Math.round(s.ramPct) + " %",
                Float.isNaN(s.ramPct) ? "" : gb(s.ramUsedMb) + " of " + gb(s.ramTotalMb) + " GB" + (s.lowMemory ? " · low" : ""), Float.isNaN(s.ramPct) ? 2 : 0);
        tiles[5].set("Video (GPU)", "—", "Android gives apps no GPU load", 2);
        String tVal = s.batTempC == null ? (s.cpuTempC == null ? "N/A" : Math.round(s.cpuTempC) + " °C") : Math.round(s.batTempC) + " °C";
        String tSub = (s.batTempC == null ? "" : "battery") + (s.cpuTempC == null ? "" : (s.batTempC == null ? "" : " · ") + "CPU " + Math.round(s.cpuTempC) + " °C")
                + (s.thermalStatus == null ? "" : " · " + s.thermalStatus);
        tiles[6].set("Temperatures", tVal, tSub.isEmpty() ? "no sensor readable" : tSub, s.batTempC == null && s.cpuTempC == null ? 2 : 0);
        deviceSection.setSummary((Float.isNaN(s.cpuPct) ? "" : "CPU " + Math.round(s.cpuPct) + " % · ")
                + (Float.isNaN(s.ramPct) ? "" : "RAM " + Math.round(s.ramPct) + " % · ")
                + (s.wifiConnected ? "Wi-Fi " + s.wifiSignalPct + " %" : "no Wi-Fi")
                + (s.batTempC == null ? "" : " · " + Math.round(s.batTempC) + " °C"));

        // charts
        cpuSeries.values = s.cpu;
        ramSeries.values = s.ram;
        wifiSeries.values = s.wifi;
        List<int[]> marks = new ArrayList<>();
        List<EventMark> strip = new ArrayList<>();
        int n = s.cpu.length;
        for (EventLog.Event e : s.events) {
            long age = (s.time - e.time) / 1000;
            if (age < 0 || age >= n) continue;
            int idx = (int) (n - 1 - age);
            int color = colorOf(e.kind);
            marks.add(new int[]{idx, color});
            strip.add(new EventMark(idx, color, clock.format(new Date(e.time)), e.text));
        }
        loadChart.setMarkers(marks);
        loadChart.refresh();
        tBatSeries.values = s.tBat;
        tCpuSeries.values = s.tCpu;
        tempChart.refresh();
        strips.set(Arrays.asList("USB printer", "Bluetooth"), Arrays.asList(s.usbHist, s.btHist));
        eventStrip.set(strip, n);

        // event list, latest six
        eventsEmpty.setVisibility(s.events.isEmpty() ? View.VISIBLE : View.GONE);
        int want = Math.min(6, s.events.size());
        while (eventList.getChildCount() > want) eventList.removeViewAt(eventList.getChildCount() - 1);
        while (eventList.getChildCount() < want) eventList.addView(eventRow());
        for (int i = 0; i < want; i++) {
            EventLog.Event e = s.events.get(i);
            LinearLayout r = (LinearLayout) eventList.getChildAt(i);
            View dot = r.getChildAt(0);
            GradientDrawable d = (GradientDrawable) dot.getBackground();
            d.setColor(colorOf(e.kind));
            setText((TextView) r.getChildAt(1), clock.format(new Date(e.time)));
            setText((TextView) r.getChildAt(2), e.text);
        }

        setText(telemetryText, "Every tile, both graphs and every event go to " + (s.telemetryToday == null ? "the app's files folder" : s.telemetryToday.getPath())
                + ", one row every 5 s with a full timestamp. Kept 14 days. "
                + (s.telemetryOn ? "Today " + mb(s.telemetryBytesToday) + ", " + s.telemetryFiles + " file" + (s.telemetryFiles == 1 ? "" : "s") + ", " + s.eventsSinceStart + " events since the app started." : "Switched off."));
        if (telemetrySwitch.isChecked() != s.telemetryOn) telemetrySwitch.setChecked(s.telemetryOn);

        // battery: only the face that is showing is updated, so the hidden one raises no accessibility events
        setText(batLevel, b.percent >= 0 ? b.percent + "%" : "—");
        batterySection.setSummary((b.percent >= 0 ? b.percent + " % · " : "") + b.stateText + (b.ma == null ? "" : " · " + amps(b.ma)));
        if (!flip.isFlipped()) {
            batState.set(b.stateText + (b.plugged != 0 ? " · " + b.sourceText : "") + (b.ma == null ? "" : " · " + amps(b.ma)) + (b.tempC == null ? "" : " · " + fmt1(b.tempC) + " °C"));
            levelSeries.values = s.levelMin;
            levelChart.refresh();
            maSeries.values = s.maMin;
            float peak = 500;
            for (float v : s.maMin) if (!Float.isNaN(v)) peak = Math.max(peak, Math.abs(v));
            float top = peak <= 500 ? 500 : peak <= 1000 ? 1000 : peak <= 2000 ? 2000 : peak <= 3000 ? 3000 : 5000;
            maChart.range(-top, top, "+" + ampsLabel(top), "0", "−" + ampsLabel(top));
            maChart.refresh();
        } else {
            String[] values = {
                    b.stateText, b.sourceText,
                    b.ma == null ? "not reported" : b.ma + " mA", b.avgMa == null ? "not reported" : b.avgMa + " mA",
                    b.volts == null ? "—" : String.format(Locale.US, "%.2f V", b.volts), b.watts == null ? "—" : String.format(Locale.US, "%.1f W", b.watts),
                    b.tempC == null ? "—" : fmt1(b.tempC) + " °C",
                    b.chargeMah == null ? "not reported" : b.chargePlausible ? String.format(Locale.US, "%,d mAh", b.chargeMah) : String.format(Locale.US, "%,d (raw, unit not standard)", b.chargeCounterRaw),
                    b.health, b.technology.isEmpty() ? "—" : b.technology, b.timeToFull,
                    b.cycleCount == null ? (android.os.Build.VERSION.SDK_INT >= 34 ? "not reported" : "Android 14+ only") : String.valueOf(b.cycleCount)
            };
            for (int i = 0; i < kv.length; i++) kv[i].set(values[i]);
        }
        tBatMinCache = s.tBatMin;

        refreshCoolDown(s);
    }

    private void refreshCoolDown(DeviceMonitor.Snapshot s) {
        boolean active = Throttle.isActive(a);
        long left = Throttle.leftMs(a);
        startRow.setVisibility(active ? View.GONE : View.VISIBLE);
        stopRow.setVisibility(active ? View.VISIBLE : View.GONE);
        if (active) coolLeft.set(Throttle.leftText(left) + " left");
        setText(coolStatus, Throttle.describe(a));
        coolStatus.setTextColor(active ? 0xFF7A4A00 : TEXT);
        if (coolSection != null) coolSection.setSummary(active ? "throttled · " + Throttle.leftText(left) + " left" : "full performance");
        String thermal = s == null || s.thermalStatus == null ? (android.os.Build.VERSION.SDK_INT >= 29 ? "—" : "Android 10+ only") : s.thermalStatus
                + (s.thermalHeadroom == null ? "" : String.format(Locale.US, " · headroom %.0f %%", (1 - s.thermalHeadroom) * 100));
        setText(thermalText, "Thermal status: " + thermal);
    }

    private LinearLayout eventRow() {
        LinearLayout r = row();
        r.setGravity(Gravity.TOP);
        r.setPadding(0, dp(4), 0, dp(4));
        View dot = new View(a);
        GradientDrawable d = new GradientDrawable();
        d.setShape(GradientDrawable.OVAL);
        d.setColor(MUTED);
        dot.setBackground(d);
        LinearLayout.LayoutParams dlp = new LinearLayout.LayoutParams(dp(8), dp(8));
        dlp.topMargin = dp(5);
        dlp.rightMargin = dp(8);
        r.addView(dot, dlp);
        TextView t = text("", 12, MUTED, false);
        t.setTypeface(Typeface.MONOSPACE);
        t.setPadding(0, 0, dp(8), 0);
        r.addView(t);
        TextView x = text("", 12, TEXT, false);
        x.setLayoutParams(new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));
        r.addView(x);
        return r;
    }

    private static int colorOf(EventLog.Kind k) {
        switch (k) {
            case PRINTED: return DeviceViews.GOOD;
            case WARNING: return DeviceViews.WARNING;
            case OFFLINE: return DeviceViews.SERIOUS;
            default: return DeviceViews.CRITICAL;
        }
    }

    // ------------------------------------------------------------------ formatting

    private static String ago(int i, int n, String unit) {
        int back = n - 1 - i;
        return back == 0 ? "now" : back + " " + unit + " ago";
    }

    private static String val(float v, String unit) {
        return Float.isNaN(v) ? "—" : Math.round(v) + unit;
    }

    private static String fmt1(float v) {
        return String.format(Locale.US, "%.1f", v);
    }

    private static String kb(float kbps) {
        return kbps >= 1024 ? String.format(Locale.US, "%.1f MB/s", kbps / 1024) : Math.round(kbps) + " KB/s";
    }

    private static String gb(long mb) {
        return String.format(Locale.US, "%.1f", mb / 1024.0);
    }

    private static String mb(long bytes) {
        return bytes >= 1024 * 1024 ? String.format(Locale.US, "%.1f MB", bytes / 1048576.0) : (bytes / 1024) + " KB";
    }

    private static String amps(int ma) {
        String sign = ma > 0 ? "+" : ma < 0 ? "−" : "";
        int abs = Math.abs(ma);
        return sign + (abs >= 1000 ? String.format(Locale.US, "%.1f A", abs / 1000.0) : abs + " mA");
    }

    private static String ampsLabel(float ma) {
        return ma >= 1000 ? String.format(Locale.US, "%.0f A", ma / 1000) : Math.round(ma) + " mA";
    }

    private static void setText(TextView v, String t) {
        if (t == null) t = "";
        if (!t.contentEquals(v.getText())) v.setText(t);
    }

    // ------------------------------------------------------------------ widgets

    private LinearLayout card(String title) {
        LinearLayout card = new LinearLayout(a);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setBackground(rounded(CARD, 14));
        card.setElevation(dp(1));
        card.setPadding(dp(16), dp(14), dp(16), dp(14));
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        lp.bottomMargin = dp(12);
        card.setLayoutParams(lp);
        if (title != null) {
            TextView t = text(title, 15, TEXT, true);
            t.setPadding(0, 0, 0, dp(8));
            card.addView(t);
        }
        return card;
    }

    private LinearLayout panel() {
        LinearLayout p = new LinearLayout(a);
        p.setOrientation(LinearLayout.VERTICAL);
        p.setBackground(rounded(PANEL, 10));
        p.setPadding(dp(12), dp(10), dp(12), dp(10));
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        lp.topMargin = dp(10);
        p.setLayoutParams(lp);
        return p;
    }

    private LinearLayout row() {
        LinearLayout r = new LinearLayout(a);
        r.setOrientation(LinearLayout.HORIZONTAL);
        r.setGravity(Gravity.CENTER_VERTICAL);
        r.setLayoutParams(new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        return r;
    }

    private View chartTitle(String title, String[] names, int[] colors) {
        LinearLayout r = row();
        ((LinearLayout.LayoutParams) r.getLayoutParams()).topMargin = dp(12);
        TextView t = text(title, 12, TEXT, true);
        t.setLayoutParams(new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));
        r.addView(t);
        for (int i = 0; i < names.length; i++) {
            View swatch = new View(a);
            swatch.setBackgroundColor(colors[i]);
            LinearLayout.LayoutParams slp = new LinearLayout.LayoutParams(dp(12), dp(2));
            slp.leftMargin = dp(8);
            slp.rightMargin = dp(4);
            r.addView(swatch, slp);
            r.addView(text(names[i], 10.5f, MUTED, false));
        }
        return r;
    }

    private LinearLayout.LayoutParams chartParams(int heightDp) {
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(heightDp));
        lp.topMargin = dp(4);
        return lp;
    }

    private TextView text(String s, float sp, int color, boolean bold) {
        TextView t = new TextView(a);
        t.setText(s);
        t.setTextSize(sp);
        t.setTextColor(color);
        if (bold) t.setTypeface(Typeface.DEFAULT_BOLD);
        return t;
    }

    private TextView hint(String s) {
        TextView t = text(s, 11.5f, MUTED, false);
        t.setPadding(0, dp(8), 0, 0);
        return t;
    }

    private TextView chipLabel(String s, int bg, int fg) {
        TextView c = text(s, 9, fg, true);
        c.setPadding(dp(4), dp(1), dp(4), dp(1));
        c.setBackground(rounded(bg, 3));
        c.setLayoutParams(new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        return c;
    }

    private Button button(String label, View.OnClickListener onClick) {
        Button b = new Button(a);
        b.setText(label);
        b.setAllCaps(false);
        b.setTextSize(14);
        b.setTextColor(TEXT);
        b.setBackground(rounded(CHIP, 10));
        b.setStateListAnimator(null);
        b.setOnClickListener(onClick);
        b.setLayoutParams(new LinearLayout.LayoutParams(0, dp(44), 1f));
        return b;
    }

    private Button smallButton(String label, View.OnClickListener onClick) {
        Button b = button(label, onClick);
        b.setTextSize(12);
        b.setPadding(dp(12), 0, dp(12), 0);
        b.setMinWidth(0);
        b.setMinimumWidth(0);
        b.setLayoutParams(new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, dp(34)));
        return b;
    }

    private LinearLayout buttonRow(Button... buttons) {
        LinearLayout r = row();
        for (int i = 0; i < buttons.length; i++) {
            LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(0, dp(44), 1f);
            if (i > 0) lp.leftMargin = dp(8);
            buttons[i].setLayoutParams(lp);
            r.addView(buttons[i]);
        }
        ((LinearLayout.LayoutParams) r.getLayoutParams()).topMargin = dp(8);
        return r;
    }

    private View switchRow(String title, Switch sw) {
        LinearLayout r = row();
        r.setPadding(0, dp(6), 0, 0);
        TextView t = text(title, 13, TEXT, false);
        t.setLayoutParams(new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));
        r.addView(t);
        r.addView(sw);
        return r;
    }

    private GradientDrawable rounded(int color, int radiusDp) {
        GradientDrawable d = new GradientDrawable();
        d.setColor(color);
        d.setCornerRadius(dp(radiusDp));
        return d;
    }

    private int dp(int value) {
        return Math.round(a.getResources().getDisplayMetrics().density * value);
    }

    private void toast(String m) {
        Toast.makeText(a, m, Toast.LENGTH_SHORT).show();
    }
}
