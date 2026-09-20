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
* **NO CUT emergency switch, per printer**: tick "No cut" on a printer's row and every cutter command is removed
  from every job to that printer, whatever the POS app or driver asked for, with a feed to the tear bar instead.
  Applies at once, while running; the other printers keep cutting.
* **Printer actions tab**: every ticket as it went to the printer, line by line, what each job contained, the
  cuts removed, and what Windows reports about the printer (offline, paper out, cover open, paused, jobs stuck).
* **Device tab**: live cards for the PC the bridge runs on. USB, Wi-Fi, Bluetooth, processor, RAM, GPU and
  temperatures as tiles and 60-second graphs, with every printer event marked on the same timeline; a battery
  card that flips over to show everything Windows knows about the battery; a cooling card that runs the fans at
  maximum or caps the CPU for a set number of minutes. All of it goes to a telemetry CSV that can be cleared from the tab.
* **ePOS device id and copy links**: the id a POS app names in the ePOS URL is set per printer, and the ePOS
  link (with `?devid=`) and the certificate link a client visits once are each one click away.
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

The bridge answers the **device id** set for the mapping: `local_printer` by default, which is what a real Epson
uses, or a name of your own such as `kitchen`. A request naming any other id gets `DeviceNotFound`, as a real
printer answers. The row under the mapping list holds the id and two buttons: **Copy ePOS link** copies the full
URL with `?devid=` for the selected printer, ready to paste into the POS app (in the SDK the same id goes into
`createDevice`), and **Copy certificate link** copies the page described below. The bridge parses the
`<epos-print>` XML, converts it to ESC/POS and prints it, then returns the `<response success="true" …/>` the SDK
expects. Because the conversion happens on the PC, **any generic ESC/POS printer works**; it does not have to be an Epson.

Port 80 and 443 are the defaults because the SDK builds its URL with no port at all, which is why POS apps often ask
only for an IP address. 8008/8043 are served as well for clients that use those.

Supported ePOS-Print elements: `text` (align, font, bold, underline, reverse, size/double, line spacing), `feed`,
`cut`, `pulse` (cash drawer), `barcode` (UPC/EAN/CODE39/ITF/CODABAR/CODE93/CODE128), `symbol` (QR Code), `image`
(raster), and `command` (raw hex passthrough). Page-mode and ruled-line elements are skipped with a warning in the log.

### HTTPS and browser security

If your POS page is served over **HTTPS you must use the https endpoint**. Browsers block a request from an https
page to an http address (mixed content), and that is usually what a Content-Security-Policy error is really about.
The bridge serves TLS with a self-signed certificate it generates on first use. To stop the browser warning:

* **Copy certificate link** copies `https://<LAN address>/cert`. Open it once on each client device and accept
  the warning; the page then confirms that the device trusts the bridge, and offers the `.cer` file for a
  permanent install so no browser or app on that device ever warns again. The same page is served at
  `/cert` on the http ports, where it points at the https link.
* Or **Tools → Export ePOS HTTPS certificate…**, copy the `.cer` to the client, and install it as a trusted CA
  (Windows: Local Machine → Trusted Root Certification Authorities; Android: Settings → Security → Encryption &
  credentials → Install a certificate → CA certificate; iOS: install the profile, then enable full trust).

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
* **No cut** (per mapping, emergency): see below. Saved the moment it changes, so a broken cutter stays bypassed after a restart.

