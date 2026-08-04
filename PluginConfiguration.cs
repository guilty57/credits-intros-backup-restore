using MediaBrowser.Model.Plugins;

namespace IntrosBackupReplacement
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// Top-level mode, mutually exclusive: "Custom" (user-selected
        /// folder(s)) or "AutomaticMediaFolder" (next to each video).
        /// Controls both where the scheduled Backup/Restore tasks read and
        /// write, and which folder ManualEditWatcher/AutoRestoreEntryPoint
        /// target - those two keep running in either mode, just aimed at a
        /// different location depending on this setting.
        /// </summary>
        public string BackupMode { get; set; } = "AutomaticMediaFolder";

        // ------------------------------------------------------------------
        // Custom mode
        // ------------------------------------------------------------------

        /// <summary>
        /// Folder where per-episode JSON backups are stored (flat, no
        /// per-series subfolders). Custom mode only.
        /// </summary>
        public string JsonBackupPath { get; set; } = string.Empty;

        /// <summary>
        /// Folder where per-episode standalone NFO backups are stored (flat).
        /// This is a plugin-owned file containing only a &lt;markers&gt;
        /// element - unrelated to Emby's own scraper-generated NFO next to
        /// the video. Custom mode only. Can be the same folder as
        /// JsonBackupPath or a different one.
        /// </summary>
        public string NfoBackupPath { get; set; } = string.Empty;

        /// <summary>Custom mode: write JSON to JsonBackupPath on Backup.</summary>
        public bool CustomBackupJson { get; set; } = true;

        /// <summary>Custom mode: write the standalone NFO to NfoBackupPath on Backup.</summary>
        public bool CustomBackupNfo { get; set; } = false;

        /// <summary>Custom mode: read JSON from JsonBackupPath on Restore.</summary>
        public bool CustomRestoreJson { get; set; } = true;

        /// <summary>Custom mode: read the standalone NFO from NfoBackupPath on Restore.</summary>
        public bool CustomRestoreNfo { get; set; } = false;

        // ------------------------------------------------------------------
        // Automatic (Media Folder) mode
        // ------------------------------------------------------------------

        /// <summary>Automatic mode: write JSON next to the video on Backup. Independent of AutomaticBackupNfo - check both for what used to be "Both".</summary>
        public bool AutomaticBackupJson { get; set; } = true;

        /// <summary>Automatic mode: insert markers into the video's existing Emby-scraper NFO on Backup. Independent of AutomaticBackupJson.</summary>
        public bool AutomaticBackupNfo { get; set; } = false;

        /// <summary>
        /// Automatic mode only: in addition to whichever of the two options
        /// above are selected, also write a plain JSON safety copy into
        /// JsonBackupPath (the same field Custom mode's JSON option uses).
        /// This is a "just in case" extra copy only - Restore never reads
        /// from it, it exists purely as insurance.
        /// </summary>
        public bool AutomaticJsonSafetyCopy { get; set; } = false;

        /// <summary>
        /// Automatic mode Restore source, mutually exclusive: "Nfo", "Json",
        /// or "Best" (field-level + newer-file-wins merge of both - the
        /// suggested/default option).
        /// </summary>
        public string AutomaticRestoreSource { get; set; } = "Best";

        // ------------------------------------------------------------------
        // Shared - apply in both modes, just target a different folder
        // ------------------------------------------------------------------

        /// <summary>
        /// If true, the plugin subscribes to Emby's item-updated event and
        /// automatically re-applies backed-up intro/credits markers to an
        /// episode the moment they're wiped by a Library Scan or Refresh
        /// Metadata, instead of requiring a manual/scheduled Restore run.
        /// Off by default - this is a bigger behavioral change than the rest
        /// of the plugin's opt-in settings.
        /// </summary>
        public bool EnableAutoRestore { get; set; } = false;

        /// <summary>
        /// If true, the plugin watches the Emby server log for signs of a
        /// manual chapter edit made through ChapterApi, EmbyCredits, Segment
        /// Reporting, or Emby's own native intro detection - none of which
        /// raise the event EnableAutoRestore listens for - and automatically
        /// writes a fresh backup (or, for native intro detection, backs up
        /// the new intro and reapplies any existing credits marker). Off by
        /// default - this is separate from and independent of
        /// EnableAutoRestore.
        /// </summary>
        public bool AutoBackupOnManualEdit { get; set; } = false;

        public PluginConfiguration()
        {
        }
    }
}
