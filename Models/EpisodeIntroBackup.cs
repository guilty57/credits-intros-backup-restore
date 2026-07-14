namespace IntrosBackupReplacement.Models
{
    /// <summary>
    /// Serialized shape of one episode's backed-up chapter markers.
    /// Ticks are .NET ticks (100ns units), matching ChapterInfo.StartPositionTicks.
    /// </summary>
    public class EpisodeIntroBackup
    {
        public string? TvdbId { get; set; }
        public string? ImdbId { get; set; }
        public string? TmdbId { get; set; }

        public string SeriesName { get; set; } = string.Empty;
        public int SeasonNumber { get; set; }
        public int EpisodeNumber { get; set; }
        public string EpisodeTitle { get; set; } = string.Empty;

        public long? IntroStartTicks { get; set; }
        public long? IntroEndTicks { get; set; }
        public long? CreditsStartTicks { get; set; }
    }
}
