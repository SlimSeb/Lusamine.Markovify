namespace Lusamine.Markovify;

/// <summary>
/// A <see cref="Text"/> variant that treats each non-empty line of the input as
/// a single sentence, instead of detecting sentence boundaries by punctuation.
/// Useful for corpora like tweets, song lines, or headlines.
/// </summary>
public sealed class NewlineText : Text
{
    /// <summary>Trains a model where every line of <paramref name="inputText"/> is one sentence.</summary>
    public NewlineText(string inputText, int stateSize = 2, bool retainOriginal = true, Random? rng = null)
        : base(ParseLines(inputText), stateSize, retainOriginal, rng, chain: null, sentenceSplitterUsed: false)
    {
    }

    private static IReadOnlyList<IReadOnlyList<string>> ParseLines(string inputText)
    {
        var sentences = new List<IReadOnlyList<string>>();
        foreach (var line in inputText.Split('\n'))
        {
            var words = Splitters.SplitIntoWords(line);
            if (words.Count > 0)
                sentences.Add(words);
        }
        return sentences;
    }
}
