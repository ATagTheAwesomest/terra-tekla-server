# Moving from the old setup to terra-tekla-server

The old setup put **tekla-conversion-layer on the internet** and the **iPad app on GitHub Pages**.
The new one puts **terra-tekla-server** on the internet instead. It serves the iPad app, the admin
board and the live sync itself, and is the only thing that talks to the conversion layer.

```
OLD   iPad (github.io app) ──HTTPS──► Cloudflare Tunnel ──► conversion layer :8090 ──► PowerFab
                                                              └─ photos (FILES_*)

NEW   iPad app  (https://<host>/)        ┐
      admin board (https://<host>/admin/) ├─HTTPS──► Cloudflare Tunnel ──► terra-tekla-server :8095
      live sync (wss://<host>/ws)        ┘                                  ├─ data\terra.db
                                                                            ├─ CLIENT_ROOT (the app)
                                                                            ├─ PHOTOS_ROOT (photos)
                                                                            └─► conversion layer 127.0.0.1:8090 ──► PowerFab
```

The conversion layer **stays installed and running**. It just stops being reachable from outside:
only terra-tekla-server calls it, on the same computer, with its key.

## The short version

1. Pick a quiet time (end of shift). Ship or finish the loads you can: stacks kept on the iPads don't
   carry over (see "What happens to the data").
2. On the server, in an **elevated** PowerShell:
   ```powershell
   git clone https://github.com/ATagTheAwesomest/terra-tekla-server C:\src\terra-tekla-server   # first time only
   cd C:\src\terra-tekla-server
   git pull
   .\windows\migrate-from-conversion-layer.ps1 -PublicHostname <your hostname>
   ```
   It asks before every change. Answer the questions; at the end it prints the new **access key**
   and a **setup code** for the iPads, and a list of anything left to do by hand.
3. If your tunnel is managed in the Cloudflare dashboard, repoint the hostname there (below). The
   script tells you if so.
4. Set up each iPad and the admin board (below).
5. From then on, updates are `.\windows\deploy.ps1` or the admin board's **Updates**, as before.

Running the script again is safe: each step checks first and skips what's done.

## What the script does

| Step | What | Undo |
|---|---|---|
| 1. Checks | Elevated, git, .NET 10 SDK, the conversion layer's service and `local.settings.json`, whether it answers. | |
| 2. Backups | Copies every file it might change to `C:\TerraMigrationBackup\<time>\`: the conversion layer's settings and `file-rules.json`, the new server's settings, rules and database (if installed), and the tunnel's `config.yml` before editing it. | `rollback.ps1` in that folder |
| 3. Code | Pulls this repo; clones terra-tekla-client to `C:\TerraTeklaClient` (`CLIENT_ROOT`) if it isn't there. | |
| 4. Settings | Writes `C:\TerraTeklaServer\local.settings.json`: `CONVERSION_URL` = `http://127.0.0.1:8090`, `CONVERSION_KEY` = the conversion layer's `API_TOKEN`, `CLIENT_ROOT`, `SERVER_REPO`, a new random `ACCESS_KEY`, and the photo settings carried over from the conversion layer's old `FILES_*` lines. An existing file is kept: only empty keys are filled. Copies `file-rules.json` to `photo-rules.json` (same format). | backup |
| 5. Service | First time: builds into `C:\TerraTeklaServer` and runs `install-service.ps1` (service `TerraTeklaServer`, port 8095, restart on crash, firewall rule for private networks). Already installed: runs `deploy.ps1`. | `Stop-Service TerraTeklaServer` |
| 6. Conversion layer | If it listens on anything but `127.0.0.1`, changes its `Urls` to `http://127.0.0.1:8090` and restarts it. | backup |
| 7. Tunnel | Finds the `cloudflared` service. Locally managed (`config.yml`): repoints the rule that went to `:8090` to `http://localhost:8095`, validates the file (puts the old one back if invalid) and restarts cloudflared. Dashboard managed (token): prints what to click. | backup |
| 8. Checks | Locally and (with `-PublicHostname`) over the public address: the app at `/`, the admin board at `/admin/`, `/api/status` with the new key (conversion layer reachable), a read-only PowerFab call through the new server (proves `CONVERSION_KEY`), and the live connection `/ws` over the public address. Flags a Cloudflare Access sign-in page in the way. | |

