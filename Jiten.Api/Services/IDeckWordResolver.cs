using Jiten.Api.Dtos;
using Jiten.Core.Data;

namespace Jiten.Api.Services;

public record DeckWordResolveRequest(
    int DeckId,
    Deck Deck,
    DeckDownloadType DownloadType,
    DeckOrder Order,
    int MinFrequency,
    int MaxFrequency,
    bool ExcludeMatureMasteredBlacklisted,
    bool ExcludeAllTrackedWords,
    float? TargetPercentage,
    int? MinOccurrences = null,
    int? MaxOccurrences = null);

public interface IDeckWordResolver
{
    Task<(List<DeckWord>? Words, IResult? Error)> ResolveDeckWords(DeckWordResolveRequest request);
    Task<HashSet<long>> GetStudyDeckWordKeys(List<int> deckIds);
}
