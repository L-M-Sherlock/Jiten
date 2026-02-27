using System.Text;
using System.Linq;
using System.Text.RegularExpressions;
using Jiten.Core;
using Jiten.Core.Data;

namespace Jiten.Parser;

public readonly record struct SubtitleKanaStats(long KanaCount, long DurationMs)
{
    public static readonly SubtitleKanaStats Empty = new(0, 0);

    public double KanaPerMinute => DurationMs > 0 ? KanaCount / (DurationMs / 60000.0) : 0;
}

public static class SubtitleKanaRateCalculator
{
    private static readonly Regex KanaTildeRegex = new(@"(?<=[\u3040-\u309F\u30A0-\u30FF])[～〜]+", RegexOptions.Compiled);

    public static async Task<SubtitleKanaStats> ComputeAsync(IEnumerable<SubtitleItem> items)
    {
        var intervals = new List<(int start, int end)>();
        var texts = new List<string>();

        foreach (var item in items)
        {
            var cleaned = SubtitleTextCleaner.CleanText(item.Text);
            if (string.IsNullOrWhiteSpace(cleaned))
                continue;

            intervals.Add((item.StartMs, item.EndMs));

            var spoken = SubtitleTextCleaner.StripNonSpoken(cleaned);
            if (string.IsNullOrWhiteSpace(spoken))
                continue;

            spoken = KanaTildeRegex.Replace(spoken, "");
            if (string.IsNullOrWhiteSpace(spoken))
                continue;

            texts.Add(spoken);
        }

        var kanaCount = await CountKanaAsync(texts);
        var durationMs = MergeIntervals(intervals).Sum(i => (long)i.end - i.start);

        return new SubtitleKanaStats(kanaCount, durationMs);
    }

    private static async Task<long> CountKanaAsync(IReadOnlyList<string> texts)
    {
        if (texts.Count == 0)
            return 0;

        var combined = string.Join("\n", texts);
        if (string.IsNullOrWhiteSpace(combined))
            return 0;

        var parser = new MorphologicalAnalyser();
        var sentences = await parser.Parse(combined, morphemesOnly: true);

        long count = 0;
        foreach (var sentence in sentences)
        {
            foreach (var (word, _, _) in sentence.Words)
            {
                count += CountKana(word);
            }
        }

        return count;
    }

    private static int CountKana(WordInfo word)
    {
        if (word.PartOfSpeech is PartOfSpeech.Symbol or PartOfSpeech.SupplementarySymbol or PartOfSpeech.BlankSpace)
            return 0;

        var reading = word.Reading;
        if (string.IsNullOrEmpty(reading) || reading == "*")
            reading = word.Text;

        var count = 0;
        foreach (var rune in reading.EnumerateRunes())
        {
            if (IsKana(rune))
                count++;
        }

        return count;
    }

    private static bool IsKana(Rune rune)
    {
        return rune.Value is >= 0x3040 and <= 0x309F
            || rune.Value is >= 0x30A0 and <= 0x30FF;
    }

    private static List<(int start, int end)> MergeIntervals(List<(int start, int end)> intervals)
    {
        var valid = intervals.Where(i => i.end > i.start).OrderBy(i => i.start).ToList();
        if (valid.Count == 0)
            return [];

        var merged = new List<(int start, int end)> { valid[0] };
        for (var i = 1; i < valid.Count; i++)
        {
            var current = valid[i];
            var last = merged[^1];
            if (current.start <= last.end)
            {
                merged[^1] = (last.start, Math.Max(last.end, current.end));
            }
            else
            {
                merged.Add(current);
            }
        }

        return merged;
    }
}
