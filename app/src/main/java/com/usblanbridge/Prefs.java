package com.usblanbridge;

import android.content.Context;
import android.content.SharedPreferences;

/** Persisted settings. Deliberately small and typed, so the service and the activity agree on defaults. */
public final class Prefs {

    public static final String TARGET_USB = "usb";
    public static final String TARGET_TCP = "tcp";
    /** Built-in thermal printer on a Sunmi or compatible POS terminal, reached through the vendor service. */
    public static final String TARGET_SUNMI = "sunmi";
    /** A paired Bluetooth ESC/POS printer, over the serial port profile. */
    public static final String TARGET_BLUETOOTH = "bt";

    private static final String FILE = "bridge";
    private final SharedPreferences p;

    public Prefs(Context context) {
        this.p = context.getApplicationContext().getSharedPreferences(FILE, Context.MODE_PRIVATE);
    }

    public int getRawPort() {
        return p.getInt("rawPort", 9100);
    }

    public void setRawPort(int v) {
        p.edit().putInt("rawPort", v).apply();
    }

    /** ePOS-Print port. Cannot be 80: an unrooted Android app may not bind below 1024. */
    public int getEposPort() {
        return p.getInt("eposPort", 8080);
    }

    public void setEposPort(int v) {
        p.edit().putInt("eposPort", v).apply();
    }

    public boolean isEposEnabled() {
        return p.getBoolean("epos", true);
    }

    public void setEposEnabled(boolean v) {
        p.edit().putBoolean("epos", v).apply();
    }

    public boolean isStatusReplies() {
        return p.getBoolean("statusReplies", true);
    }

    public void setStatusReplies(boolean v) {
        p.edit().putBoolean("statusReplies", v).apply();
    }

    public String getModelName() {
        return p.getString("modelName", "TM-T20II");
    }

    public void setModelName(String v) {
        p.edit().putString("modelName", v).apply();
    }

    public int getIdleTimeoutMs() {
        return p.getInt("idleMs", 1500);
    }

    public void setIdleTimeoutMs(int v) {
        p.edit().putInt("idleMs", v).apply();
    }

    public String getTargetMode() {
        return p.getString("target", TARGET_USB);
    }

    public void setTargetMode(String v) {
        p.edit().putString("target", v).apply();
    }

    /** UsbDevice.getDeviceName() of the chosen printer, e.g. /dev/bus/usb/001/002. */
    public String getUsbDeviceName() {
        return p.getString("usbDevice", "");
    }

    public void setUsbDeviceName(String v) {
        p.edit().putString("usbDevice", v).apply();
    }

    /** MAC address of the chosen paired Bluetooth printer. */
    public String getBluetoothAddress() {
        return p.getString("btAddress", "");
    }

    public void setBluetoothAddress(String v) {
        p.edit().putString("btAddress", v).apply();
    }

    public String getTcpHost() {
        return p.getString("tcpHost", "192.168.1.180");
    }

    public void setTcpHost(String v) {
        p.edit().putString("tcpHost", v).apply();
    }

    public int getTcpPort() {
        return p.getInt("tcpPort", 9100);
    }

    /** True once the user has saved a network printer address, as opposed to the built-in default. */
    public boolean hasTcpHost() {
        return p.contains("tcpHost");
    }

    /** Licence key that removes the printed footer; empty when none has been entered. */
    public String getLicenseKey() {
        return p.getString("licenseKey", "");
    }

    public void setLicenseKey(String v) {
        p.edit().putString("licenseKey", v == null ? "" : v.trim()).apply();
    }

    /** The device id the ePOS endpoint answers to (the devid in the URL, or createDevice in the SDK). */
    public String getEposDeviceId() {
        return com.usblanbridge.core.EposDeviceId.sanitize(p.getString("eposDeviceId", com.usblanbridge.core.EposDeviceId.DEFAULT));
    }

    public void setEposDeviceId(String v) {
        p.edit().putString("eposDeviceId", com.usblanbridge.core.EposDeviceId.sanitize(v)).apply();
    }

    /** HTTPS ePOS port. Cannot be 443 on an unrooted phone. */
    public int getEposHttpsPort() {
        return p.getInt("eposHttpsPort", 8443);
    }

    public void setEposHttpsPort(int v) {
        p.edit().putInt("eposHttpsPort", v).apply();
    }

    /** When the cool-down (throttle) ends, as a wall-clock time in ms; 0 when off. */
    public long getThrottleUntil() {
        return p.getLong("throttleUntil", 0);
    }

    public void setThrottleUntil(long v) {
        p.edit().putLong("throttleUntil", v).apply();
    }

    /** The last cool-down length the user chose, in minutes. */
    public int getThrottleMinutes() {
        return p.getInt("throttleMinutes", 15);
    }

    public void setThrottleMinutes(int v) {
        p.edit().putInt("throttleMinutes", v).apply();
    }

    /** Whether the Device card's telemetry is written to daily CSV files. */
    public boolean isTelemetryLogged() {
        return p.getBoolean("telemetry", true);
    }

    public void setTelemetryLogged(boolean v) {
        p.edit().putBoolean("telemetry", v).apply();
    }

    /** Emergency switch: remove every cutter command from every ticket. Read on every write, so it applies at once. */
    public boolean isNoCut() {
        return p.getBoolean("noCut", false);
    }

    public void setNoCut(boolean v) {
        p.edit().putBoolean("noCut", v).apply();
    }

    /** Lines fed in place of each removed cut, so the paper reaches the tear bar. */
    public int getNoCutFeedLines() {
        int v = p.getInt("noCutFeed", 4);
        return v < 0 ? 0 : v > 30 ? 30 : v;
    }

    public void setNoCutFeedLines(int v) {
        p.edit().putInt("noCutFeed", v).apply();
    }

    /** Whether a section of the screen is folded up. */
    public boolean isSectionCollapsed(String key, boolean def) {
        return p.getBoolean("collapsed." + key, def);
    }

    public void setSectionCollapsed(String key, boolean v) {
        p.edit().putBoolean("collapsed." + key, v).apply();
    }

    /** The order the user dragged a group of sections into, as comma-separated keys; null for the default. */
    public String getSectionOrder(String group) {
        return p.getString("order." + group, null);
    }

    public void setSectionOrder(String group, String csv) {
        p.edit().putString("order." + group, csv).apply();
    }

    /** Random device id used only on ROMs that provide no ANDROID_ID. */
    public String getFallbackDeviceId() {
        return p.getString("deviceId", "");
    }

    public void setFallbackDeviceId(String v) {
        p.edit().putString("deviceId", v).apply();
    }

    public void setTcpPort(int v) {
        p.edit().putInt("tcpPort", v).apply();
    }

    public boolean isAutoStart() {
        return p.getBoolean("autoStart", false);
    }

    public void setAutoStart(boolean v) {
        p.edit().putBoolean("autoStart", v).apply();
    }
}
