namespace Lusamine.Markovify;

/// <summary>
/// An immutable, value-equatable window of words representing the current
/// position in a <see cref="Chain"/>. The length of a state equals the
/// chain's state size.
/// </summary>
public readonly struct State : IEquatable<State>
{
    private readonly string[] _words;

    /// <summary>Creates a state that takes ownership of <paramref name="words"/>.</summary>
    public State(string[] words) => _words = words;

    /// <summary>The words that make up this state, in order.</summary>
    public IReadOnlyList<string> Words => _words;

    /// <summary>The number of words in the state (i.e. the chain's state size).</summary>
    public int Length => _words.Length;

    /// <summary>Gets the word at the given position within the state.</summary>
    public string this[int index] => _words[index];

    /// <summary>
    /// Produces the next state by dropping the oldest word and appending
    /// <paramref name="next"/> to the end.
    /// </summary>
    public State Shift(string next)
    {
        var shifted = new string[_words.Length];
        Array.Copy(_words, 1, shifted, 0, _words.Length - 1);
        shifted[^1] = next;
        return new State(shifted);
    }

    /// <summary>Determines whether this state has the same words, in order, as <paramref name="other"/>.</summary>
    public bool Equals(State other)
    {
        if (_words.Length != other._words.Length)
            return false;
        for (var i = 0; i < _words.Length; i++)
        {
            if (!string.Equals(_words[i], other._words[i], StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is State other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var word in _words)
            hash.Add(word, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString() => $"({string.Join(", ", _words)})";
}
