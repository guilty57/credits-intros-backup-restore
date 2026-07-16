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
        /// per-series subfolders). Ignored if SaveNfoToMediaFolder is true
        /// or if AlsoWriteNfo is false.
        /// </summary>
        public string NfoBackupPath { get; set; } = string.Empty;

        /// <summary>
        /// If true, also write a companion .nfo file for each episode.
        /// </summary>
        public bool AlsoWriteNfo { get; set; } = false;

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

        public PluginConfiguration()
        {
        }
    }
}
