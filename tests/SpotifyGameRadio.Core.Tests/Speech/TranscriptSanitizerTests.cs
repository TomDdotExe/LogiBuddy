using SpotifyGameRadio.Core.Speech;
using Xunit;

namespace SpotifyGameRadio.Core.Tests.Speech;

public class TranscriptSanitizerTests
{
    [Fact]
    public void LeadingComma_IsStripped()
    {
        Assert.Equal("tower, tower", TranscriptSanitizer.TrimLeadingSymbols(", tower, tower"));
    }

    [Fact]
    public void LeadingCommaWithoutSpace_IsStripped()
    {
        Assert.Equal("tower", TranscriptSanitizer.TrimLeadingSymbols(",tower"));
    }

    [Fact]
    public void LeadingDash_IsStripped()
    {
        Assert.Equal("can I get supplies", TranscriptSanitizer.TrimLeadingSymbols("- can I get supplies"));
    }

    [Fact]
    public void MultipleLeadingSymbols_AreAllStripped()
    {
        Assert.Equal("can I get supplies", TranscriptSanitizer.TrimLeadingSymbols("-, ... can I get supplies"));
    }

    [Fact]
    public void NoLeadingSymbols_IsUnchanged()
    {
        Assert.Equal("can I get supplies", TranscriptSanitizer.TrimLeadingSymbols("can I get supplies"));
    }

    [Fact]
    public void InteriorPunctuation_IsNeverTouched()
    {
        Assert.Equal("can I get supplies, please, on tower 2", TranscriptSanitizer.TrimLeadingSymbols("can I get supplies, please, on tower 2"));
    }

    [Fact]
    public void AllSymbols_ReturnsEmpty()
    {
        Assert.Equal("", TranscriptSanitizer.TrimLeadingSymbols(",-..."));
    }

    [Fact]
    public void EmptyString_ReturnsEmpty()
    {
        Assert.Equal("", TranscriptSanitizer.TrimLeadingSymbols(""));
    }
}
