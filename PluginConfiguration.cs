using MediaBrowser.Model.Plugins;

namespace IntrosBackupReplacement
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// Folder where per-episode JSON (and optionally NFO) backups are stored.
        /// Files are written flat here - no per-series subfolders.
        /// </summary>
        public string IntroBackupPath { get; set; } = string.Empty;

        /// <summary>
        /// If true, also write a companion .nfo file next to each JSON backup.
        /// </summary>
        public bool AlsoWriteNfo { get; set; } = false;

        public PluginConfiguration()
        {
        }
    }
}
