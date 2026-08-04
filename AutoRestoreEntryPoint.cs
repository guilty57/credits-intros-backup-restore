using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Xml;
using IntrosBackupReplacement.Models;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace IntrosBackupReplacement
{
    /// <summary>
    /// Subscribes to Emby's item-updated event so that whenever an episode's
    /// chapters change - whether from a Library Scan or a Refresh Metadata,
    /// either one wipes IntroStart/IntroEnd/CreditsStart the same way - this
    /// plugin notices immediately and re-applies the backed-up markers,
    /// instead of relying on the user to remember to run the Restore task
    /// (or on "Lock this item", which only protects against Library Scan,
    /// not Refresh Metadata - confirmed by direct testing).
    ///
    /// Opt-in via PluginConfiguration.EnableAutoRestore (default off), since
    /// this changes the plugin's runtime behavior significantly and is new/
    /// less battle-tested than the scheduled-task-based restore.
    ///
    /// Auto-discovered by Emby via reflection (implementing IServerEntryPoint
    /// is enough - no explicit registration needed, same as IScheduledTask).
    /// </summary>
    public class AutoRestoreEntryPoint : IServerEntryPoint
    {
        private static readonly TimeSpan IndexCacheLifetime = TimeSpan.FromMinutes(5);

        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly ILogger _logger;

        // Guards against our own SaveChapters call re-triggering this same
        // handler for the same item while we're still processing it.
        private readonly ConcurrentDictionary<long, bool> _processing = new();

        // Per-directory index cache (dir -> built-at time, TvdbId index, ImdbId
        // index) so a big Refresh Metadata run - which can fire one
        // ItemUpdated event per episode - doesn't re-scan the same folder's
        // JSON files hundreds of times over. Rebuilt automatically once the
        // TTL expires, so a fresh Backup run's changes still get picked up
        // without requiring a server restart.
        private readonly object _cacheLock = new();
        private readonly Dictionary<string, (DateTime BuiltAt, Dictionary<(string, int, int), string> Tvdb, Dictionary<(string, int, int), string> Imdb)> _indexCache = new();

        public AutoRestoreEntryPoint(ILibraryManager libraryManager, IItemRepository itemRepository, ILogManager logManager)
        {
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _logger = logManager.GetLogger(nameof(AutoRestoreEntryPoint));
        }

        public void Run()
        {
            _libraryManager.ItemUpdated += OnItemUpdated;
        }

        private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
        {
            try
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null || !config.EnableAutoRestore)
                {
                    return;
                }

                if (e.Item is not Episode episode)
                {
                    return;
                }

                if (!_processing.TryAdd(episode.InternalId, true))
                {
                    // Already handling this item - most likely our own
                    // SaveChapters call below looping back into this same
                    // handler. Skip to avoid recursion.
                    return;
                }

                try
                {
                    ProcessEpisode(episode, config);
                }
                finally
                {
                    _processing.TryRemove(episode.InternalId, out _);
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Auto-restore handler failed: {0}", ex.Message);
            }
        }

        private void ProcessEpisode(Episode episode, PluginConfiguration config)
        {
            var tvdbId = episode.ProviderIds.GetValueOrDefault("Tvdb");
            var imdbId = episode.ProviderIds.GetValueOrDefault("Imdb");
            var seasonNumber = episode.ParentIndexNumber ?? 0;
            var episodeNumber = episode.IndexNumber ?? 0;
            var mediaFolder = string.IsNullOrEmpty(episode.Path) ? null : Path.GetDirectoryName(episode.Path);

            EpisodeIntroBackup? jsonBackup = null;
            DateTime? jsonWriteTimeUtc = null;
            EpisodeIntroBackup? nfoBackup = null;
            DateTime? nfoWriteTimeUtc = null;

            var jsonDir = BackupModeResolver.GetRestoreJsonDir(config, mediaFolder);
            if (!string.IsNullOrEmpty(jsonDir) && Directory.Exists(jsonDir))
            {
                string? filePath = null;

                // 1. TVDB ID + season + episode (content-based).
                if (!string.IsNullOrEmpty(tvdbId))
                {
                    var index = GetOrBuildIndex(jsonDir, b => b.TvdbId, tvdb: true);
                    if (index.TryGetValue((tvdbId, seasonNumber, episodeNumber), out var match))
                    {
                        filePath = match;
                    }
                }

                // 2. IMDB ID + season + episode (content-based).
                if (filePath == null && !string.IsNullOrEmpty(imdbId))
                {
                    var index = GetOrBuildIndex(jsonDir, b => b.ImdbId, tvdb: false);
                    if (index.TryGetValue((imdbId, seasonNumber, episodeNumber), out var match))
                    {
                        filePath = match;
                    }
                }

                // 3. Last resort: same filename as the video itself.
                if (filePath == null && !string.IsNullOrEmpty(episode.Path))
                {
                    var candidate = Path.Combine(jsonDir, Path.ChangeExtension(Path.GetFileName(episode.Path), ".json"));
                    if (File.Exists(candidate))
                    {
                        filePath = candidate;
                    }
                }

                if (filePath != null)
                {
                    try
                    {
                        jsonBackup = JsonSerializer.Deserialize<EpisodeIntroBackup>(File.ReadAllText(filePath));
                        jsonWriteTimeUtc = File.GetLastWriteTimeUtc(filePath);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("Auto-restore: failed to parse {0}: {1}", filePath, ex.Message);
                    }
                }
            }

            // NFO - Automatic mode reads the video's existing Emby-scraper
            // NFO; Custom mode reads the plugin's own standalone NFO.
            // Checked independently of whether a JSON match was found - a
            // JSON backup written before intro detection ran (Intro fields
            // null) must not permanently block a more complete NFO from ever
            // being consulted. The two are merged field-by-field below
            // rather than JSON taking all-or-nothing priority.
            if (BackupModeResolver.ShouldRestoreFromExistingNfo(config) && !string.IsNullOrEmpty(episode.Path))
            {
                var mediaNfoPath = Path.ChangeExtension(episode.Path, ".nfo");
                if (File.Exists(mediaNfoPath))
                {
                    try
                    {
                        nfoBackup = ReadExistingNfoWithRetry(mediaNfoPath, out nfoWriteTimeUtc);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _logger.Error("Auto-restore: failed to read markers from {0}: {1}", mediaNfoPath, ex.Message);
                    }
                }
            }
            else
            {
                var customNfoDir = BackupModeResolver.GetCustomRestoreNfoDir(config);
                if (customNfoDir != null && !string.IsNullOrEmpty(episode.Path))
                {
                    var standaloneNfoPath = StandaloneNfo.GetPath(customNfoDir, episode.Path);
                    if (File.Exists(standaloneNfoPath))
                    {
                        try
                        {
                            nfoBackup = StandaloneNfo.Read(standaloneNfoPath);
                            nfoWriteTimeUtc = File.GetLastWriteTimeUtc(standaloneNfoPath);
                        }
                        catch (Exception ex)
                        {
                            _logger.Error("Auto-restore: failed to read standalone NFO {0}: {1}", standaloneNfoPath, ex.Message);
                        }
                    }
                }
            }

            var backup = EpisodeRestoreHelper.MergeSources(jsonBackup, jsonWriteTimeUtc, nfoBackup, nfoWriteTimeUtc);

            if (backup == null)
            {
                return;
            }

            var wasApplied = EpisodeRestoreHelper.ApplyBackup(episode, backup, _itemRepository);
            if (wasApplied)
            {
                _logger.Debug("Auto-restored intro/credits markers for {0} after an external update wiped them.", episode.Path ?? episode.Name ?? episode.InternalId.ToString());
            }
        }

        private Dictionary<(string, int, int), string> GetOrBuildIndex(string dir, Func<EpisodeIntroBackup, string?> idSelector, bool tvdb)
        {
            lock (_cacheLock)
            {
                if (_indexCache.TryGetValue(dir, out var cached) && DateTime.UtcNow - cached.BuiltAt < IndexCacheLifetime)
                {
                    return tvdb ? cached.Tvdb : cached.Imdb;
                }

                var tvdbIndex = new Dictionary<(string, int, int), string>();
                var imdbIndex = new Dictionary<(string, int, int), string>();

                foreach (var file in Directory.GetFiles(dir, "*.json"))
                {
                    try
                    {
                        var parsed = JsonSerializer.Deserialize<EpisodeIntroBackup>(File.ReadAllText(file));
                        if (parsed == null)
                        {
                            continue;
                        }

                        if (!string.IsNullOrEmpty(parsed.TvdbId))
                        {
                            var key = (parsed.TvdbId, parsed.SeasonNumber, parsed.EpisodeNumber);
                            if (!tvdbIndex.ContainsKey(key))
                            {
                                tvdbIndex[key] = file;
                            }
                        }

                        if (!string.IsNullOrEmpty(parsed.ImdbId))
                        {
                            var key = (parsed.ImdbId, parsed.SeasonNumber, parsed.EpisodeNumber);
                            if (!imdbIndex.ContainsKey(key))
                            {
                                imdbIndex[key] = file;
                            }
                        }
                    }
                    catch
                    {
                        // Unreadable/malformed file - skip it for indexing.
                    }
                }

                _indexCache[dir] = (DateTime.UtcNow, tvdbIndex, imdbIndex);
                return tvdb ? tvdbIndex : imdbIndex;
            }
        }

        /// <summary>
        /// Reads the video's existing NFO, retrying a couple of times on a
        /// transient XmlException before giving up. Emby's own NfoMetadata
        /// scraper isn't guaranteed to write the file atomically - during a
        /// Refresh Metadata it can briefly leave the file empty/partial
        /// while it's being rewritten, and if our event handler happens to
        /// read at that exact moment, XmlDocument.Load sees an empty file
        /// ("Root element is missing") even though the file is fine a
        /// fraction of a second later. That's a timing race, not a real
        /// error, so it's worth a couple of short retries instead of
        /// treating the first attempt as final and silently losing the
        /// restore for this event.
        /// </summary>
        private EpisodeIntroBackup? ReadExistingNfoWithRetry(string nfoPath, out DateTime? writeTimeUtc)
        {
            const int maxAttempts = 3;
            const int retryDelayMs = 300;
            XmlException? lastError = null;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    var result = TryReadMarkersFromNfo(nfoPath);
                    writeTimeUtc = File.GetLastWriteTimeUtc(nfoPath);
                    return result;
                }
                catch (XmlException ex)
                {
                    lastError = ex;
                    if (attempt < maxAttempts)
                    {
                        Thread.Sleep(retryDelayMs);
                    }
                }
            }

            _logger.Error("Auto-restore: failed to read markers from {0} after {1} attempts (likely a scraper write race): {2}", nfoPath, maxAttempts, lastError?.Message);
            writeTimeUtc = null;
            return null;
        }

        private static EpisodeIntroBackup? TryReadMarkersFromNfo(string nfoPath)
        {
            var doc = new System.Xml.XmlDocument();
            doc.Load(nfoPath);

            var markers = doc.DocumentElement?.SelectSingleNode("markers");
            if (markers == null)
            {
                return null;
            }

            long? ReadRaw(string elementName)
            {
                var text = markers.SelectSingleNode(elementName)?.InnerText;
                if (string.IsNullOrWhiteSpace(text) || !long.TryParse(text, out var value))
                {
                    return null;
                }
                return value;
            }

            var rawIntroStart = ReadRaw("introstart");
            var rawIntroEnd = ReadRaw("introend");
            var rawCreditsStart = ReadRaw("creditstart");

            // introstart/introend are only treated as "not detected" when BOTH
            // are zero/missing - a genuine intro starting at the very first
            // frame (IntroStart == 0) is otherwise indistinguishable from "no
            // intro" in the NFO, since the schema has no way to represent
            // "absent" separately from zero. Checking the pair resolves this:
            // if either field is non-zero, the intro is real and a
            // IntroStart == 0 value is preserved rather than dropped.
            long? introStart = null;
            long? introEnd = null;
            if ((rawIntroStart ?? 0) != 0 || (rawIntroEnd ?? 0) != 0)
            {
                introStart = rawIntroStart ?? 0;
                introEnd = rawIntroEnd ?? 0;
            }

            // CreditsStart == 0 isn't a realistic case (credits never start at
            // the very first frame), so the original zero-means-absent rule
            // stays as-is here.
            var creditsStart = (rawCreditsStart.HasValue && rawCreditsStart.Value != 0) ? rawCreditsStart : null;

            var backup = new EpisodeIntroBackup
            {
                IntroStartTicks = introStart,
                IntroEndTicks = introEnd,
                CreditsStartTicks = creditsStart
            };

            if (backup.IntroStartTicks == null && backup.IntroEndTicks == null && backup.CreditsStartTicks == null)
            {
                return null;
            }

            return backup;
        }

        public void Dispose()
        {
            _libraryManager.ItemUpdated -= OnItemUpdated;
            GC.SuppressFinalize(this);
        }
    }
}