Configuration and logs live in `C:\ProgramData\UsbLanPrinterBridge\` (File → Open data folder).

## NO CUT: stop one printer's cutter at once

When a cutter is jammed, broken, or must not cut (a kitchen printer on continuous paper, a printer whose
blade shreds the roll), tick **No cut** on that printer's row. It is the one column you can change while the
bridge is running. Other ways to the same switch: select rows and press **F8** or use the *ON for selected* /
*OFF for selected* buttons under the settings, right-click the tray icon (every printer is listed there with a
tick), or start the exe with `--no-cut`, which ticks it on every mapping. From that moment, for that printer:

* Every cutter command is removed from every job to it: `GS V` in all its forms (`GS V 0/1/48/49`,
  `GS V 65/66 n`, the paper-feed-and-cut variants), and the older `ESC i` and `ESC m`. Raw 9100 jobs, ePOS jobs
  and the test pages all go through the same filter, so it does not matter which way the POS app prints, or
  which driver on a Windows client generated the job. Printers without the tick keep cutting.
* Each removed cut is replaced by a short feed (4 lines by default, adjustable, 0 to disable) so the receipt
  still comes out past the tear bar for tearing by hand. Two cuts in a row give one feed, not two blank strips.
* It applies immediately, including to a job already streaming on an open connection, and stays on until you
  untick it. The row's Status reads `NO CUT ·`, the status bar turns red and counts the cuts removed, and each
  removed cut is listed in the Printer actions tab.
* Image and QR data are parsed, not pattern-matched. A logo whose pixel bytes happen to spell `GS V` is left
  alone, because the filter skips image, graphics, barcode and symbol payloads by their declared lengths.

What it cannot do: the bridge only controls the bytes it forwards. A cut triggered inside the printer, by a
feed button, a DIP switch or a memory switch such as "cut on form feed", is not something software on the PC
can prevent. If the printer still cuts with No cut on, the Printer actions tab shows the job contained no cut
command, and the printer's own setup is where to look.

## The Device tab

The third tab at the bottom of the window is about the PC the bridge runs on, for the moment a till slows down,
a laptop gets hot, or a printer drops off. When it is selected the tab area grows to most of the window. Three cards:

**System.** Seven tiles, refreshed every second: **USB** (printers online out of those mapped), **Wi-Fi** (signal
and link speed, or the wired link), **Bluetooth** (printers online out of those mapped), **Processor** (load and
clock), **RAM**, **Video** (GPU load, memory and adapter name) and **Temperatures** (CPU, GPU, battery). Each tile
carries a chip: **LIVE** for a reading Windows gives directly, **EST** for the closest reading available, **N/A**
when this PC does not expose it (a desktop has no battery temperature; a GPU without a driver counter shows no
load). Below the tiles, two graphs of the last 60 seconds with a crosshair and tooltip on hover: **Load** (CPU,
RAM, GPU, Wi-Fi signal) and **Temperature** with a red line at the throttling point. **Every printer event is a
marker on the same timeline**: a ticket printed (green), a cut removed by No cut or an ePOS element skipped
(amber), a printer reported offline or not taking data (orange), a failed job (red), so a spike and the order that
caused it line up. Two strips show when each USB and Bluetooth printer was online, and a list under the graphs
names the latest events with their time.

**Battery** (laptops; a desktop shows "no battery"). The front shows the level in large type, the state
(charging on AC, discharging, full, the watts and volts), a graph of the level per minute over the last hour and
bars of the current in and out, with tooltips. Tap **Details** and the card flips over to everything Windows
reports: state, power source, current, power, voltage, temperature, time remaining, design and full-charge
capacity, wear, cycle count, chemistry and manufacturer.

**Cooling.** Windows has no fan control an application may use, so the card does the two things it can:
**Fans to max for N minutes** sets the active power plan's cooling policy to *Active* and the processor maximum to
100 %, which on most laptops runs the fans up at once, and **Throttle: CPU at N % for N minutes** caps the
processor maximum instead, for a machine that is overheating. Both use `powercfg`, count down on the card, and
restore the plan's previous values when the timer ends, when you press Stop, or when the app closes. They need the
administrator rights the app normally runs with. Each start and stop is a printer event as well, so a throttled
period is visible on the graphs.

**Telemetry log.** Every tile, both graphs and every event go to `logs\telemetry-YYYYMMDD.csv`, one row every
five seconds with a full timestamp and the events since the previous row, kept for 14 days. The panel at the
bottom of the System card shows today's file and its size, opens the log folder, and **Clear telemetry logs…**
deletes the files after a confirmation.

**Fold and reorder.** Every card, and every block inside a card (the tiles, each graph, the printer strips, the
event list, the telemetry panel, each battery graph, each cooling control), is an accordion section. Click its
heading to fold it; the heading then carries a one-line summary (`CPU 29 % · RAM 62 % · GPU 6 %`), so the gist
stays on screen. Drag the **≡** grip to move a card, or a block within its card, to where you want it; the page
scrolls when the pointer nears its edge. On the main window, **Fold settings** next to the buttons tucks the
settings, NO CUT and ePOS rows away so the printer list and the tabs get the room. What is folded and the order of
everything is saved in the configuration and restored at the next start.

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
* **The printer cuts when it must not, or the cutter is jammed**: tick **No cut** on that printer's row. See above.
* **The printer prints garbage or nothing although jobs arrive**: open the **Printer actions** tab. If it says
  the job is *not ESC/POS* (PCL, PostScript, PDF, ZPL), the sending device is using the wrong driver. If it lists
  *unknown commands*, the app speaks another printer's dialect. If Windows reports *Offline*, *Paper out*,
  *Paused* or *jobs waiting in the queue*, the problem is between Windows and the printer, not in the bridge.
* **Remove does nothing for some rows**: it removes every selected row, stopping running bridges first after one
  confirmation. Ctrl-click or Shift-click selects several rows; the Delete key also removes the selection.

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

On a phone that runs the [Android build of this bridge](https://github.com/azzaroES/escpos-printer-bridge/tree/android)
the problem does not arise: that app is itself a print service, so the phone's Print menu lists its printers directly.

## Print log and diagnostics

Every job is recorded: the time, the device that sent it, how it arrived, its size, whether it printed, and the
ticket as it went to the printer. It is the **Print log** tab at the bottom of the window, next to Log, Printer
actions and Device; Ctrl+L or **Log → Print log** jumps to it. Failed jobs are listed too, in red, which is the
case that matters when a client is being rejected.

**Any bottom tab can be pulled out into its own window**, for a second monitor or to keep the orders beside the
printer list: double-click the tab, drag it off the tab strip, or right-click it. The window carries on exactly
where the tab was. Close it, or click *Dock back*, and the tab returns to its place. Windows left floating are
reopened where they were at the next start.

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

### The Printer actions tab

Next to the log at the bottom of the window is a second tab, **Printer actions**, about the printer and the paper
rather than about addresses and ports. Each row is one thing that happened, with the printer, who caused it, and
the details; errors are red, warnings orange. Select a row and the pane on the right shows **the ticket as it
went to the printer**, line by line, as it looks on paper:

```text
                   ACME STORE