Options: `-InstallDir`, `-ServiceName`, `-ClientRoot`, `-Port` (8095), `-OldInstallDir`
(`C:\TeklaConversionLayer`), `-OldServiceName`, `-BackupRoot`, `-SkipTunnel`, `-Yes` (answer yes
to every question).

Nothing is deleted. The keys are never printed except the new access key at the very end, and the
tunnel token in the cloudflared service is never printed.

## Cloudflare

### Public hostnames: one is enough

| Public hostname | Path | Service |
|---|---|---|
| `<your hostname>` (the one the conversion layer used, or a new one) | *(leave empty)* | `HTTP` → `localhost:8095` |

That one hostname carries everything, on these paths:

| Path | What |
|---|---|
| `/` (and `/index.html`, `/js/*`, `/css/*`, `/icons/*`, `/sw.js`, `/version.js`, `/manifest.webmanifest`) | The iPad app (PWA) |
| `/admin/` (and everything under it, its own `manifest.webmanifest` and `sw.js`) | The admin board (PWA) |
| `/api/*` | Everything the app and admin board ask for (stacks, devices, photos, PowerFab, `/api/admin/*`) |
| `/ws` | Live sync (WebSocket) |
| `/health` | Up check, no key |

**Leave Path empty** so all of them go through; a path-limited route (say only `/api`) breaks the
app. Don't point any hostname at `:8090` (the conversion layer) any more.

**Keep the admin board on the same hostname** as the app (`https://<host>/admin/`), not a hostname
of its own. On one hostname, an iPad's app and admin board share the access key and the admin-code
unlock, and the app's "Open admin board" button just works. A separate hostname also works (point
it at the same `localhost:8095`), but then the key and code are asked separately there.

**Reusing the old hostname** is simplest: the script (or you, in the dashboard) only changes the
port from 8090 to 8095. A new hostname is fine too: add it, move the iPads, then delete the old
one.

**Dashboard-managed tunnel**: Cloudflare One (one.dash.cloudflare.com) > **Networks > Tunnels** >
your tunnel > **Edit** > **Published application routes** (older dashboards: **Public Hostname**) >
the route for your hostname > **Edit** > Service **Type** `HTTP`, **URL** `localhost:8095` > Save.
Takes effect in seconds.

**Locally managed tunnel** (`config.yml`), what it ends up as:

```yaml
tunnel: <tunnel id>
credentials-file: C:\...\<tunnel id>.json
ingress:
  - hostname: <your hostname>
    service: http://localhost:8095
  - service: http_status:404
```

### Cloudflare settings for that hostname

- **WebSockets**: on (dashboard > your domain > Network > WebSockets; on by default). Live sync
  needs it. The script's `/ws` check fails without it.
- **Caching**: add a **Cache Rule**: *Hostname equals `<your hostname>`* → **Bypass cache**. The
  server already sends `Cache-Control: no-cache`, but this makes sure an update (and the app's version
  check) is never answered from Cloudflare's cache.
- **Rocket Loader**: off for this hostname (Speed settings, or a Configuration Rule). It rewrites
  script tags, and the app and admin board are ES modules.
- **Security challenges**: "I'm Under Attack", Bot Fight Mode or a WAF challenge would answer the
  app's background requests with a challenge page the app can't show. If you use any of them, add a
  WAF **Skip** rule for this hostname.
- **Upload size**: photos go up shrunk, well under Cloudflare's 100 MB request limit.

### Cloudflare Access: leave it off this hostname

Recommended: **no Access application on this hostname.** The server already protects itself:

- every `/api` call and the `/ws` hello need the **access key**;
- everything in the admin board's admin part (`/api/admin/*`) also needs the **admin code**, checked
  by the server, locked to that device's IP for 30 minutes, with growing lockouts after 5 wrong
  tries. Behind the tunnel the server reads the real IP from `CF-Connecting-IP`.

