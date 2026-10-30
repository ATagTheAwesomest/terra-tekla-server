# Photos: taken on the iPads, saved per job and load

The TERRA app (Shipping) can take photos of a load, or of one piece on it, and send them to this
server. Each one is saved in a folder for the job, then a folder for the load, and its name says when
it was taken, of what, and by which iPad (the name set in the app's Server settings):

```
<job folder>\<load folder>\<date-time>_<mark or "load">_<iPad name>.<ext>

D:\TerraPhotos\EPM-001\Load 8\20261028-143502_load_Dock iPad.jpg
D:\TerraPhotos\EPM-001\Load 8\20261028-143611_1001-C1_Yard iPad.jpg
\\fileserver\Jobs\2026\26101\Photos\Load 3\20261028-150012_load_Dock iPad (2).jpg
```

The server also keeps a record of each photo in its database (piece, iPad, size, time), so the app
and the admin page can show who took it even after the iPad is renamed.

Photos are off until `PHOTOS_ENABLED` is turned on. The app has its own switch too (Server settings,
"Send photos to the server"), off until turned on on each iPad.

This file ships next to `TerraTeklaServer.exe` as `PHOTOS.md` and is shown on the admin page
(Docs > Photos), so it always matches the running build.

> Photos used to be saved by tekla-conversion-layer (`FILES_*` settings). That moved here: the
> conversion layer is only a PowerFab converter now. Rename `FILES_*` keys to `PHOTOS_*` and
> `file-rules.json` to `photo-rules.json`; the folder rules work exactly the same.

## Turning it on (startup options)

In `local.settings.json` in the install folder (`C:\TerraTeklaServer\local.settings.json`), or as
environment variables, or on the admin page (Settings > Photos). Restart the service after changing them (`Restart-Service TerraTeklaServer`, or Restart server on the admin page).

| Key | Default | Meaning |
|---|---|---|
| `PHOTOS_ENABLED` | `false` | `true` lets the app send photos. Off: the photo routes answer 403. |
| `PHOTOS_ROOT` | none | Full path of the base folder. Required when on. Jobs go in `PHOTOS_ROOT\{job}` unless a rule says otherwise. |
| `PHOTOS_RULES` | `photo-rules.json` | The rules file. A relative path is next to the exe. Missing file = no rules. |
| `PHOTOS_MAX_MB` | `25` | Largest single photo, in MB. |
| `PHOTOS_MAX_PER_UPLOAD` | `20` | Most photos in one upload. |
| `PHOTOS_TYPES` | `jpg,png,heic,webp,pdf` | File types taken, from that list. The type is read from the file's first bytes, not its name. |

```json
{
  "PHOTOS_ENABLED": "true",
  "PHOTOS_ROOT": "D:\\TerraPhotos",
  "PHOTOS_RULES": "photo-rules.json",
  "PHOTOS_MAX_MB": "25"
}
```

The Windows service runs as LocalSystem by default. LocalSystem can write to local drives, but on a
network share (`\\fileserver\...`) it acts as the computer account (`DOMAIN\SERVERNAME$`), which needs
write permission on that share, or set the service to log on as a user that has it
(`services.msc` > TERRA Server > Log On). Mapped drive letters (`J:`) are per user and don't exist for
a service: use the `\\server\share\...` path in rules instead.

## The rules file (which folder a job goes in)

`photo-rules.json` maps job numbers to folders with regexes. It is re-read when it changes, so edits
take effect without a restart. The admin page edits it too (Settings > Photo folders, behind the
admin code): add, reorder and remove rules, try a job number to see its folder, and save. Saving from
the page rewrites the file without comments (the old one is kept as `photo-rules.json.bak`). The
admin page's Jobs list shows the folder each PowerFab job's photos go in. `photo-rules.example.json` next to the exe is a starting point:

```jsonc
{
  "default": "D:\\TerraPhotos\\{job}",
  "loadFolder": "Load {load}",
  "rules": [
    { "match": "^25-\\d+$", "folder": "\\\\fileserver\\Projects 2025\\{job}\\Shipping Photos" },
    { "match": "^(?<year>\\d{2})\\d{3}$", "folder": "\\\\fileserver\\Jobs\\20{year}\\{job}\\Photos" },
    { "match": "^EPM-001$", "folder": "E:\\Special\\EPM\\Load photos" }
  ]
}
```

- `rules`: checked top to bottom, first match wins. `match` is a .NET regex tested against the job
  number (case-insensitive, 1 second time limit). Anchor it with `^...$` to match the whole number.
- `folder`: the **job's** folder, a full path (`C:\...`, `D:\...` or `\\server\share\...`). It can
  use `{job}` and the regex's groups: named groups like `(?<year>\d{2})` as `{year}`, or plain
  `( )` groups as `{1}`, `{2}`... (.NET numbers the plain groups first, then the named ones, so naming
  them is clearer). The load folder is always added under it.
- `default` (optional): the job folder when no rule matches. Without it, `PHOTOS_ROOT\{job}`.
- `loadFolder` (optional): the folder name per load, from `{load}` (and `{job}`). Default `{load}`,
  so `...\8\`. `"Load {load}"` gives `...\Load 8\`.
- In JSON, every `\` is written `\\`: `D:\Jobs` is `"D:\\Jobs"`, `\\fs\share` is `"\\\\fs\\share"`, and
  the regex `\d` is `"\\d"`. Comments (`//`) and trailing commas are allowed.
- If the file can't be read, isn't valid JSON or a regex is broken, uploads answer 500 with the reason
  and nothing is saved. Fix the file and try again; no restart needed.

To check a rule without the app, list a load's photos (this creates nothing):
`GET /api/photos/{job}/{load}` answers 500 with the reason if the rules are wrong, 200 otherwise.

## What is checked

- The access key, as for every `/api` route (`Authorization: Bearer <ACCESS_KEY>`). Uploads also need
  the iPad's `X-Terra-Device` header, which is how the photo gets its iPad name.
- Job, load and mark: 1 to 64 characters; none of `< > : " / \ | ? *` or control characters; not `.`
  or `..`; no leading/trailing space or trailing dot; not a Windows device name (`CON`, `NUL`,
  `COM1`...). Anything else is refused with 400, never cleaned up, so a name can't reach a folder
  outside its job folder. The final path is also checked to be inside the job folder.
- The iPad name in a file name has those characters replaced with `-` and is cut to 40 characters.
- Each file's size (`PHOTOS_MAX_MB`, 413) and type by its first bytes (`PHOTOS_TYPES`, 415). The name
  the app gives a file is ignored; the server names every file itself and never overwrites one
  (a second photo in the same second gets ` (2)`).
- Every file in an upload is checked before any is saved.
- View and delete take a file name exactly as the list gives it: the same name checks, no name
  starting with `.` (so `.deleted` and half-written uploads can't be reached), and it must be inside the
  load folder.

## API

All need `Authorization: Bearer <ACCESS_KEY>`. Errors are JSON problem details with `detail` saying why.

| Method | Path | Answer |
|---|---|---|
| GET | `/api/photos` | `{ "enabled": true, "maxBytes": 26214400, "maxFiles": 20, "types": ["jpg", ...] }`. Works when off (enabled false). |
| GET | `/api/photos/recent?device=&limit=` | The newest photos across all loads (optionally one iPad's), from the database: `[{ "id", "job", "loadNumber", "fileName", "mark", "deviceId", "deviceName", "bytes", "takenAt" }]`. |
| GET | `/api/photos/{job}/{load}` | `{ "job", "load", "files": [{ "name", "bytes", "modified", "mark", "by", "byId", "color" }] }`, oldest first. `by` is the iPad's current name. Empty list if the folder doesn't exist yet. Photos put in the folder by hand show up too, without `mark`/`by`. |
| POST | `/api/photos/{job}/{load}` | `multipart/form-data`: one or more `file` fields, optional `mark` field (the piece the photos are of; left out = the whole load). Needs `X-Terra-Device`. 201 `{ "job", "load", "saved": [{ "name", "bytes" }] }`. |
| GET | `/api/photos/{job}/{load}/{name}` | The photo itself, with its type (`image/jpeg`...). `{name}` is a name from the list. |
| DELETE | `/api/photos/{job}/{load}/{name}` | Deletes it: the file is **moved** to a `.deleted` folder inside the load folder, never erased. `{ "job", "load", "deleted", "movedTo": ".deleted\\<name>" }`. |

`{job}` is the PowerFab job number and `{load}` the load number (`8`), URL-encoded. Status codes:
400 bad name or no files, 401 bad key, 403 photos off, 404 no such file, 413 too big or too many,
415 type not taken, 500 rules file wrong.

Every upload and delete is written to the activity history (who, which load, which piece) and sent
live to the other iPads and the admin page, which refresh their photo lists.

**Deleting.** The app deletes a photo only after a 1.5 second hold on its Delete button. On the server
it lands in `<load folder>\.deleted\` (a second one with the same name gets ` (2)`), which the list,
view and delete routes never show. To undo, move it back out of `.deleted`. To free the space, empty
the `.deleted` folders by hand now and then, for example
`Get-ChildItem D:\TerraPhotos -Recurse -Directory -Filter .deleted -Force | Remove-Item -Recurse -Force`.

```bash
curl -H "Authorization: Bearer $ACCESS_KEY" -H "X-Terra-Device: test-device-0001" \
  -F file=@photo.jpg -F mark=1001-C1 http://127.0.0.1:8095/api/photos/EPM-001/8
```

```powershell
$h = @{ Authorization = "Bearer $key" }
Invoke-RestMethod http://127.0.0.1:8095/api/photos -Headers $h
Invoke-RestMethod http://127.0.0.1:8095/api/photos/EPM-001/8 -Headers $h
Invoke-WebRequest "http://127.0.0.1:8095/api/photos/EPM-001/8/20261028-143502_load_Dock%20iPad.jpg" -Headers $h -OutFile photo.jpg
```

## Setting it up on the server (checklist)

1. Pick the base folder and create it (`D:\TerraPhotos`). Make sure the service's account can write
   there and to every folder a rule points at.
2. Add `PHOTOS_ENABLED`, `PHOTOS_ROOT` (and any limits) to `local.settings.json`.
3. Optional: copy `photo-rules.example.json` to `photo-rules.json` in the install folder and write the
   rules. `deploy.ps1` never overwrites `photo-rules.json` or `local.settings.json`.
4. `Restart-Service TerraTeklaServer`. The admin page's home shows "Photos: on".
5. Check a job's folder: `GET /api/photos/<job>/1` must answer 200.
6. On each iPad: Server settings > turn on "Send photos to the server".

Photos taken in the app wait on the iPad until they are sent, so a dropped connection doesn't lose
them; the app retries when it is back online.
