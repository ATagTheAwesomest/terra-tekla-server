# TERRA server: setup and reference

terra-tekla-server is the shop's own server for the TERRA iPad app (terra-tekla-client). It:

- **hosts the iPad app**, so it no longer needs GitHub Pages: the iPads open this server's address;
- **keeps everything PowerFab doesn't**: stacks, which iPad scanned each piece, the truck check,
  device names and colours, the activity history and photos, in one SQLite database;
- **syncs the iPads live**: a scan, move or rename on one iPad shows on the others at once
  (WebSocket);
- **passes PowerFab calls through** to tekla-conversion-layer, adding the conversion layer's key so
  it never has to be on an iPad;
- **has an admin board** at `/admin/`, installable as an app on a laptop or iPad: devices (rename,
  colour), what each one did, the PowerFab jobs it can reach, loads with their stacks, photos,
  server stats and health, settings (these settings, photo folders, admin code, backup, restart,
  updates) and these docs, all behind the access key and an admin code (see "Admin board" below);
- **updates itself and the iPad app** when asked from the admin board or an iPad
  (`deploy.ps1`, `update-client.ps1`).

This file ships next to `TerraTeklaServer.exe` as `SETUP.md` and is shown on the admin page
(Docs > Setup), so it always matches the running build. Photos have their own guide, `PHOTOS.md`.

```
 iPad (TERRA app, Safari / home screen)          admin page (any browser)
        │  HTTPS: app files, /api/*, /ws (live)        │
        ▼                                              ▼
 terra-tekla-server  (this, Windows service, port 8095)
   ├─ data\terra.db       SQLite: stacks, devices, history, truck checks, photo records
   ├─ CLIENT_ROOT         a git clone of terra-tekla-client, served as the app
   ├─ PHOTOS_ROOT         photo folders (PHOTOS.md)
   └─ /api/powerfab/* ──► tekla-conversion-layer (PowerFab REST converter, port 8090) ──► PowerFab
```

PowerFab stays the source of truth for what is loaded, shipped and returned. The server only adds
what PowerFab has no place for. When the two disagree (someone loads or unloads a piece in PowerFab
itself), the app's next read brings the stacks in line: new pieces go into a stack at the bottom,
pieces gone from the load leave their stack.

## Settings

Read at startup from `local.settings.json` next to the exe (the Windows service), or environment
variables with the same names. Restart the service after changing them
(`Restart-Service TerraTeklaServer`). `local.settings.example.json` next to the exe is a template.

