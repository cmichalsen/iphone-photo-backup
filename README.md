# iPhone Photo Backup

Copies photos and videos from an iPhone (USB) to a network folder, verifies each copy by checksum, and can then delete the verified ones from the phone.

## One-time setup

**Phone**
- Settings > Photos > Transfer to Mac or PC > **Keep Originals**
- iCloud Photos must be off (otherwise the phone won't allow deletes over USB)

**PC**
- Open `\\10.0.0.41\iphone` once in File Explorer and tick **Remember my credentials**. The app never stores the NAS password.
- Smart App Control must be off: it blocks unsigned apps, which includes this one.

## Use

1. Plug in the iPhone, unlock it, tap **Trust This Computer** / **Allow**. Keep it unlocked while the app works.
2. Run `publish\PhotoBackup.App.exe`.
3. **Back up now** copies anything new to `<destination>\YYYY\YYYY-MM\`. Safe to cancel or unplug; the next run carries on.
4. **Preview delete** lists what could be removed. It deletes nothing.
5. **Delete verified from phoneâ€¦** asks for confirmation, then deletes only files that have a verified copy.

## What "verified" means

While a file streams off the phone the app computes its SHA-256. After the file lands on the NAS it is read back and hashed again; only a match is recorded as verified. Before every delete the app re-checks that the NAS file still exists with the right size (or the full checksum, with the "Re-read every file" box ticked).

## Where things are kept

- Record of backed-up files: `%LOCALAPPDATA%\PhotoBackup\state.jsonl` (a copy is written to `<destination>\.photobackup\`)
- Logs: `%LOCALAPPDATA%\PhotoBackup\logs\`
- Settings: `%LOCALAPPDATA%\PhotoBackup\settings.json`

## Limits

- Files only: albums, favourites and edit history are not preserved. Live Photos arrive as a photo plus a `.MOV`; edited photos as the original plus an `IMG_Eâ€¦` file.
- Folders are chosen from the date the phone reports for each file.

## Development

```
dotnet test
dotnet publish src/PhotoBackup.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

`src/PhotoBackup.Spike` is a console tool for poking at the phone directly: `list`, `copy <phone path> <dir>`, `delete <phone path>`.

## Web gallery (on the NAS)

A read-only gallery for browsing the backup and downloading files back to the phone lives in `web/`. GitHub Actions builds it into `ghcr.io/cmichalsen/iphone-photo-backup:latest` on every push to `main`. To run it, paste `web/compose.yaml` into a new Dockge stack and open `https://<nas>:8643` on the iPhone (accept the one-time certificate warning; it enables Save to Photos and multi-select) or `http://<nas>:8642` elsewhere. It has no login, so keep it on the home network only.

