namespace LogiBuddy.Core.Speech;

/// Fuzzy-corrects Whisper transcript segments Whisper itself was already
/// unsure about (low Probability) against the user's own vocabulary list —
/// catches cases like "tower too" getting corrected to "Tower 2" when the
/// user has "Tower 2" in their vocabulary. Deliberately does nothing to
/// high-confidence segments: this is a targeted fix-up for words the model
/// already flagged as shaky, not a general spell-checker that might rewrite
/// something that was already right.
public static class VocabularyCorrector
{
    /// Segments at or above this Probability are left untouched.
    public const float LowConfidenceThreshold = 0.6f;

    // Vocabulary terms shorter than this (once whitespace is stripped) only
    // match exactly, never fuzzily — a short term like "RP" is within one
    // edit of plenty of ordinary words ("up"), so fuzzy-matching it would
    // "correct" things that were never wrong.
    private const int MinLengthForFuzzyMatch = 4;

    public sealed record Segment(string Text, float Probability);

    public static string Correct(IReadOnlyList<Segment> segments, IReadOnlyList<string> vocabularyTerms)
    {
        if (segments.Count == 0) return "";

        var sb = new System.Text.StringBuilder();
        foreach (var segment in segments)
        {
            sb.Append(vocabularyTerms.Count == 0 || segment.Probability >= LowConfidenceThreshold
                ? segment.Text
                : CorrectSegment(segment.Text, vocabularyTerms));
        }
        return sb.ToString();
    }

    private static string CorrectSegment(string text, IReadOnlyList<string> vocabularyTerms)
    {
        string[] words = text.Split(' ');
        // Longest term first: a multi-word term like "Tower 2" should win
        // over any single-word term that would otherwise match just "tower".
        var termsByWordCountDesc = vocabularyTerms
            .Select(term => (Term: term, Words: term.Split(' ')))
            .OrderByDescending(t => t.Words.Length)
            .ToList();

        var result = new List<string>(words.Length);
        int i = 0;
        while (i < words.Length)
        {
            (string Term, int WordCount)? match = FindBestMatch(words, i, termsByWordCountDesc);
            if (match is { } m)
            {
                // Preserve trailing punctuation from the last matched word
                // (e.g. a sentence-ending period) rather than dropping it.
                string lastWord = words[i + m.WordCount - 1];
                var (_, trailingPunctuation) = SplitTrailingPunctuation(lastWord);
                result.Add(m.Term + trailingPunctuation);
                i += m.WordCount;
            }
            else
            {
                result.Add(words[i]);
                i++;
            }
        }
        return string.Join(' ', result);
    }

    private static (string Term, int WordCount)? FindBestMatch(
        string[] words, int startIndex, List<(string Term, string[] Words)> termsByWordCountDesc)
    {
        foreach (var (term, termWords) in termsByWordCountDesc)
        {
            int wordCount = termWords.Length;
            if (startIndex + wordCount > words.Length) continue;

            string window = string.Join(' ', words.Skip(startIndex).Take(wordCount).Select(w => SplitTrailingPunctuation(w).Core));
            string normalizedTerm = string.Join(' ', termWords);

            if (string.Equals(window, normalizedTerm, StringComparison.OrdinalIgnoreCase))
                return (term, wordCount);

            if (normalizedTerm.Length < MinLengthForFuzzyMatch) continue;

            int maxDistance = Math.Max(1, (int)Math.Ceiling(normalizedTerm.Length * 0.5));
            if (Levenshtein(window.ToLowerInvariant(), normalizedTerm.ToLowerInvariant()) <= maxDistance)
                return (term, wordCount);
        }
        return null;
    }

    /// Splits a word into its core text and any trailing punctuation run
    /// (e.g. "2," -> ("2", ",")), so matching can ignore punctuation while
    /// a replacement can still keep it.
    private static (string Core, string Punctuation) SplitTrailingPunctuation(string word)
    {
        int end = word.Length;
        while (end > 0 && char.IsPunctuation(word[end - 1])) end--;
        return (word[..end], word[end..]);
    }

    /// Classic dynamic-programming edit distance (insert/delete/substitute,
    /// each cost 1) between two strings, case-sensitive — callers lowercase
    /// first if a case-insensitive comparison is wanted.
    private static int Levenshtein(string a, string b)
    {
        int[,] d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        }
        return d[a.Length, b.Length];
    }
}
