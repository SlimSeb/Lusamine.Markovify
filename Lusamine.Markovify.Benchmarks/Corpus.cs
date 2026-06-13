namespace Lusamine.Markovify.Benchmarks;

/// <summary>
/// Generates a deterministic, pre-tokenized synthetic corpus so both chains build
/// from identical input and tokenization cost is excluded from the measurements.
/// Word frequencies follow a rough Zipf distribution to mimic natural language:
/// a few words appear constantly, most are rare. That skew is what makes the
/// integer-id representation pay off (the common words would otherwise be stored
/// as many duplicate string objects).
/// </summary>
internal static class Corpus
{
    public static IReadOnlyList<IReadOnlyList<string>> Generate(
        int sentences, int vocabSize, int minWords, int maxWords, int seed)
    {
        var rng = new Random(seed);
        var words = new string[vocabSize];
        for (var i = 0; i < vocabSize; i++)
            words[i] = "w" + i;

        // Precompute a Zipf cumulative distribution over the vocabulary.
        var cumulative = new double[vocabSize];
        var total = 0.0;
        for (var i = 0; i < vocabSize; i++)
        {
            total += 1.0 / (i + 1);
            cumulative[i] = total;
        }

        var corpus = new List<IReadOnlyList<string>>(sentences);
        for (var s = 0; s < sentences; s++)
        {
            var length = rng.Next(minWords, maxWords + 1);
            var run = new string[length];
            for (var w = 0; w < length; w++)
            {
                var roll = rng.NextDouble() * total;
                // Fresh string instance per occurrence, the way a real tokenizer
                // (e.g. string.Split over File.ReadLines) produces them. This is what
                // lets the string-keyed chain accumulate duplicate word objects and is
                // exactly the duplication the vocabulary collapses.
                run[w] = new string(words[PickZipf(cumulative, roll)].AsSpan());
            }
            corpus.Add(run);
        }

        return corpus;
    }

    private static int PickZipf(double[] cumulative, double value)
    {
        var low = 0;
        var high = cumulative.Length;
        while (low < high)
        {
            var mid = (low + high) >> 1;
            if (value < cumulative[mid])
                high = mid;
            else
                low = mid + 1;
        }
        return low < cumulative.Length ? low : cumulative.Length - 1;
    }
}
