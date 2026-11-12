# Piece codes

Some jobs put a long unique code on every single piece, not just a mark on the assembly: a mark with
40 pieces has 40 different codes. On those jobs, loading and the truck check can scan each piece's
code, so the server knows exactly which piece went on which truck. PowerFab never sees the codes;
they live in this server's database only.

Piece codes are **off** for every job until switched on. A job with them off works exactly as before.

## Switching them on (admin board)

Jobs › the job › **Piece codes** (changing it needs the admin code):

- **Piece codes on**: the iPads start treating code scans on this job as codes.
- **What a code looks like**: a regex. A scan that is already on the job's code list is always a code.
  A scan that isn't counts as a new code when it matches this pattern, and as a mark when it doesn't.
  The default, `^[A-Za-z0-9]{12,}$`, is 12 or more letters and digits with no dashes (marks have
  dashes). Change it once real codes are known. A named group `(?<code>…)` keeps just that part of
  the scan, e.g. `id=(?<code>[A-Z0-9]+)` for a QR code that holds a link.
- **Pair codes not on the list**: on, a new code is paired on the iPad (scan the code, then the
  piece's mark tag) and remembered. Off, a code not on the list is refused.
- **Add codes**: paste lines of `code, mark` or `code, mark, sequence` (commas, tabs or semicolons; a
  copy from a spreadsheet works; a header line is skipped). A code already paired with a different
  mark is left alone unless "Move a code…" is ticked. **Remove all** clears the job's list (rows
  already loaded keep their code).

An API can fill the same list later: it would call the same code as the import (`Store.AddCodes`).

## Importing an export

The admin board's **Add codes** takes a file (or a paste) of either kind:

- **An export with a first line naming its columns**, like the facility portal's
  `Status, Sequence, Assembly, Barcode, Description, ...`. The code column (Barcode, Code, Serial,
  Tag, ...) and the mark column (Assembly, Mark, Main Mk, ...) are found by name; Sequence and
  Status are kept when there; other columns are ignored. Values in double quotes may hold commas.
- **A plain list**, one `code, mark[, sequence]` per line.

Importing the same export again later refreshes it: each piece's status and sequence are updated,
codes already there are left as they are, and new ones are added (tick "Move a code..." to also move
codes the export now gives another mark). Nothing is removed by a re-import.

After an import the board shows how many pieces have each status, and, when every code has the
same shape, a pattern for them (for example `^C\d{7}Q\d$` for `C9227621Q1`) with a button to use it.

The facility export's **Assembly** is the PowerFab main mark (for example `10121601`), so no mapping
is needed. A piece's **Status** from the list (for example "Shipped to treatment") is shown on the
handheld when its code is scanned, in orange when it says shipped. It never blocks a scan.

## On the iPads and handhelds

- **Loading**: scanning a code loads one piece of its mark, exactly as scanning the mark does, and the
  row keeps the code. If the list gives a sequence, that sequence is taken first. A code can be on one
  load only: scanning it again, here or on another load, is refused with where it is.
  Scanning a plain mark on a code job still works; the row just has no code.
- **New code** (pairing on): the app asks for the piece's mark; scan the mark tag (or type it) and the
  piece loads. Cancel, or scan something else, to drop it.
- **Truck check**: a code ticks off the row that was loaded with that code. A code whose row was
  loaded by mark ticks off a row of its mark that has no code. The same code twice is refused. Each
  mark shows how many of its pieces are checked (`1001-C1 12/40`).

## API

| Method | Path | What |
|---|---|---|
| GET | `/api/jobs/{job}/codes?since=` | `{ job, settings: { enabled, pattern, learn }, count, codes: [{ id, code, mark, sequence, source }], last }`. Codes with an id above `since` (0: all); `last` is the next `since`. The app keeps a copy and asks for what is new. |
| GET | `/api/jobs/{job}/codes/{code}` | `{ code, mark, sequence, source, load: { loadId, loadNumber } }` (`load` null when no row has it). 404 if the job has no such code anywhere. |
| POST | `/api/jobs/{job}/codes` | Pairs a code from an iPad: `{ code, mark, sequence? }`. 403 unless the job's codes are on and set to pair; 409 when the code is already paired with another mark. |
| GET | `/api/admin/codes` | Jobs with code settings or codes: `[{ job, settings, codes }]` (admin code). |
| PUT | `/api/admin/jobs/{job}/codes/settings` | `{ enabled, pattern, learn }`. 400 if the pattern isn't a valid regex. |
| POST | `/api/admin/jobs/{job}/codes/import` | `{ text, overwrite }`. Answers `{ read, added, updated, unchanged, conflicts, conflictCount, errors, errorCount }`. |
| DELETE | `/api/admin/jobs/{job}/codes` | Removes the job's list. `{ removed }`. |

Operations carry the code: `beginScan` takes `code` (with one `entryId`) and refuses a code already on
this or another load of the job; `addCheck` takes `code` (a repeat of a code already checked changes
nothing). Rows and check scans have `code` in the load state when they have one.

Live messages: `codes` `{ job, codes }` (codes added), `codes-settings` `{ job, settings }`,
`codes-cleared` `{ job }`.

## Database

| Table | Columns | Holds |
|---|---|---|
| `piece_codes` | `id`, `job`, `code`, `mark`, `sequence`, `source` (`import`, `paired`), `added_at`, `added_by` | Each job's code list; one row per code per job (any case). |
| `job_settings` | `job`, `piece_codes` (JSON), `updated_at` | Per-job settings. |

`entries.code` and `check_scans.code` hold the code a row was loaded with, and a truck check scan's code.
