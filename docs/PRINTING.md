# Printing through the server

The iPads can print shipping tickets on the shop's printers through this server, so an iPad in
another building (or anywhere it can reach the server) prints on a printer the server is wired to.
AirPrint from the iPad still works as before and is always offered too.

```
iPad ──HTTPS──► terra-tekla-server ──► Windows printer (its own driver)
   "Print"          lays out the       ──► network printer, IPP (PDF)
                    ticket             ──► network printer, port 9100 (PDF)
```

## Setting it up (admin board › Settings › Printers)

Everything here is behind the admin code.

1. **Scan for printers.** The server looks three ways at once (about 10 seconds):
   - the printers **installed on the Windows server** (as Windows lists them);
   - **Bonjour / mDNS**: printers that announce themselves (`_ipp._tcp`, `_ipps._tcp`,
     `_pdl-datastream._tcp`), which almost every network printer made in the last 10 years does;
   - a **probe of the server's own networks**: every address on each network the server is on (its
     /24, or the whole network up to a /22, 1022 addresses) is tried on port 631 (IPP) and 9100
     (raw). Printers on port 631 are then asked their name, model and whether they take PDF.
2. **Add** the printers you want. One that the scan can't see (another network the server reaches,
   say) can be added by its address: an IP, `ipp://…`, `host:9100` or a Windows printer's name.
3. Tick **iPads** on each printer the iPads may use, give it a name the shop knows ("Yard office"),
   and optionally where it is.
4. **Save printers.** Then **Test page** prints a one-page sample ticket to check it.
   **Check** asks the printer what it is and whether it is ready, without printing.

The list is kept in `DATA_DIR\printers.json` (the same folder as the database). Saving takes effect
at once; no restart. The last 50 print jobs (what, where, who, and the error if one failed) are
listed under the printers, and each print also shows in the activity history.

## The three kinds of printer

| Kind | Address | How it is printed | Works with |
|---|---|---|---|
| **Windows printer** | its name in Windows, e.g. `HP LaserJet Yard` | drawn through the printer's own Windows driver | any printer Windows can print to: USB, network, shared |
| **Network (IPP)** | `ipp://192.168.1.50/ipp/print` (or `ipps://`) | the ticket as a PDF, sent with IPP Print-Job | printers that print PDF themselves (most office lasers and copiers; the scan and Check say "takes PDF") |
| **Network (raw 9100)** | `192.168.1.50:9100` | the ticket as a PDF inside a PJL job (`ENTER LANGUAGE=PDF`) | printers with a PDF interpreter on port 9100 (many HP, Ricoh, Kyocera, Canon, Xerox) |

**When in doubt, use the Windows printer.** Install the printer on the Windows server (with its
driver) and pick the Windows entry: that is the one that works with every printer, including cheap
ones that can't print PDF. If an IPP printer says "can't print PDF", that is the fix.

Paper: Windows printers use the printer's default paper (set it in Windows). IPP and raw printers get
a PDF in the paper size picked for them (Letter, A4 or Legal; Letter by default).

### Windows printers and the service account

The server runs as a Windows service, as **LocalSystem** unless it was installed otherwise.
LocalSystem sees printers installed **for the whole machine**: added from Settings › Printers &
scanners by an administrator, from Print Management, or with `Add-Printer`. A printer that was only
connected for one Windows user (for example `\\printserver\Yard` added while logged in as yourself)
is not seen by the service. Check lists only the printers the service sees, so:

```powershell
# As administrator on the server: a network printer for every account, by IP.
Add-PrinterPort -Name "IP_192.168.1.50" -PrinterHostAddress "192.168.1.50"
Add-Printer -Name "Yard office" -DriverName "HP Universal Printing PCL 6" -PortName "IP_192.168.1.50"
# Or a shared printer on a print server, for every account:
rundll32 printui.dll,PrintUIEntry /ga /n"\\printserver\Yard"   # then restart the Print Spooler
```

Then **Scan for printers** again and add it.

### Networks, firewalls

- The scan only searches the networks the **server** is on. A printer on another VLAN or in
  another building's network can still be used: add it by address, or install it on Windows.
- mDNS needs UDP 5353 multicast to reach the printers' network; many routers don't pass it between
  networks. The port probe still finds them on the server's own network.
