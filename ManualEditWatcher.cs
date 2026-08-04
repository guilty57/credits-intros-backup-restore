using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using IntrosBackupReplacement.Models;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace IntrosBackupReplacement
{
    /// <summary>
    /// Watches Emby's own server log for signs of a chapter edit that our
    /// AutoRestoreEntryPoint can't see, because the tool that made it calls
    /// IItemRepository.SaveChapters directly without going through
    /// ILibraryManager.ItemUpdated - confirmed by decompiling and live-testing
    /// ChapterApi, EmbyCredits, and Segment Reporting: none of them raise the
    /// event. Emby's own native "Detect Episode Intros" task behaves the
    /// same way for its own write step (its initial wipe-and-rescan DOES
    /// fire the event, but the detection results it writes afterward do not).
    ///
    /// This is inherently fragile - it depends on each tool's specific log
    /// message format, which could change in a future release of that tool,
    /// and it only covers the specific tools it has a signature for.
    ///
    /// Two different reactions depending on the source:
    /// - ChapterApi / EmbyCredits / Segment Reporting (a human manually
    ///   correcting a marker): capture whatever is now live and write it as
    ///   a fresh backup. The human's edit is authoritative - nothing else
    ///   is touched.
    /// - Emby's native intro detection (an automated process that only
    ///   knows about Intro, never Credits): back up the newly-detected
    ///   Intro values, then immediately trigger a restore for that episode
    ///   so any Credits value already in the backup gets reapplied via the
    ///   same field-level merge AutoRestoreEntryPoint uses (the merge means
    ///   the Intro values we just wrote are left alone since they already
    ///   match - only the missing Credits gets filled back in).
    ///
    /// Off by default via PluginConfiguration.AutoBackupOnManualEdit.
    /// </summary>
    public class ManualEditWatcher : IServerEntryPoint
    {
        private static readonly Regex ChapterApiPattern = new(
            @"chapter_api/update_chapters\?id=(\d+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex EmbyCreditsPattern = new(
            @"Updated credits marker for episode '([^']+)' to",
            RegexOptions.Compiled);

        private static readonly Regex SegmentReportingPattern = new(
            @"UpdateSegment:\s*itemId=(\d+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex NativeDetectionPattern = new(
            @"Caching intro fingerprint as extradata for (\d+)",
            RegexOptions.Compiled);

        private static readonly TimeSpan DebounceWindow = TimeSpan.FromSeconds(3);

        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly IApplicationPaths _applicationPaths;
        private readonly ILogger _logger;

        private readonly Dictionary<long, DateTime> _lastHandled = new();
        private readonly object _debounceLock = new();

        private CancellationTokenSource? _cts;

        public ManualEditWatcher(ILibraryManager libraryManager, IItemRepository itemRepository, IApplicationPaths applicationPaths, ILogManager logManager)
        {
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _applicationPaths = applicationPaths;
            _logger = logManager.GetLogger(nameof(ManualEditWatcher));
        }

        public void Run()
        {
            _cts = new CancellationTokenSource();
            Task.Run(() => TailLoop(_cts.Token));
        }

        private void TailLoop(CancellationToken token)
        {
            var logPath = Path.Combine(_applicationPaths.LogDirectoryPath, "embyserver.txt");
            long position = 0;

            try
            {
                if (File.Exists(logPath))
                {
                    using var initial = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    position = initial.Length;
                }
            }
            catch (Exception ex)
            {
                _logger.Error("ManualEditWatcher: failed to open log file initially: {0}", ex.Message);
            }

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var config = Plugin.Instance?.Configuration;
                    if (config == null || !config.AutoBackupOnManualEdit)
                    {
                        Thread.Sleep(2000);
                        continue;
                    }

                    if (!File.Exists(logPath))
                    {
                        Thread.Sleep(2000);
                        continue;
                    }

                    using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        if (fs.Length < position)
                        {
                            position = 0;
                        }

                        fs.Seek(position, SeekOrigin.Begin);
                        using var reader = new StreamReader(fs);
                        string? line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            HandleLogLine(line);
                        }
                        position = fs.Position;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error("ManualEditWatcher: error while tailing log: {0}", ex.Message);
                }

                Thread.Sleep(1000);
            }
        }

        private void HandleLogLine(string line)
        {
            var chapterApiMatch = ChapterApiPattern.Match(line);
            if (chapterApiMatch.Success && long.TryParse(chapterApiMatch.Groups[1].Value, out var chapterApiId))
            {
                if (ShouldHandle(chapterApiId) && _libraryManager.GetItemById(chapterApiId) is Episode ep1)
                {
                    BackupLiveChapters(ep1);
                }
                return;
            }

            var segmentMatch = SegmentReportingPattern.Match(line);
            if (segmentMatch.Success && long.TryParse(segmentMatch.Groups[1].Value, out var segmentId))
            {
                if (ShouldHandle(segmentId) && _libraryManager.GetItemById(segmentId) is Episode ep2)
                {
                    BackupLiveChapters(ep2);
                }
                return;
            }

            var creditsMatch = EmbyCreditsPattern.Match(line);
            if (creditsMatch.Success)
            {
                var title = creditsMatch.Groups[1].Value;
                var episode = FindEpisodeByTitle(title);
                if (episode != null && ShouldHandle(episode.InternalId))
                {
                    BackupLiveChapters(episode);
                }
                return;
            }

            var nativeMatch = NativeDetectionPattern.Match(line);
            if (nativeMatch.Success && long.TryParse(nativeMatch.Groups[1].Value, out var nativeId))
            {
                if (ShouldHandle(nativeId) && _libraryManager.GetItemById(nativeId) is Episode ep3)
                {
                    HandleNativeDetection(ep3);
                }
            }
        }

        private bool ShouldHandle(long itemId)
        {
            lock (_debounceLock)
            {
                if (_lastHandled.TryGetValue(itemId, out var last) && DateTime.UtcNow - last < DebounceWindow)
                {
                    return false;
                }
                _lastHandled[itemId] = DateTime.UtcNow;
                return true;
            }
        }

        private Episode? FindEpisodeByTitle(string title)
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Episode) },
                Recursive = true
            };
            return _libraryManager.GetItemList(query).OfType<Episode>().FirstOrDefault(e => e.Name == title);
        }

        /// <summary>
        /// A human manually corrected a marker via ChapterApi, EmbyCredits,
        /// or Segment Reporting. Their edit is authoritative - capture
        /// whatever is now live for all three marker types and write it as
        /// a fresh backup, no merge logic needed here.
        /// </summary>
        private void BackupLiveChapters(Episode episode)
        {
            try
            {
                var config = Plugin.Instance!.Configuration;
                var current = _itemRepository.GetChapters(episode);

                var backup = new EpisodeIntroBackup
                {
                    TvdbId = episode.ProviderIds.GetValueOrDefault("Tvdb"),
                    ImdbId = episode.ProviderIds.GetValueOrDefault("Imdb"),
                    TmdbId = episode.ProviderIds.GetValueOrDefault("Tmdb"),
                    SeriesName = episode.SeriesName ?? "Unknown Series",
                    SeasonNumber = episode.ParentIndexNumber ?? 0,
                    EpisodeNumber = episode.IndexNumber ?? 0,
                    EpisodeTitle = episode.Name ?? string.Empty,
                    IntroStartTicks = current.FirstOrDefault(c => c.MarkerType == MarkerType.IntroStart)?.StartPositionTicks,
                    IntroEndTicks = current.FirstOrDefault(c => c.MarkerType == MarkerType.IntroEnd)?.StartPositionTicks,
                    CreditsStartTicks = current.FirstOrDefault(c => c.MarkerType == MarkerType.CreditsStart)?.StartPositionTicks
                };

                if (backup.IntroStartTicks == null && backup.IntroEndTicks == null && backup.CreditsStartTicks == null)
                {
                    return;
                }

                WriteBackupFile(episode, backup, config);
                _logger.Debug("Detected a manual chapter edit for {0} - wrote a fresh backup.", episode.Path ?? episode.Name ?? episode.InternalId.ToString());
            }
            catch (Exception ex)
            {
                _logger.Error("ManualEditWatcher: failed to auto-backup item {0}: {1}", episode.InternalId, ex.Message);
            }
        }

        /// <summary>
        /// Emby's native intro detection just wrote new Intro values (it
        /// never touches Credits). Back those up, then immediately trigger
        /// a restore for this episode so any Credits value already in the
        /// backup gets reapplied via the same field-level merge
        /// AutoRestoreEntryPoint uses - the Intro values we just wrote are
        /// left alone since they already match what we just backed up, only
        /// a missing Credits gets filled back in.
        /// </summary>
        private void HandleNativeDetection(Episode episode)
        {
            try
            {
                var config = Plugin.Instance!.Configuration;
                var current = _itemRepository.GetChapters(episode);
                var introStart = current.FirstOrDefault(c => c.MarkerType == MarkerType.IntroStart)?.StartPositionTicks;
                var introEnd = current.FirstOrDefault(c => c.MarkerType == MarkerType.IntroEnd)?.StartPositionTicks;

                if (!introStart.HasValue && !introEnd.HasValue)
                {
                    return;
                }

                var backup = LoadExistingBackupOrNew(episode, config);
                backup.IntroStartTicks = introStart;
                backup.IntroEndTicks = introEnd;

                WriteBackupFile(episode, backup, config);
                _logger.Debug("Backed up newly-detected intro for {0}.", episode.Path ?? episode.Name ?? episode.InternalId.ToString());

                if (EpisodeRestoreHelper.ApplyBackup(episode, backup, _itemRepository))
                {
                    _logger.Debug("Reapplied credits marker for {0} after intro detection overwrote it.", episode.Path ?? episode.Name ?? episode.InternalId.ToString());
                }
            }
            catch (Exception ex)
            {
                _logger.Error("ManualEditWatcher: failed to handle native detection for item {0}: {1}", episode.InternalId, ex.Message);
            }
        }

        private EpisodeIntroBackup LoadExistingBackupOrNew(Episode episode, PluginConfiguration config)
        {
            var jsonPath = GetJsonPath(episode, config);
            if (jsonPath != null && File.Exists(jsonPath))
            {
                try
                {
                    var existing = JsonSerializer.Deserialize<EpisodeIntroBackup>(File.ReadAllText(jsonPath));
                    if (existing != null)
                    {
                        return existing;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error("Failed to read existing backup {0}: {1}", jsonPath, ex.Message);
                }
            }

            return new EpisodeIntroBackup
            {
                TvdbId = episode.ProviderIds.GetValueOrDefault("Tvdb"),
                ImdbId = episode.ProviderIds.GetValueOrDefault("Imdb"),
                TmdbId = episode.ProviderIds.GetValueOrDefault("Tmdb"),
                SeriesName = episode.SeriesName ?? "Unknown Series",
                SeasonNumber = episode.ParentIndexNumber ?? 0,
                EpisodeNumber = episode.IndexNumber ?? 0,
                EpisodeTitle = episode.Name ?? string.Empty
            };
        }

        private static string? GetJsonPath(Episode episode, PluginConfiguration config)
        {
            if (string.IsNullOrEmpty(episode.Path))
            {
                return null;
            }

            var mediaFolder = Path.GetDirectoryName(episode.Path);
            var jsonDir = BackupModeResolver.GetBackupJsonDir(config, mediaFolder);
            if (string.IsNullOrEmpty(jsonDir))
            {
                return null;
            }

            return Path.Combine(jsonDir, Path.ChangeExtension(Path.GetFileName(episode.Path), ".json"));
        }

        private void WriteBackupFile(Episode episode, EpisodeIntroBackup backup, PluginConfiguration config)
        {
            var jsonPath = GetJsonPath(episode, config);
            if (jsonPath != null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
                    var json = JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(jsonPath, json);
                }
                catch (Exception ex)
                {
                    _logger.Error("Failed to write backup {0}: {1}", jsonPath, ex.Message);
                }
            }

            if (BackupModeResolver.ShouldBackupIntoExistingNfo(config) && !string.IsNullOrEmpty(episode.Path))
            {
                var mediaNfoPath = Path.ChangeExtension(episode.Path, ".nfo");
                if (File.Exists(mediaNfoPath))
                {
                    try
                    {
                        InsertMarkersIntoExistingNfo(backup, mediaNfoPath);
                    }
                    catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
                    {
                        _logger.Error("Failed to insert markers into NFO {0}: {1}", mediaNfoPath, ex.Message);
                    }
                }
            }

            var customNfoDir = BackupModeResolver.GetCustomBackupNfoDir(config);
            if (customNfoDir != null && !string.IsNullOrEmpty(episode.Path))
            {
                try
                {
                    Directory.CreateDirectory(customNfoDir);
                    var standaloneNfoPath = StandaloneNfo.GetPath(customNfoDir, episode.Path);
                    StandaloneNfo.Write(standaloneNfoPath, backup);
                }
                catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
                {
                    _logger.Error("Failed to write standalone NFO for {0}: {1}", episode.Path, ex.Message);
                }
            }
        }

        private static void InsertMarkersIntoExistingNfo(EpisodeIntroBackup backup, string nfoPath)
        {
            var doc = new XmlDocument();
            doc.Load(nfoPath);

            var root = doc.DocumentElement;
            if (root == null)
            {
                return;
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

        public void Dispose()
        {
            _cts?.Cancel();
            GC.SuppressFinalize(this);
        }
    }
}
