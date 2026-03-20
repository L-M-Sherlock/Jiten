namespace Jiten.Core.Data.User;

public class UserStudyDeck
{
    public int UserStudyDeckId { get; set; }
    public string UserId { get; set; } = default!;
    public int DeckId { get; set; }
    public int SortOrder { get; set; }
    public int DownloadType { get; set; }
    public int Order { get; set; }
    public int MinFrequency { get; set; }
    public int MaxFrequency { get; set; }
    public float? TargetPercentage { get; set; }
    public int? MinOccurrences { get; set; }
    public int? MaxOccurrences { get; set; }
    public bool ExcludeKana { get; set; }
    public bool ExcludeMatureMasteredBlacklisted { get; set; }
    public bool ExcludeAllTrackedWords { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
