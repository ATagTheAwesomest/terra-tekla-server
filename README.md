# terra-tekla-server

The shop's server for the TERRA iPad app. The iPads open this server's web address and it:

- serves the iPad app ([terra-tekla-client](https://github.com/ATagTheAwesomest/terra-tekla-client));
- keeps what PowerFab has no place for (stacks, who scanned what, truck checks, device names,
  history, photos) in a SQLite database;
- keeps every iPad in sync live (WebSocket);
- passes PowerFab calls on to
  [tekla-conversion-layer](https://github.com/ATagTheAwesomest/tekla-conversion-layer), so the
  PowerFab key never sits on an iPad;
- has an admin board at `/admin/` (devices, jobs, loads, photos, settings, updates).

```
iPads / admin board ──HTTPS──► terra-tekla-server :8095 ──► tekla-conversion-layer :8090 ──► PowerFab
                                 ├─ data\terra.db   (SQLite)
                                 ├─ CLIENT_ROOT     (git clone of terra-tekla-client)
                                 └─ PHOTOS_ROOT     (photo folders)
```

## Requirements

- Windows 10/11 or Windows Server 2019+ (it runs as a Windows service)
- [.NET 10 SDK](https://dotnet.microsoft.com/download) and git
- tekla-conversion-layer installed and running, and its `API_TOKEN`
- HTTPS in front of it for the iPads (a Cloudflare Tunnel is simplest)

## Install

In an elevated PowerShell:

```powershell
git clone https://github.com/ATagTheAwesomest/terra-tekla-server C:\src\terra-tekla-server
git clone https://github.com/ATagTheAwesomest/terra-tekla-client C:\TerraTeklaClient
cd C:\src\terra-tekla-server
dotnet publish src\TerraTeklaServer -c Release -o C:\TerraTeklaServer
copy windows\install-service.ps1 C:\TerraTeklaServer\
cd C:\TerraTeklaServer
copy local.settings.example.json local.settings.json   # then fill it in (see Configure)
.\install-service.ps1                                   # installs and starts the service
Invoke-RestMethod http://127.0.0.1:8095/health          # should say status: ok
```

The admin code is made up by the server on first start: read it from
`C:\TerraTeklaServer\data\admin-code.txt`, then change it on the admin board.

## Configure

Settings live in `C:\TerraTeklaServer\local.settings.json`. The ones you must fill in:

| Key | What to put |
|---|---|
| `ACCESS_KEY` | A long random key. Every iPad and the admin board needs it. |
| `CONVERSION_URL` | The conversion layer's address, usually `http://127.0.0.1:8090`. |
| `CONVERSION_KEY` | The conversion layer's `API_TOKEN`. |
| `CLIENT_ROOT` | The terra-tekla-client clone, e.g. `C:\TerraTeklaClient`. |

Photos are off until you set `PHOTOS_ENABLED` to `true` and `PHOTOS_ROOT` to a folder (see
[docs/PHOTOS.md](docs/PHOTOS.md)). Most settings can also be changed on the admin board under
Settings. Restart after editing the file by hand: `Restart-Service TerraTeklaServer`.

**HTTPS (Cloudflare Tunnel):** one public hostname, no path, pointing at `http://localhost:8095`.
Keep WebSockets on. Other options are in [docs/SETUP.md](docs/SETUP.md#https-needed-for-the-ipads).

## Set up an iPad

1. Open the server's HTTPS address in Safari.
2. Menu > **Server settings / Admin**: enter the access key and **Save & test** (or scan the setup QR
   from the admin board, Settings > Set up an iPad).
3. Share > **Add to Home Screen**, and always open it from there.

Name and colour each iPad on the admin board (Devices).

## Update

From the admin board (Settings > Updates) or an iPad (**Update the server**), or by hand:

```powershell
cd C:\src\terra-tekla-server
.\windows\deploy.ps1     # pulls this repo and the app, builds, restarts, checks /health
```

`deploy.ps1` never touches `local.settings.json`, `photo-rules.json` or `data\`. To update only the
iPad app, use **Update the app on the server** or run `C:\TerraTeklaServer\update-client.ps1`.

## Run it on your own computer (no PowerFab)

Any OS with the .NET 10 SDK and Node 18+. A fake conversion layer stands in for PowerFab:

```bash
node tools/fake-powerfab.mjs &
ACCESS_KEY=test-key CONVERSION_URL=http://127.0.0.1:8099 CONVERSION_KEY=fake-key \
CLIENT_ROOT=../terra-tekla-client DATA_DIR=/tmp/terra-data Urls=http://127.0.0.1:8095 \
  dotnet run --project src/TerraTeklaServer
```

Open `http://127.0.0.1:8095/` (key `test-key`, job `EPM-001`) and `/admin/`. Run the tests with
`dotnet test`. Never point a test at the real PowerFab: every write is real.

## More

- [docs/SETUP.md](docs/SETUP.md): every setting, the admin board, backups, how sync works, the API
  and the database.
- [docs/PHOTOS.md](docs/PHOTOS.md): photo folders, naming rules and the photo API.

Both are also on the admin board under Docs.
