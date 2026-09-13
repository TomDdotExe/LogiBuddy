namespace SpotifyGameRadio.Core.Speech;

/// Defensive cleanup applied to a finished transcript, independent of
/// whatever caused the raw text to need it (a leaky prompt, a decode
/// artifact, etc.) — a transcript should never legitimately start with
/// bare punctuation, so trimming it is always safe.
public static class TranscriptSanitizer
{
    /// Strips any leading run of non-alphanumeric characters — punctuation,
    /// symbols, and whitespace, in any mixture — e.g. "-, ... can I get" ->
    /// "can I get". Leaves interior punctuation (mid-sentence commas, etc.)
    /// untouched — only a leading run is ever spurious.
    public static string TrimLeadingSymbols(string text)
    {
        int i = 0;
        while (i < text.Length && !char.IsLetterOrDigit(text[i])) i++;
        return text[i..];
    }
}
