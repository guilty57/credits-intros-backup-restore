using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using IntrosBackupReplacement.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace IntrosBackupReplacement.ScheduledTasks
{
    public class BackupIntrosTask : IScheduledTask
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly ILogger _logger;

        public BackupIntrosTask(ILibraryManager libraryManager, IItemRepository itemRepository, ILogManager logManager)
        {
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _logger = logManager.GetLogger(nameof(BackupIntrosTask));
        }

        public string Name => "Backup Intro/Credits Markers";
        public string Key => "IntrosBackupReplacement_Backup";
        public string Description => "Writes each episode's intro/credits chapter markers to a JSON file, either in the backup folder or next to the media.";
        public string Category => "Intro/Credits Backup & Restore (Open Source)";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // Default: once a day. The user can change this from Emby's
            // Scheduled Tasks UI - no in-app buttons needed.
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerDaily,
                TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
            };
        }

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var config = Plugin.Instance!.Configuration;

            var jsonUsesMediaFolder = config.SaveJsonToMediaFolder;

            if (!jsonUsesMediaFolder && string.IsNullOrWhiteSpace(config.JsonBackupPath))
            {
                _logger.Warn("JsonBackupPath is not configured and 'Save JSON to media folders' is off - skipping backup.");
                return Task.CompletedTask;
            }

            try
            {
                if (!jsonUsesMediaFolder)
                {
                    Directory.CreateDirectory(config.JsonBackupPath);
                    ArchiveAndClearExisting(config.JsonBackupPath, "*.json");
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                _logger.Error(
                    "Cannot access the configured backup folder - check that the account Emby runs as has write "
                    + "permission there (see the README's Permissions section). Backup aborted. Error: {0}", ex.Message);
                return Task.CompletedTask;
            }

            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Episode) },
                Recursive = true
            };

            var episodes = _libraryManager.GetItemList(query).OfType<Episode>().ToList();
            var total = episodes.Count;
            var processed = 0;
            var failedWrites = 0;

            foreach (var episode in episodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                processed++;
                progress.Report(100.0 * processed / Math.Max(total, 1));

                var chapters = _itemRepository.GetChapters(episode);
                if (chapters == null || chapters.Count == 0)
                {
                    continue;
                }

                var backup = new EpisodeIntroBackup
                {
                    TvdbId = episode.ProviderIds.GetValueOrDefault("Tvdb"),
                    ImdbId = episode.ProviderIds.GetValueOrDefault("Imdb"),
                    TmdbId = episode.ProviderIds.GetValueOrDefault("Tmdb"),
                    SeriesName = episode.SeriesName ?? "Unknown Series",
                    SeasonNumber = episode.ParentIndexNumber ?? 0,
                    EpisodeNumber = episode.IndexNumber ?? 0,
                    EpisodeTitle = episode.Name ?? string.Empty,
                    IntroStartTicks = chapters.FirstOrDefault(c => c.MarkerType == MarkerType.IntroStart)?.StartPositionTicks,
                    IntroEndTicks = chapters.FirstOrDefault(c => c.MarkerType == MarkerType.IntroEnd)?.StartPositionTicks,
                    CreditsStartTicks = chapters.FirstOrDefault(c => c.MarkerType == MarkerType.CreditsStart)?.StartPositionTicks
                };

                // Nothing worth backing up if none of the three markers were found.
                if (backup.IntroStartTicks == null && backup.IntroEndTicks == null && backup.CreditsStartTicks == null)
                {
                    continue;
                }

                var mediaFolder = string.IsNullOrEmpty(episode.Path) ? null : Path.GetDirectoryName(episode.Path);
                var fileName = BuildFileName(backup);

                var jsonDir = jsonUsesMediaFolder ? mediaFolder : config.JsonBackupPath;
                if (string.IsNullOrEmpty(jsonDir))
                {
                    _logger.Warn("Skipping {0} - could not determine a media folder for its JSON backup.", fileName);
                }
                else
                {
                    try
                    {
                        Directory.CreateDirectory(jsonDir);
                        var jsonPath = Path.Combine(jsonDir, fileName);
                        var json = JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true });
                        File.WriteAllText(jsonPath, json);
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                    {
                        // A single folder's permission/IO problem (e.g. Emby's
                        // user account lacking write access to that media
                        // folder) shouldn't abort the whole backup run - log
                        // it and keep going with the rest of the library.
                        failedWrites++;
                        _logger.Error("Failed to write JSON backup for {0} to {1}: {2}", fileName, jsonDir, ex.Message);
                    }
                }

                if (config.InsertIntoMediaNfo && mediaFolder != null && !string.IsNullOrEmpty(episode.Path))
                {
                    var mediaNfoPath = Path.ChangeExtension(episode.Path, ".nfo");
                    if (!File.Exists(mediaNfoPath))
                    {
                        // We only ever insert into an NFO that already exists
                        // (typically written by the NfoMetadata plugin) - we
                        // never invent one from scratch, since we don't know
                        // what root element/schema Emby's own scraper expects.
                        _logger.Warn("Skipping media-NFO insert for {0} - no existing NFO found at {1}.", fileName, mediaNfoPath);
                    }
                    else
                    {
                        try
                        {
                            InsertMarkersIntoExistingNfo(backup, mediaNfoPath);
                        }
                        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or XmlException)
                        {
                            failedWrites++;
                            _logger.Error("Failed to insert markers into existing NFO {0}: {1}", mediaNfoPath, ex.Message);
                        }
                    }
                }
            }

            if (failedWrites > 0)
            {
                _logger.Warn("Intro/credits backup complete: {0} episode(s) processed, {1} file write(s) failed (likely a permissions issue - see errors above).", total, failedWrites);
            }
            else
            {
                _logger.Info("Intro/credits backup complete: {0} episode(s) processed.", total);
            }
            return Task.CompletedTask;
        }

        // The characters below are invalid in Windows/SMB file names. We hard-code
        // this set instead of relying on Path.GetInvalidFileNameChars(), because
        // that method returns the HOST OS's invalid set - on Linux (where Emby
        // runs here) it only excludes '/' and NUL, so things like ':' or '?' in an
        // episode title would slip through and get mangled into 8.3 short names
        // once the backup folder is browsed over SMB from Windows.
        private static readonly char[] WindowsInvalidFileNameChars =
            { '<', '>', ':', '"', '/', '\\', '|', '?', '*',
              '\u0000','\u0001','\u0002','\u0003','\u0004','\u0005','\u0006','\u0007',
              '\u0008','\u0009','\u000A','\u000B','\u000C','\u000D','\u000E','\u000F',
              '\u0010','\u0011','\u0012','\u0013','\u0014','\u0015','\u0016','\u0017',
              '\u0018','\u0019','\u001A','\u001B','\u001C','\u001D','\u001E','\u001F' };

        /// <summary>
        /// Zips any existing files matching searchPattern (top-level only -
        /// the "archive" subfolder itself is never included) into a
        /// timestamped archive under {folder}/archive/, then deletes the
        /// originals so this run starts clean instead of overwriting files
        /// in place. Only used for centralized (non-media-folder) output.
        /// </summary>
        private void ArchiveAndClearExisting(string folder, string searchPattern)
        {
            var existingFiles = Directory.GetFiles(folder, searchPattern).ToList();

            if (existingFiles.Count == 0)
            {
                return;
            }

            var archiveDir = Path.Combine(folder, "archive");
            Directory.CreateDirectory(archiveDir);

            var zipPath = Path.Combine(archiveDir, $"backup-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (var file in existingFiles)
                {
                    archive.CreateEntryFromFile(file, Path.GetFileName(file), CompressionLevel.Optimal);
                }
            }

            foreach (var file in existingFiles)
            {
                File.Delete(file);
            }

            _logger.Info("Archived {0} existing file(s) matching {1} to {2} before running a fresh backup.", existingFiles.Count, searchPattern, zipPath);
        }

        /// <summary>
        /// Builds "{SeriesName} ({TvdbId}) S{SS}E{EE} - {EpisodeTitle}.json",
        /// stripping characters that are invalid in file names.
        /// </summary>
        internal static string BuildFileName(EpisodeIntroBackup backup)
        {
            var tvdbPart = string.IsNullOrEmpty(backup.TvdbId) ? "unknown" : backup.TvdbId;
            var raw = $"{backup.SeriesName} ({tvdbPart}) S{backup.SeasonNumber:00}E{backup.EpisodeNumber:00} - {backup.EpisodeTitle}.json";

            foreach (var c in WindowsInvalidFileNameChars)
            {
                raw = raw.Replace(c, '_');
            }

            // Windows also disallows file names ending in a space or a period
            // (except the final extension's dot). Trim the base name (without
            // the .json extension) so a title ending in "..." or a trailing
            // space doesn't produce an inaccessible file over SMB.
            var withoutExt = raw.Substring(0, raw.Length - 5); // strip ".json"
            withoutExt = withoutExt.TrimEnd(' ', '.');
            raw = withoutExt + ".json";

            return raw;
        }

        /// <summary>
        /// Loads an existing NFO file (e.g. one written by the NfoMetadata
        /// plugin), replaces or adds a top-level &lt;markers&gt; element with
        /// this episode's intro/credits ticks, and saves back over the same
        /// file. XmlDocument's DOM model preserves every other element in
        /// the file untouched - we only touch the one node we own. Uses the
        /// same &lt;markers&gt;&lt;introstart&gt;/&lt;introend&gt;/&lt;creditstart&gt;
        /// schema as the original commercial "Intros Backup/Restore" plugin.
        /// </summary>
        private static void InsertMarkersIntoExistingNfo(EpisodeIntroBackup backup, string nfoPath)
        {
            var doc = new XmlDocument();
            doc.Load(nfoPath);

            var root = doc.DocumentElement;
            if (root == null)
            {
                throw new XmlException($"NFO file has no root element: {nfoPath}");
            }

            var existingMarkers = root.SelectSingleNode("markers");
            if (existingMarkers != null)
            {
                root.RemoveChild(existingMarkers);
            }

            var markers = doc.CreateElement("markers");

            var introStart = doc.CreateElement("introstart");
            introStart.InnerText = (backup.IntroStartTicks ?? 0).ToString();
            markers.AppendChild(introStart);

            var introEnd = doc.CreateElement("introend");
            introEnd.InnerText = (backup.IntroEndTicks ?? 0).ToString();
            markers.AppendChild(introEnd);

            var creditStart = doc.CreateElement("creditstart");
            creditStart.InnerText = (backup.CreditsStartTicks ?? 0).ToString();
            markers.AppendChild(creditStart);

            root.AppendChild(markers);

            doc.Save(nfoPath);
        }
    }
}
