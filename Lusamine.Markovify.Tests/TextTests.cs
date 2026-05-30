using Lusamine.Markovify;

namespace Lusamine.Markovify.Tests;

public class TextTests
{
    // A small but branching corpus so that novel sentences are possible.
    private const string Corpus =
        "The cat sat on the mat. " +
        "The dog sat on the floor. " +
        "The cat ran across the room. " +
        "The dog ran across the yard. " +
        "A happy cat sat quietly. " +
        "A happy dog ran loudly.";

    [Fact]
    public void MakeSentence_ProducesKnownVocabulary()
    {
        var model = new Text(Corpus, stateSize: 2, rng: new Random(1));
        // Tokenization keeps punctuation attached, so build the vocabulary the same way.
        var vocabulary = Corpus
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet();

        var sentence = model.MakeSentence(testOutput: false);

        Assert.NotNull(sentence);
        foreach (var word in sentence!.Split(' '))
            Assert.Contains(word, vocabulary);
    }

    [Fact]
    public void MakeSentence_IsDeterministicForAGivenSeed()
    {
        var a = new Text(Corpus, stateSize: 2, rng: new Random(99));
        var b = new Text(Corpus, stateSize: 2, rng: new Random(99));

        Assert.Equal(
            a.MakeSentence(testOutput: false),
            b.MakeSentence(testOutput: false));
    }

    [Fact]
    public void MakeSentence_RejectsVerbatimReproductionWhenTestingOutput()
    {
        // Single sentence: the only possible walk reproduces it exactly.
        var model = new Text("The cat sat on the mat.", stateSize: 2, rng: new Random(3));

        Assert.Null(model.MakeSentence(testOutput: true));
        Assert.NotNull(model.MakeSentence(testOutput: false));
    }

    [Fact]
    public void MakeShortSentence_RespectsCharacterBounds()
    {
        var model = new Text(Corpus, stateSize: 2, rng: new Random(11));

        var sentence = model.MakeShortSentence(maxChars: 40, minChars: 1, testOutput: false);

        Assert.NotNull(sentence);
        Assert.InRange(sentence!.Length, 1, 40);
    }

    [Fact]
    public void MakeSentenceWithStart_BeginsWithRequestedWords()
    {
        var model = new Text(Corpus, stateSize: 2, rng: new Random(5));

        var sentence = model.MakeSentenceWithStart("The cat", testOutput: false);

        Assert.NotNull(sentence);
        Assert.StartsWith("The cat", sentence);
    }

    [Fact]
    public void MakeSentenceWithStart_StrictPadsShortBeginning()
    {
        var model = new Text(Corpus, stateSize: 2, rng: new Random(5));

        // One word with a state size of two -> padded with BEGIN internally.
        var sentence = model.MakeSentenceWithStart("The", strict: true, testOutput: false);

        Assert.NotNull(sentence);
        Assert.StartsWith("The", sentence);
    }

    [Fact]
    public void MakeSentenceWithStart_ThrowsWhenBeginningTooLong()
    {
        var model = new Text(Corpus, stateSize: 2, rng: new Random(5));

        Assert.Throws<ArgumentException>(
            () => model.MakeSentenceWithStart("The cat sat on"));
    }

    [Fact]
    public void ParsedSentences_AreRetainedByDefault_AndDroppedWhenAsked()
    {
        var retained = new Text(Corpus, stateSize: 2);
        var notRetained = new Text(Corpus, stateSize: 2, retainOriginal: false);

        Assert.NotNull(retained.ParsedSentences);
        Assert.Null(notRetained.ParsedSentences);
    }

    [Fact]
    public void ToJson_FromJson_RoundTripsAndStillGenerates()
    {
        var model = new Text(Corpus, stateSize: 2, rng: new Random(8));

        var restored = Text.FromJson(model.ToJson(), new Random(8));

        Assert.Equal(model.StateSize, restored.StateSize);
        // testOutput is disabled because the restored model dropped the source text.
        Assert.NotNull(restored.MakeSentence(testOutput: false));
    }

    [Fact]
    public void Combine_MergesVocabularyFromBothModels()
    {
        var a = new Text("Alpha beta gamma delta epsilon.", stateSize: 2, rng: new Random(1));
        var b = new Text("One two three four five.", stateSize: 2, rng: new Random(1));

        var combined = Text.Combine([a, b], [1.0, 1.0], new Random(2));

        // The combined chain knows transitions from both sources.
        Assert.True(combined.Chain.Model.ContainsKey(new State(["Alpha", "beta"])));
        Assert.True(combined.Chain.Model.ContainsKey(new State(["One", "two"])));
    }
}
