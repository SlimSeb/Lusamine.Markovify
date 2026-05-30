using System.Text.RegularExpressions;

namespace Lusamine.Markovify;

/// <summary>
/// Default tokenization helpers used by <see cref="Text"/>. These mirror the
/// pragmatic behavior of markovify: split a paragraph into sentences on
/// terminal punctuation, and split a sentence into words on whitespace.
/// </summary>
public static partial class Splitters
{
    [GeneratedRegex(@"(?<=[.!?])[\s]+(?=[""'(\[]?[A-Z0-9])", RegexOptions.Compiled)]
    private static partial Regex SentenceBoundary();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex Whitespace();

    /// <summary>
    /// Splits a block of text into sentences. The split occurs at whitespace
    /// that follows a <c>.</c>, <c>!</c> or <c>?</c> and precedes a capital
    /// letter or digit, which avoids breaking on most abbreviations.
    /// </summary>
    public static IReadOnlyList<string> SplitIntoSentences(string text)
    {
        var result = new List<string>();
        foreach (var piece in SentenceBoundary().Split(text))
        {
            var trimmed = piece.Trim();
            if (trimmed.Length > 0)
                result.Add(trimmed);
        }
        return result;
    }

    /// <summary>Splits a sentence into words on runs of whitespace.</summary>
    public static IReadOnlyList<string> SplitIntoWords(string sentence)
    {
        var trimmed = sentence.Trim();
        if (trimmed.Length == 0)
            return Array.Empty<string>();
        return Whitespace().Split(trimmed);
    }
}
