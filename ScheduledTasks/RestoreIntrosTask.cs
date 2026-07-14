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

            if (string.IsNullOrWhiteSpace(config.IntroBackupPath) || !Directory.Exists(config.IntroBackupPath))
            {
                _logger.Warn("IntroBackupPath is not configured or does not exist - skipping restore.");
                return Task.CompletedTask;
            }

            // Build a lookup of every episode in the library keyed by TvdbId+Season+Episode
            // so we only need one library query instead of one per backup file.
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Episode) },
                Recursive = true
            };

            var episodesByKey = _libraryManager.GetItemList(query)
                .OfType<Episode>()
                .Where(e => e.ProviderIds.ContainsKey("Tvdb"))
                .GroupBy(e => (e.ProviderIds["Tvdb"], e.ParentIndexNumber ?? 0, e.IndexNumber ?? 0))
                .ToDictionary(g => g.Key, g => g.First());

            var files = Directory.GetFiles(config.IntroBackupPath, "*.json");
            var total = files.Length;
            var processed = 0;
            var restored = 0;

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                processed++;
                progress.Report(100.0 * processed / Math.Max(total, 1));

                EpisodeIntroBackup? backup;
                try
                {
                    var json = File.ReadAllText(file);
                    backup = JsonSerializer.Deserialize<EpisodeIntroBackup>(json);
                }
                catch (Exception ex)
                {
                    _logger.Error("Failed to parse backup file {0}: {1}", file, ex.Message);
                    continue;
                }

                if (backup == null || string.IsNullOrEmpty(backup.TvdbId))
                {
                    continue;
                }

                var key = (backup.TvdbId, backup.SeasonNumber, backup.EpisodeNumber);
                if (!episodesByKey.TryGetValue(key, out var episode))
                {
                    _logger.Warn("No matching episode found for {0}", Path.GetFileName(file));
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

            _logger.Info("Intro/credits restore complete: {0} of {1} backup file(s) applied.", restored, total);
            return Task.CompletedTask;
        }
    }
}
