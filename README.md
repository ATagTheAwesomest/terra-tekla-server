# terra-tekla-server

The shop's own server for the TERRA iPad app
([terra-tekla-client](https://github.com/ATagTheAwesomest/terra-tekla-client)):

- **Hosts the iPad app.** The iPads open this server's address; no GitHub Pages needed. Updating the
  app is a `git pull` of the client folder.
- **Keeps what PowerFab doesn't**, in a SQLite database: stacks and their order, who scanned each
  piece, the shared truck check, device names and colours, an activity history, and photos.
- **Syncs the iPads live.** A scan, move, rename or truck-check tick on one iPad shows on the others
  straight away (WebSocket).
- **Passes PowerFab calls through** to
  [tekla-conversion-layer](https://github.com/ATagTheAwesomest/tekla-conversion-layer), which stays
  a plain PowerFab REST converter. Its key stays on this server.
- **Admin page** at `/admin/`: every device (rename, recolour, what it did recently), the PowerFab
  jobs it can reach, loads with their stacks and who scanned what, photos and the docs. Server stats
  and health, and Settings (server settings, photo folder rules, backup, restart) sit behind an
  admin code entered on an on-screen keypad.

```
iPads / admin page ──HTTPS──► terra-tekla-server ──► tekla-conversion-layer ──► PowerFab
                               ├─ data/terra.db (SQLite)
                               ├─ CLIENT_ROOT (terra-tekla-client clone, served as the app)
                               └─ PHOTOS_ROOT (photo folders)
```

.NET 10 minimal API, runs as a Windows service (`TerraTeklaServer`), default port 8095.

## Docs

- [docs/SETUP.md](docs/SETUP.md): install, settings, HTTPS, iPad setup, updating, backups, how sync
  works, every endpoint and live message, the operations, the database schema, testing without
  PowerFab, and a setup checklist.
- [docs/PHOTOS.md](docs/PHOTOS.md): photo settings, folder rules, naming, the photo API.

Both ship next to the exe and show on the admin page (Docs).

## Quick start (development)

```bash
node tools/fake-powerfab.mjs &          # a fake conversion layer with one job, EPM-001
ACCESS_KEY=test-key CONVERSION_URL=http://127.0.0.1:8099 CONVERSION_KEY=fake-key \
CLIENT_ROOT=../terra-tekla-client DATA_DIR=/tmp/terra-data Urls=http://127.0.0.1:8095 \
  dotnet run --project src/TerraTeklaServer
```

Open `http://127.0.0.1:8095/` (the app; key `test-key`) and `http://127.0.0.1:8095/admin/`.
`dotnet test` runs the tests.

## Deploy (Windows)

First install: [docs/SETUP.md](docs/SETUP.md#installing-on-windows-first-time). After that, from the
repo in an elevated PowerShell:

```powershell
.\windows\deploy.ps1      # pulls this repo and the app's clone, builds, restarts, checks /health
```

`windows\build-release.ps1` makes a self-contained zip for a server without the .NET SDK.

## Layout

| Path | What |
|---|---|
| `src/TerraTeklaServer/Program.cs` | Routes: app hosting, `/api`, `/ws`, PowerFab pass-through, photos |
| `src/TerraTeklaServer/LoadOps.cs` | The stack operations and the PowerFab sync rules |
| `src/TerraTeklaServer/Store.cs` | SQLite database |
| `src/TerraTeklaServer/Hub.cs` | Live connections |
| `src/TerraTeklaServer/PhotoStore.cs` | Photo folders and rules |
| `src/TerraTeklaServer/AdminLock.cs` | The admin code (hashed) and its sessions |
| `src/TerraTeklaServer/SettingsFile.cs` | Reading, checking and saving `local.settings.json` from the admin page |
| `src/TerraTeklaServer/ServerStats.cs` | Request, error and log figures for the Server view |
| `src/TerraTeklaServer/wwwroot/admin/` | Admin page |
| `tools/fake-powerfab.mjs` | Fake conversion layer for testing |
| `windows/` | Service install, deploy and release scripts, example settings |
