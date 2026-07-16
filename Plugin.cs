using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace IntrosBackupReplacement
{
    /// <summary>
    /// Independent replacement for the abandoned "Intro/Credits Backupper" plugin.
    /// Backs up and restores intro/credits chapter markers as one JSON file per
    /// episode, stored flat under a configurable backup path.
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public static Plugin? Instance { get; private set; }

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public override Guid Id => Guid.Parse("7602a9a8-788d-4007-84c1-a08a3671e1aa");

        public override string Name => "Intro/Credits Backup & Restore";

        public override string Description =>
            "Backs up and restores intro/credits chapter markers as per-episode JSON files.";

        public IEnumerable<PluginPageInfo> GetPages()
        {
            yield return new PluginPageInfo
            {
                Name = "IntrosBackupReplacementConfigPage",
                DisplayName = "Intro/Credits Backup & Restore",
                EmbeddedResourcePath = string.Format("{0}.Configuration.configPage.html", GetType().Namespace),
                EnableInMainMenu = true,
                IsMainConfigPage = true,
                MenuIcon = "cloud_upload"
            };

            yield return new PluginPageInfo
            {
                Name = "IntrosBackupReplacementConfigPage.js",
                EmbeddedResourcePath = string.Format("{0}.Configuration.configPage.js", GetType().Namespace),
                EnableInMainMenu = false
            };
        }
    }
}
