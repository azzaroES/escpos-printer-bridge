package com.usblanbridge;

import android.Manifest;
import android.app.Activity;
import android.app.AlertDialog;
import android.app.PendingIntent;
import android.content.BroadcastReceiver;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.content.DialogInterface;
import android.content.Intent;
import android.content.IntentFilter;
import android.content.pm.PackageManager;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.hardware.usb.UsbDevice;
import android.hardware.usb.UsbManager;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.provider.Settings;
import android.text.InputType;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.widget.ArrayAdapter;
import android.widget.Button;
import android.widget.CompoundButton;
import android.widget.EditText;
import android.widget.HorizontalScrollView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.Spinner;
import android.widget.Switch;
import android.widget.TextView;
import android.widget.Toast;

import com.usblanbridge.core.BluetoothPrintTarget;
import com.usblanbridge.core.EposDeviceId;
import com.usblanbridge.core.License;
import com.usblanbridge.core.Log;
import com.usblanbridge.core.NetUtil;
import com.usblanbridge.core.NetworkPrinterScanner;
import com.usblanbridge.core.PrintHistory;
import com.usblanbridge.core.PrinterScanner;
import com.usblanbridge.core.RawServer;
import com.usblanbridge.core.TestReceipt;
import com.usblanbridge.print.PrintServiceStatus;
import com.usblanbridge.ui.Sections;
import com.usblanbridge.ui.Sections.SectionView;

import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.util.ArrayList;
import java.util.List;

/**
 * The control panel: a coloured header with the address to type into POS software and a live status, one big
 * Start/Stop button, then cards for the printer route, the NO CUT emergency switch, the last tickets as they
 * went to the printer, options, the Android print service, the ticket footer and the log.
 *
 * Built in code from framework widgets only, so the app carries no layout XML and no AndroidX dependency and
 * still runs on Android 5. Cards are rounded white panels on a light grey background; the route is chosen with
 * chips and only the controls of the chosen route are shown.
 */
public final class MainActivity extends Activity {

    private static final String ACTION_USB_PERMISSION = "com.usblanbridge.USB_PERMISSION";

    // palette
    private static final int PRIMARY = 0xFF1D4ED8;
    private static final int PRIMARY_DARK = 0xFF1E40AF;
    private static final int ON_PRIMARY_MUTED = 0xCCFFFFFF;
    private static final int GREEN = 0xFF15803D;
    private static final int GREEN_BG = 0xFFDCFCE7;
    private static final int RED = 0xFFB91C1C;
    private static final int RED_TEXT = 0xFF991B1B;
    private static final int RED_BG = 0xFFFEE2E2;
    private static final int AMBER = 0xFFB45309;
    private static final int CARD = 0xFFFFFFFF;
    private static final int TEXT = 0xFF111827;
    private static final int MUTED = 0xFF6B7280;
    private static final int CHIP = 0xFFE5E7EB;
    private static final int LINE = 0xFFE5E7EB;
    private static final int LOG_BG = 0xFF0F172A;
    private static final int LOG_TEXT = 0xFFCBD5E1;

    /** Printing routes offered as chips, kept in step with ROUTE_LABELS. */
    private static final String[] ROUTE_MODES = {
            Prefs.TARGET_USB, Prefs.TARGET_SUNMI, Prefs.TARGET_BLUETOOTH, Prefs.TARGET_TCP
    };
    private static final String[] ROUTE_LABELS = {"USB", "Built-in", "Bluetooth", "Network"};
    private static final int MAX_TICKETS = 8;

    private Prefs prefs;
    private final Handler ui = new Handler(Looper.getMainLooper());
    private final List<UsbDevice> usbDevices = new ArrayList<>();
    private boolean resumed;

    // header
    private TextView statusPill;
    private TextView addressView;
    private TextView addressHint;
    private TextView eposView;
    private TextView httpsView;
    private TextView copyNote;
    private Button btnStartStop;

    // device cards, ePOS device id
    private DeviceCards deviceCards;
    private DeviceMonitor monitor;
    private EditText devIdField;
    private TextView devIdNote;
    private EditText httpsPort;

    // collapsible, draggable sections
    private Sections.Store sectionStore;
    private Sections.Column column;
    private SectionView printerSection, noCutSection, ticketsSection, optionsSection, devIdSection, footerSection;

    private final Runnable hideCopyNote = new Runnable() {
        @Override
        public void run() {
            copyNote.setVisibility(View.GONE);
        }
    };

    private final DeviceMonitor.Listener monitorListener = new DeviceMonitor.Listener() {
        @Override
        public void onSnapshot(final DeviceMonitor.Snapshot s) {
            ui.post(new Runnable() {
                @Override
                public void run() {
                    if (resumed) deviceCards.refresh(s);
                }
            });
        }
    };

    // printer card
    private final TextView[] chips = new TextView[ROUTE_MODES.length];
    private final View[] routePanels = new View[ROUTE_MODES.length];
    private int route;
    private Spinner deviceSpinner;
    private TextView btChosen;
    private EditText tcpHost;
    private EditText tcpPort;

    // emergency
    private Switch noCutSwitch;
    private TextView noCutText;

    // tickets
    private LinearLayout ticketsBox;
    private TextView ticketsEmpty;

    // options
    private EditText rawPort;
    private EditText eposPort;
    private Switch statusReplies;
    private Switch autoStart;
    private Switch scaleSwitch;
    private EditText scaleHost;
    private EditText scalePortField;
    private EditText scaleHttpField;
    private EditText scaleUnit;
    private SectionView scaleSection;

    // print service, footer, log
    private TextView printServiceView;
    private TextView footerView;
    private EditText licenceKey;
    private TextView logView;
    private ScrollView logScroll;

    private final Runnable ticker = new Runnable() {
        @Override
        public void run() {
            if (!resumed) return;
            refreshStatus();
            ui.postDelayed(this, 2000);
        }
    };

    private final Log.Listener logListener = new Log.Listener() {
        @Override
        public void onLine(final String line) {
            ui.post(new Runnable() {
                @Override
                public void run() {
                    appendLog(line);
                }
            });
        }
    };

    private final PrintHistory.Listener historyListener = new PrintHistory.Listener() {
        @Override
        public void onRecord(PrintHistory.Record record) {
            ui.post(new Runnable() {
                @Override
                public void run() {
                    refreshTickets();
                    refreshStatus();
                }
            });
        }
    };

    private final BroadcastReceiver usbReceiver = new BroadcastReceiver() {
        @Override
        public void onReceive(Context context, Intent intent) {
            String action = intent.getAction();
            if (ACTION_USB_PERMISSION.equals(action)) {
                boolean granted = intent.getBooleanExtra(UsbManager.EXTRA_PERMISSION_GRANTED, false);
                Log.i(granted ? "USB permission granted." : "USB permission refused.");
                refreshDevices();
            } else if (UsbManager.ACTION_USB_DEVICE_ATTACHED.equals(action)
                    || UsbManager.ACTION_USB_DEVICE_DETACHED.equals(action)) {
                refreshDevices();
            }
        }
    };

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        prefs = new Prefs(this);
        Log.setDirectory(getExternalFilesDir(null));
        sectionStore = new Sections.Store() {
            @Override
            public boolean isCollapsed(String key, boolean def) {
                return prefs.isSectionCollapsed(key, def);
            }

            @Override
            public void setCollapsed(String key, boolean collapsed) {
                prefs.setSectionCollapsed(key, collapsed);
            }

            @Override
            public String getOrder(String group) {
                return prefs.getSectionOrder(group);
            }

            @Override
            public void setOrder(String group, String csv) {
                prefs.setSectionOrder(group, csv);
            }
        };
        deviceCards = new DeviceCards(this, prefs, sectionStore);
        monitor = DeviceMonitor.get(this);
        setContentView(buildUi());
        Log.i("Log file: " + Log.getLogFile());
        monitor.addListener(monitorListener);

