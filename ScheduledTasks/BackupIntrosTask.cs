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
        public string Description => "Writes each episode's intro/credits chapter markers to JSON and/or NFO, per the Custom or Automatic (Media Folder) settings.";
        public string Category => "Intro/Credits Backup & Restore (Open Source)";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerDaily,
                TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
            };
        }

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var config = Plugin.Instance!.Configuration;
            var isCustom = config.BackupMode == BackupModeResolver.ModeCustom;

            // Pre-flight: only relevant for Custom mode's fixed folders (a
            // media folder is per-episode and doesn't exist ahead of time).
            if (isCustom)
            {
                try
                {
                    if (config.CustomBackupJson && !string.IsNullOrWhiteSpace(config.JsonBackupPath))
                    {
                        Directory.CreateDirectory(config.JsonBackupPath);
                        ArchiveAndClearExisting(config.JsonBackupPath, "*.json");
                    }

                    if (config.CustomBackupNfo && !string.IsNullOrWhiteSpace(config.NfoBackupPath))
                    {
                        Directory.CreateDirectory(config.NfoBackupPath);
                        ArchiveAndClearExisting(config.NfoBackupPath, "*.nfo");
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    _logger.Error(
                        "Cannot access the configured backup folder - check that the account Emby runs as has write "
                        + "permission there (see the README's Permissions section). Backup aborted. Error: {0}", ex.Message);
                    return Task.CompletedTask;
                }

                if (!config.CustomBackupJson && !config.CustomBackupNfo)
                {
                    _logger.Warn("Custom mode is selected but neither 'Backup as JSON' nor 'Backup as NFO' is checked - nothing to do.");
                    return Task.CompletedTask;
                }
            }
            else if (!config.AutomaticBackupJson && !config.AutomaticBackupNfo && !config.AutomaticJsonSafetyCopy)
            {
                _logger.Warn("Automatic (Media Folder) mode is selected but nothing is checked - nothing to do.");
                return Task.CompletedTask;
            }

            // Automatic mode's safety copy also writes into a fixed folder
            // (unlike the per-episode media-folder writes), so it gets the
            // same archive-before-overwrite treatment Custom mode's central
            // folders get, instead of silently overwriting with no history.
            var safetyCopyPreflightDir = BackupModeResolver.GetAutomaticJsonSafetyCopyDir(config);
            if (safetyCopyPreflightDir != null)
            {
                try
                {
                    Directory.CreateDirectory(safetyCopyPreflightDir);
                    ArchiveAndClearExisting(safetyCopyPreflightDir, "*.json");
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    _logger.Error(
                        "Cannot access the JSON safety copy folder - check that the account Emby runs as has write "
                        + "permission there. Backup aborted. Error: {0}", ex.Message);
                    return Task.CompletedTask;
                }
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

                if (backup.IntroStartTicks == null && backup.IntroEndTicks == null && backup.CreditsStartTicks == null)
                {
                    continue;
                }

                var mediaFolder = string.IsNullOrEmpty(episode.Path) ? null : Path.GetDirectoryName(episode.Path);
                var fileName = BuildFileName(backup);

                // JSON (Custom -> JsonBackupPath if CustomBackupJson, or
                // Automatic -> media folder if target is Json/Both).
                var jsonDir = BackupModeResolver.GetBackupJsonDir(config, mediaFolder);
                if (jsonDir == null)
                {
                    if (isCustom ? config.CustomBackupJson : config.AutomaticBackupJson)
                    {
                        _logger.Warn("Skipping JSON backup for {0} - could not determine a target folder.", fileName);
                    }
                }
                else
                {
                    try
                    {
                        Directory.CreateDirectory(jsonDir);

                        var jsonFileName = !string.IsNullOrEmpty(episode.Path)
                            ? Path.ChangeExtension(Path.GetFileName(episode.Path), ".json")
                            : fileName;

                        var jsonPath = Path.Combine(jsonDir, jsonFileName);
                        var json = JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true });
                        File.WriteAllText(jsonPath, json);
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                    {
                        failedWrites++;
                        _logger.Error("Failed to write JSON backup for {0} to {1}: {2}", fileName, jsonDir, ex.Message);
                    }
                }

                // Automatic mode: insert into the video's own existing
                // Emby-scraper NFO.
                if (BackupModeResolver.ShouldBackupIntoExistingNfo(config) && mediaFolder != null && !string.IsNullOrEmpty(episode.Path))
                {
                    var mediaNfoPath = Path.ChangeExtension(episode.Path, ".nfo");
                    if (!File.Exists(mediaNfoPath))
                    {
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

                // Custom mode: standalone, plugin-owned NFO file.
                var customNfoDir = BackupModeResolver.GetCustomBackupNfoDir(config);
                if (customNfoDir != null && !string.IsNullOrEmpty(episode.Path))
                {
                    try
                    {
                        Directory.CreateDirectory(customNfoDir);
                        var standaloneNfoPath = StandaloneNfo.GetPath(customNfoDir, episode.Path);
                        StandaloneNfo.Write(standaloneNfoPath, backup);
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or XmlException)
                    {
                        failedWrites++;
                        _logger.Error("Failed to write standalone NFO backup for {0} to {1}: {2}", fileName, customNfoDir, ex.Message);
                    }
                }

                // Automatic mode only: extra "just in case" JSON safety copy
                // in a selected folder, independent of whatever the two
                // options above are doing. Restore never reads from here -
                // this is insurance only, same filename-as-video convention
                // as everywhere else.
                var safetyCopyDir = BackupModeResolver.GetAutomaticJsonSafetyCopyDir(config);
                if (safetyCopyDir != null && !string.IsNullOrEmpty(episode.Path))
                {
                    try
                    {
                        Directory.CreateDirectory(safetyCopyDir);
                        var safetyFileName = Path.ChangeExtension(Path.GetFileName(episode.Path), ".json");
                        var safetyPath = Path.Combine(safetyCopyDir, safetyFileName);
                        var safetyJson = JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true });
                        File.WriteAllText(safetyPath, safetyJson);
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                    {
                        failedWrites++;
                        _logger.Error("Failed to write JSON safety copy for {0} to {1}: {2}", fileName, safetyCopyDir, ex.Message);
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

        private static readonly char[] WindowsInvalidFileNameChars =
            { '<', '>', ':', '"', '/', '\\', '|', '?', '*',
              '\u0000','\u0001','\u0002','\u0003','\u0004','\u0005','\u0006','\u0007',
              '\u0008','\u0009','\u000A','\u000B','\u000C','\u000D','\u000E','\u000F',
              '\u0010','\u0011','\u0012','\u0013','\u0014','\u0015','\u0016','\u0017',
              '\u0018','\u0019','\u001A','\u001B','\u001C','\u001D','\u001E','\u001F' };

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

        internal static string BuildFileName(EpisodeIntroBackup backup)
        {
            var tvdbPart = string.IsNullOrEmpty(backup.TvdbId) ? "unknown" : backup.TvdbId;
            var raw = $"{backup.SeriesName} ({tvdbPart}) S{backup.SeasonNumber:00}E{backup.EpisodeNumber:00} - {backup.EpisodeTitle}.json";

            foreach (var c in WindowsInvalidFileNameChars)
            {
                raw = raw.Replace(c, '_');
            }

            var withoutExt = raw.Substring(0, raw.Length - 5);
            withoutExt = withoutExt.TrimEnd(' ', '.');
            raw = withoutExt + ".json";

            return raw;
        }

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
