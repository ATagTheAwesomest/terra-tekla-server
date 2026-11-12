# Job BOM

Each job has a slot for its BOM: the big workbook the BOM script makes (one per job). It adds what
PowerFab doesn't say about an assembly, mainly **which transmittal it is in** (TL1282, or TLR1361 for
a revision), and its due date, paint / notes, drawing and revision.

It is information only: nothing about scanning, loading or shipping changes because of it.

## Putting a BOM in (admin board)

Open the job's page, find **BOM**, pick the file and press **Upload**. A new upload replaces the
one there, so upload again whenever the script makes a newer BOM.

- **An .xlsx workbook**: the "Master BOM" sheet is read (or the first sheet with an Assembly
  column). Formulas are read as the values Excel saved.
- **A CSV** (or tab-separated) copy of that sheet works too.

Columns are found by name: Assembly (or MainMark / Main Mk), Transmittal, Sequence, Drawing, Rev,
Revises, Due Date, Paint / Notes, Asm Description, Asm Qty, Asm Length, Asm Wt Ea (lbs), Exception.
The BOM repeats an assembly's details on each of its part lines: the first line of each assembly
and transmittal is kept.

If the workbook's Settings sheet gives "Our job #" and it isn't the job you uploaded it to, the
board says so (the BOM may be in the wrong slot).

## In the app

Piece lookup shows the transmittal, due date, paint / notes and revision of a mark, and the Job
view can be narrowed to one transmittal. Shipping shows the transmittal on the last scan's card.

## API

| | Route | |
|---|---|---|
| GET | `/api/jobs/{job}/bom` (`?mark=`) | `{job, info, rows}`; 404 when the slot is empty |
| GET | `/api/admin/boms` | Jobs with a BOM |
| POST | `/api/admin/jobs/{job}/bom` | The file as the body, its name in `X-File-Name` |
| DELETE | `/api/admin/jobs/{job}/bom` | Empties the slot |

Live message: `{"type":"bom","job","uploadedAt"}` (null when removed).

## Database

`job_bom (job, mark, transmittal, row JSON)` and `job_settings.bom` (the file's name, when, who,
counts, transmittals).
