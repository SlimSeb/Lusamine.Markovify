using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Lusamine.Markovify.Tests;

public class ChainTests
{
    private static IReadOnlyList<IReadOnlyList<string>> Corpus(params string[] sentences) =>
        sentences.Select(s => (IReadOnlyList<string>)s.Split(' ')).ToList();

    [Fact]
    public void Build_RejectsStateSizeBelowOne()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Chain.Build(Corpus("a b c"), stateSize: 0));
    }

    [Fact]
    public void Build_RecordsBeginAndEndTransitions()
    {
        var chain = Chain.Build(Corpus("the cat sat"), stateSize: 2);

        var begin = chain.BeginState();
        Assert.True(chain.Model.ContainsKey(begin));
        Assert.Equal(1, chain.Model[begin]["the"]);

        // The final real state transitions to END.
        var last = new State(["cat", "sat"]);
        Assert.Equal(1, chain.Model[last][Chain.End]);
    }

    [Fact]
    public void Build_AccumulatesRepeatedTransitions()
    {
        var chain = Chain.Build(Corpus("a b", "a b", "a c"), stateSize: 1);

        var stateA = new State(["a"]);
        Assert.Equal(2, chain.Model[stateA]["b"]);
        Assert.Equal(1, chain.Model[stateA]["c"]);
    }

    [Fact]
    public void Move_AlwaysReturnsAValidFollower()
    {
        var chain = Chain.Build(Corpus("a b c"), stateSize: 1);
        var rng = new Random(42);

        var next = chain.Move(new State(["a"]), rng);

        Assert.Equal("b", next);
    }

    [Fact]
    public void Move_ThrowsForUnknownState()
    {
        var chain = Chain.Build(Corpus("a b c"), stateSize: 1);

        Assert.Throws<KeyNotFoundException>(
            () => chain.Move(new State(["zzz"]), new Random(1)));
    }

    [Fact]
    public void Move_RespectsWeighting()
    {
        // "a" is followed by "b" 9 times and "c" once.
        var corpus = Enumerable.Repeat("a b", 9).Append("a c").ToArray();
        var chain = Chain.Build(Corpus(corpus), stateSize: 1);
        var rng = new Random(123);

        var bCount = 0;
        for (var i = 0; i < 1000; i++)
        {
            if (chain.Move(new State(["a"]), rng) == "b")
                bCount++;
        }

        // Expected ~900; allow generous slack for randomness.
        Assert.InRange(bCount, 820, 960);
    }

    [Fact]
    public void Walk_ProducesTheOnlyPossibleSentence()
    {
        var chain = Chain.Build(Corpus("the cat sat down"), stateSize: 2);

        var walk = chain.Walk(rng: new Random(7)).ToArray();

        Assert.Equal(["the", "cat", "sat", "down"], walk);
    }

    [Fact]
    public void Walk_NeverYieldsSentinelTokens()
    {
        var chain = Chain.Build(Corpus("one two three", "two three four", "three four five"), stateSize: 2);
        var rng = new Random(99);

        for (var i = 0; i < 50; i++)
        {
            foreach (var word in chain.Walk(rng: rng))
            {
                Assert.NotEqual(Chain.Begin, word);
                Assert.NotEqual(Chain.End, word);
            }
        }
    }

    [Fact]
    public void Combine_SumsScaledCounts()
    {
        var c1 = Chain.Build(Corpus("a b"), stateSize: 1);
        var c2 = Chain.Build(Corpus("a b"), stateSize: 1);

        var combined = Chain.Combine([c1, c2], [1.0, 2.0]);

        Assert.Equal(3, combined.Model[new State(["a"])]["b"]);
    }

    [Fact]
    public void Combine_RejectsMismatchedStateSizes()
    {
        var c1 = Chain.Build(Corpus("a b c"), stateSize: 1);
        var c2 = Chain.Build(Corpus("a b c"), stateSize: 2);

        Assert.Throws<ArgumentException>(() => Chain.Combine([c1, c2]));
    }

    [Fact]
    public void Combine_RejectsWeightCountMismatch()
    {
        var c1 = Chain.Build(Corpus("a b"), stateSize: 1);

        Assert.Throws<ArgumentException>(() => Chain.Combine([c1], [1.0, 2.0]));
    }

    [Fact]
    public void ToJson_FromJson_RoundTripsModel()
    {
        var original = Chain.Build(Corpus("the cat sat", "the dog ran"), stateSize: 2);

        var restored = Chain.FromJson(original.ToJson());

        Assert.Equal(original.StateSize, restored.StateSize);
        Assert.Equal(original.Model.Count, restored.Model.Count);
        foreach (var (state, followers) in original.Model)
        {
            Assert.True(restored.Model.ContainsKey(state));
            Assert.Equal(followers, restored.Model[state]);
        }
    }

    [Fact]
    public void WriteBinary_ReadBinary_RoundTripsModel()
    {
        var original = Chain.Build(Corpus("the cat sat", "the dog ran", "the cat ran"), stateSize: 2);

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            original.WriteBinary(writer);

        stream.Position = 0;
        using var reader = new BinaryReader(stream);
        var restored = Chain.ReadBinary(reader);

        Assert.Equal(original.StateSize, restored.StateSize);
        Assert.Equal(original.Model.Count, restored.Model.Count);
        foreach (var (state, followers) in original.Model)
        {
            Assert.True(restored.Model.ContainsKey(state));
            Assert.Equal(followers, restored.Model[state]);
        }
    }
}
