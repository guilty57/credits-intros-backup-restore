using MediaBrowser.Model.Plugins;

namespace IntrosBackupReplacement
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// Folder where per-episode JSON backups are stored (flat, no
        /// per-series subfolders). Ignored if SaveJsonToMediaFolder is true.
        /// </summary>
        public string JsonBackupPath { get; set; } = string.Empty;

        /// <summary>
        /// Folder where per-episode NFO backups are stored (flat, no
        /// per-series subfolders). Ignored if SaveNfoToMediaFolder is true.
        /// Leave this and SaveNfoToMediaFolder both empty/off to skip NFO
        /// output entirely.
        /// </summary>
        public string NfoBackupPath { get; set; } = string.Empty;

        /// <summary>
        /// If true, write each episode's JSON backup into its own media
        /// folder (next to the video file) instead of JsonBackupPath.
        /// Overrides JsonBackupPath.
        /// </summary>
        public bool SaveJsonToMediaFolder { get; set; } = false;

        /// <summary>
        /// If true, write each episode's NFO backup into its own media
        /// folder (next to the video file) instead of NfoBackupPath.
        /// Overrides NfoBackupPath.
        /// </summary>
        public bool SaveNfoToMediaFolder { get; set; } = false;

        /// <summary>
        /// If true, also insert/update a &lt;markers&gt; node directly inside
        /// the media's own existing NFO file (named after the video itself,
        /// e.g. "Episode Title.nfo" - not our own {Series} ({TvdbId})... file).
        /// Only touches a file that already exists (typically written by the
        /// NfoMetadata plugin) - never creates one from scratch. Uses the
        /// same &lt;markers&gt;&lt;introstart&gt;/&lt;introend&gt;/&lt;creditstart&gt;
        /// schema as the original commercial "Intros Backup/Restore" plugin,
        /// for interoperability with existing backups made by it. This is in
        /// addition to, not a replacement for, the JSON backup above.
        /// </summary>
        public bool InsertIntoMediaNfo { get; set; } = false;

        public PluginConfiguration()
        {
        }
    }
}
