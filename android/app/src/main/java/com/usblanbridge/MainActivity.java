package com.usblanbridge;

import android.Manifest;
import android.app.Activity;
import android.app.AlertDialog;
import android.app.PendingIntent;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.DialogInterface;
import android.content.Intent;
import android.content.IntentFilter;
import android.content.pm.PackageManager;
import android.graphics.Color;
import android.graphics.Typeface;
import android.hardware.usb.UsbDevice;
import android.hardware.usb.UsbManager;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.text.InputType;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.widget.ArrayAdapter;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.Spinner;
import android.widget.TextView;
import android.widget.Toast;

import com.usblanbridge.core.BluetoothPrintTarget;
import com.usblanbridge.core.Log;
import com.usblanbridge.core.NetUtil;
import com.usblanbridge.core.NetworkPrinterScanner;
import com.usblanbridge.core.PrinterScanner;
import com.usblanbridge.core.RawServer;
import com.usblanbridge.core.TestReceipt;

import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.util.ArrayList;
import java.util.List;

/**
 * Single-screen control panel: pick the printer, start sharing, see the address to type into POS software.
 * The layout is built in code so the app carries no layout XML and no AndroidX dependency.
 */
public final class MainActivity extends Activity {

    private static final String ACTION_USB_PERMISSION = "com.usblanbridge.USB_PERMISSION";

    private Prefs prefs;
    private final Handler ui = new Handler(Looper.getMainLooper());
    private final List<UsbDevice> usbDevices = new ArrayList<>();

    private TextView addressView;
    private TextView statusView;
    /** Printing routes offered in the dropdown, kept in step with TARGET_LABELS. */
    private static final String[] TARGET_MODES = {
            Prefs.TARGET_USB, Prefs.TARGET_SUNMI, Prefs.TARGET_BLUETOOTH, Prefs.TARGET_TCP
    };
    private static final String[] TARGET_LABELS = {
            "USB printer over OTG",
            "Built-in thermal printer, Sunmi and similar",
            "Bluetooth printer, paired",
            "Forward to a network printer"
    };

    private Spinner deviceSpinner;
    private Spinner targetSpinner;
    private EditText tcpHost;
    private EditText tcpPort;
    private EditText rawPort;
    private EditText eposPort;
    private CheckBox statusReplies;
    private CheckBox autoStart;
    private TextView logView;
    private ScrollView logScroll;

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
        setContentView(buildUi());
        Log.i("Log file: " + Log.getLogFile());

        IntentFilter filter = new IntentFilter(ACTION_USB_PERMISSION);
        filter.addAction(UsbManager.ACTION_USB_DEVICE_ATTACHED);
        filter.addAction(UsbManager.ACTION_USB_DEVICE_DETACHED);
        if (Build.VERSION.SDK_INT >= 34) {
            registerReceiver(usbReceiver, filter, Context.RECEIVER_NOT_EXPORTED);
        } else {
            registerReceiver(usbReceiver, filter);
        }

