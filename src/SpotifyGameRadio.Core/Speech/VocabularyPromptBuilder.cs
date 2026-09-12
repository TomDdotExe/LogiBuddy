namespace SpotifyGameRadio.Core.Speech;

/// Turns the free-text vocabulary list a user types into the UI into the
/// prompt string handed to Whisper as a soft bias toward those terms.
public static class VocabularyPromptBuilder
{
    // The initial-prompt is only a soft bias, not a hard constraint, so it
    // doesn't need to hold much — this keeps prompt-construction cheap and
    // avoids the (unlikely but possible) case of the prompt itself eating
    // into the model's context window.
    private const int CharacterBudget = 200;

    public static string Build(string? rawList)
    {
        if (string.IsNullOrWhiteSpace(rawList)) return "";

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var terms = new List<string>();
        foreach (var raw in rawList.Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string term = raw.Trim();
            if (term.Length == 0) continue;
            if (!seen.Add(term)) continue;
            terms.Add(term);
        }

        var result = new System.Text.StringBuilder();
        foreach (var term in terms)
        {
            string candidate = result.Length == 0 ? term : result + ", " + term;
            if (candidate.Length > CharacterBudget) break;
            if (result.Length > 0) result.Append(", ");
            result.Append(term);
        }
        return result.ToString();
    }
}
