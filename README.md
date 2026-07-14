# Intro/Credits Backup & Restore

An independent, open-source Emby plugin that backs up and restores
intro/credits chapter markers as one JSON file per episode. Built as a
replacement for a commercial plugin whose developer stopped maintaining it.

- **Backup Intro/Credits Markers** - scans your TV libraries and writes one
  JSON file per episode (only for episodes that actually have intro/credits
  markers) into a folder you choose. Each run first zips any existing
  `.json`/`.nfo` files into a timestamped archive
  (`{BackupPath}/archive/backup-{yyyyMMdd-HHmmss}.zip`) and clears the
  folder, so backups never silently pile up or get overwritten in place.
  Optional companion `.nfo` file per episode.
- **Restore Intro/Credits Markers** - reads the JSON backups back and
  re-applies the markers, matching episodes by TVDB ID + season + episode
  number. No default schedule - run it manually from Scheduled Tasks (e.g.
  after a library rescan wipes markers).

Both are plain Emby scheduled tasks - no custom in-app buttons or dialogs,
just the tasks you already know from Emby's Dashboard → Scheduled Tasks.

## Scope

- **TV episodes only.** Movie intro/credits markers are not handled.
- Markers are matched to episodes via TVDB ID, so episodes without a TVDB ID
  are skipped on restore.

## File naming

```
{SeriesName} ({TvdbId}) S{SS}E{EE} - {EpisodeTitle}.json
```

Example: `House (324313) S03E17 - Fetal Position.json`

Files are written flat in the backup folder (no per-series subfolders), and
the name is sanitized against the full Windows/SMB-invalid character set
(not just the host OS's set), so the folder stays browsable from Windows
clients even when Emby itself runs on Linux.

## Requirements

- An Emby Server install (developed and tested against **4.10.0.18**; should
  work on nearby 4.10.x versions, but the three reference DLLs below must
  match your server's version).
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build the
  plugin (or Docker, see below).

## Building

The plugin references three DLLs from your own Emby Server install -
`MediaBrowser.Common.dll`, `MediaBrowser.Model.dll`, and
`MediaBrowser.Controller.dll`. These are **not** redistributed here since
they're part of Emby Server itself; you need to copy them from your own
install into `libs/` before building. Typical locations:

| Platform | Path |
|---|---|
| Synology (Package Center) | `/var/packages/EmbyServer/target/system/` |
| Docker (`emby/embyserver` image) | inside the container at `/system/` |
| Linux (manual install) | wherever you extracted the server, under `system/` |
| Windows | `C:\Program Files\Emby-Server\system\` |

```bash
git clone https://github.com/<your-username>/intros-backup-restore.git
cd intros-backup-restore

mkdir -p libs
cp /path/to/your/emby/system/MediaBrowser.Common.dll libs/
cp /path/to/your/emby/system/MediaBrowser.Model.dll libs/
cp /path/to/your/emby/system/MediaBrowser.Controller.dll libs/

dotnet build -c Release
```

No local .NET SDK? Build with Docker instead:

```bash
docker run --rm -v "$(pwd)":/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 dotnet build -c Release
```

## Installing

1. Copy the built `IntrosBackupReplacement.dll` (from `bin/Release/net8.0/`)
   into Emby's plugin folder. On a Synology Package Center install:
   ```
   /var/packages/EmbyServer/var/plugins/
   ```
   Make sure the file is owned by the same user Emby runs as, and is
   readable (e.g. `chown emby:emby` + `chmod 644` on Synology).
2. Restart Emby Server.
3. Go to Dashboard → Plugins - "Intro/Credits Backup & Restore" should
   appear, with its own config page (click it to set the backup folder path
   and toggle NFO output).
4. Go to Dashboard → Scheduled Tasks - both tasks appear grouped under their
   own "Intro/Credits Backup & Restore" heading. The backup task defaults to
   a daily 04:00 trigger; the restore task has no default trigger (run it
   manually when needed).

## Configuration

| Setting | Description |
|---|---|
| Backup folder path | Where per-episode JSON (and optional NFO) backups are written. |
| Also write a companion .nfo file | If enabled, writes a matching `.nfo` next to each JSON backup. |

## Why this exists

The original commercial "Intros Backup/Restore" plugin this replaces has
gone unmaintained, and its users have been asking for updates. This is an
independent, from-scratch implementation built purely against Emby's public
plugin SDK (`ILibraryManager`, `IItemRepository`, `IScheduledTask`,
`ChapterInfo`) - no code from the original plugin is included; only its
general approach (JSON export per episode via the chapter marker API,
scheduled-task architecture) was used as a reference point.

## License

MIT - see [LICENSE](LICENSE).

## Contributing

Issues and pull requests welcome.
