# USB LAN Printer Bridge for Windows

Turns any printer installed on a Windows PC (typically a USB receipt printer) into a **network printer**:
other devices on the LAN print to an IP address and port 9100 (RAW / JetDirect style), and the bridge
pushes the bytes straight into the Windows print queue of the USB printer.

* One executable, no installer, no runtime to install: **Windows 8, 8.1, 10, 11** (and Windows 7 SP1 with .NET 4.5).
  It is built AnyCPU, so the same file runs on x86 and x64 Windows.
* Several printers at once, each with its own LAN address.
* **Epson ePOS-Print emulation over HTTP and HTTPS**, so web and Android POS apps written against the Epson
  ePOS SDK can print to *any* generic ESC/POS printer through the bridge (see below).
* Virtual LAN addresses are added to the adapter **without touching its DHCP settings** and vanish at reboot.
* Understands POS software that keeps one connection open and sends receipt after receipt.
* Optional ESC/POS status emulation, so cash-register apps do not hang waiting for a reply the USB printer
  cannot send.
* Windows Firewall rule, "Start with Windows" (logon task with admin rights), tray icon, log files, print log.

This is the Windows half of [escpos-printer-bridge](https://github.com/azzaroES/escpos-printer-bridge).
The Android app, which does the same job from a phone or a POS terminal, lives on the
[`android` branch](https://github.com/azzaroES/escpos-printer-bridge/tree/android).

## Download

Get `UsbLanPrinterBridge.exe` from the
[Releases page](https://github.com/azzaroES/escpos-printer-bridge/releases) (tags starting `windows-`).
It is a single file; put it anywhere and run it.

## Quick start

1. Run `UsbLanPrinterBridge.exe` (it asks for administrator rights; that is needed to add LAN addresses and the firewall rule).
2. Click **+ Add mapping**. The app picks the first USB printer and suggests a free address such as `192.168.1.200`.
   Keep port `9100`, leave the adapter on *(Auto)*.
3. Click **▶ Start all**. The status column turns green: `● Listening 192.168.1.200:9100`.
4. Select the row and use **Test print… → Through the bridge over TCP**. A test receipt should come out of the USB printer.
5. On the other devices, add a network printer pointing at that address, port 9100 (see below).
6. Optional: **Tools → Start with Windows** so the bridge comes back after a reboot, minimised in the tray.

The "LAN address" can also be one of the PC's own addresses or `0.0.0.0` (all addresses). Then no virtual address
is created and administrator rights are not required. `0.0.0.0` is also the robust choice for a laptop that moves
between networks.

To point a mapping at a different printer, stop that row, pick the new printer, and start it again. The log
confirms the switch with a line naming both printers.

## Connecting devices

| Device | How |
|---|---|
| Windows | Settings → Printers → Add printer → *The printer that I want isn't listed* → *Add a printer using a TCP/IP address*. Device type **TCP/IP Device**, hostname = the LAN address, untick *Query the printer*. Choose **Custom → Settings → Protocol RAW, port 9100**. Install the same driver the printer uses on the bridge PC (EPSON driver, *Generic / Text Only*, …). |
| POS / cash-register apps (Android, iOS, Windows) | Choose *Network / Ethernet / LAN printer*, enter the LAN address and port `9100`. Loyverse is confirmed working by IP address alone. |
| Linux / macOS (CUPS) | `socket://<LAN address>:9100` with the matching driver. |
| Anything ESC/POS | Just open a TCP connection to the address:9100 and write the bytes. |

On the raw 9100 port the bridge does not convert anything: it forwards the bytes unchanged, so the sending side must
produce the printer's own language (ESC/POS, PCL, ZPL, …), exactly as for a real network printer. The ePOS endpoint
below is the exception; there the bridge does convert.

## Printing from web / Android POS apps (Epson ePOS SDK)

Apps written against the **Epson ePOS SDK** (JavaScript or Android) talk to a printer that hosts an ePOS-Print
server. A plain USB printer has none, so the bridge provides one. Tick **ePOS web** on the mapping and the bridge
answers, on the mapping's LAN address:

| URL | Port |
|---|---|
| `http://<LAN address>/cgi-bin/epos/service.cgi` | 80 (also 8008) |
| `https://<LAN address>/cgi-bin/epos/service.cgi` | 443 (also 8043) |

Use device id `local_printer`. The bridge parses the `<epos-print>` XML, converts it to ESC/POS and prints it, then
returns the `<response success="true" …/>` the SDK expects. Because the conversion happens on the PC, **any generic
ESC/POS printer works**; it does not have to be an Epson.

Port 80 and 443 are the defaults because the SDK builds its URL with no port at all, which is why POS apps often ask
only for an IP address. 8008/8043 are served as well for clients that use those.

Supported ePOS-Print elements: `text` (align, font, bold, underline, reverse, size/double, line spacing), `feed`,
`cut`, `pulse` (cash drawer), `barcode` (UPC/EAN/CODE39/ITF/CODABAR/CODE93/CODE128), `symbol` (QR Code), `image`
(raster), and `command` (raw hex passthrough). Page-mode and ruled-line elements are skipped with a warning in the log.

### HTTPS and browser security

If your POS page is served over **HTTPS you must use the https endpoint**. Browsers block a request from an https
page to an http address (mixed content), and that is usually what a Content-Security-Policy error is really about.
The bridge serves TLS with a self-signed certificate it generates on first use. To stop the browser warning:

* **Tools → Export ePOS HTTPS certificate…**, copy the `.cer` to the client, and install it as a trusted CA
  (Windows: Local Machine → Trusted Root Certification Authorities; Android: Settings → Security → Encryption &
  credentials → Install a certificate → CA certificate; iOS: install the profile, then enable full trust).
* Or open the https URL once on the client and accept the warning.

Cross-origin requests are handled: the bridge answers the CORS preflight and sets
`Access-Control-Allow-Private-Network: true`, which Chrome requires when a public https page calls a private LAN address.

## Settings

* **Job idle timeout**: a print job is handed to the printer when the client disconnects, or after this much silence on
  an open connection (default 1500 ms). Set `0` to only cut jobs on disconnect (use this for PCL/PostScript jobs that may pause).
* **ESC/POS status replies** (per mapping): answers the status and identity queries a USB printer cannot answer through
  the spooler, and does not forward them to the printer. Untick it for non-ESC/POS printers or if you print raw
  bitmaps that could contain those sequences.
* **Adapter**: which adapter receives the virtual address. *(Auto)* picks the adapter whose subnet contains the address.
* **Add Windows Firewall rule automatically**: adds an inbound allow rule for the exe on first start.

Configuration and logs live in `C:\ProgramData\UsbLanPrinterBridge\` (File → Open data folder).

## Troubleshooting

* **"A socket operation was attempted to an unreachable host" / clients cannot connect**: the LAN address is not (or no
  longer) present on the adapter. Stop and start the bridge; the app also re-adds lost addresses every 15 s while running.
  Make sure the address is inside the adapter's subnet (e.g. `192.168.1.x` for a `192.168.1.0/24` network).
* **Nothing can reach the bridge on a phone hotspot**: a phone hotspot does not use the 192.168.1.x network you
  are used to. An iPhone hands out **172.20.10.0/28**, which has only fourteen usable addresses, .1 to .14. A
  mapping bound to something like 192.168.1.201 is then in a subnet that does not exist, so clients have nowhere
  to send packets and simply spin while trying to add the printer. The bridge refuses this outright and names
  a working address instead of starting and being silently unreachable. Either take the address it suggests, or
  set the address to `0.0.0.0`. If it still fails once the address is right, check the hotspot allows its clients
  to talk to each other, by pinging the PC's hotspot address from the other device.
* **Another device already uses the address**: the log warns when the address answers to ping before it is added. Pick a
  free one; reserve it in the router so DHCP never hands it out.
* **Port 9100 refused (error 10013/10048)**: another program listens on that port, or Windows reserved the port
  (`netsh int ipv4 show excludedportrange protocol=tcp`). Use another port.
* **Adapter lost internet / DHCP shows "disabled"**: Tools → *Network repair: re-enable DHCP on an adapter…*. Version 1.0.0
  added addresses with `netsh`, which switches DHCP adapters to static; this was replaced by the iphlpapi call, which does not.
* **Nothing prints but the job counter increases**: the Windows queue received the job; check the printer queue on the
  bridge PC (paused? offline? wrong printer selected?). Use *Test print → Direct to the printer* to isolate the spooler side.
* **POS app says "printer offline"**: tick *ESC/POS status replies* for that mapping.

## Android's own "Add printer" needs an ESC/POS print service

If Android's system print dialog spins forever and never adds the bridge, that is expected. It is not a bug in
the bridge; the cause is which print service is handling the request.

Android's **Built-In Print Service**, `com.android.bips`, is IPP only. It discovers printers over mDNS looking
for `_ipp._tcp`, `_ipps._tcp` and `_printer._tcp`, then prints with IPP by sending the page as PDF or
PWG-Raster. A thermal receipt printer speaks ESC/POS, a different language entirely, and this bridge passes
bytes straight through rather than rendering pages. So there is nothing for it to discover, and the payload
would be unusable even if you added it by hand.

There are two ways forward, and the first is far easier:

1. **Use a POS app.** They talk raw ESC/POS to an IP and port directly.
2. **Install an ESC/POS print service**, such as RawBT. These plug into Android's print framework and speak raw
   ESC/POS over TCP, so the system print dialog starts working. Point the service at the bridge's address on
   port 9100. Check Settings → Printing first, because such a service may already be installed and merely
   disabled.

## Print log and diagnostics

Every job is recorded: the time, the device that sent it, how it arrived, its size, whether it printed, and a
readable extract of the text that went to the printer. Open it from **Log → Print log**, or press Ctrl+L.
Failed jobs are listed too, in red, which is the case that matters when a client is being rejected.

The same rows are appended to a daily CSV in the log folder, so the history survives restarts:

```
logs\prints-YYYYMMDD.csv     columns: Time, Source, Printer, Path, Bytes, Status, Text
```

Two switches in the **Log** menu exist for when a device refuses to connect at all:

* **Save raw bytes of every job** writes each job verbatim to `logs\jobs`, for when you need to see exactly what
  a client sent rather than a text extract.
* **Trace connection attempts** listens on the other ports printing software commonly tries, including LPR on 515,
  IPP on 631, the alternate JetDirect ports, and discovery datagrams on UDP 3289 and SNMP. Anything that arrives is
  logged with a hex dump.

That second switch is the answer to "the app says it cannot find the printer". Turn it on, try to add the printer
on the other device, and the log shows which port it really used and what it sent.

## Building

Requires the .NET SDK, version 6 or newer. No Visual Studio needed.

```
build.cmd
```

produces `dist\UsbLanPrinterBridge.exe` and runs the self-test harness (`tests\UsbLanPrinterBridge.Tests`, 49 checks),
which covers the ESC/POS responder, the TCP listener, the manager, the ePOS converter and HTTP/HTTPS server, the config
store, a real RAW job through winspool (redirected to a file via the XPS writer), the iphlpapi struct layout, and
renders the main window to `test-output\mainform.png`. Running the harness as administrator additionally adds and
removes a real address on the loopback adapter; without admin that check expects "access denied" instead.

The project targets .NET Framework 4.5 through the `Microsoft.NETFramework.ReferenceAssemblies` package, so it
compiles with the plain .NET SDK and runs on the .NET Framework that ships inside Windows 8 and later.
BouncyCastle, used to generate the HTTPS certificate, is embedded in the exe so it stays a single file.

## How it works

```
LAN client ──TCP 192.168.1.200:9100──▶ BridgeListener ──▶ SpoolerPrintTarget ──winspool RAW──▶ USB printer
                                          │
                                          └─ EscPosResponder (optional): answers status/identity queries
```

* `EscPosResponder` answers the queries a USB printer cannot answer through the spooler, using values captured from a
  real Epson TM-T20II: `DLE EOT 1` → `0x16`, `GS I 67` → `_TM-T20II\0`, `GS a` → `14 00 00 0F`, and crucially
  `GS ( H` → `37 22 <id> 00`, the process-id echo Epson's SDK waits for to confirm a job finished. Without that last
  one, SDK-based apps report the printer as not found.
* `HttpBridgeServer` serves the ePOS-Print endpoint and returns `status="251658262"`, the same value the real printer sends.
* `EposPrintConverter` turns the ePOS-Print XML into ESC/POS.
* `IpHelperApi` adds the secondary address with `CreateUnicastIpAddressEntry` (runtime only, SkipAsSource, DHCP untouched).
* `BridgeListener` accepts connections, opens a RAW spooler job on the first byte and closes it on disconnect or idle timeout.
* `BridgeManager` owns the listeners, the virtual addresses (removed on stop/exit) and the firewall rule.
* `StartupHelper` creates a logon scheduled task (`schtasks /RL HIGHEST`) because elevated programs cannot autostart from the Run key.

## License

MIT, see [LICENSE](LICENSE). Epson, ePOS and TM-T20 are trademarks of Seiko Epson Corporation. This project is not
affiliated with Epson; it implements the ePOS-Print protocol and contains no Epson SDK code.
