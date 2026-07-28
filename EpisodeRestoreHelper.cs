using System.Collections.Generic;
using System.Linq;
using IntrosBackupReplacement.Models;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;

namespace IntrosBackupReplacement
{
    /// <summary>
    /// Applies a backup to an episode's live chapters using a field-level
    /// merge: only the marker types the backup actually has a value for are
    /// overwritten. A field the backup has no opinion on (null) is left
    /// exactly as it currently is - it is never treated as "wrong" and is
    /// carried forward untouched rather than being dropped. This is what
    /// lets a partial backup (e.g. one written before intro detection had
    /// run, containing only CreditsStart) coexist with a newer live value
    /// for a field it doesn't know about (e.g. a freshly-detected
    /// IntroStart/IntroEnd) instead of erasing it.
    /// </summary>
    internal static class EpisodeRestoreHelper
    {
        /// <summary>
        /// Returns true if anything was actually written (i.e. at least one
        /// field needed correcting), false if the episode's live chapters
        /// already matched the backup for every field the backup has an
        /// opinion on (no-op, SaveChapters is not called).
        /// </summary>
        public static bool ApplyBackup(Episode episode, EpisodeIntroBackup backup, IItemRepository itemRepository)
        {
            var current = itemRepository.GetChapters(episode);
            var currentIntroStart = current.FirstOrDefault(c => c.MarkerType == MarkerType.IntroStart)?.StartPositionTicks;
            var currentIntroEnd = current.FirstOrDefault(c => c.MarkerType == MarkerType.IntroEnd)?.StartPositionTicks;
            var currentCreditsStart = current.FirstOrDefault(c => c.MarkerType == MarkerType.CreditsStart)?.StartPositionTicks;

            var introStartOk = !backup.IntroStartTicks.HasValue || currentIntroStart == backup.IntroStartTicks;
            var introEndOk = !backup.IntroEndTicks.HasValue || currentIntroEnd == backup.IntroEndTicks;
            var creditsStartOk = !backup.CreditsStartTicks.HasValue || currentCreditsStart == backup.CreditsStartTicks;

            if (introStartOk && introEndOk && creditsStartOk)
            {
                return false;
            }

            var chapters = new List<ChapterInfo>();

            if (backup.IntroStartTicks.HasValue)
            {
                chapters.Add(new ChapterInfo { StartPositionTicks = backup.IntroStartTicks.Value, MarkerType = MarkerType.IntroStart, Name = "Intro Start" });
            }
            else
            {
                var live = current.FirstOrDefault(c => c.MarkerType == MarkerType.IntroStart);
                if (live != null)
                {
                    chapters.Add(live);
                }
            }

            if (backup.IntroEndTicks.HasValue)
            {
                chapters.Add(new ChapterInfo { StartPositionTicks = backup.IntroEndTicks.Value, MarkerType = MarkerType.IntroEnd, Name = "Intro End" });
            }
            else
            {
                var live = current.FirstOrDefault(c => c.MarkerType == MarkerType.IntroEnd);
                if (live != null)
                {
                    chapters.Add(live);
                }
            }

            if (backup.CreditsStartTicks.HasValue)
            {
                chapters.Add(new ChapterInfo { StartPositionTicks = backup.CreditsStartTicks.Value, MarkerType = MarkerType.CreditsStart, Name = "Credits Start" });
            }
            else
            {
                var live = current.FirstOrDefault(c => c.MarkerType == MarkerType.CreditsStart);
                if (live != null)
                {
                    chapters.Add(live);
                }
            }

            if (chapters.Count == 0)
            {
                return false;
            }

            var existingPlain = current.Where(c => c.MarkerType == MarkerType.Chapter).ToList();
            var merged = existingPlain.Concat(chapters).OrderBy(c => c.StartPositionTicks).ToList();

            itemRepository.SaveChapters(episode.InternalId, merged);
            return true;
        }
    }
}
