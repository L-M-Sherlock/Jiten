using Jiten.Api.Dtos;
using Jiten.Api.Helpers;
using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services;

public class DeckWordResolver(JitenDbContext context, ICurrentUserService currentUserService) : IDeckWordResolver
{
    public async Task<(List<DeckWord>? Words, IResult? Error)> ResolveDeckWords(DeckWordResolveRequest request)
    {
        var (deckId, deck, downloadType, order, minFrequency, maxFrequency,
            excludeMatureMasteredBlacklisted, excludeAllTrackedWords,
            targetPercentage, minOccurrences, maxOccurrences) = request;

        IQueryable<DeckWord> deckWordsQuery = context.DeckWords.AsNoTracking().Where(dw => dw.DeckId == deckId);

        List<DeckWord>? deckWordsRaw = null;

        switch (downloadType)
        {
            case DeckDownloadType.Full:
                break;

            case DeckDownloadType.TopGlobalFrequency:
                deckWordsQuery = deckWordsQuery.Where(dw => context.WordFormFrequencies
                                                                   .Any(wff => wff.WordId == dw.WordId &&
                                                                               wff.ReadingIndex == (short)dw.ReadingIndex &&
                                                                               wff.FrequencyRank >= minFrequency &&
                                                                               wff.FrequencyRank <= maxFrequency));
                break;

            case DeckDownloadType.TopDeckFrequency:
                deckWordsQuery = deckWordsQuery
                                 .OrderByDescending(dw => dw.Occurrences)
                                 .Skip(minFrequency)
                                 .Take(maxFrequency - minFrequency);
                break;

            case DeckDownloadType.TopChronological:
                deckWordsQuery = deckWordsQuery
                                 .OrderBy(dw => dw.DeckWordId)
                                 .Skip(minFrequency)
                                 .Take(maxFrequency - minFrequency);
                break;

            case DeckDownloadType.TargetCoverage:
                if (!currentUserService.IsAuthenticated)
                    return (null, Results.Unauthorized());

                if (targetPercentage is null or < 1 or > 100)
                    return (null, Results.BadRequest("Target percentage must be between 1 and 100"));

                var allDeckWordsForCoverage = await deckWordsQuery
                                                    .OrderByDescending(dw => dw.Occurrences)
                                                    .ToListAsync();

                var coverageWordKeys = allDeckWordsForCoverage
                                       .Select(dw => (dw.WordId, dw.ReadingIndex))
                                       .ToList();

                var coverageStates = await currentUserService.GetKnownWordsState(coverageWordKeys);

                var knownKeysSet = coverageStates
                                   .Where(kvp => kvp.Value.Any(s => s is KnownState.Mastered or KnownState.Blacklisted
                                                                   or KnownState.Mature))
                                   .Select(kvp => ((long)kvp.Key.WordId << 8) | kvp.Key.ReadingIndex)
                                   .ToHashSet();

                int totalOccurrences = deck.WordCount;
                int knownOccurrences = allDeckWordsForCoverage
                                       .Where(dw => knownKeysSet.Contains(((long)dw.WordId << 8) | dw.ReadingIndex))
                                       .Sum(dw => dw.Occurrences);

                double targetCoverage = targetPercentage.Value;

                var resultWords = new List<DeckWord>();
                int cumulativeOccurrences = knownOccurrences;

                foreach (var dw in allDeckWordsForCoverage)
                {
                    var key = ((long)dw.WordId << 8) | dw.ReadingIndex;
                    if (knownKeysSet.Contains(key))
                        continue;

                    resultWords.Add(dw);
                    cumulativeOccurrences += dw.Occurrences;

                    double newCoverage = (double)cumulativeOccurrences / totalOccurrences * 100;
                    if (newCoverage >= targetCoverage)
                        break;
                }

                if (order == DeckOrder.Chronological)
                {
                    deckWordsRaw = resultWords.OrderBy(dw => dw.DeckWordId).ToList();
                }
                else if (order == DeckOrder.GlobalFrequency)
                {
                    var resultWordIds = resultWords.Select(dw => dw.WordId).Distinct().ToList();
                    var freqMap = await WordFormHelper.LoadWordFormFrequencies(context, resultWordIds);

                    deckWordsRaw = resultWords.OrderBy(dw =>
                                                           freqMap.TryGetValue((dw.WordId, (short)dw.ReadingIndex), out var wff)
                                                               ? wff.FrequencyRank
                                                               : int.MaxValue
                                                      ).ToList();
                }
                else
                {
                    deckWordsRaw = resultWords;
                }

                break;

            case DeckDownloadType.OccurrenceCount:
                if (minOccurrences.HasValue)
                    deckWordsQuery = deckWordsQuery.Where(dw => dw.Occurrences >= minOccurrences.Value);
                if (maxOccurrences.HasValue)
                    deckWordsQuery = deckWordsQuery.Where(dw => dw.Occurrences <= maxOccurrences.Value);
                break;

            default:
                return (null, Results.BadRequest());
        }

        if (deckWordsRaw == null)
        {
            switch (order)
            {
                case DeckOrder.Chronological:
                    deckWordsQuery = deckWordsQuery.OrderBy(dw => dw.DeckWordId);
                    break;

                case DeckOrder.GlobalFrequency:
                    deckWordsQuery = deckWordsQuery.OrderBy(dw => context.WordFormFrequencies
                                                                         .Where(wff => wff.WordId == dw.WordId &&
                                                                                       wff.ReadingIndex == (short)dw.ReadingIndex)
                                                                         .Select(wff => wff.FrequencyRank)
                                                                         .FirstOrDefault()
                                                           );
                    break;

                case DeckOrder.DeckFrequency:
                    deckWordsQuery = deckWordsQuery.OrderByDescending(dw => dw.Occurrences);
                    break;
                default:
                    return (null, Results.BadRequest());
            }

            deckWordsRaw = await deckWordsQuery.ToListAsync();
        }

        if ((excludeMatureMasteredBlacklisted || excludeAllTrackedWords) && currentUserService.IsAuthenticated)
        {
            var wordKeys = deckWordsRaw.Select(dw => (dw.WordId, dw.ReadingIndex)).ToList();
            var knownStates = await currentUserService.GetKnownWordsState(wordKeys);

            deckWordsRaw = deckWordsRaw
                           .Where(dw =>
                           {
                               if (!knownStates.TryGetValue((dw.WordId, dw.ReadingIndex), out var states))
                                   return true;

                               if (excludeAllTrackedWords && states.Any(s => s != KnownState.New))
                                   return false;

                               if (excludeMatureMasteredBlacklisted &&
                                   states.Any(s => s is KnownState.Mastered or KnownState.Blacklisted or KnownState.Mature))
                                   return false;

                               return true;
                           })
                           .ToList();
        }

        return (deckWordsRaw, null);
    }

    public async Task<HashSet<long>> GetStudyDeckWordKeys(List<int> deckIds)
    {
        var keys = await context.DeckWords
            .AsNoTracking()
            .Where(dw => deckIds.Contains(dw.DeckId))
            .Select(dw => ((long)dw.WordId << 8) | dw.ReadingIndex)
            .Distinct()
            .ToListAsync();

        return keys.ToHashSet();
    }
}
