using Lusamine.Markovify;

namespace Lusamine.Markovify.Tests;

public class NewlineTextTests
{
    [Fact]
    public void EachLineBecomesOneSentence_RegardlessOfPunctuation()
    {
        // No terminal punctuation: a regular Text would see this as one sentence.
        var input = "roses are red\nviolets are blue\nsugar is sweet";

        var model = new NewlineText(input, stateSize: 2);

        Assert.NotNull(model.ParsedSentences);
        Assert.Equal(3, model.ParsedSentences!.Count);
        Assert.Equal(["roses", "are", "red"], model.ParsedSentences[0]);
    }

    [Fact]
    public void BlankLinesAreSkipped()
    {
        var model = new NewlineText("one two\n\n\nthree four\n", stateSize: 1);

        Assert.Equal(2, model.ParsedSentences!.Count);
    }

    [Fact]
    public void GeneratesFromLineBasedCorpus()
    {
        var input = string.Join('\n', Enumerable.Range(0, 20)
            .Select(i => $"line number {i} of text"));

        var model = new NewlineText(input, stateSize: 2, rng: new Random(4));

        Assert.NotNull(model.MakeSentence(testOutput: false));
    }
}
