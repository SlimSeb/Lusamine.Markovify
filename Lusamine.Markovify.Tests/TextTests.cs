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
    public void ToJson_FromJson_PreservesTemperature()
    {
        var model = new Text(Corpus, stateSize: 2, rng: new Random(8), temperature: 1.7);

        var restored = Text.FromJson(model.ToJson());

        Assert.Equal(1.7, restored.Chain.Temperature);
    }

    [Fact]
    public void Save_Load_RoundTripsThroughAFile()
    {
        var model = new Text(Corpus, stateSize: 2, rng: new Random(8), temperature: 1.5);
        var path = Path.Combine(Path.GetTempPath(), $"markovify-{Guid.NewGuid():N}.json");

        try
        {
            model.Save(path);
            var restored = Text.Load(path, new Random(8));

            Assert.Equal(model.StateSize, restored.StateSize);
            Assert.Equal(model.Chain.Temperature, restored.Chain.Temperature);
            Assert.NotNull(restored.MakeSentence(testOutput: false));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveAsync_LoadAsync_RoundTripsThroughAFile()
    {
        var model = new Text(Corpus, stateSize: 2, rng: new Random(8));
        var path = Path.Combine(Path.GetTempPath(), $"markovify-{Guid.NewGuid():N}.json");

        try
        {
            await model.SaveAsync(path);
            var restored = await Text.LoadAsync(path, new Random(8));

            Assert.Equal(model.StateSize, restored.StateSize);
            Assert.NotNull(restored.MakeSentence(testOutput: false));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SaveBinary_LoadBinary_RoundTripsThroughAFile()
    {
        var model = new Text(Corpus, stateSize: 2, rng: new Random(8), temperature: 1.5);
        var path = Path.Combine(Path.GetTempPath(), $"markovify-{Guid.NewGuid():N}.bin");

        try
        {
            model.SaveBinary(path);
            var restored = Text.LoadBinary(path, new Random(8));

            Assert.Equal(model.StateSize, restored.StateSize);
            Assert.Equal(model.Chain.Temperature, restored.Chain.Temperature);
            Assert.Equal(model.Chain.Model.Count, restored.Chain.Model.Count);
            Assert.NotNull(restored.MakeSentence(testOutput: false));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveBinaryAsync_LoadBinaryAsync_RoundTripsThroughAFile()
    {
        var model = new Text(Corpus, stateSize: 2, rng: new Random(8), temperature: 1.3);
        var path = Path.Combine(Path.GetTempPath(), $"markovify-{Guid.NewGuid():N}.bin");

        try
        {
            await model.SaveBinaryAsync(path);
            var restored = await Text.LoadBinaryAsync(path, new Random(8));

            Assert.Equal(model.StateSize, restored.StateSize);
            Assert.Equal(model.Chain.Temperature, restored.Chain.Temperature);
            Assert.NotNull(restored.MakeSentence(testOutput: false));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SaveBinary_IsSmallerThanJson()
    {
        var model = new Text(Corpus, stateSize: 2, rng: new Random(8));
        var path = Path.Combine(Path.GetTempPath(), $"markovify-{Guid.NewGuid():N}.bin");

        try
        {
            model.SaveBinary(path);
            var binarySize = new FileInfo(path).Length;
            var jsonSize = System.Text.Encoding.UTF8.GetByteCount(model.ToJson());

            Assert.True(binarySize < jsonSize, $"Expected binary ({binarySize}) < JSON ({jsonSize}).");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SaveMapped_OpenMapped_GeneratesKnownVocabulary()
    {
        var model = new Text(Corpus, stateSize: 2, temperature: 1.4);
        var vocabulary = Corpus
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet();
        var path = Path.Combine(Path.GetTempPath(), $"markovify-{Guid.NewGuid():N}.mmf");

        try
        {
            model.SaveMapped(path);
            using var mapped = Text.OpenMapped(path);

            Assert.Equal(model.StateSize, mapped.StateSize);
            Assert.Equal(model.Chain.Temperature, mapped.Temperature);

            var sentence = mapped.MakeSentence(rng: new Random(3));
            Assert.NotNull(sentence);
            foreach (var word in sentence!.Split(' '))
                Assert.Contains(word, vocabulary);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SaveMapped_OpenMapped_MatchesInMemoryWalk()
    {
        // With a single-path corpus the only possible walk is deterministic, so the mapped
        // model must reproduce exactly what the in-memory chain produces.
        var model = new Text("the quick brown fox jumps.", stateSize: 2);
        var path = Path.Combine(Path.GetTempPath(), $"markovify-{Guid.NewGuid():N}.mmf");

        try
        {
            model.SaveMapped(path);
            using var mapped = Text.OpenMapped(path);

            Assert.Equal("the quick brown fox jumps.", mapped.MakeSentence(rng: new Random(11)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OpenMapped_RejectsNonMappedData()
    {
        var path = Path.Combine(Path.GetTempPath(), $"markovify-{Guid.NewGuid():N}.mmf");
        File.WriteAllBytes(path, new byte[128]);

        try
        {
            Assert.Throws<InvalidDataException>(() => Text.OpenMapped(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadBinary_RejectsNonMarkovifyData()
    {
        var path = Path.Combine(Path.GetTempPath(), $"markovify-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, [0x00, 0x01, 0x02, 0x03, 0x04]);

        try
        {
            Assert.Throws<InvalidDataException>(() => Text.LoadBinary(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RetainedSource_IsChunkedAndStillDetectsOverlapAcrossBoundaries()
    {
        // Force tiny chunks so the retained source is split into many pieces, then confirm
        // a sentence copied verbatim from the source is still rejected even though the
        // matching text may straddle a chunk boundary.
        var savedLimit = Text.RejoinedChunkLimit;
        var savedOverlap = Text.RejoinedChunkOverlap;
        Text.RejoinedChunkLimit = 64;
        Text.RejoinedChunkOverlap = 16;
        try
        {
            var model = new Text("The cat sat on the mat.", stateSize: 2, rng: new Random(3));

            // The only possible walk reproduces the source, so the overlap test must reject it.
            Assert.Null(model.MakeSentence(testOutput: true));
            Assert.NotNull(model.MakeSentence(testOutput: false));
        }
        finally
        {
            Text.RejoinedChunkLimit = savedLimit;
            Text.RejoinedChunkOverlap = savedOverlap;
        }
    }

    [Fact]
    public void Construction_DoesNotRejoinTheCorpusIntoOneGiantString()
    {
        // Many sentences with a tiny chunk limit exercises the chunking path used to keep
        // huge corpora from overflowing .NET's maximum string length during construction.
        var savedLimit = Text.RejoinedChunkLimit;
        Text.RejoinedChunkLimit = 32;
        try
        {
            var builder = new System.Text.StringBuilder();
            for (var i = 0; i < 200; i++)
                builder.Append("Word").Append(i).Append(" alpha beta gamma. ");

            var model = new Text(builder.ToString(), stateSize: 2, rng: new Random(7));

            Assert.NotNull(model.ParsedSentences);
            Assert.NotNull(model.MakeSentence(testOutput: false));
        }
        finally
        {
            Text.RejoinedChunkLimit = savedLimit;
        }
    }

    [Fact]
    public void FromSentences_BuildsAModelThatGenerates()
    {
        var sentences = Corpus
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Splitters.SplitIntoWords(s));

        var model = Text.FromSentences(sentences, stateSize: 2, rng: new Random(8), temperature: 1.4);

        Assert.Equal(2, model.StateSize);
        Assert.Equal(1.4, model.Chain.Temperature);
        Assert.NotNull(model.MakeSentence(testOutput: false));
        // The source is not retained, so it generates with the overlap test off.
        Assert.Null(model.ParsedSentences);
    }

    [Fact]
    public void FromSentences_ConsumesTheSequenceLazilyAndOnce()
    {
        var enumerations = 0;

        IEnumerable<IReadOnlyList<string>> Stream()
        {
            enumerations++;
            foreach (var sentence in Corpus.Split('.', StringSplitOptions.RemoveEmptyEntries))
                yield return Splitters.SplitIntoWords(sentence);
        }

        var model = Text.FromSentences(Stream(), stateSize: 2, rng: new Random(8));

        // Building must walk the sequence exactly once; generation must not re-enumerate it.
        model.MakeSentence(testOutput: false);
        Assert.Equal(1, enumerations);
    }

    [Fact]
    public void FromSentences_SkipsEmptySentences()
    {
        IReadOnlyList<IReadOnlyList<string>> sentences =
        [
            ["the", "cat", "sat"],
            [],
            ["the", "dog", "ran"],
        ];

        var model = Text.FromSentences(sentences, stateSize: 1, rng: new Random(1));

        // The empty run must not create a degenerate BEGIN -> END state.
        Assert.DoesNotContain(model.Chain.Model, kvp => kvp.Value.ContainsKey(Chain.End)
            && kvp.Key.Words.All(w => w == Chain.Begin));
        Assert.NotNull(model.MakeSentence(testOutput: false));
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