- The server talks to IPP printers on TCP 631 and to raw printers on TCP 9100. Windows Firewall
  allows outgoing connections by default.
- `ipps://` printers almost always have a self-made certificate, so the server doesn't check
  printer certificates.

## What the iPad does

Menu › **Print shipping ticket** opens a printer list: the printers ticked here, then **This iPad
(AirPrint)** at the bottom. Pick one and the number of copies (1 to 10); the iPad sends the ticket
to the server, which prints it and says whether it worked. The iPad remembers the last printer it
used. With no printers ticked, or no connection to the server, Print goes straight to AirPrint as
before. If the server can't print (printer off, wrong address), the iPad says why and offers
AirPrint instead.

## The ticket

The iPad lays the ticket out (`ticketModel()` in the app's `js/export.js`, the same data it prints
through AirPrint) and the server only draws it, so both look the same: a title, the summary (load,
job, sequences, totals), one table per stack with its subtotal, the pieces not loaded yet, and the
sign-off lines. Each page repeats the table header and has "Page n of N" at the bottom. A row is
never split across pages. Marks typed on the keypad are underlined and slanted, as on the AirPrint
ticket. The PDF uses only the standard PDF fonts (Helvetica and Courier); Windows printers get
Arial and Courier New, which have the same widths.

## API

All need the access key (`Authorization: Bearer <ACCESS_KEY>`); `/api/admin/*` also need the admin
code's token (`X-Terra-Admin`).

| Route | What |
|---|---|
| `GET /api/printers` | The printers the iPads may use: `{printers: [{id, name, location, kind, paper}]}` |
| `POST /api/print` | Prints. Body `{printerId, copies, ticket}`. 200 `{ok, printer, pages, copies, jobId}`; 404 printer not allowed; 400 bad ticket; 502 the printer couldn't print (with why) |
| `POST /api/print/pdf` | The ticket as a PDF, as the server would print it. Body `{ticket, paper}` |
| `GET /api/admin/printers` | Every printer set up, the file, the paper sizes and the recent jobs |
| `PUT /api/admin/printers` | Saves the whole list. Body `{printers: [{id?, name, kind, address, allowed, paper, location}]}` |
| `POST /api/admin/printers/scan` | Looks for printers (takes a few seconds) |
| `POST /api/admin/printers/check` | Asks one printer what it is. Body `{kind, address}` |
| `POST /api/admin/printers/{id}/test` | Prints a test page |

The ticket JSON:

```json
{
  "title": "Shipping Ticket", "job": "EPM-001", "load": "12",
  "summary": [["Load", "12"], ["Job", "EPM-001"], ["Total weight", "18,450 lbs"]],
  "sections": [{
    "heading": "Stack 12 - 1", "note": "3 pcs · 1,234 lbs",
    "columns": [{"label": "#", "kind": "num"}, {"label": "Mark", "kind": "mark"}, {"label": "Description", "kind": "text"},
                {"label": "Weight (lbs)", "kind": "num"}, {"label": "✓", "kind": "check"}],
    "rows": [{"cells": ["1", "1001-C1", "Column", "500", ""], "manual": false}],
    "foot": [{"text": "Stack subtotal", "span": 3}, {"text": "500"}, {"text": ""}]
  }],
  "signoff": ["Loaded by: ______", "Checked by: ______", "Driver: ______"]
}
```

Column kinds: `text` (wraps, takes the room left), `num` (right aligned), `mark` (bold
monospace), `check` (a tick box). At most 12 columns and 5000 rows.

## What was tested, and what wasn't

Tested (in `tests/TerraTeklaServer.Tests/PrintingTests.cs`): the layout (page breaks, repeated
headers, page numbers, nothing outside the margins), the PDF's structure, IPP requests against a
pretend IPP printer (Print-Job with a PDF, copies, job name; Get-Printer-Attributes; a printer that
refuses PDF), the raw 9100 job, reading Bonjour answers, the network address maths, the admin code
on every admin route and the allowed-printers rule.

Not tested before release, because there was no real printer or Windows machine to hand: printing
on a real printer of each kind, Windows' printing through a driver (the service and LocalSystem),
the scan on the shop's real network, and how long it takes there.
