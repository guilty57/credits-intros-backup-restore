using System;
using System.Collections.Generic;
using System.IO;
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
        public string Description => "Restores intro/credits chapter markers from JSON/NFO files, either from the backup folder or next to the media.";
        public string Category => "Intro/Credits Backup & Restore (Open Source)";

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
            var jsonPathUsable = jsonUsesMediaFolder || !string.IsNullOrWhiteSpace(config.JsonBackupPath);

            if (!jsonPathUsable && !config.InsertIntoMediaNfo)
            {
                _logger.Warn("No JSON backup path configured and 'Insert into media NFO' is off - nothing to restore from.");
                return Task.CompletedTask;
            }

            if (jsonPathUsable && !jsonUsesMediaFolder && !Directory.Exists(config.JsonBackupPath))
            {
                _logger.Warn("JsonBackupPath does not exist - JSON restore will be skipped, continuing with media-NFO restore only if enabled.");
                jsonPathUsable = false;
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
            var matchedByTvdb = 0;
            var matchedByImdb = 0;
            var matchedByPath = 0;
            var skippedNoFile = 0;

            // Caches so a directory's JSON files are only scanned/parsed once,
            // even though media-folder mode revisits the same season folder
            // for every episode in it.
            var tvdbIndexCache = new Dictionary<string, Dictionary<(string, int, int), string>>();
            var imdbIndexCache = new Dictionary<string, Dictionary<(string, int, int), string>>();

            Dictionary<(string, int, int), string> GetOrBuildIndex(
                Dictionary<string, Dictionary<(string, int, int), string>> cache,
                string dir,
                Func<EpisodeIntroBackup, string?> idSelector)
            {
                if (cache.TryGetValue(dir, out var cached))
                {
                    return cached;
                }

                var index = new Dictionary<(string, int, int), string>();
                foreach (var file in Directory.GetFiles(dir, "*.json"))
                {
                    try
                    {
                        var parsed = JsonSerializer.Deserialize<EpisodeIntroBackup>(File.ReadAllText(file));
                        var id = parsed == null ? null : idSelector(parsed);
                        if (parsed != null && !string.IsNullOrEmpty(id))
                        {
                            var key = (id, parsed.SeasonNumber, parsed.EpisodeNumber);
                            if (!index.ContainsKey(key))
                            {
                                index[key] = file;
                            }
                        }
                    }
                    catch
                    {
                        // Unreadable/malformed file - ignore for indexing purposes,
                        // the direct path-match fallback may still find it later.
                    }
                }

                cache[dir] = index;
                return index;
            }

            foreach (var episode in episodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                processed++;
                progress.Report(100.0 * processed / Math.Max(total, 1));

                var tvdbId = episode.ProviderIds.GetValueOrDefault("Tvdb");
                var imdbId = episode.ProviderIds.GetValueOrDefault("Imdb");
                var seasonNumber = episode.ParentIndexNumber ?? 0;
                var episodeNumber = episode.IndexNumber ?? 0;
                var mediaFolder = string.IsNullOrEmpty(episode.Path) ? null : Path.GetDirectoryName(episode.Path);

                EpisodeIntroBackup? backup = null;
                var foundFile = false;
                var matchKind = string.Empty;

                if (jsonPathUsable)
                {
                    var jsonDir = jsonUsesMediaFolder ? mediaFolder : config.JsonBackupPath;
                    if (!string.IsNullOrEmpty(jsonDir) && Directory.Exists(jsonDir))
                    {
                        string? filePath = null;

                        // 1. Match by TVDB ID + season + episode (content-based -
                        // independent of filename, survives renames).
                        if (!string.IsNullOrEmpty(tvdbId))
                        {
                            var index = GetOrBuildIndex(tvdbIndexCache, jsonDir, b => b.TvdbId);
                            if (index.TryGetValue((tvdbId, seasonNumber, episodeNumber), out var match))
                            {
                                filePath = match;
                                matchKind = "tvdb";
                            }
                        }

                        // 2. Fall back to IMDB ID + season + episode, for content
                        // that isn't in TheTVDB's database.
                        if (filePath == null && !string.IsNullOrEmpty(imdbId))
                        {
                            var index = GetOrBuildIndex(imdbIndexCache, jsonDir, b => b.ImdbId);
                            if (index.TryGetValue((imdbId, seasonNumber, episodeNumber), out var match))
                            {
                                filePath = match;
                                matchKind = "imdb";
                            }
                        }

                        // 3. Last resort: same filename as the video itself. No ID
                        // needed at all here, but this breaks if the video file
                        // gets renamed after the backup was written.
                        if (filePath == null && !string.IsNullOrEmpty(episode.Path))
                        {
                            var candidate = Path.Combine(jsonDir, Path.ChangeExtension(Path.GetFileName(episode.Path), ".json"));
                            if (File.Exists(candidate))
                            {
                                filePath = candidate;
                                matchKind = "path";
                            }
                        }

                        if (filePath != null)
                        {
                            foundFile = true;
                            try
                            {
                                var json = File.ReadAllText(filePath);
                                backup = JsonSerializer.Deserialize<EpisodeIntroBackup>(json);
                            }
                            catch (Exception ex)
                            {
                                _logger.Error("Failed to parse backup file {0}: {1}", filePath, ex.Message);
                            }
                        }
                    }
                }

                if (backup == null && config.InsertIntoMediaNfo && mediaFolder != null && !string.IsNullOrEmpty(episode.Path))
                {
                    var mediaNfoPath = Path.ChangeExtension(episode.Path, ".nfo");
                    if (File.Exists(mediaNfoPath))
                    {
                        foundFile = true;
                        try
                        {
                            backup = TryReadMarkersFromNfo(mediaNfoPath);
                        }
                        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
                        {
                            _logger.Error("Failed to read markers from {0}: {1}", mediaNfoPath, ex.Message);
                        }
                    }
                }

                if (backup == null)
                {
                    if (!foundFile)
                    {
                        skippedNoFile++;
                    }
                    continue;
                }

                if (matchKind == "tvdb") matchedByTvdb++;
                else if (matchKind == "imdb") matchedByImdb++;
                else if (matchKind == "path") matchedByPath++;

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

            _logger.Info(
                "Intro/credits restore complete: {0} of {1} episode(s) had a backup applied "
                + "({2} by TVDB, {3} by IMDB, {4} by filename match). {5} skipped: no matching backup file.",
                restored, total, matchedByTvdb, matchedByImdb, matchedByPath, skippedNoFile);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Reads a top-level &lt;markers&gt; node (with &lt;introstart&gt;,
        /// &lt;introend&gt;, &lt;creditstart&gt; children, in ticks) from an
        /// existing NFO file, using the same schema as the original
        /// commercial "Intros Backup/Restore" plugin. Returns null if the
        /// file has no &lt;markers&gt; node at all. A value of 0 (or a
        /// missing child element) is treated as "not set", matching this
        /// plugin's own convention.
        /// </summary>
        private static EpisodeIntroBackup? TryReadMarkersFromNfo(string nfoPath)
        {
            var doc = new XmlDocument();
            doc.Load(nfoPath);

            var markers = doc.DocumentElement?.SelectSingleNode("markers");
            if (markers == null)
            {
                return null;
            }

            long? ReadTicks(string elementName)
            {
                var text = markers.SelectSingleNode(elementName)?.InnerText;
                if (string.IsNullOrWhiteSpace(text) || !long.TryParse(text, out var value) || value == 0)
                {
                    return null;
                }
                return value;
            }

            var backup = new EpisodeIntroBackup
            {
                IntroStartTicks = ReadTicks("introstart"),
                IntroEndTicks = ReadTicks("introend"),
                CreditsStartTicks = ReadTicks("creditstart")
            };

            if (backup.IntroStartTicks == null && backup.IntroEndTicks == null && backup.CreditsStartTicks == null)
            {
                return null;
            }

            return backup;
        }
    }
}
