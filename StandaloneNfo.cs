using System;
using System.IO;
using System.Xml;
using IntrosBackupReplacement.Models;

namespace IntrosBackupReplacement
{
    /// <summary>
    /// Reads/writes the Custom-mode standalone NFO backup: a plugin-owned
    /// file containing only a &lt;markers&gt; element (introstart/introend/
    /// creditstart), named after the video like the JSON backup is. This is
    /// unrelated to and never touches the video's own Emby-scraper-generated
    /// NFO - that file is handled separately by the "insert into existing
    /// NFO" (Automatic mode) code path.
    /// </summary>
    internal static class StandaloneNfo
    {
        public static void Write(string nfoPath, EpisodeIntroBackup backup)
        {
            var doc = new XmlDocument();
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

            doc.AppendChild(markers);
            doc.Save(nfoPath);
        }

        /// <summary>
        /// Same zero-vs-absent disambiguation as the existing-NFO reader:
        /// IntroStart/IntroEnd are only treated as "not detected" when BOTH
        /// are zero, since a genuine intro starting at frame zero would
        /// otherwise be indistinguishable from "no intro" in this schema.
        /// </summary>
        public static EpisodeIntroBackup? Read(string nfoPath)
        {
            var doc = new XmlDocument();
            doc.Load(nfoPath);

            var markers = doc.DocumentElement?.Name == "markers" ? doc.DocumentElement : doc.DocumentElement?.SelectSingleNode("markers");
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

            long? introStart = null;
            long? introEnd = null;
            if ((rawIntroStart ?? 0) != 0 || (rawIntroEnd ?? 0) != 0)
            {
                introStart = rawIntroStart ?? 0;
                introEnd = rawIntroEnd ?? 0;
            }

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

        public static string GetPath(string nfoDir, string videoPath)
        {
            return Path.Combine(nfoDir, Path.ChangeExtension(Path.GetFileName(videoPath), ".nfo"));
        }
    }
}
