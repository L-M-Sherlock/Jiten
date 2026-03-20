using System.ComponentModel.DataAnnotations;

namespace Jiten.Api.Dtos.Requests;

public class AddStudyDeckRequest
{
    public int DeckId { get; set; }
    [Range(1, 6)]
    public int DownloadType { get; set; } = 1;
    [Range(1, 3)]
    public int Order { get; set; } = 2;
    [Range(0, int.MaxValue)]
    public int MinFrequency { get; set; }
    [Range(0, int.MaxValue)]
    public int MaxFrequency { get; set; }
    [Range(0f, 100f)]
    public float? TargetPercentage { get; set; }
    public int? MinOccurrences { get; set; }
    public int? MaxOccurrences { get; set; }
    public bool ExcludeKana { get; set; }
    public bool ExcludeMatureMasteredBlacklisted { get; set; } = true;
    public bool ExcludeAllTrackedWords { get; set; }
}