Why Access hurts here: an iPad home-screen app has its own cookies, separate from Safari. When the
Access sign-in expires, the app's background requests, its service worker and the live connection
all get Access's sign-in page instead of answers. Scans then fail with confusing errors and the
waiting queue stalls, and the sign-in page can't show inside those requests. Signing in with an
email code on a shop-floor iPad, in gloves, is no fun either.

If you want Access anyway, pick one:

- **Access on the admin board's page only**: an Access application for `<host>` with path `admin`.
  It guards the admin board's page (sign in on a laptop or iPad now and then). The iPads' app, `/api`
  and `/ws` stay outside it, and the admin data stays behind the key and code.
- **Bypass from the shop**: an Access application for the whole hostname with a **Bypass** policy
  for the shop's public IP address(es) first, then an Allow policy (emails) for everyone else. iPads
  on the shop Wi-Fi never see it. An iPad on cellular would.

If the old conversion layer hostname had an Access application or a service token, check it doesn't
now sit in front of the new server: the script's public checks say "Cloudflare Access sign-in page"
when it does.

## The iPads

Each iPad moves from the GitHub Pages address to the server's own address. That's a new web address
to Safari, so it starts with empty settings.

1. Safari: `https://<your hostname>/`. The app loads from the server.
2. Menu > **Server settings / Admin**: leave **Server address** blank (blank = this server), enter
   the **access key**, or type/scan the **setup code** the script printed, then Save.
3. Set this iPad's **name and colour** in the same place.
4. **Share > Add to Home Screen**. Delete the old TERRA icon (the github.io one).

The first iPad (or the admin board) to ask for the admin code sets it.

Make a printed QR of the setup code if you like (`node tools/setup-qr.mjs` in terra-tekla-client,
or any QR maker): the app's Server settings reads it with the camera or the scanner. It contains
the key: keep it off walls and photos.

## The admin board (also an installable app)

`https://<your hostname>/admin/`

- **Laptop (Chrome / Edge)**: open it, then the install icon in the address bar (or menu > Save and
  share > Install). It opens in its own window.
- **iPad**: Safari > the address > Share > **Add to Home Screen**. Or from the app: Server settings /
  Admin > Open admin board.

It asks for the access key once per browser, then the admin code (first time: set one).

## What happens to the data

| What | Where it was | After |
|---|---|---|
| Loads, pieces, shipped/returned | PowerFab | Unchanged. PowerFab stays the source of truth. |
| Photos already taken | The conversion layer's `FILES_ROOT` / rule folders | Same folders: the script carries the settings and rules over, and the photo lists read the folders, so they show in the app and admin board. They have no "taken by" (that's new) and aren't in the admin board's "latest photos". |
| Stacks (how pieces are split into stacks) | Only on each iPad, under the github.io address | Not carried over. The first time a load is opened, the server puts every loaded piece into one stack, in PowerFab's order. Split them again if a load is still being built. |
| Truck checks in progress | Only on each iPad | Not carried over: finish them first, or start again. |
| Server address / key on each iPad | iPad, github.io address | Set again on the new address (above). |
| Device names | none | New: set per iPad. |

## After everyone has moved

- terra-tekla-client on GitHub > Settings > **Pages** > turn it off, so nobody keeps using the old
  copy.
- The conversion layer's `local.settings.json`: delete the `FILES_*` lines (ignored now) and clear
  `CORS_ORIGINS` (only the server calls it now). Restart it.
- Keep `C:\TerraMigrationBackup` until you're happy, then delete it (it holds keys).

## Undo

`C:\TerraMigrationBackup\<time>\rollback.ps1` (elevated) puts back every file the script changed
(conversion layer settings, tunnel config, new server settings) and restarts what it restarted.
With a dashboard-managed tunnel, change the route back to `localhost:8090` there. The new server can
stay installed (`Stop-Service TerraTeklaServer` stops it).
