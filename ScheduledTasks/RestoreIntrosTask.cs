using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
    public class RestoreIntrosTask : IScheduledTask
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly ILogger _logger;

        public RestoreIntrosTask(ILibraryManager libraryManager, IItemRepository itemRepository, ILogManager logManager)
        {
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _logger = logManager.GetLogger(nameof(RestoreIntrosTask));
        }

        public string Name => "Restore Intro/Credits Markers";
        public string Key => "IntrosBackupReplacement_Restore";
        public string Description => "Reads per-episode JSON backups and re-applies intro/credits chapter markers.";
        public string Category => "Intro/Credits Backup & Restore";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // No default schedule - restore is meant to be triggered manually
            // from Emby's Scheduled Tasks UI (e.g. after a library rescan wipes markers).
            return Array.Empty<TaskTriggerInfo>();
        }

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var config = Plugin.Instance!.Configuration;
            var jsonUsesMediaFolder = config.SaveJsonToMediaFolder;

            if (!jsonUsesMediaFolder && string.IsNullOrWhiteSpace(config.JsonBackupPath))
            {
                _logger.Warn("JsonBackupPath is not configured and 'Save JSON to media folders' is off - skipping restore.");
                return Task.CompletedTask;
            }

            if (!jsonUsesMediaFolder && !Directory.Exists(config.JsonBackupPath))
            {
                _logger.Warn("JsonBackupPath does not exist - skipping restore.");
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
            var restored = 0;

            foreach (var episode in episodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                processed++;
                progress.Report(100.0 * processed / Math.Max(total, 1));

                var tvdbId = episode.ProviderIds.GetValueOrDefault("Tvdb");
                if (string.IsNullOrEmpty(tvdbId))
                {
                    continue;
                }

                // Build the file name from this episode's own known metadata -
                // if a backup exists, it will be sitting at exactly this name.
                var expectedBackup = new EpisodeIntroBackup
                {
                    TvdbId = tvdbId,
                    SeriesName = episode.SeriesName ?? "Unknown Series",
                    SeasonNumber = episode.ParentIndexNumber ?? 0,
                    EpisodeNumber = episode.IndexNumber ?? 0,
                    EpisodeTitle = episode.Name ?? string.Empty
                };
                var fileName = BackupIntrosTask.BuildFileName(expectedBackup);

                var mediaFolder = string.IsNullOrEmpty(episode.Path) ? null : Path.GetDirectoryName(episode.Path);
                var jsonDir = jsonUsesMediaFolder ? mediaFolder : config.JsonBackupPath;
                if (string.IsNullOrEmpty(jsonDir))
                {
                    continue;
                }

                var filePath = Path.Combine(jsonDir, fileName);
                if (!File.Exists(filePath))
                {
                    continue;
                }

                EpisodeIntroBackup? backup;
                try
                {
                    var json = File.ReadAllText(filePath);
                    backup = JsonSerializer.Deserialize<EpisodeIntroBackup>(json);
                }
                catch (Exception ex)
                {
                    _logger.Error("Failed to parse backup file {0}: {1}", filePath, ex.Message);
                    continue;
                }

                if (backup == null)
                {
                    continue;
                }

                var chapters = new List<ChapterInfo>();

                if (backup.IntroStartTicks.HasValue)
                {
                    chapters.Add(new ChapterInfo
                    {
                        StartPositionTicks = backup.IntroStartTicks.Value,
                        MarkerType = MarkerType.IntroStart,
                        Name = "Intro Start"
                    });
                }

                if (backup.IntroEndTicks.HasValue)
                {
                    chapters.Add(new ChapterInfo
                    {
                        StartPositionTicks = backup.IntroEndTicks.Value,
                        MarkerType = MarkerType.IntroEnd,
                        Name = "Intro End"
                    });
                }

                if (backup.CreditsStartTicks.HasValue)
                {
                    chapters.Add(new ChapterInfo
                    {
                        StartPositionTicks = backup.CreditsStartTicks.Value,
                        MarkerType = MarkerType.CreditsStart,
                        Name = "Credits Start"
                    });
                }

                if (chapters.Count == 0)
                {
                    continue;
                }

                // Merge with any existing plain chapter markers so we don't
                // clobber non-intro/credits chapters already saved on the item.
                var existing = _itemRepository.GetChapters(episode)
                    .Where(c => c.MarkerType == MarkerType.Chapter)
                    .ToList();

                var merged = existing.Concat(chapters).OrderBy(c => c.StartPositionTicks).ToList();

                _itemRepository.SaveChapters(episode.InternalId, merged);
                restored++;
            }

            _logger.Info("Intro/credits restore complete: {0} of {1} episode(s) had a backup applied.", restored, total);
            return Task.CompletedTask;
        }
    }
}
