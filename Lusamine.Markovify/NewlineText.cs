namespace Lusamine.Markovify;

/// <summary>
/// A <see cref="Text"/> variant that treats each non-empty line of the input as
/// a single sentence, instead of detecting sentence boundaries by punctuation.
/// Useful for corpora like tweets, song lines, or headlines.
/// </summary>
public sealed class NewlineText : Text
{
    /// <summary>Trains a model where every line of <paramref name="inputText"/> is one sentence.</summary>
    /// <param name="inputText">The corpus to learn from; each non-empty line is one sentence.</param>
    /// <param name="stateSize">Number of words per state (the Markov order).</param>
    /// <param name="retainOriginal">
    /// When <c>true</c> (default), the source is kept so generated sentences can be
    /// rejection-tested for excessive overlap with the original.
    /// </param>
    /// <param name="rng">Random source; defaults to <see cref="Random.Shared"/>.</param>
    /// <param name="normalize">
    /// When <c>true</c>, each word is lowercased and stripped of non-alphanumeric
    /// characters before training, so tokens like <c>"Hello,"</c> and <c>"hello"</c>
    /// are treated as the same word.
    /// </param>
    /// <param name="temperature">
    /// Sampling temperature for generation. <c>1.0</c> (default) reproduces the
    /// trained distribution; values greater than <c>1.0</c> reduce verbatim copying
    /// of the source by favoring rarer transitions. See <see cref="Chain.Temperature"/>.
    /// </param>
    public NewlineText(string inputText, int stateSize = 2, bool retainOriginal = true, Random? rng = null,
        bool normalize = false, double temperature = 1.0)
        : base(ParseLines(inputText, normalize), stateSize, retainOriginal, rng, chain: null, sentenceSplitterUsed: false)
    {
        Chain.Temperature = temperature;
    }

    private static IReadOnlyList<IReadOnlyList<string>> ParseLines(string inputText, bool normalize = false)
    {
        var sentences = new List<IReadOnlyList<string>>();
        foreach (var line in inputText.Split('\n'))
        {
            var words = Splitters.SplitIntoWords(line, normalize);
            if (words.Count > 0)
                sentences.Add(words);
        }

        return sentences;
    }
}