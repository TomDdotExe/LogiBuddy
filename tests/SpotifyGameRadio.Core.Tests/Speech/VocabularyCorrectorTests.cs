using SpotifyGameRadio.Core.Speech;
using Xunit;

namespace SpotifyGameRadio.Core.Tests.Speech;

public class VocabularyCorrectorTests
{
    [Fact]
    public void HighConfidenceSegment_IsNeverTouched_EvenIfItWouldOtherwiseMatch()
    {
        var segments = new[] { new VocabularyCorrector.Segment("dropping supplies on tower too", 0.95f) };

        string result = VocabularyCorrector.Correct(segments, new[] { "Tower 2" });

        Assert.Equal("dropping supplies on tower too", result);
    }

    [Fact]
    public void LowConfidenceSegment_CorrectsAHomophoneMatch_AgainstVocabulary()
    {
        var segments = new[] { new VocabularyCorrector.Segment("dropping supplies on tower too", 0.4f) };

        string result = VocabularyCorrector.Correct(segments, new[] { "Tower 2" });

        Assert.Equal("dropping supplies on Tower 2", result);
    }

    [Fact]
    public void LowConfidenceSegment_PreservesTrailingPunctuation()
    {
        var segments = new[] { new VocabularyCorrector.Segment("send it to tower too.", 0.4f) };

        string result = VocabularyCorrector.Correct(segments, new[] { "Tower 2" });

        Assert.Equal("send it to Tower 2.", result);
    }

    [Fact]
    public void LowConfidenceSegment_ExactMatch_NormalizesCasingToVocabularyTerm()
    {
        var segments = new[] { new VocabularyCorrector.Segment("returning to fob now", 0.3f) };

        string result = VocabularyCorrector.Correct(segments, new[] { "FOB" });

        Assert.Equal("returning to FOB now", result);
    }

    [Fact]
    public void LowConfidenceSegment_NoCloseVocabularyMatch_LeavesWordsUnchanged()
    {
        var segments = new[] { new VocabularyCorrector.Segment("we are heading north now", 0.3f) };

        string result = VocabularyCorrector.Correct(segments, new[] { "Tower 2", "FOB" });

        Assert.Equal("we are heading north now", result);
    }

    [Fact]
    public void ShortVocabularyTerm_OnlyMatchesExactly_NeverFuzzily()
    {
        // "RP" (rally point) is one Levenshtein edit from "up" — must not
        // fuzzy-match an unrelated short word just because it's low confidence.
        var segments = new[] { new VocabularyCorrector.Segment("moving up the hill", 0.2f) };

        string result = VocabularyCorrector.Correct(segments, new[] { "RP" });

        Assert.Equal("moving up the hill", result);
    }

    [Fact]
    public void MultiWordTerm_PreferredOverASingleWordTermThatWouldPartiallyMatch()
    {
        var segments = new[] { new VocabularyCorrector.Segment("meet at tower too", 0.3f) };

        string result = VocabularyCorrector.Correct(segments, new[] { "Tower 2", "Tower" });

        Assert.Equal("meet at Tower 2", result);
    }

    [Fact]
    public void MultipleSegments_OnlyCorrectsTheLowConfidenceOne()
    {
        var segments = new[]
        {
            new VocabularyCorrector.Segment("all clear at the ", 0.9f),
            new VocabularyCorrector.Segment("tower too", 0.3f),
        };

        string result = VocabularyCorrector.Correct(segments, new[] { "Tower 2" });

        Assert.Equal("all clear at the Tower 2", result);
    }

    [Fact]
    public void EmptyVocabulary_LeavesLowConfidenceSegmentUnchanged()
    {
        var segments = new[] { new VocabularyCorrector.Segment("tower too", 0.1f) };

        string result = VocabularyCorrector.Correct(segments, Array.Empty<string>());

        Assert.Equal("tower too", result);
    }

    [Fact]
    public void NoSegments_ReturnsEmptyString()
    {
        string result = VocabularyCorrector.Correct(Array.Empty<VocabularyCorrector.Segment>(), new[] { "Tower 2" });

        Assert.Equal("", result);
    }
}
