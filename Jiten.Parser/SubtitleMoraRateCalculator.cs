using System.Text;
using System.Linq;
using System.Text.RegularExpressions;
using Jiten.Core;
using Jiten.Core.Data;

namespace Jiten.Parser;

public readonly record struct SubtitleMoraStats(long MoraCount, long DurationMs)
{
    public static readonly SubtitleMoraStats Empty = new(0, 0);

    public double MoraPerMinute => DurationMs > 0 ? MoraCount / (DurationMs / 60000.0) : 0;
}

public static class SubtitleMoraRateCalculator
{
    private static readonly Regex KanaTildeRegex = new(@"(?<=[\u3040-\u309F\u30A0-\u30FF])[～〜]+", RegexOptions.Compiled);

    public static async Task<SubtitleMoraStats> ComputeAsync(IEnumerable<SubtitleItem> items)
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

        var moraCount = await CountMoraAsync(texts);
        var durationMs = MergeIntervals(intervals).Sum(i => (long)i.end - i.start);

        return new SubtitleMoraStats(moraCount, durationMs);
    }

    private static async Task<long> CountMoraAsync(IReadOnlyList<string> texts)
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
                count += CountMora(word);
            }
        }

        return count;
    }

    private static int CountMora(WordInfo word)
    {
        if (word.PartOfSpeech is PartOfSpeech.Symbol or PartOfSpeech.SupplementarySymbol or PartOfSpeech.BlankSpace)
            return 0;

        var reading = word.Reading;
        if (string.IsNullOrEmpty(reading) || reading == "*")
            reading = word.Text;

        var count = 0;
        foreach (var rune in reading.EnumerateRunes())
        {
            if (IsSokuon(rune))
                continue;
            if (IsSmallKana(rune))
                continue;
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

    private static bool IsSokuon(Rune rune)
    {
        return rune.Value is 0x3063 or 0x30C3;
    }

    private static bool IsSmallKana(Rune rune)
    {
        return rune.Value is 0x3041 or 0x3043 or 0x3045 or 0x3047 or 0x3049
            or 0x3083 or 0x3085 or 0x3087 or 0x308E or 0x3095 or 0x3096
            or 0x30A1 or 0x30A3 or 0x30A5 or 0x30A7 or 0x30A9
            or 0x30E3 or 0x30E5 or 0x30E7 or 0x30EE or 0x30F5 or 0x30F6;
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
