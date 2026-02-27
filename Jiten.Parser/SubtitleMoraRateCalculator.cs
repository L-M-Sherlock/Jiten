using System.Text;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Unicode;
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
    // Drop elongation tildes that follow kana so they do not count toward mora.
    private static readonly Regex KanaTildeRegex =
        new(@"(?<=[\u3040-\u309F\u30A0-\u30FF])[～〜]+", RegexOptions.Compiled);
    private const string SokuonChars = "っッ";
    private const string SmallKanaChars = "ぁぃぅぇぉゃゅょゎゕゖァィゥェォャュョヮヵヶ";
    private const char LongVowelMark = 'ー';

    public static async Task<SubtitleMoraStats> ComputeAsync(IEnumerable<SubtitleItem> items)
    {
        var intervals = new List<(int start, int end)>();
        var texts = new List<string>();

        foreach (var item in items)
        {
            if (!TryGetSpokenText(item.Text, out var spoken))
                continue;

            intervals.Add((item.StartMs, item.EndMs));
            texts.Add(spoken);
        }

        var moraCount = await CountMoraAsync(texts);
        var durationMs = MergeIntervals(intervals).Sum(i => (long)i.end - i.start);

        return new SubtitleMoraStats(moraCount, durationMs);
    }

    private static bool TryGetSpokenText(string rawText, out string spoken)
    {
        spoken = string.Empty;

        var cleaned = SubtitleTextCleaner.CleanText(rawText);
        if (string.IsNullOrWhiteSpace(cleaned))
            return false;

        var stripped = SubtitleTextCleaner.StripNonSpoken(cleaned);
        if (string.IsNullOrWhiteSpace(stripped))
            return false;

        stripped = KanaTildeRegex.Replace(stripped, "");
        if (string.IsNullOrWhiteSpace(stripped))
            return false;

        spoken = stripped;
        return true;
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

        var reading = GetReadingOrSurface(word);

        var count = 0;
        foreach (var rune in reading.EnumerateRunes())
        {
            if (IsMoraKana(rune))
                count++;
        }

        return count;
    }

    private static string GetReadingOrSurface(WordInfo word)
    {
        var reading = word.Reading;
        return string.IsNullOrEmpty(reading) || reading == "*" ? word.Text : reading;
    }

    // Mora counting here explicitly excludes sokuon (small tsu) and the long vowel mark.
    private static bool IsMoraKana(Rune rune)
    {
        if (!IsKana(rune))
            return false;
        if (IsSokuon(rune))
            return false;
        if (IsLongVowelMark(rune))
            return false;
        if (IsSmallKana(rune))
            return false;
        return true;
    }

    private static bool IsKana(Rune rune)
    {
        return IsInRange(rune, UnicodeRanges.Hiragana) || IsInRange(rune, UnicodeRanges.Katakana);
    }

    private static bool IsSokuon(Rune rune)
    {
        return rune.Value <= char.MaxValue && SokuonChars.Contains((char)rune.Value);
    }

    private static bool IsLongVowelMark(Rune rune)
    {
        return rune.Value == LongVowelMark;
    }

    private static bool IsSmallKana(Rune rune)
    {
        return rune.Value <= char.MaxValue && SmallKanaChars.Contains((char)rune.Value);
    }

    private static bool IsInRange(Rune rune, UnicodeRange range)
    {
        var start = range.FirstCodePoint;
        var end = start + range.Length - 1;
        var value = rune.Value;
        return value >= start && value <= end;
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
