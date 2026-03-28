using Jiten.Api.Dtos;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Controllers;

[ApiController]
[Route("api/difficulty-rankings")]
[Produces("application/json")]
[Authorize]
public class DifficultyRankingController(
    JitenDbContext context,
    UserDbContext userContext,
    ICurrentUserService currentUserService) : ControllerBase
{
    [HttpGet]
    public async Task<IResult> GetRankings([FromQuery] MediaTypeGroup? group = null)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var completedDeckIds = await userContext.UserDeckPreferences
            .Where(p => p.UserId == userId && p.Status == DeckStatus.Completed)
            .Select(p => p.DeckId)
            .ToListAsync();

        if (completedDeckIds.Count == 0)
            return Results.Ok(Array.Empty<DifficultyRankingSectionDto>());

        var deckRows = await context.Decks.AsNoTracking()
            .Where(d => completedDeckIds.Contains(d.DeckId) && d.ParentDeckId == null)
            .Select(d => new { d.DeckId, d.OriginalTitle, d.RomajiTitle, d.EnglishTitle, d.CoverName, d.Difficulty, d.MediaType })
            .ToListAsync();

        var deckMap = deckRows.ToDictionary(d => d.DeckId, d => new
        {
            Summary = MapDeckSummary(d.DeckId, d.OriginalTitle, d.RomajiTitle, d.EnglishTitle, d.CoverName, d.Difficulty, d.MediaType),
            Group = MediaTypeGroups.GetGroup(d.MediaType)
        });

        var groupFilter = group.HasValue ? new HashSet<MediaTypeGroup> { group.Value } : null;

        var rankGroups = await context.DifficultyRankGroups.AsNoTracking()
            .Where(g => g.UserId == userId && (groupFilter == null || groupFilter.Contains(g.MediaTypeGroup)))
            .Include(g => g.Items)
            .OrderBy(g => g.SortIndex)
            .ToListAsync();

        var sections = new Dictionary<MediaTypeGroup, DifficultyRankingSectionDto>();
        foreach (var deckEntry in deckMap.Values)
        {
            if (groupFilter != null && !groupFilter.Contains(deckEntry.Group)) continue;
            if (!sections.ContainsKey(deckEntry.Group))
                sections[deckEntry.Group] = new DifficultyRankingSectionDto { Group = deckEntry.Group };
        }

        foreach (var groupEntry in sections.Values)
        {
            var groupsForSection = rankGroups
                .Where(g => g.MediaTypeGroup == groupEntry.Group)
                .OrderBy(g => g.SortIndex)
                .ToList();

            var rankedDeckIds = new HashSet<int>();
            foreach (var g in groupsForSection)
            {
                var decks = g.Items
                    .Select(i => deckMap.GetValueOrDefault(i.DeckId))
                    .Where(d => d != null)
                    .Select(d => d!.Summary)
                    .ToList();

                foreach (var d in decks) rankedDeckIds.Add(d.Id);

                groupEntry.Groups.Add(new DifficultyRankGroupDto
                {
                    Id = g.Id,
                    SortIndex = g.SortIndex,
                    Decks = decks
                });
            }

            var unranked = deckMap.Values
                .Where(d => d.Group == groupEntry.Group && !rankedDeckIds.Contains(d.Summary.Id))
                .Select(d => d.Summary)
                .OrderBy(d => d.Title)
                .ToList();

            groupEntry.Unranked = unranked;
        }

        return Results.Ok(sections.Values.OrderBy(s => s.Group).ToList());
    }

    [HttpPost("move")]
    public async Task<IResult> Move([FromBody] DifficultyRankingMoveRequest request)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var deck = await context.Decks
            .Where(d => d.DeckId == request.DeckId)
            .Select(d => new { d.DeckId, d.MediaType, d.ParentDeckId })
            .FirstOrDefaultAsync();
        if (deck == null)
            return Results.NotFound("Deck not found.");
        if (deck.ParentDeckId != null)
            return Results.BadRequest("Difficulty rankings are only available for parent decks, not subdecks.");

        var completed = await userContext.UserDeckPreferences
            .AnyAsync(p => p.UserId == userId && p.DeckId == request.DeckId && p.Status == DeckStatus.Completed);
        if (!completed)
            return Results.Problem("You must have completed this deck to rank it.", statusCode: 403);

        var group = MediaTypeGroups.GetGroup(deck.MediaType);

        var groups = await context.DifficultyRankGroups
            .Where(g => g.UserId == userId && g.MediaTypeGroup == group)
            .Include(g => g.Items)
            .OrderBy(g => g.SortIndex)
            .ToListAsync();

        DifficultyRankGroup? currentGroup = null;
        DifficultyRankItem? currentItem = null;
        foreach (var g in groups)
        {
            currentItem = g.Items.FirstOrDefault(i => i.DeckId == request.DeckId);
            if (currentItem != null)
            {
                currentGroup = g;
                break;
            }
        }

        if (currentItem != null)
        {
            currentGroup!.Items.Remove(currentItem);
            context.DifficultyRankItems.Remove(currentItem);
            if (currentGroup.Items.Count == 0)
            {
                context.DifficultyRankGroups.Remove(currentGroup);
                groups.Remove(currentGroup);
            }
        }

        switch (request.Mode)
        {
            case DifficultyRankingMoveMode.Unrank:
                break;
            case DifficultyRankingMoveMode.Merge:
            {
                if (request.TargetGroupId == null)
                    return Results.BadRequest("TargetGroupId is required for merge.");

                var target = groups.FirstOrDefault(g => g.Id == request.TargetGroupId.Value);
                if (target == null)
                    return Results.BadRequest("Target group not found.");

                var newItem = new DifficultyRankItem
                {
                    UserId = userId,
                    DeckId = request.DeckId,
                    Group = target,
                    CreatedAt = DateTimeOffset.UtcNow
                };
                target.Items.Add(newItem);
                context.DifficultyRankItems.Add(newItem);
                break;
            }
            case DifficultyRankingMoveMode.Insert:
            {
                if (request.InsertIndex == null)
                    return Results.BadRequest("InsertIndex is required for insert.");

                var insertIndex = Math.Clamp(request.InsertIndex.Value, 0, groups.Count);
                var newGroup = new DifficultyRankGroup
                {
                    UserId = userId,
                    MediaTypeGroup = group,
                    SortIndex = insertIndex,
                    CreatedAt = DateTimeOffset.UtcNow
                };
                var newItem = new DifficultyRankItem
                {
                    UserId = userId,
                    DeckId = request.DeckId,
                    Group = newGroup,
                    CreatedAt = DateTimeOffset.UtcNow
                };
                newGroup.Items.Add(newItem);
                groups.Insert(insertIndex, newGroup);
                context.DifficultyRankGroups.Add(newGroup);
                context.DifficultyRankItems.Add(newItem);
                break;
            }
            default:
                return Results.BadRequest("Invalid move mode.");
        }

        for (var i = 0; i < groups.Count; i++)
            groups[i].SortIndex = i;

        await context.SaveChangesAsync();
        await SyncDerivedVotes(userId, group);

        var result = await GetRankings(group);
        return result;
    }

    private async Task SyncDerivedVotes(string userId, MediaTypeGroup group)
    {
        var completedDeckIds = await userContext.UserDeckPreferences
            .Where(p => p.UserId == userId && p.Status == DeckStatus.Completed)
            .Select(p => p.DeckId)
            .ToListAsync();

        if (completedDeckIds.Count == 0)
            return;

        var deckRows = await context.Decks.AsNoTracking()
            .Where(d => completedDeckIds.Contains(d.DeckId) && d.ParentDeckId == null)
            .Select(d => new { d.DeckId, d.MediaType })
            .ToListAsync();

        var groups = await context.DifficultyRankGroups
            .Where(g => g.UserId == userId && g.MediaTypeGroup == group)
            .Include(g => g.Items)
            .OrderBy(g => g.SortIndex)
            .ToListAsync();

        var rankedDeckIds = groups
            .SelectMany(g => g.Items)
            .Select(i => i.DeckId)
            .ToHashSet();

        var groupDeckIds = deckRows
            .Where(d => MediaTypeGroups.GetGroup(d.MediaType) == group)
            .Select(d => d.DeckId)
            .ToHashSet();

        var implied = new Dictionary<(int lowId, int highId), ComparisonOutcome>();

        void AddPair(int deckAId, int deckBId, ComparisonOutcome outcomeForA)
        {
            var low = Math.Min(deckAId, deckBId);
            var high = Math.Max(deckAId, deckBId);
            var outcome = outcomeForA;
            if (outcome != ComparisonOutcome.Same && deckAId > deckBId)
                outcome = (ComparisonOutcome)(-(int)outcome);
            implied[(low, high)] = outcome;
        }

        var orderedGroups = groups
            .Select(g => g.Items.Select(i => i.DeckId).OrderBy(id => id).ToList())
            .ToList();

        var rankIndexByDeckId = new Dictionary<int, int>();
        for (var i = 0; i < orderedGroups.Count; i++)
        {
            foreach (var id in orderedGroups[i])
                rankIndexByDeckId[id] = i;
        }

        foreach (var groupDecks in orderedGroups)
        {
            for (var i = 1; i < groupDecks.Count; i++)
                AddPair(groupDecks[i - 1], groupDecks[i], ComparisonOutcome.Same);
        }

        for (var i = 0; i + 1 < orderedGroups.Count; i++)
        {
            var easierGroup = orderedGroups[i];
            var harderGroup = orderedGroups[i + 1];
            foreach (var easier in easierGroup)
            foreach (var harder in harderGroup)
                AddPair(easier, harder, ComparisonOutcome.Easier);
        }

        var staleDerived = await context.DifficultyVotes
            .Where(v => v.UserId == userId
                && v.Source == DifficultyVoteSource.WeakOrder
                && (!rankedDeckIds.Contains(v.DeckLowId) || !rankedDeckIds.Contains(v.DeckHighId))
                && (groupDeckIds.Contains(v.DeckLowId) || groupDeckIds.Contains(v.DeckHighId)))
            .ToListAsync();
        if (staleDerived.Count > 0)
            context.DifficultyVotes.RemoveRange(staleDerived);

        if (rankedDeckIds.Count < 2)
        {
            await context.SaveChangesAsync();
            return;
        }

        var manualPairSet = await context.DifficultyVotes
            .Where(v => v.UserId == userId
                && v.IsValid
                && v.Source == DifficultyVoteSource.Manual
                && rankedDeckIds.Contains(v.DeckLowId)
                && rankedDeckIds.Contains(v.DeckHighId))
            .Select(v => new { v.DeckLowId, v.DeckHighId })
            .ToListAsync();
        var manualPairs = manualPairSet
            .Select(v => (v.DeckLowId, v.DeckHighId))
            .ToHashSet();

        foreach (var pair in manualPairs)
            implied.Remove(pair);

        var existingWeakOrder = await context.DifficultyVotes
            .Where(v => v.UserId == userId
                && v.Source == DifficultyVoteSource.WeakOrder
                && rankedDeckIds.Contains(v.DeckLowId)
                && rankedDeckIds.Contains(v.DeckHighId))
            .ToListAsync();
        existingWeakOrder = existingWeakOrder
            .Where(v => !manualPairs.Contains((v.DeckLowId, v.DeckHighId)))
            .ToList();

        var existingPairs = existingWeakOrder.ToDictionary(v => (v.DeckLowId, v.DeckHighId));
        var now = DateTimeOffset.UtcNow;

        foreach (var vote in existingWeakOrder)
        {
            var rankLow = rankIndexByDeckId[vote.DeckLowId];
            var rankHigh = rankIndexByDeckId[vote.DeckHighId];
            var outcome = rankLow == rankHigh
                ? ComparisonOutcome.Same
                : rankLow < rankHigh ? ComparisonOutcome.Easier : ComparisonOutcome.Harder;

            if (vote.Outcome != outcome || vote.Source != DifficultyVoteSource.WeakOrder || !vote.IsValid)
            {
                vote.Outcome = outcome;
                vote.Source = DifficultyVoteSource.WeakOrder;
                vote.IsValid = true;
                vote.UpdatedAt = now;
            }
        }

        foreach (var kvp in implied)
        {
            if (existingPairs.ContainsKey(kvp.Key)) continue;
            context.DifficultyVotes.Add(new DifficultyVote
            {
                UserId = userId,
                DeckLowId = kvp.Key.lowId,
                DeckHighId = kvp.Key.highId,
                Outcome = kvp.Value,
                Source = DifficultyVoteSource.WeakOrder,
                CreatedAt = now,
                IsValid = true
            });
        }

        await context.SaveChangesAsync();
    }

    private static DeckSummaryDto MapDeckSummary(int deckId, string title, string? romajiTitle, string? englishTitle, string coverName, float difficulty, MediaType mediaType) => new()
    {
        Id = deckId,
        Title = title,
        RomajiTitle = romajiTitle,
        EnglishTitle = englishTitle,
        CoverUrl = coverName,
        Difficulty = difficulty,
        MediaType = mediaType
    };
}