| Key | Default | Meaning |
|---|---|---|
| `Urls` | `http://localhost:5000` | Where the server listens. `http://0.0.0.0:8095` takes connections from the network on port 8095. |
| `ACCESS_KEY` | empty | The key every iPad and the admin page must give (`Authorization: Bearer <key>`, and in the live connection's hello). Empty = no key, anyone who can reach the server can use it; only for testing. Make one with `[Convert]::ToBase64String((1..32 \| % { Get-Random -Max 256 }) -as [byte[]])`. |
| `CONVERSION_URL` | empty | tekla-conversion-layer's address as this server reaches it, e.g. `http://127.0.0.1:8090` when both run on the same computer. Without it, scans and reads answer 502. |
| `CONVERSION_KEY` | empty | The conversion layer's `API_TOKEN`. Only this server holds it; the iPads never see it. |
| `CLIENT_ROOT` | empty | Folder with the iPad app: a git clone of terra-tekla-client (the folder with `index.html`). Relative paths are next to the exe. Without it `/` shows a short "not set up" page. |
| `DATA_DIR` | `data` | Folder for `terra.db`. Relative paths are next to the exe. Back this folder up. |
| `CORS_ORIGINS` | empty | Other web addresses allowed to call the API from a browser, comma separated (e.g. `http://localhost:8080` while working on the app). Not needed when the app is served by this server. |
| `PHOTOS_*` | | See `PHOTOS.md`. |
| `ADMIN_LOCK_MINUTES` | `30` | After the admin code is entered, that device (from that IP address) stays unlocked this many minutes. Takes effect without a restart. |
| `SERVER_REPO` | empty | The terra-tekla-server git clone (e.g. `C:\src\terra-tekla-server`). Needed for **Update the server** from the admin board or an iPad, which runs its `windows\deploy.ps1`. Takes effect without a restart. |
| `ADMIN_PIN_HASH` | none | The admin code, as a salted PBKDF2 hash. Written by the admin page; never type a code here. Remove the line and restart to forget the code (the next visit asks for a new one). |

Most of these can also be changed on the admin board (Settings), which writes this same file (the
old one is kept as `local.settings.json.bak`) and refuses a value that would stop the server from
starting. `DATA_DIR` is only changed here, by hand.

Logging is in `appsettings.json` next to the exe (`Logging:LogLevel`). As a Windows service the logs
go to the Windows Event Log (Application, source TerraTeklaServer).

## Admin board

`https://<server>/admin/` (or `http://<server>:8095/admin/`). In the iPad app it is
**Server settings / Admin > Open admin board**.

It needs the same **access key** the iPads use (asked once per browser; opened from the iPad app,
the app's key is used) and the **admin code** for everything on it.

**As an app**: it has its own manifest and service worker, so Chrome or Edge on a laptop offers
"Install TERRA admin" (address bar icon, or menu > Save and share > Install), and Safari on an iPad
can Share > Add to Home Screen. Like the iPad app, that needs the HTTPS address. The installed app
still asks the server for everything: key and code included.

- **Devices & activity**: every iPad, live, and what each did; rename and recolour them.
- **Jobs**: every job PowerFab gives the server's PowerFab user (the list the iPads pick from), with
  the loads and pieces this server has for each and the folder its photos go in. Filterable.
- **Photos**: the latest photos from every iPad.
- **Server**: whether everything is working (PowerFab bridge, app served, access key, photo rules,
  disk space), uptime, memory, connected iPads, scans and refusals per day for 14 days, requests and
  errors per minute for the last hour, PowerFab call times, each iPad's scans this week, disk space,
  and the server's last warnings and errors. Updates every 10 seconds.
- **Settings**: the settings above, grouped, with a Test button for the conversion layer's address;
  the **photo folder rules** (add, reorder, remove, try a job number, save: takes effect at once);
  **Updates** (below); change the admin code; lock now; download a database backup; restart.
- **Docs**: this guide and the photo guide.

### The admin code

4 to 8 digits, entered on an on-screen keypad (the iPad app's 9-pin keypad, so an iPad's keyboard
never comes up; a desk keyboard's digits work too). The same code opens the admin board and the
admin part of the iPad app's **Server settings / Admin**.

- The first time it is needed (on the admin board or an iPad), it asks to **set** a code (twice).
  After that it can only be changed with the current one (admin board > Settings); changing it
  locks every other device out.
- Entering it unlocks **that device, from that IP address, for 30 minutes** (`ADMIN_LOCK_MINUTES`),
  then it locks again. The iPad app and the admin board share the unlock on the same iPad. Lock now
  ends it early; a server restart ends every unlock.
- The server keeps only a salted PBKDF2 hash (`ADMIN_PIN_HASH`), and checks the code itself on every
  `/api/admin/*` route: hiding the page isn't what protects them. (The device, activity, job and
  photo lists the board shows are the same routes the iPads use, so those need only the access key.)
- Behind a Cloudflare Tunnel every request reaches the server from 127.0.0.1; the visitor's own
  address is then taken from `CF-Connecting-IP` (or `X-Forwarded-For`), which the server only
  believes from 127.0.0.1.
- 5 wrong codes in a row block every try for 30 s, then 1 min, 2 min... up to 15 min.
- Forgot it: on the server, remove the `ADMIN_PIN_HASH` line from `local.settings.json` and restart
  the service.

### Server settings on the iPads

The iPad app has one entry for all of it, **Server settings / Admin** (side menu, and the home
screen tile before the first setup). In it:

- **This iPad**: its name and colour, sounds, sending photos. No code needed.
- **Admin** (admin code): open the admin board, change the server connection (address, access key,
  setup QR), update the app on the server, update the server.

The server connection is open until it is first saved; after that it needs the code. If the server
can't be reached at all (or refuses the saved key), the code can't be checked, so the connection
opens without it to be fixed.

### Updates (from the admin board or an iPad)

- **Update the app on the server** runs `update-client.ps1` (next to the exe): it gets the newest
  terra-tekla-client into `CLIENT_ROOT` (git pull; first time git clone; without git, a zip from
  GitHub). No restart. The output shows on the screen; when the version changed every iPad is told,
  and gets it with Update in its menu. The iPad app's Update dialog has **Get newest from GitHub
  first**, which does this and then reloads that iPad.
- **Update the server** runs `windows\deploy.ps1` from `SERVER_REPO`: pull, build, stop the service,
  copy, start, then `update-client.ps1`. The server stops half way, so it runs on its own and writes
  to `DATA_DIR\deploy-last.log`; Settings > Updates shows that log once the server is back. Needs
  the .NET SDK on the server (as for a deploy by hand) and the service running.

Both run as the service's Windows user (LocalSystem unless changed). git then needs to reach the
repos without a sign-in: fine for public repos; for private ones, set the service to log on as a
user whose git can pull (services.msc > TERRA Server > Log On), or keep updating by hand. git's
"dubious ownership" check is turned off for these runs, since that user didn't make the clones.

### Restart from the admin page

Most settings take effect after a restart; the page says which saved ones are waiting. **Restart
server** ends the process with an error code so Windows starts it again by the service's recovery
setting ("restart after a crash", which `install-service.ps1` sets). It only works when running as
that service; the iPads reconnect by themselves a few seconds later.

### Admin routes

All need the access key and, except the first four, `X-Terra-Admin: <token>` from the same IP
address that got it.

| Route | |
|---|---|
| `GET /api/admin/lock` | Whether a code is set, whether this token is unlocked, seconds until tries are taken again. |
| `POST /api/admin/setup` `{"pin"}` | Sets the first code (409 once one is set). Answers `{"token","until"}`. |
| `POST /api/admin/unlock` `{"pin"}` | Answers `{"token","until"}`; 403 wrong code, 429 blocked. |
| `POST /api/admin/lock` | Ends this token. |
| `PUT /api/admin/pin` `{"current","pin"}` | Changes the code. |
| `GET/PUT /api/admin/settings` | The editable settings / save some (`{"KEY": "value"}`; a secret left out or null is kept). |
| `POST /api/admin/test-conversion` `{"url"}` | Whether that address answers like the conversion layer. |
| `GET/PUT /api/admin/photo-rules`, `POST /api/admin/photo-rules/test` | The photo folder rules; where a job's photos would go. |
| `GET /api/admin/stats` | Everything the Server view shows. |
| `GET /api/admin/backup` | A copy of `terra.db`. |
| `POST /api/admin/restart` | See above. |
| `GET /api/admin/update` | Versions, whether updates can run here, the last app update's output, the last server update's log. |
| `POST /api/admin/update/client` | Starts `update-client.ps1` (202; follow it with `GET /api/admin/update`). |
| `POST /api/admin/update/server` | Starts `deploy.ps1` (202); the server restarts. |

`GET /api/jobs/powerfab` (access key only) is the Jobs list.

## Installing on Windows (first time)

Needs: Windows 10/11 or Server 2019+, git, and either the .NET 10 SDK (to build on the server) or a
release zip built elsewhere with `windows\build-release.ps1`.

1. **Get the code.** `git clone https://github.com/ATagTheAwesomest/terra-tekla-server C:\src\terra-tekla-server`
2. **Get the iPad app.** `git clone https://github.com/ATagTheAwesomest/terra-tekla-client C:\TerraTeklaClient`
3. **Build and copy.** From `C:\src\terra-tekla-server`:
   `dotnet publish src\TerraTeklaServer -c Release -o C:\TerraTeklaServer`
   (or unzip the release zip into `C:\TerraTeklaServer`).
4. **Settings.** In `C:\TerraTeklaServer`, copy `local.settings.example.json` to `local.settings.json`
   and fill in `ACCESS_KEY`, `CONVERSION_URL`, `CONVERSION_KEY` and `CLIENT_ROOT`.
5. **Service.** Elevated PowerShell: `C:\src\terra-tekla-server\windows\install-service.ps1` copied
   into `C:\TerraTeklaServer` and run there (it installs the `TerraTeklaServer` service, sets it to
   restart after a crash, opens the port in the firewall for domain/private networks and starts it).
6. **Check.** `Invoke-RestMethod http://127.0.0.1:8095/health` answers `status: ok`. Open
   `http://<server>:8095/admin/`, enter the access key: Status shows PowerFab bridge "ok" and the
   iPad app version.
7. **HTTPS** (next section), then set up the iPads (the section after).

### HTTPS (needed for the iPads)

Safari only runs the app's offline cache and "Add to Home Screen" app mode from an `https://` address
(or `localhost`). Pick one:

- **Cloudflare Tunnel** (what the conversion layer used): point the tunnel's public hostname at
  `http://localhost:8095` instead of the conversion layer. WebSockets go through Cloudflare tunnels
  with no extra setting. The conversion layer then needs no tunnel at all: only this server talks to
  it, on `127.0.0.1`. Cloudflare Access can sit in front as before.
- **A certificate on this server**: give Kestrel a certificate in `local.settings.json`, e.g.
  ```json
  "Urls": "https://0.0.0.0:8095",
  "Kestrel": { "Certificates": { "Default": { "Path": "C:\\TerraTeklaServer\\terra.pfx", "Password": "..." } } }
  ```
  The certificate must be for the name the iPads use and trusted by them (a public certificate, or
  your own CA installed and trusted on each iPad under Settings > General > About > Certificate Trust
  Settings).

Over plain `http://` on the network the app still works while it is open, but it can't be used
offline or installed to the home screen.

### Setting up an iPad

1. Open the server's address in Safari (`https://terra.example.com/`). The app loads from the server.
2. Menu > Server settings: leave **Server address** blank (it means "this server"), enter the
   **Access key**, Save. Or scan a setup code made on another iPad's Server settings.
3. In the same dialog, set this iPad's **name** and **colour**. They show on everything it scans, on
   its photos and in the history. They can also be changed from the admin page.
4. Share > Add to Home Screen.

An iPad that used the GitHub Pages copy keeps working against this server if its Server address is
set to this server, but its stacks start fresh from the server (stacks kept only on that iPad before
are not copied). Open the server's own address instead and add that to the home screen.

## Updating

From the admin board or an iPad (**Updates**, above), or by hand from the repo, elevated
PowerShell: `.\windows\deploy.ps1`. It:

1. `git pull --ff-only` this repo, then runs `update-client.ps1` (beside it) for the iPad app in
   `CLIENT_ROOT` (skip the app with `-SkipClient`, both pulls with `-SkipPull`);
2. builds, stops the service, copies the build over `C:\TerraTeklaServer` (never touching
   `local.settings.json`, `photo-rules.json` or `data\`), starts it and checks `/health`.

The iPad app alone: `C:\TerraTeklaServer\update-client.ps1` (or the button). No restart; the iPads
get it with Update in their menu or the next time the app is opened (the app's version shows in its
menu and on the admin board). `deploy.ps1 -InstallDir D:\Other -ServiceName Other` for a non-default install.

The database schema is created and upgraded by the server itself at startup.

## Backups

Everything the server knows is in `DATA_DIR\terra.db` (plus the photo folders). Copy it while the
service is stopped, or live with `sqlite3 terra.db ".backup terra-backup.db"`. Losing it loses the
stacks, names and history, not PowerFab data: the next read of each load rebuilds its pieces into one
stack per load.

## How the iPads stay in sync

- Each iPad makes up a device id once (kept in the app's storage) and sends it on every request
  (`X-Terra-Device`). The first time the server sees an id it creates the device as "iPad N" with
  the next colour.
- Every change to a load's stacks is an **operation** (below) the app applies on screen at once, then
  sends to `POST /api/loads/{loadId}/ops`. The server applies it to its copy, saves it, answers the
  load as it now is, and sends it to every connected iPad over `/ws`. The server's copy wins.
- Operations name stacks and rows by ids the app makes up, so two iPads working the same load at the
  same time don't overwrite each other. Each has an `opId`; one the server already applied is skipped,
  so the app can safely resend.
- If the server can't be reached, operations wait in order on the iPad and are sent when it is back
  (the status bar says how many are waiting). Scanning itself needs the server, because PowerFab is
  reached through it. A solo truck check works offline.
- A scan shows on every iPad straight away as "waiting on PowerFab"; when PowerFab answers it becomes
  a normal row (or disappears, with the refusal in the history, if PowerFab said no).
- A read of a load from PowerFab (`sync` operation) carries the load's revision from before the read
  started. Rows changed after that, scans still waiting on PowerFab (up to 2 minutes) and rows being
  taken off are left alone, and pieces taken off in the last 10 minutes aren't put back, so a slow
  read on one iPad can't undo what another just did.

### Truck check: shared or solo

Each iPad picks in the Truck check screen. **Shared** (the default) keeps the check on the server: every
iPad on that load sees and adds to the same check, each scan tagged with who made it. **Solo** keeps a
private check on that iPad only (also works offline). Switching doesn't erase either.

## API

Every `/api` route needs `Authorization: Bearer <ACCESS_KEY>`. Routes that change something or name
the iPad also need `X-Terra-Device: <device id>` (8 to 64 letters, digits and `-`); the app sends it,
plus `X-Terra-Version`. Errors are JSON problem details with `detail` saying why.

| Method | Path | What |
|---|---|---|
| GET | `/health` | No key. `{ "status": "ok", "version" }`. |
| GET | `/api/status` | Server version, whether a key is set, the iPad app folder and version, photos on/off, the conversion layer's health, the database path. |
| POST | `/api/powerfab/{command}` | Sent on to the conversion layer's `/api/powerfab/{command}` with `CONVERSION_KEY`; its answer comes back as it is. The conversion layer refusing that key answers 502. |
| GET | `/api/me` | This device (needs `X-Terra-Device`): `{ id, name, color, createdAt, lastSeenAt, ... }`. |
| PUT | `/api/me` | `{ "name"?, "color"? }`: rename / recolour this device. Name 1-40 characters, colour `#rrggbb`. |
| GET | `/api/devices` | Every device, with `online` and `onlineSince`. |
| PUT | `/api/devices/{id}` | Same as `PUT /api/me` for any device (the admin page). |
| GET | `/api/jobs` | Jobs the server has stacks for: `[{ job, loads, pieces, lastAt }]`. |
| GET | `/api/jobs/{job}/loads` | Every load of a job the server has, as load states (below). |
| GET | `/api/loads/{loadId}?job=&number=` | One load's state. A load not seen before answers an empty state with one stack (not saved until changed). |
| POST | `/api/loads/{loadId}/ops` | Apply one operation. Body: `{ "op", "opId", "job", "loadNumber", ...fields }`. Answers `{ state, changed }`. 400 with the reason if it can't be applied. |
| GET | `/api/activity?device=&job=&load=&mark=&before=&limit=` | History, newest first. `load` is the load id; `before` an activity id for paging; `limit` up to 500 (default 100). |
| GET | `/api/docs/setup`, `/api/docs/photos` | This file and `PHOTOS.md`, as Markdown. |
| | `/api/photos...` | See `PHOTOS.md`. |

`{loadId}` is PowerFab's load id (`LoadID`), not the load number.

A **load state**:

```json
{
  "loadId": "5012", "job": "EPM-001", "loadNumber": "8", "rev": 412, "updatedAt": "2026-10-30T14:02:11.000Z",
  "stacks": [
    { "id": "first-5012", "name": "Stack 8 - 1", "createdBy": null,
      "items": [
        { "id": "e-lk3j2...", "mark": "1001-C1", "sequence": "1", "manual": false, "pending": false,
          "removing": false, "scannedAt": "...", "scannedBy": "<device id>", "addedAt": "...", "rev": 410,
          "...": "PowerFab details of the piece (description, weight, length...) as the app sent them" }
      ] }
  ],
  "truckCheck": { "startedAt": "...", "updatedAt": "...",
                  "scans": [{ "id": "...", "mark": "1001-C1", "manual": false, "scannedAt": "...", "scannedBy": "<device id>" }] },
  "recentOps": ["..."]
}
```

### Operations

All carry `op`, `opId` (unique, made by the app), `job` and `loadNumber`. Ids (`stackId`, `entryId`,
`scanId`) are made by the app.

| `op` | Fields | Does |
|---|---|---|
| `addStack` | `stackId`, `name`?, `afterStackId`? | New stack after the given one (or last). A missing or taken name becomes the next "Stack {load} - N". |
| `rename` | `stackId`, `name` | Renames a stack (spaces squeezed, up to 60 characters). |
| `removeStack` | `stackId` | Deletes the stack. The app takes its pieces off the load in PowerFab first. |
| `moveEntryTo` | `entryId`, `stackId`, `index` | Moves a row to position `index` of a stack (counted before the move). |
| `beginScan` | `entryId`, `mark`, `manual`, `stackId`? | A scan started: a "waiting on PowerFab" row in that stack (or the last). |
| `finishScan` | `entryId`, `mark`, `manual`, `stackId`?, `piece` | PowerFab assigned and loaded it: the row gets `piece`'s details and sequence. |
| `failScan` | `entryId`, `mark`, `title`?, `message`? | PowerFab refused it: the row goes, the refusal goes in the history. |
| `refused` | `mark`, `title`?, `message`? | History only: a scan refused before it got a row (too many, not on the job, shipped load...). |
| `markRemoving` / `unmarkRemoving` | `entryId` | A row is being taken off in PowerFab / that failed. |
| `removeEntry` | `entryId` | PowerFab took it off the load: the row goes. |
| `sync` | `pieces: [{ mark, sequence, n, ...details }]`, `knownRev` | Brings the stacks in line with what PowerFab has loaded (see sync above). |
| `startCheck` / `resetCheck` | | Starts / clears the shared truck check. |
| `addCheck` | `scanId`, `mark`, `manual` | A piece checked on the truck. |
| `removeCheck` | `scanId` | Undo one check. |
| `log` | `kind`, `text`, `mark`? | History only, for things done in PowerFab: `ship`, `unship`, `return`, `unreturn`, `load-create`, `check-complete`. |
| `clear` | | The load was deleted in PowerFab: back to one empty stack. History stays. |

### Live connection (`/ws`)

A WebSocket. The first message must be the hello, within 10 seconds:

```json
{ "type": "hello", "key": "<ACCESS_KEY>", "deviceId": "<device id>", "kind": "ipad", "version": "0.13.0" }
```

`kind` is `"ipad"` or `"admin"` (the admin page, which doesn't count as a device). A wrong key closes
the socket with code 4401. Then the server sends:

| `type` | Fields | When |
|---|---|---|
| `welcome` | `device`, `devices`, `serverVersion`, `photosEnabled` | Right after the hello. |
| `load` | `state`, `by` (device id), `op` | A load changed. |
| `activity` | `items` | New history lines. |
| `devices` | `devices` | A device was added, renamed, recoloured, came online or went offline. |
| `photos` | `job`, `load` | A load's photos changed. |
| `pong` | `at` | Answer to `{ "type": "ping" }`. |

The app reconnects by itself (backing off up to 30 seconds) and, on reconnect, sends anything waiting
and re-reads the open load.

### History kinds

`scan`, `refused`, `unload`, `sync-remove` (a piece PowerFab no longer has on the load),
`auto-stack` (a piece loaded in PowerFab itself), `move`, `stack-add`, `stack-rename`,
`stack-delete`, `check`, `check-undo`, `check-reset`, `check-complete`, `ship`, `unship`, `return`,
`unreturn`, `load-create`, `load-delete`, `photo`, `photo-delete`, `device-rename`.

## Database

SQLite, `DATA_DIR\terra.db`. Created and kept up to date by the server; don't edit it while the service
is running. Times are UTC ISO-8601 text.

| Table | Columns | Holds |
|---|---|---|
| `meta` | `key`, `value` | `rev`: the last revision handed out. |
| `devices` | `id`, `name`, `color`, `created_at`, `last_seen_at`, `last_ip`, `user_agent`, `app_version` | Every iPad seen. |
| `loads` | `load_id`, `job`, `load_number`, `rev`, `updated_at`, `check_started_at`, `check_updated_at`, `recent_ops` (JSON), `removed` (JSON) | One row per load with stacks. |
| `stacks` | `load_id`, `id`, `position`, `name`, `created_by` | Stacks in order. |
| `entries` | `load_id`, `id`, `stack_id`, `position`, `mark`, `sequence`, `manual`, `pending`, `removing`, `scanned_at`, `scanned_by`, `added_at`, `rev`, `details` (JSON) | Rows (pieces) in each stack. |
| `check_scans` | `load_id`, `id`, `position`, `mark`, `manual`, `scanned_at`, `scanned_by` | Shared truck check scans. |
| `activity` | `id`, `at`, `device_id`, `device_name`, `kind`, `job`, `load_id`, `load_number`, `stack_id`, `stack_name`, `mark`, `text` | The history. `device_name` is the name at the time. |
| `photos` | `id`, `job`, `load_number`, `file_name`, `mark`, `device_id`, `device_name`, `bytes`, `taken_at`, `deleted_at` | A record of each photo saved. |

Handy queries (`sqlite3 terra.db`):

```sql
-- Who scanned what today
SELECT a.at, d.name, a.job, a.load_number, a.mark FROM activity a LEFT JOIN devices d ON d.id = a.device_id
WHERE a.kind = 'scan' AND a.at >= date('now') ORDER BY a.id;
-- Pieces per stack on a load
SELECT s.name, COUNT(e.id) FROM stacks s LEFT JOIN entries e ON e.load_id = s.load_id AND e.stack_id = s.id
WHERE s.load_id = '5012' GROUP BY s.id ORDER BY s.position;
```

## Testing without PowerFab

`tools/fake-powerfab.mjs` is a stand-in for tekla-conversion-layer with one made-up job, kept in
memory (Node 18+):

```bash
node tools/fake-powerfab.mjs            # http://127.0.0.1:8099, key "fake-key"
ACCESS_KEY=test-key CONVERSION_URL=http://127.0.0.1:8099 CONVERSION_KEY=fake-key \
CLIENT_ROOT=../terra-tekla-client DATA_DIR=/tmp/terra-data Urls=http://127.0.0.1:8095 \
  dotnet run --project src/TerraTeklaServer
```

Then open `http://127.0.0.1:8095/` in two browser windows (one private, so they are two devices) and
job `EPM-001`. `node tools/fake-powerfab.mjs <port> <key>` changes the fake's port and key, and `FAKE_DELAY_MS`
(default 120) its answer delay. `dotnet test` runs the server's tests.

**Never point a test at the real PowerFab or conversion layer.** Every write is real.

## Setup checklist (for a person or an AI assistant)

1. tekla-conversion-layer is installed, healthy at its own `/health`, and its `API_TOKEN` is known.
2. `C:\TerraTeklaClient` is a git clone of terra-tekla-client.
3. `C:\TerraTeklaServer` has the build, `local.settings.json` with `Urls`, `ACCESS_KEY`,
   `CONVERSION_URL`, `CONVERSION_KEY`, `CLIENT_ROOT` (and `PHOTOS_*` if photos are wanted).
4. The `TerraTeklaServer` service is installed and running; `/health` answers.
5. The admin page's Status shows PowerFab bridge ok, the app served and its version.
6. HTTPS reaches the server (tunnel or certificate); the WebSocket connects through it (the admin
   page's dot is green).
7. Each iPad opens the HTTPS address, has the access key, a name and colour, and is on the home screen.
8. Two iPads on the same load: a scan on one shows on the other within a second.
