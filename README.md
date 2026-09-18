# USB LAN Printer Bridge for Android

Turns a phone, tablet or POS terminal into a **Wi-Fi print server**. A receipt printer attached to the device
becomes a network printer that tills, POS apps and PCs on the same Wi-Fi print to by IP address, with no PC in
the loop. Old phones make good dedicated print servers, which is the point.

* **One APK for every device.** It contains no native code, so the same file runs on arm64-v8a, armeabi-v7a, x86
  and x86_64 with no ABI splits.
* **Android 5.0 and newer** (`minSdk` 21). Everything above that is called behind an explicit version check.
* **No dependencies.** Plain Android framework, no AndroidX and no vendor SDKs, so it stays under 1 MB.
* **Four printer routes**: USB over OTG, a Sunmi-style built-in printer, Bluetooth, or relaying to a printer that
  is already on the network.
* **Raw / JetDirect on port 9100** and **Epson ePOS-Print emulation over HTTP**, so apps written against the Epson
  ePOS SDK can print to any generic ESC/POS printer.
* Runs as a foreground service holding a Wi-Fi lock and a wake lock, so printing keeps working with the screen off.
  It can also start itself after a reboot.

This is the Android half of [escpos-printer-bridge](https://github.com/azzaroES/escpos-printer-bridge).
The Windows app, which shares a printer installed on a PC, lives on the
[`windows` branch](https://github.com/azzaroES/escpos-printer-bridge/tree/windows).

## Download

Get the APK from the [Releases page](https://github.com/azzaroES/escpos-printer-bridge/releases) (tags starting
`android-`). Sideloading it needs "Install unknown apps" allowed for your browser or file manager.

## What clients connect to

| | Port | Works from an IP alone |
|---|---|---|
| Raw / JetDirect | 9100 | yes |
| ePOS-Print | 8080 | no, the port must be in the URL |

**That port difference is a platform limit, not a choice.** An unrooted Android app may not bind ports below 1024,
so the phone cannot answer on 80 the way the Windows build does. Raw 9100 is unaffected, which is why it stays the
path that works when a POS app only asks for an IP address.

The ePOS endpoint is `http://<phone address>:8080/cgi-bin/epos/service.cgi` with device id `local_printer`.

## Choosing the printer, and POS terminals with a built-in one

The app offers four printing routes in a dropdown, and a **Detect printers** button that reports what the
device in your hand actually has:

| Route | Used on |
|---|---|
| USB printer over OTG | a phone or tablet with a printer on a cable |
| Built-in thermal printer | Sunmi and compatible POS terminals |
| Bluetooth printer | the many cheap ESC/POS printers that are Bluetooth only |
| Forward to a network printer | anything, to relay to a printer already on the LAN |

**Bluetooth** uses the serial port profile, which every ESC/POS Bluetooth printer exposes. Pair the printer in
Android's own Bluetooth settings first, then use **Choose Bluetooth printer** in the app. Only paired devices are
offered, which is deliberate: running a discovery scan would drag in location permissions for no benefit. Two
allowances are made for cheap hardware, namely a fallback to RFCOMM channel 1 when the standard connect is
refused, and sending in small chunks with a pause so a tiny receive buffer is not overrun.

On a **Sunmi terminal** the printer is not a USB device. It is wired to the board and reached through the
vendor's own service, which exposes `sendRAWData` for raw ESC/POS. That is exactly what the bridge already
produces, so bytes pass through untranslated and the terminal becomes a network printer for the whole shop.

One caution that is designed around rather than ignored. AIDL assigns binder transaction ids by declaration
order, and a mismatch against a terminal's firmware binds successfully and then silently prints nothing. This is
a well-known trap with hand-copied versions of that interface. Two things guard against it:

* Only the first twelve methods are declared, up to and including `sendRAWData`. Transaction ids are positional,
  so a prefix keeps every id actually used correct without guessing at the rest of the interface.
* Before printing, the app calls an identity method and checks the reply is a sane string. If it is not, the
  Sunmi route is refused with an explanation rather than emitting garbage.

## Finding a printer that is already on the network

"Forward to a network printer" needs an address, and typing one by hand only helps if you already know it.
**Scan network for printers** sweeps the device's own subnet and lists what answers. Tap a result to fill in the
address and port.

| Port | Meaning |
|---|---|
| 9100 | RAW / JetDirect, what this bridge speaks |
| 515 | LPR / LPD |
| 631 | IPP |

Anything answering on 9100 is then asked to prove itself with `GS I 67`, the ESC/POS request for a printer's
model. A real receipt printer replies with a header byte, its model name and a NUL, so the list shows the model
instead of a bare address. Subnets wider than 1022 addresses are truncated, because sweeping a /16 is not
practical on a phone.

## Printing from Android's own print dialog

Android's built-in print service (`com.android.bips`) only speaks IPP, so it will never find a raw ESC/POS
printer; the dialog just spins. Use a POS app that prints to an IP and port, or install an ESC/POS print
service such as RawBT and point it at the bridge's address on port 9100.

## Logs

Some ROMs hide app output from `logcat`, so the app writes its own log to
`Android/data/com.usblanbridge/files/bridge.log` on the device's storage, alongside the print history.

## What has and has not been tested on hardware

| Path | Status |
|---|---|
| App, foreground service, listening on 9100 and 8080 | verified on a real phone |
| ESC/POS responder | 24 checks against bytes captured from a real Epson TM-T20II |
| Network scan | logic verified against a live LAN, where it identified a TM-T20II from its `GS I 67` reply |
| Forwarding to a network printer | **not yet confirmed end to end from a phone** |
| Sunmi built-in printer | written to the published interface and guarded, **not yet run on a Sunmi terminal** |
| Bluetooth printers | **not yet run against a Bluetooth printer** |
| USB printer over OTG | **not yet run with a USB printer attached** |

If you try one of the untested paths, press **Detect printers** first; it says whether the route was found and
validated. Reports are welcome.

## Building

Requirements:

* JDK 17 or newer. Android Studio's bundled `jbr` works.
* The Android SDK with platform 36. Point the build at it with `ANDROID_HOME`, or create `local.properties`
  containing `sdk.dir=<path to the SDK>`.

The Gradle wrapper downloads the right Gradle version itself, checksum-verified.

```bash
./gradlew assembleDebug
```

On Windows use `gradlew.bat assembleDebug`. Install it on a connected device with:

```bash
adb install -r app/build/outputs/apk/debug/app-debug.apk
```

### Self-test without a device

The protocol core, `EscPosResponder`, is deliberately free of Android imports so it can be verified on a plain JVM:

```
tools\run-responder-test.cmd
```

That runs 24 checks against the values captured from a real Epson TM-T20II, including the `GS ( H` process-id echo
at every possible TCP split point.

## License

MIT, see [LICENSE](LICENSE). Epson, ePOS and TM-T20 are trademarks of Seiko Epson Corporation, and Sunmi is a
trademark of its owner. This project is not affiliated with either; it implements the published protocols and
contains no vendor SDK code.
