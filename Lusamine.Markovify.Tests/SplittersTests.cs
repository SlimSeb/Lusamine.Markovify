using Lusamine.Markovify;

namespace Lusamine.Markovify.Tests;

public class SplittersTests
{
    [Fact]
    public void SplitIntoSentences_SplitsOnTerminalPunctuation()
    {
        var sentences = Splitters.SplitIntoSentences("Hello there. How are you? I am fine!");

        Assert.Equal(["Hello there.", "How are you?", "I am fine!"], sentences);
    }

    [Fact]
    public void SplitIntoSentences_DoesNotSplitMidNumber()
    {
        var sentences = Splitters.SplitIntoSentences("The value is 3.14 today.");

        Assert.Single(sentences);
    }

    [Fact]
    public void SplitIntoSentences_IgnoresBlankSegments()
    {
        var sentences = Splitters.SplitIntoSentences("   ");

        Assert.Empty(sentences);
    }

    [Fact]
    public void SplitIntoWords_SplitsOnWhitespaceRuns()
    {
        var words = Splitters.SplitIntoWords("  the   quick\tbrown\nfox ");

        Assert.Equal(["the", "quick", "brown", "fox"], words);
    }

    [Fact]
    public void SplitIntoWords_EmptyInputYieldsNoWords()
    {
        Assert.Empty(Splitters.SplitIntoWords("   "));
    }
}