        Log.addListener(logListener);
        appendLog(Log.snapshot());
        requestNotificationPermissionIfNeeded();
        refreshDevices();
        refreshStatus();
    }

    @Override
    protected void onResume() {
        super.onResume();
        refreshDevices();
        refreshStatus();
    }

    @Override
    protected void onDestroy() {
        Log.removeListener(logListener);
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
        int pad = dp(16);
        root.setPadding(pad, pad, pad, pad);

        TextView title = new TextView(this);
        title.setText("USB LAN Printer Bridge");
        title.setTextSize(20);
        title.setTypeface(Typeface.DEFAULT_BOLD);
        root.addView(title);

        addressView = new TextView(this);
        addressView.setTextSize(16);
        addressView.setPadding(0, dp(8), 0, dp(4));
        root.addView(addressView);

        statusView = new TextView(this);
        statusView.setPadding(0, 0, 0, dp(12));
        root.addView(statusView);

        root.addView(label("Printer"));
        targetSpinner = new Spinner(this);
        ArrayAdapter<String> targetAdapter =
                new ArrayAdapter<>(this, android.R.layout.simple_spinner_item, TARGET_LABELS);
        targetAdapter.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item);
        targetSpinner.setAdapter(targetAdapter);
        root.addView(targetSpinner);

        LinearLayout detectRow = new LinearLayout(this);
        detectRow.setOrientation(LinearLayout.HORIZONTAL);
        detectRow.addView(button("Detect printers", new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                detectPrinters();
            }
        }));
        root.addView(detectRow);
        selectTargetMode(prefs.getTargetMode());

        root.addView(label("USB printer"));
        deviceSpinner = new Spinner(this);
        root.addView(deviceSpinner);

        LinearLayout usbButtons = new LinearLayout(this);
        usbButtons.setOrientation(LinearLayout.HORIZONTAL);
        usbButtons.addView(button("Refresh", new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                refreshDevices();
            }
        }));
        usbButtons.addView(button("Grant USB access", new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                requestUsbPermission();
            }
        }));
        root.addView(usbButtons);

        LinearLayout tcpRow = new LinearLayout(this);
        tcpRow.setOrientation(LinearLayout.HORIZONTAL);
        tcpHost = edit(prefs.getTcpHost(), InputType.TYPE_CLASS_TEXT, 3f);
        tcpPort = edit(String.valueOf(prefs.getTcpPort()), InputType.TYPE_CLASS_NUMBER, 1f);
        tcpRow.addView(tcpHost);
        tcpRow.addView(tcpPort);
        root.addView(tcpRow);

        LinearLayout scanRow = new LinearLayout(this);
        scanRow.setOrientation(LinearLayout.HORIZONTAL);
        scanRow.addView(button("Scan network for printers", new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                scanNetwork();
            }
        }));
        root.addView(scanRow);

        LinearLayout btRow = new LinearLayout(this);
        btRow.setOrientation(LinearLayout.HORIZONTAL);
        btRow.addView(button("Choose Bluetooth printer", new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                chooseBluetoothPrinter();
            }
        }));
        root.addView(btRow);

        root.addView(label("Ports"));
        LinearLayout portRow = new LinearLayout(this);
        portRow.setOrientation(LinearLayout.HORIZONTAL);
        rawPort = edit(String.valueOf(prefs.getRawPort()), InputType.TYPE_CLASS_NUMBER, 1f);
        eposPort = edit(String.valueOf(prefs.getEposPort()), InputType.TYPE_CLASS_NUMBER, 1f);
        portRow.addView(labelled("Raw", rawPort));
        portRow.addView(labelled("ePOS", eposPort));
        root.addView(portRow);

        statusReplies = new CheckBox(this);
        statusReplies.setText("Answer printer status queries");
        statusReplies.setChecked(prefs.isStatusReplies());
        root.addView(statusReplies);

        autoStart = new CheckBox(this);
        autoStart.setText("Start automatically after reboot");
        autoStart.setChecked(prefs.isAutoStart());
        root.addView(autoStart);

        LinearLayout actions = new LinearLayout(this);
        actions.setOrientation(LinearLayout.HORIZONTAL);
        actions.setPadding(0, dp(12), 0, dp(8));
        actions.addView(button("Start", new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                start();
            }
        }));
        actions.addView(button("Stop", new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                stop();
            }
        }));
        actions.addView(button("Test print", new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                testPrint();
            }
        }));
        root.addView(actions);

        logView = new TextView(this);
        logView.setTypeface(Typeface.MONOSPACE);
        logView.setTextSize(11);
        logView.setBackgroundColor(Color.parseColor("#F5F5F5"));
        logView.setPadding(dp(6), dp(6), dp(6), dp(6));
        logScroll = new ScrollView(this);
        logScroll.addView(logView);
        LinearLayout.LayoutParams logParams = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f);
        root.addView(logScroll, logParams);

        ScrollView outer = new ScrollView(this);
        outer.addView(root, new ViewGroup.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        return outer;
    }

    private TextView label(String text) {
        TextView t = new TextView(this);
        t.setText(text);
        t.setTypeface(Typeface.DEFAULT_BOLD);
        t.setPadding(0, dp(10), 0, dp(2));
        return t;
    }

    private View labelled(String text, View field) {
        LinearLayout box = new LinearLayout(this);
        box.setOrientation(LinearLayout.VERTICAL);
        TextView t = new TextView(this);
        t.setText(text);
        box.addView(t);
        box.addView(field);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f);
        box.setLayoutParams(lp);
        return box;
    }

    private EditText edit(String value, int inputType, float weight) {
        EditText e = new EditText(this);
        e.setText(value);
        e.setInputType(inputType);
        e.setSingleLine(true);
        e.setLayoutParams(new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, weight));
        return e;
    }

    private Button button(String text, View.OnClickListener listener) {
        Button b = new Button(this);
        b.setText(text);
        b.setOnClickListener(listener);
        b.setLayoutParams(new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));
        return b;
    }

    private int dp(int value) {
        return Math.round(getResources().getDisplayMetrics().density * value);
    }

    // ------------------------------------------------------------------ devices and permissions

    /**
     * Sweeps the local subnet for printers, because typing an address by hand only helps if you already
     * know it. Runs off the UI thread; the sweep takes a few seconds.
     */
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

    private String selectedTargetMode() {
        int i = (targetSpinner == null) ? -1 : targetSpinner.getSelectedItemPosition();
        return (i >= 0 && i < TARGET_MODES.length) ? TARGET_MODES[i] : Prefs.TARGET_USB;
    }

    private void selectTargetMode(String mode) {
        for (int i = 0; i < TARGET_MODES.length; i++) {
            if (TARGET_MODES[i].equals(mode)) {
                targetSpinner.setSelection(i);
                return;
            }
        }
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
        prefs.setStatusReplies(statusReplies.isChecked());
        prefs.setAutoStart(autoStart.isChecked());
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
            toast("Start the bridge first.");
            return;
        }
        final int port = parsePort(rawPort.getText().toString(), 9100);
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

    private void refreshStatus() {
        String ip = NetUtil.getLanAddress();
        int raw = parsePort(rawPort.getText().toString(), 9100);
        addressView.setText(ip == null
                ? "No Wi-Fi connection"
                : "Add this printer as  " + ip + "  port " + raw);

        RawServer server = BridgeService.rawServer();
        if (BridgeService.isRunning() && server != null) {
            statusView.setText("Running. " + server.getJobsCompleted() + " job(s), "
                    + RawServer.formatBytes(server.getBytesReceived()) + ", "
                    + server.getActiveClients() + " connected.");
            statusView.setTextColor(Color.parseColor("#1B7F1B"));
        } else {
            statusView.setText(BridgeService.status());
            statusView.setTextColor(Color.GRAY);
        }
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
