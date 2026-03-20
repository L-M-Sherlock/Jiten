using System.Text.Json;
using Jiten.Api.Dtos;
using Jiten.Api.Dtos.Requests;
using Jiten.Api.Helpers;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.JMDict;
using Jiten.Core.Data.User;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Swashbuckle.AspNetCore.Annotations;

namespace Jiten.Api.Controllers;

[ApiController]
[Route("api/srs")]
[Authorize]
public class StudyController(
    JitenDbContext context,
    UserDbContext userContext,
    ICurrentUserService currentUserService,
    IDeckWordResolver deckWordResolver,
    IWordFormSiblingCache wordFormCache,
    IStudySessionService sessionService,
    ILogger<StudyController> logger) : ControllerBase
{
    [HttpGet("study-decks")]
    [SwaggerOperation(Summary = "Get user's studied decks")]
    public async Task<IResult> GetStudyDecks()
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        var studyDecks = await userContext.UserStudyDecks
            .AsNoTracking()
            .Where(sd => sd.UserId == userId)
            .OrderBy(sd => sd.SortOrder)
            .ToListAsync();

        if (studyDecks.Count == 0)
            return Results.Ok(new List<StudyDeckDto>());

        var deckIds = studyDecks.Select(sd => sd.DeckId).ToList();
        var decks = await context.Decks
            .AsNoTracking()
            .Where(d => deckIds.Contains(d.DeckId))
            .ToDictionaryAsync(d => d.DeckId);

        var cardStateMap = new Dictionary<(int, byte), (FsrsState State, DateTime Due)>();
        foreach (var c in await userContext.FsrsCards
                     .AsNoTracking()
                     .Where(fc => fc.UserId == userId)
                     .Select(fc => new { fc.WordId, fc.ReadingIndex, fc.State, fc.Due })
                     .ToListAsync())
            cardStateMap[(c.WordId, c.ReadingIndex)] = (c.State, c.Due);

        var dueCutoff = DateTime.UtcNow.AddMinutes(15);

        var allKanaFilterWordIds = new HashSet<int>();
        var resolvedDecks = new List<(UserStudyDeck Sd, Deck? Deck, List<DeckWord>? Words)>();

        foreach (var sd in studyDecks)
        {
            decks.TryGetValue(sd.DeckId, out var deck);
            if (deck == null)
            {
                resolvedDecks.Add((sd, null, null));
                continue;
            }

            var (words, _) = await deckWordResolver.ResolveDeckWords(new DeckWordResolveRequest(
                sd.DeckId, deck,
                (DeckDownloadType)sd.DownloadType, (DeckOrder)sd.Order,
                sd.MinFrequency, sd.MaxFrequency,
                sd.ExcludeMatureMasteredBlacklisted, sd.ExcludeAllTrackedWords,
                sd.TargetPercentage,
                sd.MinOccurrences, sd.MaxOccurrences));

            if (sd.ExcludeKana && words != null)
                foreach (var w in words)
                    allKanaFilterWordIds.Add(w.WordId);

            resolvedDecks.Add((sd, deck, words));
        }

        HashSet<int>? kanaOnlyWords = null;
        if (allKanaFilterWordIds.Count > 0)
        {
            var kanaForms = await context.WordForms.AsNoTracking()
                .Where(wf => allKanaFilterWordIds.Contains(wf.WordId))
                .ToListAsync();
            kanaOnlyWords = kanaForms
                .GroupBy(wf => wf.WordId)
                .Where(g => g.All(wf => wf.FormType == JmDictFormType.KanaForm))
                .Select(g => g.Key)
                .ToHashSet();
        }

        var result = resolvedDecks.Select(entry =>
        {
            var (sd, deck, words) = entry;
            var dto = new StudyDeckDto
            {
                UserStudyDeckId = sd.UserStudyDeckId,
                DeckId = sd.DeckId,
                Title = deck?.OriginalTitle ?? "",
                RomajiTitle = deck?.RomajiTitle,
                EnglishTitle = deck?.EnglishTitle,
                CoverName = deck?.CoverName,
                MediaType = (int)(deck?.MediaType ?? 0),
                SortOrder = sd.SortOrder,
                DownloadType = sd.DownloadType,
                Order = sd.Order,
                MinFrequency = sd.MinFrequency,
                MaxFrequency = sd.MaxFrequency,
                TargetPercentage = sd.TargetPercentage,
                MinOccurrences = sd.MinOccurrences,
                MaxOccurrences = sd.MaxOccurrences,
                ExcludeKana = sd.ExcludeKana,
                ExcludeMatureMasteredBlacklisted = sd.ExcludeMatureMasteredBlacklisted,
                ExcludeAllTrackedWords = sd.ExcludeAllTrackedWords
            };

            if (words != null)
            {
                var filtered = sd.ExcludeKana && kanaOnlyWords != null
                    ? words.Where(w => !kanaOnlyWords.Contains(w.WordId)).ToList()
                    : words;

                dto.TotalWords = filtered.Count;
                foreach (var w in filtered)
                {
                    if (!cardStateMap.TryGetValue((w.WordId, w.ReadingIndex), out var card))
                        dto.UnseenCount++;
                    else if (card.State is FsrsState.New or FsrsState.Learning or FsrsState.Relearning)
                    {
                        dto.LearningCount++;
                        if (card.State is FsrsState.Learning or FsrsState.Relearning && card.Due <= dueCutoff)
                            dto.DueReviewCount++;
                    }
                    else if (card.State == FsrsState.Review)
                    {
                        dto.ReviewCount++;
                        if (card.Due <= dueCutoff)
                            dto.DueReviewCount++;
                    }
                    else if (card.State == FsrsState.Mastered)
                        dto.MasteredCount++;
                    else if (card.State == FsrsState.Blacklisted)
                        dto.BlacklistedCount++;
                    else if (card.State == FsrsState.Suspended)
                        dto.SuspendedCount++;
                }
            }

            return dto;
        }).ToList();

        return Results.Ok(result);
    }

    [HttpPost("study-decks")]
    [SwaggerOperation(Summary = "Add a deck to study")]
    public async Task<IResult> AddStudyDeck(AddStudyDeckRequest request)
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        if (request.MaxFrequency > 0 && request.MinFrequency > request.MaxFrequency)
            return Results.BadRequest("MinFrequency cannot exceed MaxFrequency.");

        var exists = await userContext.UserStudyDecks
            .AnyAsync(sd => sd.UserId == userId && sd.DeckId == request.DeckId);
        if (exists)
            return Results.BadRequest("This deck is already in your study list.");

        var deckExists = await context.Decks.AnyAsync(d => d.DeckId == request.DeckId);
        if (!deckExists)
            return Results.NotFound("Deck not found.");

        await using var transaction = await userContext.Database.BeginTransactionAsync();

        var maxOrder = await userContext.UserStudyDecks
            .Where(sd => sd.UserId == userId)
            .MaxAsync(sd => (int?)sd.SortOrder) ?? -1;

        var studyDeck = new UserStudyDeck
        {
            UserId = userId,
            DeckId = request.DeckId,
            SortOrder = maxOrder + 1,
            DownloadType = request.DownloadType,
            Order = request.Order,
            MinFrequency = request.MinFrequency,
            MaxFrequency = request.MaxFrequency,
            TargetPercentage = request.TargetPercentage,
            MinOccurrences = request.MinOccurrences,
            MaxOccurrences = request.MaxOccurrences,
            ExcludeKana = request.ExcludeKana,
            ExcludeMatureMasteredBlacklisted = request.ExcludeMatureMasteredBlacklisted,
            ExcludeAllTrackedWords = request.ExcludeAllTrackedWords,
            CreatedAt = DateTime.UtcNow
        };

        userContext.UserStudyDecks.Add(studyDeck);
        await userContext.SaveChangesAsync();
        await transaction.CommitAsync();

        logger.LogInformation("User added study deck: DeckId={DeckId}", request.DeckId);
        return Results.Ok(new { studyDeck.UserStudyDeckId });
    }

    [HttpPut("study-decks/{id:int}")]
    [SwaggerOperation(Summary = "Update study deck filters")]
    public async Task<IResult> UpdateStudyDeck(int id, UpdateStudyDeckRequest request)
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        if (request.MaxFrequency > 0 && request.MinFrequency > request.MaxFrequency)
            return Results.BadRequest("MinFrequency cannot exceed MaxFrequency.");

        var studyDeck = await userContext.UserStudyDecks
            .FirstOrDefaultAsync(sd => sd.UserStudyDeckId == id && sd.UserId == userId);
        if (studyDeck == null) return Results.NotFound();

        studyDeck.DownloadType = request.DownloadType;
        studyDeck.Order = request.Order;
        studyDeck.MinFrequency = request.MinFrequency;
        studyDeck.MaxFrequency = request.MaxFrequency;
        studyDeck.TargetPercentage = request.TargetPercentage;
        studyDeck.MinOccurrences = request.MinOccurrences;
        studyDeck.MaxOccurrences = request.MaxOccurrences;
        studyDeck.ExcludeKana = request.ExcludeKana;
        studyDeck.ExcludeMatureMasteredBlacklisted = request.ExcludeMatureMasteredBlacklisted;
        studyDeck.ExcludeAllTrackedWords = request.ExcludeAllTrackedWords;

        await userContext.SaveChangesAsync();
        return Results.Ok(new { success = true });
    }

    [HttpDelete("study-decks/{id:int}")]
    [SwaggerOperation(Summary = "Remove a study deck (keeps existing cards)")]
    public async Task<IResult> RemoveStudyDeck(int id)
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        var studyDeck = await userContext.UserStudyDecks
            .FirstOrDefaultAsync(sd => sd.UserStudyDeckId == id && sd.UserId == userId);
        if (studyDeck == null) return Results.NotFound();

        userContext.UserStudyDecks.Remove(studyDeck);
        await userContext.SaveChangesAsync();

        logger.LogInformation("User removed study deck: DeckId={DeckId}", studyDeck.DeckId);
        return Results.Ok(new { success = true });
    }

    [HttpPut("study-decks/reorder")]
    [SwaggerOperation(Summary = "Reorder study decks")]
    public async Task<IResult> ReorderStudyDecks(ReorderStudyDecksRequest request)
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        if (request.Items.Count == 0)
            return Results.BadRequest("Items cannot be empty.");

        if (request.Items.Select(i => i.UserStudyDeckId).Distinct().Count() != request.Items.Count)
            return Results.BadRequest("Duplicate UserStudyDeckId values.");

        if (request.Items.Select(i => i.SortOrder).Distinct().Count() != request.Items.Count)
            return Results.BadRequest("Duplicate SortOrder values.");

        var ids = request.Items.Select(i => i.UserStudyDeckId).ToList();
        var decks = await userContext.UserStudyDecks
            .Where(sd => sd.UserId == userId && ids.Contains(sd.UserStudyDeckId))
            .ToListAsync();

        var deckMap2 = decks.ToDictionary(d => d.UserStudyDeckId);
        foreach (var item in request.Items)
        {
            if (deckMap2.TryGetValue(item.UserStudyDeckId, out var deck))
                deck.SortOrder = item.SortOrder;
        }

        await userContext.SaveChangesAsync();
        return Results.Ok(new { success = true });
    }

    [HttpPost("study-decks/preview-count")]
    [SwaggerOperation(Summary = "Preview word count for study deck filters")]
    public async Task<IResult> PreviewStudyDeckCount(AddStudyDeckRequest request)
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        var deck = await context.Decks.AsNoTracking().FirstOrDefaultAsync(d => d.DeckId == request.DeckId);
        if (deck == null) return Results.NotFound("Deck not found.");

        var (words, error) = await deckWordResolver.ResolveDeckWords(new DeckWordResolveRequest(
            request.DeckId, deck,
            (DeckDownloadType)request.DownloadType, (DeckOrder)request.Order,
            request.MinFrequency, request.MaxFrequency,
            request.ExcludeMatureMasteredBlacklisted, request.ExcludeAllTrackedWords,
            request.TargetPercentage,
            request.MinOccurrences, request.MaxOccurrences));

        if (error != null) return error;
        if (words == null || words.Count == 0) return Results.Ok(0);

        if (request.ExcludeKana)
        {
            var wordIds = words.Select(dw => dw.WordId).Distinct().ToList();
            var kanaForms = await context.WordForms.AsNoTracking()
                .Where(wf => wordIds.Contains(wf.WordId))
                .ToListAsync();
            var kanaOnlyWords = kanaForms
                .GroupBy(wf => wf.WordId)
                .Where(g => g.All(wf => wf.FormType == JmDictFormType.KanaForm))
                .Select(g => g.Key)
                .ToHashSet();
            words = words.Where(w => !kanaOnlyWords.Contains(w.WordId)).ToList();
        }

        return Results.Ok(words.Count);
    }

    [HttpGet("study-batch")]
    [SwaggerOperation(Summary = "Get a batch of cards for study")]
    public async Task<IResult> GetStudyBatch([FromQuery] int limit = 20, [FromQuery] string? sessionId = null)
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        if (!string.IsNullOrEmpty(sessionId) && await sessionService.ValidateSessionAsync(sessionId, userId))
            await sessionService.RefreshSessionAsync(sessionId);
        else
            sessionId = await sessionService.CreateSessionAsync(userId);

        limit = Math.Clamp(limit, 1, 100);
        var settings = await LoadStudySettings(userId);
        var now = DateTime.UtcNow;
        var todayStart = now.Date;

        var newCardsToday = await userContext.FsrsCards
            .CountAsync(c => c.UserId == userId && c.CreatedAt >= todayStart
                             && c.ReviewLogs.Any(l => l.ReviewDateTime >= todayStart));

        var reviewsToday = await userContext.FsrsCards
            .Where(c => c.UserId == userId)
            .SelectMany(c => c.ReviewLogs)
            .CountAsync(l => l.ReviewDateTime >= todayStart);

        var newCardBudget = Math.Max(0, settings.NewCardsPerDay - newCardsToday);

        // ── Phase 1: Build kanji knowledge set for kana redundancy ──
        // Stream all cards instead of materializing them all into memory.
        // Collect only the data each phase needs: knownKanjiWordIds (Phase 1),
        // potentiallyRedundant cards (Phase 2), and existingKeys (Phase 4, conditional).
        var knownKanjiWordIds = new HashSet<int>();
        var potentiallyRedundant = new List<(long CardId, int WordId, byte ReadingIndex)>();
        HashSet<long>? existingKeys = newCardBudget > 0 ? new HashSet<long>() : null;

        await foreach (var c in userContext.FsrsCards
            .AsNoTracking()
            .Where(c => c.UserId == userId)
            .Select(c => new { c.CardId, c.WordId, c.ReadingIndex, c.State })
            .AsAsyncEnumerable())
        {
            if (wordFormCache.GetKanaIndexesForKanji(c.WordId, c.ReadingIndex) != null)
                knownKanjiWordIds.Add(c.WordId);

            if (c.State is not FsrsState.Mastered and not FsrsState.Blacklisted and not FsrsState.Suspended
                && wordFormCache.GetKanjiIndexesForKana(c.WordId, c.ReadingIndex) != null)
            {
                potentiallyRedundant.Add((c.CardId, c.WordId, c.ReadingIndex));
            }

            existingKeys?.Add(((long)c.WordId << 8) | c.ReadingIndex);
        }

        var masteredSetIds = await userContext.UserWordSetStates
            .AsNoTracking()
            .Where(uwss => uwss.UserId == userId && uwss.State == WordSetStateType.Mastered)
            .Select(uwss => uwss.SetId)
            .ToListAsync();

        if (masteredSetIds.Count > 0)
        {
            var setMembers = await context.WordSetMembers
                .AsNoTracking()
                .Where(wsm => masteredSetIds.Contains(wsm.SetId))
                .Select(wsm => new { wsm.WordId, wsm.ReadingIndex })
                .ToListAsync();

            foreach (var m in setMembers)
            {
                if (wordFormCache.GetKanaIndexesForKanji(m.WordId, (byte)m.ReadingIndex) != null)
                    knownKanjiWordIds.Add(m.WordId);
            }
        }

        // ── Phase 2: Auto-master redundant kana cards + delete legacy New cards ──
        var redundantCardIds = potentiallyRedundant
            .Where(c => knownKanjiWordIds.Contains(c.WordId))
            .Select(c => c.CardId)
            .ToList();

        if (redundantCardIds.Count > 0)
        {
            await userContext.FsrsCards
                .Where(c => redundantCardIds.Contains(c.CardId))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.State, FsrsState.Mastered)
                    .SetProperty(c => c.LastReview, now)
                    .SetProperty(c => c.Due, now));
        }

        // ── Load study decks (used by both review filtering and new card selection) ──
        var studyDecks = await userContext.UserStudyDecks
            .AsNoTracking()
            .Where(sd => sd.UserId == userId)
            .OrderBy(sd => sd.SortOrder)
            .ToListAsync();

        // ── Phase 3: Collect due reviews ──
        // Include cards due within 15 minutes to catch learning/relearning steps
        var dueCutoff = now.AddMinutes(15);
        var batch = new List<(int WordId, byte ReadingIndex, long CardId, bool IsNew, int State)>();

        var reviewBudget = Math.Max(0, settings.MaxReviewsPerDay - reviewsToday);
        var totalDueCount = 0;
        if (reviewBudget > 0)
        {
            var dueQuery = userContext.FsrsCards
                .AsNoTracking()
                .Where(c => c.UserId == userId
                            && c.State != FsrsState.Blacklisted
                            && c.State != FsrsState.Mastered
                            && c.State != FsrsState.Suspended
                            && c.Due <= dueCutoff);

            if (settings.ReviewFrom == StudyReviewFrom.StudyDecksOnly && studyDecks.Count > 0)
            {
                var studyDeckWordKeys = await deckWordResolver.GetStudyDeckWordKeys(
                    studyDecks.Select(sd => sd.DeckId).ToList());
                var studyWordIds = studyDeckWordKeys.Select(k => (int)(k >> 8)).Distinct().ToList();
                dueQuery = dueQuery.Where(c => studyWordIds.Contains(c.WordId));
            }

            totalDueCount = await dueQuery.CountAsync();

            var dueCards = await dueQuery
                .OrderBy(c => c.Due)
                .Take(reviewBudget)
                .ToListAsync();

            // Sort: learning/relearning steps first (by due), then reviews by relative overdueness
            // Add slight randomness so batches don't always come in the exact same order
            var rng = Random.Shared;
            dueCards = dueCards
                .OrderBy(c => c.State is FsrsState.Learning or FsrsState.Relearning ? 0 : 1)
                .ThenByDescending(c =>
                {
                    double overdueness;
                    if (c.Stability is > 0 && c.LastReview.HasValue)
                    {
                        var elapsed = (now - c.LastReview.Value).TotalDays;
                        overdueness = elapsed / c.Stability.Value;
                    }
                    else
                    {
                        overdueness = (now - c.Due).TotalDays;
                    }
                    // ±10% jitter so similarly-overdue cards shuffle around between sessions
                    return overdueness * (0.9 + rng.NextDouble() * 0.2);
                })
                .ToList();

            foreach (var card in dueCards)
                batch.Add((card.WordId, card.ReadingIndex, card.CardId, false, (int)card.State));
        }

        // ── Phase 4: Resolve new word candidates from study decks ──
        if (newCardBudget > 0)
        {

            var studyDeckIds = studyDecks.Select(sd => sd.DeckId).ToList();
            var deckMap = await context.Decks.AsNoTracking()
                .Where(d => studyDeckIds.Contains(d.DeckId))
                .ToDictionaryAsync(d => d.DeckId);

            var resolvedDecks = new List<(UserStudyDeck StudyDeck, List<DeckWord> Words)>();
            var allKanaFilterWordIds = new HashSet<int>();

            foreach (var studyDeck in studyDecks)
            {
                if (!deckMap.TryGetValue(studyDeck.DeckId, out var deck)) continue;

                var (words, error) = await deckWordResolver.ResolveDeckWords(new DeckWordResolveRequest(
                    studyDeck.DeckId, deck,
                    (DeckDownloadType)studyDeck.DownloadType, (DeckOrder)studyDeck.Order,
                    studyDeck.MinFrequency, studyDeck.MaxFrequency,
                    studyDeck.ExcludeMatureMasteredBlacklisted, studyDeck.ExcludeAllTrackedWords,
                    studyDeck.TargetPercentage,
                    studyDeck.MinOccurrences, studyDeck.MaxOccurrences));

                if (error != null || words == null) continue;

                resolvedDecks.Add((studyDeck, words));

                if (studyDeck.ExcludeKana)
                    foreach (var w in words)
                        allKanaFilterWordIds.Add(w.WordId);
            }

            HashSet<int>? kanaOnlyWords = null;
            if (allKanaFilterWordIds.Count > 0)
            {
                var kanaForms = await context.WordForms.AsNoTracking()
                    .Where(wf => allKanaFilterWordIds.Contains(wf.WordId))
                    .ToListAsync();
                kanaOnlyWords = kanaForms
                    .GroupBy(wf => wf.WordId)
                    .Where(g => g.All(wf => wf.FormType == JmDictFormType.KanaForm))
                    .Select(g => g.Key)
                    .ToHashSet();
            }

            var candidates = new List<(int WordId, byte ReadingIndex)>();

            foreach (var (studyDeck, words) in resolvedDecks)
            {
                var filtered = studyDeck.ExcludeKana && kanaOnlyWords != null
                    ? words.Where(w => !kanaOnlyWords.Contains(w.WordId))
                    : words;

                foreach (var word in filtered)
                {
                    var key = ((long)word.WordId << 8) | word.ReadingIndex;
                    if (existingKeys!.Contains(key)) continue;

                    if (knownKanjiWordIds.Contains(word.WordId)
                        && wordFormCache.GetKanjiIndexesForKana(word.WordId, word.ReadingIndex) != null)
                        continue;

                    existingKeys!.Add(key);
                    candidates.Add((word.WordId, word.ReadingIndex));
                }
            }

            if (settings.NewCardOrder == StudyNewCardOrder.GlobalFrequency && candidates.Count > 0)
            {
                var candidateWordIds = candidates.Select(c => c.WordId).Distinct().ToList();
                var globalFreqs = await WordFormHelper.LoadWordFormFrequencies(context, candidateWordIds);
                candidates = candidates
                    .OrderBy(c => globalFreqs.TryGetValue((c.WordId, c.ReadingIndex), out var f) ? f.FrequencyRank : int.MaxValue)
                    .ToList();
            }
            else if (settings.NewCardOrder == StudyNewCardOrder.Random)
            {
                var rng = Random.Shared;
                candidates = candidates.OrderBy(_ => rng.Next()).ToList();
            }

            var taken = candidates.Take(newCardBudget).ToList();
            foreach (var c in taken)
                batch.Add((c.WordId, c.ReadingIndex, 0, true, (int)FsrsState.New));

        }

        if (batch.Count == 0)
        {
            return Results.Ok(new StudyBatchResponse
            {
                SessionId = sessionId,
                NewCardsRemaining = Math.Max(0, settings.NewCardsPerDay - newCardsToday),
                ReviewsRemaining = 0,
                NewCardsToday = newCardsToday,
                ReviewsToday = reviewsToday
            });
        }

        // ── Phase 5: Interleave and build response ──
        var ordered = settings.Interleaving switch
        {
            StudyInterleaving.NewFirst => batch.OrderBy(c => !c.IsNew).ThenBy(c => c.CardId).ToList(),
            StudyInterleaving.ReviewsFirst => batch.OrderBy(c => c.IsNew).ThenBy(c => c.CardId).ToList(),
            _ => InterleaveMixed(batch)
        };

        ordered = ordered.Take(limit).ToList();

        var wordIds = ordered.Select(c => c.WordId).Distinct().ToList();

        var wordsData = await context.JMDictWords
            .AsNoTracking()
            .Include(w => w.Definitions)
            .Where(w => wordIds.Contains(w.WordId))
            .ToDictionaryAsync(w => w.WordId);

        var wordForms = await context.WordForms
            .AsNoTracking()
            .Where(wf => wordIds.Contains(wf.WordId))
            .ToListAsync();
        var wordFormsMap = wordForms.GroupBy(wf => wf.WordId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var freqs = await WordFormHelper.LoadWordFormFrequencies(context, wordIds);

        var studyDeckIdSet = studyDecks.Select(sd => sd.DeckId).ToHashSet();

        var allExampleWords = await context.ExampleSentenceWords
            .AsNoTracking()
            .Include(esw => esw.ExampleSentence)
            .Where(esw => wordIds.Contains(esw.WordId))
            .ToListAsync();
        var exampleMap = allExampleWords
            .GroupBy(esw => ((long)esw.WordId << 8) | esw.ReadingIndex)
            .ToDictionary(g => g.Key, g => g
                .OrderByDescending(esw => esw.ExampleSentence != null && studyDeckIdSet.Contains(esw.ExampleSentence.DeckId) ? 1 : 0)
                .ThenBy(esw => esw.ExampleSentenceId)
                .First());

        var exampleDeckIds = exampleMap.Values
            .Where(esw => esw.ExampleSentence != null)
            .Select(esw => esw.ExampleSentence!.DeckId)
            .Distinct().ToList();
        var exampleDecks = exampleDeckIds.Count > 0
            ? await context.Decks.AsNoTracking()
                .Where(d => exampleDeckIds.Contains(d.DeckId))
                .Select(d => new DeckProjection(d.DeckId, d.OriginalTitle, d.RomajiTitle, d.EnglishTitle, d.MediaType, d.ParentDeckId))
                .ToDictionaryAsync(d => d.DeckId)
            : new();
        var exampleParentIds = exampleDecks.Values
            .Where(d => d.ParentDeckId.HasValue)
            .Select(d => d.ParentDeckId!.Value)
            .Distinct().ToList();
        var exampleParentDecks = exampleParentIds.Count > 0
            ? await context.Decks.AsNoTracking()
                .Where(d => exampleParentIds.Contains(d.DeckId))
                .Select(d => new DeckProjection(d.DeckId, d.OriginalTitle, d.RomajiTitle, d.EnglishTitle, d.MediaType, d.ParentDeckId))
                .ToDictionaryAsync(d => d.DeckId)
            : new();

        var occDeckIds = studyDecks.Select(sd => sd.DeckId).ToList();
        var deckOccurrences = occDeckIds.Count > 0
            ? await context.DeckWords
                .AsNoTracking()
                .Where(dw => occDeckIds.Contains(dw.DeckId) && wordIds.Contains(dw.WordId))
                .Select(dw => new { dw.DeckId, dw.WordId, dw.ReadingIndex, dw.Occurrences })
                .ToListAsync()
            : new();
        var occurrenceMap = deckOccurrences
            .GroupBy(dw => ((long)dw.WordId << 8) | dw.ReadingIndex)
            .ToDictionary(g => g.Key, g => g.ToList());

        var occurrenceDeckIds = deckOccurrences.Select(dw => dw.DeckId).Distinct().ToList();
        var occurrenceDecks = occurrenceDeckIds.Count > 0
            ? await context.Decks.AsNoTracking()
                .Where(d => occurrenceDeckIds.Contains(d.DeckId))
                .Select(d => new { d.DeckId, d.OriginalTitle, d.RomajiTitle, d.EnglishTitle })
                .ToDictionaryAsync(d => d.DeckId)
            : new();

        var cards = new List<StudyCardDto>();
        foreach (var item in ordered)
        {
            wordsData.TryGetValue(item.WordId, out var word);
            wordFormsMap.TryGetValue(item.WordId, out var forms);
            freqs.TryGetValue((item.WordId, (short)item.ReadingIndex), out var freq);

            var mainForm = forms?.FirstOrDefault(f => f.ReadingIndex == item.ReadingIndex);
            var exKey = ((long)item.WordId << 8) | item.ReadingIndex;
            exampleMap.TryGetValue(exKey, out var exWord);

            cards.Add(new StudyCardDto
            {
                CardId = item.CardId,
                WordId = item.WordId,
                ReadingIndex = item.ReadingIndex,
                State = item.State,
                IsNewCard = item.IsNew,
                WordText = mainForm?.RubyText ?? mainForm?.Text ?? "",
                WordTextPlain = mainForm?.Text ?? "",
                Readings = forms?.Select(f => new StudyReadingDto
                {
                    Text = f.Text,
                    RubyText = f.RubyText,
                    ReadingIndex = f.ReadingIndex,
                    FormType = (int)f.FormType
                }).ToList() ?? new(),
                Definitions = word?.Definitions
                    .OrderBy(d => d.SenseIndex)
                    .Select(d => new StudyDefinitionDto
                    {
                        Index = d.SenseIndex,
                        Meanings = d.EnglishMeanings.ToArray(),
                        PartsOfSpeech = d.PartsOfSpeech.ToHumanReadablePartsOfSpeech().ToArray()
                    }).ToList() ?? new(),
                PartsOfSpeech = (word?.PartsOfSpeech.ToHumanReadablePartsOfSpeech() ?? []).ToArray(),
                PitchAccents = word?.PitchAccents?.ToArray(),
                FrequencyRank = freq?.FrequencyRank ?? 0,
                ExampleSentence = exWord?.ExampleSentence != null
                    ? BuildStudyExampleSentence(exWord, exampleDecks, exampleParentDecks)
                    : null,
                DeckOccurrences = occurrenceMap.TryGetValue(exKey, out var occs)
                    ? occs
                        .OrderByDescending(o => o.Occurrences)
                        .Take(5)
                        .Where(o => occurrenceDecks.ContainsKey(o.DeckId))
                        .Select(o => new StudyDeckOccurrenceDto
                        {
                            DeckId = o.DeckId,
                            OriginalTitle = occurrenceDecks[o.DeckId].OriginalTitle,
                            RomajiTitle = occurrenceDecks[o.DeckId].RomajiTitle,
                            EnglishTitle = occurrenceDecks[o.DeckId].EnglishTitle,
                            Occurrences = o.Occurrences
                        }).ToList()
                    : null
            });
        }

        if (settings.ShowNextInterval)
        {
            var userSettings = await userContext.UserFsrsSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.UserId == userId);
            var fsrsParams = userSettings?.Parameters is { Length: > 0 } p ? p : FsrsConstants.DefaultParameters;
            var desiredRetention = userSettings?.DesiredRetention is double dr and > 0 and < 1 ? dr : FsrsConstants.DefaultDesiredRetention;
            var previewScheduler = new FsrsScheduler(desiredRetention: desiredRetention, parameters: fsrsParams, enableFuzzing: false);

            var cardLookup = new Dictionary<(int WordId, byte ReadingIndex), FsrsCard>();
            var reviewCardIds = ordered.Where(o => !o.IsNew && o.CardId > 0).Select(o => o.CardId).ToList();
            if (reviewCardIds.Count > 0)
            {
                var dbCards = await userContext.FsrsCards
                    .AsNoTracking()
                    .Where(c => reviewCardIds.Contains(c.CardId))
                    .ToListAsync();
                foreach (var c in dbCards)
                    cardLookup[(c.WordId, c.ReadingIndex)] = c;
            }

            var now2 = DateTime.UtcNow;
            foreach (var dto in cards)
            {
                FsrsCard fsrsCard;
                if (cardLookup.TryGetValue((dto.WordId, (byte)dto.ReadingIndex), out var existing))
                    fsrsCard = existing;
                else
                    fsrsCard = new FsrsCard(userId, dto.WordId, (byte)dto.ReadingIndex);

                var intervals = previewScheduler.PreviewIntervals(fsrsCard, now2);
                dto.IntervalPreview = new IntervalPreviewDto
                {
                    AgainSeconds = (int)intervals[FsrsRating.Again].TotalSeconds,
                    HardSeconds = (int)intervals[FsrsRating.Hard].TotalSeconds,
                    GoodSeconds = (int)intervals[FsrsRating.Good].TotalSeconds,
                    EasySeconds = (int)intervals[FsrsRating.Easy].TotalSeconds,
                };
            }
        }

        var remainingReviews = totalDueCount - batch.Count(k => !k.IsNew);

        return Results.Ok(new StudyBatchResponse
        {
            SessionId = sessionId,
            Cards = cards,
            NewCardsRemaining = Math.Max(0, settings.NewCardsPerDay - newCardsToday),
            ReviewsRemaining = Math.Max(0, remainingReviews),
            NewCardsToday = newCardsToday,
            ReviewsToday = reviewsToday
        });
    }

    [HttpGet("study-settings")]
    [SwaggerOperation(Summary = "Get study experience settings")]
    public async Task<IResult> GetStudySettings()
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        var settings = await LoadStudySettings(userId);
        return Results.Ok(settings);
    }

    [HttpPut("study-settings")]
    [SwaggerOperation(Summary = "Update study experience settings")]
    public async Task<IResult> UpdateStudySettings(StudySettingsDto request)
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        request.NewCardsPerDay = Math.Clamp(request.NewCardsPerDay, 0, 9999);
        request.MaxReviewsPerDay = Math.Clamp(request.MaxReviewsPerDay, 0, 9999);
        request.GradingButtons = request.GradingButtons is 2 or 4 ? request.GradingButtons : 4;

        var fsrsSettings = await userContext.UserFsrsSettings
            .FirstOrDefaultAsync(s => s.UserId == userId);

        if (fsrsSettings == null)
        {
            fsrsSettings = new UserFsrsSettings { UserId = userId };
            userContext.UserFsrsSettings.Add(fsrsSettings);
        }

        fsrsSettings.SettingsJson = JsonSerializer.Serialize(request);
        await userContext.SaveChangesAsync();

        return Results.Ok(request);
    }

    private async Task<StudySettingsDto> LoadStudySettings(string userId)
    {
        var fsrsSettings = await userContext.UserFsrsSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.UserId == userId);

        if (fsrsSettings == null || string.IsNullOrEmpty(fsrsSettings.SettingsJson) || fsrsSettings.SettingsJson == "{}")
            return new StudySettingsDto();

        try
        {
            return JsonSerializer.Deserialize<StudySettingsDto>(fsrsSettings.SettingsJson) ?? new StudySettingsDto();
        }
        catch (JsonException)
        {
            return new StudySettingsDto();
        }
    }

    private static List<(int WordId, byte ReadingIndex, long CardId, bool IsNew, int State)> InterleaveMixed(
        List<(int WordId, byte ReadingIndex, long CardId, bool IsNew, int State)> items)
    {
        var reviews = items.Where(i => !i.IsNew).ToList();
        var newCards = items.Where(i => i.IsNew).ToList();

        if (newCards.Count == 0) return reviews;
        if (reviews.Count == 0) return newCards;

        var result = new List<(int, byte, long, bool, int)>();
        var ratio = Math.Max(1, reviews.Count / Math.Max(1, newCards.Count));
        var ri = 0;
        var ni = 0;

        while (ri < reviews.Count || ni < newCards.Count)
        {
            for (var i = 0; i < ratio && ri < reviews.Count; i++)
                result.Add(reviews[ri++]);

            if (ni < newCards.Count)
                result.Add(newCards[ni++]);
        }

        return result;
    }

    [HttpGet("due-summary")]
    [SwaggerOperation(Summary = "Get due card counts for decks overview")]
    public async Task<IResult> GetDueSummary()
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        var now = DateTime.UtcNow;
        var todayStart = now.Date;
        var settings = await LoadStudySettings(userId);

        var dueCutoff = now.AddMinutes(15);
        var reviewsDue = await userContext.FsrsCards
            .CountAsync(c => c.UserId == userId
                             && c.State != FsrsState.New
                             && c.State != FsrsState.Blacklisted
                             && c.State != FsrsState.Mastered
                             && c.State != FsrsState.Suspended
                             && c.Due <= dueCutoff);

        var newCardsToday = await userContext.FsrsReviewLogs
            .Where(rl => rl.ReviewDateTime >= todayStart
                         && rl.Card.UserId == userId
                         && rl.Card.CreatedAt >= todayStart)
            .Select(rl => rl.CardId)
            .Distinct()
            .CountAsync();

        var newCardsAvailable = Math.Max(0, settings.NewCardsPerDay - newCardsToday);

        var reviewsToday = await userContext.FsrsReviewLogs
            .CountAsync(rl => rl.Card.UserId == userId && rl.ReviewDateTime >= todayStart);

        var reviewBudgetLeft = Math.Max(0, settings.MaxReviewsPerDay - reviewsToday);

        DateTime? nextReviewAt = null;
        if (reviewsDue == 0)
        {
            nextReviewAt = await userContext.FsrsCards
                .Where(c => c.UserId == userId
                            && c.State != FsrsState.New
                            && c.State != FsrsState.Blacklisted
                            && c.State != FsrsState.Mastered
                            && c.State != FsrsState.Suspended
                            && c.Due > dueCutoff)
                .OrderBy(c => c.Due)
                .Select(c => (DateTime?)c.Due)
                .FirstOrDefaultAsync();
        }

        return Results.Ok(new
        {
            reviewsDue,
            newCardsAvailable,
            reviewsToday,
            reviewBudgetLeft,
            nextReviewAt,
        });
    }

    [HttpGet("review-forecast")]
    [SwaggerOperation(Summary = "Get upcoming review forecast")]
    public async Task<IResult> GetReviewForecast()
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        var now = DateTime.UtcNow;
        var oneHour = now.AddHours(1);
        var oneDay = now.AddHours(24);
        var twoDays = now.AddHours(48);

        var baseQuery = userContext.FsrsCards
            .AsNoTracking()
            .Where(c => c.UserId == userId
                        && c.State != FsrsState.New
                        && c.State != FsrsState.Blacklisted
                        && c.State != FsrsState.Mastered
                        && c.State != FsrsState.Suspended
                        && c.Due > now);

        var dueWithinHour = await baseQuery.CountAsync(c => c.Due <= oneHour);
        var dueToday = await baseQuery.CountAsync(c => c.Due > oneHour && c.Due <= oneDay);
        var dueTomorrow = await baseQuery.CountAsync(c => c.Due > oneDay && c.Due <= twoDays);

        DateTime? nextReviewAt = null;
        if (dueWithinHour == 0 && dueToday == 0)
        {
            nextReviewAt = await userContext.FsrsCards
                .AsNoTracking()
                .Where(c => c.UserId == userId
                            && c.State != FsrsState.New
                            && c.State != FsrsState.Blacklisted
                            && c.State != FsrsState.Mastered
                            && c.State != FsrsState.Suspended
                            && c.Due > now)
                .OrderBy(c => c.Due)
                .Select(c => (DateTime?)c.Due)
                .FirstOrDefaultAsync();
        }

        return Results.Ok(new
        {
            dueWithinHour,
            dueToday,
            dueTomorrow,
            nextReviewAt,
        });
    }

    [HttpGet("deck-streak")]
    [SwaggerOperation(Summary = "Get streak info and recent activity for the decks page")]
    public async Task<IResult> GetDeckStreak()
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        var today = DateTime.UtcNow.Date;
        var windowStart = today.AddDays(-83); // ~12 weeks

        var dailyStats = await userContext.FsrsReviewLogs
            .AsNoTracking()
            .Where(rl => rl.Card.UserId == userId && rl.ReviewDateTime >= windowStart)
            .GroupBy(rl => rl.ReviewDateTime.Date)
            .Select(g => new { Date = g.Key, Count = g.Count() })
            .OrderBy(g => g.Date)
            .ToListAsync();

        var totalReviewDays = await userContext.FsrsReviewLogs
            .AsNoTracking()
            .Where(rl => rl.Card.UserId == userId)
            .Select(rl => rl.ReviewDateTime.Date)
            .Distinct()
            .CountAsync();

        var windowDates = dailyStats.Select(d => d.Date).OrderByDescending(d => d).ToList();
        var (currentStreak, longestStreak) = ComputeStreaks(windowDates, today);

        return Results.Ok(new
        {
            currentStreak,
            longestStreak,
            isNewRecord = currentStreak > 0 && currentStreak >= longestStreak,
            totalReviewDays,
            recentDays = dailyStats.Select(d => new { date = DateOnly.FromDateTime(d.Date).ToString("yyyy-MM-dd"), count = d.Count }),
        });
    }

    [HttpGet("session-streak")]
    [SwaggerOperation(Summary = "Get current streak info for session summary")]
    public async Task<IResult> GetSessionStreak()
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Results.Unauthorized();

        var today = DateTime.UtcNow.Date;
        var windowStart = today.AddDays(-83);

        var recentDates = await userContext.FsrsReviewLogs
            .AsNoTracking()
            .Where(rl => rl.Card.UserId == userId && rl.ReviewDateTime >= windowStart)
            .Select(rl => rl.ReviewDateTime.Date)
            .Distinct()
            .OrderByDescending(d => d)
            .ToListAsync();

        var (currentStreak, longestStreak) = ComputeStreaks(recentDates, today);

        return Results.Ok(new
        {
            currentStreak,
            longestStreak,
            isNewRecord = currentStreak > 0 && currentStreak >= longestStreak,
        });
    }

    private static (int currentStreak, int longestStreak) ComputeStreaks(List<DateTime> sortedDatesDesc, DateTime today)
    {
        if (sortedDatesDesc.Count == 0)
            return (0, 0);

        var currentStreak = 0;
        var checkDate = today;

        if (sortedDatesDesc[0].Date != today)
        {
            if (sortedDatesDesc[0].Date == today.AddDays(-1))
                checkDate = today.AddDays(-1);
            else
                goto longestOnly;
        }

        foreach (var date in sortedDatesDesc)
        {
            if (date.Date == checkDate)
            {
                currentStreak++;
                checkDate = checkDate.AddDays(-1);
            }
            else if (date.Date < checkDate)
                break;
        }

        longestOnly:

        var longest = 0;
        var streak = 1;
        for (var i = 1; i < sortedDatesDesc.Count; i++)
        {
            if (sortedDatesDesc[i - 1].Date.AddDays(-1) == sortedDatesDesc[i].Date)
                streak++;
            else
            {
                longest = Math.Max(longest, streak);
                streak = 1;
            }
        }
        longest = Math.Max(longest, streak);

        return (currentStreak, Math.Max(longest, currentStreak));
    }

    private record DeckProjection(int DeckId, string OriginalTitle, string? RomajiTitle, string? EnglishTitle, MediaType MediaType, int? ParentDeckId);

    private static StudyExampleSentenceDto BuildStudyExampleSentence(
        ExampleSentenceWord exWord,
        Dictionary<int, DeckProjection> decks,
        Dictionary<int, DeckProjection> parentDecks)
    {
        var dto = new StudyExampleSentenceDto
        {
            Text = exWord.ExampleSentence!.Text,
            WordPosition = exWord.Position,
            WordLength = exWord.Length
        };

        if (decks.TryGetValue(exWord.ExampleSentence.DeckId, out var deck))
        {
            dto.SourceDeck = new StudyExampleSourceDto
            {
                DeckId = deck.DeckId,
                OriginalTitle = deck.OriginalTitle,
                RomajiTitle = deck.RomajiTitle,
                EnglishTitle = deck.EnglishTitle,
                MediaType = (int)deck.MediaType
            };

            if (deck.ParentDeckId != null && parentDecks.TryGetValue(deck.ParentDeckId.Value, out var parent))
            {
                dto.SourceParent = new StudyExampleSourceDto
                {
                    DeckId = parent.DeckId,
                    OriginalTitle = parent.OriginalTitle,
                    RomajiTitle = parent.RomajiTitle,
                    EnglishTitle = parent.EnglishTitle,
                    MediaType = (int)parent.MediaType
                };
            }
        }

        return dto;
    }
}
