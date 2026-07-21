# Intro/Credits Backup & Restore

An independent, open-source Emby plugin that backs up and restores
intro/credits chapter markers as one JSON file per episode. Built as a
replacement for a commercial plugin whose developer stopped maintaining it.

- **Backup Intro/Credits Markers** - scans your TV libraries and writes one
  JSON file per episode (only for episodes that actually have intro/credits
  markers) into a folder you choose - or, if you'd rather the marker data
  travel with your media, directly into each episode's own media folder.
  When using a centralized folder, each run first zips any existing
  `.json`/`.nfo` files into a timestamped archive
  (`{BackupPath}/archive/backup-{yyyyMMdd-HHmmss}.zip`) and clears the
  folder, so backups never silently pile up or get overwritten in place.
  Optional companion `.nfo` file per episode, with its own independent path
  and media-folder setting.
- **Restore Intro/Credits Markers** - reads the JSON backups back and
  re-applies the markers, matching each episode by looking for its own
  expected backup file (built from that episode's TVDB ID + season +
  episode number) in whichever location is configured. No default schedule -
  run it manually from Scheduled Tasks (e.g. after a library rescan wipes
  markers).

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
| JSON backup path | Where per-episode JSON backups are written, flat (no per-series subfolders). Ignored if "Save JSON backup files to media folders" is checked. |
| NFO backup path | Where per-episode NFO backups are written, flat. Leave empty (and the media-folder checkbox below unchecked) to skip NFO output entirely. |
| Save JSON backup files to media folders | Overrides the JSON backup path - writes each episode's JSON next to its video file instead. |
| Save NFO backup files to media folders | Overrides the NFO backup path - writes each episode's NFO next to its video file instead. |
| Also insert markers into the media's existing NFO file | See "Media NFO integration" below. |

Storing backups centrally (the default) keeps everything in one place and
makes the automatic pre-backup archiving possible. Storing them alongside
your media instead means the marker data travels with the file if you move
or copy it elsewhere - useful if you rsync or share your library folders
directly. Pick whichever fits your workflow; JSON and NFO can each be
configured independently.

## Media NFO integration

Separately from the JSON/NFO backup options above, there's a "Also insert
markers into the media's existing NFO file" toggle. When enabled:

- **On backup**, it adds a `<markers>` element directly into the NFO file
  your metadata scraper (e.g. the NfoMetadata plugin) already writes next
  to each episode's video - the same file, the same name as the video
  itself, not a separate file. It only touches a file that already exists;
  nothing is created from scratch. Everything else in that NFO (title,
  overview, ratings, etc.) is preserved untouched - only the `<markers>`
  node is added or replaced.
- **On restore**, if no JSON backup is found for an episode, this same
  `<markers>` element is read as a fallback. This means backups made by
  the original commercial "Intros Backup/Restore" plugin - which used this
  exact approach and schema - can be restored directly with this plugin,
  no conversion needed.

Schema used (matches the original commercial plugin's, for compatibility):
```xml
<markers>
  <introstart>771144278</introstart>
  <introend>875621889</introend>
  <creditstart>0</creditstart>
</markers>
```
A value of `0` (or a missing element) is treated as "not set".

This is an addition to, not a replacement for, the JSON backup - JSON
remains the more reliable source since it doesn't depend on a scraper NFO
already existing for every episode.

## Permissions

Whichever folder(s) you point this at - a central backup path or "next to
the media" - the account **Emby Server itself runs as** needs write access
there. This is an OS-level requirement, not something the plugin can work
around, and it's the most common reason a backup run fails.

**Symptom:** the "Backup Intro/Credits Markers" task fails, and Emby's log
(Dashboard → Logs) shows something like:

```
System.UnauthorizedAccessException: Access to the path '...' is denied.
 ---> System.IO.IOException: Permission denied
```

As of v1.1, a permission error in one folder no longer aborts the whole
run - it's logged and that episode is skipped, with a summary count at the
end. But you'll still want to fix the underlying permission so backups
actually complete.

**Fixing it - general Linux/Docker:**
```bash
sudo chown -R <emby-user>:<emby-group> /path/to/backup/folder
sudo chmod -R 755 /path/to/backup/folder
```
Find out which user Emby actually runs as with `ps aux | grep -i emby`.

**Fixing it - Synology (Btrfs volumes with ACLs):**
Synology's Btrfs volumes use ACLs that can override what `ls -la` appears
to show (look for a `+` after the permission bits, e.g. `drwxrwxrwx+` -
that `+` means ACL rules are in effect and may silently override the
visible Unix permissions). `chown`/`chmod` alone often isn't enough here.
Grant the plugin's account an explicit ACL entry instead:
```bash
sudo synoacltool -add /volume1/your/share "user:emby:allow:rwxpdDaARWcCo:fd"
```
If the folder already existed before you added the rule, force the new
rule to apply to existing subfolders/files too:
```bash
sudo synoacltool -enforce-inherit /volume1/your/share/existing-subfolder
```
Replace `emby` with whatever account your Emby install actually runs as
(check with `ps aux | grep -i emby`) and adjust the path to whichever
folder is failing - this applies equally to a central backup path and to
your actual media library folders if you're using the "save to media
folders" option.

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
