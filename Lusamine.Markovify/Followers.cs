using System.Collections;

namespace Lusamine.Markovify;

/// <summary>
/// The set of words that can follow a single <see cref="State"/>, with the transition
/// count observed for each during training.
/// </summary>
/// <remarks>
/// Stored as two parallel arrays rather than a <see cref="Dictionary{TKey,TValue}"/> so a
/// state with only a handful of followers (the common case) costs a small array pair instead
/// of the fixed ~200 bytes of dictionary overhead. Across the millions of sparse states in a
/// large model this is the difference between a chain that fits in memory and one that does
/// not. It implements <see cref="IReadOnlyDictionary{TKey,TValue}"/> so existing
/// <c>chain.Model[state]["word"]</c>-style access keeps working; the indexer is a linear scan,
/// which is fine for the few followers a state typically has.
/// </remarks>
public readonly struct Followers : IReadOnlyDictionary<string, int>
{
    private readonly string[] _words;
    private readonly int[] _counts;

    /// <summary>
    /// Wraps the parallel <paramref name="words"/> and <paramref name="counts"/> arrays,
    /// which must have the same length. Takes ownership of both.
    /// </summary>
    internal Followers(string[] words, int[] counts)
    {
        _words = words;
        _counts = counts;
    }

    // Direct array access for the library's hot paths, avoiding interface dispatch.
    internal string[] WordsArray => _words;
    internal int[] CountsArray => _counts;

    /// <summary>The number of distinct follower words.</summary>
    public int Count => _words.Length;

    /// <summary>The transition count recorded for <paramref name="key"/>.</summary>
    /// <exception cref="KeyNotFoundException">The word never followed this state.</exception>
    public int this[string key]
    {
        get
        {
            var index = IndexOf(key);
            if (index < 0)
                throw new KeyNotFoundException($"'{key}' is not a follower of this state.");
            return _counts[index];
        }
    }

    /// <inheritdoc />
    public bool ContainsKey(string key) => IndexOf(key) >= 0;

    /// <inheritdoc />
    public bool TryGetValue(string key, out int value)
    {
        var index = IndexOf(key);
        if (index < 0)
        {
            value = 0;
            return false;
        }

        value = _counts[index];
        return true;
    }

    /// <inheritdoc />
    public IEnumerable<string> Keys
    {
        get
        {
            foreach (var word in _words)
                yield return word;
        }
    }

    /// <inheritdoc />
    public IEnumerable<int> Values
    {
        get
        {
            foreach (var count in _counts)
                yield return count;
        }
    }

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, int>> GetEnumerator()
    {
        for (var i = 0; i < _words.Length; i++)
            yield return new KeyValuePair<string, int>(_words[i], _counts[i]);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private int IndexOf(string key)
    {
        for (var i = 0; i < _words.Length; i++)
        {
            if (string.Equals(_words[i], key, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }
}
