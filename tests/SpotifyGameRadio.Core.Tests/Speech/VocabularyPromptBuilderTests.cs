using SpotifyGameRadio.Core.Speech;
using Xunit;

namespace SpotifyGameRadio.Core.Tests.Speech;

public class VocabularyPromptBuilderTests
{
    [Fact]
    public void Build_NullOrWhitespace_ReturnsEmpty()
    {
        Assert.Equal("", VocabularyPromptBuilder.Build(null));
        Assert.Equal("", VocabularyPromptBuilder.Build(""));
        Assert.Equal("", VocabularyPromptBuilder.Build("   \n  \t "));
    }

    [Fact]
    public void Build_CommaSeparated_JoinsWithCommaSpace()
    {
        Assert.Equal("FOB, RTB, LZ", VocabularyPromptBuilder.Build("FOB, RTB, LZ"));
    }

    [Fact]
    public void Build_NewlineSeparated_JoinsWithCommaSpace()
    {
        Assert.Equal("FOB, RTB, LZ", VocabularyPromptBuilder.Build("FOB\nRTB\nLZ"));
    }

    [Fact]
    public void Build_MixedSeparatorsAndWhitespace_TrimsAndSplitsBoth()
    {
        Assert.Equal("FOB, RTB, LZ", VocabularyPromptBuilder.Build("  FOB \n, RTB ,\nLZ  "));
    }

    [Fact]
    public void Build_DropsBlankEntries()
    {
        Assert.Equal("FOB, RTB", VocabularyPromptBuilder.Build("FOB,,\n\nRTB,"));
    }

    [Fact]
    public void Build_DropsDuplicates_CaseInsensitive_KeepsFirstCasing()
    {
        Assert.Equal("FOB, RTB", VocabularyPromptBuilder.Build("FOB, RTB, fob, Rtb"));
    }

    [Fact]
    public void Build_PastCharacterBudget_TruncatesKeepingEarliestTerms()
    {
        // Each term is "term1".."term40" (5-7 chars) joined by ", " (2 chars).
        // 200-char budget should keep a prefix of the list, not all 40, and
        // never include a term whose full text would exceed the budget.
        var terms = Enumerable.Range(1, 40).Select(i => $"term{i}");
        string result = VocabularyPromptBuilder.Build(string.Join(",", terms));

        Assert.True(result.Length <= 200);
        Assert.StartsWith("term1, term2, term3", result);
        Assert.DoesNotContain("term40", result);
    }
}
