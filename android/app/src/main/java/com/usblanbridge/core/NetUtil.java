package com.usblanbridge.core;

import java.net.Inet4Address;
import java.net.InetAddress;
import java.net.NetworkInterface;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;

/** Finds the address other devices on the LAN should point their POS software at. */
public final class NetUtil {

    private NetUtil() {
    }

    /** Best guess at this phone's LAN address: Wi-Fi first, then any other non-loopback IPv4. */
    public static String getLanAddress() {
        List<String> all = getLanAddresses();
        return all.isEmpty() ? null : all.get(0);
    }

    public static List<String> getLanAddresses() {
        List<String> wifi = new ArrayList<>();
        List<String> other = new ArrayList<>();
        try {
            for (NetworkInterface nic : Collections.list(NetworkInterface.getNetworkInterfaces())) {
                if (!nic.isUp() || nic.isLoopback()) continue;
                String name = nic.getName() == null ? "" : nic.getName().toLowerCase();
                for (InetAddress addr : Collections.list(nic.getInetAddresses())) {
                    if (!(addr instanceof Inet4Address) || addr.isLoopbackAddress()) continue;
                    String text = addr.getHostAddress();
                    if (text == null) continue;
                    if (name.startsWith("wlan") || name.startsWith("ap")) wifi.add(text);
                    else other.add(text);
                }
            }
        } catch (Exception e) {
            Log.e("Could not read network interfaces", e);
        }
        wifi.addAll(other);
        return wifi;
    }
}
