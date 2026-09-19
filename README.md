# ESC/POS Printer Bridge

Share a receipt printer on the network. A USB, Bluetooth or built-in POS printer becomes a **network printer**
that tills, POS apps and PCs print to by IP address, exactly as they would print to a networked Epson.

Two apps do the same job from different hosts:

| | Windows | Android |
|---|---|---|
| Runs on | Windows 8, 8.1, 10, 11 | Android 5.0 and newer, every CPU type |
| Shares | any printer installed in Windows | USB over OTG, Bluetooth, Sunmi built-in, or a network printer |
| Raw / JetDirect | port 9100 | port 9100 |
| Epson ePOS-Print | HTTP 80 and 8008, HTTPS 443 and 8043 | HTTP 8080 |
| Ships as | one `.exe`, no installer | one `.apk` |
| Written in | C#, .NET Framework 4.5, WinForms | Java, plain Android framework |
| Source | [`windows` branch](https://github.com/azzaroES/escpos-printer-bridge/tree/windows) | [`android` branch](https://github.com/azzaroES/escpos-printer-bridge/tree/android) |

## Download

Prebuilt apps are on the [Releases page](https://github.com/azzaroES/escpos-printer-bridge/releases):
tags starting `windows-` carry the exe, tags starting `android-` carry the APK.

## How it works

```
 till / POS app / PC                     bridge                              printer
 ───────────────────        ───────────────────────────────        ───────────────────────
 raw ESC/POS  ──TCP 9100──▶ forwards bytes unchanged          ──▶  USB, Bluetooth, Sunmi
 ePOS SDK app ──HTTP(S)───▶ ePOS-Print XML → ESC/POS          ──▶  built-in, or another
                            answers status/identity queries        network printer
                            the way a real Epson does
```

Most POS software checks it is talking to a real printer before it prints. The bridge answers those checks with
bytes captured from a genuine Epson TM-T20II, including the `GS ( H` process-id echo that Epson's SDK waits for
to confirm a job finished. Without that reply, SDK-based apps report the printer as not found. Because the ePOS
XML is converted to ESC/POS by the bridge, any generic ESC/POS printer works, not just an Epson.

Both apps also carry a **NO CUT** emergency switch, which strips every cutter command from every ticket to a
printer (per printer on Windows) and feeds the paper to the tear bar instead, for a jammed or broken cutter; and
both show **each ticket as it went to the printer**, line by line, in their logs. The Android app is also an
Android print service, so the phone's own Print menu lists its printers.

## Repository layout

The two apps share a protocol but no code, one being C# and the other Java, so each is a complete project on its
own.

| Branch | Contents |
|---|---|
| `main` | both apps side by side, in `windows/` and `android/` |
| `windows` | the Windows app alone, with its project at the root |
| `android` | the Android app alone, with its Gradle project at the root |

`main` is where changes are made. The two app branches are generated from its folders with `git subtree split`,
so each carries only its own source and history:

```bash
git subtree split --prefix=windows -b windows
```

```bash
git subtree split --prefix=android -b android
```

Build instructions are in each app's README: [Windows](windows/README.md), [Android](android/README.md).

## What has been verified

* The Windows bridge prints from **Loyverse** by IP address alone, and passes its 60-check self-test suite.
* Every reply the bridges send was captured from a real **Epson TM-T20II**, not taken from documentation.
* The Android app runs on a real phone (Android 10) and serves 9100 and 8080; its responder passes 24 checks and
  its raster, footer, NO CUT and ticket code 53 desktop checks.

Still waiting on hardware: printing through the Android app's Sunmi, Bluetooth and USB-OTG routes, its print
service, and running the Windows exe on ARM64 Windows. Each README says precisely what is and is not tested.

## License

MIT, see [LICENSE](LICENSE). Epson, ePOS and TM-T20 are trademarks of Seiko Epson Corporation, and Sunmi is a
trademark of its owner. This project is not affiliated with either; it implements the published protocols and
contains no vendor SDK code.
