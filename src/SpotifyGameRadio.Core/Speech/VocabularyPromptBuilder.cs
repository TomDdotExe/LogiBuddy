namespace SpotifyGameRadio.Core.Speech;

/// Turns the free-text vocabulary list a user types into the UI into either
/// the prompt string handed to Whisper as a soft bias toward those terms
/// (Build/BuildFromTerms), or the parsed term list itself (ParseTerms) —
/// used by VocabularyCorrector to fuzzy-match low-confidence transcript
/// segments against the same vocabulary.
public static class VocabularyPromptBuilder
{
    // The initial-prompt is only a soft bias, not a hard constraint, so it
    // doesn't need to hold much — this keeps prompt-construction cheap and
    // avoids the (unlikely but possible) case of the prompt itself eating
    // into the model's context window.
    private const int CharacterBudget = 200;

    /// Splits, trims, and de-duplicates (case-insensitively, keeping the
    /// first casing seen) the raw comma/newline-separated list. The full,
    /// untruncated term list — unlike Build's prompt string, which is
    /// budget-limited — since VocabularyCorrector needs every term to match
    /// against, not just the ones that fit in a Whisper prompt.
    public static IReadOnlyList<string> ParseTerms(string? rawList)
    {
        if (string.IsNullOrWhiteSpace(rawList)) return Array.Empty<string>();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var terms = new List<string>();
        foreach (var raw in rawList.Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string term = raw.Trim();
            if (term.Length == 0) continue;
            if (!seen.Add(term)) continue;
            terms.Add(term);
        }
        return terms;
    }

    public static string Build(string? rawList) => BuildFromTerms(ParseTerms(rawList));

    /// Joins an already-parsed term list into a Whisper initial-prompt,
    /// keeping only as many terms (earliest first) as fit CharacterBudget.
    /// Ends with a period rather than trailing off after the last term —
    /// confirmed via real-model A/B testing that a comma-separated list with
    /// no closing punctuation measurably (5/5 repeated runs vs. 0/5 after
    /// this fix) leaks stray punctuation ("-hour,", etc.) onto the start of
    /// the actual transcript, since the model reads the prompt as unfinished
    /// list-formatting to continue rather than a completed thought.
    public static string BuildFromTerms(IReadOnlyList<string> terms)
    {
        var result = new System.Text.StringBuilder();
        foreach (var term in terms)
        {
            string candidate = result.Length == 0 ? term : result + ", " + term;
            if (candidate.Length + 1 > CharacterBudget) break; // +1 leaves room for the closing period
            if (result.Length > 0) result.Append(", ");
            result.Append(term);
        }
        if (result.Length > 0) result.Append('.');
        return result.ToString();
    }
}
