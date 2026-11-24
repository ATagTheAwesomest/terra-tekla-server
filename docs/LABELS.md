# Piece labels

The admin board's **Labels** tab prints a label for each piece code (barcode) of a job, on
**Avery 5160** sheets: 30 labels to a letter sheet, 3 across and 10 down, each 2-5/8 in x 1 in.
The server makes the labels itself from what it has on file, so PowerFab isn't needed.

## What a label says

```
 ||| |||| || ||| | |||| ||  ← Code 128 barcode of the piece code
          C9227605Q1
 10124520            SEQ 1012
 HSS 6X6X1/2, A500C
 QTY 1 of 12   351 lbs   JOB 26418
```

- **QTY n of N**: this piece among all the codes of its mark and sequence. A reprint of one piece
  still says which of how many it is.
- **Description and weight** come from the code list: import it with `Description` and
  `Weight (lbs)` columns (the facility export has both), or a plain list
  `code, mark, sequence, description, weight`. A code without them takes the description and
  "Asm Wt Ea" of its mark from the job's BOM, if one is uploaded. The tab says how many labels
  would print without a description or weight.

## Printing

1. Pick the **job**.
2. Pick **what to print**:
   - **Whole job**: every code, by mark and sequence.
   - **One assembly**: one mark (and sequence). Untick pieces you don't want.
   - **Chosen barcodes**: scan or paste codes, one per line, to replace torn or lost labels.
     A code that isn't on the job's list is named, and nothing prints until it's fixed.
3. **The sheet in the printer**: tap the positions already peeled off (grey, "used"). Labels go
   into the free positions in order (green, #1, #2, ...); anything more goes onto new sheets. To
   leave a free position empty just this once, switch the tap to **Leave empty this time**.
4. **Print**: the sheet opens in a new tab. Print it at **100 %** (Scale: Actual size or Default,
   not Fit to page) with **Margins: None**. Test on plain paper held against a label sheet first.
5. **Yes, it printed** marks the positions it used, so next time starts where this one stopped.
   The server keeps this one sheet's state (shared by every PC using the admin board). A full
   sheet starts over; **New sheet (all free)** does the same by hand.

## Routes (admin code required)

| Method | Path | What |
| --- | --- | --- |
| GET | `/api/admin/jobs/{job}/labels/marks` | `{ job, marks: [{ mark, sequence, count }], total, missingWeight, missingDescription }` |
| GET | `/api/admin/jobs/{job}/labels?mark=&seq=` | The labels of a pick: `[{ code, mark, sequence, index, of, description, weight }]` |
| POST | `/api/admin/jobs/{job}/labels/sheet` | `{ mark?, seq?, codes?, positions? }` → the printable page (HTML). `codes` = only these; `positions` = free positions (1-30) on the first sheet, in order. 400 names codes not on the job's list. |
| GET / PUT | `/api/admin/labels/sheet-state` | `{ used: [1-30] }`: the positions used on the sheet in the printer. |
