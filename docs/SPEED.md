# Scan speed

What makes a scan slow, what this server and the app do about it, and how to see the timings.

## Where a scan's time goes

A scan on Shipping goes from the device to this server, which passes it to tekla-conversion-layer,
which sends it to PowerFab. **The conversion layer runs one PowerFab command at a time** (one
PowerFab session), so every call from every device and the admin board waits in one line.

Two kinds of call matter:

- **Writes** for the scan itself: assign the piece to the load (`ProductionControlLoadMaterial_Add`)
  and load it (`ProductionControlLoadMaterial_Load`). Small and quick.
- **Reads** of the whole job: every load with its material (`ProductionControlLoad_Get`) and
  everything left to ship (`ProductionControlLoadRemaining_Get`). These grow with the job: every
  load the job ships adds to `ProductionControlLoad_Get`, so they get slower week by week.

Up to app v0.25 each scan made five calls: assign, read every load, load, then read every load and
everything left again. A second device on the same job read both again after each of those scans,
and the admin board's Jobs page read PowerFab's job list on every live update. With a job of about
1,000 assemblies and 60 shipped loads, a scan every 1.5 s fell behind by about 5 s per scan: the
twelfth scan in a row waited a minute before it was loaded.

## What is done about it (server v0.12.0, app v0.26.0)

- **The app** makes two calls per scan: assign and load. PowerFab answers both with the load they
  changed, so the app takes the piece's row from that answer instead of reading every load again
  (only if an answer doesn't have it, it reads them as before). The job is read once, 1.5 s after
  the scanning stops, to catch up with anything else. Any other change (taking a piece off, shipping)
  still reads first if that catch-up read hasn't happened yet.
- **Other devices** read PowerFab's numbers again 4 s after the last change another device made,
  not after each one (their stacks still arrive live at once), and not while the app is in the
  background.
- **This server** shares reads: when two devices ask for the same read at once, one call goes to
  PowerFab and both get its answer; an answer is reused for 2 s (PowerFab's job list, which the admin
  board reads on every live update, for 60 s). Any write through the server throws every kept answer
  away first, so a read after a write is always PowerFab's own (`PowerFabCalls.cs`).

## Seeing the timings

- **Admin board › Server › Speed**: each device's scans (average, slowest, how long they waited
  behind earlier scans, and the last 40 with each step), and PowerFab calls per command (how many,
  how many were shared, average and slowest time, reply size), plus the last 40 calls with which
  device made them. Kept in memory since the server started.
- **On a device**: tap the version number at the bottom right. It lists the last scans on that device
  with the same steps. Devices send these to the server a couple of seconds after each scan.

What to look for: if `ProductionControlLoad_Get` or `ProductionControlLoadRemaining_Get` take several
seconds, the reads are the slow part (a big job); if the PowerFab writes themselves take seconds, it
is PowerFab or the conversion layer's computer; if "waited" is large but each step is quick, scans
are queueing behind each other faster than PowerFab answers.

## Trying it without PowerFab

`tools/fake-powerfab.mjs` can play a job of real size, one command at a time like the real bridge:

```
FAKE_ASSEMBLIES=1000 FAKE_LOADS=60 FAKE_SERIAL=1 FAKE_DELAY_MS=150 FAKE_BYTES_PER_MS=400 node tools/fake-powerfab.mjs
```
