using Jiten.Core.Data;

namespace Jiten.Parser.Scoring;

internal static class AdjacentWordScorer
{
    private static readonly HashSet<string> CommonParticles =
    [
        "が", "を", "に", "で", "へ", "は", "の", "も", "や",
        "から", "まで", "より", "だけ", "しか", "ばかり", "など", "さえ"
    ];

    private static readonly HashSet<string> CopulaForms = ["だ", "です", "である"];

    internal readonly record struct AdjacentContext(
        List<PartOfSpeech>? PrevResolvedPOS,
        List<PartOfSpeech>? NextResolvedPOS,
        string? PrevText,
        string? NextText,
        int? PrevWordId,
        int? NextWordId,
        bool IsSentenceInitial,
        bool IsSentenceFinal)
    {
        internal bool PrevIs(PartOfSpeech pos) => PrevResolvedPOS?.Contains(pos) == true;
        internal bool NextIs(PartOfSpeech pos) => NextResolvedPOS?.Contains(pos) == true;

        internal bool PrevIsAny(PartOfSpeech a, PartOfSpeech b)
        {
            var list = PrevResolvedPOS;
            return list != null && (list.Contains(a) || list.Contains(b));
        }

        internal bool NextIsAny(PartOfSpeech a, PartOfSpeech b)
        {
            var list = NextResolvedPOS;
            return list != null && (list.Contains(a) || list.Contains(b));
        }
    }

    internal static bool HasApplicableRules(
        DeckWord current, WordInfo currentInfo,
        DeckWord? prev, WordInfo? prevInfo,
        DeckWord? next, WordInfo? nextInfo)
    {
        if (current.PartsOfSpeech.Count == 0)
            return false;

        if (next != null && nextInfo != null)
        {
            // Noun+Particle synergy
            if (next.PartsOfSpeech.Contains(PartOfSpeech.Particle) && CommonParticles.Contains(nextInfo.Text))
                return true;

            // Noun+Copula
            if (CopulaForms.Contains(nextInfo.Text))
                return true;

            // NaAdj+な/に
            if (nextInfo.Text is "な" or "に")
                return true;

            // Adverb+Verb: use resolved POS to check if the next word is actually a verb
            if (next.PartsOfSpeech.Any(p => p is PartOfSpeech.Verb or PartOfSpeech.IAdjective))
                return true;
        }

        // Verb+Auxiliary: use resolved POS
        if (prev != null && prevInfo != null)
        {
            if (prev.PartsOfSpeech.Any(p => p is PartOfSpeech.Verb or PartOfSpeech.IAdjective)
                && current.PartsOfSpeech.Contains(PartOfSpeech.Auxiliary))
                return true;
        }

        // SingleKana+SingleKana penalty
        if (currentInfo.Text.Length == 1 && !current.PartsOfSpeech.Contains(PartOfSpeech.Particle))
        {
            if (prevInfo is { Text.Length: 1 } && prev?.PartsOfSpeech.Contains(PartOfSpeech.Particle) != true)
                return true;
            if (nextInfo is { Text.Length: 1 } && next?.PartsOfSpeech.Contains(PartOfSpeech.Particle) != true)
                return true;
        }

        // Particle+Particle penalty
        if (current.PartsOfSpeech.Contains(PartOfSpeech.Particle))
        {
            if (prev?.PartsOfSpeech.Contains(PartOfSpeech.Particle) == true)
                return true;
            if (next?.PartsOfSpeech.Contains(PartOfSpeech.Particle) == true)
                return true;
        }

        return false;
    }

    internal static (int bonus, List<string> rulesMatched) CalculateContextBonus(
        FormCandidate candidate,
        AdjacentContext context)
    {
        int bonus = 0;
        var rulesMatched = new List<string>();

        var candidatePosList = candidate.Word.PartsOfSpeech.ToPartOfSpeech();

        bool isNounLike = candidatePosList.Any(p => p is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                                                         or PartOfSpeech.NaAdjective or PartOfSpeech.Pronoun
                                                         or PartOfSpeech.Name);
        bool isNaAdj = candidatePosList.Contains(PartOfSpeech.NaAdjective);
        bool isAdverb = candidatePosList.Any(p => p is PartOfSpeech.Adverb or PartOfSpeech.AdverbTo);
        bool isAuxiliary = candidatePosList.Contains(PartOfSpeech.Auxiliary);
        bool isParticle = candidatePosList.Contains(PartOfSpeech.Particle);

        // Noun+Particle synergy
        if (isNounLike && context.NextIs(PartOfSpeech.Particle) && context.NextText != null
            && CommonParticles.Contains(context.NextText))
        {
            bonus += 40;
            rulesMatched.Add($"Noun+Particle (right: {context.NextText})");
        }

        // Noun+Copula synergy
        if (isNounLike && context.NextText != null && CopulaForms.Contains(context.NextText))
        {
            bonus += 30;
            rulesMatched.Add($"Noun+Copula (right: {context.NextText})");
        }

        // NaAdj+な/に synergy
        if (isNaAdj && context.NextText is "な" or "に")
        {
            bonus += 30;
            rulesMatched.Add($"NaAdj+な/に (right: {context.NextText})");
        }

        // Adverb+Verb synergy
        if (isAdverb && context.NextIsAny(PartOfSpeech.Verb, PartOfSpeech.IAdjective))
        {
            bonus += 20;
            rulesMatched.Add("Adverb+Verb/Adj");
        }

        // Verb+Auxiliary synergy
        if (isAuxiliary && context.PrevIsAny(PartOfSpeech.Verb, PartOfSpeech.IAdjective))
        {
            bonus += 20;
            rulesMatched.Add("Verb+Auxiliary");
        }

        // SingleKana+SingleKana penalty
        if (candidate.Form.Text.Length <= 1 && !isParticle)
        {
            if (context.PrevText is { Length: 1 } && !context.PrevIs(PartOfSpeech.Particle))
            {
                bonus -= 40;
                rulesMatched.Add("SingleKana+SingleKana (left)");
            }

            if (context.NextText is { Length: 1 } && !context.NextIs(PartOfSpeech.Particle))
            {
                bonus -= 40;
                rulesMatched.Add("SingleKana+SingleKana (right)");
            }
        }

        // Particle+Particle penalty
        if (isParticle)
        {
            if (context.PrevIs(PartOfSpeech.Particle))
            {
                bonus -= 20;
                rulesMatched.Add("Particle+Particle (left)");
            }

            if (context.NextIs(PartOfSpeech.Particle))
            {
                bonus -= 20;
                rulesMatched.Add("Particle+Particle (right)");
            }
        }

        return (bonus, rulesMatched);
    }
}
