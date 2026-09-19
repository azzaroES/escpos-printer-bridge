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
* **An Android print service**: the phone's own Print menu, in every app, lists the printers this phone can reach,
  and pages print as they look on screen.
* **NO CUT emergency switch**: every cutter command is removed from every ticket and the paper is fed to the tear
  bar instead. For a jammed or broken cutter; applies at once.
* **The last tickets, as they went to the printer**, line by line, on the main screen.
* Runs as a foreground service holding a Wi-Fi lock and a wake lock, so printing keeps working with the screen off.
  It can also start itself after a reboot, and announces itself on the Wi-Fi so other phones find it.

This is the Android half of [escpos-printer-bridge](https://github.com/azzaroES/escpos-printer-bridge).
The Windows app, which shares a printer installed on a PC, lives on the
[`windows` branch](https://github.com/azzaroES/escpos-printer-bridge/tree/windows).

## Download

Get the APK from the [Releases page](https://github.com/azzaroES/escpos-printer-bridge/releases) (tags starting
`android-`). Sideloading it needs "Install unknown apps" allowed for your browser or file manager.

## The screen

One screen, built from cards. The blue header shows the address to type into POS software in large type (tap
to copy), the ePOS URL under it, and a live status pill: running, with the job and client counts, or stopped.
Below it one big **Start sharing / Stop sharing** button and **Test print**. Then:

* **Printer**: four chips, USB, Built-in, Bluetooth, Network. Only the chosen route's controls are shown: the USB
  device list with Refresh and Grant access, the Bluetooth chooser, or the network address with the subnet scan.
  **Detect printers on this device** picks the best route automatically.
* **NO CUT · emergency**: a red card with a switch, described below.
* **Last tickets**: the last eight jobs with time, sender, size and result. Tap one to see the ticket as it went
  to the printer, line by line, and copy it.
* **Options** (ports, status replies, start after reboot), **Print from any app**, **Ticket footer & licence**,
  and the **Log**, which can be hidden or copied.

## What clients connect to

| | Port | Works from an IP alone |
|---|---|---|
| Raw / JetDirect | 9100 | yes |
| ePOS-Print | 8080 | no, the port must be in the URL |

**That port difference is a platform limit, not a choice.** An unrooted Android app may not bind ports below 1024,
so the phone cannot answer on 80 the way the Windows build does. Raw 9100 is unaffected, which is why it stays the
path that works when a POS app only asks for an IP address.

The ePOS endpoint is `http://<phone address>:8080/cgi-bin/epos/service.cgi` with device id `local_printer`.

## The phone's own Print menu, with no other app

The app registers itself with Android as a **print service**, the same mechanism the built-in print service
uses. Once it is on, the Print menu of every app on the phone, Chrome, Gmail, Photos, a POS app's "print via
Android" option, lists the printers this phone can reach: the USB printer on the OTG cable, a paired Bluetooth
printer, a Sunmi terminal's built-in printer, the network printer you configured, and any other phone running
this app on the same Wi-Fi. Pick one and print. Nothing else needs installing.

On Android 7 and newer a freshly installed print service is on automatically. On Android 5 and 6 open
Settings → Printing and switch on "USB LAN Printer Bridge" once; the app has a button that opens that screen
and a line that says whether the service is currently on.

**Pages print as they look on screen.** Android renders the page to a PDF with the app's own layout, and the
bridge rasterises that PDF at 203 dpi and sends it as an ESC/POS image. A web receipt keeps its CSS, fonts,
bold, tables and images; nothing is reduced to plain text. Two paper sizes are offered, 80 mm (default) and
58 mm; blank space at the top and bottom of each page is trimmed so a short receipt does not feed a long strip.

## NO CUT: stop the cutter at once

Switch on **NO CUT** and every cutter command is removed from every ticket, on every route (raw 9100, ePOS, the
phone's Print menu, the test receipt), whatever the sending app asked for: `GS V` in all its forms and the older
`ESC i` and `ESC m`. Each removed cut is replaced by a four-line feed so the paper reaches the tear bar; two cuts
in a row give one feed. The switch is read on every write, so it applies to the next bytes printed with no
restart, and it is saved, so it survives a reboot. Image and QR payloads are parsed, not pattern-matched, so a
logo whose pixel bytes spell a cut is left alone. Each removed cut is named in the log.

The bridge only controls the bytes it forwards. A cut the printer decides on itself, from a feed button or a
DIP switch, is not something the phone can prevent.

## Ticket footer and licence key

Every ticket printed through the app, whether it arrived on port 9100, through ePOS, from the phone's Print menu
or as a test print, ends with a centred line reading **digitalstudio.PRO**, placed just before the cut (or
before the feed that replaces it under NO CUT). Tickets with nothing printable, such as a bare cash-drawer
pulse, are left alone.

A licence key removes the footer (20 EUR, up to two devices). The app shows a **Device ID** under "Ticket
footer & licence" with a Copy button; the customer sends it, and receives a key that names that device (and
optionally a second one). Paste the key into the app and tap Apply; it takes effect on the next ticket, no
restart needed. A key is signed with a private key that is not in this repository and checked offline against
the embedded public key, so a till with no internet can verify it, and it does nothing on a device it does not name.

The binding uses Android's device id rather than a MAC address because Android hides MAC addresses from apps
(every app sees `02:00:00:00:00:00` from Android 6 for Wi-Fi and Android 10 for Bluetooth). The id is tied to
the app's signing key from Android 8 onward, so keep signing releases with the same keystore once keys are out.

Being open source, anyone can also rebuild the app without the footer; the key is for people who use the
published APK and would rather pay than compile.

To issue keys for your own build, run `tools\run-keygen.cmd keypair <folder>` once, paste the printed public
key into `License.java`, and then `run-keygen.cmd sign <folder>\private.key "<customer>" <device id>
[second device id]` per customer.

## Sharing between phones

While the bridge is running, the phone announces its raw port on the Wi-Fi with DNS-SD as
`_pdl-datastream._tcp`, the standard name for port 9100 printing. A second phone running this app lists it in
its own Print menu without typing an address, and CUPS on Linux or macOS discovers it as a socket printer.

## Choosing the printer, and POS terminals with a built-in one

The app offers four printing routes as chips, and a **Detect printers** button that reports what the device in
your hand actually has:

| Route | Used on |
|---|---|
| USB printer over OTG | a phone or tablet with a printer on a cable |
| Built-in thermal printer | Sunmi and compatible POS terminals |
| Bluetooth printer | the many cheap ESC/POS printers that are Bluetooth only |
| Forward to a network printer | anything, to relay to a printer already on the LAN |

**Bluetooth** uses the serial port profile, which every ESC/POS Bluetooth printer exposes. Pair the printer in
Android's own Bluetooth settings first, then use **Choose paired printer** in the app. Only paired devices are
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

## Logs

Some ROMs hide app output from `logcat`, so the app writes its own log to
`Android/data/com.usblanbridge/files/bridge.log` on the device's storage, alongside a daily print-history CSV.
The **Last tickets** card on the main screen shows the recent jobs and, on tap, each ticket as it went to the
printer.

## What has and has not been tested on hardware

| Path | Status |
|---|---|
| App, foreground service, listening on 9100 and 8080 | verified on a real phone (Android 10) |
| ESC/POS responder | 24 checks against bytes captured from a real Epson TM-T20II |
| Raster encoder, ticket footer, NO CUT filter, ticket renderer, licence check | 53 desktop checks |
| Network scan | logic verified against a live LAN, where it identified a TM-T20II from its `GS I 67` reply |
| Forwarding to a network printer | **not yet confirmed end to end from a phone** |
| Print service (the phone's Print menu) | built and manifest-verified, **not yet exercised on a phone** |
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

The protocol core is deliberately free of Android imports so it can be verified on a plain JVM:

```text
tools\run-responder-test.cmd
```

That runs 24 checks of `EscPosResponder` against the values captured from a real Epson TM-T20II, including the
`GS ( H` process-id echo at every possible TCP split point, followed by 53 checks of the raster encoder behind
the Print menu, the footer injector (including cut bytes hidden inside image and QR payloads, and every possible
split point), the NO CUT filter (every cut form, payload bytes that spell a cut, every split point, the switch
flipped mid-job, the footer and the filter stacked), the ticket renderer and the licence check (forged and
mismatched keys are rejected).

## License

MIT, see [LICENSE](LICENSE). Epson, ePOS and TM-T20 are trademarks of Seiko Epson Corporation, and Sunmi is a
trademark of its owner. This project is not affiliated with either; it implements the published protocols and
contains no vendor SDK code.
