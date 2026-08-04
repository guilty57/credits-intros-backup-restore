using System.IO;

namespace IntrosBackupReplacement
{
    /// <summary>
    /// Resolves where Backup/Restore (and the two background watchers) should
    /// read/write, based on the top-level Custom vs Automatic (Media Folder)
    /// mode and the mutually-exclusive sub-options within each. Centralizing
    /// this in one place means BackupIntrosTask, RestoreIntrosTask,
    /// AutoRestoreEntryPoint, and ManualEditWatcher all resolve paths the
    /// same way instead of four separate copies of the same branching.
    /// </summary>
    internal static class BackupModeResolver
    {
        public const string ModeCustom = "Custom";
        public const string ModeAutomatic = "AutomaticMediaFolder";

        public const string AutomaticRestoreNfo = "Nfo";
        public const string AutomaticRestoreJson = "Json";
        public const string AutomaticRestoreBest = "Best";

        /// <summary>Folder to write/read JSON in, or null if JSON isn't in play for Backup right now.</summary>
        public static string? GetBackupJsonDir(PluginConfiguration config, string? mediaFolder)
        {
            if (config.BackupMode == ModeCustom)
            {
                return config.CustomBackupJson && !string.IsNullOrWhiteSpace(config.JsonBackupPath)
                    ? config.JsonBackupPath
                    : null;
            }

            return config.AutomaticBackupJson ? mediaFolder : null;
        }

        /// <summary>True if Backup should insert markers into the video's existing (Emby-scraper) NFO. Automatic mode only.</summary>
        public static bool ShouldBackupIntoExistingNfo(PluginConfiguration config)
        {
            return config.BackupMode == ModeAutomatic && config.AutomaticBackupNfo;
        }

        /// <summary>
        /// Automatic mode only: folder for the extra "just in case" JSON
        /// safety copy (reuses JsonBackupPath), or null if not enabled.
        /// Independent of AutomaticBackupJson/Nfo above - Restore never
        /// reads from here, this is insurance only.
        /// </summary>
        public static string? GetAutomaticJsonSafetyCopyDir(PluginConfiguration config)
        {
            if (config.BackupMode != ModeAutomatic || !config.AutomaticJsonSafetyCopy)
            {
                return null;
            }

            return !string.IsNullOrWhiteSpace(config.JsonBackupPath) ? config.JsonBackupPath : null;
        }

        /// <summary>Folder to write the standalone (Custom-mode-only) NFO in, or null if not in play.</summary>
        public static string? GetCustomBackupNfoDir(PluginConfiguration config)
        {
            if (config.BackupMode != ModeCustom)
            {
                return null;
            }

            return config.CustomBackupNfo && !string.IsNullOrWhiteSpace(config.NfoBackupPath)
                ? config.NfoBackupPath
                : null;
        }

        /// <summary>Folder to read JSON from for Restore, or null if JSON isn't in play right now.</summary>
        public static string? GetRestoreJsonDir(PluginConfiguration config, string? mediaFolder)
        {
            if (config.BackupMode == ModeCustom)
            {
                return config.CustomRestoreJson && !string.IsNullOrWhiteSpace(config.JsonBackupPath)
                    ? config.JsonBackupPath
                    : null;
            }

            var source = config.AutomaticRestoreSource;
            return (source == AutomaticRestoreJson || source == AutomaticRestoreBest) ? mediaFolder : null;
        }

        /// <summary>True if Restore should read the video's existing (Emby-scraper) NFO. Automatic mode only.</summary>
        public static bool ShouldRestoreFromExistingNfo(PluginConfiguration config)
        {
            if (config.BackupMode != ModeAutomatic)
            {
                return false;
            }

            return config.AutomaticRestoreSource == AutomaticRestoreNfo || config.AutomaticRestoreSource == AutomaticRestoreBest;
        }

        /// <summary>Folder to read the standalone (Custom-mode-only) NFO from for Restore, or null if not in play.</summary>
        public static string? GetCustomRestoreNfoDir(PluginConfiguration config)
        {
            if (config.BackupMode != ModeCustom)
            {
                return null;
            }

            return config.CustomRestoreNfo && !string.IsNullOrWhiteSpace(config.NfoBackupPath)
                ? config.NfoBackupPath
                : null;
        }
    }
}