Total   12.50
[image 384x120]
[QR data: https://example.com/r/1234]
[QR code]
[barcode: 12345]
[drawer opened]

- - - - - - - - - -  cut  - - - - - - - - - -
```

Centred and right-aligned lines are padded to the 48 columns of an 80 mm roll, feeds become blank lines, and
everything that is not text gets a marker. Image and symbol payloads are skipped by their declared lengths, so
pixel bytes never show up as garbage. The same rendering is in the Print log window (Ctrl+L) for every job.

The tab records:

* **What each job told the printer**, parsed from the ESC/POS stream: `init; 14 lines of text; image; QR/2D
  symbol; drawer pulse; cut`. A job that is not ESC/POS at all (PCL, PostScript, PDF, ZPL) is flagged, as are
  commands the bridge does not recognise, a job that changes the printer's own settings (`GS ( E`), and a job
  that ends in the middle of a command because a client disconnected early or the idle timeout split a receipt.
* **Every cut removed by No cut**, naming the command (`GS V 66 0 (feed and cut)`) and the feed sent instead.
* **The queries answered on the printer's behalf**: `DLE EOT 1`, `GS I 67`, `GS ( H` and the rest, so you can
  see what a POS app asked before it printed, or when it asked and then never printed.
* **ePOS elements that were skipped**, and ePOS documents rejected as bad XML.
* **What Windows says about the printer**: the spooler is asked after every job and every five seconds while a
  bridge runs, and any change is logged: *Offline*, *Paper out*, *Paper jam*, *Cover open*, *Paused*, *"Use
  Printer Offline" is ticked*, a printer that is no longer installed, or jobs piling up in the Windows queue
  because the printer is not taking data. A healthy printer produces one "Printer ready" line and then silence.
* **Spooler errors** with what they usually mean: the queue cannot be opened, Windows refused to start the job,
  the printer stopped accepting data after N bytes, the Print Spooler service is not running.

The same rows go to `logs\printer-actions-YYYYMMDD.log`, ticket text included, so they survive restarts.

## Building

Requires the .NET SDK, version 6 or newer. No Visual Studio needed.

```
build.cmd
```

produces `dist\UsbLanPrinterBridge.exe` and runs the self-test harness (`tests\UsbLanPrinterBridge.Tests`, 65 checks),
which covers the ESC/POS responder, the NO CUT filter (every cut form, cut bytes inside image and QR payloads, every
TCP split point, 256 KB of random data, the switch flipped mid-job, per printer end to end through two listeners),
the ticket renderer, the job summariser, the printer actions log, the spooler status probe, the device monitor (a
sample, the telemetry row it writes, and a printer action arriving as an event), the ePOS device id (a print with
the right id, `DeviceNotFound` for another) and the `/cert` page and `.cer` download, the TCP listener, the manager,
the ePOS converter and HTTP/HTTPS server, the config store, a real RAW job through winspool (redirected to a file via
the XPS writer), the iphlpapi struct layout, and renders the main window to `test-output\mainform.png` and the Device
tab to `test-output\mainform-device.png`.
Running the harness as administrator additionally adds and removes a real address on the loopback adapter; without
admin that check expects "access denied" instead.

Command-line switches: `--autostart` (start all bridges, open in the tray), `--minimized`, `--no-elevate`,
`--no-cut` (tick No cut on every mapping at start-up), `--export-cert <file>`.

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
* `EscPosStreamScanner` walks an ESC/POS stream command by command, skipping image and symbol payloads by their
  declared lengths. `EscPosCutFilter` (No cut), `EscPosTicketText` (the ticket rendering) and `EscPosJobSummary`
  (the Printer actions tab) are built on it; `NoCutPrintTarget` wraps every print target with its printer's switch.
* `PrinterStatusProbe` asks winspool (`GetPrinter` level 2) what Windows thinks of a queue; `PrinterWatch` logs changes.
* `IpHelperApi` adds the secondary address with `CreateUnicastIpAddressEntry` (runtime only, SkipAsSource, DHCP untouched).
* `BridgeListener` accepts connections, opens a RAW spooler job on the first byte and closes it on disconnect or idle timeout.
* `BridgeManager` owns the listeners, the virtual addresses (removed on stop/exit) and the firewall rule.
* `StartupHelper` creates a logon scheduled task (`schtasks /RL HIGHEST`) because elevated programs cannot autostart from the Run key.

## License

MIT, see [LICENSE](LICENSE). Epson, ePOS and TM-T20 are trademarks of Seiko Epson Corporation. This project is not
affiliated with Epson; it implements the ePOS-Print protocol and contains no Epson SDK code.