        IntentFilter filter = new IntentFilter(ACTION_USB_PERMISSION);
        filter.addAction(UsbManager.ACTION_USB_DEVICE_ATTACHED);
        filter.addAction(UsbManager.ACTION_USB_DEVICE_DETACHED);
        if (Build.VERSION.SDK_INT >= 34) {
            registerReceiver(usbReceiver, filter, Context.RECEIVER_NOT_EXPORTED);
        } else {
            registerReceiver(usbReceiver, filter);
        }

        Log.addListener(logListener);
        PrintHistory.addListener(historyListener);
        appendLog(Log.snapshot());
        requestNotificationPermissionIfNeeded();
        refreshDevices();
        refreshTickets();
        refreshStatus();
    }

    @Override
    protected void onResume() {
        super.onResume();
        resumed = true;
        refreshDevices();
        refreshStatus();
        ui.removeCallbacks(ticker);
        ui.postDelayed(ticker, 2000);
        monitor.acquire("activity");
        monitor.setVisible(true);
        DeviceMonitor.Snapshot last = monitor.last();
        if (last != null) deviceCards.refresh(last);
    }

    @Override
    protected void onPause() {
        resumed = false;
        ui.removeCallbacks(ticker);
        monitor.setVisible(false);
        monitor.release("activity");
        super.onPause();
    }

    @Override
    protected void onDestroy() {
        monitor.removeListener(monitorListener);
        Log.removeListener(logListener);
        PrintHistory.removeListener(historyListener);
        try {
            unregisterReceiver(usbReceiver);
        } catch (Exception ignored) {
        }
        super.onDestroy();
    }

    // ------------------------------------------------------------------ ui construction

    private View buildUi() {
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setPadding(dp(14), dp(12), dp(14), dp(24));

        root.addView(buildHeader());
        root.addView(buildActions());

        // Every card is a section: tap its heading to fold it, drag its grip to move it. Order and folds are kept.
        column = new Sections.Column(this, "main", sectionStore);
        column.add(buildPrinterCard());
        column.add(buildEmergencyCard());
        column.add((SectionView) deviceCards.deviceCard());
        column.add((SectionView) deviceCards.batteryCard());
        column.add((SectionView) deviceCards.coolDownCard());
        column.add(buildEposIdCard());
        column.add(buildTicketsCard());
        column.add(buildOptionsCard());
        column.add(buildScaleCard());
        column.add(buildPrintServiceCard());
        column.add(buildFooterCard());
        column.add(buildLogCard());
        column.applySavedOrder();
        root.addView(column, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        TextView foot = hintText("Tap a card's heading to fold it; drag ≡ to put the cards in the order you want. The app remembers both.");
        foot.setGravity(Gravity.CENTER);
        root.addView(foot);

        ScrollView outer = new ScrollView(this);
        outer.setFillViewport(true);
        outer.addView(root, new ViewGroup.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        return outer;
    }

    /** The coloured header: title, live status pill, and the address to type into POS software. */
    private View buildHeader() {
        LinearLayout header = new LinearLayout(this);
        header.setOrientation(LinearLayout.VERTICAL);
        header.setBackground(rounded(PRIMARY, 16));
        header.setPadding(dp(18), dp(16), dp(18), dp(18));
        header.setElevation(dp(2));
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        lp.bottomMargin = dp(12);
        header.setLayoutParams(lp);

        LinearLayout titleRow = new LinearLayout(this);
        titleRow.setOrientation(LinearLayout.HORIZONTAL);
        titleRow.setGravity(Gravity.CENTER_VERTICAL);
        TextView title = text("Printer Bridge", 20, Color.WHITE, true);
        title.setLayoutParams(new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));
        titleRow.addView(title);
        statusPill = text("Stopped", 12, TEXT, true);
        statusPill.setPadding(dp(10), dp(4), dp(10), dp(4));
        statusPill.setBackground(rounded(CHIP, 999));
        statusPill.setContentDescription("status_pill");
        titleRow.addView(statusPill);
        header.addView(titleRow);

        TextView hint = text("Add this printer in your POS app as", 13, ON_PRIMARY_MUTED, false);
        hint.setPadding(0, dp(14), 0, dp(2));
        header.addView(hint);

        addressView = text("—", 26, Color.WHITE, true);
        addressView.setTypeface(Typeface.MONOSPACE, Typeface.BOLD);
        addressView.setContentDescription("address");
        addressView.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                copyToClipboard("Printer address", addressView.getText().toString(), "Address copied");
            }
        });
        header.addView(addressView);

        addressHint = text("Tap to copy. Raw printing, works from an IP address alone.", 12, ON_PRIMARY_MUTED, false);
        header.addView(addressHint);

        eposView = text("", 12, ON_PRIMARY_MUTED, false);
        eposView.setPadding(0, dp(8), 0, 0);
        eposView.setTypeface(Typeface.MONOSPACE);
        eposView.setContentDescription("epos_line");
        eposView.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                copyEposLink(false);
            }
        });
        header.addView(eposView);

        httpsView = text("", 12, ON_PRIMARY_MUTED, false);
        httpsView.setPadding(0, dp(2), 0, 0);
        httpsView.setTypeface(Typeface.MONOSPACE);
        httpsView.setContentDescription("https_line");
        httpsView.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                copyEposLink(true);
            }
        });
        header.addView(httpsView);

        LinearLayout links = new LinearLayout(this);
        links.setOrientation(LinearLayout.HORIZONTAL);
        Button copyEpos = headerButton("Copy ePOS link", new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                copyEposLink(false);
            }
        });
        copyEpos.setContentDescription("btn_copy_epos");
        Button copyCert = headerButton("Copy certificate link", new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                copyCertificateLink();
            }
        });
        copyCert.setContentDescription("btn_copy_cert");
        LinearLayout.LayoutParams b1 = new LinearLayout.LayoutParams(0, dp(36), 1f);
        LinearLayout.LayoutParams b2 = new LinearLayout.LayoutParams(0, dp(36), 1f);
        b2.leftMargin = dp(8);
        links.addView(copyEpos, b1);
        links.addView(copyCert, b2);
        LinearLayout.LayoutParams llp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        llp.topMargin = dp(10);
        header.addView(links, llp);

        copyNote = text("", 11.5f, Color.WHITE, false);
        copyNote.setPadding(dp(10), dp(6), dp(10), dp(6));
        copyNote.setBackground(rounded(0x33FFFFFF, 8));
        copyNote.setVisibility(View.GONE);
        copyNote.setContentDescription("copy_note");
        LinearLayout.LayoutParams nlp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        nlp.topMargin = dp(8);
        header.addView(copyNote, nlp);
        return header;
    }

    private Button headerButton(String label, View.OnClickListener onClick) {
        Button b = button(label, onClick);
        b.setTextSize(12.5f);
        b.setTextColor(Color.WHITE);
        b.setTypeface(Typeface.DEFAULT_BOLD);
        b.setBackground(outlined(0x99FFFFFF, 0x22FFFFFF, 10));
        b.setPadding(dp(6), 0, dp(6), 0);
        return b;
    }

    private SectionView section(String key, String title) {
        return new SectionView(this, key, title, true, CARD, sectionStore);
    }

    /** The card where the ePOS device id is set: the name POS apps put in the URL or in createDevice. */
    private SectionView buildEposIdCard() {
        LinearLayout card = body();
        devIdSection = section("devid", "ePOS device id");
        devIdSection.setContentDescription("card_devid");
        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        row.setGravity(Gravity.CENTER_VERTICAL);
        devIdField = edit(prefs.getEposDeviceId(), "local_printer", InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS, 1f);
        devIdField.setContentDescription("field_devid");
        row.addView(labelled("Answer to", devIdField));
        Button apply = smallButton("Apply", new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                applyDeviceId(true);
            }
        });
        apply.setContentDescription("btn_devid_apply");
        LinearLayout.LayoutParams alp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, dp(40));
        alp.leftMargin = dp(8);
        row.addView(apply, alp);
        card.addView(row);
        devIdField.setOnFocusChangeListener(new View.OnFocusChangeListener() {
            @Override
            public void onFocusChange(View v, boolean hasFocus) {
                if (!hasFocus) applyDeviceId(false);
            }
        });
        devIdNote = hintText("");
        card.addView(devIdNote);
        card.addView(hintText("The name a POS app puts in the ePOS URL (?devid=) or in createDevice. Default local_printer, or name it after the "
                + "printer, for example kitchen. A request naming another id gets DeviceNotFound, as a real printer answers. "
                + "The header links and Copy ePOS link use this id. Letters, digits, '_', '-' and '.' only, up to 32."));
        return devIdSection.content(card);
    }

    private void applyDeviceId(boolean announce) {
        String wanted = EposDeviceId.sanitize(devIdField.getText().toString());
        if (!wanted.equals(devIdField.getText().toString())) devIdField.setText(wanted);
        if (wanted.equals(prefs.getEposDeviceId())) {
            if (announce) toast("Answering to \"" + wanted + "\".");
            return;
        }
        prefs.setEposDeviceId(wanted);
        Log.i("ePOS device id set to \"" + wanted + "\"; the endpoint answers it at once.");
        if (announce) toast("Answering to \"" + wanted + "\".");
        refreshStatus();
    }

    private String linkHost() {
        String ip = NetUtil.getLanAddress();
        return ip == null ? "<phone address>" : ip;
    }

    private void copyEposLink(boolean https) {
        int port = https ? parsePort(httpsPort.getText().toString(), 8443) : parsePort(eposPort.getText().toString(), 8080);
        String url = EposDeviceId.serviceUrl(https, linkHost(), port, prefs.getEposDeviceId());
        copyToClipboard("ePOS link", url, "ePOS link copied");
        showCopyNote("Copied " + url + ". Paste it into the POS app; in the SDK the same id goes into createDevice."
                + (https ? " HTTPS: open the certificate link once on that device first." : ""));
    }

    private void copyCertificateLink() {
        String url = EposDeviceId.certificateUrl(linkHost(), parsePort(httpsPort.getText().toString(), 8443));
        copyToClipboard("Certificate link", url, "Certificate link copied");
        showCopyNote("Copied " + url + ". Open it once on each device that prints from an https page and accept the warning; "
                + "the page then confirms the device trusts the bridge, and offers the .cer file for a permanent install.");
    }

    private void showCopyNote(String text) {
        copyNote.setText(text);
        copyNote.setVisibility(View.VISIBLE);
        ui.removeCallbacks(hideCopyNote);
        ui.postDelayed(hideCopyNote, 8000);
    }

    /** One big Start/Stop button and the test print beside it. */
    private View buildActions() {
        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        lp.bottomMargin = dp(12);
        row.setLayoutParams(lp);

        btnStartStop = primaryButton("Start sharing", GREEN, new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                if (BridgeService.isRunning()) stop();
                else start();
            }
        });
        btnStartStop.setContentDescription("btn_start_stop");
        btnStartStop.setLayoutParams(new LinearLayout.LayoutParams(0, dp(52), 2f));
        row.addView(btnStartStop);

        Button test = outlineButton("Test print", new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                testPrint();
            }
        });
        test.setContentDescription("btn_test");
        LinearLayout.LayoutParams tlp = new LinearLayout.LayoutParams(0, dp(52), 1f);
        tlp.leftMargin = dp(8);
        test.setLayoutParams(tlp);
        row.addView(test);
        return row;
    }

    private SectionView buildPrinterCard() {
        LinearLayout card = body();
        printerSection = section("printer", "Printer");

        // Route chips
        LinearLayout chipRow = new LinearLayout(this);
        chipRow.setOrientation(LinearLayout.HORIZONTAL);
        for (int i = 0; i < ROUTE_MODES.length; i++) {
            final int index = i;
            TextView chip = chip(ROUTE_LABELS[i]);
            chip.setContentDescription("route_" + ROUTE_MODES[i]);
            chip.setOnClickListener(new View.OnClickListener() {
                @Override
                public void onClick(View v) {
                    selectRoute(index, true);
                }
            });
            LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(0, dp(40), 1f);
            lp.rightMargin = i < ROUTE_MODES.length - 1 ? dp(6) : 0;
            chip.setLayoutParams(lp);
            chips[i] = chip;
            chipRow.addView(chip);
        }
        card.addView(chipRow);

        // USB
        LinearLayout usb = panel();
        deviceSpinner = new Spinner(this);
        deviceSpinner.setContentDescription("usb_spinner");
        usb.addView(deviceSpinner);
        LinearLayout usbButtons = buttonRow(
                button("Refresh", new View.OnClickListener() {
                    @Override
                    public void onClick(View v) {
                        refreshDevices();
                    }
                }),
                button("Grant USB access", new View.OnClickListener() {
                    @Override
                    public void onClick(View v) {
                        requestUsbPermission();
                    }
                }));
        usb.addView(usbButtons);
        usb.addView(hintText("A receipt printer on an OTG cable. Android asks once for permission."));
        routePanels[0] = usb;
        card.addView(usb);

        // Built-in (Sunmi)
        LinearLayout sunmi = panel();
        sunmi.addView(hintText("The terminal's built-in thermal printer, through the vendor's printer service. Tap Detect printers to check it is present and answering."));
        routePanels[1] = sunmi;
        card.addView(sunmi);

        // Bluetooth
        LinearLayout bt = panel();
        btChosen = text("", 14, TEXT, false);
        bt.addView(btChosen);
        bt.addView(buttonRow(button("Choose paired printer", new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                chooseBluetoothPrinter();
            }
        })));
        bt.addView(hintText("Pair the printer in Android's Bluetooth settings first. Only paired devices are offered, so no location permission is needed."));
        routePanels[2] = bt;
        card.addView(bt);

        // Network
        LinearLayout tcp = panel();
        LinearLayout tcpRow = new LinearLayout(this);
        tcpRow.setOrientation(LinearLayout.HORIZONTAL);
        tcpHost = edit(prefs.getTcpHost(), "Printer address", InputType.TYPE_CLASS_TEXT, 3f);
        tcpHost.setContentDescription("field_host");
        tcpPort = edit(String.valueOf(prefs.getTcpPort()), "Port", InputType.TYPE_CLASS_NUMBER, 1f);
        tcpPort.setContentDescription("field_port");
        ((LinearLayout.LayoutParams) tcpPort.getLayoutParams()).leftMargin = dp(8);
        tcpRow.addView(tcpHost);
        tcpRow.addView(tcpPort);
        tcp.addView(tcpRow);
        Button scan = button("Scan network for printers", new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                scanNetwork();
            }
        });
        scan.setContentDescription("btn_scan");
        tcp.addView(buttonRow(scan));
        tcp.addView(hintText("Relays every ticket to a printer already on the Wi-Fi. The scan tries ports 9100, 515 and 631 and asks 9100 for the model."));
        routePanels[3] = tcp;
        card.addView(tcp);

        Button detect = button("Detect printers on this device", new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                detectPrinters();
            }
        });
        detect.setContentDescription("btn_detect");
        LinearLayout detectRow = buttonRow(detect);
        ((LinearLayout.LayoutParams) detectRow.getLayoutParams()).topMargin = dp(6);
        card.addView(detectRow);

        selectRoute(indexOfRoute(prefs.getTargetMode()), false);
        return printerSection.content(card);
    }

    private SectionView buildEmergencyCard() {
        noCutSection = new SectionView(this, "nocut", "NO CUT · emergency", true, RED_BG, sectionStore).titleColor(RED_TEXT);
        noCutText = text("", 13, RED_TEXT, false);
        noCutText.setPadding(dp(4), dp(2), dp(8), 0);

        noCutSwitch = new Switch(this);
        noCutSwitch.setChecked(prefs.isNoCut());
        noCutSwitch.setContentDescription("switch_nocut");
        noCutSwitch.setOnCheckedChangeListener(new CompoundButton.OnCheckedChangeListener() {
            @Override
            public void onCheckedChanged(CompoundButton b, boolean checked) {
                // Saved the moment it changes and read on every write, so it applies to the next bytes printed.
                prefs.setNoCut(checked);
                if (checked) Log.w("NO CUT switched on: cutter commands are removed from every ticket, "
                        + prefs.getNoCutFeedLines() + " lines are fed instead.");
                else Log.i("NO CUT switched off: cutter commands reach the printer again.");
                toast(checked ? "NO CUT is on. Tear receipts by hand." : "Cutting again.");
                refreshStatus();
            }
        });
        noCutSection.extraFill(noCutSwitch);
        return noCutSection.content(noCutText);
    }

    private SectionView buildTicketsCard() {
        LinearLayout card = body();
        ticketsSection = section("tickets", "Last tickets");
        ticketsEmpty = hintText("Nothing printed yet. Each ticket that goes through the phone is listed here; tap one to see it as it went to the printer.");
        card.addView(ticketsEmpty);
        ticketsBox = new LinearLayout(this);
        ticketsBox.setOrientation(LinearLayout.VERTICAL);
        card.addView(ticketsBox);
        card.addView(buttonRow(button("Clear list", new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                PrintHistory.clear();
                refreshTickets();
            }
        })));
        return ticketsSection.content(card);
    }

    private SectionView buildOptionsCard() {
        LinearLayout card = body();
        optionsSection = section("options", "Options");

        LinearLayout portRow = new LinearLayout(this);
        portRow.setOrientation(LinearLayout.HORIZONTAL);
        rawPort = edit(String.valueOf(prefs.getRawPort()), "Raw port", InputType.TYPE_CLASS_NUMBER, 1f);
        rawPort.setContentDescription("field_raw_port");
        eposPort = edit(String.valueOf(prefs.getEposPort()), "ePOS port", InputType.TYPE_CLASS_NUMBER, 1f);
        eposPort.setContentDescription("field_epos_port");
        httpsPort = edit(String.valueOf(prefs.getEposHttpsPort()), "HTTPS port", InputType.TYPE_CLASS_NUMBER, 1f);
        httpsPort.setContentDescription("field_https_port");
        portRow.addView(labelled("Raw port (9100)", rawPort));
        View eposBox = labelled("ePOS port (8080)", eposPort);
        ((LinearLayout.LayoutParams) eposBox.getLayoutParams()).leftMargin = dp(8);
        portRow.addView(eposBox);
        View httpsBox = labelled("HTTPS port (8443)", httpsPort);
        ((LinearLayout.LayoutParams) httpsBox.getLayoutParams()).leftMargin = dp(8);
        portRow.addView(httpsBox);
        card.addView(portRow);
        card.addView(hintText("Raw works from an IP address alone. ePOS cannot use ports 80 or 443 on an unrooted phone, so ePOS clients must "
                + "include the port in the URL. HTTPS uses a self-signed certificate the phone makes for its own address; each client device "
                + "accepts it once at the certificate link. Port changes apply at the next start."));

        statusReplies = new Switch(this);
        statusReplies.setChecked(prefs.isStatusReplies());
        card.addView(switchRow("Answer printer status queries", "Replies to DLE EOT, GS I and GS ( H as a TM-T20II would, so POS apps do not wait for a USB printer that cannot answer.", statusReplies));

        autoStart = new Switch(this);
        autoStart.setChecked(prefs.isAutoStart());
        card.addView(switchRow("Start sharing after a reboot", "The bridge comes back on its own when the phone restarts.", autoStart));
        return optionsSection.content(card);
    }

    /** The card that reads a weighing scale and publishes it at /scale, mirroring the Windows Scale tab. */
    private SectionView buildScaleCard() {
        LinearLayout card = body();
        scaleSection = section("scale", "Scale");

        scaleSwitch = new Switch(this);
        scaleSwitch.setChecked(prefs.isScaleEnabled());
        card.addView(switchRow("Publish a scale on the network", "Reads a scale and serves the weight at /scale so any POS on the Wi-Fi can read it. Applies at the next start.", scaleSwitch));

        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        scaleHost = edit(prefs.getScaleHost(), "192.168.1.50", InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS, 2f);
        scaleHost.setContentDescription("field_scale_host");
        scalePortField = edit(String.valueOf(prefs.getScalePort()), "4001", InputType.TYPE_CLASS_NUMBER, 1f);
        scalePortField.setContentDescription("field_scale_port");
        row.addView(labelled("Scale host / IP", scaleHost));
        View pB = labelled("Port", scalePortField);
        ((LinearLayout.LayoutParams) pB.getLayoutParams()).leftMargin = dp(8);
        row.addView(pB);
        card.addView(row);

        LinearLayout row2 = new LinearLayout(this);
        row2.setOrientation(LinearLayout.HORIZONTAL);
        scaleHttpField = edit(String.valueOf(prefs.getScaleHttpPort()), "8020", InputType.TYPE_CLASS_NUMBER, 1f);
        scaleHttpField.setContentDescription("field_scale_http_port");
        scaleUnit = edit(prefs.getScaleDisplayUnit(), "as scale (kg/g/lb/oz)", InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS, 1f);
        scaleUnit.setContentDescription("field_scale_unit");
        row2.addView(labelled("Publish on port", scaleHttpField));
        View uB = labelled("Show as", scaleUnit);
        ((LinearLayout.LayoutParams) uB.getLayoutParams()).leftMargin = dp(8);
        row2.addView(uB);
        card.addView(row2);

        card.addView(hintText("A network scale streams its weight over TCP: enter its address and port. A serial scale is reached by plugging it into any machine on the LAN and reading it here over TCP. The weight is served at http://<phone>:8020/scale, with an exact kg/g/lb/oz conversion when you set \"Show as\". A port below 1024 cannot be used on an unrooted phone."));
        return scaleSection.content(card);
    }

    private SectionView buildPrintServiceCard() {
        LinearLayout card = body();
        printServiceView = text("", 13, MUTED, false);
        card.addView(printServiceView);
        card.addView(buttonRow(button("Android print settings", new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                openPrintSettings();
            }
        })));
        return section("printservice", "Print from any app").content(card);
    }

    private SectionView buildFooterCard() {
        LinearLayout card = body();
        footerSection = section("footer", "Ticket footer & licence");
        footerView = text("", 13, MUTED, false);
        card.addView(footerView);
        licenceKey = edit(prefs.getLicenseKey(), "Licence key", InputType.TYPE_CLASS_TEXT, 1f);
        licenceKey.setLayoutParams(new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        card.addView(licenceKey);
        card.addView(buttonRow(
                button("Apply key", new View.OnClickListener() {
                    @Override
                    public void onClick(View v) {
                        applyLicence();
                    }
                }),
                button("Copy device ID", new View.OnClickListener() {
                    @Override
                    public void onClick(View v) {
                        copyDeviceId();
                    }
                })));
        return footerSection.content(card);
    }

    private SectionView buildLogCard() {
        SectionView sec = section("log", "Log");
        Button copy = smallButton("Copy", new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                copyToClipboard("Bridge log", logView.getText().toString(), "Log copied");
            }
        });
        sec.extra(copy);

        logView = new TextView(this);
        logView.setTypeface(Typeface.MONOSPACE);
        logView.setTextSize(11);
        logView.setTextColor(LOG_TEXT);
        logView.setPadding(dp(10), dp(8), dp(10), dp(8));
        logView.setTextIsSelectable(true);
        logScroll = new ScrollView(this);
        logScroll.setBackground(rounded(LOG_BG, 10));
        logScroll.addView(logView);
        LinearLayout card = body();
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(220));
        lp.topMargin = dp(8);
        logScroll.setLayoutParams(lp);
        card.addView(logScroll);
        return sec.content(card);
    }

    /** The body of a section: a plain vertical box; the section supplies the card, heading and spacing. */
    private LinearLayout body() {
        LinearLayout b = new LinearLayout(this);
        b.setOrientation(LinearLayout.VERTICAL);
        b.setPadding(dp(4), 0, 0, 0);
        return b;
    }

    // ------------------------------------------------------------------ widget helpers

    private GradientDrawable rounded(int color, int radiusDp) {
        GradientDrawable d = new GradientDrawable();
        d.setColor(color);
        d.setCornerRadius(dp(radiusDp));
        return d;
    }

    private GradientDrawable outlined(int strokeColor, int fill, int radiusDp) {
        GradientDrawable d = rounded(fill, radiusDp);
        d.setStroke(dp(1), strokeColor);
        return d;
    }

    /** A white rounded panel with an optional title, spaced from the next one. */
    private LinearLayout card(String title) {
        LinearLayout card = new LinearLayout(this);
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
        LinearLayout p = new LinearLayout(this);
        p.setOrientation(LinearLayout.VERTICAL);
        p.setPadding(0, dp(10), 0, 0);
        p.setVisibility(View.GONE);
        return p;
    }

    private TextView text(String s, float sp, int color, boolean bold) {
        TextView t = new TextView(this);
        t.setText(s);
        t.setTextSize(sp);
        t.setTextColor(color);
        if (bold) t.setTypeface(Typeface.DEFAULT_BOLD);
        return t;
    }

    private TextView hintText(String s) {
        TextView t = text(s, 12, MUTED, false);
        t.setPadding(0, dp(6), 0, 0);
        return t;
    }

    private TextView chip(String label) {
        TextView c = new TextView(this);
        c.setText(label);
        c.setTextSize(13);
        c.setGravity(Gravity.CENTER);
        c.setTypeface(Typeface.DEFAULT_BOLD);
        c.setClickable(true);
        return c;
    }

    private Button button(String label, View.OnClickListener onClick) {
        Button b = new Button(this);
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

    private Button primaryButton(String label, int color, View.OnClickListener onClick) {
        Button b = button(label, onClick);
        b.setTextSize(16);
        b.setTypeface(Typeface.DEFAULT_BOLD);
        b.setTextColor(Color.WHITE);
        b.setBackground(rounded(color, 12));
        return b;
    }

    private Button outlineButton(String label, View.OnClickListener onClick) {
        Button b = button(label, onClick);
        b.setTextColor(PRIMARY);
        b.setTypeface(Typeface.DEFAULT_BOLD);
        b.setBackground(outlined(PRIMARY, CARD, 12));
        return b;
    }

    private LinearLayout buttonRow(Button... buttons) {
        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        for (int i = 0; i < buttons.length; i++) {
            LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(0, dp(44), 1f);
            if (i > 0) lp.leftMargin = dp(8);
            buttons[i].setLayoutParams(lp);
            row.addView(buttons[i]);
        }
        LinearLayout.LayoutParams rlp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        rlp.topMargin = dp(8);
        row.setLayoutParams(rlp);
        return row;
    }

    private View switchRow(String title, String subtitle, Switch sw) {
        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        row.setGravity(Gravity.CENTER_VERTICAL);
        row.setPadding(0, dp(12), 0, 0);
        LinearLayout texts = new LinearLayout(this);
        texts.setOrientation(LinearLayout.VERTICAL);
        texts.setLayoutParams(new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));
        texts.addView(text(title, 14, TEXT, false));
        if (subtitle != null && !subtitle.isEmpty()) {
            TextView sub = text(subtitle, 12, MUTED, false);
            sub.setPadding(0, dp(2), dp(8), 0);
            texts.addView(sub);
        }
        row.addView(texts);
        row.addView(sw);
        return row;
    }

    private View labelled(String label, View field) {
        LinearLayout box = new LinearLayout(this);
        box.setOrientation(LinearLayout.VERTICAL);
        TextView t = text(label, 12, MUTED, false);
        t.setPadding(0, 0, 0, dp(2));
        box.addView(t);
        field.setLayoutParams(new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        box.addView(field);
        box.setLayoutParams(new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));
        return box;
    }

    private EditText edit(String value, String hint, int inputType, float weight) {
        EditText e = new EditText(this);
        e.setText(value);
        e.setHint(hint);
        e.setInputType(inputType);
        e.setSingleLine(true);
        e.setTextSize(15);
        e.setLayoutParams(new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, weight));
        return e;
    }

    private int dp(int value) {
        return Math.round(getResources().getDisplayMetrics().density * value);
    }

    private void copyToClipboard(String label, String value, String message) {
        try {
            ClipboardManager clipboard = (ClipboardManager) getSystemService(Context.CLIPBOARD_SERVICE);
            if (clipboard != null) {
                clipboard.setPrimaryClip(ClipData.newPlainText(label, value));
                toast(message);
                return;
            }
        } catch (Exception ignored) {
        }
        toast(value);
    }

    // ------------------------------------------------------------------ routes

    private static int indexOfRoute(String mode) {
        for (int i = 0; i < ROUTE_MODES.length; i++) if (ROUTE_MODES[i].equals(mode)) return i;
        return 0;
    }

    private String selectedTargetMode() {
        return ROUTE_MODES[route];
    }

    private void selectTargetMode(String mode) {
        selectRoute(indexOfRoute(mode), false);
    }

    private void selectRoute(int index, boolean fromUser) {
        route = index;
        for (int i = 0; i < chips.length; i++) {
            boolean on = i == index;
            chips[i].setBackground(rounded(on ? PRIMARY : CHIP, 10));
            chips[i].setTextColor(on ? Color.WHITE : TEXT);
            if (routePanels[i] != null) routePanels[i].setVisibility(on ? View.VISIBLE : View.GONE);
        }
        if (btChosen != null) {
            String address = prefs.getBluetoothAddress();
            btChosen.setText(address == null || address.isEmpty() ? "No printer chosen yet." : "Chosen printer: " + address);
        }
        if (fromUser) prefs.setTargetMode(ROUTE_MODES[index]);
    }

    // ------------------------------------------------------------------ devices and permissions

    /**
     * Lists paired Bluetooth devices so one can be chosen as the printer.
     * Only paired devices are offered, deliberately: discovery would drag in location permissions.
     */
    private void chooseBluetoothPrinter() {
        if (BluetoothPrintTarget.adapter(this) == null) {
            toast("This device has no Bluetooth.");
            return;
        }
        if (BluetoothPrintTarget.needsRuntimePermission(this)) {
            if (Build.VERSION.SDK_INT >= 23) {
                requestPermissions(new String[]{"android.permission.BLUETOOTH_CONNECT"}, 2);
            }
            toast("Allow Bluetooth access, then tap again.");
            return;
        }

        final List<android.bluetooth.BluetoothDevice> paired = BluetoothPrintTarget.pairedDevices(this);
        if (paired.isEmpty()) {
            new AlertDialog.Builder(this)
                    .setTitle("No paired Bluetooth devices")
                    .setMessage("Pair the printer in Android's Bluetooth settings first, then come back here.\n\n"
                            + "If Bluetooth is switched off, turn it on.")
                    .setPositiveButton("OK", null)
                    .show();
            return;
        }

        final String[] labels = new String[paired.size()];
        for (int i = 0; i < paired.size(); i++) {
            android.bluetooth.BluetoothDevice d = paired.get(i);
            String name = BluetoothPrintTarget.safeName(d);
            if (name == null || name.isEmpty()) name = d.getAddress();
            labels[i] = name + (BluetoothPrintTarget.looksLikePrinter(d) ? "   looks like a printer" : "");
        }

        new AlertDialog.Builder(this)
                .setTitle("Paired Bluetooth devices")
                .setItems(labels, new DialogInterface.OnClickListener() {
                    @Override
                    public void onClick(DialogInterface dialog, int which) {
                        android.bluetooth.BluetoothDevice d = paired.get(which);
                        prefs.setBluetoothAddress(d.getAddress());
                        selectTargetMode(Prefs.TARGET_BLUETOOTH);
                        saveSettings();
                        toast("Selected " + labels[which]);
                        Log.i("Bluetooth printer selected: " + d.getAddress());
                    }
                })
                .setNegativeButton("Cancel", null)
                .show();
    }

    /** Sweeps the local subnet for printers, because typing an address by hand only helps if you already know it. */
    private void scanNetwork() {
        final NetworkPrinterScanner.Range range = NetworkPrinterScanner.localRange();
        if (range == null) {
            toast("No Wi-Fi connection, so there is nothing to scan.");
            return;
        }
        toast("Scanning " + range.first + " to " + range.last + "...");
        Log.i("Scanning " + range.describe());

        new Thread(new Runnable() {
            @Override
            public void run() {
                final List<NetworkPrinterScanner.Found> found =
                        NetworkPrinterScanner.scan(new NetworkPrinterScanner.Progress() {
                            @Override
                            public void onProgress(int done, int total) {
                                // progress goes to the log; the dialog appears when the sweep finishes
                            }
                        });
                ui.post(new Runnable() {
                    @Override
                    public void run() {
                        showScanResults(found);
                    }
                });
            }
        }, "net-scan").start();
    }

    private void showScanResults(final List<NetworkPrinterScanner.Found> found) {
        if (found == null || found.isEmpty()) {
            new AlertDialog.Builder(this)
                    .setTitle("No printers found")
                    .setMessage("Nothing answered on ports 9100, 515 or 631.\n\n"
                            + "Check that the printer is switched on and joined to the same Wi-Fi network as this device.")
                    .setPositiveButton("OK", null)
                    .show();
            return;
        }

        final String[] labels = new String[found.size()];
        for (int i = 0; i < found.size(); i++) labels[i] = found.get(i).label();

        new AlertDialog.Builder(this)
                .setTitle("Printers found")
                .setItems(labels, new DialogInterface.OnClickListener() {
                    @Override
                    public void onClick(DialogInterface dialog, int which) {
                        NetworkPrinterScanner.Found f = found.get(which);
                        tcpHost.setText(f.host);
                        tcpPort.setText(String.valueOf(f.port));
                        selectTargetMode(Prefs.TARGET_TCP);
                        saveSettings();
                        toast("Selected " + f.host + ":" + f.port);
                        Log.i("Network printer selected: " + f.label());
                    }
                })
                .setNegativeButton("Cancel", null)
                .show();
    }

    /**
     * Reports every printing route this particular device offers, and picks the best one automatically.
     * A phone will usually only have USB, while a Sunmi terminal has its printer wired in.
     */
    private void detectPrinters() {
        try {
            List<PrinterScanner.Finding> found = PrinterScanner.scan(this);
            Log.i(PrinterScanner.summarise(found));

            PrinterScanner.Finding best = null;
            for (PrinterScanner.Finding f : found) {
                // Prefer a real local printer over merely forwarding elsewhere.
                if (f.usable && !Prefs.TARGET_TCP.equals(f.targetMode)) {
                    best = f;
                    break;
                }
            }

            if (best == null) {
                toast("No local printer found. See the log for what was checked.");
                return;
            }

            selectTargetMode(best.targetMode);
            prefs.setTargetMode(best.targetMode);
            if (Prefs.TARGET_USB.equals(best.targetMode) && best.deviceId.length() > 0) {
                prefs.setUsbDeviceName(best.deviceId);
                refreshDevices();
            }
            toast("Found: " + best.label);
        } catch (Exception e) {
            Log.e("Printer scan failed", e);
            toast("Scan failed: " + e.getMessage());
        }
    }

    private void refreshDevices() {
        usbDevices.clear();
        List<String> labels = new ArrayList<>();
        UsbManager manager = (UsbManager) getSystemService(Context.USB_SERVICE);
        if (manager != null) {
            for (UsbDevice d : manager.getDeviceList().values()) {
                usbDevices.add(d);
                String name;
                try {
                    name = d.getProductName();
                } catch (Throwable t) {
                    name = null;
                }
                if (name == null || name.isEmpty()) {
                    name = String.format("%04X:%04X", d.getVendorId(), d.getProductId());
                }
                labels.add(name
                        + (com.usblanbridge.core.UsbPrintTarget.looksPrintable(d) ? "" : "  (not a printer)")
                        + (manager.hasPermission(d) ? "" : "  (no permission)"));
            }
        }
        if (labels.isEmpty()) labels.add("No USB device connected");

        ArrayAdapter<String> adapter = new ArrayAdapter<>(this, android.R.layout.simple_spinner_item, labels);
        adapter.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item);
        deviceSpinner.setAdapter(adapter);

        String wanted = prefs.getUsbDeviceName();
        for (int i = 0; i < usbDevices.size(); i++) {
            if (usbDevices.get(i).getDeviceName().equals(wanted)) {
                deviceSpinner.setSelection(i);
                break;
            }
        }
    }

    private UsbDevice selectedDevice() {
        int i = deviceSpinner.getSelectedItemPosition();
        return (i >= 0 && i < usbDevices.size()) ? usbDevices.get(i) : null;
    }

    private void requestUsbPermission() {
        UsbDevice device = selectedDevice();
        if (device == null) {
            toast("No USB device selected.");
            return;
        }
        UsbManager manager = (UsbManager) getSystemService(Context.USB_SERVICE);
        if (manager == null) return;
        if (manager.hasPermission(device)) {
            toast("Permission already granted.");
            return;
        }
        Intent intent = new Intent(ACTION_USB_PERMISSION).setPackage(getPackageName());
        int flags = 0;
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) flags |= PendingIntent.FLAG_MUTABLE;
        manager.requestPermission(device, PendingIntent.getBroadcast(this, 0, intent, flags));
    }

    private void requestNotificationPermissionIfNeeded() {
        if (Build.VERSION.SDK_INT < 33) return;
        if (checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[]{Manifest.permission.POST_NOTIFICATIONS}, 1);
        }
    }

    // ------------------------------------------------------------------ actions

    private void saveSettings() {
        prefs.setTargetMode(selectedTargetMode());
        prefs.setTcpHost(tcpHost.getText().toString().trim());
        prefs.setTcpPort(parsePort(tcpPort.getText().toString(), 9100));
        prefs.setRawPort(parsePort(rawPort.getText().toString(), 9100));
        prefs.setEposPort(parsePort(eposPort.getText().toString(), 8080));
        prefs.setEposHttpsPort(parsePort(httpsPort.getText().toString(), 8443));
        prefs.setEposDeviceId(devIdField.getText().toString());
        prefs.setStatusReplies(statusReplies.isChecked());
        prefs.setAutoStart(autoStart.isChecked());
        prefs.setNoCut(noCutSwitch.isChecked());
        prefs.setScaleEnabled(scaleSwitch.isChecked());
        prefs.setScaleHost(scaleHost.getText().toString().trim());
        prefs.setScalePort(parsePort(scalePortField.getText().toString(), 4001));
        prefs.setScaleHttpPort(parsePort(scaleHttpField.getText().toString(), 8020));
        prefs.setScaleDisplayUnit(scaleUnit.getText().toString().trim());
        UsbDevice device = selectedDevice();
        if (device != null) prefs.setUsbDeviceName(device.getDeviceName());
    }

    private static int parsePort(String text, int fallback) {
        try {
            int v = Integer.parseInt(text.trim());
            return (v >= 1 && v <= 65535) ? v : fallback;
        } catch (Exception e) {
            return fallback;
        }
    }

    private void start() {
        saveSettings();
        UsbDevice device = selectedDevice();
        UsbManager manager = (UsbManager) getSystemService(Context.USB_SERVICE);
        if (Prefs.TARGET_USB.equals(selectedTargetMode()) && device != null && manager != null && !manager.hasPermission(device)) {
            toast("Grant USB access first.");
            requestUsbPermission();
            return;
        }
        Intent intent = new Intent(this, BridgeService.class).setAction(BridgeService.ACTION_START);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) startForegroundService(intent);
        else startService(intent);
        statusPill.setText("Starting…");
        ui.postDelayed(new Runnable() {
            @Override
            public void run() {
                refreshStatus();
            }
        }, 1200);
    }

    private void stop() {
        Intent intent = new Intent(this, BridgeService.class).setAction(BridgeService.ACTION_STOP);
        startService(intent);
        ui.postDelayed(new Runnable() {
            @Override
            public void run() {
                refreshStatus();
            }
        }, 600);
    }

    /** Sends a receipt through the phone's own raw port, so it exercises the whole path a client would use. */
    private void testPrint() {
        if (!BridgeService.isRunning()) {
            toast("Start sharing first.");
            return;
        }
        final int port = parsePort(rawPort.getText().toString(), 9100);
        toast("Sending a test receipt…");
        new Thread(new Runnable() {
            @Override
            public void run() {
                Socket socket = new Socket();
                try {
                    socket.connect(new InetSocketAddress("127.0.0.1", port), 4000);
                    OutputStream out = socket.getOutputStream();
                    byte[] data = TestReceipt.build("shared printer", NetUtil.getLanAddress() + ":" + port);
                    out.write(data);
                    out.flush();
                    socket.shutdownOutput();
                    Log.i("Test receipt sent through 127.0.0.1:" + port);
                } catch (Exception e) {
                    Log.e("Test print failed", e);
                } finally {
                    try {
                        socket.close();
                    } catch (Exception ignored) {
                    }
                }
            }
        }, "test-print").start();
    }

    private void openPrintSettings() {
        try {
            startActivity(new Intent(Settings.ACTION_PRINT_SETTINGS));
        } catch (Exception e) {
            toast("This Android build has no print settings screen.");
        }
    }

    private void applyLicence() {
        String key = licenceKey.getText().toString().trim();
        if (key.isEmpty()) {
            prefs.setLicenseKey("");
            refreshStatus();
            toast("No licence key. The footer is printed again.");
            return;
        }
        License.Info info = License.inspect(key);
        if (info == null) {
            toast("That key is not valid.");
            return;
        }
        String device = DeviceId.get(this);
        if (!info.allows(device)) {
            toast("This key is for other devices. Send this device's ID (" + DeviceId.pretty(device) + ") to get a key for it.");
            return;
        }
        prefs.setLicenseKey(key);
        refreshStatus();
        toast("Licensed to " + info.licensee + ". The footer is off.");
    }

    private void copyDeviceId() {
        String id = DeviceId.pretty(DeviceId.get(this));
        copyToClipboard("Device ID", id, "Copied: " + id);
    }

    // ------------------------------------------------------------------ status, tickets, log

    /** Runs every two seconds; it only touches a view whose content actually changed, so an idle screen stays idle. */
    private void refreshStatus() {
        setText(printServiceView, PrintServiceStatus.describe(this));
        setText(footerView, Branding.describe(this));
        setText(noCutText, NoCut.describe(this));
        if (noCutSwitch.isChecked() != prefs.isNoCut()) noCutSwitch.setChecked(prefs.isNoCut());

        String ip = NetUtil.getLanAddress();
        int raw = parsePort(rawPort.getText().toString(), 9100);
        int epos = parsePort(eposPort.getText().toString(), 8080);
        int https = parsePort(httpsPort.getText().toString(), 8443);
        String devid = prefs.getEposDeviceId();
        setText(addressView, ip == null ? "No Wi-Fi" : ip + ":" + raw);
        setText(addressHint, ip == null
                ? "Join a Wi-Fi network; POS devices reach the phone over it."
                : "Tap to copy. Raw printing, works from an IP address alone.");
        String host = ip == null ? "<phone address>" : ip;
        setText(eposView, !prefs.isEposEnabled() ? "" : "ePOS  " + EposDeviceId.serviceUrl(false, host, epos, devid).replace("&timeout=10000", ""));
        boolean httpsUp = BridgeService.httpsServer() != null;
        setText(httpsView, !prefs.isEposEnabled() ? "" : "HTTPS " + EposDeviceId.serviceUrl(true, host, https, devid).replace("&timeout=10000", "")
                + (httpsUp || !BridgeService.isRunning() ? "" : "  (not running, see the log)"));
        if (!devIdField.hasFocus() && !devIdField.getText().toString().equals(devid)) devIdField.setText(devid);
        setText(devIdNote, "Answering to \"" + devid + "\"" + (devid.equals(EposDeviceId.DEFAULT) ? " (the default a real Epson uses)." : ". Other ids get DeviceNotFound."));

        // what each folded card says in its heading
        devIdSection.setSummary(devid);
        noCutSection.setSummary(prefs.isNoCut() ? "ON" : "off");
        printerSection.setSummary(ROUTE_LABELS[route] + (route == 3 ? " · " + prefs.getTcpHost() + ":" + prefs.getTcpPort() : ""));
        optionsSection.setSummary(raw + " · " + epos + " · " + https);
        footerSection.setSummary(prefs.getLicenseKey().isEmpty() ? "footer printed" : "licensed");

        RawServer server = BridgeService.rawServer();
        boolean running = BridgeService.isRunning() && server != null;
        String status = BridgeService.status();
        boolean error = !running && status != null && status.startsWith("Error");
        int state = running ? 1 : error ? 2 : 0;
        if (running) {
            long jobs = server.getJobsCompleted();
            int clients = server.getActiveClients();
            setText(statusPill, "● Running · " + jobs + " job" + (jobs == 1 ? "" : "s") + (clients > 0 ? " · " + clients + " connected" : ""));
        } else {
            setText(statusPill, error ? "Error" : "Stopped");
        }
        setText(btnStartStop, running ? "Stop sharing" : error ? "Start sharing (last attempt failed, see the log)" : "Start sharing");
        if (state != shownState) {
            shownState = state;
            statusPill.setBackground(rounded(running ? GREEN_BG : error ? RED_BG : CHIP, 999));
            statusPill.setTextColor(running ? GREEN : error ? RED : MUTED);
            btnStartStop.setBackground(rounded(running ? RED : GREEN, 12));
        }
    }

    private int shownState = -1;

    private static void setText(TextView view, String text) {
        CharSequence current = view.getText();
        if (current == null ? text != null : !current.toString().equals(text)) view.setText(text);
    }

    private void refreshTickets() {
        ticketsBox.removeAllViews();
        List<PrintHistory.Record> all = PrintHistory.snapshot();
        ticketsEmpty.setVisibility(all.isEmpty() ? View.VISIBLE : View.GONE);
        ticketsSection.setSummary(all.isEmpty() ? "none yet" : all.size() + (all.size() == 1 ? " ticket" : " tickets") + " · last " + all.get(all.size() - 1).timeText().substring(11));
        int shown = 0;
        for (int i = all.size() - 1; i >= 0 && shown < MAX_TICKETS; i--, shown++) {
            final PrintHistory.Record r = all.get(i);
            LinearLayout row = new LinearLayout(this);
            row.setOrientation(LinearLayout.VERTICAL);
            row.setPadding(0, dp(8), 0, dp(8));
            row.setClickable(true);
            row.setBackground(rounded(i % 2 == 0 ? 0xFFF9FAFB : CARD, 8));

            String clock = r.timeText().substring(11);
            TextView head = text(clock + "   " + r.source + "   " + RawServer.formatBytes(r.bytes) + "   " + r.path, 13, TEXT, true);
            row.addView(head);

            String first = firstLine(r.ticket == null || r.ticket.isEmpty() ? r.preview : r.ticket);
            TextView body = text((r.failed() ? r.status : "Printed") + (first.isEmpty() ? "" : "  ·  " + first), 12, r.failed() ? RED : MUTED, false);
            row.addView(body);

            row.setOnClickListener(new View.OnClickListener() {
                @Override
                public void onClick(View v) {
                    showTicket(r);
                }
            });
            ticketsBox.addView(row);
            View line = new View(this);
            line.setBackgroundColor(LINE);
            line.setLayoutParams(new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(1)));
            ticketsBox.addView(line);
        }
    }

    private static String firstLine(String s) {
        if (s == null) return "";
        for (String l : s.split("\n")) {
            String t = l.trim();
            if (!t.isEmpty()) return t.length() > 60 ? t.substring(0, 60) + "…" : t;
        }
        return "";
    }

    /** The ticket as it went to the printer, in a monospace dialog that can be copied. */
    private void showTicket(final PrintHistory.Record r) {
        final String ticket = (r.ticket == null || r.ticket.isEmpty())
                ? (r.preview == null || r.preview.isEmpty() ? "(nothing printable: a status exchange, a drawer pulse or an image)" : r.preview)
                : r.ticket;
        TextView t = new TextView(this);
        t.setTypeface(Typeface.MONOSPACE);
        t.setTextSize(12);
        t.setTextColor(TEXT);
        t.setText(ticket);
        t.setPadding(dp(16), dp(12), dp(16), dp(12));
        t.setTextIsSelectable(true);
        HorizontalScrollView h = new HorizontalScrollView(this);
        h.addView(t);
        ScrollView s = new ScrollView(this);
        s.addView(h);
        new AlertDialog.Builder(this)
                .setTitle("Ticket  " + r.timeText().substring(11) + "  ·  " + r.source + "  ·  " + r.status)
                .setView(s)
                .setPositiveButton("Close", null)
                .setNeutralButton("Copy", new DialogInterface.OnClickListener() {
                    @Override
                    public void onClick(DialogInterface dialog, int which) {
                        copyToClipboard("Ticket", ticket, "Ticket copied");
                    }
                })
                .show();
    }

    private void appendLog(String line) {
        if (line == null || line.isEmpty()) return;
        logView.append(line.endsWith("\n") ? line : line + "\n");
        logScroll.post(new Runnable() {
            @Override
            public void run() {
                logScroll.fullScroll(View.FOCUS_DOWN);
            }
        });
    }

    private void toast(String message) {
        Toast.makeText(this, message, Toast.LENGTH_SHORT).show();
    }
}
